using System;
using Chaptarr.Http;
using Chaptarr.Http.REST;
using FluentValidation;
using Microsoft.AspNetCore.Mvc;
using NzbDrone.Core.Talearr.MetadataFallback;
using NzbDrone.Http.REST.Attributes;

namespace Chaptarr.Api.V1.Config
{
    // Talearr: Open Library metadata fallback setting plus its live status. A new route; no
    // existing route or field changes. The mode is stored by TalearrMetadataFallbackSettingsService
    // as a talearr.metadatafallback.* row in the Config table, which Chaptarr ignores. The status
    // fields are read-only and in memory.
    public class TalearrMetadataFallbackConfigResource : RestResource
    {
        // off | whenPrimaryDown | whenPrimaryDownShowSource
        public string Mode { get; set; }

        public string ActiveSource { get; set; }
        public string PrimarySource { get; set; }
        public bool PrimaryUnavailable { get; set; }
        public DateTime? LastUsed { get; set; }
        public string LastOperation { get; set; }
        public string LastError { get; set; }
        public int UsesSinceStart { get; set; }
    }

    [V1ApiController("config/talearrmetadatafallback")]
    public class TalearrMetadataFallbackConfigController : RestController<TalearrMetadataFallbackConfigResource>
    {
        private readonly ITalearrMetadataFallbackSettingsService _settingsService;
        private readonly ITalearrMetadataFallback _fallback;

        public TalearrMetadataFallbackConfigController(ITalearrMetadataFallbackSettingsService settingsService, ITalearrMetadataFallback fallback)
        {
            _settingsService = settingsService;
            _fallback = fallback;

            SharedValidator.RuleFor(c => c.Mode)
                .Must(mode => TalearrMetadataFallbackSettingsService.TryParse(mode, out _))
                .WithMessage("mode must be off, whenPrimaryDown or whenPrimaryDownShowSource");
        }

        protected override TalearrMetadataFallbackConfigResource GetResourceById(int id)
        {
            return GetConfig();
        }

        [HttpGet]
        public TalearrMetadataFallbackConfigResource GetConfig()
        {
            return ToResource();
        }

        [RestPutById]
        public ActionResult<TalearrMetadataFallbackConfigResource> SaveConfig([FromBody] TalearrMetadataFallbackConfigResource resource)
        {
            TalearrMetadataFallbackSettingsService.TryParse(resource.Mode, out var mode);
            _settingsService.SaveMode(mode);
            return Accepted(ToResource());
        }

        private TalearrMetadataFallbackConfigResource ToResource()
        {
            var status = _fallback.GetStatus();
            return new TalearrMetadataFallbackConfigResource
            {
                Id = 1,
                Mode = TalearrMetadataFallbackSettingsService.ToApiValue(_settingsService.GetMode()),
                ActiveSource = status.ActiveSource,
                PrimarySource = status.PrimarySource,
                PrimaryUnavailable = status.PrimaryUnavailable,
                LastUsed = status.LastUsed,
                LastOperation = status.LastOperation,
                LastError = status.LastError,
                UsesSinceStart = status.UsesSinceStart
            };
        }
    }
}
