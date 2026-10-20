using Chaptarr.Http;
using Microsoft.AspNetCore.Mvc;
using NzbDrone.Core.Talearr.Catalog;

namespace Chaptarr.Api.V1.Talearr
{
    public class TalearrAuthorCatalogResource
    {
        public int AuthorId { get; set; }

        // "full" (all books by the author) or "tracked" (only the user's books).
        public string Mode { get; set; }
    }

    // Talearr: which books of an author are kept (docs/TALEARR_TRACKED_AUTHORS.md). New routes only.
    //   GET /api/v1/talearr/authors/{id}/catalog
    //   PUT /api/v1/talearr/authors/{id}/catalog {"mode": "full" | "tracked"}
    // A change queues a refresh of the author: full brings the catalog in, tracked removes
    // unmonitored, fileless, non-manual catalog rows.
    [V1ApiController("talearr/authors")]
    public class TalearrAuthorCatalogController : Controller
    {
        private readonly ITalearrAuthorCatalogService _catalogService;

        public TalearrAuthorCatalogController(ITalearrAuthorCatalogService catalogService)
        {
            _catalogService = catalogService;
        }

        [HttpGet("{authorId:int}/catalog")]
        public TalearrAuthorCatalogResource GetCatalog(int authorId)
        {
            return ToResource(authorId);
        }

        [HttpPut("{authorId:int}/catalog")]
        public ActionResult<TalearrAuthorCatalogResource> SetCatalog(int authorId, [FromBody] TalearrAuthorCatalogResource request)
        {
            if (!TalearrAuthorCatalogModes.TryParse(request?.Mode, out var mode))
            {
                return BadRequest(new { message = "mode must be full or tracked" });
            }

            if (!_catalogService.SetMode(authorId, mode))
            {
                return NotFound();
            }

            return ToResource(authorId);
        }

        private TalearrAuthorCatalogResource ToResource(int authorId)
        {
            return new TalearrAuthorCatalogResource
            {
                AuthorId = authorId,
                Mode = TalearrAuthorCatalogModes.ToApi(_catalogService.GetMode(authorId))
            };
        }
    }
}
