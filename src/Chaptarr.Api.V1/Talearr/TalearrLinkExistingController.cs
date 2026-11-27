using System;
using System.Collections.Generic;
using System.Linq;
using Chaptarr.Http;
using Microsoft.AspNetCore.Mvc;
using NzbDrone.Core.Datastore;
using NzbDrone.Core.Talearr.LinkExisting;

namespace Chaptarr.Api.V1.Talearr
{
    public class TalearrLinkExistingRequest
    {
        public int BookId { get; set; }
        public List<string> Paths { get; set; } = new List<string>();
    }

    // Talearr: Link Existing Files in the Add Book modal under /api/v1/talearr/linkexisting.
    // New routes only; the add itself is unchanged and the link is a normal Manual Import.
    [V1ApiController("talearr/linkexisting")]
    public class TalearrLinkExistingController : Controller
    {
        private readonly ITalearrLinkExistingService _linkService;

        public TalearrLinkExistingController(ITalearrLinkExistingService linkService)
        {
            _linkService = linkService;
        }

        [HttpPost("candidates")]
        public List<TalearrLinkCandidate> GetCandidates([FromBody] TalearrLinkCandidateRequest request)
        {
            return _linkService.FindCandidates(request);
        }

        [HttpPost]
        public ActionResult<TalearrLinkResult> Link([FromBody] TalearrLinkExistingRequest request)
        {
            var paths = request?.Paths?.Where(p => !string.IsNullOrWhiteSpace(p)).ToList() ?? new List<string>();
            if (request == null || request.BookId <= 0 || paths.Count == 0)
            {
                return BadRequest(new { message = "A book and at least one path are required" });
            }

            try
            {
                return Ok(_linkService.Link(request.BookId, paths));
            }
            catch (ModelNotFoundException)
            {
                return NotFound();
            }
            catch (ArgumentException ex)
            {
                return BadRequest(new { message = ex.Message });
            }
        }
    }
}
