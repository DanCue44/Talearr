using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using NLog;
using NzbDrone.Core.Books;
using NzbDrone.Core.IndexerSearch;
using NzbDrone.Core.MediaFiles;
using NzbDrone.Core.Messaging.Commands;
using NzbDrone.Core.Talearr.GrabPolicy;

namespace NzbDrone.Core.Talearr.Recordings
{
    public static class RecordingKinds
    {
        public const string Unabridged = "unabridged";
        public const string Abridged = "abridged";
        public const string Narrator = "narrator";
        public const string Edition = "edition";

        public static readonly IReadOnlyList<string> All = new[] { Unabridged, Abridged, Narrator, Edition };
    }

    public sealed class RecordingFilesInfo
    {
        public int Count { get; set; }
        public string Quality { get; set; }
        public string Codec { get; set; }
        public int? BitrateKbps { get; set; }
        public long SizeBytes { get; set; }
        public int? DurationMinutes { get; set; }
    }

    // Facts shared by recordings and candidates.
    public class RecordingEditionInfo
    {
        public int EditionId { get; set; }
        public string ForeignEditionId { get; set; }
        public string Title { get; set; }
        public List<string> Narrators { get; set; } = new List<string>();
        public string Abridged { get; set; }
        public string AbridgedSource { get; set; }
        public string AbridgedReason { get; set; }
        public int? RuntimeMinutes { get; set; }
        public string Publisher { get; set; }
        public string ReleaseDate { get; set; }
        public string Language { get; set; }
        public string Asin { get; set; }
        public string AudibleUrl { get; set; }
        public double? AudibleConfidence { get; set; }
    }

    public sealed class RecordingInfo : RecordingEditionInfo
    {
        // Local handle; store foreignEditionId / baseBookId instead.
        public int BookId { get; set; }

        // primary (the base row) | additional (a wanted or copy row)
        public string Role { get; set; }
        public bool Monitored { get; set; }
        public bool HasFiles { get; set; }
        public RecordingFilesInfo Files { get; set; }
        public string Path { get; set; }
        public bool CanRemove { get; set; }
    }

    public sealed class RecordingCandidateInfo : RecordingEditionInfo
    {
        // Near-identical editions folded into this candidate (same narrator, abridged status and runtime).
        public List<int> EditionIds { get; set; } = new List<int>();
    }

    public sealed class RecordingsView
    {
        public string BaseBookId { get; set; }
        public List<string> ProviderIdsAll { get; set; } = new List<string>();
        public string Marketplace { get; set; }
        public List<RecordingInfo> Recordings { get; set; } = new List<RecordingInfo>();
        public List<RecordingCandidateInfo> Candidates { get; set; } = new List<RecordingCandidateInfo>();
    }

    public sealed class AddRecordingRequest
    {
        public int BookId { get; set; }
        public string Kind { get; set; }
        public int? EditionId { get; set; }
        public string Narrator { get; set; }

        // Null: the talearr.recordings.searchonadd setting decides.
        public bool? Search { get; set; }
        public string RequestedBy { get; set; }
        public string ExternalRef { get; set; }
    }

    public enum AddRecordingStatus
    {
        // A new copy row was made (201).
        Created,

        // The base row had no files, so upstream re-pinned its wanted edition instead (201).
        Pinned,

        // That recording already exists (200).
        Existing,

        // Bad input or an ebook-only work (400).
        Invalid,

        // No edition fits the request (404).
        NoMatch,

        // Several candidates fit equally well (409).
        Ambiguous
    }

    public sealed class AddRecordingOutcome
    {
        public AddRecordingStatus Status { get; set; }
        public string Message { get; set; }
        public RecordingInfo Recording { get; set; }
        public bool SearchQueued { get; set; }
        public List<RecordingCandidateInfo> Candidates { get; set; } = new List<RecordingCandidateInfo>();
    }

    public enum RemoveRecordingStatus
    {
        Removed,
        NotFound,
        Refused
    }

    public sealed class RemoveRecordingOutcome
    {
        public RemoveRecordingStatus Status { get; set; }
        public string Message { get; set; }
    }

    public interface ITalearrRecordingsService
    {
        // Null when the book is unknown or its work has no audiobook rows.
        RecordingsView GetRecordings(int bookId, bool allLanguages = false);
        RecordingsView GetRecordings(Book anyRow, bool allLanguages = false);

