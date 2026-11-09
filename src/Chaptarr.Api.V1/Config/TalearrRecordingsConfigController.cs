using System.Collections.Generic;
using System.Linq;
using Chaptarr.Http;
using Chaptarr.Http.REST;
using FluentValidation;
using Microsoft.AspNetCore.Mvc;
using NzbDrone.Core.Talearr.Metadata.Source;
using NzbDrone.Core.Talearr.Recordings;
using NzbDrone.Http.REST.Attributes;

namespace Chaptarr.Api.V1.Config
{
    // Talearr: recordings settings (Audible marketplace and provider, background enrichment,
    // search on add, keep both recordings, upgrade guard). A new route; no existing route or field changes.
    // Values are stored as talearr.recordings.* rows in the Config table, which Chaptarr ignores.
    public class TalearrRecordingsConfigResource : RestResource
    {
        public string Marketplace { get; set; }
        public TalearrAudibleProvider Provider { get; set; }
        public bool AudnexusFallback { get; set; }
        public TalearrEnrichScope Enrich { get; set; }
        public bool SearchOnAdd { get; set; }
        public bool KeepBoth { get; set; }

        // Upgrade guard (talearr.upgrade.* rows). Nullable so a client that does not know these
        // fields yet keeps the current values when it saves.
        public bool? UpgradeGuard { get; set; }
        public int? MinBitrateGainKbps { get; set; }
        public int? RuntimeTolerancePct { get; set; }

        // Audible fills what Hardcover lacks for every author (talearr.metadata.audiblefill).
        // Nullable like the guard fields.
        public bool? AudibleFill { get; set; }

        // Read only: the marketplaces Talearr knows, for a settings dropdown.
        public List<TalearrMarketplaceResource> AvailableMarketplaces { get; set; }
    }

    public class TalearrMarketplaceResource
    {
        public string Code { get; set; }
        public string Name { get; set; }
        public string Domain { get; set; }
    }

    [V1ApiController("config/talearrrecordings")]
    public class TalearrRecordingsConfigController : RestController<TalearrRecordingsConfigResource>
    {
        private readonly ITalearrRecordingsSettingsService _settingsService;
        private readonly ITalearrAudibleSourceSettings _audibleSource;

        public TalearrRecordingsConfigController(ITalearrRecordingsSettingsService settingsService, ITalearrAudibleSourceSettings audibleSource = null)
        {
            _settingsService = settingsService;
            _audibleSource = audibleSource;

            SharedValidator.RuleFor(c => c.Marketplace)
                .Must(TalearrAudibleMarketplaces.IsKnown)
                .WithMessage("Unknown Audible marketplace. Use one of: " + string.Join(", ", TalearrAudibleMarketplaces.All.Select(m => m.Code)));
            SharedValidator.RuleFor(c => c.Provider).IsInEnum();
            SharedValidator.RuleFor(c => c.Enrich).IsInEnum();
            SharedValidator.RuleFor(c => c.MinBitrateGainKbps).InclusiveBetween(0, TalearrRecordingsSettingsService.MaxBitrateGainKbps).When(c => c.MinBitrateGainKbps.HasValue);
            SharedValidator.RuleFor(c => c.RuntimeTolerancePct).InclusiveBetween(0, TalearrRecordingsSettingsService.MaxRuntimeTolerancePct).When(c => c.RuntimeTolerancePct.HasValue);
        }

        protected override TalearrRecordingsConfigResource GetResourceById(int id)
        {
            return GetConfig();
        }

        [HttpGet]
        public TalearrRecordingsConfigResource GetConfig()
        {
            return WithAudibleFill(ToResource(_settingsService.Get()));
        }

        [RestPutById]
        public ActionResult<TalearrRecordingsConfigResource> SaveConfig([FromBody] TalearrRecordingsConfigResource resource)
        {
            var current = _settingsService.Get();
            var saved = _settingsService.Save(new TalearrRecordingsSettings
            {
                Marketplace = resource.Marketplace,
                Provider = resource.Provider,
                AudnexusFallback = resource.AudnexusFallback,
                Enrich = resource.Enrich,
                SearchOnAdd = resource.SearchOnAdd,
                KeepBoth = resource.KeepBoth,
                UpgradeGuard = resource.UpgradeGuard ?? current.UpgradeGuard,
                MinBitrateGainKbps = resource.MinBitrateGainKbps ?? current.MinBitrateGainKbps,
                RuntimeTolerancePct = resource.RuntimeTolerancePct ?? current.RuntimeTolerancePct
            });

            if (resource.AudibleFill.HasValue && _audibleSource != null && resource.AudibleFill.Value != _audibleSource.FillEnabled)
            {
                _audibleSource.SetFillEnabled(resource.AudibleFill.Value);
            }

            return Accepted(WithAudibleFill(ToResource(saved)));
        }

        private TalearrRecordingsConfigResource WithAudibleFill(TalearrRecordingsConfigResource resource)
        {
            resource.AudibleFill = _audibleSource?.FillEnabled;
            return resource;
        }

        internal static TalearrRecordingsConfigResource ToResource(TalearrRecordingsSettings settings)
        {
            return new TalearrRecordingsConfigResource
            {
                Id = 1,
                Marketplace = settings.Marketplace,
                Provider = settings.Provider,
                AudnexusFallback = settings.AudnexusFallback,
                Enrich = settings.Enrich,
                SearchOnAdd = settings.SearchOnAdd,
                KeepBoth = settings.KeepBoth,
                UpgradeGuard = settings.UpgradeGuard,
                MinBitrateGainKbps = settings.MinBitrateGainKbps,
                RuntimeTolerancePct = settings.RuntimeTolerancePct,
                AvailableMarketplaces = TalearrAudibleMarketplaces.All
                    .Select(m => new TalearrMarketplaceResource { Code = m.Code, Name = m.Name, Domain = m.Domain })
                    .ToList()
            };
        }
    }
}
