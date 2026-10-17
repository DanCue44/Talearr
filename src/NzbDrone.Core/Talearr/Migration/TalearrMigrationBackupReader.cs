using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using Microsoft.Data.Sqlite;
using Newtonsoft.Json.Linq;

namespace NzbDrone.Core.Talearr.Migration
{
    public interface ITalearrMigrationBackupReader
    {
        TalearrMigrationSnapshot Read(string path, string source);
    }

    // TaleArr: reads a readarr.db (Readarr or Bookshelf) or a Readarr backup zip. The file is
    // copied to a temp folder first and only the copy is opened, read-only, so the source (which
    // may belong to a running Readarr) is never touched or locked.
    public class TalearrMigrationBackupReader : ITalearrMigrationBackupReader
    {
        public TalearrMigrationSnapshot Read(string path, string source)
        {
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            {
                throw new TalearrMigrationException($"The file '{path}' does not exist on this server.");
            }

            var temp = Path.Combine(Path.GetTempPath(), "talearr-migration-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(temp);
            try
            {
                var db = Path.Combine(temp, "source.db");
                if (path.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
                {
                    using var zip = ZipFile.OpenRead(path);
                    var entry = zip.Entries.FirstOrDefault(e => e.Name.EndsWith(".db", StringComparison.OrdinalIgnoreCase) && !e.Name.StartsWith("logs", StringComparison.OrdinalIgnoreCase));
                    if (entry == null)
                    {
                        throw new TalearrMigrationException("The zip has no database in it. Use a backup from System > Backup.");
                    }

                    entry.ExtractToFile(db);
                }
                else
                {
                    File.Copy(path, db);
                    foreach (var suffix in new[] { "-wal", "-shm" })
                    {
                        if (File.Exists(path + suffix))
                        {
                            File.Copy(path + suffix, db + suffix);
                        }
                    }
                }

                return ReadDatabase(db, source);
            }
            finally
            {
                SqliteConnection.ClearAllPools();
                try
                {
                    Directory.Delete(temp, true);
                }
                catch (IOException)
                {
                }
                catch (UnauthorizedAccessException)
                {
                }
            }
        }

        public static TalearrMigrationSnapshot ReadDatabase(string databasePath, string source)
        {
            var builder = new SqliteConnectionStringBuilder { DataSource = databasePath, Mode = SqliteOpenMode.ReadOnly, Pooling = false };
            using var connection = new SqliteConnection(builder.ToString());
            try
            {
                connection.Open();
            }
            catch (SqliteException ex)
            {
                throw new TalearrMigrationException("Not a readable SQLite database: " + ex.Message, ex);
            }

            var tables = new HashSet<string>(Query(connection, "SELECT name FROM sqlite_master WHERE type = 'table'").Select(r => Text(r, "name")), StringComparer.OrdinalIgnoreCase);
            if (!tables.Contains("Authors") || !tables.Contains("Books") || !tables.Contains("AuthorMetadata"))
            {
                throw new TalearrMigrationException("This does not look like a Readarr database (no Authors, AuthorMetadata and Books tables).");
            }

            var snapshot = new TalearrMigrationSnapshot { Source = source ?? TalearrMigrationSources.Readarr, AppName = source == TalearrMigrationSources.Bookshelf ? "Bookshelf" : "Readarr" };
            if (tables.Contains("VersionInfo"))
            {
                snapshot.Version = "schema " + Query(connection, "SELECT MAX(Version) AS v FROM VersionInfo").Select(r => Text(r, "v")).FirstOrDefault();
            }

            List<Dictionary<string, object>> All(string table) => tables.Contains(table) ? Query(connection, $"SELECT * FROM \"{table}\"") : new List<Dictionary<string, object>>();

            foreach (var row in All("RootFolders"))
            {
                snapshot.RootFolders.Add(new TalearrSourceRootFolder
                {
                    Path = Text(row, "Path"),
                    Name = Text(row, "Name"),
                    QualityProfileId = Number(row, "DefaultQualityProfileId"),
                    MetadataProfileId = Number(row, "DefaultMetadataProfileId"),
                    NewItemMonitor = NewItemMonitor(Number(row, "DefaultNewItemMonitorOption")),
                    Tags = IdList(Text(row, "DefaultTags"))
                });
            }

            snapshot.QualityProfiles = All("QualityProfiles").Select(r => new TalearrSourceProfile { Id = Number(r, "Id") ?? 0, Name = Text(r, "Name") }).ToList();
            snapshot.MetadataProfiles = All("MetadataProfiles").Select(r => new TalearrSourceProfile { Id = Number(r, "Id") ?? 0, Name = Text(r, "Name") }).ToList();
            snapshot.Tags = All("Tags").Select(r => new TalearrSourceTag { Id = Number(r, "Id") ?? 0, Label = Text(r, "Label") }).ToList();
            snapshot.DownloadClients = All("DownloadClients").Select(DbProvider).ToList();
            snapshot.Indexers = All("Indexers").Select(DbProvider).ToList();

            var metadata = All("AuthorMetadata").ToDictionary(r => Number(r, "Id") ?? 0);
            var authorByMetadata = new Dictionary<int, int>();
            foreach (var row in All("Authors"))
            {
                var metadataId = Number(row, "AuthorMetadataId") ?? 0;
                metadata.TryGetValue(metadataId, out var meta);
                var author = new TalearrSourceAuthor
                {
                    Id = Number(row, "Id") ?? 0,
                    Name = Text(meta, "Name") ?? Text(row, "CleanName"),
                    ForeignAuthorId = Text(meta, "ForeignAuthorId"),
                    Monitored = Flag(row, "Monitored"),
                    MonitorNewItems = NewItemMonitor(Number(row, "MonitorNewItems")),
                    Path = Text(row, "Path"),
                    QualityProfileId = Number(row, "QualityProfileId") ?? 0,
                    MetadataProfileId = Number(row, "MetadataProfileId") ?? 0,
                    Tags = IdList(Text(row, "Tags"))
                };

                authorByMetadata[metadataId] = author.Id;
                snapshot.Authors.Add(author);
            }

            var editions = All("Editions")
                .GroupBy(r => Number(r, "BookId") ?? 0)
                .ToDictionary(g => g.Key, g => g.OrderByDescending(r => Flag(r, "Monitored")).First());
            var editionsWithFiles = new HashSet<int>(All("BookFiles").Select(r => Number(r, "EditionId") ?? 0));
            var booksWithFiles = new HashSet<int>(All("Editions").Where(r => editionsWithFiles.Contains(Number(r, "Id") ?? -1)).Select(r => Number(r, "BookId") ?? 0));

            foreach (var row in All("Books"))
            {
                var id = Number(row, "Id") ?? 0;
                if (!authorByMetadata.TryGetValue(Number(row, "AuthorMetadataId") ?? 0, out var authorId))
                {
                    continue;
                }

                editions.TryGetValue(id, out var edition);
                snapshot.Books.Add(new TalearrSourceBook
                {
                    Id = id,
                    AuthorId = authorId,
                    Title = Text(row, "Title"),
                    ForeignBookId = Text(row, "ForeignBookId"),
                    ForeignEditionId = Text(edition, "ForeignEditionId"),
                    Isbn13 = Text(edition, "Isbn13"),
                    Asin = Text(edition, "Asin"),
                    Monitored = Flag(row, "Monitored"),
                    HasFile = booksWithFiles.Contains(id)
                });
            }

            return snapshot;
        }

        private static TalearrSourceProvider DbProvider(Dictionary<string, object> row)
        {
            var rss = Flag(row, "EnableRss");
            var automatic = Flag(row, "EnableAutomaticSearch");
            var interactive = Flag(row, "EnableInteractiveSearch");
            return new TalearrSourceProvider
            {
                Name = Text(row, "Name"),
                Implementation = Text(row, "Implementation"),
                ConfigContract = Text(row, "ConfigContract"),
                Settings = Text(row, "Settings") ?? "{}",
                Enable = row.ContainsKey("Enable") ? Flag(row, "Enable") : rss || automatic || interactive,
                EnableRss = rss,
                EnableAutomaticSearch = automatic,
                EnableInteractiveSearch = interactive,
                Priority = Number(row, "Priority") ?? 0,
                Tags = IdList(Text(row, "Tags"))
            };
        }

        // Readarr's NewItemMonitorTypes: 0 all, 1 none, 2 new (the same order as Chaptarr's).
        internal static string NewItemMonitor(int? value)
        {
            return value switch
            {
                0 => "all",
                1 => "none",
                2 => "new",
                _ => null
            };
        }

        private static List<Dictionary<string, object>> Query(SqliteConnection connection, string sql)
        {
            var rows = new List<Dictionary<string, object>>();
            using var command = connection.CreateCommand();
            command.CommandText = sql;
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                var row = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
                for (var i = 0; i < reader.FieldCount; i++)
                {
                    row[reader.GetName(i)] = reader.IsDBNull(i) ? null : reader.GetValue(i);
                }

                rows.Add(row);
            }

            return rows;
        }

        private static string Text(Dictionary<string, object> row, string column)
        {
            if (row == null || !row.TryGetValue(column, out var value) || value == null)
            {
                return null;
            }

            var text = Convert.ToString(value, CultureInfo.InvariantCulture);
            return string.IsNullOrWhiteSpace(text) ? null : text.Trim();
        }

        private static int? Number(Dictionary<string, object> row, string column)
        {
            var text = Text(row, column);
            return int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) ? value : null;
        }

        private static bool Flag(Dictionary<string, object> row, string column)
        {
            var text = Text(row, column);
            return text == "1" || string.Equals(text, "true", StringComparison.OrdinalIgnoreCase);
        }

        private static List<int> IdList(string json)
        {
            if (string.IsNullOrWhiteSpace(json))
            {
                return new List<int>();
            }

            try
            {
                return TalearrMigrationApiReader.Ints(JToken.Parse(json));
            }
            catch (Newtonsoft.Json.JsonException)
            {
                return new List<int>();
            }
        }
    }
}
