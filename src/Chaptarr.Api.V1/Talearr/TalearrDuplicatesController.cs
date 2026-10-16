using Chaptarr.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using NzbDrone.Core.Talearr.Catalog;

namespace Chaptarr.Api.V1.Talearr
{
    public class TalearrDuplicateCleanupRequest
    {
        // Dry run unless false is sent explicitly.
        public bool? DryRun { get; set; }
        public int? AuthorId { get; set; }
    }

    // TaleArr: report and opt-in cleanup of book rows that store the same work twice for one
    // media type (unmonitored, file-less, automatic copies only). New routes only.
    //   GET  /api/v1/talearr/duplicates[?authorId=16]          dry run report
    //   POST /api/v1/talearr/duplicates/cleanup {"dryRun": false, "authorId": 16}
    [V1ApiController("talearr/duplicates")]
    public class TalearrDuplicatesController : Controller
    {
        private readonly ITalearrDuplicateBookCleanupService _cleanupService;

        public TalearrDuplicatesController(ITalearrDuplicateBookCleanupService cleanupService)
        {
            _cleanupService = cleanupService;
        }

        [HttpGet]
        public TalearrDuplicateCleanupReport GetReport([FromQuery] int? authorId = null)
        {
            return _cleanupService.Run(true, authorId);
        }

        [HttpPost("cleanup")]
        public TalearrDuplicateCleanupReport Cleanup([FromBody(EmptyBodyBehavior = EmptyBodyBehavior.Allow)] TalearrDuplicateCleanupRequest request = null)
        {
            var dryRun = request?.DryRun ?? true;
            return _cleanupService.Run(dryRun, request?.AuthorId);
        }
    }
}
