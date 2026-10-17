using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using NLog;
using NzbDrone.Core.Books;
using NzbDrone.Core.Configuration;

namespace NzbDrone.Core.Talearr.Soulseek
{
    public enum TalearrSoulseekGrabState
    {
        Queued,
        Downloading,
        Imported,
        ImportFailed,
        Failed,
        Cancelled
    }

    public class TalearrSoulseekGrab
    {
        public string Id { get; set; }
        public int BookId { get; set; }
        public int AuthorId { get; set; }
        public BookMediaType MediaType { get; set; }
        public string Title { get; set; }
        public string Username { get; set; }
        public string Directory { get; set; }
        public List<SlskdQueueFile> Files { get; set; } = new List<SlskdQueueFile>();
        public long Size { get; set; }

        // Relative folder under slskd's downloads folder when slskd accepted TaleArr's batch
        // destination; empty when slskd chose the folder itself (older slskd).
        public string Destination { get; set; }
        public string LocalFolder { get; set; }
        public TalearrSoulseekGrabState State { get; set; }
        public string Message { get; set; }
        public long BytesTransferred { get; set; }
        public DateTime CreatedAt { get; set; }
        public DateTime UpdatedAt { get; set; }

        public bool IsActive => State == TalearrSoulseekGrabState.Queued || State == TalearrSoulseekGrabState.Downloading;
    }

    public interface ITalearrSoulseekGrabStore
    {
        List<TalearrSoulseekGrab> All();
        TalearrSoulseekGrab Get(string id);
        void Save(TalearrSoulseekGrab grab);
        void Remove(string id);
    }

    // TaleArr: Soulseek grabs live in the existing Config key/value table, one row per grab
    // ("talearr.soulseek.grab.<id>", JSON), so nothing is written to History, Blocklist,
    // PendingReleases or the download client tables. Stock Chaptarr never reads these keys.
    public class TalearrSoulseekGrabStore : ITalearrSoulseekGrabStore
    {
        public const string KeyPrefix = "talearr.soulseek.grab.";

        private static readonly JsonSerializerOptions JsonOptions = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            PropertyNameCaseInsensitive = true,
            Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() }
        };

        private readonly IConfigRepository _repository;
        private readonly Logger _logger;
        private readonly object _lock = new object();

        public TalearrSoulseekGrabStore(IConfigRepository repository, Logger logger)
        {
            _repository = repository;
            _logger = logger;
        }

        public List<TalearrSoulseekGrab> All()
        {
            lock (_lock)
            {
                try
                {
                    return (_repository.All() ?? Enumerable.Empty<Config>())
                        .Where(c => c?.Key != null && c.Key.StartsWith(KeyPrefix, StringComparison.OrdinalIgnoreCase))
                        .Select(c => Parse(c.Key.Substring(KeyPrefix.Length), c.Value))
                        .Where(g => g != null)
                        .OrderByDescending(g => g.CreatedAt)
                        .ToList();
                }
                catch (Exception ex)
                {
                    _logger.Warn(ex, "TaleArr: unable to read the Soulseek grabs");
                    return new List<TalearrSoulseekGrab>();
                }
            }
        }

        public TalearrSoulseekGrab Get(string id)
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

        public void Save(TalearrSoulseekGrab grab)
        {
            if (grab == null || string.IsNullOrWhiteSpace(grab.Id))
            {
                return;
            }

            lock (_lock)
            {
                grab.UpdatedAt = DateTime.UtcNow;
                _repository.Upsert(KeyPrefix + grab.Id, JsonSerializer.Serialize(grab, JsonOptions));
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

        private TalearrSoulseekGrab Parse(string id, string json)
        {
            if (string.IsNullOrWhiteSpace(json))
            {
                return null;
            }

            try
            {
                var grab = JsonSerializer.Deserialize<TalearrSoulseekGrab>(json, JsonOptions);
                if (grab == null)
                {
                    return null;
                }

                grab.Id = id;
                grab.Files ??= new List<SlskdQueueFile>();
                return grab;
            }
            catch (JsonException ex)
            {
                _logger.Warn(ex, "TaleArr: ignoring an unreadable Soulseek grab {0}", id);
                return null;
            }
        }
    }
}
