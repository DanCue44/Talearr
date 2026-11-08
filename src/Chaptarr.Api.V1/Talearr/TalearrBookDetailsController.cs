using System.Collections.Generic;
using Chaptarr.Http;
using Microsoft.AspNetCore.Mvc;
using NzbDrone.Core.Talearr.BookEdits;

namespace Chaptarr.Api.V1.Talearr
{
    public class TalearrBookDetailsProblem
    {
        public string Message { get; set; }
    }

    // Talearr: "Edit book details" under /api/v1/talearr/bookdetails. The user's title, series and
    // narrator edits are kept in talearr.db and written onto the book's own rows, so the existing
    // book and series routes return them unchanged in shape; these routes only read and change
    // the edits. bookId may be any row of the work (audiobook, ebook or a recording copy).
    //
    // PUT body: { "title": "...", "series": [{ "title": "...", "position": "2", "seriesId": 12 }],
    //             "narrators": ["..."], "reset": ["title", "series", "narrators"] }
    // A field left out (or null) is left as it is; "reset" puts the provider's values back first.
    [V1ApiController("talearr/bookdetails")]
    public class TalearrBookDetailsController : Controller
    {
        private readonly ITalearrBookEditService _bookEditService;

        public TalearrBookDetailsController(ITalearrBookEditService bookEditService)
        {
            _bookEditService = bookEditService;
        }

        [HttpGet("{bookId:int}")]
        public ActionResult<TalearrBookDetails> Get(int bookId)
        {
            return ToResponse(_bookEditService.Get(bookId));
        }

        [HttpPut("{bookId:int}")]
        public ActionResult<TalearrBookDetails> Set(int bookId, [FromBody] TalearrBookEditChange body)
        {
            return ToResponse(_bookEditService.Save(bookId, body));
        }

        // Clears edits and puts the provider's values back. ?field=title|series|narrators (repeatable);
        // no field clears every edit of the book.
        [HttpDelete("{bookId:int}")]
        public ActionResult<TalearrBookDetails> Clear(int bookId, [FromQuery] List<string> field)
        {
            return ToResponse(_bookEditService.Clear(bookId, field));
        }

        private ActionResult<TalearrBookDetails> ToResponse(TalearrBookEditResult result)
        {
            switch (result?.Status)
            {
                case TalearrBookEditStatus.Ok:
                    return result.Details;
                case TalearrBookEditStatus.NotFound:
                    return NotFound(new TalearrBookDetailsProblem { Message = result.Message });
                default:
                    return BadRequest(new TalearrBookDetailsProblem { Message = result?.Message ?? "Invalid request" });
            }
        }
    }
}
