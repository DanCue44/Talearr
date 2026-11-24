using System;
using System.Collections.Generic;
using System.Linq;
using NLog;
using NzbDrone.Core.Books;
using NzbDrone.Core.Books.Events;
using NzbDrone.Core.History;
using NzbDrone.Core.MediaFiles;
using NzbDrone.Core.MediaFiles.Events;
using NzbDrone.Core.Messaging.Events;
using NzbDrone.Core.Queue;
using NzbDrone.Core.Talearr.EditionLocks;

namespace NzbDrone.Core.Talearr.Recordings
{
    public enum MakePrimaryStatus
    {
        Done,
        NotFound,
        Refused
    }

    public sealed class MakePrimaryOutcome
    {
        public MakePrimaryStatus Status { get; set; }
        public string Message { get; set; }

        // The book row that holds the primary recording (unchanged by a swap).
        public int PrimaryBookId { get; set; }
    }

    public interface ITalearrRecordingMaintenance
    {
        // Folds exact duplicate recordings of the work anyRow belongs to into one row. Returns the
        // ids of the rows it removed (none when there was nothing to do). Never throws.
        List<int> MergeDuplicates(Book anyRow);

        // Makes an additional recording the primary one: the two rows swap their recordings
        // (monitored edition and files); the primary row itself stays the book.
        MakePrimaryOutcome MakePrimary(int bookId);
    }

