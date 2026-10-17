using System.Collections.Generic;
using System.Linq;
using NLog;
using NzbDrone.Core.Books.Events;
using NzbDrone.Core.Messaging.Events;
using NzbDrone.Core.Tags;

namespace NzbDrone.Core.Talearr.Tags
{
    public enum TalearrTagApplyMode
    {
        Add,
        Remove,
        Replace
    }

    public interface ITalearrBookTagService
    {
        Dictionary<int, List<int>> GetAll();
        List<int> Get(int bookId);

        // Applies tags to each book (add, remove or replace) and returns the new tags per book.
        // Unknown tag ids are dropped.
        Dictionary<int, List<int>> Apply(IEnumerable<int> bookIds, IEnumerable<int> tagIds, TalearrTagApplyMode mode);
    }

    // TaleArr: book level tags. The labels are Chaptarr's own tags (Settings > Tags), so one tag
    // can mark authors and books; only the book links live in talearr.db.
    public class TalearrBookTagService : ITalearrBookTagService,
        IHandle<BookDeletedEvent>,
        IHandle<TagsUpdatedEvent>
    {
        private readonly ITalearrBookTagStore _store;
        private readonly ITagService _tagService;
        private readonly Logger _logger;

        public TalearrBookTagService(ITalearrBookTagStore store, ITagService tagService, Logger logger)
        {
            _store = store;
            _tagService = tagService;
            _logger = logger;
        }

        public Dictionary<int, List<int>> GetAll()
        {
            return _store.GetAll();
        }

        public List<int> Get(int bookId)
        {
            return _store.Get(bookId);
        }

        public Dictionary<int, List<int>> Apply(IEnumerable<int> bookIds, IEnumerable<int> tagIds, TalearrTagApplyMode mode)
        {
            var ids = (bookIds ?? Enumerable.Empty<int>()).Where(id => id > 0).Distinct().ToList();
            var known = _tagService.All().Select(t => t.Id).ToHashSet();
            var requested = (tagIds ?? Enumerable.Empty<int>()).Where(known.Contains).Distinct().ToList();

            var current = _store.GetAll();
            var result = new Dictionary<int, List<int>>();

            foreach (var bookId in ids)
            {
                var existing = current.TryGetValue(bookId, out var tags) ? tags : new List<int>();
                result[bookId] = Combine(existing, requested, mode);
            }

            if (!_store.Set(result))
            {
                return ids.ToDictionary(id => id, id => current.TryGetValue(id, out var tags) ? tags : new List<int>());
            }

            _logger.Debug("TaleArr: {0} tags on {1} book(s)", mode, ids.Count);
            return result;
        }

        public static List<int> Combine(IEnumerable<int> existing, IEnumerable<int> requested, TalearrTagApplyMode mode)
        {
            var current = new HashSet<int>(existing ?? Enumerable.Empty<int>());
            var change = new HashSet<int>(requested ?? Enumerable.Empty<int>());

            switch (mode)
            {
                case TalearrTagApplyMode.Add:
                    current.UnionWith(change);
                    break;
                case TalearrTagApplyMode.Remove:
                    current.ExceptWith(change);
                    break;
                default:
                    current = change;
                    break;
            }

            return current.OrderBy(t => t).ToList();
        }

        public void Handle(BookDeletedEvent message)
        {
            var ids = new List<int>();
            if (message.Book != null)
            {
                ids.Add(message.Book.Id);
            }

            if (message.DeletedBooks != null)
            {
                ids.AddRange(message.DeletedBooks.Where(b => b != null).Select(b => b.Id));
            }

            _store.RemoveBooks(ids);
        }

        // A tag deleted in Settings > Tags (Chaptarr only checks authors and providers before
        // deleting) must not linger on books, or a later tag could inherit its id.
        public void Handle(TagsUpdatedEvent message)
        {
            _store.RemoveTagsExcept(_tagService.All().Select(t => t.Id));
        }
    }
}
