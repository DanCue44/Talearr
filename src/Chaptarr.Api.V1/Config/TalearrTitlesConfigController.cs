using Chaptarr.Http;
using Chaptarr.Http.REST;
using Microsoft.AspNetCore.Mvc;
using NzbDrone.Core.Talearr.Titles;
using NzbDrone.Http.REST.Attributes;

namespace Chaptarr.Api.V1.Config
{
    // Talearr: Settings > Metadata > Book Titles. A new route; no existing route or field
    // changes. Stored by TalearrTitleSettingsService as a talearr.titles.* row in the Config table.
    public class TalearrTitlesConfigResource : RestResource
    {
        public bool CleanSeriesFromTitles { get; set; }
    }

    [V1ApiController("config/talearrtitles")]
    public class TalearrTitlesConfigController : RestController<TalearrTitlesConfigResource>
    {
        private readonly ITalearrTitleSettingsService _settingsService;

        public TalearrTitlesConfigController(ITalearrTitleSettingsService settingsService)
        {
            _settingsService = settingsService;
        }

        protected override TalearrTitlesConfigResource GetResourceById(int id)
        {
            return GetConfig();
        }

        [HttpGet]
        public TalearrTitlesConfigResource GetConfig()
        {
            return ToResource(_settingsService.Get());
        }

        [RestPutById]
        public ActionResult<TalearrTitlesConfigResource> SaveConfig([FromBody] TalearrTitlesConfigResource resource)
        {
            var saved = _settingsService.Save(new TalearrTitleSettings
            {
                CleanSeriesFromTitles = resource?.CleanSeriesFromTitles == true
            });

            return Accepted(ToResource(saved));
        }

        private static TalearrTitlesConfigResource ToResource(TalearrTitleSettings settings)
        {
            return new TalearrTitlesConfigResource
            {
                Id = 1,
                CleanSeriesFromTitles = settings.CleanSeriesFromTitles
            };
        }
    }
}
