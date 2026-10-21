using System;
using System.Collections.Generic;
using System.Data;
using System.Globalization;
using System.Linq;
using Dapper;

namespace NzbDrone.Core.Talearr.Comics
{
    public interface ITalearrComicsStore
    {
        List<ComicRootFolder> GetRootFolders();
        ComicRootFolder GetRootFolder(int id);
        ComicRootFolder AddRootFolder(ComicRootFolder folder);
        void UpdateRootFolder(ComicRootFolder folder);
        void DeleteRootFolder(int id);

        List<ComicSeries> GetAllSeries();
        ComicSeries GetSeries(int id);
        ComicSeries FindSeriesByAniList(int aniListId);
        ComicSeries FindSeriesByHardcover(int hardcoverSeriesId);
        ComicSeries AddSeries(ComicSeries series);
        void UpdateSeries(ComicSeries series);
        void DeleteSeries(int id);

        List<ComicVolume> GetVolumes(int seriesId);
        List<ComicVolume> GetAllVolumes();
        ComicVolume GetVolume(int id);

        // Adds volumes Talearr has not seen and refreshes the facts of the others. Monitored is
        // only set on new rows (newVolumesMonitored); existing choices are kept.
        List<ComicVolume> MergeVolumes(int seriesId, IEnumerable<ComicVolume> volumes, bool newVolumesMonitored);
        void SetVolumesMonitored(IEnumerable<int> volumeIds, bool monitored);

        List<ComicFile> GetFiles(int seriesId);
        List<ComicFile> GetAllFiles();
        ComicFile AddFile(ComicFile file);
        void UpdateFile(ComicFile file);
        void DeleteFile(int id);

        ComicDownload GetDownload(string downloadId);
        List<ComicDownload> GetDownloads(int? seriesId = null);
        List<string> GetTrackedDownloadIds(DateTime since);
        void SaveDownload(ComicDownload download);

        void AddHistory(ComicHistoryEntry entry);
        List<ComicHistoryEntry> GetHistory(int? seriesId, int limit);

        void AddBlocklist(ComicBlocklistEntry entry);
        List<ComicBlocklistEntry> GetBlocklist(int? seriesId = null);
        void DeleteBlocklist(int id);

        string GetSetting(string key);
        void SetSetting(string key, string value);
    }

    // Talearr: reads and writes talearr-comics.db. Unlike the talearr.db stores, failures are not
    // swallowed: this is the user's library, so a write that did not happen must say so.
    public class TalearrComicsStore : ITalearrComicsStore
    {
        private const string SeriesColumns = "Id, Kind, Title, SortTitle, AlternateTitles, Year, Status, Overview, Authors, Publisher, Genres, CoverUrl, HardcoverSeriesId, AniListId, TotalVolumes, AgeRating, AgeRatingOverride, IsAdult, ReadingDirection, LanguageIso, RootFolderId, Path, Monitored, MonitorNewVolumes, Added, LastRefreshed";
        private const string VolumeColumns = "Id, SeriesId, Number, Title, ReleaseDate, CoverUrl, PageCount, HardcoverBookId, Monitored";
        private const string FileColumns = "Id, SeriesId, VolumeId, Path, Size, Format, Source, ReleaseGroup, SourceTitle, Score, DateAdded";
        private const string DownloadColumns = "DownloadId, DownloadClientId, Protocol, SeriesId, VolumeIds, ReleaseTitle, Indexer, Score, Status, OutputPath, Message, GrabbedAt, UpdatedAt";

        private readonly ITalearrComicsDatabase _database;

        public TalearrComicsStore(ITalearrComicsDatabase database)
        {
            _database = database;
        }

        public static string FormatDate(DateTime? date)
        {
            return date?.ToUniversalTime().ToString("o", CultureInfo.InvariantCulture);
        }

        public static DateTime? ParseDate(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return null;
            }

