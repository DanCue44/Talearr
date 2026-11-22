using Chaptarr.Http;
using Microsoft.AspNetCore.Mvc;
using NzbDrone.Core.Talearr.PreferredSeries;

namespace Chaptarr.Api.V1.Talearr
{
    public class TalearrPreferredSeriesBody
    {
        // A series title from the book's options; null or empty goes back to the metadata's.
        public string SeriesTitle { get; set; }
    }

    // Talearr: the preferred series of a book in more than one series, under
    // /api/v1/talearr/preferredseries. The choice is kept in talearr.db and written onto the
    // book's own series columns, so the book routes return it unchanged in shape. bookId may be
    // any row of the work (audiobook, ebook or a recording copy).
    [V1ApiController("talearr/preferredseries")]
    public class TalearrPreferredSeriesController : Controller
    {
        private readonly ITalearrPreferredSeriesService _preferredSeriesService;

        public TalearrPreferredSeriesController(ITalearrPreferredSeriesService preferredSeriesService)
        {
            _preferredSeriesService = preferredSeriesService;
        }

        [HttpGet("{bookId:int}")]
        public ActionResult<TalearrPreferredSeriesDetails> Get(int bookId)
        {
            var details = _preferredSeriesService.Get(bookId);
            if (details == null)
            {
                return NotFound(new TalearrBookDetailsProblem { Message = $"Book {bookId} not found" });
            }

            return details;
        }

        [HttpPut("{bookId:int}")]
        public ActionResult<TalearrPreferredSeriesDetails> Set(int bookId, [FromBody] TalearrPreferredSeriesBody body)
        {
            var result = _preferredSeriesService.Set(bookId, body?.SeriesTitle);
            switch (result?.Status)
            {
                case TalearrPreferredSeriesStatus.Ok:
                    return result.Details;
                case TalearrPreferredSeriesStatus.NotFound:
                    return NotFound(new TalearrBookDetailsProblem { Message = result.Message });
                default:
                    return BadRequest(new TalearrBookDetailsProblem { Message = result?.Message ?? "Invalid request" });
            }
        }
    }
}
