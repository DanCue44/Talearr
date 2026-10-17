using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using NLog;
using NzbDrone.Core.HealthCheck;
using NzbDrone.Core.Messaging.Commands;
using NzbDrone.Core.Messaging.Events;

namespace NzbDrone.Core.Indexers.MyAnonaMouse
{
    // TaleArr: bonus points auto-spend. The hourly "Spend My Anona Mouse Bonus Points" task
    // (System > Tasks) checks each native MAM indexer with a rule enabled, using the stored
    // account status (refreshed first when it is older than an hour), and makes at most one
    // purchase per indexer per run. Every attempt is written to talearr.db before the request is
    // sent, so the daily cap and the back-off hold even if TaleArr stops mid-purchase. The account
    // status is refreshed right after a purchase. Only VIP, upload and wedges can be bought.
    public class MyAnonaMouseBonusSpendService : IExecute<SpendMyAnonaMouseBonusPointsCommand>
    {
        public const int SchedulerIntervalMinutes = 60;

        private static readonly TimeSpan RefreshBeforeSpendAge = TimeSpan.FromMinutes(60);

        private readonly IIndexerFactory _indexerFactory;
        private readonly IMyAnonaMouseBonusPurchaseStore _store;
        private readonly IEventAggregator _eventAggregator;
        private readonly Logger _logger;
        private readonly object _runLock = new object();

        public MyAnonaMouseBonusSpendService(IIndexerFactory indexerFactory,
                                             IMyAnonaMouseBonusPurchaseStore store,
                                             IEventAggregator eventAggregator,
                                             Logger logger)
        {
            _indexerFactory = indexerFactory;
            _store = store;
            _eventAggregator = eventAggregator;
            _logger = logger;
        }

        // For tests.
        internal Func<DateTime> UtcNow { get; set; } = () => DateTime.UtcNow;

        public void Execute(SpendMyAnonaMouseBonusPointsCommand message)
        {
            Run(message.Trigger == CommandTrigger.Manual ? "manual run" : "scheduled task");
        }

