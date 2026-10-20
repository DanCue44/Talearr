using System;
using System.Collections.Generic;
using System.Linq;
using Chaptarr.Http;
using Microsoft.AspNetCore.Mvc;
using NzbDrone.Core.Talearr.Wishlists;

namespace Chaptarr.Api.V1.Talearr
{
    public class TalearrWishlistResource
    {
        public string Id { get; set; }
        public string Name { get; set; }
        public string Kind { get; set; }
        public string Path { get; set; }
        public string Url { get; set; }
        public bool Uploaded { get; set; }
        public bool Audiobook { get; set; }
        public bool Ebook { get; set; }
        public bool SearchOnAdd { get; set; }
        public bool ToReadOnly { get; set; }
        public bool Enabled { get; set; }
        public string Format { get; set; }
        public DateTime AddedAt { get; set; }
        public DateTime? LastSyncAt { get; set; }
        public string LastResult { get; set; }
        public string LastError { get; set; }
        public int HandledCount { get; set; }
    }

    // Talearr: wishlists under /api/v1/talearr/wishlists (docs/TALEARR_WISHLISTS.md). New routes
    // only; upstream's import lists are unchanged.
    [V1ApiController("talearr/wishlists")]
    public class TalearrWishlistsController : Controller
    {
        private readonly ITalearrWishlistService _wishlistService;

        public TalearrWishlistsController(ITalearrWishlistService wishlistService)
        {
            _wishlistService = wishlistService;
        }

        [HttpGet]
        public List<TalearrWishlistResource> GetWishlists()
        {
            return _wishlistService.All().Select(ToResource).ToList();
        }

        [HttpGet("{id}")]
        public ActionResult<TalearrWishlistResource> GetWishlist(string id)
        {
            var wishlist = _wishlistService.Get(id);
            return wishlist == null ? NotFound() : ToResource(wishlist);
        }

        // {"name": "StoryGraph to-read", "kind": "file", "csv": "...", "audiobook": true, "ebook": false,
        //  "searchOnAdd": true, "toReadOnly": true} or {"kind": "hardcoverList", "url": "https://hardcover.app/@me/lists/x"}
        // or {"kind": "file", "path": "/config/wishlists/goodreads.csv"}.
        [HttpPost]
        public ActionResult<TalearrWishlistResource> CreateWishlist([FromBody] TalearrWishlistRequest request)
        {
            return Run(() => ToResource(_wishlistService.Create(request)));
        }

        [HttpPut("{id}")]
        public ActionResult<TalearrWishlistResource> UpdateWishlist(string id, [FromBody] TalearrWishlistRequest request)
        {
            return Run(() =>
            {
                var wishlist = _wishlistService.Update(id, request);
                return wishlist == null ? null : ToResource(wishlist);
            });
        }

        // Removes the wishlist. Books it added stay in the library as they are.
        [HttpDelete("{id}")]
        public IActionResult DeleteWishlist(string id)
        {
            if (_wishlistService.Get(id) == null)
            {
                return NotFound();
            }

            _wishlistService.Delete(id);
            return Ok();
        }

        // Dry run for a file or list that is not saved yet: which rows match which book and what a
        // sync would do. Nothing is added.
        [HttpPost("preview")]
        public ActionResult<TalearrWishlistPreview> PreviewUpload([FromBody] TalearrWishlistRequest request)
        {
            return Run(() => _wishlistService.PreviewUpload(request));
        }

        [HttpGet("{id}/preview")]
        public ActionResult<TalearrWishlistPreview> Preview(string id)
        {
            return Run(() => _wishlistService.Preview(id));
        }

        // Adds the new rows now, as the scheduled Sync Wishlists task does.
        [HttpPost("{id}/sync")]
        public ActionResult<TalearrWishlistSyncResult> Sync(string id)
        {
            return Run(() => _wishlistService.Sync(id));
        }

        private ActionResult<T> Run<T>(Func<T> action)
            where T : class
        {
            try
            {
                var result = action();
                return result == null ? NotFound() : result;
            }
            catch (Exception ex) when (ex is TalearrWishlistException || ex is FormatException)
            {
                return BadRequest(new { message = ex.Message });
            }
        }

        private static TalearrWishlistResource ToResource(TalearrWishlist wishlist)
        {
            return new TalearrWishlistResource
            {
                Id = wishlist.Id,
                Name = wishlist.Name,
                Kind = wishlist.Kind,
                Path = wishlist.Path,
                Url = wishlist.Url,
                Uploaded = wishlist.Path != null && wishlist.Path.Replace('\\', '/').Contains("/" + TalearrWishlistService.UploadFolderName + "/"),
                Audiobook = wishlist.Audiobook,
                Ebook = wishlist.Ebook,
                SearchOnAdd = wishlist.SearchOnAdd,
                ToReadOnly = wishlist.ToReadOnly,
                Enabled = wishlist.Enabled,
                Format = wishlist.Format,
                AddedAt = wishlist.AddedAt,
                LastSyncAt = wishlist.LastSyncAt,
                LastResult = wishlist.LastResult,
                LastError = wishlist.LastError,
                HandledCount = wishlist.Handled?.Count ?? 0
            };
        }
    }
}
