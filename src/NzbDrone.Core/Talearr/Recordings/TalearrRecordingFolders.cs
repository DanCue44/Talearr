using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using NLog;
using NzbDrone.Core.Books;
using NzbDrone.Core.MediaFiles;

namespace NzbDrone.Core.Talearr.Recordings
{
    // One other recording (audiobook row) of the same work, as the folder rule needs it.
    public sealed class RecordingFolderRow
    {
        public int BookId { get; set; }
        public bool IsPrimary { get; set; }

        // Primary first, then copies by row id: the order folders are handed out in.
        public int Ordinal { get; set; }
        public Edition Edition { get; set; }

        // A file of the row, used for file based naming tokens; null when the row has no files.
        public BookFile SampleFile { get; set; }

        // Folders that hold the row's files today (absolute).
        public List<string> FileFolders { get; set; } = new List<string>();
        public string Asin { get; set; }
    }

    public sealed class RecordingFolderContext
    {
        public int BookId { get; set; }
        public int Ordinal { get; set; }
        public string Asin { get; set; }

        // Rows handed a folder before this one (the primary and lower copies).
        public List<RecordingFolderRow> Earlier { get; set; } = new List<RecordingFolderRow>();

        // Every other row of the work, for the "already holds files" check.
        public List<RecordingFolderRow> Others { get; set; } = new List<RecordingFolderRow>();
    }

    // What tells two recordings apart, in the order the folder rule tries it.
    public sealed class RecordingFolderKey
    {
        public string Narrator { get; set; }
        public string Abridgement { get; set; }
        public string EditionYear { get; set; }
        public string Asin { get; set; }
        public int Ordinal { get; set; }

        public IEnumerable<(string Name, string Value)> Disambiguators()
        {
            yield return ("narrator", Narrator);
            yield return ("abridgement", Abridgement);
            yield return ("edition year", EditionYear);
            yield return ("asin", Asin);
        }
    }

    public interface ITalearrRecordingFolders
    {
        // Null when the edition's row is not an additional recording of an audiobook with more
        // than one recording. The primary row never gets a context, so its path never changes.
        RecordingFolderContext GetContext(Edition edition);

        // Message when the folder already holds files of another recording of the target row's
        // work, else null. Used by import so two recordings never share one folder.
        string FindFolderConflict(Book target, string destinationFolder);
    }

    // Talearr: two recordings of one book (say the abridged and unabridged Scott Brick readings)
    // would get the same book folder when the naming format cannot tell them apart. The file name
    // builder asks this service for the other recordings of the work and appends a disambiguator to
    // an additional recording's book folder: the narrator, then Abridged or Unabridged, then the
    // edition (its year, then its ASIN), else "Copy N". Values come from the existing naming
    // tokens. Nothing is stored; the base row's folder never changes.
    public class TalearrRecordingFolders : ITalearrRecordingFolders
    {
        private static readonly TimeSpan MemoLifetime = TimeSpan.FromSeconds(20);

        private readonly IBookService _bookService;
        private readonly IEditionService _editionService;
        private readonly IMediaFileService _mediaFileService;
        private readonly ITalearrRecordingGroupService _groupService;
        private readonly ITalearrAudibleFactsService _factsService;
        private readonly ITalearrRecordingsSettingsService _settingsService;
        private readonly Logger _logger;
        private readonly ConcurrentDictionary<int, (DateTime At, RecordingFolderContext Value)> _memo = new ConcurrentDictionary<int, (DateTime, RecordingFolderContext)>();

        public TalearrRecordingFolders(IBookService bookService,
                                       IEditionService editionService,
                                       IMediaFileService mediaFileService,
                                       ITalearrRecordingGroupService groupService,
                                       ITalearrAudibleFactsService factsService,
                                       ITalearrRecordingsSettingsService settingsService,
                                       Logger logger)
        {
            _bookService = bookService;
            _editionService = editionService;
            _mediaFileService = mediaFileService;
            _groupService = groupService;
            _factsService = factsService;
            _settingsService = settingsService;
            _logger = logger;
        }

        public RecordingFolderContext GetContext(Edition edition)
        {
            var book = edition?.Book;
            if (book == null && edition?.BookId > 0)
            {
                book = _bookService.GetBook(edition.BookId);
            }

            // Fast path: only upstream's wanted and copy rows can be additional recordings.
            if (book == null || book.Id <= 0 || book.MediaType != BookMediaType.Audiobook || !TalearrRecordingGroupService.IsCopySlug(book.TitleSlug))
            {
                return null;
            }

            if (_memo.TryGetValue(book.Id, out var hit) && hit.At + MemoLifetime > DateTime.UtcNow)
            {
                return hit.Value;
            }

            var context = BuildContext(book);
            _memo[book.Id] = (DateTime.UtcNow, context);
            return context;
        }

