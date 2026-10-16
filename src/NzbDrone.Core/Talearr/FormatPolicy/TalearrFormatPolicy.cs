using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using NLog;
using NzbDrone.Core.Configuration;

namespace NzbDrone.Core.Talearr.FormatPolicy
{
    // Which format TaleArr grabs for a wanted book (docs/TALEARR_FORMAT_POLICY.md). Monitoring
    // still decides which books are wanted, per format, exactly as upstream; the policy only
    // filters the grabs.
    public enum FormatPolicyMode
    {
        // Every monitored format is grabbed (upstream behaviour, the default).
        Both,

        // Audiobooks only: ebook releases are skipped, even for a monitored ebook.
        AudioOnly,

        // Ebooks only: audiobook releases are skipped.
        EbookOnly,

        // Prefer the audiobook, accept the ebook when the audiobook has been missing for the
        // fallback time, and still grab the audiobook when it appears later (the ebook is kept).
        PreferAudio
    }

    public sealed class TalearrFormatPolicySettings
    {
        public const int DefaultFallbackHours = 24;
        public const int MaxFallbackHours = 24 * 90;

        public FormatPolicyMode Default { get; set; } = FormatPolicyMode.Both;
        public int FallbackHours { get; set; } = DefaultFallbackHours;

        // Author id -> policy, overriding Default.
        public Dictionary<int, FormatPolicyMode> Authors { get; set; } = new Dictionary<int, FormatPolicyMode>();

        public FormatPolicyMode For(int authorId)
        {
            return Authors != null && Authors.TryGetValue(authorId, out var mode) ? mode : Default;
        }

        public TalearrFormatPolicySettings Clone()
        {
            return new TalearrFormatPolicySettings
            {
                Default = Default,
                FallbackHours = FallbackHours,
                Authors = new Dictionary<int, FormatPolicyMode>(Authors ?? new Dictionary<int, FormatPolicyMode>())
            };
        }
    }

    public interface ITalearrFormatPolicyService
    {
        TalearrFormatPolicySettings Get();
        TalearrFormatPolicySettings Save(TalearrFormatPolicySettings settings);
    }

    // TaleArr: format policy settings as talearr.formatpolicy.* rows in the Config table. Stock
    // Chaptarr ignores them.
    public class TalearrFormatPolicyService : ITalearrFormatPolicyService
    {
        public const string DefaultKey = "talearr.formatpolicy.default";
        public const string FallbackHoursKey = "talearr.formatpolicy.fallbackhours";
        public const string AuthorsKey = "talearr.formatpolicy.authors";

        private readonly IConfigRepository _repository;
        private readonly Logger _logger;
        private readonly object _lock = new object();
        private TalearrFormatPolicySettings _cached;

        public TalearrFormatPolicyService(IConfigRepository repository, Logger logger)
        {
            _repository = repository;
            _logger = logger;
        }

        public static TalearrFormatPolicySettings Resolve(ITalearrFormatPolicyService service)
        {
            try
            {
                return service?.Get() ?? new TalearrFormatPolicySettings();
            }
            catch (Exception)
            {
                return new TalearrFormatPolicySettings();
            }
        }

        public TalearrFormatPolicySettings Get()
        {
            lock (_lock)
            {
                _cached ??= Load();
                return _cached.Clone();
            }
        }

        public TalearrFormatPolicySettings Save(TalearrFormatPolicySettings settings)
        {
            settings ??= new TalearrFormatPolicySettings();

            lock (_lock)
            {
                var authors = (settings.Authors ?? new Dictionary<int, FormatPolicyMode>())
                    .Where(kv => kv.Key > 0 && Enum.IsDefined(typeof(FormatPolicyMode), kv.Value))
                    .ToDictionary(kv => kv.Key.ToString(), kv => kv.Value.ToString());

                _repository.Upsert(DefaultKey, (Enum.IsDefined(typeof(FormatPolicyMode), settings.Default) ? settings.Default : FormatPolicyMode.Both).ToString());
                _repository.Upsert(FallbackHoursKey, Math.Clamp(settings.FallbackHours, 0, TalearrFormatPolicySettings.MaxFallbackHours).ToString());
                _repository.Upsert(AuthorsKey, JsonSerializer.Serialize(authors));

                _cached = Load();
                _logger.Info("TaleArr format policy saved: default={0}, fallbackHours={1}, author overrides={2}", _cached.Default, _cached.FallbackHours, _cached.Authors.Count);
                return _cached.Clone();
            }
        }

        private TalearrFormatPolicySettings Load()
        {
            var settings = new TalearrFormatPolicySettings();

            if (Enum.TryParse<FormatPolicyMode>(Read(DefaultKey), true, out var mode) && Enum.IsDefined(typeof(FormatPolicyMode), mode))
            {
                settings.Default = mode;
            }

            if (int.TryParse(Read(FallbackHoursKey), out var hours) && hours >= 0 && hours <= TalearrFormatPolicySettings.MaxFallbackHours)
            {
                settings.FallbackHours = hours;
            }

            var authors = Read(AuthorsKey);
            if (authors != null)
            {
                try
                {
                    foreach (var (key, value) in JsonSerializer.Deserialize<Dictionary<string, string>>(authors) ?? new Dictionary<string, string>())
                    {
                        if (int.TryParse(key, out var authorId) && authorId > 0 &&
                            Enum.TryParse<FormatPolicyMode>(value, true, out var authorMode) && Enum.IsDefined(typeof(FormatPolicyMode), authorMode))
                        {
                            settings.Authors[authorId] = authorMode;
                        }
                    }
                }
                catch (JsonException ex)
                {
                    _logger.Warn(ex, "TaleArr: ignoring unreadable author format policies");
                }
            }

            return settings;
        }

        private string Read(string key)
        {
            try
            {
                var value = _repository.Get(key)?.Value;
                return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
            }
            catch (Exception ex)
            {
                _logger.Debug(ex, "Unable to read TaleArr setting {0}, using the default", key);
                return null;
            }
        }
    }
}
