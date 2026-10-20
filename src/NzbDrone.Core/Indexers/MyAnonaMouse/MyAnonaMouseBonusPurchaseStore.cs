using System;
using System.Collections.Generic;
using System.Data;
using System.Linq;
using Dapper;
using NLog;
using NzbDrone.Core.Talearr.Recordings;

namespace NzbDrone.Core.Indexers.MyAnonaMouse
{
    public interface IMyAnonaMouseBonusPurchaseStore
    {
        // False when talearr.db could not store it; the caller then makes no real purchase.
        bool Record(MyAnonaMouseBonusPurchaseRecord record);

        // Stores the reply for a row written before the request was sent.
        bool UpdateResult(MyAnonaMouseBonusPurchaseRecord record);

        // Newest first. indexerId null returns every indexer's rows.
        List<MyAnonaMouseBonusPurchaseRecord> GetRecent(int? indexerId, int limit);
    }

    // Talearr: the bonus auto-spend log in talearr.db (schema 7, MamBonusPurchases). Keeps the
    // newest KeepRows rows per indexer.
    public class MyAnonaMouseBonusPurchaseStore : IMyAnonaMouseBonusPurchaseStore
    {
        public const int KeepRows = 200;

        private readonly ITalearrDatabase _database;
        private readonly Logger _logger;

        public MyAnonaMouseBonusPurchaseStore(ITalearrDatabase database, Logger logger)
        {
            _database = database;
            _logger = logger;
        }

        public bool Record(MyAnonaMouseBonusPurchaseRecord record)
        {
            if (record == null)
            {
                return false;
            }

            return Run("record a MAM bonus purchase", false, connection =>
            {
                record.Id = connection.ExecuteScalar<long>(@"
INSERT INTO MamBonusPurchases (IndexerId, AttemptedAt, SpendType, Parameters, EstimatedCost, PointsBefore, DryRun, Success, Message, Response, Reason)
VALUES (@IndexerId, @AttemptedAt, @SpendType, @Parameters, @EstimatedCost, @PointsBefore, @DryRun, @Success, @Message, @Response, @Reason);
SELECT last_insert_rowid();", new
                {
                    record.IndexerId,
                    AttemptedAt = TalearrRecordingsStore.FormatDate(record.AttemptedAt),
                    record.SpendType,
                    record.Parameters,
                    record.EstimatedCost,
                    record.PointsBefore,
                    DryRun = record.DryRun ? 1 : 0,
                    Success = record.Success ? 1 : 0,
                    record.Message,
                    record.Response,
                    record.Reason
                });

                connection.Execute(@"
DELETE FROM MamBonusPurchases WHERE IndexerId = @IndexerId AND Id NOT IN (
    SELECT Id FROM MamBonusPurchases WHERE IndexerId = @IndexerId ORDER BY AttemptedAt DESC, Id DESC LIMIT @Keep);",
                    new { record.IndexerId, Keep = KeepRows });

                return true;
            });
        }

        public bool UpdateResult(MyAnonaMouseBonusPurchaseRecord record)
        {
            if (record == null || record.Id <= 0)
            {
                return false;
            }

            return Run("update a MAM bonus purchase", false, connection => connection.Execute(
                "UPDATE MamBonusPurchases SET Success = @Success, Message = @Message, Response = @Response WHERE Id = @Id",
                new { record.Id, Success = record.Success ? 1 : 0, record.Message, record.Response }) > 0);
        }

        public List<MyAnonaMouseBonusPurchaseRecord> GetRecent(int? indexerId, int limit)
        {
            return Run("read MAM bonus purchases", new List<MyAnonaMouseBonusPurchaseRecord>(), connection =>
                connection.Query<Row>(
                        indexerId.HasValue
                            ? "SELECT * FROM MamBonusPurchases WHERE IndexerId = @IndexerId ORDER BY AttemptedAt DESC, Id DESC LIMIT @Limit"
                            : "SELECT * FROM MamBonusPurchases ORDER BY AttemptedAt DESC, Id DESC LIMIT @Limit",
                        new { IndexerId = indexerId ?? 0, Limit = Math.Max(1, limit) })
                    .Select(r => new MyAnonaMouseBonusPurchaseRecord
                    {
                        Id = r.Id,
                        IndexerId = (int)r.IndexerId,
                        AttemptedAt = TalearrRecordingsStore.ParseDate(r.AttemptedAt),
                        SpendType = r.SpendType,
                        Parameters = r.Parameters,
                        EstimatedCost = (int)r.EstimatedCost,
                        PointsBefore = r.PointsBefore,
                        DryRun = r.DryRun != 0,
                        Success = r.Success != 0,
                        Message = r.Message,
                        Response = r.Response,
                        Reason = r.Reason
                    })
                    .ToList());
        }

        private T Run<T>(string what, T fallback, Func<IDbConnection, T> action)
        {
            try
            {
                using var connection = _database.OpenConnection();
                return action(connection);
            }
            catch (Exception ex) when (_database.IsCorruption(ex))
            {
                _database.Reset($"corruption found during {what}");
                return fallback;
            }
            catch (Exception ex)
            {
                _logger.Warn(ex, "Talearr: could not {0} in {1}", what, TalearrDatabase.FileName);
                return fallback;
            }
        }

        private sealed class Row
        {
            public long Id { get; set; }
            public long IndexerId { get; set; }
            public string AttemptedAt { get; set; }
            public string SpendType { get; set; }
            public string Parameters { get; set; }
            public long EstimatedCost { get; set; }
            public double? PointsBefore { get; set; }
            public long DryRun { get; set; }
            public long Success { get; set; }
            public string Message { get; set; }
            public string Response { get; set; }
            public string Reason { get; set; }
        }
    }
}
