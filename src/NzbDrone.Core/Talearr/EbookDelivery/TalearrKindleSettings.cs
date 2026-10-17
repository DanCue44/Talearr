using System;
using System.Collections.Generic;
using System.Linq;
using NLog;
using NzbDrone.Common.Messaging;
using NzbDrone.Core.Configuration;
using NzbDrone.Core.Messaging.Events;

namespace NzbDrone.Core.Talearr.EbookDelivery
{
    // TaleArr: Send to Kindle (README.md, "Ebook delivery"). Emails an ebook to one or more
    // Send-to-Kindle addresses, on request from the book page or after each import.
    public sealed class TalearrKindleSettings
    {
        // What the API returns instead of a stored password. Saving it back keeps the stored one.
        public const string MaskedPassword = "********";

        // Off by default: nothing is emailed until this is on and an address is set.
        public bool Enabled { get; set; }

        // Also send every ebook imported from a download (not disk scans).
        public bool AutoSendOnImport { get; set; }

        // Kindle addresses, for example name_123@kindle.com.
        public List<string> KindleAddresses { get; set; } = new List<string>();

        // Id of an existing Email connection whose SMTP server, login and From address are reused.
        // 0 means the SMTP fields below are used instead.
        public int EmailConnectionId { get; set; }

        public string Server { get; set; } = string.Empty;
        public int Port { get; set; } = 587;
        public bool RequireEncryption { get; set; } = true;
        public string Username { get; set; } = string.Empty;
        public string Password { get; set; } = string.Empty;
        public string From { get; set; } = string.Empty;

        public static TalearrKindleSettings Default => new TalearrKindleSettings();

        public bool IsActive => Enabled && CleanAddresses(KindleAddresses).Count > 0;

        public static List<string> CleanAddresses(IEnumerable<string> addresses)
        {
            return (addresses ?? Enumerable.Empty<string>())
                .SelectMany(a => (a ?? string.Empty).Split(new[] { ',', ';', ' ', '\n', '\r', '\t' }, StringSplitOptions.RemoveEmptyEntries))
                .Select(a => a.Trim())
                .Where(a => a.Length > 0)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        public TalearrKindleSettings Clone()
        {
            return new TalearrKindleSettings
            {
                Enabled = Enabled,
                AutoSendOnImport = AutoSendOnImport,
                KindleAddresses = CleanAddresses(KindleAddresses),
                EmailConnectionId = EmailConnectionId,
                Server = Server ?? string.Empty,
                Port = Port,
                RequireEncryption = RequireEncryption,
                Username = Username ?? string.Empty,
                Password = Password ?? string.Empty,
                From = From ?? string.Empty
            };
        }
    }

    public class TalearrKindleSettingsChangedEvent : IEvent
    {
    }

    public interface ITalearrKindleSettingsService
    {
        TalearrKindleSettings Get();

        // A Password equal to TalearrKindleSettings.MaskedPassword keeps the stored password.
        TalearrKindleSettings Save(TalearrKindleSettings settings);

        // Replaces a masked password with the stored one (for Test with unsaved settings).
        TalearrKindleSettings WithStoredPassword(TalearrKindleSettings settings);
    }

    // Stored as talearr.kindle.* rows in the existing Config table, like the Email connection keeps
    // its password in the Notifications table. No schema change; Chaptarr ignores these rows.
    public class TalearrKindleSettingsService : ITalearrKindleSettingsService
    {
        public const string EnabledKey = "talearr.kindle.enabled";
        public const string AutoSendKey = "talearr.kindle.autosend";
        public const string AddressesKey = "talearr.kindle.addresses";
        public const string EmailConnectionKey = "talearr.kindle.emailconnection";
        public const string ServerKey = "talearr.kindle.smtp.server";
        public const string PortKey = "talearr.kindle.smtp.port";
        public const string EncryptionKey = "talearr.kindle.smtp.encryption";
        public const string UsernameKey = "talearr.kindle.smtp.username";
        public const string PasswordKey = "talearr.kindle.smtp.password";
        public const string FromKey = "talearr.kindle.smtp.from";

