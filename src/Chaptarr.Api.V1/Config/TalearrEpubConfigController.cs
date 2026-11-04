using Chaptarr.Http;
using Chaptarr.Http.REST;
using Microsoft.AspNetCore.Mvc;
using NzbDrone.Core.Talearr.Ebook.Epub;
using NzbDrone.Http.REST.Attributes;

namespace Chaptarr.Api.V1.Config
{
    // Talearr: Kindle to EPUB conversion settings. A new route; no existing route or field changes.
    // Values are stored by TalearrEpubSettingsService as talearr.ebook.* rows in the Config table.
    public class TalearrEpubConfigResource : RestResource
    {
        public bool ConvertAfterImport { get; set; }
        public bool KeepOriginal { get; set; }
        public bool ConverterAvailable { get; set; }
    }

    [V1ApiController("config/talearrepub")]
    public class TalearrEpubConfigController : RestController<TalearrEpubConfigResource>
    {
        private readonly ITalearrEpubSettingsService _settingsService;
        private readonly ITalearrEpubConverter _converter;

        public TalearrEpubConfigController(ITalearrEpubSettingsService settingsService, ITalearrEpubConverter converter)
        {
            _settingsService = settingsService;
            _converter = converter;
        }

        protected override TalearrEpubConfigResource GetResourceById(int id)
        {
            return GetConfig();
        }

        [HttpGet]
        public TalearrEpubConfigResource GetConfig()
        {
            return ToResource(_settingsService.Get());
        }

        [RestPutById]
        public ActionResult<TalearrEpubConfigResource> SaveConfig([FromBody] TalearrEpubConfigResource resource)
        {
            var saved = _settingsService.Save(new TalearrEpubSettings
            {
                ConvertAfterImport = resource.ConvertAfterImport,
                KeepOriginal = resource.KeepOriginal
            });

            return Accepted(ToResource(saved));
        }

        private TalearrEpubConfigResource ToResource(TalearrEpubSettings settings)
        {
            return new TalearrEpubConfigResource
            {
                Id = 1,
                ConvertAfterImport = settings.ConvertAfterImport,
                KeepOriginal = settings.KeepOriginal,
                ConverterAvailable = _converter.IsAvailable()
            };
        }
    }
}