    // Talearr: keeps the recordings of a work free of duplicates and lets the user pick which
    // recording is the primary one.
    //
    // Duplicate merge (automatic, after an author refresh, after an import and when the
    // recordings of a book are loaded). Rows are the same recording per TalearrSameRecording.
    //   - The primary row has no files and an additional row holds the same recording's files:
    //     the files move (database only, nothing on disk) to the primary row's monitored edition,
    //     the additional row's history moves along, and the emptied row is removed.
    //   - An additional row without files that duplicates another row (the primary, a row with
    //     files, or an older empty row) is removed, its history moved to the row that stays.
    //   - Two rows that both hold files are left alone; Make Primary and Remove sort those out.
    //   - A row with a download in the queue is never removed.
    //
    // Make Primary swaps recordings, not rows: the primary row (the one metadata refresh,
    // Allseerr, edition locks, per-book policies, title overrides and book edits are keyed to)
    // stays the book; it takes over the additional row's monitored edition (its own copy of that
    // edition) and files, and the additional row takes the old primary recording.
    public class TalearrRecordingMaintenance : ITalearrRecordingMaintenance,
                                               IHandleAsync<AuthorRefreshCompleteEvent>,
                                               IHandleAsync<BookImportedEvent>
    {
        private static readonly object Sync = new object();

        private readonly IBookService _bookService;
        private readonly IEditionService _editionService;
        private readonly IMediaFileService _mediaFileService;
        private readonly ITalearrRecordingGroupService _groupService;
        private readonly ITalearrAudibleFactsService _factsService;
        private readonly IHistoryRepository _historyRepository;
        private readonly IQueueService _queueService;
        private readonly ITalearrEditionLockService _editionLocks;
        private readonly Logger _logger;

        public TalearrRecordingMaintenance(IBookService bookService,
                                           IEditionService editionService,
                                           IMediaFileService mediaFileService,
                                           ITalearrRecordingGroupService groupService,
                                           ITalearrAudibleFactsService factsService,
                                           Logger logger,
                                           IHistoryRepository historyRepository = null,
                                           IQueueService queueService = null,
                                           ITalearrEditionLockService editionLocks = null)
        {
            _bookService = bookService;
            _editionService = editionService;
            _mediaFileService = mediaFileService;
            _groupService = groupService;
            _factsService = factsService;
            _historyRepository = historyRepository;
            _queueService = queueService;
            _editionLocks = editionLocks;
            _logger = logger;
        }

        public void HandleAsync(AuthorRefreshCompleteEvent message)
        {
            var authorId = message?.Author?.Id ?? 0;
            if (authorId <= 0)
            {
                return;
            }

            try
            {
                // Only works with more than one audiobook row can hold a duplicate; a copy row
                // (wanted or import copy) is the sign of one.
                var done = new HashSet<int>();
                var copies = (_bookService.GetBooksByAuthor(authorId) ?? new List<Book>())
                    .Where(b => b != null && b.MediaType == BookMediaType.Audiobook && TalearrRecordingGroupService.IsCopySlug(b.TitleSlug))
                    .OrderBy(b => b.Id)
                    .ToList();

                foreach (var copy in copies)
                {
                    if (done.Contains(copy.Id))
                    {
                        continue;
                    }

                    var group = _groupService.GetGroup(copy);
                    foreach (var id in group?.Books.Select(b => b.Id) ?? Enumerable.Empty<int>())
                    {
                        done.Add(id);
                    }

                    Merge(group);
                }
            }
            catch (Exception ex)
            {
                _logger.Warn(ex, "Talearr: could not check the recordings of author {0} for duplicates", authorId);
            }
        }

        public void HandleAsync(BookImportedEvent message)
        {
            if (message?.Book == null || message.Book.MediaType != BookMediaType.Audiobook)
            {
                return;
            }

            MergeDuplicates(message.Book);
        }

        public List<int> MergeDuplicates(Book anyRow)
        {
            try
            {
                return Merge(anyRow == null ? null : _groupService.GetGroup(anyRow));
            }
            catch (Exception ex)
            {
                _logger.Warn(ex, "Talearr: could not check the recordings of '{0}' for duplicates", anyRow?.Title);
                return new List<int>();
            }
        }

        private List<int> Merge(RecordingGroup group)
        {
            var removed = new List<int>();
            if (group == null || group.Books.Count < 2)
            {
                return removed;
            }

            lock (Sync)
            {
                var state = Load(group);
                var primary = group.Primary;
                var primaryRecording = state.Recording(primary.Id);
                var queueRead = false;
                HashSet<int> queued = null;
                bool IsQueued(int id)
                {
                    if (!queueRead)
                    {
                        queued = QueuedBookIds();
                        queueRead = true;
                    }

                    // An unreadable queue counts as busy: never remove a row mid download.
                    return queued == null || queued.Contains(id);
                }

                // 1. The primary row is missing its files while a copy of the same recording has them.
                if (primaryRecording != null && state.Files(primary.Id).Count == 0)
                {
                    foreach (var copy in group.Copies.Where(c => state.Files(c.Id).Count > 0).OrderBy(c => c.Id))
                    {
                        var files = state.Files(copy.Id);
                        if (!files.All(f => TalearrSameRecording.Same(state.EditionOf(f) ?? state.Recording(copy.Id), primaryRecording, state.Facts)))
                        {
                            continue;
                        }

                        if (IsQueued(copy.Id))
                        {
                            _logger.Debug("Talearr: '{0}' book row {1} duplicates the primary recording but has a download in the queue; merging later", primary.Title, copy.Id);
                            continue;
                        }

                        foreach (var file in files)
                        {
                            file.EditionId = primaryRecording.Id;
                            file.Edition = primaryRecording;
                        }

                        _mediaFileService.Update(files);
                        MoveHistory(copy.Id, primary.Id);
                        _bookService.DeleteBook(copy.Id, false);

                        state.FilesByBook[primary.Id] = files;
                        state.FilesByBook[copy.Id] = new List<BookFile>();
                        removed.Add(copy.Id);

                        _logger.Info("Talearr: '{0}' had the same recording twice; moved {1} file(s) from book row {2} to the primary recording (edition {3}) and removed the duplicate row",
                            primary.Title, files.Count, copy.Id, primaryRecording.Id);
                        break;
                    }
                }

                // 2. Additional rows without files that duplicate a row that stays.
                foreach (var row in group.Copies.Where(c => !removed.Contains(c.Id)).OrderByDescending(c => c.Id).ToList())
                {
                    var recording = state.Recording(row.Id);
                    if (recording == null || state.Files(row.Id).Count > 0)
                    {
                        continue;
                    }

                    var keeper = group.Books
                        .Where(other => other.Id != row.Id && !removed.Contains(other.Id))
                        .Where(other => other.Id == primary.Id || state.Files(other.Id).Count > 0 || other.Id < row.Id)
                        .OrderByDescending(other => other.Id == primary.Id)
                        .ThenBy(other => other.Id)
                        .FirstOrDefault(other => TalearrSameRecording.Same(state.Recording(other.Id), recording, state.Facts));

                    if (keeper == null)
                    {
                        continue;
                    }

                    if (IsQueued(row.Id))
                    {
                        _logger.Debug("Talearr: '{0}' book row {1} duplicates book row {2} but has a download in the queue; merging later", primary.Title, row.Id, keeper.Id);
                        continue;
                    }

                    MoveHistory(row.Id, keeper.Id);
                    _bookService.DeleteBook(row.Id, false);
                    removed.Add(row.Id);

                    _logger.Info("Talearr: '{0}' had the same recording twice; removed book row {1}, which had no files, and kept book row {2}",
                        primary.Title, row.Id, keeper.Id);
                }
            }

            return removed;
        }

        public MakePrimaryOutcome MakePrimary(int bookId)
        {
            lock (Sync)
            {
                var book = bookId > 0 ? _bookService.GetBook(bookId) : null;
                var group = book == null || book.MediaType != BookMediaType.Audiobook ? null : _groupService.GetGroup(book);
                if (group == null)
                {
                    return new MakePrimaryOutcome { Status = MakePrimaryStatus.NotFound, Message = "No such audiobook recording" };
                }

                var primary = group.Primary;
                if (primary.Id == book.Id)
                {
                    return new MakePrimaryOutcome { Status = MakePrimaryStatus.Refused, Message = "This recording already is the primary one", PrimaryBookId = primary.Id };
                }

                var state = Load(group);
                var promoted = state.Recording(book.Id);
                if (promoted == null)
                {
                    return Refused(primary, "This recording has no edition to make primary");
                }

                var primaryEditions = state.Editions(primary.Id);
                var rowEditions = state.Editions(book.Id);

                // The primary row's own copy of the promoted edition: its editions come from the
                // metadata server, so files never land on an edition a refresh would remove.
                var target = FindTwin(primaryEditions, promoted, state.Facts);
                if (target == null)
                {
                    return Refused(primary, "The book has no edition matching this recording, so it cannot become the primary one; refresh the book and try again");
                }

                // The old primary recording moves to the additional row; that row gets a copy of
                // its edition when it has none (upstream's wanted copies clone the same way).
                var demoted = state.Recording(primary.Id);
                var demotedOnRow = demoted == null ? null : FindTwin(rowEditions, demoted, state.Facts);
                if (demoted != null && demotedOnRow == null)
                {
                    demotedOnRow = CloneOnto(demoted, book);
                    rowEditions.Add(demotedOnRow);
                }

                var promotedFiles = state.Files(book.Id);
                var demotedFiles = state.Files(primary.Id);

                if (demotedFiles.Count > 0 && demotedOnRow == null)
                {
                    return Refused(primary, "The primary recording has files but no edition, so it cannot be swapped");
                }

                Pin(primaryEditions, target);
                if (demotedOnRow != null)
                {
                    Pin(rowEditions, demotedOnRow);
                }

                _editionService.UpdateMany(primaryEditions);
                _editionService.UpdateMany(rowEditions.Where(e => e.Id > 0).ToList());

                foreach (var file in promotedFiles)
                {
                    file.EditionId = target.Id;
                    file.Edition = target;
                }

                foreach (var file in demotedFiles)
                {
                    file.EditionId = demotedOnRow.Id;
                    file.Edition = demotedOnRow;
                }

                var moved = promotedFiles.Concat(demotedFiles).ToList();
                if (moved.Count > 0)
                {
                    _mediaFileService.Update(moved);
                }

                // A user choice: both rows keep their edition (no automatic edition switching).
                primary.AnyEditionOk = false;
                book.AnyEditionOk = false;

                // The unit hash routed the promoted files' folder to the additional row; those
                // files now belong to the primary row, which finds them by path.
                if (!string.IsNullOrWhiteSpace(book.UnitKeyHash) && promotedFiles.Count > 0)
                {
                    book.UnitKeyHash = null;
                }

                _bookService.UpdateMany(new List<Book> { primary, book });

                _editionLocks?.FollowManualSelection(primary.Id, target.Id);
                if (demotedOnRow != null)
                {
                    _editionLocks?.FollowManualSelection(book.Id, demotedOnRow.Id);
                }

                _logger.Info("Talearr: '{0}': the recording of book row {1} (edition {2}, {3} file(s)) is now the primary recording; the old primary recording (edition {4}, {5} file(s)) is now book row {1}",
                    primary.Title, book.Id, target.Id, promotedFiles.Count, demoted?.Id.ToString() ?? "none", demotedFiles.Count);

                return new MakePrimaryOutcome { Status = MakePrimaryStatus.Done, PrimaryBookId = primary.Id };
            }
        }

        private static MakePrimaryOutcome Refused(Book primary, string message)
        {
            return new MakePrimaryOutcome { Status = MakePrimaryStatus.Refused, Message = message, PrimaryBookId = primary.Id };
        }

        // Exact identity first (the same provider edition), else the same recording.
        private static Edition FindTwin(List<Edition> editions, Edition edition, IReadOnlyDictionary<int, EditionAudibleFacts> facts)
        {
            return editions.FirstOrDefault(e => e.Id == edition.Id)
                   ?? editions.FirstOrDefault(e => TalearrEditionKey.Same(e, edition) || BookEditionIdentity.EditionsMatch(e, edition))
                   ?? editions.FirstOrDefault(e => TalearrSameRecording.Same(e, edition, facts));
        }

        private Edition CloneOnto(Edition source, Book row)
        {
            var clone = RefreshEntityCopy.CloneEdition(source);
            clone.Id = 0;
            clone.BookId = row.Id;
            clone.Book = row;
            clone.ForeignEditionId = BookEditionIdentity.GetTrustedForeignEditionId(source) ?? source.ForeignEditionId;
            clone.Monitored = false;
            clone.ManualAdd = false;

            _editionService.InsertMany(new List<Edition> { clone });
            return clone;
        }

        // Upstream's wanted edition pin: the edition monitored and marked as chosen, the rest not.
        private static void Pin(List<Edition> editions, Edition chosen)
        {
            foreach (var edition in editions)
            {
                var isChosen = ReferenceEquals(edition, chosen) || (chosen.Id > 0 && edition.Id == chosen.Id);
                edition.Monitored = isChosen;
                edition.ManualAdd = isChosen;
            }
        }

        private void MoveHistory(int fromBookId, int toBookId)
        {
            if (_historyRepository == null)
            {
                return;
            }

            try
            {
                var rows = _historyRepository.GetByBook(fromBookId, null) ?? new List<EntityHistory>();
                if (rows.Count == 0)
                {
                    return;
                }

                foreach (var row in rows)
                {
                    row.BookId = toBookId;
                }

                _historyRepository.UpdateMany(rows);
            }
            catch (Exception ex)
            {
                _logger.Debug(ex, "Talearr: could not move the history of book row {0} to {1}", fromBookId, toBookId);
            }
        }

        private HashSet<int> QueuedBookIds()
        {
            try
            {
                return (_queueService?.GetQueue() ?? new List<Queue.Queue>())
                    .Where(q => q?.Book != null)
                    .Select(q => q.Book.Id)
                    .ToHashSet();
            }
            catch (Exception ex)
            {
                // Unknown queue: treat every row as busy rather than remove one mid download.
                _logger.Debug(ex, "Talearr: could not read the queue; duplicate rows are kept for now");
                return null;
            }
        }

        private State Load(RecordingGroup group)
        {
            var ids = group.Books.Select(b => b.Id).ToList();
            var editions = _editionService.GetEditionsByBook(ids) ?? new List<Edition>();
            var files = _mediaFileService.GetFilesByBooks(ids) ?? new List<BookFile>();
            var editionsById = editions.GroupBy(e => e.Id).ToDictionary(g => g.Key, g => g.First());

            Dictionary<int, EditionAudibleFacts> facts;
            try
            {
                facts = _factsService?.GetCachedFacts(editions) ?? new Dictionary<int, EditionAudibleFacts>();
            }
            catch (Exception ex)
            {
                _logger.Debug(ex, "Talearr: Audible facts unavailable while comparing recordings");
                facts = new Dictionary<int, EditionAudibleFacts>();
            }

            return new State
            {
                EditionsByBook = ids.ToDictionary(id => id, id => editions.Where(e => e.BookId == id).OrderBy(e => e.Id).ToList()),
                FilesByBook = ids.ToDictionary(id => id, id => files.Where(f => editionsById.TryGetValue(f.EditionId, out var e) && e.BookId == id).ToList()),
                EditionsById = editionsById,
                Facts = facts
            };
        }

        private sealed class State
        {
            public Dictionary<int, List<Edition>> EditionsByBook { get; set; }
            public Dictionary<int, List<BookFile>> FilesByBook { get; set; }
            public Dictionary<int, Edition> EditionsById { get; set; }
            public Dictionary<int, EditionAudibleFacts> Facts { get; set; }

            public List<Edition> Editions(int bookId) => EditionsByBook.TryGetValue(bookId, out var list) ? list : new List<Edition>();

            public List<BookFile> Files(int bookId) => FilesByBook.TryGetValue(bookId, out var list) ? list : new List<BookFile>();

            public Edition EditionOf(BookFile file) => EditionsById.TryGetValue(file.EditionId, out var edition) ? edition : null;

            // The row's recording: its chosen (pinned) monitored edition, else its monitored one,
            // else the edition its files are on.
            public Edition Recording(int bookId)
            {
                var list = Editions(bookId);
                return list.FirstOrDefault(e => e.ManualAdd && e.Monitored)
                       ?? list.FirstOrDefault(e => e.Monitored)
                       ?? Files(bookId).Select(EditionOf).FirstOrDefault(e => e != null);
            }
        }
    }
}
