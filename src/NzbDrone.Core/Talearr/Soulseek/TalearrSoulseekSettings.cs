using System;
using System.Text.Json;
using System.Text.Json.Serialization;
using NLog;
using NzbDrone.Core.Configuration;

namespace NzbDrone.Core.Talearr.Soulseek
{
    // TaleArr: Soulseek through slskd (docs/TALEARR_SOULSEEK.md). Not a download client or
    // indexer provider: stock Chaptarr deletes provider rows whose type it does not know, and a
    // new DownloadProtocol value in History, Blocklist or PendingReleases could break a rollback.
    public sealed class TalearrSoulseekSettings
    {
        public const string MaskedApiKey = "********";

        // Off by default: TaleArr never talks to slskd until this is on.
        public bool Enabled { get; set; }

        // slskd's web address, for example http://slskd:5030.
        public string Url { get; set; } = string.Empty;

        // An slskd API key (slskd's web.authentication.api_keys).
        public string ApiKey { get; set; } = string.Empty;

        // slskd's downloads folder as TaleArr sees it (inside the TaleArr container).
        public string DownloadsPath { get; set; } = string.Empty;

        // How long slskd collects answers for one search.
        public int SearchTimeoutSeconds { get; set; } = 15;

        // Etiquette: only offer peers that have a free upload slot right now.
        public bool RequireFreeUploadSlot { get; set; } = true;

        // Etiquette: skip peers with more queued uploads than this (0 = no limit).
        public int MaxPeerQueueLength { get; set; } = 50;

        // Skip peers slower than this, in KiB/s (0 = no limit).
        public int MinUploadSpeedKbps { get; set; }

        // Etiquette: at most this many unfinished grabs from one peer at a time.
        public int MaxActiveGrabsPerPeer { get; set; } = 1;

        // At most this many unfinished Soulseek grabs in total.
        public int MaxActiveGrabs { get; set; } = 3;

        // A grab whose files are still queued by the peer after this many hours is given up.
        public int QueueTimeoutHours { get; set; } = 24;

        // Remove the finished transfers from slskd's list and the leftover folder after import.
        public bool CleanupAfterImport { get; set; } = true;

        public static TalearrSoulseekSettings Default => new TalearrSoulseekSettings();

        [JsonIgnore]
        public bool IsConfigured => !string.IsNullOrWhiteSpace(Url);

        [JsonIgnore]
        public bool IsActive => Enabled && IsConfigured;

        public TalearrSoulseekSettings Clone()
        {
            return (TalearrSoulseekSettings)MemberwiseClone();
        }

        public void Normalize()
        {
            Url = (Url ?? string.Empty).Trim().TrimEnd('/');
            ApiKey = (ApiKey ?? string.Empty).Trim();
            DownloadsPath = (DownloadsPath ?? string.Empty).Trim();
            SearchTimeoutSeconds = Math.Clamp(SearchTimeoutSeconds, 5, 120);
            MaxPeerQueueLength = Math.Max(0, MaxPeerQueueLength);
            MinUploadSpeedKbps = Math.Max(0, MinUploadSpeedKbps);
            MaxActiveGrabsPerPeer = Math.Clamp(MaxActiveGrabsPerPeer, 1, 10);
            MaxActiveGrabs = Math.Clamp(MaxActiveGrabs, 1, 50);
            QueueTimeoutHours = Math.Clamp(QueueTimeoutHours, 1, 24 * 14);
        }
    }

    public interface ITalearrSoulseekSettingsService
    {
        TalearrSoulseekSettings Get();
        TalearrSoulseekSettings Save(TalearrSoulseekSettings settings);
    }

    // Stored as one talearr.soulseek.settings row (JSON) in the existing Config table. No schema
    // change; Chaptarr ignores the row, so a rollback only stops TaleArr's Soulseek grabs.
    public class TalearrSoulseekSettingsService : ITalearrSoulseekSettingsService
    {
        public const string SettingsKey = "talearr.soulseek.settings";

        private readonly IConfigRepository _repository;
        private readonly Logger _logger;
        private readonly object _lock = new object();
        private TalearrSoulseekSettings _cached;

        public TalearrSoulseekSettingsService(IConfigRepository repository, Logger logger)
        {
            _repository = repository;
            _logger = logger;
        }

        public TalearrSoulseekSettings Get()
        {
            lock (_lock)
            {
                _cached ??= Load();
                return _cached.Clone();
            }
        }

        public TalearrSoulseekSettings Save(TalearrSoulseekSettings settings)
        {
            settings = (settings ?? TalearrSoulseekSettings.Default).Clone();

            lock (_lock)
            {
                var current = _cached ?? Load();

                // The API hands out a masked key; saving the mask keeps the stored key.
                if (settings.ApiKey == TalearrSoulseekSettings.MaskedApiKey)
                {
                    settings.ApiKey = current.ApiKey;
                }

                settings.Normalize();
                _repository.Upsert(SettingsKey, JsonSerializer.Serialize(settings, TalearrSlskdClient.JsonOptions));
                _logger.Info("TaleArr Soulseek settings saved: enabled={0}, url='{1}', downloads='{2}'", settings.Enabled, settings.Url, settings.DownloadsPath);

                _cached = Load();
                return _cached.Clone();
            }
        }

        private TalearrSoulseekSettings Load()
        {
            try
            {
                var json = _repository.Get(SettingsKey)?.Value;
                var settings = TalearrSlskdClient.Parse<TalearrSoulseekSettings>(json) ?? TalearrSoulseekSettings.Default;
                settings.Normalize();
                return settings;
            }
            catch (Exception ex)
            {
                _logger.Debug(ex, "Unable to read the TaleArr Soulseek settings, using the defaults");
                return TalearrSoulseekSettings.Default;
            }
        }
    }
}