        public string FindFolderConflict(Book target, string destinationFolder)
        {
            if (target == null || target.Id <= 0 || target.MediaType != BookMediaType.Audiobook || string.IsNullOrWhiteSpace(destinationFolder))
            {
                return null;
            }

            var group = _groupService.GetGroup(target);
            if (group == null || group.Books.Count < 2 || group.Primary.Id == target.Id)
            {
                // The primary recording imports where it always did.
                return null;
            }

            var others = group.Books.Where(b => b.Id != target.Id).ToList();
            var files = _mediaFileService.GetFilesByBooks(others.Select(b => b.Id).ToList()) ?? new List<BookFile>();
            var editions = _editionService.GetEditionsByBook(others.Select(b => b.Id).ToList()) ?? new List<Edition>();
            var editionToBook = editions.GroupBy(e => e.Id).ToDictionary(g => g.Key, g => g.First().BookId);
            var wanted = Normalize(destinationFolder);

            var holder = files.FirstOrDefault(f => !string.IsNullOrWhiteSpace(f.Path) &&
                                                   editionToBook.ContainsKey(f.EditionId) &&
                                                   string.Equals(Normalize(SafeDirectory(f.Path)), wanted, StringComparison.OrdinalIgnoreCase));
            if (holder == null)
            {
                return null;
            }

            var holderBookId = editionToBook[holder.EditionId];
            return $"Talearr: {destinationFolder} already holds the files of another recording of this book (book {holderBookId}). " +
                   "Two recordings never share a folder; give audiobooks a book folder in the naming format, for example {Book Title}{ - Narrator}.";
        }

        // Import hook: where the batch would land for the target row, and whether another recording
        // of the same work already has files there. Null when there is nothing to refuse.
        public static string FindImportConflict(ITalearrRecordingFolders folders,
                                                IMoveBookFiles mover,
                                                Book target,
                                                Edition targetEdition,
                                                Author author,
                                                IEnumerable<Parser.Model.LocalBook> items,
                                                Logger logger)
        {
            if (folders == null || mover == null || target == null || target.MediaType != BookMediaType.Audiobook)
            {
                return null;
            }

            try
            {
                var item = items?.FirstOrDefault(i => i?.Quality != null && !string.IsNullOrWhiteSpace(i.Path));
                var edition = targetEdition ?? item?.Edition;
                if (item == null || edition == null)
                {
                    return null;
                }

                edition.Book ??= target;
                var preview = new Parser.Model.LocalBook
                {
                    Path = item.Path,
                    Author = author,
                    Book = target,
                    Edition = edition,
                    Quality = item.Quality,
                    Part = item.Part,
                    PartCount = item.PartCount
                };
                var previewFile = new BookFile
                {
                    Path = item.Path,
                    Quality = item.Quality,
                    EditionId = edition.Id,
                    Edition = edition,
                    Author = author,
                    Part = item.Part,
                    PartCount = item.PartCount,
                    Narrator = item.Narrator,
                    MediaType = BookFile.DetermineMediaType(item.Quality)
                };

                var destination = mover.GetImportDestinationPath(previewFile, preview);
                return string.IsNullOrWhiteSpace(destination) ? null : folders.FindFolderConflict(target, SafeDirectory(destination));
            }
            catch (Exception ex)
            {
                logger?.Debug(ex, "Talearr: recording folder check skipped for book {0}", target.Id);
                return null;
            }
        }

        // The folder an additional recording gets: the folder the naming format gives it, unless
        // an earlier recording already has that folder (computed, or holding its files today). Then
        // the first disambiguator that differs from the recordings it collides with is appended to
        // the book folder, else "Copy N".
        public static string Resolve(string folder,
                                     RecordingFolderKey self,
                                     IReadOnlyCollection<(string Folder, RecordingFolderKey Key)> taken,
                                     IReadOnlyCollection<(string Folder, RecordingFolderKey Key)> occupied)
        {
            if (string.IsNullOrWhiteSpace(folder) || self == null)
            {
                return folder;
            }

            taken ??= Array.Empty<(string, RecordingFolderKey)>();
            occupied ??= Array.Empty<(string, RecordingFolderKey)>();

            var colliders = Colliders(folder, taken, occupied).ToList();
            if (colliders.Count == 0)
            {
                return folder;
            }

            foreach (var (name, value) in self.Disambiguators())
            {
                if (string.IsNullOrWhiteSpace(value))
                {
                    continue;
                }

                var differs = colliders.All(c => !string.Equals(Value(c, name), value, StringComparison.OrdinalIgnoreCase));
                if (!differs)
                {
                    continue;
                }

                var candidate = $"{folder} ({value})";
                if (!Colliders(candidate, taken, occupied).Any())
                {
                    return candidate;
                }
            }

            for (var n = Math.Max(2, self.Ordinal + 1); n < self.Ordinal + 100; n++)
            {
                var candidate = $"{folder} (Copy {n})";
                if (!Colliders(candidate, taken, occupied).Any())
                {
                    return candidate;
                }
            }

            return folder;
        }

        public static string Normalize(string path)
        {
            return string.IsNullOrWhiteSpace(path) ? string.Empty : path.Replace('\\', '/').TrimEnd('/');
        }

