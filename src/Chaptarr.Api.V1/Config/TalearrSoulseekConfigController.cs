using System.Collections.Generic;
using Chaptarr.Http;
using Chaptarr.Http.REST;
using FluentValidation;
using Microsoft.AspNetCore.Mvc;
using NzbDrone.Core.Talearr.Soulseek;
using NzbDrone.Core.Validation.Paths;
using NzbDrone.Http.REST.Attributes;

namespace Chaptarr.Api.V1.Config
{
    // Talearr: Soulseek (slskd) settings. A new route; no existing route or field changes. Stored
    // as the talearr.soulseek.settings Config row. The API key is returned masked.
    public class TalearrSoulseekConfigResource : RestResource
    {
        public bool Enabled { get; set; }
        public string Url { get; set; }
        public string ApiKey { get; set; }
        public string DownloadsPath { get; set; }
        public int SearchTimeoutSeconds { get; set; }
        public bool RequireFreeUploadSlot { get; set; }
        public int MaxPeerQueueLength { get; set; }
        public int MinUploadSpeedKbps { get; set; }
        public int MaxActiveGrabsPerPeer { get; set; }
        public int MaxActiveGrabs { get; set; }
        public int QueueTimeoutHours { get; set; }
        public bool CleanupAfterImport { get; set; }
    }

    public class TalearrSoulseekTestResource
    {
        public bool Success { get; set; }
        public string Message { get; set; }
        public List<string> Warnings { get; set; }
    }

    [V1ApiController("config/talearrsoulseek")]
    public class TalearrSoulseekConfigController : RestController<TalearrSoulseekConfigResource>
    {
        private readonly ITalearrSoulseekSettingsService _settingsService;
        private readonly ITalearrSoulseekService _soulseekService;

        public TalearrSoulseekConfigController(ITalearrSoulseekSettingsService settingsService, ITalearrSoulseekService soulseekService)
        {
            _settingsService = settingsService;
            _soulseekService = soulseekService;

            SharedValidator.RuleFor(c => c.Url).NotEmpty().When(c => c.Enabled);
            SharedValidator.RuleFor(c => c.DownloadsPath).IsValidPath().When(c => c.Enabled);
        }

        protected override TalearrSoulseekConfigResource GetResourceById(int id)
        {
            return GetConfig();
        }

        [HttpGet]
        public TalearrSoulseekConfigResource GetConfig()
        {
            return ToResource(_settingsService.Get());
        }

        [RestPutById]
        public ActionResult<TalearrSoulseekConfigResource> SaveConfig([FromBody] TalearrSoulseekConfigResource resource)
        {
            return Accepted(ToResource(_settingsService.Save(FromResource(resource))));
        }

        // Checks the (possibly unsaved) settings: slskd answers, the API key works, and Talearr
        // can see the downloads folder.
        [HttpPost("test")]
        public TalearrSoulseekTestResource Test([FromBody] TalearrSoulseekConfigResource resource)
        {
            var result = _soulseekService.Test(FromResource(resource));

            return new TalearrSoulseekTestResource
            {
                Success = result.Success,
                Message = result.Message,
                Warnings = result.Warnings
            };
        }

        private static TalearrSoulseekSettings FromResource(TalearrSoulseekConfigResource resource)
        {
            var defaults = TalearrSoulseekSettings.Default;
            if (resource == null)
            {
                return defaults;
            }

            return new TalearrSoulseekSettings
            {
                Enabled = resource.Enabled,
                Url = resource.Url ?? string.Empty,
                ApiKey = resource.ApiKey ?? string.Empty,
                DownloadsPath = resource.DownloadsPath ?? string.Empty,
                SearchTimeoutSeconds = resource.SearchTimeoutSeconds > 0 ? resource.SearchTimeoutSeconds : defaults.SearchTimeoutSeconds,
                RequireFreeUploadSlot = resource.RequireFreeUploadSlot,
                MaxPeerQueueLength = resource.MaxPeerQueueLength,
                MinUploadSpeedKbps = resource.MinUploadSpeedKbps,
                MaxActiveGrabsPerPeer = resource.MaxActiveGrabsPerPeer > 0 ? resource.MaxActiveGrabsPerPeer : defaults.MaxActiveGrabsPerPeer,
                MaxActiveGrabs = resource.MaxActiveGrabs > 0 ? resource.MaxActiveGrabs : defaults.MaxActiveGrabs,
                QueueTimeoutHours = resource.QueueTimeoutHours > 0 ? resource.QueueTimeoutHours : defaults.QueueTimeoutHours,
                CleanupAfterImport = resource.CleanupAfterImport
            };
        }

        private static TalearrSoulseekConfigResource ToResource(TalearrSoulseekSettings settings)
        {
            return new TalearrSoulseekConfigResource
            {
                Id = 1,
                Enabled = settings.Enabled,
                Url = settings.Url,
                ApiKey = string.IsNullOrEmpty(settings.ApiKey) ? string.Empty : TalearrSoulseekSettings.MaskedApiKey,
                DownloadsPath = settings.DownloadsPath,
                SearchTimeoutSeconds = settings.SearchTimeoutSeconds,
                RequireFreeUploadSlot = settings.RequireFreeUploadSlot,
                MaxPeerQueueLength = settings.MaxPeerQueueLength,
                MinUploadSpeedKbps = settings.MinUploadSpeedKbps,
                MaxActiveGrabsPerPeer = settings.MaxActiveGrabsPerPeer,
                MaxActiveGrabs = settings.MaxActiveGrabs,
                QueueTimeoutHours = settings.QueueTimeoutHours,
                CleanupAfterImport = settings.CleanupAfterImport
            };
        }
    }
}
