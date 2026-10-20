using Chaptarr.Http;
using Microsoft.AspNetCore.Mvc;
using NzbDrone.Core.Talearr.Seeding;

namespace Chaptarr.Api.V1.Talearr
{
    // Talearr: what the torrent clients seed for Talearr, with private-tracker seeding progress,
    // hit-and-run risk and the stored MAM account status. Read-only, new route only.
    //   GET /api/v1/talearr/seeding
    [V1ApiController("talearr/seeding")]
    public class TalearrSeedingController : Controller
    {
        private readonly ITalearrSeedingService _seedingService;

        public TalearrSeedingController(ITalearrSeedingService seedingService)
        {
            _seedingService = seedingService;
        }

        [HttpGet]
        public TalearrSeedingReport GetSeeding()
        {
            return _seedingService.GetReport();
        }
    }
}
