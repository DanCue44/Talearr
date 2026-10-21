using System.Collections.Generic;
using System.Linq;
using Chaptarr.Http;
using Microsoft.AspNetCore.Mvc;
using NzbDrone.Core.Talearr.Catalog;

namespace Chaptarr.Api.V1.Talearr
{
    public class TalearrMergeProblem
    {
        public string Message { get; set; }
    }

    public class TalearrMergeAllRequest
    {
        public bool DryRun { get; set; } = true;
    }

    public class TalearrMergeAllSkipped
    {
        public string Title { get; set; }
        public string AuthorName { get; set; }
        public string MediaType { get; set; }
        public string Reason { get; set; }
    }

    public class TalearrMergeAllReport
    {
        public bool DryRun { get; set; }
        public List<TalearrMergeReport> Merged { get; set; } = new List<TalearrMergeReport>();
        public List<TalearrMergeAllSkipped> Skipped { get; set; } = new List<TalearrMergeAllSkipped>();
    }

    // Talearr: merge duplicate authors or book rows. New routes only.
    //   GET  /api/v1/talearr/merge/candidates                      possible duplicates
    //   POST /api/v1/talearr/merge/authors {"targetId": 1, "sourceIds": [2], "dryRun": true}
    //   POST /api/v1/talearr/merge/books   {"targetId": 10, "sourceIds": [11], "dryRun": true, "force": false}
    //   POST /api/v1/talearr/merge/books/all {"dryRun": true}           every duplicate book set
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

        // Merges every duplicate book set into its suggested row (the one with files, then the
        // monitored one, as on the Duplicates page). Sets with a separate recording copy, or with
        // files on more than one row, are left for a person to decide.
        [HttpPost("books/all")]
        public TalearrMergeAllReport MergeAllBooks([FromBody(EmptyBodyBehavior = Microsoft.AspNetCore.Mvc.ModelBinding.EmptyBodyBehavior.Allow)] TalearrMergeAllRequest request = null)
        {
            var dryRun = request?.DryRun ?? true;
            var report = new TalearrMergeAllReport { DryRun = dryRun };

            foreach (var set in _duplicateFinder.Find().Books)
            {
                string reason = null;
                if (set.Books.Count < 2)
                {
                    continue;
                }

                if (set.Books.Any(b => b.RecordingCopy))
                {
                    reason = "a separate recording";
                }
                else if (set.Books.Count(b => b.FileCount > 0) > 1)
                {
                    reason = "more than one row has files";
                }

                if (reason == null)
                {
                    try
                    {
                        report.Merged.Add(_mergeService.MergeBooks(new TalearrMergeRequest
                        {
                            TargetId = set.Books[0].Id,
                            SourceIds = set.Books.Skip(1).Select(b => b.Id).ToList(),
                            DryRun = dryRun
                        }));
                        continue;
                    }
                    catch (TalearrMergeException ex)
                    {
                        reason = ex.Message;
                    }
                }

                report.Skipped.Add(new TalearrMergeAllSkipped
                {
                    Title = set.Title,
                    AuthorName = set.AuthorName,
                    MediaType = set.MediaType,
                    Reason = reason
                });
            }

            return report;
        }
    }
}
