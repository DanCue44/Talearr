using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using NzbDrone.Core.Configuration;
using NzbDrone.Core.Download;
using NzbDrone.Core.Download.TrackedDownloads;
using NzbDrone.Core.History;
using NzbDrone.Core.Indexers;
using NzbDrone.Core.Messaging.Commands;
using NzbDrone.Core.Profiles.Delay;
using NzbDrone.Core.Talearr.GrabPolicy;

namespace Chaptarr.Core.Test.Talearr.GrabPolicy
{
    internal sealed class FakeGrabPolicyService : ITalearrGrabPolicyService
    {
        public TalearrGrabPolicy Policy { get; set; } = TalearrGrabPolicy.Default;

        public TalearrGrabPolicy Get()
        {
            return Policy.Clone();
        }

        public TalearrGrabPolicy Save(TalearrGrabPolicy policy)
        {
            Policy = policy.Clone();
            return Policy.Clone();
        }
    }

    internal class ConfigRepositoryProxy : DispatchProxy
    {
        public Dictionary<string, string> Values { get; } = new(StringComparer.Ordinal);

        protected override object Invoke(MethodInfo targetMethod, object[] args)
        {
            switch (targetMethod?.Name)
            {
                case nameof(IConfigRepository.Get):
                    var key = (string)args[0];
                    return Values.TryGetValue(key, out var value) ? new Config { Key = key, Value = value } : null;

                case nameof(IConfigRepository.Upsert):
                    Values[(string)args[0]] = (string)args[1];
                    return new Config { Key = (string)args[0], Value = (string)args[1] };

                default:
                    throw new NotImplementedException($"Test proxy does not implement IConfigRepository.{targetMethod?.Name}");
            }
        }

        public static (IConfigRepository Repository, ConfigRepositoryProxy Proxy) Create()
        {
            var repository = DispatchProxy.Create<IConfigRepository, ConfigRepositoryProxy>();
            return (repository, (ConfigRepositoryProxy)(object)repository);
        }
    }

    internal class HistoryServiceProxy : DispatchProxy
    {
        public List<EntityHistory> Rows { get; } = new();
        public List<EntityHistory> Updated { get; } = new();

        protected override object Invoke(MethodInfo targetMethod, object[] args)
        {
            switch (targetMethod?.Name)
            {
                case nameof(IHistoryService.GetByBook):
                    var bookId = (int)args[0];
                    var eventType = (EntityHistoryEventType?)args[1];
                    return Rows.Where(h => h.BookId == bookId && (!eventType.HasValue || h.EventType == eventType.Value)).ToList();

                case nameof(IHistoryService.FindByDownloadId):
                    return Rows.Where(h => string.Equals(h.DownloadId, (string)args[0], StringComparison.OrdinalIgnoreCase)).ToList();

                case nameof(IHistoryService.Find):
                    var type = (EntityHistoryEventType)args[1];
                    return Rows.Where(h => string.Equals(h.DownloadId, (string)args[0], StringComparison.OrdinalIgnoreCase) && h.EventType == type).ToList();

                case nameof(IHistoryService.UpdateMany):
                    Updated.AddRange((IList<EntityHistory>)args[0]);
                    return null;

                default:
                    throw new NotImplementedException($"Test proxy does not implement IHistoryService.{targetMethod?.Name}");
            }
        }

        public static (IHistoryService Service, HistoryServiceProxy Proxy) Create()
        {
            var service = DispatchProxy.Create<IHistoryService, HistoryServiceProxy>();
            return (service, (HistoryServiceProxy)(object)service);
        }
    }

    internal class CommandQueueProxy : DispatchProxy
    {
        public List<(Command Command, CommandPriority Priority, CommandTrigger Trigger)> Pushed { get; } = new();

        protected override object Invoke(MethodInfo targetMethod, object[] args)
        {
            if (targetMethod?.Name == nameof(IManageCommandQueue.Push) && args.Length == 3 && args[0] is Command command)
            {
                Pushed.Add((command, (CommandPriority)args[1], (CommandTrigger)args[2]));
                return null;
            }

            throw new NotImplementedException($"Test proxy does not implement IManageCommandQueue.{targetMethod?.Name}");
        }

        public static (IManageCommandQueue Queue, CommandQueueProxy Proxy) Create()
        {
            var queue = DispatchProxy.Create<IManageCommandQueue, CommandQueueProxy>();
            return (queue, (CommandQueueProxy)(object)queue);
        }
    }

    internal class DownloadConfigProxy : DispatchProxy
    {
        public bool AutoRedownloadFailed { get; set; } = true;
        public bool AutoRedownloadFailedFromInteractiveSearch { get; set; } = true;

        protected override object Invoke(MethodInfo targetMethod, object[] args)
        {
            switch (targetMethod?.Name)
            {
                case "get_AutoRedownloadFailed":
                    return AutoRedownloadFailed;
                case "get_AutoRedownloadFailedFromInteractiveSearch":
                    return AutoRedownloadFailedFromInteractiveSearch;
                default:
                    throw new NotImplementedException($"Test proxy does not implement IConfigService.{targetMethod?.Name}");
            }
        }

        public static (IConfigService Config, DownloadConfigProxy Proxy) Create()
        {
            var config = DispatchProxy.Create<IConfigService, DownloadConfigProxy>();
            return (config, (DownloadConfigProxy)(object)config);
        }
    }

    internal class FailedDownloadServiceRecorder : IFailedDownloadService
    {
        public List<(TrackedDownload TrackedDownload, string Reason)> Failed { get; } = new();

        public void MarkAsFailed(int historyId, bool skipRedownload = false)
        {
            throw new NotImplementedException();
        }

        public void MarkAsFailed(string downloadId, bool skipRedownload = false)
        {
            throw new NotImplementedException();
        }

        public void MarkAsFailed(TrackedDownload trackedDownload, string reason, bool skipRedownload = false)
        {
            Failed.Add((trackedDownload, reason));
            trackedDownload.State = TrackedDownloadState.DownloadFailed;
        }

        public void Check(TrackedDownload trackedDownload)
        {
            throw new NotImplementedException();
        }

        public void ProcessFailed(TrackedDownload trackedDownload)
        {
            throw new NotImplementedException();
        }
    }

    internal class DelayProfileServiceProxy : DispatchProxy
    {
        protected override object Invoke(MethodInfo targetMethod, object[] args)
        {
            if (targetMethod?.Name == nameof(IDelayProfileService.BestForTags))
            {
                return new DelayProfile { PreferredProtocol = DownloadProtocol.Usenet };
            }

            throw new NotImplementedException($"Test proxy does not implement IDelayProfileService.{targetMethod?.Name}");
        }

        public static IDelayProfileService Create()
        {
            return DispatchProxy.Create<IDelayProfileService, DelayProfileServiceProxy>();
        }
    }
}
