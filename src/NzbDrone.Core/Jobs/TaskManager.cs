using System;
using System.Collections.Generic;
using System.Linq;
using NLog;
using NzbDrone.Common.Cache;
using NzbDrone.Core.Backup;
using NzbDrone.Core.Books.Commands;
using NzbDrone.Core.Configuration;
using NzbDrone.Core.Configuration.Events;
using NzbDrone.Core.Download;
using NzbDrone.Core.HealthCheck;
using NzbDrone.Core.Housekeeping;
using NzbDrone.Core.ImportLists;
using NzbDrone.Core.ImportLists.Hardcover.Library;
using NzbDrone.Core.Indexers;
using NzbDrone.Core.Indexers.MyAnonaMouse;
using NzbDrone.Core.Lifecycle;
using NzbDrone.Core.MediaFiles.Commands;
using NzbDrone.Core.MediaCover.Commands;
using NzbDrone.Core.Messaging.Commands;
using NzbDrone.Core.Messaging.Events;
using NzbDrone.Core.Talearr.MissingSearch;
using NzbDrone.Core.ThingiProvider.Events;
using NzbDrone.Core.Update.Commands;

namespace NzbDrone.Core.Jobs
{
    public interface ITaskManager
    {
        IList<ScheduledTask> GetPending();
        List<ScheduledTask> GetAll();
        DateTime GetNextExecution(Type type);
    }

