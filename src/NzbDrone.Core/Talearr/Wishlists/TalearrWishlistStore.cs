using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using NLog;
using NzbDrone.Core.Configuration;

namespace NzbDrone.Core.Talearr.Wishlists
{
    public interface ITalearrWishlistStore
    {
        List<TalearrWishlist> All();
        TalearrWishlist Get(string id);
        void Save(TalearrWishlist wishlist);
        void Remove(string id);
    }

    // TaleArr: wishlists live in the existing Config key/value table, one row per wishlist
    // ("talearr.wishlist.<id>", JSON). Stock Chaptarr never reads these keys.
    public class TalearrWishlistStore : ITalearrWishlistStore
    {
        public const string KeyPrefix = "talearr.wishlist.";

        private static readonly JsonSerializerOptions JsonOptions = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            PropertyNameCaseInsensitive = true
        };

        private readonly IConfigRepository _repository;
        private readonly Logger _logger;
        private readonly object _lock = new object();

        public TalearrWishlistStore(IConfigRepository repository, Logger logger)
        {
            _repository = repository;
            _logger = logger;
        }

        public List<TalearrWishlist> All()
        {
            lock (_lock)
            {
                try
                {
                    return (_repository.All() ?? Enumerable.Empty<Config>())
                        .Where(c => c?.Key != null && c.Key.StartsWith(KeyPrefix, StringComparison.OrdinalIgnoreCase))
                        .Select(c => Parse(c.Key.Substring(KeyPrefix.Length), c.Value))
                        .Where(w => w != null)
                        .OrderBy(w => w.Name, StringComparer.OrdinalIgnoreCase)
                        .ToList();
                }
                catch (Exception ex)
                {
                    _logger.Warn(ex, "TaleArr: unable to read the wishlists");
                    return new List<TalearrWishlist>();
                }
            }
        }

        public TalearrWishlist Get(string id)
        {
            if (string.IsNullOrWhiteSpace(id))
            {
                return null;
            }

            lock (_lock)
            {
                var row = _repository.Get(KeyPrefix + id);
                return row == null ? null : Parse(id, row.Value);
            }
        }

        public void Save(TalearrWishlist wishlist)
        {
            if (wishlist == null || string.IsNullOrWhiteSpace(wishlist.Id))
            {
                return;
            }

            lock (_lock)
            {
                _repository.Upsert(KeyPrefix + wishlist.Id, JsonSerializer.Serialize(wishlist, JsonOptions));
            }
        }

        public void Remove(string id)
        {
            if (string.IsNullOrWhiteSpace(id))
            {
                return;
            }

            lock (_lock)
            {
                var row = _repository.Get(KeyPrefix + id);
                if (row != null)
                {
                    _repository.Delete(row);
                }
            }
        }

        private TalearrWishlist Parse(string id, string json)
        {
            if (string.IsNullOrWhiteSpace(json))
            {
                return null;
            }

            try
            {
                var wishlist = JsonSerializer.Deserialize<TalearrWishlist>(json, JsonOptions);
                if (wishlist == null)
                {
                    return null;
                }

                wishlist.Id = id;
                wishlist.Handled = new Dictionary<string, string>(wishlist.Handled ?? new Dictionary<string, string>(), StringComparer.OrdinalIgnoreCase);
                return wishlist;
            }
            catch (JsonException ex)
            {
                _logger.Warn(ex, "TaleArr: ignoring an unreadable wishlist {0}", id);
                return null;
            }
        }
    }
}
