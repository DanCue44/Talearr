using System.Collections.Generic;
using System.Linq;
using Chaptarr.Http;
using Chaptarr.Http.REST;
using FluentValidation;
using Microsoft.AspNetCore.Mvc;
using NzbDrone.Core.Talearr.Recordings;
using NzbDrone.Http.REST.Attributes;

namespace Chaptarr.Api.V1.Config
{
    // TaleArr: recordings settings (Audible marketplace and provider, background enrichment,
    // search on add, keep both recordings). A new route; no existing route or field changes.
    // Values are stored as talearr.recordings.* rows in the Config table, which Chaptarr ignores.
    public class TalearrRecordingsConfigResource : RestResource
    {
        public string Marketplace { get; set; }
        public TalearrAudibleProvider Provider { get; set; }
        public bool AudnexusFallback { get; set; }
        public TalearrEnrichScope Enrich { get; set; }
        public bool SearchOnAdd { get; set; }
        public bool KeepBoth { get; set; }

        // Read only: the marketplaces TaleArr knows, for a settings dropdown.
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

        public TalearrRecordingsConfigController(ITalearrRecordingsSettingsService settingsService)
        {
            _settingsService = settingsService;

            SharedValidator.RuleFor(c => c.Marketplace)
                .Must(TalearrAudibleMarketplaces.IsKnown)
                .WithMessage("Unknown Audible marketplace. Use one of: " + string.Join(", ", TalearrAudibleMarketplaces.All.Select(m => m.Code)));
            SharedValidator.RuleFor(c => c.Provider).IsInEnum();
            SharedValidator.RuleFor(c => c.Enrich).IsInEnum();
        }

        protected override TalearrRecordingsConfigResource GetResourceById(int id)
        {
            return GetConfig();
        }

        [HttpGet]
        public TalearrRecordingsConfigResource GetConfig()
        {
            return ToResource(_settingsService.Get());
        }

        [RestPutById]
        public ActionResult<TalearrRecordingsConfigResource> SaveConfig([FromBody] TalearrRecordingsConfigResource resource)
        {
            var saved = _settingsService.Save(new TalearrRecordingsSettings
            {
                Marketplace = resource.Marketplace,
                Provider = resource.Provider,
                AudnexusFallback = resource.AudnexusFallback,
                Enrich = resource.Enrich,
                SearchOnAdd = resource.SearchOnAdd,
                KeepBoth = resource.KeepBoth
            });

            return Accepted(ToResource(saved));
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
                AvailableMarketplaces = TalearrAudibleMarketplaces.All
                    .Select(m => new TalearrMarketplaceResource { Code = m.Code, Name = m.Name, Domain = m.Domain })
                    .ToList()
            };
        }
    }
}
