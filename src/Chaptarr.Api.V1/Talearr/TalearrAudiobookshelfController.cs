using System;
using System.Collections.Generic;
using System.Linq;
using Chaptarr.Http;
using Microsoft.AspNetCore.Mvc;
using NzbDrone.Core.Books;
using NzbDrone.Core.Messaging.Commands;
using NzbDrone.Core.Talearr.Audiobookshelf;
using NzbDrone.Core.Talearr.GrabPolicy;

namespace Chaptarr.Api.V1.Talearr
{
    public class TalearrAbsItemResource
    {
        public int ConnectionId { get; set; }
        public string ConnectionName { get; set; }
        public string LibraryId { get; set; }
        public string ItemId { get; set; }
        public string Title { get; set; }
        public string Subtitle { get; set; }
        public string Authors { get; set; }
        public string Narrators { get; set; }
        public string Asin { get; set; }
        public string Isbn { get; set; }
        public double? DurationSeconds { get; set; }

        // abridged | unabridged | unknown
        public string Abridged { get; set; }
        public bool HasAudio { get; set; }
        public bool HasEbook { get; set; }
        public string EbookFormat { get; set; }
        public string Path { get; set; }

        // path | asin | isbn | title
        public string MatchedBy { get; set; }

        // True when the item is TaleArr's own import.
        public bool ManagedByTalearr { get; set; }
        public string Url { get; set; }
    }

    public class TalearrAbsBookResource
    {
        public int BookId { get; set; }
        public bool InAudiobookshelf { get; set; }

        // True when the decision engine skips releases for this book because of these items.
        public bool SkipsGrabs { get; set; }
        public string SkipReason { get; set; }
        public List<TalearrAbsItemResource> Items { get; set; } = new List<TalearrAbsItemResource>();
    }

    public class TalearrAbsConnectionStatusResource
    {
        public int Id { get; set; }
        public string Name { get; set; }
        public bool SyncLibrary { get; set; }
        public bool SkipOwned { get; set; }
        public DateTime? LastSyncAt { get; set; }
        public string LastError { get; set; }
        public int ItemCount { get; set; }
        public int MatchedCount { get; set; }
    }

    // TaleArr: Audiobookshelf library awareness under /api/v1/talearr/audiobookshelf. New routes
    // only; nothing upstream changes.
    [V1ApiController("talearr/audiobookshelf")]
    public class TalearrAudiobookshelfController : Controller
    {
        private readonly ITalearrAbsLibraryService _libraryService;
        private readonly ITalearrAbsConnections _connections;
        private readonly IBookService _bookService;
        private readonly IManageCommandQueue _commandQueueManager;

        public TalearrAudiobookshelfController(ITalearrAbsLibraryService libraryService,
                                               ITalearrAbsConnections connections,
                                               IBookService bookService,
                                               IManageCommandQueue commandQueueManager)
        {
            _libraryService = libraryService;
            _connections = connections;
            _bookService = bookService;
            _commandQueueManager = commandQueueManager;
        }

        [HttpGet("book/{bookId:int}")]
        public ActionResult<TalearrAbsBookResource> GetBook(int bookId)
        {
            Book book;
            try
            {
                book = _bookService.GetBook(bookId);
            }
            catch (InvalidOperationException)
            {
                book = null;
            }
            catch (NzbDrone.Core.Datastore.ModelNotFoundException)
            {
                book = null;
            }

            if (book == null)
            {
                return NotFound();
            }

            var items = _libraryService.GetItems(bookId);
            var owned = _libraryService.FindOwnedCopy(book);

            return new TalearrAbsBookResource
            {
                BookId = bookId,
                InAudiobookshelf = items.Count > 0,
                SkipsGrabs = owned != null,
                SkipReason = owned == null ? null : TalearrInAudiobookshelfSpecification.Describe(owned),
                Items = items.Select(ToResource).ToList()
            };
        }

        [HttpGet("status")]
        public List<TalearrAbsConnectionStatusResource> GetStatus()
        {
            var states = _libraryService.GetStates().ToDictionary(s => s.ConnectionId);

            return _connections.GetConnections().Select(c =>
            {
                states.TryGetValue(c.Id, out var state);
                return new TalearrAbsConnectionStatusResource
                {
                    Id = c.Id,
                    Name = c.Name,
                    SyncLibrary = c.SyncEnabled,
                    SkipOwned = c.SkipOwned,
                    LastSyncAt = state?.LastSyncAt,
                    LastError = state?.LastError,
                    ItemCount = state?.ItemCount ?? 0,
                    MatchedCount = state?.MatchedCount ?? 0
                };
            }).ToList();
        }

        // Queues a sync of one connection (connectionId) or all of them.
        [HttpPost("sync")]
        public ActionResult Sync([FromQuery] int? connectionId)
        {
            var command = _commandQueueManager.Push(new SyncAudiobookshelfLibraryCommand { ConnectionId = connectionId }, trigger: CommandTrigger.Manual);
            return Accepted(new { commandId = command.Id });
        }

        private static TalearrAbsItemResource ToResource(TalearrAbsBookItem item)
        {
            return new TalearrAbsItemResource
            {
                ConnectionId = item.Connection.Id,
                ConnectionName = item.Connection.Name,
                LibraryId = item.Item.LibraryId,
                ItemId = item.Item.ItemId,
                Title = item.Item.Title,
                Subtitle = item.Item.Subtitle,
                Authors = item.Item.Authors,
                Narrators = item.Item.Narrators,
                Asin = item.Item.Asin,
                Isbn = item.Item.Isbn,
                DurationSeconds = item.Item.DurationSeconds,
                Abridged = item.Abridged switch
                {
                    AbridgedMarker.Abridged => "abridged",
                    AbridgedMarker.Unabridged => "unabridged",
                    _ => "unknown"
                },
                HasAudio = item.Item.HasAudio,
                HasEbook = item.Item.HasEbook,
                EbookFormat = item.Item.EbookFormat,
                Path = item.Item.Path,
                MatchedBy = item.Match.Method,
                ManagedByTalearr = item.Match.Managed,
                Url = item.Url
            };
        }
    }
}
