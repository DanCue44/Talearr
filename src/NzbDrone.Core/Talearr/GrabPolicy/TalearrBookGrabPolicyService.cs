using System;
using System.Collections.Generic;
using System.Linq;
using NLog;
using NzbDrone.Core.Books;
using NzbDrone.Core.Datastore;
using NzbDrone.Core.Talearr.Recordings;

namespace NzbDrone.Core.Talearr.GrabPolicy
{
    public static class AbridgedPolicySources
    {
        // A per-book override decides.
        public const string Book = "book";

        // The global grab policy decides.
        public const string Global = "global";

        // An additional recording (an upstream copy row) decides by what it is a recording of.
        public const string Recording = "recording";
    }

    public enum BookGrabPolicyStatus
    {
        Ok,

        // Unknown book (404).
        NotFound,

        // An ebook-only work or a bad value (400).
        Invalid
    }

    public sealed class BookGrabPolicyResult
    {
        public BookGrabPolicyStatus Status { get; set; }
        public string Message { get; set; }
        public int BookId { get; set; }

        // The override, or null when the book follows the global policy.
        public AbridgedAudiobookPolicy? AbridgedPolicy { get; set; }
        public AbridgedAudiobookPolicy EffectivePolicy { get; set; }

        // book | global
        public string Source { get; set; }
    }

    public interface ITalearrBookGrabPolicyService
    {
        // The override that applies to this audiobook row, or null. Additional recordings (copy
        // rows) and ebook rows never take an override.
        AbridgedAudiobookPolicy? GetOverride(Book row);

        // API: bookId may be any row of the work (ebook rows and additional recordings included);
        // the answer is for the work's audiobook.
        BookGrabPolicyResult Get(int bookId);

        // API: null clears the override.
        BookGrabPolicyResult Set(int bookId, AbridgedAudiobookPolicy? policy);
    }

    // TaleArr: per-book abridged policy overrides (AllSeerr's "Audiobook versions" setting with
    // "requester chooses"). Overrides live in talearr.db keyed by the work's provider id
    // (Book.BaseBookId, the same key the recordings code uses for a work), never only by row id:
    // the audiobook row and every duplicate row of the same work share it, and it survives a
    // refresh. A row without a BaseBookId (none on a normal library) falls back to "book:<id>",
    // the fallback the recordings code uses too. Nothing is written to chaptarr.db.
    public class TalearrBookGrabPolicyService : ITalearrBookGrabPolicyService
    {
        private static readonly TimeSpan CacheLifetime = TimeSpan.FromMinutes(5);

        private readonly ITalearrBookGrabPolicyStore _store;
        private readonly IBookService _bookService;
        private readonly ITalearrRecordingGroupService _groupService;
        private readonly ITalearrGrabPolicyService _grabPolicyService;
        private readonly Logger _logger;
        private readonly object _lock = new object();

        private Dictionary<string, AbridgedAudiobookPolicy> _cache;
        private DateTime _cachedAt;

        public TalearrBookGrabPolicyService(ITalearrBookGrabPolicyStore store,
                                            IBookService bookService,
                                            ITalearrRecordingGroupService groupService,
                                            ITalearrGrabPolicyService grabPolicyService,
                                            Logger logger)
        {
            _store = store;
            _bookService = bookService;
            _groupService = groupService;
            _grabPolicyService = grabPolicyService;
            _logger = logger;
        }

        public static string WorkKey(Book row)
        {
            if (row == null)
            {
                return null;
            }

            return !string.IsNullOrWhiteSpace(row.BaseBookId) ? row.BaseBookId.Trim() : $"book:{row.Id}";
        }

        // Null-safe accessor for optional dependencies (tests and partially wired services).
        public static AbridgedAudiobookPolicy? ResolveOverride(ITalearrBookGrabPolicyService service, Book row)
        {
            try
            {
                return service?.GetOverride(row);
            }
            catch (Exception)
            {
                return null;
            }
        }

        public static string ToApiValue(AbridgedAudiobookPolicy policy)
        {
            var name = policy.ToString();
            return char.ToLowerInvariant(name[0]) + name.Substring(1);
        }

        // Accepts the API spelling ("abridgedOnly"), any case. Numbers are refused.
        public static bool TryParseApiValue(string value, out AbridgedAudiobookPolicy policy)
        {
            return TalearrBookGrabPolicyStore.TryParse(value, out policy);
        }

        public AbridgedAudiobookPolicy? GetOverride(Book row)
        {
            if (row == null || row.MediaType != BookMediaType.Audiobook || TalearrRecordingGroupService.IsCopySlug(row.TitleSlug))
            {
                return null;
            }

            var key = WorkKey(row);
            return Overrides().TryGetValue(key, out var policy) ? policy : null;
        }