        private readonly IConfigRepository _repository;
        private readonly IEventAggregator _eventAggregator;
        private readonly Logger _logger;
        private readonly object _lock = new object();
        private TalearrKindleSettings _cached;

        public TalearrKindleSettingsService(IConfigRepository repository, IEventAggregator eventAggregator, Logger logger)
        {
            _repository = repository;
            _eventAggregator = eventAggregator;
            _logger = logger;
        }

        public TalearrKindleSettings Get()
        {
            lock (_lock)
            {
                _cached ??= Load();
                return _cached.Clone();
            }
        }

        public TalearrKindleSettings WithStoredPassword(TalearrKindleSettings settings)
        {
            var copy = (settings ?? TalearrKindleSettings.Default).Clone();

            if (copy.Password == TalearrKindleSettings.MaskedPassword)
            {
                copy.Password = Get().Password;
            }

            return copy;
        }

        public TalearrKindleSettings Save(TalearrKindleSettings settings)
        {
            settings = WithStoredPassword(settings);
            TalearrKindleSettings saved;

            lock (_lock)
            {
                _repository.Upsert(EnabledKey, settings.Enabled.ToString());
                _repository.Upsert(AutoSendKey, settings.AutoSendOnImport.ToString());
                _repository.Upsert(AddressesKey, string.Join(",", settings.KindleAddresses));
                _repository.Upsert(EmailConnectionKey, Math.Max(0, settings.EmailConnectionId).ToString());
                _repository.Upsert(ServerKey, settings.Server.Trim());
                _repository.Upsert(PortKey, settings.Port.ToString());
                _repository.Upsert(EncryptionKey, settings.RequireEncryption.ToString());
                _repository.Upsert(UsernameKey, settings.Username.Trim());
                _repository.Upsert(PasswordKey, settings.Password);
                _repository.Upsert(FromKey, settings.From.Trim());

                // Never log the password, and keep the addresses out of the log too.
                _logger.Info("TaleArr Send to Kindle settings saved: enabled={0}, autoSend={1}, addresses={2}, emailConnection={3}, server='{4}', port={5}",
                    settings.Enabled,
                    settings.AutoSendOnImport,
                    settings.KindleAddresses.Count,
                    settings.EmailConnectionId,
                    settings.Server,
                    settings.Port);

                _cached = Load();
                saved = _cached.Clone();
            }

            _eventAggregator.PublishEvent(new TalearrKindleSettingsChangedEvent());

            return saved;
        }

        private TalearrKindleSettings Load()
        {
            var settings = TalearrKindleSettings.Default;

            try
            {
                settings.Enabled = ReadBool(EnabledKey, settings.Enabled);
                settings.AutoSendOnImport = ReadBool(AutoSendKey, settings.AutoSendOnImport);
                settings.KindleAddresses = TalearrKindleSettings.CleanAddresses(new[] { Read(AddressesKey) });
                settings.EmailConnectionId = ReadInt(EmailConnectionKey, 0);
                settings.Server = Read(ServerKey).Trim();
                settings.Port = ReadInt(PortKey, settings.Port);
                settings.RequireEncryption = ReadBool(EncryptionKey, settings.RequireEncryption);
                settings.Username = Read(UsernameKey).Trim();
                settings.Password = Read(PasswordKey);
                settings.From = Read(FromKey).Trim();
            }
            catch (Exception ex)
            {
                _logger.Debug(ex, "Unable to read TaleArr Send to Kindle settings, using the defaults");
                settings = TalearrKindleSettings.Default;
            }

            return settings;
        }

        private string Read(string key)
        {
            return _repository.Get(key)?.Value ?? string.Empty;
        }

        private bool ReadBool(string key, bool fallback)
        {
            return bool.TryParse(Read(key).Trim(), out var parsed) ? parsed : fallback;
        }

        private int ReadInt(string key, int fallback)
        {
            return int.TryParse(Read(key).Trim(), out var parsed) ? parsed : fallback;
        }
    }
}
