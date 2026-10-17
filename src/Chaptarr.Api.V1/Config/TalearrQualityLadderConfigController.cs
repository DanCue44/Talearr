using System.Collections.Generic;
using System.Linq;
using Chaptarr.Http;
using Chaptarr.Http.REST;
using Microsoft.AspNetCore.Mvc;
using NzbDrone.Core.Talearr.QualityLadder;
using NzbDrone.Http.REST.Attributes;

namespace Chaptarr.Api.V1.Config
{
    // TaleArr: audio quality ladder and ebook format preference. A new route; no existing route
    // or field changes. Stored as talearr.ladder.* rows in the Config table. Fields a client does
    // not send keep their current values.
    public class TalearrQualityLadderConfigResource : RestResource
    {
        public bool? AudioRanking { get; set; }
        public bool? AudioUpgrades { get; set; }
        public int? MinimumAudioKbps { get; set; }
        public List<string> EbookFormats { get; set; }
        public bool? EbookUpgrades { get; set; }

        // Read only: every format the ebook order accepts.
        public List<string> KnownEbookFormats { get; set; }
    }

    [V1ApiController("config/talearrqualityladder")]
    public class TalearrQualityLadderConfigController : RestController<TalearrQualityLadderConfigResource>
    {
        private readonly ITalearrQualityLadderSettingsService _settingsService;

        public TalearrQualityLadderConfigController(ITalearrQualityLadderSettingsService settingsService)
        {
            _settingsService = settingsService;
        }

        protected override TalearrQualityLadderConfigResource GetResourceById(int id)
        {
            return GetConfig();
        }

        [HttpGet]
        public TalearrQualityLadderConfigResource GetConfig()
        {
            return ToResource(_settingsService.Get());
        }

        [RestPutById]
        public ActionResult<TalearrQualityLadderConfigResource> SaveConfig([FromBody] TalearrQualityLadderConfigResource resource)
        {
            var current = _settingsService.Get();

            var saved = _settingsService.Save(new TalearrQualityLadderSettings
            {
                AudioRanking = resource.AudioRanking ?? current.AudioRanking,
                AudioUpgrades = resource.AudioUpgrades ?? current.AudioUpgrades,
                MinimumAudioKbps = resource.MinimumAudioKbps ?? current.MinimumAudioKbps,
                EbookFormats = resource.EbookFormats ?? current.EbookFormats,
                EbookUpgrades = resource.EbookUpgrades ?? current.EbookUpgrades
            });

            return Accepted(ToResource(saved));
        }

        private static TalearrQualityLadderConfigResource ToResource(TalearrQualityLadderSettings settings)
        {
            return new TalearrQualityLadderConfigResource
            {
                Id = 1,
                AudioRanking = settings.AudioRanking,
                AudioUpgrades = settings.AudioUpgrades,
                MinimumAudioKbps = settings.MinimumAudioKbps,
                EbookFormats = settings.EbookFormats.ToList(),
                EbookUpgrades = settings.EbookUpgrades,
                KnownEbookFormats = TalearrEbookPreference.DefaultOrder.ToList()
            };
        }
    }
}
