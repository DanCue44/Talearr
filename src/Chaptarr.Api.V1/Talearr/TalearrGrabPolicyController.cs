using System.Text.Json.Serialization;
using Chaptarr.Http;
using Microsoft.AspNetCore.Mvc;
using NzbDrone.Core.Talearr.GrabPolicy;

namespace Chaptarr.Api.V1.Talearr
{
    public class TalearrBookGrabPolicyBody
    {
        // unabridgedOnly | abridgedOnly | preferUnabridged | any | null (null clears the override)
        public string AbridgedPolicy { get; set; }
    }

    public class TalearrBookGrabPolicyResource
    {
        public int BookId { get; set; }

        // The book's override, or null when it follows the global grab policy. Always written,
        // also when null (the API leaves other null fields out).
        [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
        public string AbridgedPolicy { get; set; }
        public string EffectivePolicy { get; set; }

        // book | global
        public string Source { get; set; }
    }

    public class TalearrGrabPolicyProblem
    {
        public string Message { get; set; }
    }

    // TaleArr: per-book grab policy under /api/v1/talearr/grabpolicy (AllSeerr's "Audiobook
    // versions", requester chooses). New routes only; the global policy stays at
    // /api/v1/config/talearrgrabpolicy. Overrides are stored in talearr.db, never chaptarr.db.
    [V1ApiController("talearr/grabpolicy")]
    public class TalearrGrabPolicyController : Controller
    {
        private readonly ITalearrBookGrabPolicyService _bookGrabPolicyService;

        public TalearrGrabPolicyController(ITalearrBookGrabPolicyService bookGrabPolicyService)
        {
            _bookGrabPolicyService = bookGrabPolicyService;
        }

        // bookId may be any row of the work, the ebook row included; the answer is for its audiobook.
        [HttpGet("book/{bookId:int}")]
        public ActionResult<TalearrBookGrabPolicyResource> GetBook(int bookId)
        {
            return ToActionResult(_bookGrabPolicyService.Get(bookId));
        }

        [HttpPut("book/{bookId:int}")]
        public ActionResult<TalearrBookGrabPolicyResource> SetBook(int bookId, [FromBody] TalearrBookGrabPolicyBody body)
        {
            AbridgedAudiobookPolicy? policy = null;
            var value = body?.AbridgedPolicy;
            if (!string.IsNullOrWhiteSpace(value))
            {
                if (!TalearrBookGrabPolicyService.TryParseApiValue(value, out var parsed))
                {
                    return BadRequest(new TalearrGrabPolicyProblem { Message = "abridgedPolicy must be unabridgedOnly, abridgedOnly, preferUnabridged, any or null" });
                }

                policy = parsed;
            }
            else if (value != null)
            {
                return BadRequest(new TalearrGrabPolicyProblem { Message = "abridgedPolicy must be unabridgedOnly, abridgedOnly, preferUnabridged, any or null" });
            }

            return ToActionResult(_bookGrabPolicyService.Set(bookId, policy));
        }

        private ActionResult<TalearrBookGrabPolicyResource> ToActionResult(BookGrabPolicyResult result)
        {
            switch (result?.Status)
            {
                case BookGrabPolicyStatus.Ok:
                    return new TalearrBookGrabPolicyResource
                    {
                        BookId = result.BookId,
                        AbridgedPolicy = result.AbridgedPolicy.HasValue ? TalearrBookGrabPolicyService.ToApiValue(result.AbridgedPolicy.Value) : null,
                        EffectivePolicy = TalearrBookGrabPolicyService.ToApiValue(result.EffectivePolicy),
                        Source = result.Source
                    };
                case BookGrabPolicyStatus.NotFound:
                    return NotFound(new TalearrGrabPolicyProblem { Message = result.Message });
                default:
                    return BadRequest(new TalearrGrabPolicyProblem { Message = result?.Message ?? "Invalid request" });
            }
        }
    }
}
