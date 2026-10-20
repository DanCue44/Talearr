using NLog;
using NzbDrone.Core.Books;
using NzbDrone.Core.Books.Commands;
using NzbDrone.Core.Datastore;
using NzbDrone.Core.Messaging.Commands;

namespace NzbDrone.Core.Talearr.Catalog
{
    public interface ITalearrAuthorCatalogService
    {
        TalearrAuthorCatalogMode GetMode(int authorId);

        // False when the author does not exist. A change queues a refresh of the author.
        bool SetMode(int authorId, TalearrAuthorCatalogMode mode);
    }

    // Talearr: switching an author between all books and only the user's books
    // (docs/TALEARR_TRACKED_AUTHORS.md). The refresh does the work: Full brings the catalog in
    // again, Tracked leaves it out so unmonitored, fileless, non-manual rows are pruned.
    public class TalearrAuthorCatalogService : ITalearrAuthorCatalogService
    {
        private readonly ITalearrAuthorCatalogStore _store;
        private readonly IAuthorService _authorService;
        private readonly IManageCommandQueue _commandQueueManager;
        private readonly Logger _logger;

        public TalearrAuthorCatalogService(ITalearrAuthorCatalogStore store,
                                           IAuthorService authorService,
                                           IManageCommandQueue commandQueueManager,
                                           Logger logger)
        {
            _store = store;
            _authorService = authorService;
            _commandQueueManager = commandQueueManager;
            _logger = logger;
        }

        public TalearrAuthorCatalogMode GetMode(int authorId)
        {
            return _store.Get(authorId);
        }

        public bool SetMode(int authorId, TalearrAuthorCatalogMode mode)
        {
            var author = FindAuthor(authorId);
            if (author == null)
            {
                return false;
            }

            if (_store.Get(authorId) == mode)
            {
                return true;
            }

            _store.Set(authorId, mode);
            _logger.Info("Author '{0}' now keeps {1}; refreshing the author", author.Name,
                mode == TalearrAuthorCatalogMode.Tracked ? "only its own books" : "all books by the author");

            _commandQueueManager.Push(new RefreshAuthorCommand(authorId, refreshMetadata: true, rescanFolders: false, forceRefresh: true));
            return true;
        }

        private Author FindAuthor(int authorId)
        {
            if (authorId <= 0)
            {
                return null;
            }

            try
            {
                return _authorService.GetAuthor(authorId);
            }
            catch (ModelNotFoundException)
            {
                return null;
            }
        }
    }
}
