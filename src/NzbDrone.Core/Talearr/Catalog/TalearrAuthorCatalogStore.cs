using System;
using System.Collections.Generic;
using System.Linq;
using NLog;
using NzbDrone.Core.Books.Events;
using NzbDrone.Core.Configuration;
using NzbDrone.Core.Messaging.Events;

namespace NzbDrone.Core.Talearr.Catalog
{
    // Talearr: which part of an author's catalog is kept (docs/TALEARR_TRACKED_AUTHORS.md).
    public enum TalearrAuthorCatalogMode
    {
        // Every book the metadata profile allows (Readarr and Chaptarr behavior).
        Full = 0,

        // Only the books the user added, requested, monitored or has files for.
        Tracked = 1
    }

    // The API spelling of the modes: "full" and "tracked".
    public static class TalearrAuthorCatalogModes
    {
        public const string Full = "full";
        public const string Tracked = "tracked";

        public static string ToApi(TalearrAuthorCatalogMode mode) => mode == TalearrAuthorCatalogMode.Tracked ? Tracked : Full;

        public static bool TryParse(string value, out TalearrAuthorCatalogMode mode)
        {
            var normalized = (value ?? string.Empty).Trim();
            if (string.Equals(normalized, Full, StringComparison.OrdinalIgnoreCase))
            {
                mode = TalearrAuthorCatalogMode.Full;
                return true;
            }

            if (string.Equals(normalized, Tracked, StringComparison.OrdinalIgnoreCase))
            {
                mode = TalearrAuthorCatalogMode.Tracked;
                return true;
            }

            mode = TalearrAuthorCatalogMode.Full;
            return false;
        }
    }

    public interface ITalearrAuthorCatalogStore
    {
        // Full unless the author was stored as Tracked.
        TalearrAuthorCatalogMode Get(int authorId);
        void Set(int authorId, TalearrAuthorCatalogMode mode);
        IReadOnlyCollection<int> GetTrackedAuthorIds();

        // A single-book request that had to wait in the pending import queue: remembers that the
        // author it creates later is Tracked. A Full request for the same author clears it.
        void MarkPendingRequest(string authorProviderId, bool tracked);

        // True (and forgets it) when a pending request marked the author as Tracked.
        bool TakePendingRequest(string authorProviderId);
    }

    // Talearr: one Config row per Tracked author ("talearr.authorcatalog.<id>" = "tracked"); Full
    // authors have no row. Stock Chaptarr never reads these keys, so a rollback simply treats every
    // author as Full again and the next refresh brings their catalog back.
    public class TalearrAuthorCatalogStore : ITalearrAuthorCatalogStore, IHandle<AuthorDeletedEvent>
    {
        public const string KeyPrefix = "talearr.authorcatalog.";
        public const string PendingKeyPrefix = "talearr.authorcatalog.pending.";
        public const string TrackedValue = "tracked";

        private readonly IConfigRepository _repository;
        private readonly Logger _logger;
        private readonly object _lock = new object();
        private HashSet<int> _trackedIds;

        public TalearrAuthorCatalogStore(IConfigRepository repository, Logger logger)
        {
            _repository = repository;
            _logger = logger;
        }

        public static string ConfigKey(int authorId) => KeyPrefix + authorId;

        public static string PendingKey(string authorProviderId) => PendingKeyPrefix + (authorProviderId ?? string.Empty).Trim().ToLowerInvariant();

        public TalearrAuthorCatalogMode Get(int authorId)
        {
            if (authorId <= 0)
            {
                return TalearrAuthorCatalogMode.Full;
            }

            return LoadTrackedIds().Contains(authorId) ? TalearrAuthorCatalogMode.Tracked : TalearrAuthorCatalogMode.Full;
        }

        public IReadOnlyCollection<int> GetTrackedAuthorIds()
        {
            return LoadTrackedIds().ToList();
        }

        public void Set(int authorId, TalearrAuthorCatalogMode mode)
        {
            if (authorId <= 0)
            {
                return;
            }

            lock (_lock)
            {
                if (mode == TalearrAuthorCatalogMode.Tracked)
                {
                    _repository.Upsert(ConfigKey(authorId), TrackedValue);
                }
                else
                {
                    Delete(ConfigKey(authorId));
                }

                _trackedIds = null;
            }
        }

        public void MarkPendingRequest(string authorProviderId, bool tracked)
        {
            if (string.IsNullOrWhiteSpace(authorProviderId))
            {
                return;
            }

            try
            {
                if (tracked)
                {
                    _repository.Upsert(PendingKey(authorProviderId), TrackedValue);
                }
                else
                {
                    Delete(PendingKey(authorProviderId));
                }
            }
            catch (Exception ex)
            {
                _logger.Warn(ex, "Talearr: unable to remember the catalog mode of the pending request for author {0}", authorProviderId);
            }
        }

        public bool TakePendingRequest(string authorProviderId)
        {
            if (string.IsNullOrWhiteSpace(authorProviderId))
            {
                return false;
            }

            try
            {
                var row = _repository.Get(PendingKey(authorProviderId));
                if (row == null)
                {
                    return false;
                }

                _repository.Delete(row);
                return string.Equals(row.Value, TrackedValue, StringComparison.OrdinalIgnoreCase);
            }
            catch (Exception ex)
            {
                _logger.Warn(ex, "Talearr: unable to read the catalog mode of the pending request for author {0}", authorProviderId);
                return false;
            }
        }

        public void Handle(AuthorDeletedEvent message)
        {
            var authorId = message?.Author?.Id ?? 0;
            if (authorId > 0 && Get(authorId) == TalearrAuthorCatalogMode.Tracked)
            {
                Set(authorId, TalearrAuthorCatalogMode.Full);
            }
        }

        private HashSet<int> LoadTrackedIds()
        {
            lock (_lock)
            {
                if (_trackedIds != null)
                {
                    return _trackedIds;
                }

                var ids = new HashSet<int>();
                try
                {
                    foreach (var row in _repository.All() ?? Enumerable.Empty<Config>())
                    {
                        if (row?.Key == null ||
                            !row.Key.StartsWith(KeyPrefix, StringComparison.OrdinalIgnoreCase) ||
                            row.Key.StartsWith(PendingKeyPrefix, StringComparison.OrdinalIgnoreCase) ||
                            !string.Equals(row.Value, TrackedValue, StringComparison.OrdinalIgnoreCase))
                        {
                            continue;
                        }

                        if (int.TryParse(row.Key.Substring(KeyPrefix.Length), out var id) && id > 0)
                        {
                            ids.Add(id);
                        }
                    }
                }
                catch (Exception ex)
                {
                    // Full is the safe reading: nothing is hidden or removed because of a read error.
                    _logger.Warn(ex, "Talearr: unable to read author catalog modes; treating every author as Full");
                    return ids;
                }

                _trackedIds = ids;
                return _trackedIds;
            }
        }

        private void Delete(string key)
        {
            var row = _repository.Get(key);
            if (row != null)
            {
                _repository.Delete(row);
            }
        }
    }
}
