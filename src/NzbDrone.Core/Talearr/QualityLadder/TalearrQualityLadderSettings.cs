using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using NLog;
using NzbDrone.Core.Configuration;

namespace NzbDrone.Core.Talearr.QualityLadder
{
    // TaleArr: audio quality ladder and ebook format preference settings. The defaults only change
    // ranking; nothing is rejected or upgraded unless it is turned on.
    public sealed class TalearrQualityLadderSettings
    {
        public const int MaxMinimumAudioKbps = 128;

        // On by default: among releases the quality profile ranks equal, the better audio tier wins,
        // and very low or implausibly small audio releases rank below every other audio release.
        public bool AudioRanking { get; set; } = true;

        // Off by default: a clearly better copy of the same recording (higher tier, at the same
        // place in the quality profile) may replace files below the Good tier. Needs the quality
        // profile to allow upgrades and the recording-aware upgrade guard to be on.
        public bool AudioUpgrades { get; set; }

        // 0 (off) by default: audio releases whose estimated bitrate is known and below this are
        // rejected instead of only ranked last.
        public int MinimumAudioKbps { get; set; }

        // Most preferred first. Used to rank ebook releases the quality profile ranks equal.
        public List<string> EbookFormats { get; set; } = TalearrEbookPreference.DefaultOrder.ToList();

        // Off by default: an ebook may be replaced by a more preferred format the quality profile
        // ranks equal (needs the profile to allow upgrades).
        public bool EbookUpgrades { get; set; }

        public static TalearrQualityLadderSettings Default => new TalearrQualityLadderSettings();

        public TalearrQualityLadderSettings Clone()
        {
            return new TalearrQualityLadderSettings
            {
                AudioRanking = AudioRanking,
                AudioUpgrades = AudioUpgrades,
                MinimumAudioKbps = MinimumAudioKbps,
                EbookFormats = (EbookFormats ?? new List<string>()).ToList(),
                EbookUpgrades = EbookUpgrades
            };
        }
    }

    public interface ITalearrQualityLadderSettingsService
    {
        TalearrQualityLadderSettings Get();
        TalearrQualityLadderSettings Save(TalearrQualityLadderSettings settings);
    }

    // Stored as talearr.ladder.* rows in the existing Config key/value table. No schema change;
    // Chaptarr ignores rows it does not know.
    public class TalearrQualityLadderSettingsService : ITalearrQualityLadderSettingsService
    {
        public const string AudioRankingKey = "talearr.ladder.audioranking";
        public const string AudioUpgradesKey = "talearr.ladder.audioupgrades";
        public const string MinimumAudioKbpsKey = "talearr.ladder.minaudiokbps";
        public const string EbookFormatsKey = "talearr.ladder.ebookformats";
        public const string EbookUpgradesKey = "talearr.ladder.ebookupgrades";

        private readonly IConfigRepository _repository;
        private readonly Logger _logger;
        private readonly object _lock = new object();
        private TalearrQualityLadderSettings _cached;

        public TalearrQualityLadderSettingsService(IConfigRepository repository, Logger logger)
        {
            _repository = repository;
            _logger = logger;
        }

        public static TalearrQualityLadderSettings Resolve(ITalearrQualityLadderSettingsService service)
        {
            try
            {
                return service?.Get() ?? TalearrQualityLadderSettings.Default;
            }
            catch (Exception)
            {
                return TalearrQualityLadderSettings.Default;
            }
        }

        public TalearrQualityLadderSettings Get()
        {
            lock (_lock)
            {
                _cached ??= Load();
                return _cached.Clone();
            }
        }

        public TalearrQualityLadderSettings Save(TalearrQualityLadderSettings settings)
        {
            settings ??= TalearrQualityLadderSettings.Default;

            lock (_lock)
            {
                var minimum = Math.Clamp(settings.MinimumAudioKbps, 0, TalearrQualityLadderSettings.MaxMinimumAudioKbps);
                var formats = TalearrEbookPreference.Normalize(settings.EbookFormats);

                _repository.Upsert(AudioRankingKey, settings.AudioRanking.ToString());
                _repository.Upsert(AudioUpgradesKey, settings.AudioUpgrades.ToString());
                _repository.Upsert(MinimumAudioKbpsKey, minimum.ToString(CultureInfo.InvariantCulture));
                _repository.Upsert(EbookFormatsKey, string.Join(",", formats));
                _repository.Upsert(EbookUpgradesKey, settings.EbookUpgrades.ToString());

                _logger.Info("TaleArr quality ladder settings saved: audioRanking={0}, audioUpgrades={1}, minimumAudioKbps={2}, ebookFormats={3}, ebookUpgrades={4}",
                    settings.AudioRanking, settings.AudioUpgrades, minimum, string.Join(">", formats), settings.EbookUpgrades);

                _cached = Load();
                return _cached.Clone();
            }
        }

        private TalearrQualityLadderSettings Load()
        {
            var settings = TalearrQualityLadderSettings.Default;

            try
            {
                settings.AudioRanking = ReadBool(AudioRankingKey, settings.AudioRanking);
                settings.AudioUpgrades = ReadBool(AudioUpgradesKey, settings.AudioUpgrades);
                settings.EbookUpgrades = ReadBool(EbookUpgradesKey, settings.EbookUpgrades);

                if (int.TryParse(_repository.Get(MinimumAudioKbpsKey)?.Value?.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var minimum))
                {
                    settings.MinimumAudioKbps = Math.Clamp(minimum, 0, TalearrQualityLadderSettings.MaxMinimumAudioKbps);
                }

                var formats = _repository.Get(EbookFormatsKey)?.Value;
                if (!string.IsNullOrWhiteSpace(formats))
                {
                    settings.EbookFormats = TalearrEbookPreference.Parse(formats);
                }
            }
            catch (Exception ex)
            {
                _logger.Debug(ex, "Unable to read TaleArr quality ladder settings, using the defaults");
            }

            return settings;
        }

        private bool ReadBool(string key, bool fallback)
        {
            return bool.TryParse(_repository.Get(key)?.Value?.Trim(), out var value) ? value : fallback;
        }
    }
}
