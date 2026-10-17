using System;
using System.Collections.Generic;
using System.Linq;
using Chaptarr.Http;
using Microsoft.AspNetCore.Mvc;
using NzbDrone.Core.Talearr.Soulseek;

namespace Chaptarr.Api.V1.Talearr
{
    public class TalearrSoulseekSearchRequest
    {
        public int BookId { get; set; }
    }

    public class TalearrSoulseekGrabRequest
    {
        public string Token { get; set; }
        public string Key { get; set; }
    }

    public class TalearrSoulseekGrabResource
    {
        public string Id { get; set; }
        public int BookId { get; set; }
        public string MediaType { get; set; }
        public string Title { get; set; }
        public string Username { get; set; }
        public string Directory { get; set; }
        public int FileCount { get; set; }
        public long Size { get; set; }
        public long BytesTransferred { get; set; }
        public string State { get; set; }
        public string Message { get; set; }
        public string LocalFolder { get; set; }
        public bool IsActive { get; set; }
        public DateTime CreatedAt { get; set; }
        public DateTime UpdatedAt { get; set; }
    }

    public class TalearrSoulseekStatusResource
    {
        public bool Enabled { get; set; }
        public int ActiveGrabs { get; set; }
    }

    // TaleArr: Soulseek through slskd under /api/v1/talearr/soulseek (docs/TALEARR_SOULSEEK.md).
    // New routes only; upstream's release search and download client routes are unchanged.
    [V1ApiController("talearr/soulseek")]
    public class TalearrSoulseekController : Controller
    {
        private readonly ITalearrSoulseekService _soulseekService;
        private readonly ITalearrSoulseekSettingsService _settingsService;

        public TalearrSoulseekController(ITalearrSoulseekService soulseekService, ITalearrSoulseekSettingsService settingsService)
        {
            _soulseekService = soulseekService;
            _settingsService = settingsService;
        }

        [HttpGet("status")]
        public TalearrSoulseekStatusResource GetStatus()
        {
            return new TalearrSoulseekStatusResource
            {
                Enabled = _settingsService.Get().IsActive,
                ActiveGrabs = _soulseekService.GetGrabs(null).Count(g => g.IsActive)
            };
        }

        // Searches Soulseek for one book. Can take the search timeout plus a few seconds.
        [HttpPost("search")]
        public ActionResult<TalearrSoulseekSearchResult> Search([FromBody] TalearrSoulseekSearchRequest request)
        {
            return Run(() => _soulseekService.Search(request?.BookId ?? 0));
        }

        [HttpPost("grab")]
        public ActionResult<TalearrSoulseekGrabResource> Grab([FromBody] TalearrSoulseekGrabRequest request)
        {
            return Run(() => ToResource(_soulseekService.Grab(request?.Token, request?.Key)));
        }

        [HttpGet("grabs")]
        public List<TalearrSoulseekGrabResource> GetGrabs([FromQuery] int? bookId = null)
        {
            return _soulseekService.GetGrabs(bookId).Select(ToResource).ToList();
        }

        [HttpPost("grabs/refresh")]
        public List<TalearrSoulseekGrabResource> Refresh([FromQuery] int? bookId = null)
        {
            _soulseekService.Refresh();
            return _soulseekService.GetGrabs(bookId).Select(ToResource).ToList();
        }

        [HttpPost("grabs/{id}/cancel")]
        public ActionResult<TalearrSoulseekGrabResource> Cancel(string id)
        {
            return Run(() => ToResource(_soulseekService.Cancel(id)));
        }

        // Forgets a grab (cancelling it first if it is still running). Files are left alone.
        [HttpDelete("grabs/{id}")]
        public IActionResult Delete(string id)
        {
            _soulseekService.Remove(id);
            return Ok(new { });
        }

        private ActionResult<T> Run<T>(Func<T> action)
        {
            try
            {
                return action();
            }
            catch (TalearrSoulseekException ex)
            {
                return BadRequest(new { message = ex.Message });
            }
        }

        private static TalearrSoulseekGrabResource ToResource(TalearrSoulseekGrab grab)
        {
            return new TalearrSoulseekGrabResource
            {
                Id = grab.Id,
                BookId = grab.BookId,
                MediaType = grab.MediaType.ToString().ToLowerInvariant(),
                Title = grab.Title,
                Username = grab.Username,
                Directory = grab.Directory,
                FileCount = grab.Files?.Count ?? 0,
                Size = grab.Size,
                BytesTransferred = grab.BytesTransferred,
                State = grab.State.ToString().ToLowerInvariant(),
                Message = grab.Message,
                LocalFolder = grab.LocalFolder,
                IsActive = grab.IsActive,
                CreatedAt = grab.CreatedAt,
                UpdatedAt = grab.UpdatedAt
            };
        }
    }
}
