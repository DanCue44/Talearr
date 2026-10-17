using Chaptarr.Http;
using Microsoft.AspNetCore.Mvc;
using NzbDrone.Core.Talearr.Catalog;

namespace Chaptarr.Api.V1.Talearr
{
    public class TalearrMergeProblem
    {
        public string Message { get; set; }
    }

    // TaleArr: merge duplicate authors or book rows. New routes only.
    //   GET  /api/v1/talearr/merge/candidates                      possible duplicates
    //   POST /api/v1/talearr/merge/authors {"targetId": 1, "sourceIds": [2], "dryRun": true}
    //   POST /api/v1/talearr/merge/books   {"targetId": 10, "sourceIds": [11], "dryRun": true, "force": false}
    // dryRun runs the merge in a transaction and rolls it back, so the report is the exact preview.
    [V1ApiController("talearr/merge")]
    public class TalearrMergeController : Controller
    {
        private readonly ITalearrMergeService _mergeService;
        private readonly ITalearrDuplicateFinder _duplicateFinder;

        public TalearrMergeController(ITalearrMergeService mergeService, ITalearrDuplicateFinder duplicateFinder)
        {
            _mergeService = mergeService;
            _duplicateFinder = duplicateFinder;
        }

        [HttpGet("candidates")]
        public TalearrDuplicateCandidates GetCandidates()
        {
            return _duplicateFinder.Find();
        }

        [HttpPost("authors")]
        public ActionResult<TalearrMergeReport> MergeAuthors([FromBody] TalearrMergeRequest request)
        {
            try
            {
                return _mergeService.MergeAuthors(request);
            }
            catch (TalearrMergeException ex)
            {
                return BadRequest(new TalearrMergeProblem { Message = ex.Message });
            }
        }

        [HttpPost("books")]
        public ActionResult<TalearrMergeReport> MergeBooks([FromBody] TalearrMergeRequest request)
        {
            try
            {
                return _mergeService.MergeBooks(request);
            }
            catch (TalearrMergeException ex)
            {
                return BadRequest(new TalearrMergeProblem { Message = ex.Message });
            }
        }
    }
}
