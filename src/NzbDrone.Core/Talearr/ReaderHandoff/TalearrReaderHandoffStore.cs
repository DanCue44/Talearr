using System;
using System.Collections.Generic;
using System.Data;
using System.Globalization;
using System.Linq;
using Dapper;
using NLog;
using NzbDrone.Core.Talearr.Recordings;

namespace NzbDrone.Core.Talearr.ReaderHandoff
{
    public static class TalearrReaderHandoffStates
    {
        public const string Delivered = "delivered";
        public const string Failed = "failed";
    }

    public sealed class TalearrReaderHandoffDelivery
    {
        public int BookFileId { get; set; }
        public int BookId { get; set; }
        public string SourcePath { get; set; }
        public long SourceSize { get; set; }
        public string DeliveredPath { get; set; }
        public string State { get; set; }
        public int Attempts { get; set; }
        public string LastError { get; set; }
        public DateTime UpdatedAt { get; set; }

        public bool IsDeliveredCopyOf(string sourcePath, long size)
        {
            return State == TalearrReaderHandoffStates.Delivered &&
                   string.Equals(SourcePath, sourcePath, StringComparison.Ordinal) &&
                   SourceSize == size;
        }
    }

    public interface ITalearrReaderHandoffStore
    {
        TalearrReaderHandoffDelivery Get(int bookFileId);
        List<TalearrReaderHandoffDelivery> GetFailed();
        void Save(TalearrReaderHandoffDelivery delivery);
        void Remove(int bookFileId);
    }

    // Talearr: reader hand-off delivery log in talearr.db. Best effort like the other talearr.db
    // stores: a failure is logged and read as "no record", so a broken file never blocks a copy.
    public class TalearrReaderHandoffStore : ITalearrReaderHandoffStore
    {
        private readonly ITalearrDatabase _database;
        private readonly Logger _logger;

        public TalearrReaderHandoffStore(ITalearrDatabase database, Logger logger)
        {
            _database = database;
            _logger = logger;
        }

        public TalearrReaderHandoffDelivery Get(int bookFileId)
        {
            return Run("read a reader hand-off record", null, connection =>
                connection.Query<Row>("SELECT * FROM ReaderHandoffDeliveries WHERE BookFileId = @BookFileId", new { BookFileId = bookFileId })
                    .Select(ToDelivery)
                    .FirstOrDefault());
        }

        public List<TalearrReaderHandoffDelivery> GetFailed()
        {
            return Run("read failed reader hand-offs", new List<TalearrReaderHandoffDelivery>(), connection =>
                connection.Query<Row>("SELECT * FROM ReaderHandoffDeliveries WHERE State = @State ORDER BY UpdatedAt DESC", new { State = TalearrReaderHandoffStates.Failed })
                    .Select(ToDelivery)
                    .ToList());
        }

        public void Save(TalearrReaderHandoffDelivery delivery)
        {
            if (delivery == null || delivery.BookFileId <= 0)
            {
                return;
            }

            Run("save a reader hand-off record", 0, connection => connection.Execute(@"
INSERT INTO ReaderHandoffDeliveries (BookFileId, BookId, SourcePath, SourceSize, DeliveredPath, State, Attempts, LastError, UpdatedAt)
VALUES (@BookFileId, @BookId, @SourcePath, @SourceSize, @DeliveredPath, @State, @Attempts, @LastError, @UpdatedAt)
ON CONFLICT (BookFileId) DO UPDATE SET
    BookId = excluded.BookId,
    SourcePath = excluded.SourcePath,
    SourceSize = excluded.SourceSize,
    DeliveredPath = excluded.DeliveredPath,
    State = excluded.State,
    Attempts = excluded.Attempts,
    LastError = excluded.LastError,
    UpdatedAt = excluded.UpdatedAt", ToRow(delivery)));
        }

        public void Remove(int bookFileId)
        {
            Run("remove a reader hand-off record", 0, connection =>
                connection.Execute("DELETE FROM ReaderHandoffDeliveries WHERE BookFileId = @BookFileId", new { BookFileId = bookFileId }));
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
                _logger.Warn(ex, "Talearr: could not {0} in {1}; continuing without it", what, TalearrDatabase.FileName);
                return fallback;
            }
        }

        private static Row ToRow(TalearrReaderHandoffDelivery delivery)
        {
            return new Row
            {
                BookFileId = delivery.BookFileId,
                BookId = delivery.BookId,
                SourcePath = delivery.SourcePath ?? string.Empty,
                SourceSize = delivery.SourceSize,
                DeliveredPath = delivery.DeliveredPath,
                State = delivery.State ?? TalearrReaderHandoffStates.Failed,
                Attempts = delivery.Attempts,
                LastError = delivery.LastError,
                UpdatedAt = delivery.UpdatedAt.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture)
            };
        }

        private static TalearrReaderHandoffDelivery ToDelivery(Row row)
        {
            return new TalearrReaderHandoffDelivery
            {
                BookFileId = (int)row.BookFileId,
                BookId = (int)row.BookId,
                SourcePath = row.SourcePath,
                SourceSize = row.SourceSize,
                DeliveredPath = row.DeliveredPath,
                State = row.State,
                Attempts = (int)row.Attempts,
                LastError = row.LastError,
                UpdatedAt = DateTime.TryParse(row.UpdatedAt, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var updated) ? updated : DateTime.MinValue
            };
        }

        private class Row
        {
            public long BookFileId { get; set; }
            public long BookId { get; set; }
            public string SourcePath { get; set; }
            public long SourceSize { get; set; }
            public string DeliveredPath { get; set; }
            public string State { get; set; }
            public long Attempts { get; set; }
            public string LastError { get; set; }
            public string UpdatedAt { get; set; }
        }
    }
}