        private static IEnumerable<RecordingFolderKey> Colliders(string folder,
                                                                 IReadOnlyCollection<(string Folder, RecordingFolderKey Key)> taken,
                                                                 IReadOnlyCollection<(string Folder, RecordingFolderKey Key)> occupied)
        {
            var relative = Normalize(folder);

            foreach (var entry in taken)
            {
                if (string.Equals(Normalize(entry.Folder), relative, StringComparison.OrdinalIgnoreCase))
                {
                    yield return entry.Key;
                }
            }

            // Absolute folders that hold files today: same folder when they end with the relative one.
            foreach (var entry in occupied)
            {
                var absolute = Normalize(entry.Folder);
                if (string.Equals(absolute, relative, StringComparison.OrdinalIgnoreCase) ||
                    absolute.EndsWith("/" + relative, StringComparison.OrdinalIgnoreCase))
                {
                    yield return entry.Key;
                }
            }
        }

        private static string Value(RecordingFolderKey key, string name)
        {
            return key.Disambiguators().FirstOrDefault(d => d.Name == name).Value;
        }

        private RecordingFolderContext BuildContext(Book book)
        {
            try
            {
                var group = _groupService.GetGroup(book);
                if (group == null || group.Books.Count < 2 || group.Primary.Id == book.Id)
                {
                    return null;
                }

                var ordered = new List<Book> { group.Primary };
                ordered.AddRange(group.Copies.OrderBy(b => b.Id));

                var ids = ordered.Select(b => b.Id).ToList();
                var editions = _editionService.GetEditionsByBook(ids) ?? new List<Edition>();
                var files = _mediaFileService.GetFilesByBooks(ids) ?? new List<BookFile>();
                var marketplace = TalearrRecordingsSettingsService.Resolve(_settingsService).Marketplace;
                var facts = SafeFacts(editions, marketplace);

                var context = new RecordingFolderContext { BookId = book.Id };
                for (var i = 0; i < ordered.Count; i++)
                {
                    var row = ordered[i];
                    var rowEditions = editions.Where(e => e.BookId == row.Id).ToList();
                    var monitored = rowEditions.Where(e => e.ManualAdd && e.Monitored).OrderBy(e => e.Id).FirstOrDefault()
                                    ?? rowEditions.Where(e => e.Monitored).OrderBy(e => e.Id).FirstOrDefault();
                    var editionIds = rowEditions.Select(e => e.Id).ToHashSet();
                    var rowFiles = files.Where(f => editionIds.Contains(f.EditionId)).OrderBy(f => f.Part).ThenBy(f => f.Path, StringComparer.Ordinal).ToList();
                    var asin = AsinOf(monitored, facts);

                    if (row.Id == book.Id)
                    {
                        context.Ordinal = i;
                        context.Asin = asin;
                        continue;
                    }

                    if (monitored == null)
                    {
                        continue;
                    }

                    monitored.Book ??= row;
                    var entry = new RecordingFolderRow
                    {
                        BookId = row.Id,
                        IsPrimary = row.Id == group.Primary.Id,
                        Ordinal = i,
                        Edition = monitored,
                        SampleFile = rowFiles.FirstOrDefault(),
                        FileFolders = rowFiles.Select(f => SafeDirectory(f.Path)).Where(d => !string.IsNullOrWhiteSpace(d)).Distinct(StringComparer.OrdinalIgnoreCase).ToList(),
                        Asin = asin
                    };

                    context.Others.Add(entry);
                }

                context.Earlier = context.Others.Where(o => o.Ordinal < context.Ordinal).ToList();
                return context;
            }
            catch (Exception ex)
            {
                _logger.Debug(ex, "Talearr: unable to read the other recordings of book {0}; folder left as the naming format gives it", book.Id);
                return null;
            }
        }

        private Dictionary<int, EditionAudibleFacts> SafeFacts(List<Edition> editions, string marketplace)
        {
            try
            {
                return _factsService?.GetCachedFacts(editions, marketplace) ?? new Dictionary<int, EditionAudibleFacts>();
            }
            catch (Exception ex)
            {
                _logger.Debug(ex, "Talearr: talearr.db unavailable for folder disambiguation");
                return new Dictionary<int, EditionAudibleFacts>();
            }
        }

        private static string AsinOf(Edition edition, Dictionary<int, EditionAudibleFacts> facts)
        {
            if (edition == null)
            {
                return null;
            }

            if (facts.TryGetValue(edition.Id, out var known) && known.HasProduct)
            {
                return known.Product.Asin;
            }

            return LooksLikeAudibleAsin(edition.AudibleASIN) ? edition.AudibleASIN.Trim() : null;
        }

        private static bool LooksLikeAudibleAsin(string value)
        {
            return !string.IsNullOrWhiteSpace(value) && value.Trim().Length == 10 && value.Trim().StartsWith("B", StringComparison.OrdinalIgnoreCase);
        }

        private static string SafeDirectory(string path)
        {
            try
            {
                return string.IsNullOrWhiteSpace(path) ? null : Path.GetDirectoryName(path);
            }
            catch (ArgumentException)
            {
                return null;
            }
        }
    }
}
