using Chaptarr.Http;
using Chaptarr.Http.REST;
using Microsoft.AspNetCore.Mvc;
using NzbDrone.Core.Talearr.EbookDelivery;
using NzbDrone.Core.Talearr.ReaderHandoff;
using NzbDrone.Http.REST.Attributes;

namespace Chaptarr.Api.V1.Config
{
    // TaleArr: OPDS catalog settings. A new route; no existing route or field changes. Stored as
    // talearr.opds.* Config rows.
    public class TalearrOpdsConfigResource : RestResource
    {
        public bool Enabled { get; set; }
        public int PageSize { get; set; }
        public bool OfferKepub { get; set; }

        // Read only: the feed path including the URL base, for example /opds or /talearr/opds.
        public string FeedPath { get; set; }
        public bool KepubifyInstalled { get; set; }
    }

    [V1ApiController("config/talearropds")]
    public class TalearrOpdsConfigController : RestController<TalearrOpdsConfigResource>
    {
        private readonly ITalearrOpdsSettingsService _settingsService;
        private readonly ITalearrKepubify _kepubify;

        public TalearrOpdsConfigController(ITalearrOpdsSettingsService settingsService, ITalearrKepubify kepubify)
        {
            _settingsService = settingsService;
            _kepubify = kepubify;
        }

        protected override TalearrOpdsConfigResource GetResourceById(int id)
        {
            return GetConfig();
        }

        [HttpGet]
        public TalearrOpdsConfigResource GetConfig()
        {
            return ToResource(_settingsService.Get());
        }

        [RestPutById]
        public ActionResult<TalearrOpdsConfigResource> SaveConfig([FromBody] TalearrOpdsConfigResource resource)
        {
            var saved = _settingsService.Save(new TalearrOpdsSettings
            {
                Enabled = resource?.Enabled ?? false,
                PageSize = resource?.PageSize ?? TalearrOpdsSettings.DefaultPageSize,
                OfferKepub = resource?.OfferKepub ?? false
            });

            return Accepted(ToResource(saved));
        }

        private TalearrOpdsConfigResource ToResource(TalearrOpdsSettings settings)
        {
            return new TalearrOpdsConfigResource
            {
                Id = 1,
                Enabled = settings.Enabled,
                PageSize = settings.PageSize,
                OfferKepub = settings.OfferKepub,
                FeedPath = (Request?.PathBase.Value ?? string.Empty) + "/opds",
                KepubifyInstalled = _kepubify.FindExecutable() != null
            };
        }
    }
}
