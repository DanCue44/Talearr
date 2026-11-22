using Chaptarr.Http;
using Microsoft.AspNetCore.Mvc;
using NzbDrone.Core.Talearr.EditionLocks;

namespace Chaptarr.Api.V1.Talearr
{
    public class TalearrEditionLockBody
    {
        public bool Locked { get; set; }
    }

    // Talearr: Edit book > Lock edition, under /api/v1/talearr/editionlock. Locking keeps the
    // book's monitored edition through metadata refreshes and automatic switching and turns
    // Automatically Switch Edition off; the lock itself is kept in talearr.db.
    [V1ApiController("talearr/editionlock")]
    public class TalearrEditionLockController : Controller
    {
        private readonly ITalearrEditionLockService _editionLockService;

        public TalearrEditionLockController(ITalearrEditionLockService editionLockService)
        {
            _editionLockService = editionLockService;
        }

        [HttpGet("{bookId:int}")]
        public ActionResult<TalearrEditionLockDetails> Get(int bookId)
        {
            var details = _editionLockService.Get(bookId);
            if (details == null)
            {
                return NotFound(new TalearrBookDetailsProblem { Message = $"Book {bookId} not found" });
            }

            return details;
        }

        [HttpPut("{bookId:int}")]
        public ActionResult<TalearrEditionLockDetails> Set(int bookId, [FromBody] TalearrEditionLockBody body)
        {
            var result = _editionLockService.Set(bookId, body?.Locked == true);
            switch (result?.Status)
            {
                case TalearrEditionLockStatus.Ok:
                    return result.Details;
                case TalearrEditionLockStatus.NotFound:
                    return NotFound(new TalearrBookDetailsProblem { Message = result.Message });
                default:
                    return BadRequest(new TalearrBookDetailsProblem { Message = result?.Message ?? "Invalid request" });
            }
        }
    }
}
