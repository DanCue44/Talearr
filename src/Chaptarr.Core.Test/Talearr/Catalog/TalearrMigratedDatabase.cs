using System;
using System.Collections.Generic;
using System.Data;
using System.IO;
using System.Linq;
using Dapper;
using Microsoft.Data.Sqlite;
using NLog;
using NUnit.Framework;
using NzbDrone.Common.Messaging;
using NzbDrone.Core.Datastore;
using NzbDrone.Core.Datastore.Migration.Framework;
using NzbDrone.Core.Messaging.Events;

namespace Chaptarr.Core.Test.Talearr.Catalog
{
    // TaleArr: a temporary chaptarr.db built by the real migrations, with an insert helper that
    // fills every NOT NULL column the test does not care about.
    public sealed class TalearrMigratedDatabase : IDisposable
    {
        private readonly Dictionary<string, List<(string Name, string Type)>> _required = new Dictionary<string, List<(string Name, string Type)>>(StringComparer.OrdinalIgnoreCase);

        public TalearrMigratedDatabase(string name)
        {
            Folder = Path.Combine(TestContext.CurrentContext.WorkDirectory, $"{name}_{Guid.NewGuid():N}");
            Directory.CreateDirectory(Folder);
            DatabasePath = Path.Combine(Folder, "chaptarr.db");
            ConnectionString = new SqliteConnectionStringBuilder
            {
                DataSource = DatabasePath,
                Mode = SqliteOpenMode.ReadWriteCreate,
                Pooling = false
            }.ToString();

            new MigrationController(LogManager.GetLogger(name), null)
                .Migrate(ConnectionString, new MigrationContext(MigrationType.Main), DatabaseType.SQLite);

            MainDatabase = new MainDatabase(new Database("main", () =>
            {
                var connection = new SqliteConnection(ConnectionString);
                connection.Open();
                return connection;
            }));
        }

        public string Folder { get; }
        public string DatabasePath { get; }
        public string ConnectionString { get; }
        public IMainDatabase MainDatabase { get; }

        public IDbConnection Open()
        {
            var connection = new SqliteConnection(ConnectionString);
            connection.Open();
            return connection;
        }

        public int Insert(IDbConnection conn, string table, object values, IDbTransaction tran = null)
        {
            var row = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
            foreach (var property in values.GetType().GetProperties())
            {
                row[property.Name] = property.GetValue(values);
            }

            foreach (var column in Required(conn, table).Where(c => !row.ContainsKey(c.Name)))
            {
                row[column.Name] = Default(column.Type);
            }

            var names = row.Keys.ToList();
            var sql = $"INSERT INTO \"{table}\" ({string.Join(", ", names.Select(n => $"\"{n}\""))}) VALUES ({string.Join(", ", names.Select(n => "@" + n))}); SELECT last_insert_rowid();";
            var parameters = new DynamicParameters();
            foreach (var kv in row)
            {
                parameters.Add(kv.Key, kv.Value);
            }

            return conn.ExecuteScalar<int>(sql, parameters, tran);
        }

        private List<(string Name, string Type)> Required(IDbConnection conn, string table)
        {
            if (!_required.TryGetValue(table, out var columns))
            {
                columns = conn.Query<(string Name, string Type)>(
                    $"SELECT name, type FROM pragma_table_info('{table}') WHERE \"notnull\" = 1 AND dflt_value IS NULL AND pk = 0").ToList();
                _required[table] = columns;
            }

            return columns;
        }

        private static object Default(string type)
        {
            switch (type?.ToUpperInvariant())
            {
                case "INTEGER":
                case "NUMERIC":
                    return 0;
                case "DATETIME":
                    return new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
                default:
                    return "{}";
            }
        }

        public void Dispose()
        {
            SqliteConnection.ClearAllPools();
            try
            {
                Directory.Delete(Folder, true);
            }
            catch
            {
            }
        }
    }

    public sealed class TalearrRecordingEventAggregator : IEventAggregator
    {
        public List<object> Events { get; } = new List<object>();

        public void PublishEvent<TEvent>(TEvent @event)
            where TEvent : class, IEvent
        {
            Events.Add(@event);
        }
    }
}
