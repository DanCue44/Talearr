using Chaptarr.Http;
using Chaptarr.Http.REST;
using Microsoft.AspNetCore.Mvc;
using NzbDrone.Core.Talearr.Audiobook.M4b;
using NzbDrone.Http.REST.Attributes;

namespace Chaptarr.Api.V1.Config
{
    // Talearr: M4B merge settings. A new route; no existing route or field changes. Values are
    // stored by TalearrM4bSettingsService as talearr.audiobook.* rows in the Config table.
    public class TalearrM4bConfigResource : RestResource
    {
        public bool ConvertAfterImport { get; set; }
    }

    [V1ApiController("config/talearrm4b")]
    public class TalearrM4bConfigController : RestController<TalearrM4bConfigResource>
    {
        private readonly ITalearrM4bSettingsService _settingsService;

        public TalearrM4bConfigController(ITalearrM4bSettingsService settingsService)
        {
            _settingsService = settingsService;
        }

        protected override TalearrM4bConfigResource GetResourceById(int id)
        {
            return GetConfig();
        }

        [HttpGet]
        public TalearrM4bConfigResource GetConfig()
        {
            return ToResource(_settingsService.Get());
        }

        [RestPutById]
        public ActionResult<TalearrM4bConfigResource> SaveConfig([FromBody] TalearrM4bConfigResource resource)
        {
            var saved = _settingsService.Save(new TalearrM4bSettings
            {
                ConvertAfterImport = resource.ConvertAfterImport
            });

            return Accepted(ToResource(saved));
        }

        private static TalearrM4bConfigResource ToResource(TalearrM4bSettings settings)
        {
            return new TalearrM4bConfigResource
            {
                Id = 1,
                ConvertAfterImport = settings.ConvertAfterImport
            };
        }
    }
}