            return DateTime.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var parsed)
                ? parsed
                : null;
        }

        // Root folders

        public List<ComicRootFolder> GetRootFolders()
        {
            return Run(c => c.Query<RootFolderRow>("SELECT Id, Path, Kind, IsKids, MaxAgeRating FROM ComicRootFolders ORDER BY Path").Select(ToModel).ToList());
        }

        public ComicRootFolder GetRootFolder(int id)
        {
            return Run(c => c.Query<RootFolderRow>("SELECT Id, Path, Kind, IsKids, MaxAgeRating FROM ComicRootFolders WHERE Id = @Id", new { Id = id }).Select(ToModel).FirstOrDefault());
        }

        public ComicRootFolder AddRootFolder(ComicRootFolder folder)
        {
            return Run(c =>
            {
                folder.Id = (int)c.ExecuteScalar<long>(
                    "INSERT INTO ComicRootFolders (Path, Kind, IsKids, MaxAgeRating) VALUES (@Path, @Kind, @IsKids, @MaxAgeRating); SELECT last_insert_rowid();",
                    new { folder.Path, folder.Kind, IsKids = folder.IsKids ? 1 : 0, folder.MaxAgeRating });
                return folder;
            });
        }

        public void UpdateRootFolder(ComicRootFolder folder)
        {
            Run(c => c.Execute("UPDATE ComicRootFolders SET Kind = @Kind, IsKids = @IsKids, MaxAgeRating = @MaxAgeRating WHERE Id = @Id",
                new { folder.Id, folder.Kind, IsKids = folder.IsKids ? 1 : 0, folder.MaxAgeRating }));
        }

        public void DeleteRootFolder(int id)
        {
            Run(c => c.Execute("DELETE FROM ComicRootFolders WHERE Id = @Id", new { Id = id }));
        }

        // Series

        public List<ComicSeries> GetAllSeries()
        {
            return Run(c => c.Query<SeriesRow>($"SELECT {SeriesColumns} FROM ComicSeries ORDER BY SortTitle").Select(ToModel).ToList());
        }

        public ComicSeries GetSeries(int id)
        {
            return Run(c => c.Query<SeriesRow>($"SELECT {SeriesColumns} FROM ComicSeries WHERE Id = @Id", new { Id = id }).Select(ToModel).FirstOrDefault());
        }

        public ComicSeries FindSeriesByAniList(int aniListId)
        {
            return Run(c => c.Query<SeriesRow>($"SELECT {SeriesColumns} FROM ComicSeries WHERE AniListId = @Id", new { Id = aniListId }).Select(ToModel).FirstOrDefault());
        }

        public ComicSeries FindSeriesByHardcover(int hardcoverSeriesId)
        {
            return Run(c => c.Query<SeriesRow>($"SELECT {SeriesColumns} FROM ComicSeries WHERE HardcoverSeriesId = @Id", new { Id = hardcoverSeriesId }).Select(ToModel).FirstOrDefault());
        }

        public ComicSeries AddSeries(ComicSeries series)
        {
            return Run(c =>
            {
                series.Id = (int)c.ExecuteScalar<long>(@"
INSERT INTO ComicSeries (Kind, Title, SortTitle, AlternateTitles, Year, Status, Overview, Authors, Publisher, Genres, CoverUrl, HardcoverSeriesId, AniListId, TotalVolumes, AgeRating, AgeRatingOverride, IsAdult, ReadingDirection, LanguageIso, RootFolderId, Path, Monitored, MonitorNewVolumes, Added, LastRefreshed)
VALUES (@Kind, @Title, @SortTitle, @AlternateTitles, @Year, @Status, @Overview, @Authors, @Publisher, @Genres, @CoverUrl, @HardcoverSeriesId, @AniListId, @TotalVolumes, @AgeRating, @AgeRatingOverride, @IsAdult, @ReadingDirection, @LanguageIso, @RootFolderId, @Path, @Monitored, @MonitorNewVolumes, @Added, @LastRefreshed);
SELECT last_insert_rowid();", ToRow(series));
                return series;
            });
        }

        public void UpdateSeries(ComicSeries series)
        {
            Run(c => c.Execute(@"
UPDATE ComicSeries SET Kind = @Kind, Title = @Title, SortTitle = @SortTitle, AlternateTitles = @AlternateTitles, Year = @Year, Status = @Status,
    Overview = @Overview, Authors = @Authors, Publisher = @Publisher, Genres = @Genres, CoverUrl = @CoverUrl, HardcoverSeriesId = @HardcoverSeriesId,
    AniListId = @AniListId, TotalVolumes = @TotalVolumes, AgeRating = @AgeRating, AgeRatingOverride = @AgeRatingOverride, IsAdult = @IsAdult,
    ReadingDirection = @ReadingDirection, LanguageIso = @LanguageIso, RootFolderId = @RootFolderId, Path = @Path, Monitored = @Monitored,
    MonitorNewVolumes = @MonitorNewVolumes, LastRefreshed = @LastRefreshed
WHERE Id = @Id", ToRow(series)));
        }

        public void DeleteSeries(int id)
        {
            Run(c =>
            {
                using var transaction = c.BeginTransaction();
                foreach (var table in new[] { "ComicVolumes", "ComicFiles", "ComicHistory", "ComicBlocklist", "ComicDownloads" })
                {
                    c.Execute($"DELETE FROM {table} WHERE SeriesId = @Id", new { Id = id }, transaction);
                }

                c.Execute("DELETE FROM ComicSeries WHERE Id = @Id", new { Id = id }, transaction);
                transaction.Commit();
                return 0;
            });
        }

        // Volumes

        public List<ComicVolume> GetVolumes(int seriesId)
        {
            return Run(c => c.Query<VolumeRow>($"SELECT {VolumeColumns} FROM ComicVolumes WHERE SeriesId = @SeriesId ORDER BY Number", new { SeriesId = seriesId }).Select(ToModel).ToList());
        }

        public List<ComicVolume> GetAllVolumes()
        {
            return Run(c => c.Query<VolumeRow>($"SELECT {VolumeColumns} FROM ComicVolumes ORDER BY SeriesId, Number").Select(ToModel).ToList());
        }

        public ComicVolume GetVolume(int id)
        {
            return Run(c => c.Query<VolumeRow>($"SELECT {VolumeColumns} FROM ComicVolumes WHERE Id = @Id", new { Id = id }).Select(ToModel).FirstOrDefault());
        }

        public List<ComicVolume> MergeVolumes(int seriesId, IEnumerable<ComicVolume> volumes, bool newVolumesMonitored)
        {
            return Run(c =>
            {
                using var transaction = c.BeginTransaction();
                var existing = c.Query<VolumeRow>($"SELECT {VolumeColumns} FROM ComicVolumes WHERE SeriesId = @SeriesId", new { SeriesId = seriesId }, transaction)
                    .Select(ToModel)
                    .ToDictionary(v => v.Number);
                var added = new List<ComicVolume>();

                foreach (var volume in volumes.GroupBy(v => v.Number).Select(g => g.First()))
                {
                    if (existing.TryGetValue(volume.Number, out var current))
                    {
                        c.Execute(@"UPDATE ComicVolumes SET Title = COALESCE(@Title, Title), ReleaseDate = COALESCE(@ReleaseDate, ReleaseDate), CoverUrl = COALESCE(@CoverUrl, CoverUrl),
    PageCount = COALESCE(@PageCount, PageCount), HardcoverBookId = COALESCE(@HardcoverBookId, HardcoverBookId) WHERE Id = @Id",
                            new { current.Id, volume.Title, ReleaseDate = FormatDate(volume.ReleaseDate), volume.CoverUrl, volume.PageCount, volume.HardcoverBookId }, transaction);
                        continue;
                    }

                    volume.SeriesId = seriesId;
                    volume.Monitored = newVolumesMonitored;
                    volume.Id = (int)c.ExecuteScalar<long>(@"
INSERT INTO ComicVolumes (SeriesId, Number, Title, ReleaseDate, CoverUrl, PageCount, HardcoverBookId, Monitored)
VALUES (@SeriesId, @Number, @Title, @ReleaseDate, @CoverUrl, @PageCount, @HardcoverBookId, @Monitored); SELECT last_insert_rowid();",
                        new { SeriesId = seriesId, Number = (double)volume.Number, volume.Title, ReleaseDate = FormatDate(volume.ReleaseDate), volume.CoverUrl, volume.PageCount, volume.HardcoverBookId, Monitored = volume.Monitored ? 1 : 0 },
                        transaction);
                    added.Add(volume);
                }

                transaction.Commit();
                return added;
            });
        }

        public void SetVolumesMonitored(IEnumerable<int> volumeIds, bool monitored)
        {
            var ids = volumeIds?.Distinct().ToList() ?? new List<int>();
            if (ids.Count == 0)
            {
                return;
            }

            Run(c => c.Execute("UPDATE ComicVolumes SET Monitored = @Monitored WHERE Id IN @Ids", new { Monitored = monitored ? 1 : 0, Ids = ids }));
        }

        // Files

        public List<ComicFile> GetFiles(int seriesId)
        {
            return Run(c => c.Query<FileRow>($"SELECT {FileColumns} FROM ComicFiles WHERE SeriesId = @SeriesId", new { SeriesId = seriesId }).Select(ToModel).ToList());
        }

        public List<ComicFile> GetAllFiles()
        {
            return Run(c => c.Query<FileRow>($"SELECT {FileColumns} FROM ComicFiles").Select(ToModel).ToList());
        }

        public ComicFile AddFile(ComicFile file)
        {
            return Run(c =>
            {
                file.Id = (int)c.ExecuteScalar<long>(@"
INSERT INTO ComicFiles (SeriesId, VolumeId, Path, Size, Format, Source, ReleaseGroup, SourceTitle, Score, DateAdded)
VALUES (@SeriesId, @VolumeId, @Path, @Size, @Format, @Source, @ReleaseGroup, @SourceTitle, @Score, @DateAdded); SELECT last_insert_rowid();",
                    new { file.SeriesId, file.VolumeId, file.Path, file.Size, file.Format, file.Source, file.ReleaseGroup, file.SourceTitle, file.Score, DateAdded = FormatDate(file.DateAdded == default ? DateTime.UtcNow : file.DateAdded) });
                return file;
            });
        }

        public void UpdateFile(ComicFile file)
        {
            Run(c => c.Execute("UPDATE ComicFiles SET Path = @Path, Size = @Size, Format = @Format WHERE Id = @Id", new { file.Id, file.Path, file.Size, file.Format }));
        }

        public void DeleteFile(int id)
        {
            Run(c => c.Execute("DELETE FROM ComicFiles WHERE Id = @Id", new { Id = id }));
        }

        // Downloads

        public ComicDownload GetDownload(string downloadId)
        {
            return Run(c => c.Query<DownloadRow>($"SELECT {DownloadColumns} FROM ComicDownloads WHERE DownloadId = @DownloadId", new { DownloadId = downloadId }).Select(ToModel).FirstOrDefault());
        }

        public List<ComicDownload> GetDownloads(int? seriesId = null)
        {
            return Run(c => c.Query<DownloadRow>(
                $"SELECT {DownloadColumns} FROM ComicDownloads {(seriesId.HasValue ? "WHERE SeriesId = @SeriesId" : string.Empty)} ORDER BY GrabbedAt DESC",
                new { SeriesId = seriesId }).Select(ToModel).ToList());
        }

        public List<string> GetTrackedDownloadIds(DateTime since)
        {
            return Run(c => c.Query<string>("SELECT DownloadId FROM ComicDownloads WHERE GrabbedAt >= @Since OR Status IN ('grabbed', 'completed', 'importing')",
                new { Since = FormatDate(since) }).ToList());
        }

        public void SaveDownload(ComicDownload download)
        {
            download.UpdatedAt = DateTime.UtcNow;
            Run(c => c.Execute(@"
INSERT INTO ComicDownloads (DownloadId, DownloadClientId, Protocol, SeriesId, VolumeIds, ReleaseTitle, Indexer, Score, Status, OutputPath, Message, GrabbedAt, UpdatedAt)
VALUES (@DownloadId, @DownloadClientId, @Protocol, @SeriesId, @VolumeIds, @ReleaseTitle, @Indexer, @Score, @Status, @OutputPath, @Message, @GrabbedAt, @UpdatedAt)
ON CONFLICT(DownloadId) DO UPDATE SET Status = excluded.Status, OutputPath = excluded.OutputPath, Message = excluded.Message, UpdatedAt = excluded.UpdatedAt",
                new
                {
                    download.DownloadId,
                    download.DownloadClientId,
                    download.Protocol,
                    download.SeriesId,
                    download.VolumeIds,
                    download.ReleaseTitle,
                    download.Indexer,
                    download.Score,
                    download.Status,
                    download.OutputPath,
                    download.Message,
                    GrabbedAt = FormatDate(download.GrabbedAt == default ? DateTime.UtcNow : download.GrabbedAt),
                    UpdatedAt = FormatDate(download.UpdatedAt)
                }));
        }

        // History

        public void AddHistory(ComicHistoryEntry entry)
        {
            Run(c => c.Execute("INSERT INTO ComicHistory (SeriesId, VolumeId, EventType, SourceTitle, Message, Date) VALUES (@SeriesId, @VolumeId, @EventType, @SourceTitle, @Message, @Date)",
                new { entry.SeriesId, entry.VolumeId, entry.EventType, entry.SourceTitle, entry.Message, Date = FormatDate(entry.Date == default ? DateTime.UtcNow : entry.Date) }));
        }

        public List<ComicHistoryEntry> GetHistory(int? seriesId, int limit)
        {
            return Run(c => c.Query<HistoryRow>(
                $"SELECT Id, SeriesId, VolumeId, EventType, SourceTitle, Message, Date FROM ComicHistory {(seriesId.HasValue ? "WHERE SeriesId = @SeriesId" : string.Empty)} ORDER BY Date DESC, Id DESC LIMIT @Limit",
                new { SeriesId = seriesId, Limit = limit })
                .Select(r => new ComicHistoryEntry
                {
                    Id = (int)r.Id,
                    SeriesId = (int)r.SeriesId,
                    VolumeId = (int?)r.VolumeId,
                    EventType = r.EventType,
                    SourceTitle = r.SourceTitle,
                    Message = r.Message,
                    Date = ParseDate(r.Date) ?? DateTime.MinValue
                }).ToList());
        }

        // Blocklist

        public void AddBlocklist(ComicBlocklistEntry entry)
        {
            Run(c => c.Execute("INSERT INTO ComicBlocklist (SeriesId, SourceTitle, Indexer, Reason, Date) VALUES (@SeriesId, @SourceTitle, @Indexer, @Reason, @Date)",
                new { entry.SeriesId, entry.SourceTitle, entry.Indexer, entry.Reason, Date = FormatDate(entry.Date == default ? DateTime.UtcNow : entry.Date) }));
        }

        public List<ComicBlocklistEntry> GetBlocklist(int? seriesId = null)
        {
            return Run(c => c.Query<BlocklistRow>(
                $"SELECT Id, SeriesId, SourceTitle, Indexer, Reason, Date FROM ComicBlocklist {(seriesId.HasValue ? "WHERE SeriesId = @SeriesId" : string.Empty)} ORDER BY Date DESC",
                new { SeriesId = seriesId })
                .Select(r => new ComicBlocklistEntry
                {
                    Id = (int)r.Id,
                    SeriesId = (int)r.SeriesId,
                    SourceTitle = r.SourceTitle,
                    Indexer = r.Indexer,
                    Reason = r.Reason,
                    Date = ParseDate(r.Date) ?? DateTime.MinValue
                }).ToList());
        }

        public void DeleteBlocklist(int id)
        {
            Run(c => c.Execute("DELETE FROM ComicBlocklist WHERE Id = @Id", new { Id = id }));
        }

        // Settings

        public string GetSetting(string key)
        {
            return Run(c => c.ExecuteScalar<string>("SELECT Value FROM ComicSettings WHERE Key = @Key", new { Key = key }));
        }

        public void SetSetting(string key, string value)
        {
            Run(c => c.Execute("INSERT INTO ComicSettings (Key, Value) VALUES (@Key, @Value) ON CONFLICT(Key) DO UPDATE SET Value = excluded.Value", new { Key = key, Value = value }));
        }

        private T Run<T>(Func<IDbConnection, T> action)
        {
            using var connection = _database.OpenConnection();
            return action(connection);
        }

        private static ComicRootFolder ToModel(RootFolderRow r)
        {
            return new ComicRootFolder { Id = (int)r.Id, Path = r.Path, Kind = r.Kind, IsKids = r.IsKids != 0, MaxAgeRating = r.MaxAgeRating };
        }

        private static ComicSeries ToModel(SeriesRow r)
        {
            return new ComicSeries
            {
                Id = (int)r.Id,
                Kind = r.Kind,
                Title = r.Title,
                SortTitle = r.SortTitle,
                AlternateTitles = r.AlternateTitles,
                Year = (int?)r.Year,
                Status = r.Status,
                Overview = r.Overview,
                Authors = r.Authors,
                Publisher = r.Publisher,
                Genres = r.Genres,
                CoverUrl = r.CoverUrl,
                HardcoverSeriesId = (int?)r.HardcoverSeriesId,
                AniListId = (int?)r.AniListId,
                TotalVolumes = (int?)r.TotalVolumes,
                AgeRating = r.AgeRating,
                AgeRatingOverride = r.AgeRatingOverride,
                IsAdult = r.IsAdult != 0,
                ReadingDirection = r.ReadingDirection,
                LanguageIso = r.LanguageIso,
                RootFolderId = (int)r.RootFolderId,
                Path = r.Path,
                Monitored = r.Monitored != 0,
                MonitorNewVolumes = r.MonitorNewVolumes != 0,
                Added = ParseDate(r.Added) ?? DateTime.UtcNow,
                LastRefreshed = ParseDate(r.LastRefreshed)
            };
        }

        private static object ToRow(ComicSeries s)
        {
            return new
            {
                s.Id,
                s.Kind,
                s.Title,
                SortTitle = s.SortTitle ?? s.Title,
                s.AlternateTitles,
                s.Year,
                s.Status,
                s.Overview,
                s.Authors,
                s.Publisher,
                s.Genres,
                s.CoverUrl,
                s.HardcoverSeriesId,
                s.AniListId,
                s.TotalVolumes,
                s.AgeRating,
                s.AgeRatingOverride,
                IsAdult = s.IsAdult ? 1 : 0,
                s.ReadingDirection,
                s.LanguageIso,
                s.RootFolderId,
                s.Path,
                Monitored = s.Monitored ? 1 : 0,
                MonitorNewVolumes = s.MonitorNewVolumes ? 1 : 0,
                Added = FormatDate(s.Added == default ? DateTime.UtcNow : s.Added),
                LastRefreshed = FormatDate(s.LastRefreshed)
            };
        }

        private static ComicVolume ToModel(VolumeRow r)
        {
            return new ComicVolume
            {
                Id = (int)r.Id,
                SeriesId = (int)r.SeriesId,
                Number = (decimal)r.Number,
                Title = r.Title,
                ReleaseDate = ParseDate(r.ReleaseDate),
                CoverUrl = r.CoverUrl,
                PageCount = (int?)r.PageCount,
                HardcoverBookId = (int?)r.HardcoverBookId,
                Monitored = r.Monitored != 0
            };
        }

        private static ComicFile ToModel(FileRow r)
        {
            return new ComicFile
            {
                Id = (int)r.Id,
                SeriesId = (int)r.SeriesId,
                VolumeId = (int)r.VolumeId,
                Path = r.Path,
                Size = r.Size,
                Format = r.Format,
                Source = r.Source,
                ReleaseGroup = r.ReleaseGroup,
                SourceTitle = r.SourceTitle,
                Score = (int)r.Score,
                DateAdded = ParseDate(r.DateAdded) ?? DateTime.UtcNow
            };
        }

        private static ComicDownload ToModel(DownloadRow r)
        {
            return new ComicDownload
            {
                DownloadId = r.DownloadId,
                DownloadClientId = (int)r.DownloadClientId,
                Protocol = r.Protocol,
                SeriesId = (int)r.SeriesId,
                VolumeIds = r.VolumeIds,
                ReleaseTitle = r.ReleaseTitle,
                Indexer = r.Indexer,
                Score = (int)r.Score,
                Status = r.Status,
                OutputPath = r.OutputPath,
                Message = r.Message,
                GrabbedAt = ParseDate(r.GrabbedAt) ?? DateTime.UtcNow,
                UpdatedAt = ParseDate(r.UpdatedAt) ?? DateTime.UtcNow
            };
        }

        private class RootFolderRow
        {
            public long Id { get; set; }
            public string Path { get; set; }
            public string Kind { get; set; }
            public long IsKids { get; set; }
            public string MaxAgeRating { get; set; }
        }

        private class SeriesRow
        {
            public long Id { get; set; }
            public string Kind { get; set; }
            public string Title { get; set; }
            public string SortTitle { get; set; }
            public string AlternateTitles { get; set; }
            public long? Year { get; set; }
            public string Status { get; set; }
            public string Overview { get; set; }
            public string Authors { get; set; }
            public string Publisher { get; set; }
            public string Genres { get; set; }
            public string CoverUrl { get; set; }
            public long? HardcoverSeriesId { get; set; }
            public long? AniListId { get; set; }
            public long? TotalVolumes { get; set; }
            public string AgeRating { get; set; }
            public string AgeRatingOverride { get; set; }
            public long IsAdult { get; set; }
            public string ReadingDirection { get; set; }
            public string LanguageIso { get; set; }
            public long RootFolderId { get; set; }
            public string Path { get; set; }
            public long Monitored { get; set; }
            public long MonitorNewVolumes { get; set; }
            public string Added { get; set; }
            public string LastRefreshed { get; set; }
        }

        private class VolumeRow
        {
            public long Id { get; set; }
            public long SeriesId { get; set; }
            public double Number { get; set; }
            public string Title { get; set; }
            public string ReleaseDate { get; set; }
            public string CoverUrl { get; set; }
            public long? PageCount { get; set; }
            public long? HardcoverBookId { get; set; }
            public long Monitored { get; set; }
        }

        private class FileRow
        {
            public long Id { get; set; }
            public long SeriesId { get; set; }
            public long VolumeId { get; set; }
            public string Path { get; set; }
            public long Size { get; set; }
            public string Format { get; set; }
            public string Source { get; set; }
            public string ReleaseGroup { get; set; }
            public string SourceTitle { get; set; }
            public long Score { get; set; }
            public string DateAdded { get; set; }
        }

        private class DownloadRow
        {
            public string DownloadId { get; set; }
            public long DownloadClientId { get; set; }
            public string Protocol { get; set; }
            public long SeriesId { get; set; }
            public string VolumeIds { get; set; }
            public string ReleaseTitle { get; set; }
            public string Indexer { get; set; }
            public long Score { get; set; }
            public string Status { get; set; }
            public string OutputPath { get; set; }
            public string Message { get; set; }
            public string GrabbedAt { get; set; }
            public string UpdatedAt { get; set; }
        }

        private class HistoryRow
        {
            public long Id { get; set; }
            public long SeriesId { get; set; }
            public long? VolumeId { get; set; }
            public string EventType { get; set; }
            public string SourceTitle { get; set; }
            public string Message { get; set; }
            public string Date { get; set; }
        }

        private class BlocklistRow
        {
            public long Id { get; set; }
            public long SeriesId { get; set; }
            public string SourceTitle { get; set; }
            public string Indexer { get; set; }
            public string Reason { get; set; }
            public string Date { get; set; }
        }
    }
}
