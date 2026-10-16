using System.Linq;
using Chaptarr.Http;
using Microsoft.AspNetCore.Mvc;
using NzbDrone.Core.Messaging.Commands;
using NzbDrone.Core.Talearr.Recordings;

namespace Chaptarr.Api.V1.Talearr
{
    // TaleArr: recordings routes under /api/v1/talearr/recordings. New routes only; no existing
    // route, field or status code changes.
    [V1ApiController("talearr/recordings")]
    public class TalearrRecordingsController : Controller
    {
        private readonly ITalearrRecordingGroupService _groupService;
        private readonly IManageCommandQueue _commandQueueManager;

        public TalearrRecordingsController(ITalearrRecordingGroupService groupService,
                                           IManageCommandQueue commandQueueManager)
        {
            _groupService = groupService;
            _commandQueueManager = commandQueueManager;
        }

        // Refetches Audible facts for every recording of the book's work, ignoring the cache.
        [HttpPost("refresh")]
        public IActionResult Refresh([FromQuery] int bookId)
        {
            var group = bookId > 0 ? _groupService.GetGroup(bookId) : null;
            if (group == null)
            {
                return NotFound(new { message = "No audiobook recordings for that book" });
            }

            var command = _commandQueueManager.Push(new RefreshAudibleRecordingFactsCommand(group.Books.Select(b => b.Id).ToList(), force: true));

            return Accepted(new { commandId = command?.Id, bookIds = group.Books.Select(b => b.Id).ToList() });
        }
    }
}
