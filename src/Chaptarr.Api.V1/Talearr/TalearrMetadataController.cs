using Chaptarr.Http;
using Microsoft.AspNetCore.Mvc;
using NzbDrone.Core.Talearr.Metadata.Source;

namespace Chaptarr.Api.V1.Talearr
{
    public class TalearrMetadataSourceResource
    {
        // chaptarr | talearr
        public string Source { get; set; }

        // Phase 3a: the Talearr source only runs in shadow mode; nothing switches yet.
        public bool ShadowOnly { get; set; } = true;

        public TalearrHardcoverClientStatus Hardcover { get; set; }
    }

    public class TalearrMetadataSourceRequest
    {
        public string Source { get; set; }
    }

    // Talearr: Talearr's own metadata source (docs/TALEARR_METADATA_SOURCE.md) under
    // /api/v1/talearr/metadata. New routes only. The shadow endpoints are read only: they compare
    // the metadata server with Hardcover and never write books or authors.
    [V1ApiController("talearr/metadata")]
    public class TalearrMetadataController : Controller
    {
        private readonly ITalearrMetadataShadowService _shadowService;
        private readonly ITalearrMetadataSourceSettingsService _settings;
        private readonly ITalearrHardcoverMetadataClient _hardcover;

        public TalearrMetadataController(ITalearrMetadataShadowService shadowService,
                                         ITalearrMetadataSourceSettingsService settings,
                                         ITalearrHardcoverMetadataClient hardcover)
        {
            _shadowService = shadowService;
            _settings = settings;
            _hardcover = hardcover;
        }

        [HttpGet("source")]
        public TalearrMetadataSourceResource GetSource()
        {
            return ToResource();
        }

        // {"source": "chaptarr"} or {"source": "talearr"}. Stored as talearr.metadata.source.
        [HttpPut("source")]
        public ActionResult<TalearrMetadataSourceResource> SetSource([FromBody] TalearrMetadataSourceRequest request)
        {
            if (!TalearrMetadataSourceSettingsService.TryParse(request?.Source, out var source))
            {
                return BadRequest(new { message = "source must be chaptarr or talearr" });
            }

            _settings.SaveSource(source);
            return ToResource();
        }

        // The last TalearrMetadataShadowCompare run, kept in memory since start.
        [HttpGet("shadow")]
        public ActionResult<TalearrShadowRunReport> GetLastShadowRun()
        {
            var run = _shadowService.GetLastRun();
            return run == null
                ? NotFound(new { message = "No shadow comparison has run since start. Queue the TalearrMetadataShadowCompare command." })
                : run;
        }

        // Compares one local author now (one metadata server read and a few Hardcover requests).
        [HttpGet("shadow/{authorId:int}")]
        public ActionResult<TalearrShadowAuthorReport> CompareAuthor(int authorId)
        {
            var report = _shadowService.CompareAuthor(authorId);
            return report == null ? NotFound() : report;
        }

        private TalearrMetadataSourceResource ToResource()
        {
            return new TalearrMetadataSourceResource
            {
                Source = TalearrMetadataSourceSettingsService.ToApiValue(_settings.GetSource()),
                ShadowOnly = true,
                Hardcover = _hardcover.GetStatus()
            };
        }
    }
}