        AddRecordingOutcome AddRecording(AddRecordingRequest request);

        // Removes an additional recording (a copy row). Never the primary row, never automatic.
        RemoveRecordingOutcome RemoveRecording(int bookId, bool deleteFiles);
    }

    // TaleArr: recordings of a work, built on upstream's wanted copy rows. A recording is one
    // audiobook row of the identity group with its monitored edition and files. New recordings are
    // created only through IBookService.AddWantedEdition, so stock Chaptarr understands every row.
    public class TalearrRecordingsService : ITalearrRecordingsService
    {
        private readonly IBookService _bookService;
        private readonly IEditionService _editionService;
        private readonly IMediaFileService _mediaFileService;
        private readonly ITalearrRecordingGroupService _groupService;
        private readonly ITalearrAudibleFactsService _factsService;
        private readonly ITalearrAbridgedResolver _abridgedResolver;
        private readonly ITalearrRecordingsStore _store;
        private readonly ITalearrRecordingsSettingsService _settingsService;
        private readonly IManageCommandQueue _commandQueueManager;
        private readonly Logger _logger;

        public TalearrRecordingsService(IBookService bookService,
                                        IEditionService editionService,
                                        IMediaFileService mediaFileService,
                                        ITalearrRecordingGroupService groupService,
                                        ITalearrAudibleFactsService factsService,
                                        ITalearrAbridgedResolver abridgedResolver,
                                        ITalearrRecordingsStore store,
                                        ITalearrRecordingsSettingsService settingsService,
                                        IManageCommandQueue commandQueueManager,
                                        Logger logger)
        {
            _bookService = bookService;
            _editionService = editionService;
            _mediaFileService = mediaFileService;
            _groupService = groupService;
            _factsService = factsService;
            _abridgedResolver = abridgedResolver;
            _store = store;
            _settingsService = settingsService;
            _commandQueueManager = commandQueueManager;
            _logger = logger;
        }

        public RecordingsView GetRecordings(int bookId, bool allLanguages = false)
        {
            return GetRecordings(_bookService.GetBook(bookId), allLanguages);
        }

        public RecordingsView GetRecordings(Book anyRow, bool allLanguages = false)
        {
            var context = Load(anyRow);
            return context == null ? null : BuildView(context, allLanguages);
        }

