using System;
using System.Collections.Concurrent;
using NLog;
using NzbDrone.Common.Messaging;
using NzbDrone.Core.Messaging.Events;

namespace NzbDrone.Core.Indexers.MyAnonaMouse
{
    public enum MyAnonaMouseCookieField
    {
        MamId = 0,
        SeedboxMamId = 1
    }

    // TaleArr: raised when a MAM session or dynamic seedbox state changes, so the health
    // checks re-run right away instead of at the next 6-hourly health cycle.
    public class MyAnonaMouseSessionStatusChangedEvent : IEvent
    {
        public MyAnonaMouseSessionStatusChangedEvent(int indexerId)
        {
            IndexerId = indexerId;
        }

        public int IndexerId { get; }
    }

    public sealed class MyAnonaMouseSessionHealth
    {
        // The mam_id MAM last rejected, kept so the problem clears by itself once a new mam_id
        // is pasted (the stored value no longer matches) or the same one is accepted again.
        public string RejectedMamId { get; set; }
        public string RejectedSource { get; set; }
        public DateTime? RejectedUtc { get; set; }

        public MyAnonaMouseSeedboxResult Seedbox { get; set; }
        public string SeedboxMamId { get; set; }

        public MyAnonaMouseSessionHealth Clone()
        {
            return (MyAnonaMouseSessionHealth)MemberwiseClone();
        }
    }

    public interface IMyAnonaMouseSessionMonitor
    {
        void RecordSessionAccepted(int indexerId, string mamId);
        void RecordSessionRejected(int indexerId, string mamId, string source);
        void RecordRotatedCookie(int indexerId, MyAnonaMouseCookieField field, string previousValue, string rotatedValue);
        void RecordSeedboxResult(int indexerId, string mamId, MyAnonaMouseSeedboxResult result);
        MyAnonaMouseSessionHealth Get(int indexerId);
    }

    // TaleArr: in-memory MAM session state for the health checks, plus persistence of rotated
    // mam_id cookies back into the indexer settings JSON (no schema change; Chaptarr reads the
    // same MamId field). State is rebuilt after a restart by the startup account refresh and
    // the startup seedbox update.
    public class MyAnonaMouseSessionMonitor : IMyAnonaMouseSessionMonitor
    {
        private readonly Lazy<IIndexerFactory> _indexerFactory;
        private readonly IEventAggregator _eventAggregator;
        private readonly Logger _logger;
        private readonly ConcurrentDictionary<int, MyAnonaMouseSessionHealth> _state = new ConcurrentDictionary<int, MyAnonaMouseSessionHealth>();
        private readonly object _persistLock = new object();

        public MyAnonaMouseSessionMonitor(Lazy<IIndexerFactory> indexerFactory, IEventAggregator eventAggregator, Logger logger)
        {
            _indexerFactory = indexerFactory;
            _eventAggregator = eventAggregator;
            _logger = logger;
        }

        public MyAnonaMouseSessionHealth Get(int indexerId)
        {
            return _state.TryGetValue(indexerId, out var health) ? health.Clone() : new MyAnonaMouseSessionHealth();
        }

        public void RecordSessionAccepted(int indexerId, string mamId)
        {
            if (indexerId <= 0)
            {
                return;
            }

            var changed = false;
            _state.AddOrUpdate(indexerId,
                _ => new MyAnonaMouseSessionHealth(),
                (_, existing) =>
                {
                    var copy = existing.Clone();
                    if (copy.RejectedMamId != null && string.Equals(copy.RejectedMamId, mamId, StringComparison.Ordinal))
                    {
                        copy.RejectedMamId = null;
                        copy.RejectedSource = null;
                        copy.RejectedUtc = null;
                        changed = true;
                    }

                    return copy;
                });

            if (changed)
            {
                _logger.Info("MAM accepted the session for indexer {0} again", indexerId);
                Publish(indexerId);
            }
        }

        public void RecordSessionRejected(int indexerId, string mamId, string source)
        {
            if (indexerId <= 0 || string.IsNullOrWhiteSpace(mamId))
            {
                return;
            }

            var changed = false;
            _state.AddOrUpdate(indexerId,
                _ =>
                {
                    changed = true;
                    return new MyAnonaMouseSessionHealth { RejectedMamId = mamId, RejectedSource = source, RejectedUtc = DateTime.UtcNow };
                },
                (_, existing) =>
                {
                    var copy = existing.Clone();
                    changed = !string.Equals(copy.RejectedMamId, mamId, StringComparison.Ordinal);
                    copy.RejectedMamId = mamId;
                    copy.RejectedSource = source;
                    copy.RejectedUtc = DateTime.UtcNow;
                    return copy;
                });

            if (changed)
            {
                _logger.Warn("MAM rejected the mam_id for indexer {0} during {1}: the session expired or was revoked. Paste a new mam_id in the indexer settings.", indexerId, source);
                Publish(indexerId);
            }
        }

        public void RecordSeedboxResult(int indexerId, string mamId, MyAnonaMouseSeedboxResult result)
        {
            if (indexerId <= 0 || result == null)
            {
                return;
            }

            var changed = false;
            _state.AddOrUpdate(indexerId,
                _ =>
                {
                    changed = true;
                    return new MyAnonaMouseSessionHealth { Seedbox = result, SeedboxMamId = mamId };
                },
                (_, existing) =>
                {
                    var copy = existing.Clone();
                    changed = copy.Seedbox == null ||
                              copy.Seedbox.Outcome != result.Outcome ||
                              !string.Equals(copy.Seedbox.Message, result.Message, StringComparison.Ordinal);
                    copy.Seedbox = result;
                    copy.SeedboxMamId = mamId;
                    return copy;
                });

            if (changed)
            {
                Publish(indexerId);
            }
        }

        public void RecordRotatedCookie(int indexerId, MyAnonaMouseCookieField field, string previousValue, string rotatedValue)
        {
            if (indexerId <= 0 || string.IsNullOrWhiteSpace(rotatedValue))
            {
                return;
            }

            lock (_persistLock)
            {
                try
                {
                    var definition = _indexerFactory.Value.Find(indexerId);
                    if (definition?.Settings is not MyAnonaMouseSettings settings)
                    {
                        return;
                    }

                    // Only replace the value that was actually sent. If the user pasted a new
                    // mam_id in the meantime, theirs wins.
                    if (field == MyAnonaMouseCookieField.SeedboxMamId)
                    {
                        if (!string.Equals(settings.SeedboxMamId, previousValue, StringComparison.Ordinal))
                        {
                            return;
                        }

                        settings.SeedboxMamId = rotatedValue;
                    }
                    else
                    {
                        if (!string.Equals(settings.MamId, previousValue, StringComparison.Ordinal))
                        {
                            return;
                        }

                        settings.MamId = rotatedValue;
                    }

                    _indexerFactory.Value.Update(definition);
                    _logger.Info("MAM issued a new {0} for indexer '{1}'; saved it to the indexer settings", field == MyAnonaMouseCookieField.SeedboxMamId ? "seedbox mam_id" : "mam_id", definition.Name);
                }
                catch (Exception ex)
                {
                    _logger.Warn("Unable to save the rotated MAM cookie for indexer {0} ({1})", indexerId, ex.GetType().Name);
                }
            }
        }

        private void Publish(int indexerId)
        {
            try
            {
                _eventAggregator?.PublishEvent(new MyAnonaMouseSessionStatusChangedEvent(indexerId));
            }
            catch (Exception ex)
            {
                _logger.Debug(ex, "Unable to publish the MAM session status change");
            }
        }
    }
}
