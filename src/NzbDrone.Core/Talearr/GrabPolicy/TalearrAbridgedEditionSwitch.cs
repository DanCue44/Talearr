using System;
using System.Collections.Generic;
using System.Linq;
using NLog;
using NzbDrone.Core.Books;
using NzbDrone.Core.History;
using NzbDrone.Core.MediaFiles;
using NzbDrone.Core.Qualities;
using NzbDrone.Core.Talearr.Recordings;

namespace NzbDrone.Core.Talearr.GrabPolicy
{
    public interface ITalearrAbridgedEditionSwitch
    {
        // Called by ReleaseSearchService before a book search builds its criteria. May re-pin the
        // book's monitored edition (and then reloads book.Editions). Never throws.
        void Apply(Book book);
    }

    // TaleArr: when a book's effective abridged policy wants the other reading than its monitored
    // edition, pick a matching edition before searching:
    //
    //   - "abridged only" (per book or global) and the monitored edition is not abridged: the best
    //     abridged edition (TalearrRecordingsService.FindBestEdition, the "Add another recording"
    //     ranking),
    //   - "unabridged only" set for this book and the monitored edition is abridged: the best
    //     unabridged edition.
    //
    // The edition is pinned through upstream's IBookService.AddWantedEdition (the same pin the
    // recordings code uses for a row without files), so stock Chaptarr understands the result.
    // Rows with files, additional recordings and unmonitored books are left alone. When no
    // matching edition is known the search goes ahead (the decision rules then find nothing that
    // fits) and the book's history says why, at most once a day.
    public class TalearrAbridgedEditionSwitch : ITalearrAbridgedEditionSwitch
    {
        public const string NotePrefix = "TaleArr:";
        private static readonly TimeSpan NoteInterval = TimeSpan.FromHours(24);

        private readonly IBookService _bookService;
        private readonly IEditionService _editionService;
        private readonly IMediaFileService _mediaFileService;
        private readonly ITalearrRecordingsService _recordingsService;
        private readonly ITalearrAbridgedResolver _abridgedResolver;
        private readonly ITalearrGrabPolicyService _grabPolicyService;
        private readonly ITalearrBookGrabPolicyService _bookGrabPolicyService;
        private readonly IHistoryRepository _historyRepository;
        private readonly Logger _logger;

        public TalearrAbridgedEditionSwitch(IBookService bookService,
                                            IEditionService editionService,
                                            IMediaFileService mediaFileService,
                                            ITalearrRecordingsService recordingsService,
                                            ITalearrAbridgedResolver abridgedResolver,
                                            ITalearrGrabPolicyService grabPolicyService,
                                            ITalearrBookGrabPolicyService bookGrabPolicyService,
                                            IHistoryRepository historyRepository,
                                            Logger logger)
        {
            _bookService = bookService;
            _editionService = editionService;
            _mediaFileService = mediaFileService;
            _recordingsService = recordingsService;
            _abridgedResolver = abridgedResolver;
            _grabPolicyService = grabPolicyService;
            _bookGrabPolicyService = bookGrabPolicyService;
            _historyRepository = historyRepository;
            _logger = logger;
        }

        // The ReleaseSearchService hook: optional, lazy and never throws (an unresolvable service
        // only means the search runs on the monitored edition as before).
        public static void ApplyBeforeSearch(Lazy<ITalearrAbridgedEditionSwitch> editionSwitch, Book book, Logger logger)
        {
            if (editionSwitch == null || book == null)
            {
                return;
            }

            try
            {
                editionSwitch.Value?.Apply(book);
            }
            catch (Exception ex)
            {
                logger?.Debug(ex, "TaleArr: abridged edition switch unavailable, searching the monitored edition");
            }
        }

        public void Apply(Book book)
        {
            try
            {
                ApplyCore(book);
            }
            catch (Exception ex)
            {
                _logger.Warn(ex, "TaleArr: could not check the abridged edition of '{0}' before searching", book?.Title);
            }
        }