        public BookGrabPolicyResult Get(int bookId)
        {
            var target = FindTarget(bookId, out var problem);
            return target == null ? problem : Describe(bookId, target);
        }

        public BookGrabPolicyResult Set(int bookId, AbridgedAudiobookPolicy? policy)
        {
            if (policy.HasValue && !Enum.IsDefined(typeof(AbridgedAudiobookPolicy), policy.Value))
            {
                return Invalid(bookId, "abridgedPolicy must be unabridgedOnly, abridgedOnly, preferUnabridged, any or null");
            }

            var target = FindTarget(bookId, out var problem);
            if (target == null)
            {
                return problem;
            }

            var key = WorkKey(target);
            lock (_lock)
            {
                if (policy.HasValue)
                {
                    var edition = SafeEditions(target).Where(e => e.Monitored).OrderBy(e => e.Id).FirstOrDefault();
                    var written = _store.Upsert(new BookGrabPolicyOverride
                    {
                        WorkKey = key,
                        AbridgedPolicy = policy.Value,
                        BookIdHint = target.Id,
                        ForeignEditionId = TalearrEditionKey.For(edition),
                        Title = target.Title,
                        UpdatedAt = DateTime.UtcNow
                    });

                    if (!written)
                    {
                        _logger.Warn("TaleArr: the abridged policy for '{0}' could not be saved to {1}", target.Title, TalearrDatabase.FileName);
                    }
                }
                else
                {
                    _store.Delete(key);
                }

                _cache = null;
            }

            _logger.Info("TaleArr: abridged policy for '{0}' ({1}) {2}",
                target.Title,
                key,
                policy.HasValue ? "set to " + ToApiValue(policy.Value) : "cleared, it follows the global grab policy");

            return Describe(bookId, target);
        }

        private BookGrabPolicyResult Describe(int bookId, Book target)
        {
            var global = TalearrGrabPolicyService.Resolve(_grabPolicyService).AbridgedPolicy;
            var value = _store.Get(WorkKey(target))?.AbridgedPolicy;

            return new BookGrabPolicyResult
            {
                Status = BookGrabPolicyStatus.Ok,
                BookId = bookId,
                AbridgedPolicy = value,
                EffectivePolicy = value ?? global,
                Source = value.HasValue ? AbridgedPolicySources.Book : AbridgedPolicySources.Global
            };
        }

        // The audiobook row an override is about: the row itself, or for an ebook row or an
        // additional recording the work's primary audiobook row.
        private Book FindTarget(int bookId, out BookGrabPolicyResult problem)
        {
            problem = null;

            Book book;
            try
            {
                book = bookId > 0 ? _bookService.GetBook(bookId) : null;
            }
            catch (ModelNotFoundException)
            {
                book = null;
            }
            catch (InvalidOperationException)
            {
                book = null;
            }

            if (book == null)
            {
                problem = new BookGrabPolicyResult { Status = BookGrabPolicyStatus.NotFound, BookId = bookId, Message = $"Book {bookId} not found" };
                return null;
            }

            if (book.MediaType == BookMediaType.Audiobook && !TalearrRecordingGroupService.IsCopySlug(book.TitleSlug))
            {
                return book;
            }

            RecordingGroup group;
            try
            {
                group = _groupService.GetGroup(book);
            }
            catch (Exception ex)
            {
                _logger.Debug(ex, "TaleArr: could not find the audiobook rows of book {0}", bookId);
                group = null;
            }

            if (group?.Primary == null)
            {
                problem = Invalid(bookId, "This book has no audiobook; the abridged policy is audiobook only");
                return null;
            }

            return group.Primary;
        }

        private Dictionary<string, AbridgedAudiobookPolicy> Overrides()
        {
            lock (_lock)
            {
                if (_cache == null || _cachedAt + CacheLifetime < DateTime.UtcNow)
                {
                    _cache = _store.GetAll();
                    _cachedAt = DateTime.UtcNow;
                }

                return _cache;
            }
        }

        private static BookGrabPolicyResult Invalid(int bookId, string message)
        {
            return new BookGrabPolicyResult { Status = BookGrabPolicyStatus.Invalid, BookId = bookId, Message = message };
        }

        private static List<Edition> SafeEditions(Book book)
        {
            try
            {
                return book.Editions?.Where(e => e != null).ToList() ?? new List<Edition>();
            }
            catch (Exception)
            {
                return new List<Edition>();
            }
        }
    }
}
