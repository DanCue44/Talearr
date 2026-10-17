using System;
using System.Linq;
using Chaptarr.Http;
using Microsoft.AspNetCore.Mvc;
using NzbDrone.Core.Talearr.Migration;

namespace Chaptarr.Api.V1.Talearr
{
    // TaleArr: migration from Readarr, Bookshelf or LazyLibrarian under /api/v1/talearr/migration
    // (docs/TALEARR_MIGRATION.md). New routes only. Preview and apply run as a command; poll GET
    // for the state and the dry-run report.
    [V1ApiController("talearr/migration")]
    public class TalearrMigrationController : Controller
    {
        private const long UploadLimitBytes = 2L * 1024 * 1024 * 1024;

        private readonly ITalearrMigrationService _migrationService;

        public TalearrMigrationController(ITalearrMigrationService migrationService)
        {
            _migrationService = migrationService;
        }

        [HttpGet]
        public TalearrMigrationStatus GetStatus()
        {
            return _migrationService.Status();
        }

        // {"source": "readarr", "url": "http://readarr:8787", "apiKey": "...", "mediaType": "auto",
        //  "pathMappings": [{"from": "/books", "to": "/ebooks"}]} or {"source": "readarr",
        //  "backupPath": "/config/readarr_backup_v0.4.18.zip"}. Reads the source and writes the plan;
        //  nothing is added.
        [HttpPost("preview")]
        public ActionResult<TalearrMigrationStatus> Preview([FromBody] TalearrMigrationRequest request)
        {
            return Run(() => _migrationService.StartPreview(request));
        }

        // A readarr.db or Readarr backup zip from the browser. Saved in
        // <config>/talearr-migration/ and returned as the backupPath to preview.
        [HttpPost("upload")]
        [RequestSizeLimit(UploadLimitBytes)]
        [RequestFormLimits(MultipartBodyLengthLimit = UploadLimitBytes)]
        public IActionResult Upload()
        {
            var file = Request.Form.Files.FirstOrDefault();
            if (file == null)
            {
                return BadRequest(new { message = "Choose a readarr.db or backup zip." });
            }

            try
            {
                using var stream = file.OpenReadStream();
                return Ok(new { backupPath = _migrationService.SaveUpload(file.FileName, stream) });
            }
            catch (TalearrMigrationException ex)
            {
                return BadRequest(new { message = ex.Message });
            }
        }

        // Carries out the last preview's plan.
        [HttpPost("apply")]
        public ActionResult<TalearrMigrationStatus> Apply()
        {
            return Run(() => _migrationService.StartApply());
        }

        [HttpDelete]
        public ActionResult<TalearrMigrationStatus> Discard()
        {
            return Run(() =>
            {
                _migrationService.Discard();
                return _migrationService.Status();
            });
        }

        private ActionResult<TalearrMigrationStatus> Run(Func<TalearrMigrationStatus> action)
        {
            try
            {
                return action();
            }
            catch (TalearrMigrationException ex)
            {
                return BadRequest(new { message = ex.Message });
            }
        }
    }
}