        public AddRecordingOutcome AddRecording(AddRecordingRequest request)
        {
            var kind = request?.Kind?.Trim().ToLowerInvariant();
            if (request == null || request.BookId <= 0 || !RecordingKinds.All.Contains(kind))
            {
                return Invalid("bookId and kind (unabridged, abridged, narrator or edition) are required");
            }

            if (kind == RecordingKinds.Narrator && string.IsNullOrWhiteSpace(request.Narrator))
            {
                return Invalid("kind narrator needs narrator");
            }

            if (kind == RecordingKinds.Edition && (!request.EditionId.HasValue || request.EditionId.Value <= 0))
            {
                return Invalid("kind edition needs editionId");
            }

            var book = _bookService.GetBook(request.BookId);
            if (book == null)
            {
                return new AddRecordingOutcome { Status = AddRecordingStatus.NoMatch, Message = $"Book {request.BookId} not found" };
            }

            var context = Load(book);
            if (context == null)
            {
                return Invalid("This book has no audiobook; recordings are audiobook only");
            }

            var view = BuildView(context, allLanguages: true);
            var candidates = AllCandidates(context);

            Edition chosen;
            if (kind == RecordingKinds.Edition)
            {
                var asked = context.EditionsByBook.Values.SelectMany(e => e).FirstOrDefault(e => e.Id == request.EditionId.Value);
                if (asked == null)
                {
                    return new AddRecordingOutcome { Status = AddRecordingStatus.NoMatch, Message = $"Edition {request.EditionId} is not an edition of this work", Candidates = view.Candidates };
                }

                var existing = FindRecordingFor(context, view, asked);
                if (existing != null)
                {
                    return new AddRecordingOutcome { Status = AddRecordingStatus.Existing, Recording = existing, Message = "That recording already exists" };
                }

                // AddWantedEdition needs an edition of the row it is called on: use the primary's copy.
                chosen = context.PrimaryEditions.FirstOrDefault(e => SameEditionKey(e, asked)) ?? asked;
            }
            else
            {
                // Asking for a recording the work already has (say "unabridged" when the primary is
                // the unabridged reading) is answered with that recording, not a near-duplicate copy.
                var existing = view.Recordings.FirstOrDefault(r => Fits(r, kind, request.Narrator));
                if (existing != null)
                {
                    return new AddRecordingOutcome { Status = AddRecordingStatus.Existing, Recording = existing, Message = "That recording already exists" };
                }

                var fitting = candidates.Where(c => Fits(c, kind, request.Narrator)).ToList();
                if (fitting.Count == 0)
                {
                    return new AddRecordingOutcome
                    {
                        Status = AddRecordingStatus.NoMatch,
                        Message = $"No {Describe(kind, request.Narrator)} edition is known for this book",
                        Candidates = view.Candidates
                    };
                }

                var ranked = fitting.OrderByDescending(c => c.Rank).ToList();
                if (ranked.Count > 1 && ranked[0].Rank.CompareTo(ranked[1].Rank) == 0)
                {
                    return new AddRecordingOutcome
                    {
                        Status = AddRecordingStatus.Ambiguous,
                        Message = "Several editions fit equally well; pick one with kind edition",
                        Candidates = ranked.Where(c => c.Rank.CompareTo(ranked[0].Rank) == 0).Select(c => c.Info).ToList()
                    };
                }

                chosen = ranked[0].Edition;
            }

            var before = context.Group.Books.Select(b => b.Id).ToHashSet();
            var primary = context.Group.Primary;
            var primaryHadFiles = context.FilesByBook.TryGetValue(primary.Id, out var primaryFiles) && primaryFiles.Count > 0;

            Book result;
            try
            {
                result = _bookService.AddWantedEdition(chosen.BookId, chosen.Id);
            }
            catch (ArgumentException ex)
            {
                return Invalid(ex.Message);
            }
            catch (InvalidOperationException ex)
            {
                return Invalid(ex.Message);
            }

            var status = !before.Contains(result.Id)
                ? AddRecordingStatus.Created
                : result.Id == chosen.BookId && !primaryHadFiles && result.Id == primary.Id
                    ? AddRecordingStatus.Pinned
                    : AddRecordingStatus.Existing;

            var search = request.Search ?? TalearrRecordingsSettingsService.Resolve(_settingsService).SearchOnAdd;
            var searchQueued = false;
            if (search && status != AddRecordingStatus.Existing)
            {
                _commandQueueManager.Push(new BookSearchCommand(new List<int> { result.Id }));
                searchQueued = true;
            }

            _store.AddRequest(new RecordingRequest
            {
                BaseBookId = primary.BaseBookId ?? $"book:{primary.Id}",
                CopyBookIdHint = result.Id,
                ForeignEditionId = TalearrEditionKey.For(chosen),
                Kind = kind,
                RequestedBy = string.IsNullOrWhiteSpace(request.RequestedBy) ? "api" : request.RequestedBy.Trim(),
                ExternalRef = request.ExternalRef
            });

            _logger.Info("TaleArr: {0} recording for '{1}' ({2}): edition {3}, book row {4}, requested by {5}{6}",
                status,
                primary.Title,
                kind,
                chosen.Id,
                result.Id,
                string.IsNullOrWhiteSpace(request.RequestedBy) ? "api" : request.RequestedBy,
                searchQueued ? ", search queued" : string.Empty);

            var refreshed = GetRecordings(result.Id, allLanguages: true);
            return new AddRecordingOutcome
            {
                Status = status,
                Recording = refreshed?.Recordings.FirstOrDefault(r => r.BookId == result.Id),
                SearchQueued = searchQueued
            };
        }

        public RemoveRecordingOutcome RemoveRecording(int bookId, bool deleteFiles)
        {
            var book = _bookService.GetBook(bookId);
            var group = book == null ? null : _groupService.GetGroup(book);
            if (group == null || book.MediaType != BookMediaType.Audiobook)
            {
                return new RemoveRecordingOutcome { Status = RemoveRecordingStatus.NotFound, Message = "No such audiobook recording" };
            }

            if (group.Primary.Id == book.Id)
            {
                return new RemoveRecordingOutcome { Status = RemoveRecordingStatus.Refused, Message = "The primary recording is the book itself; delete the book instead" };
            }

            _bookService.DeleteBook(book.Id, deleteFiles);
            _logger.Info("TaleArr: removed recording book row {0} of '{1}' (files {2})", book.Id, group.Primary.Title, deleteFiles ? "deleted" : "kept");

            return new RemoveRecordingOutcome { Status = RemoveRecordingStatus.Removed };
        }