        // Returns the attempts made (including dry runs), for tests.
        public List<MyAnonaMouseBonusPurchaseRecord> Run(string source)
        {
            var attempts = new List<MyAnonaMouseBonusPurchaseRecord>();

            if (!System.Threading.Monitor.TryEnter(_runLock))
            {
                return attempts;
            }

            try
            {
                foreach (var indexer in _indexerFactory.GetAvailableProviders().OfType<MyAnonaMouse>())
                {
                    if (indexer.Definition is not IndexerDefinition definition ||
                        definition.Settings is not MyAnonaMouseSettings settings ||
                        !MyAnonaMouseBonusSpendRules.AnyRuleEnabled(settings))
                    {
                        continue;
                    }

                    try
                    {
                        var attempt = RunFor(indexer, definition, settings, source);
                        if (attempt != null)
                        {
                            attempts.Add(attempt);
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.Warn(ex, "MAM bonus auto-spend for '{0}' failed", definition.Name);
                    }
                }
            }
            finally
            {
                System.Threading.Monitor.Exit(_runLock);
            }

            return attempts;
        }

        private MyAnonaMouseBonusPurchaseRecord RunFor(MyAnonaMouse indexer, IndexerDefinition definition, MyAnonaMouseSettings settings, string source)
        {
            var now = UtcNow();

            if (!settings.UnsatisfiedStatusRefreshedUtc.HasValue ||
                now - settings.UnsatisfiedStatusRefreshedUtc.Value >= RefreshBeforeSpendAge)
            {
                TryRefresh(indexer, definition, "before bonus auto-spend");
                now = UtcNow();
            }

            var history = _store.GetRecent(definition.Id, 50);
            var decision = MyAnonaMouseBonusSpendRules.Decide(settings, history, now);
            var purchase = decision.Purchase;

            if (purchase == null)
            {
                _logger.Debug("MAM bonus auto-spend for '{0}' ({1}): nothing to buy ({2})", definition.Name, source, string.Join("; ", decision.Skipped));
                return null;
            }

            var record = new MyAnonaMouseBonusPurchaseRecord
            {
                IndexerId = definition.Id,
                AttemptedAt = now,
                SpendType = MyAnonaMouseBonusRequestBuilder.SpendTypeValue(purchase.SpendType),
                Parameters = MyAnonaMouseBonusRequestBuilder.BuildParameters(purchase),
                EstimatedCost = purchase.EstimatedCost,
                PointsBefore = settings.BonusPoints,
                Reason = purchase.Reason
            };

            if (settings.BonusDryRun)
            {
                record.DryRun = true;
                record.Message = "Dry run: not sent to MAM";
                _store.Record(record);
                _logger.Info("MAM bonus auto-spend for '{0}' (dry run): would buy {1} for about {2} points because {3}",
                    definition.Name, record.Parameters, Format(record.EstimatedCost), purchase.Reason);
                return record;
            }

            // Written first: without the row the cap and back-off cannot hold, so do not buy.
            record.Message = "Sent, no reply yet";
            if (!_store.Record(record))
            {
                _logger.Warn("MAM bonus auto-spend for '{0}': not buying {1} because the purchase log in talearr.db could not be written", definition.Name, record.Parameters);
                return null;
            }

            MyAnonaMouseBonusPurchaseResult result;
            try
            {
                result = indexer.BuyBonusAsync(purchase).GetAwaiter().GetResult();
            }
            catch (Exception ex)
            {
                result = MyAnonaMouseBonusPurchaseResult.Failed($"{ex.GetType().Name}: {ex.Message}", null);
            }

            record.Success = result.Success;
            record.Message = result.Message;
            record.Response = result.RawResponse;
            _store.UpdateResult(record);

            if (result.Success)
            {
                _logger.Info("MAM bonus auto-spend for '{0}' ({1}): bought {2} (about {3} points) because {4}. MAM said: {5}",
                    definition.Name, source, record.Parameters, Format(record.EstimatedCost), purchase.Reason, result.RawResponse);
            }
            else
            {
                _logger.Warn("MAM bonus auto-spend for '{0}' ({1}): {2} failed ({3}); not retrying {4} for 24 hours. MAM said: {5}",
                    definition.Name, source, record.Parameters, result.Message, record.SpendType, result.RawResponse);
            }

            Notify(definition, record);

            // Fresh balance, VIP and wedge counts before anything else uses them. Also saves a
            // rotated mam_id from the purchase reply.
            if (!TryRefresh(indexer, definition, "after a bonus purchase"))
            {
                SafeUpdate(definition);
            }

            return record;
        }

        private bool TryRefresh(MyAnonaMouse indexer, IndexerDefinition definition, string why)
        {
            try
            {
                indexer.RefreshAccountStatus().GetAwaiter().GetResult();
                SafeUpdate(definition);
                return true;
            }
            catch (Exception ex)
            {
                _logger.Warn("Failed to refresh MAM account status for indexer '{0}' {1} ({2})", definition.Name, why, ex.GetType().Name);
                return false;
            }
        }

        private void SafeUpdate(IndexerDefinition definition)
        {
            try
            {
                _indexerFactory.Update(definition);
            }
            catch (Exception ex)
            {
                _logger.Debug(ex, "Could not save MAM indexer '{0}' after bonus auto-spend", definition.Name);
            }
        }

        private void Notify(IndexerDefinition definition, MyAnonaMouseBonusPurchaseRecord record)
        {
            var what = record.SpendType switch
            {
                "VIP" => "VIP",
                "upload" => "upload credit (" + record.Parameters + ")",
                _ => "a freeleech wedge"
            };

            var message = record.Success
                ? $"MAM '{definition.Name}': bought {what} for about {Format(record.EstimatedCost)} bonus points ({record.Reason})."
                : $"MAM '{definition.Name}': buying {what} failed ({record.Message}); TaleArr will not retry it for 24 hours.";

            try
            {
                // Through On Health Issue, like TaleArr's other notices; never stored as a health result.
                var check = new HealthCheck.HealthCheck(typeof(TalearrMyAnonaMouseBonusPurchase),
                    record.Success ? HealthCheckResult.Notice : HealthCheckResult.Warning,
                    message,
                    "#mam-bonus-auto-spend");
                _eventAggregator.PublishEvent(new HealthCheckFailedEvent(check, false));
            }
            catch (Exception ex)
            {
                _logger.Debug(ex, "Could not send the MAM bonus purchase notification");
            }
        }

        private static string Format(double value)
        {
            return Math.Floor(value).ToString("N0", CultureInfo.InvariantCulture);
        }
    }
}