    public class TaskManager : ITaskManager, IHandle<ApplicationStartedEvent>, IHandle<CommandExecutedEvent>, IHandleAsync<ConfigSavedEvent>,
                               IHandle<TalearrMissingSearchSettingsSavedEvent>,
                               IHandle<ProviderAddedEvent<IIndexer>>, IHandle<ProviderUpdatedEvent<IIndexer>>,
                               IHandle<ProviderDeletedEvent<IIndexer>>
    {
        private readonly IScheduledTaskRepository _scheduledTaskRepository;
        private readonly IConfigService _configService;
        private readonly ITalearrMissingSearchSettingsService _talearrMissingSearch;
        private readonly IIndexerFactory _indexerFactory;
        private readonly Logger _logger;
        private readonly ICached<ScheduledTask> _cache;

        public TaskManager(IScheduledTaskRepository scheduledTaskRepository, IConfigService configService, ICacheManager cacheManager, Logger logger, ITalearrMissingSearchSettingsService talearrMissingSearch = null, IIndexerFactory indexerFactory = null)
        {
            _scheduledTaskRepository = scheduledTaskRepository;
            _configService = configService;
            _talearrMissingSearch = talearrMissingSearch;
            _indexerFactory = indexerFactory;
            _cache = cacheManager.GetCache<ScheduledTask>(GetType());
            _logger = logger;
        }

        public IList<ScheduledTask> GetPending()
        {
            var now = DateTime.UtcNow;

            return _cache.Values
                         .Where(task => task.Interval > 0 && IsTaskPending(task, now))
                         .ToList();
        }

        public List<ScheduledTask> GetAll()
        {
            return _cache.Values.ToList();
        }

        public DateTime GetNextExecution(Type type)
        {
            var scheduledTask = GetScheduledTask(type);

            if (scheduledTask == null)
            {
                _logger.Warn("Scheduled task '{0}' was requested before it was initialized; returning now as next execution", type.FullName);
                return DateTime.UtcNow;
            }

            try
            {
                return scheduledTask.LastExecution.AddMinutes(scheduledTask.Interval);
            }
            catch (ArgumentOutOfRangeException ex)
            {
                _logger.Warn(ex, "Invalid scheduled task timestamps for '{0}'; returning now as next execution", scheduledTask.TypeName ?? type.FullName);
                return DateTime.UtcNow;
            }
        }

        private ScheduledTask GetScheduledTask(Type type)
        {
            var typeName = type.FullName;
            var scheduledTask = _cache.Find(typeName);

            if (scheduledTask != null)
            {
                return scheduledTask;
            }

            try
            {
                scheduledTask = _scheduledTaskRepository.GetDefinition(type);
            }
            catch (InvalidOperationException ex)
            {
                _logger.Warn(ex, "Scheduled task '{0}' was not found in the database while rebuilding the task cache", typeName);
                return null;
            }

            _cache.Set(typeName, scheduledTask);

            return scheduledTask;
        }

        public void Handle(ApplicationStartedEvent message)
        {
            var defaultTasks = new List<ScheduledTask>
                {
                    new ScheduledTask
                    {
                        Interval = 1,
                        TypeName = typeof(RefreshMonitoredDownloadsCommand).FullName,
                        Priority = CommandPriority.High
                    },

                    new ScheduledTask
                    {
                        Interval = 5,
                        TypeName = typeof(MessagingCleanupCommand).FullName
                    },

                    new ScheduledTask
                    {
                        Interval = 6 * 60,
                        TypeName = typeof(ApplicationUpdateCheckCommand).FullName
                    },

                    new ScheduledTask
                    {
                        Interval = 6 * 60,
                        TypeName = typeof(CheckHealthCommand).FullName
                    },

                    // Talearr: follows the shortest MAM Account Status Refresh (60 by default).
                    new ScheduledTask
                    {
                        Interval = GetMamAccountStatusInterval(),
                        TypeName = typeof(RefreshMyAnonaMouseAccountStatusCommand).FullName
                    },

                    // Talearr: MAM dynamic seedbox IP. The service applies each indexer's own
                    // interval and does nothing unless the update is enabled. Stock Chaptarr
                    // drops this row on its next start.
                    new ScheduledTask
                    {
                        Interval = GetMamDynamicSeedboxInterval(),
                        TypeName = typeof(UpdateMyAnonaMouseDynamicSeedboxCommand).FullName,
                        Priority = CommandPriority.Low
                    },

                    // Talearr: MAM bonus points auto-spend. Does nothing unless a rule is enabled
                    // on the indexer. Stock Chaptarr drops this row on its next start.
                    new ScheduledTask
                    {
                        Interval = MyAnonaMouseBonusSpendService.SchedulerIntervalMinutes,
                        TypeName = typeof(SpendMyAnonaMouseBonusPointsCommand).FullName,
                        Priority = CommandPriority.Low
                    },

                    // Talearr: MAM freeleech seeding filler. Checks hourly; the service applies the
                    // indexer's own run interval and does nothing unless the filler is on.
                    new ScheduledTask
                    {
                        Interval = 60,
                        TypeName = typeof(NzbDrone.Core.Talearr.MamFiller.MamFreeleechFillerCommand).FullName,
                        Priority = CommandPriority.Low
                    },

                    new ScheduledTask
                    {
                        Interval = 24 * 60,
                        TypeName = typeof(RefreshAuthorCommand).FullName
                    },

                    new ScheduledTask
                    {
                        Interval = 24 * 60,
                        TypeName = typeof(RescanFoldersCommand).FullName
                    },

                    new ScheduledTask
                    {
                        Interval = 24 * 60,
                        TypeName = typeof(HousekeepingCommand).FullName
                    },

                    new ScheduledTask
                    {
                        Interval = 24 * 60,
                        TypeName = typeof(RepairAuthorMediaCoversCommand).FullName,
                        Priority = CommandPriority.Low
                    },

                    new ScheduledTask
                    {
                        Interval = GetBackupInterval(),
                        TypeName = typeof(BackupCommand).FullName
                    },

                    new ScheduledTask
                    {
                        Interval = 5,
                        TypeName = typeof(ImportListSyncCommand).FullName
                    },

                    new ScheduledTask
                    {
                        Interval = 60,
                        TypeName = typeof(HardcoverLibrarySyncCommand).FullName
                    },

                    new ScheduledTask
                    {
                        Interval = GetRssSyncInterval(),
                        TypeName = typeof(RssSyncCommand).FullName
                    },

                    new ScheduledTask
                    {
                        Interval = 1, // Run every minute
                        TypeName = typeof(ProcessPendingImportsCommand).FullName,
                        Priority = CommandPriority.Low
                    },

                    // Talearr: Audible facts for recordings (talearr.db only). Stock Chaptarr drops
                    // this row on its next start, so a rollback needs nothing.
                    new ScheduledTask
                    {
                        Interval = 12 * 60,
                        TypeName = typeof(NzbDrone.Core.Talearr.Recordings.RefreshAudibleRecordingFactsCommand).FullName,
                        Priority = CommandPriority.Low
                    },

                    // Talearr: read the Audiobookshelf libraries into talearr.db (GET only). Stock
                    // Chaptarr drops this row on its next start, so a rollback needs nothing.
                    new ScheduledTask
                    {
                        Interval = 60,
                        TypeName = typeof(NzbDrone.Core.Talearr.Audiobookshelf.SyncAudiobookshelfLibraryCommand).FullName,
                        Priority = CommandPriority.Low
                    },

                    // Talearr: monitored series pick up books added since the last run (also queued
                    // after every author refresh). Local reads only. Stock Chaptarr drops the row.
                    new ScheduledTask
                    {
                        Interval = 6 * 60,
                        TypeName = typeof(NzbDrone.Core.Talearr.SeriesMonitoring.ApplySeriesMonitoringCommand).FullName,
                        Priority = CommandPriority.Low
                    },

                    // Talearr: comics. Refresh series daily and search for missing volumes twice a
                    // day; both do nothing until a comic series exists. Stock Chaptarr drops the rows.
                    new ScheduledTask
                    {
                        Interval = 24 * 60,
                        TypeName = typeof(NzbDrone.Core.Talearr.Comics.RefreshComicSeriesCommand).FullName,
                        Priority = CommandPriority.Low
                    },

                    new ScheduledTask
                    {
                        Interval = 12 * 60,
                        TypeName = typeof(NzbDrone.Core.Talearr.Comics.SearchMissingComicsCommand).FullName,
                        Priority = CommandPriority.Low
                    },

                    // Talearr: wishlists (CSV exports, Hardcover lists) add their new rows. Stock
                    // Chaptarr drops this row on its next start.
                    new ScheduledTask
                    {
                        Interval = 12 * 60,
                        TypeName = typeof(NzbDrone.Core.Talearr.Wishlists.SyncWishlistsCommand).FullName,
                        Priority = CommandPriority.Low
                    },

                    // Talearr: scheduled missing-books search (Settings > Indexers). Off (interval 0)
                    // by default. Stock Chaptarr drops this row on its next start.
                    new ScheduledTask
                    {
                        Interval = GetTalearrMissingSearchInterval(),
                        TypeName = typeof(ScheduledMissingBookSearchCommand).FullName,
                        Priority = CommandPriority.Low
                    }
                };

            List<ScheduledTask> currentTasks;
            try
            {
                currentTasks = _scheduledTaskRepository.All().ToList();
            }
            catch (Exception ex)
            {
                _logger.Error(ex, "Failed to load scheduled tasks from database; using default task definitions");
                currentTasks = new List<ScheduledTask>();
            }

            _logger.Trace("Initializing jobs. Available: {0} Existing: {1}", defaultTasks.Count, currentTasks.Count);

            foreach (var job in currentTasks)
            {
                if (!defaultTasks.Any(c => c.TypeName == job.TypeName))
                {
                    _logger.Trace("Removing job from database '{0}'", job.TypeName);
                    _scheduledTaskRepository.Delete(job.Id);
                }
            }

            foreach (var defaultTask in defaultTasks)
            {
                var currentDefinition = currentTasks.SingleOrDefault(c => c.TypeName == defaultTask.TypeName) ?? defaultTask;

                currentDefinition.Interval = defaultTask.Interval;

                if (currentDefinition.Id == 0)
                {
                    currentDefinition.LastExecution = DateTime.UtcNow;
                    currentDefinition.LastStartTime = currentDefinition.LastExecution;
                }

                if (currentDefinition.LastExecution == default(DateTime))
                {
                    currentDefinition.LastExecution = DateTime.UtcNow;
                }

                if (currentDefinition.LastStartTime == default(DateTime) || currentDefinition.LastStartTime > currentDefinition.LastExecution)
                {
                    currentDefinition.LastStartTime = currentDefinition.LastExecution;
                }

                currentDefinition.Priority = defaultTask.Priority;

                _cache.Set(currentDefinition.TypeName, currentDefinition);
                _scheduledTaskRepository.Upsert(currentDefinition);
            }
        }

        private int GetTalearrMissingSearchInterval()
        {
            return TalearrMissingSearchSettingsService.Resolve(_talearrMissingSearch).IntervalMinutes;
        }

        // Talearr: apply a new missing-search schedule. Turning it on starts the clock now, so the
        // first run is one interval away instead of immediately.
        public void Handle(TalearrMissingSearchSettingsSavedEvent message)
        {
            var task = GetScheduledTask(typeof(ScheduledMissingBookSearchCommand));
            if (task == null)
            {
                return;
            }

            var interval = message?.Settings?.IntervalMinutes ?? 0;
            if (task.Interval == interval)
            {
                return;
            }

            task.Interval = interval;

            if (interval > 0 && message.WasOff)
            {
                task.LastExecution = DateTime.UtcNow;
                task.LastStartTime = task.LastExecution;
            }

            _scheduledTaskRepository.Update(task);
            _cache.Set(task.TypeName, task);
        }

        // Talearr: MAM task ticks follow the MAM indexer settings (MyAnonaMouseSchedule).
        public void Handle(ProviderAddedEvent<IIndexer> message)
        {
            ApplyMamIntervals();
        }

        public void Handle(ProviderUpdatedEvent<IIndexer> message)
        {
            ApplyMamIntervals();
        }

        public void Handle(ProviderDeletedEvent<IIndexer> message)
        {
            ApplyMamIntervals();
        }

        private void ApplyMamIntervals()
        {
            ApplyInterval(typeof(RefreshMyAnonaMouseAccountStatusCommand), GetMamAccountStatusInterval());
            ApplyInterval(typeof(UpdateMyAnonaMouseDynamicSeedboxCommand), GetMamDynamicSeedboxInterval());
        }

        private void ApplyInterval(Type commandType, int interval)
        {
            var task = GetScheduledTask(commandType);
            if (task == null || task.Interval == interval)
            {
                return;
            }

            _logger.Debug("Scheduled task '{0}' now runs every {1} minutes", commandType.Name, interval);
            task.Interval = interval;
            _scheduledTaskRepository.Update(task);
            _cache.Set(task.TypeName, task);
        }

        private List<IndexerDefinition> GetIndexerDefinitions()
        {
            try
            {
                return _indexerFactory?.All() ?? new List<IndexerDefinition>();
            }
            catch (Exception ex)
            {
                _logger.Warn(ex, "Could not read indexer settings for the MAM task intervals; using defaults");
                return new List<IndexerDefinition>();
            }
        }

        private int GetMamAccountStatusInterval()
        {
            return MyAnonaMouseSchedule.AccountStatusTaskMinutes(GetIndexerDefinitions());
        }

        private int GetMamDynamicSeedboxInterval()
        {
            return MyAnonaMouseSchedule.DynamicSeedboxTaskMinutes(GetIndexerDefinitions());
        }

        private int GetBackupInterval()
        {
            var interval = _configService.BackupInterval;

            if (interval < 1)
            {
                interval = 1;
            }

            return interval * 60 * 24;
        }

        private int GetRssSyncInterval()
        {
            var interval = _configService.RssSyncInterval;

            if (interval > 0 && interval < 10)
            {
                return 10;
            }

            if (interval < 0)
            {
                return 0;
            }

            return interval;
        }

        public void Handle(CommandExecutedEvent message)
        {
            var typeName = message.Command.Body.GetType().FullName;
            var scheduledTask = _cache.Find(typeName);

            if (scheduledTask != null && message.Command.Body.UpdateScheduledTask)
            {
                _logger.Trace("Updating last run time for: {0}", scheduledTask.TypeName);

                var lastExecution = DateTime.UtcNow;
                var startTime = message.Command.StartedAt.Value;

                _scheduledTaskRepository.SetLastExecutionTime(scheduledTask.Id, lastExecution, startTime);

                scheduledTask.LastExecution = lastExecution;
                scheduledTask.LastStartTime = startTime;
            }
        }

        private bool IsTaskPending(ScheduledTask task, DateTime now)
        {
            try
            {
                return task.LastExecution.AddMinutes(task.Interval) < now;
            }
            catch (ArgumentOutOfRangeException ex)
            {
                _logger.Warn(ex, "Invalid scheduled task timestamps for '{0}'; forcing task to run", task.TypeName);
                return true;
            }
        }

        public void HandleAsync(ConfigSavedEvent message)
        {
            var rss = _scheduledTaskRepository.GetDefinition(typeof(RssSyncCommand));
            rss.Interval = GetRssSyncInterval();

            var backup = _scheduledTaskRepository.GetDefinition(typeof(BackupCommand));
            backup.Interval = GetBackupInterval();

            _scheduledTaskRepository.UpdateMany(new List<ScheduledTask> { rss, backup });

            _cache.Set(rss.TypeName, rss);
            _cache.Set(backup.TypeName, backup);
        }
    }
}