        private AddRecordingOutcome Invalid(string message)
        {
            return new AddRecordingOutcome { Status = AddRecordingStatus.Invalid, Message = message };
        }

        private static string Describe(string kind, string narrator)
        {
            return kind == RecordingKinds.Narrator ? $"'{narrator}'" : kind;
        }

        private static bool Fits(RecordingEditionInfo info, string kind, string narrator)
        {
            switch (kind)
            {
                case RecordingKinds.Unabridged:
                    return info.Abridged == RecordingKinds.Unabridged;
                case RecordingKinds.Abridged:
                    return info.Abridged == RecordingKinds.Abridged;
                case RecordingKinds.Narrator:
                    return TalearrAudibleMatcher.NarratorsMatch(info.Narrators, new[] { narrator }) == true;
                default:
                    return false;
            }
        }

        private static bool Fits(Candidate candidate, string kind, string narrator)
        {
            return Fits(candidate.Info, kind, narrator);
        }

        private RecordingInfo FindRecordingFor(Context context, RecordingsView view, Edition edition)
        {
            return view.Recordings.FirstOrDefault(r =>
            {
                var monitored = context.MonitoredEdition(r.BookId);
                return monitored != null && SameEditionKey(monitored, edition);
            });
        }

        private static bool SameEditionKey(Edition a, Edition b)
        {
            if (a == null || b == null)
            {
                return false;
            }

            return (a.Id > 0 && a.Id == b.Id) || TalearrEditionKey.Same(a, b);
        }

        private Context Load(Book anyRow)
        {
            var group = anyRow == null ? null : _groupService.GetGroup(anyRow);
            if (group == null)
            {
                return null;
            }

            var ids = group.Books.Select(b => b.Id).ToList();
            var editions = _editionService.GetEditionsByBook(ids) ?? new List<Edition>();
            var files = _mediaFileService.GetFilesByBooks(ids) ?? new List<BookFile>();
            var settings = TalearrRecordingsSettingsService.Resolve(_settingsService);

            var editionsByBook = ids.ToDictionary(id => id, id => editions.Where(e => e.BookId == id).ToList());
            var editionIdToBook = editions.GroupBy(e => e.Id).ToDictionary(g => g.Key, g => g.First().BookId);
            var filesByBook = ids.ToDictionary(id => id, id => files.Where(f => editionIdToBook.TryGetValue(f.EditionId, out var b) && b == id).ToList());

            foreach (var book in group.Books)
            {
                book.Editions = editionsByBook[book.Id];
            }

            return new Context
            {
                Group = group,
                EditionsByBook = editionsByBook,
                FilesByBook = filesByBook,
                Marketplace = settings.Marketplace,
                Facts = _factsService.GetCachedFacts(editions, settings.Marketplace)
            };
        }

        private RecordingsView BuildView(Context context, bool allLanguages)
        {
            var primary = context.Group.Primary;
            var view = new RecordingsView
            {
                BaseBookId = primary.BaseBookId,
                ProviderIdsAll = BookIdentity.GetProviderIdentityTokens(primary).OrderBy(t => t, StringComparer.Ordinal).ToList(),
                Marketplace = context.Marketplace
            };

            foreach (var book in context.Group.Books)
            {
                view.Recordings.Add(BuildRecording(context, book));
            }

            var candidates = AllCandidates(context);
            var languages = view.Recordings
                .Select(r => r.Language)
                .Where(l => !string.IsNullOrWhiteSpace(l) && !l.Equals("und", StringComparison.OrdinalIgnoreCase))
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            view.Candidates = candidates
                .Where(c => allLanguages || languages.Count == 0 || string.IsNullOrWhiteSpace(c.Info.Language) || languages.Contains(c.Info.Language))
                .OrderByDescending(c => c.Rank)
                .Select(c => c.Info)
                .ToList();

            return view;
        }