        private void ApplyCore(Book book)
        {
            if (book == null || book.Id <= 0 || book.MediaType != BookMediaType.Audiobook ||
                TalearrRecordingGroupService.IsCopySlug(book.TitleSlug))
            {
                return;
            }

            var global = TalearrGrabPolicyService.Resolve(_grabPolicyService).AbridgedPolicy;
            var target = TalearrAbridgedRules.Target(book, global, _bookGrabPolicyService, _abridgedResolver);

            string kind;
            if (target.Policy == AbridgedAudiobookPolicy.AbridgedOnly)
            {
                kind = RecordingKinds.Abridged;
            }
            else if (target.Policy == AbridgedAudiobookPolicy.UnabridgedOnly && target.IsStrict)
            {
                kind = RecordingKinds.Unabridged;
            }
            else
            {
                return;
            }

            var current = _abridgedResolver?.Resolve(book)?.Marker ??
                          (AbridgedReleaseDetector.IsAbridgedBook(book) ? AbridgedMarker.Abridged : AbridgedMarker.Unknown);

            // An unmarked edition is the unabridged reading for practical purposes; only an
            // edition known to be abridged needs replacing for "unabridged only".
            if (kind == RecordingKinds.Abridged ? current == AbridgedMarker.Abridged : current != AbridgedMarker.Abridged)
            {
                return;
            }

            if (!book.IsMonitoredWithAuthor())
            {
                return;
            }

            var files = _mediaFileService.GetFilesByBook(book.Id) ?? new List<BookFile>();
            if (files.Count > 0)
            {
                _logger.Debug("TaleArr: '{0}' has files; its monitored edition is kept even though the abridged policy is {1}", book.Title, TalearrAbridgedRules.Describe(target.Policy));
                return;
            }

            var policyText = $"abridged policy {TalearrAbridgedRules.Describe(target.Policy)}{(target.IsStrict ? " for this book" : string.Empty)}";
            var edition = _recordingsService.FindBestEdition(book, kind);
            if (edition == null)
            {
                Note(book, $"no {kind} edition of this book is known, so the search can only find releases marked {kind} ({policyText})", throttle: true);
                return;
            }

            _bookService.AddWantedEdition(book.Id, edition.Id);
            book.Editions = _editionService.GetEditionsByBook(book.Id);

            _logger.Info("TaleArr: '{0}' now monitors the {1} edition {2} ({3}) before searching ({4})", book.Title, kind, edition.Id, edition.Title, policyText);
            Note(book, $"monitored edition switched to the {kind} edition '{edition.Title}' ({policyText})", throttle: false);
        }

        // A DownloadIgnored history row (an existing event type, so Chaptarr shows it after a
        // rollback) with the reason in Data["Message"], as TalearrRetryService does for failures.
        private void Note(Book book, string text, bool throttle)
        {
            var message = NotePrefix + " " + text;

            try
            {
                if (throttle)
                {
                    var since = DateTime.UtcNow - NoteInterval;
                    var recent = _historyRepository.GetByBook(book.Id, EntityHistoryEventType.DownloadIgnored)
                        .Any(h => h.Date >= since && h.Data != null && h.Data.TryGetValue("Message", out var existing) &&
                                  string.Equals(existing, message, StringComparison.Ordinal));
                    if (recent)
                    {
                        return;
                    }
                }

                var history = new EntityHistory
                {
                    EventType = EntityHistoryEventType.DownloadIgnored,
                    Date = DateTime.UtcNow,
                    Quality = new QualityModel(Quality.Unknown),
                    SourceTitle = book.Title ?? string.Empty,
                    AuthorId = book.AuthorId,
                    BookId = book.Id,
                    EditionId = book.Editions?.Where(e => e != null && e.Monitored).OrderBy(e => e.Id).FirstOrDefault()?.Id ?? 0
                };

                history.Data.Add("Message", message);
                _historyRepository.Insert(history);
            }
            catch (Exception ex)
            {
                _logger.Debug(ex, "TaleArr: could not add a history note for '{0}'", book.Title);
            }

            _logger.Info("{0}: {1}", book.Title, message);
        }
    }
}
