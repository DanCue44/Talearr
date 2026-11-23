using Chaptarr.Http;
using Microsoft.AspNetCore.Mvc;
using NzbDrone.Core.Talearr.Titles;

namespace Chaptarr.Api.V1.Talearr
{
    public class TalearrTitleOverrideBody
    {
        // Null or blank clears the title, so the metadata title shows again.
        public string Title { get; set; }
    }

    // Talearr: Edit book > Title, under /api/v1/talearr/titleoverride. The title is shown on the
    // book routes and used for file and folder names; the Books row keeps the metadata title,
    // which searches use. Kept per work in talearr.db.
    [V1ApiController("talearr/titleoverride")]
    public class TalearrTitleOverrideController : Controller
    {
        private readonly ITalearrDisplayTitleService _displayTitles;

        public TalearrTitleOverrideController(ITalearrDisplayTitleService displayTitles)
        {
            _displayTitles = displayTitles;
        }

        [HttpGet("{bookId:int}")]
        public ActionResult<TalearrTitleOverrideDetails> Get(int bookId)
        {
            var details = _displayTitles.Get(bookId);
            if (details == null)
            {
                return NotFound(new TalearrBookDetailsProblem { Message = $"Book {bookId} not found" });
            }

            return details;
        }

        [HttpPut("{bookId:int}")]
        public ActionResult<TalearrTitleOverrideDetails> Set(int bookId, [FromBody] TalearrTitleOverrideBody body)
        {
            var result = _displayTitles.Set(bookId, body?.Title);
            switch (result?.Status)
            {
                case TalearrTitleOverrideStatus.Ok:
                    return result.Details;
                case TalearrTitleOverrideStatus.NotFound:
                    return NotFound(new TalearrBookDetailsProblem { Message = result.Message });
                default:
                    return BadRequest(new TalearrBookDetailsProblem { Message = result?.Message ?? "Invalid request" });
            }
        }
    }
}
