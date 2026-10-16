using System;
using System.Collections.Generic;
using System.Linq;
using NLog;
using NzbDrone.Core.Notifications;
using NzbDrone.Core.Notifications.AudioBookShelf;

namespace NzbDrone.Core.Talearr.Audiobookshelf
{
    public sealed class TalearrAbsConnection
    {
        public int Id { get; set; }
        public string Name { get; set; }
        public AudioBookShelfSettings Settings { get; set; }

        public bool SyncEnabled => Settings != null && Settings.TalearrSyncLibrary && IsUsable;
        public bool SkipOwned => SyncEnabled && Settings.TalearrSkipOwned;

        public bool IsUsable
        {
            get
            {
                try
                {
                    return Settings != null && Settings.Validate().IsValid;
                }
                catch (Exception)
                {
                    return false;
                }
            }
        }

        public string ItemUrl(string itemId)
        {
            var baseUrl = Settings?.GetTalearrWebBaseUrl();
            return baseUrl == null || string.IsNullOrWhiteSpace(itemId) ? null : $"{baseUrl}/item/{Uri.EscapeDataString(itemId)}";
        }
    }

    public interface ITalearrAbsConnections
    {
        // Every Audiobookshelf connection under Settings > Connect.
        List<TalearrAbsConnection> GetConnections();
    }

    // TaleArr: the Audiobookshelf connections are upstream's own AudioBookShelf notification
    // rows. TaleArr adds a few keys to their settings JSON and reads them here; it never creates,
    // changes or deletes the rows.
    public class TalearrAbsConnections : ITalearrAbsConnections
    {
        public const string Implementation = "AudioBookShelf";

        private readonly INotificationFactory _notificationFactory;
        private readonly Logger _logger;

        public TalearrAbsConnections(INotificationFactory notificationFactory, Logger logger)
        {
            _notificationFactory = notificationFactory;
            _logger = logger;
        }

        public List<TalearrAbsConnection> GetConnections()
        {
            try
            {
                return (_notificationFactory.All() ?? new List<NotificationDefinition>())
                    .Where(d => d != null &&
                                string.Equals(d.Implementation, Implementation, StringComparison.OrdinalIgnoreCase) &&
                                d.Settings is AudioBookShelfSettings)
                    .Select(d => new TalearrAbsConnection
                    {
                        Id = d.Id,
                        Name = string.IsNullOrWhiteSpace(d.Name) ? "Audiobookshelf" : d.Name,
                        Settings = (AudioBookShelfSettings)d.Settings
                    })
                    .OrderBy(c => c.Id)
                    .ToList();
            }
            catch (Exception ex)
            {
                _logger.Warn(ex, "TaleArr: could not read the Audiobookshelf connections");
                return new List<TalearrAbsConnection>();
            }
        }
    }
}
