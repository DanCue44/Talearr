using System;
using System.Collections.Generic;
using System.Data;
using System.Linq;
using Dapper;
using Newtonsoft.Json;
using NLog;
using NzbDrone.Core.Talearr.Recordings;

namespace NzbDrone.Core.Talearr.MamFiller
{
    public interface ITalearrMamFillerStore
    {
        List<TalearrFillerTorrent> All();
        void Save(TalearrFillerTorrent torrent);

        // Null when talearr.db could not be read.
        TalearrMamFillerStatus GetStatus();
        void SaveStatus(TalearrMamFillerStatus status);

        // Talearr: preferred picks put into the library (JSON rows in TalearrFillerState).
        List<TalearrFillerPick> AllPicks();
        void SavePick(TalearrFillerPick pick);
    }

    // Talearr: filler bookkeeping in talearr.db (schema version 7). Best effort: a broken file
    // only means the filler forgets its own torrents (dedupe then falls back to the client's
    // torrent list and the slot reservations).
    public class TalearrMamFillerStore : ITalearrMamFillerStore
    {
        private const string StatusKey = "status";
        private const string PickPrefix = "pick:";

        private readonly ITalearrDatabase _database;
        private readonly Logger _logger;

        public TalearrMamFillerStore(ITalearrDatabase database, Logger logger)
        {
            _database = database;
            _logger = logger;
        }

        public List<TalearrFillerTorrent> All()
        {
            return Run("read filler torrents", new List<TalearrFillerTorrent>(), connection => connection
                .Query<Row>("SELECT * FROM TalearrFillerTorrents")
                .Select(r => new TalearrFillerTorrent
                {
                    TorrentId = r.TorrentId,
                    Title = r.Title,
                    Size = r.Size,
                    IsAudiobook = r.IsAudiobook != 0,
                    AddedAt = TalearrRecordingsStore.ParseDate(r.AddedAt),
                    ClientHash = r.ClientHash,
                    DownloadClient = r.DownloadClient,
                    Category = r.Category,
                    State = r.State,
                    UpdatedAt = string.IsNullOrWhiteSpace(r.UpdatedAt) ? null : TalearrRecordingsStore.ParseDate(r.UpdatedAt)
                })
                .ToList());
        }

        public void Save(TalearrFillerTorrent torrent)
        {
            if (torrent == null || string.IsNullOrWhiteSpace(torrent.TorrentId))
            {
                return;
            }

            Run("save a filler torrent", 0, connection => connection.Execute(@"
INSERT INTO TalearrFillerTorrents (TorrentId, Title, Size, IsAudiobook, AddedAt, ClientHash, DownloadClient, Category, State, UpdatedAt)
VALUES (@TorrentId, @Title, @Size, @IsAudiobook, @AddedAt, @ClientHash, @DownloadClient, @Category, @State, @UpdatedAt)
ON CONFLICT (TorrentId) DO UPDATE SET
    Title = excluded.Title, Size = excluded.Size, IsAudiobook = excluded.IsAudiobook,
    ClientHash = excluded.ClientHash, DownloadClient = excluded.DownloadClient,
    Category = excluded.Category, State = excluded.State, UpdatedAt = excluded.UpdatedAt;", new
            {
                torrent.TorrentId,
                torrent.Title,
                torrent.Size,
                IsAudiobook = torrent.IsAudiobook ? 1 : 0,
                AddedAt = TalearrRecordingsStore.FormatDate(torrent.AddedAt),
                ClientHash = torrent.ClientHash?.ToUpperInvariant(),
                torrent.DownloadClient,
                torrent.Category,
                torrent.State,
                UpdatedAt = torrent.UpdatedAt.HasValue ? TalearrRecordingsStore.FormatDate(torrent.UpdatedAt.Value) : null
            }));
        }

        public TalearrMamFillerStatus GetStatus()
        {
            var json = Run<string>("read the filler status", null, connection =>
                connection.ExecuteScalar<string>("SELECT Value FROM TalearrFillerState WHERE Key = @Key", new { Key = StatusKey }));

            if (string.IsNullOrWhiteSpace(json))
            {
                return null;
            }

            try
            {
                return JsonConvert.DeserializeObject<TalearrMamFillerStatus>(json);
            }
            catch (JsonException)
            {
                return null;
            }
        }

        public void SaveStatus(TalearrMamFillerStatus status)
        {
            if (status == null)
            {
                return;
            }

            var json = JsonConvert.SerializeObject(status);
            Run("save the filler status", 0, connection => connection.Execute(@"
INSERT INTO TalearrFillerState (Key, Value) VALUES (@Key, @Value)
ON CONFLICT (Key) DO UPDATE SET Value = excluded.Value;", new { Key = StatusKey, Value = json }));
        }

        public List<TalearrFillerPick> AllPicks()
        {
            var rows = Run("read filler picks", new List<string>(), connection => connection
                .Query<string>("SELECT Value FROM TalearrFillerState WHERE Key LIKE @Prefix", new { Prefix = PickPrefix + "%" })
                .ToList());

            var picks = new List<TalearrFillerPick>();
            foreach (var json in rows.Where(r => !string.IsNullOrWhiteSpace(r)))
            {
                try
                {
                    var pick = JsonConvert.DeserializeObject<TalearrFillerPick>(json);
                    if (!string.IsNullOrWhiteSpace(pick?.TorrentId))
                    {
                        picks.Add(pick);
                    }
                }
                catch (JsonException)
                {
                    // A damaged row only loses that pick's library state.
                }
            }

            return picks;
        }

        public void SavePick(TalearrFillerPick pick)
        {
            if (string.IsNullOrWhiteSpace(pick?.TorrentId))
            {
                return;
            }

            var json = JsonConvert.SerializeObject(pick);
            Run("save a filler pick", 0, connection => connection.Execute(@"
INSERT INTO TalearrFillerState (Key, Value) VALUES (@Key, @Value)
ON CONFLICT (Key) DO UPDATE SET Value = excluded.Value;", new { Key = PickPrefix + pick.TorrentId, Value = json }));
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
            public string TorrentId { get; set; }
            public string Title { get; set; }
            public long Size { get; set; }
            public long IsAudiobook { get; set; }
            public string AddedAt { get; set; }
            public string ClientHash { get; set; }
            public string DownloadClient { get; set; }
            public string Category { get; set; }
            public string State { get; set; }
            public string UpdatedAt { get; set; }
        }
    }
}