        private RecordingInfo BuildRecording(Context context, Book book)
        {
            var edition = context.MonitoredEdition(book.Id);
            var files = context.FilesByBook.TryGetValue(book.Id, out var list) ? list : new List<BookFile>();
            var abridged = Resolve(() => _abridgedResolver.Resolve(book));

            var info = new RecordingInfo
            {
                BookId = book.Id,
                Role = book.Id == context.Group.Primary.Id ? "primary" : "additional",
                Monitored = book.Monitored && book.AudiobookMonitored,
                HasFiles = files.Count > 0,
                CanRemove = book.Id != context.Group.Primary.Id
            };

            FillEditionFacts(info, context, edition, abridged);

            if (info.Narrators.Count == 0)
            {
                info.Narrators = files.Select(f => f.Narrator).Where(n => !string.IsNullOrWhiteSpace(n)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            }

            if (files.Count > 0)
            {
                var seconds = files.Sum(f => (long)(f.DurationSeconds ?? 0));
                var first = files.OrderBy(f => f.Part).ThenBy(f => f.Path, StringComparer.Ordinal).First();
                info.Files = new RecordingFilesInfo
                {
                    Count = files.Count,
                    Quality = first.Quality?.Quality?.Name,
                    Codec = first.MediaInfo?.AudioFormat,
                    BitrateKbps = first.MediaInfo?.AudioBitrate > 0 ? first.MediaInfo.AudioBitrate : null,
                    SizeBytes = files.Sum(f => f.Size),
                    DurationMinutes = seconds > 0 ? (int?)(seconds / 60) : null
                };
                info.RuntimeMinutes ??= info.Files.DurationMinutes;
                info.Path = SafeDirectory(first.Path);
            }

            return info;
        }

        private void FillEditionFacts(RecordingEditionInfo info, Context context, Edition edition, AbridgedResolution abridged)
        {
            info.Abridged = Marker(abridged.Marker);
            info.AbridgedSource = abridged.Source;
            info.AbridgedReason = abridged.Reason;

            if (edition == null)
            {
                return;
            }

            context.Facts.TryGetValue(edition.Id, out var facts);
            var product = facts?.HasProduct == true ? facts.Product : null;

            info.EditionId = edition.Id;
            info.ForeignEditionId = edition.ForeignEditionId;
            info.Title = edition.Title;
            info.Narrators = product?.Narrators?.Count > 0 ? product.Narrators.ToList() : TalearrAudibleMatcher.EditionNarrators(edition);
            info.RuntimeMinutes = product?.RuntimeMinutes ?? (edition.DurationSeconds > 0 ? edition.DurationSeconds / 60 : null);
            info.Publisher = product?.Publisher ?? edition.Publisher;
            info.ReleaseDate = product?.ReleaseDate ?? edition.ReleaseDate?.ToString("yyyy-MM-dd");
            info.Language = edition.Language;
            info.Asin = product?.Asin;
            info.AudibleUrl = product != null ? TalearrAudibleMarketplaces.Get(product.Marketplace).ProductUrl(product.Asin) : null;
            info.AudibleConfidence = product != null ? facts.Link?.Confidence : null;
        }

        // Audiobook editions of the primary row that are not already a recording, with near
        // identical ones (same narrators, abridged status and runtime within 5%) folded together.
        private List<Candidate> AllCandidates(Context context)
        {
            var taken = context.Group.Books
                .Select(b => context.MonitoredEdition(b.Id))
                .Where(e => e != null)
                .ToList();

            var editions = context.PrimaryEditions
                .Where(TalearrAudibleRefreshService.IsAudiobookEdition)
                .Where(e => !taken.Any(t => SameEditionKey(t, e)))
                .ToList();

            var siblings = context.PrimaryEditions;
            var singles = editions.Select(edition =>
            {
                var abridged = Resolve(() => _abridgedResolver.ResolveEdition(edition, siblings));
                var info = new RecordingCandidateInfo();
                FillEditionFacts(info, context, edition, abridged);
                info.EditionIds.Add(edition.Id);
                return new Candidate { Edition = edition, Info = info, Rank = RankOf(context, edition, info) };
            })
            .OrderByDescending(c => c.Rank)
            .ToList();

            var groups = new List<Candidate>();
            foreach (var single in singles)
            {
                var into = groups.FirstOrDefault(g => NearIdentical(g.Info, single.Info));
                if (into == null)
                {
                    groups.Add(single);
                }
                else
                {
                    into.Info.EditionIds.Add(single.Edition.Id);
                }
            }

            return groups;
        }

        private static bool NearIdentical(RecordingEditionInfo a, RecordingEditionInfo b)
        {
            if (a.Abridged != b.Abridged || !string.Equals(a.Language ?? string.Empty, b.Language ?? string.Empty, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            var leftNames = a.Narrators.Select(TalearrAudibleMatcher.NormalizeName).Where(n => n.Length > 0).OrderBy(n => n).ToList();
            var rightNames = b.Narrators.Select(TalearrAudibleMatcher.NormalizeName).Where(n => n.Length > 0).OrderBy(n => n).ToList();
            if (!leftNames.SequenceEqual(rightNames))
            {
                return false;
            }

            if (a.RuntimeMinutes.HasValue && b.RuntimeMinutes.HasValue)
            {
                return TalearrAudibleMatcher.RuntimeMatches(a.RuntimeMinutes * 60, b.RuntimeMinutes) == true;
            }

            return true;
        }

        // Abridged status known, preferred language, Audible link confidence, runtime known,
        // upstream popularity. Compared as a tuple, highest first.
        private static CandidateRank RankOf(Context context, Edition edition, RecordingEditionInfo info)
        {
            var primaryLanguage = context.MonitoredEdition(context.Group.Primary.Id)?.Language;
            return new CandidateRank(
                info.Abridged != "unknown" ? 1 : 0,
                !string.IsNullOrWhiteSpace(primaryLanguage) && string.Equals(primaryLanguage, edition.Language, StringComparison.OrdinalIgnoreCase) ? 1 : 0,
                info.AudibleConfidence ?? 0,
                info.RuntimeMinutes.HasValue ? 1 : 0,
                (edition.Ratings?.Votes ?? 0) + (edition.ReviewCount ?? 0));
        }

        private AbridgedResolution Resolve(Func<AbridgedResolution> resolve)
        {
            try
            {
                return _abridgedResolver == null ? AbridgedResolution.Unknown : resolve();
            }
            catch (Exception ex)
            {
                _logger.Debug(ex, "TaleArr: abridged resolution failed");
                return AbridgedResolution.Unknown;
            }
        }

        private static string Marker(AbridgedMarker marker)
        {
            return marker switch
            {
                AbridgedMarker.Abridged => RecordingKinds.Abridged,
                AbridgedMarker.Unabridged => RecordingKinds.Unabridged,
                _ => "unknown"
            };
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

        private sealed class Context
        {
            public RecordingGroup Group { get; set; }
            public Dictionary<int, List<Edition>> EditionsByBook { get; set; }
            public Dictionary<int, List<BookFile>> FilesByBook { get; set; }
            public Dictionary<int, EditionAudibleFacts> Facts { get; set; }
            public string Marketplace { get; set; }

            public List<Edition> PrimaryEditions => EditionsByBook.TryGetValue(Group.Primary.Id, out var list) ? list : new List<Edition>();

            public Edition MonitoredEdition(int bookId)
            {
                if (!EditionsByBook.TryGetValue(bookId, out var list))
                {
                    return null;
                }

                return list.Where(e => e.ManualAdd && e.Monitored).OrderBy(e => e.Id).FirstOrDefault()
                       ?? list.Where(e => e.Monitored).OrderBy(e => e.Id).FirstOrDefault();
            }
        }

        private sealed class Candidate
        {
            public Edition Edition { get; set; }
            public RecordingCandidateInfo Info { get; set; }
            public CandidateRank Rank { get; set; }
        }

        private readonly struct CandidateRank : IComparable<CandidateRank>
        {
            private readonly int _abridgedKnown;
            private readonly int _language;
            private readonly double _confidence;
            private readonly int _runtimeKnown;
            private readonly int _popularity;

            public CandidateRank(int abridgedKnown, int language, double confidence, int runtimeKnown, int popularity)
            {
                _abridgedKnown = abridgedKnown;
                _language = language;
                _confidence = confidence;
                _runtimeKnown = runtimeKnown;
                _popularity = popularity;
            }

            public int CompareTo(CandidateRank other)
            {
                var result = _abridgedKnown.CompareTo(other._abridgedKnown);
                if (result == 0)
                {
                    result = _language.CompareTo(other._language);
                }

                if (result == 0)
                {
                    result = Math.Round(_confidence, 3).CompareTo(Math.Round(other._confidence, 3));
                }

                if (result == 0)
                {
                    result = _runtimeKnown.CompareTo(other._runtimeKnown);
                }

                return result == 0 ? _popularity.CompareTo(other._popularity) : result;
            }
        }
    }
}
