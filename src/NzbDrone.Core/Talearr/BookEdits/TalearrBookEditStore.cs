using System;
using System.Collections.Generic;
using System.Data;
using System.Linq;
using System.Text.Json;
using Dapper;
using NLog;
using NzbDrone.Core.Talearr.Recordings;

namespace NzbDrone.Core.Talearr.BookEdits
{
    public interface ITalearrBookEditStore
    {
        // Every edit. Empty when talearr.db is unusable.
        List<TalearrBookEdit> GetAll();
        TalearrBookEdit Get(string workKey);

        // False when the write did not reach talearr.db.
        bool Upsert(TalearrBookEdit edit);
        bool Delete(string workKey);

        List<TalearrNarratorEdit> GetNarratorEdits();
        bool UpsertNarratorEdit(TalearrNarratorEdit edit);
        bool DeleteNarratorEdit(string editionKey);

        List<TalearrUserSeries> GetUserSeries();
        bool AddUserSeries(TalearrUserSeries series);
        bool DeleteUserSeries(int seriesId);
    }

    // Talearr: reads and writes the book detail edit tables in talearr.db. Like the other
    // Talearr stores every call is best effort: a failure is logged and treated as "no edit", so
    // a missing or broken talearr.db only means books show the provider's details again.
    public class TalearrBookEditStore : ITalearrBookEditStore
    {
        private static readonly JsonSerializerOptions JsonOptions = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase
        };

        private readonly ITalearrDatabase _database;
        private readonly Logger _logger;

        public TalearrBookEditStore(ITalearrDatabase database, Logger logger)
        {
            _database = database;
            _logger = logger;
        }

        public List<TalearrBookEdit> GetAll()
        {
            return Run("read book detail edits", new List<TalearrBookEdit>(), connection =>
                connection.Query<EditRow>("SELECT * FROM BookDetailEdits")
                    .Select(ToModel)
                    .Where(e => e != null)
                    .ToList());
        }

        public TalearrBookEdit Get(string workKey)
        {
            if (string.IsNullOrWhiteSpace(workKey))
            {
                return null;
            }

            return Run("read a book detail edit", (TalearrBookEdit)null, connection =>
                ToModel(connection.QueryFirstOrDefault<EditRow>("SELECT * FROM BookDetailEdits WHERE WorkKey = @WorkKey", new { WorkKey = workKey.Trim() })));
        }

        public bool Upsert(TalearrBookEdit edit)
        {
            if (edit == null || string.IsNullOrWhiteSpace(edit.WorkKey))
            {
                return false;
            }

            return Run("write a book detail edit", false, connection => connection.Execute(@"
INSERT INTO BookDetailEdits (WorkKey, Title, Series, Provider, BookIdHint, UpdatedAt)
VALUES (@WorkKey, @Title, @Series, @Provider, @BookIdHint, @UpdatedAt)
ON CONFLICT (WorkKey) DO UPDATE SET
    Title = excluded.Title, Series = excluded.Series, Provider = excluded.Provider,
    BookIdHint = excluded.BookIdHint, UpdatedAt = excluded.UpdatedAt;", new
            {
                WorkKey = edit.WorkKey.Trim(),
                edit.Title,
                Series = edit.Series == null ? null : JsonSerializer.Serialize(edit.Series.Select(s => new { s.Title, s.Position }), JsonOptions),
                Provider = JsonSerializer.Serialize(edit.Provider ?? new Dictionary<int, TalearrProviderBookValues>(), JsonOptions),
                edit.BookIdHint,
                UpdatedAt = TalearrRecordingsStore.FormatDate(edit.UpdatedAt == default ? DateTime.UtcNow : edit.UpdatedAt)
            }) > 0);
        }

        public bool Delete(string workKey)
        {
            if (string.IsNullOrWhiteSpace(workKey))
            {
                return false;
            }

            return Run("clear a book detail edit", false, connection =>
            {
                connection.Execute("DELETE FROM BookDetailEdits WHERE WorkKey = @WorkKey", new { WorkKey = workKey.Trim() });
                return true;
            });
        }

        public List<TalearrNarratorEdit> GetNarratorEdits()
        {
            return Run("read narrator edits", new List<TalearrNarratorEdit>(), connection =>
                connection.Query<NarratorRow>("SELECT * FROM NarratorEdits")
                    .Select(ToModel)
                    .Where(e => e != null)
                    .ToList());
        }

        public bool UpsertNarratorEdit(TalearrNarratorEdit edit)
        {
            if (edit == null || string.IsNullOrWhiteSpace(edit.EditionKey) || string.IsNullOrWhiteSpace(edit.WorkKey))
            {
                return false;
            }

            return Run("write a narrator edit", false, connection => connection.Execute(@"
INSERT INTO NarratorEdits (EditionKey, WorkKey, Narrators, Provider, BookIdHint, UpdatedAt)
VALUES (@EditionKey, @WorkKey, @Narrators, @Provider, @BookIdHint, @UpdatedAt)
ON CONFLICT (EditionKey) DO UPDATE SET
    WorkKey = excluded.WorkKey, Narrators = excluded.Narrators, Provider = excluded.Provider,
    BookIdHint = excluded.BookIdHint, UpdatedAt = excluded.UpdatedAt;", new
            {
                EditionKey = edit.EditionKey.Trim(),
                WorkKey = edit.WorkKey.Trim(),
                Narrators = JsonSerializer.Serialize(edit.Narrators ?? new List<string>(), JsonOptions),
                Provider = JsonSerializer.Serialize(new NarratorProvider
                {
                    Captured = edit.ProviderCaptured,
                    Narrator = edit.ProviderNarrator,
                    Names = edit.ProviderNarratorNames ?? new List<string>()
                }, JsonOptions),
                edit.BookIdHint,
                UpdatedAt = TalearrRecordingsStore.FormatDate(edit.UpdatedAt == default ? DateTime.UtcNow : edit.UpdatedAt)
            }) > 0);
        }

        public bool DeleteNarratorEdit(string editionKey)
        {
            if (string.IsNullOrWhiteSpace(editionKey))
            {
                return false;
            }

            return Run("clear a narrator edit", false, connection =>
            {
                connection.Execute("DELETE FROM NarratorEdits WHERE EditionKey = @EditionKey", new { EditionKey = editionKey.Trim() });
                return true;
            });
        }

        public List<TalearrUserSeries> GetUserSeries()
        {
            return Run("read Talearr series", new List<TalearrUserSeries>(), connection =>
                connection.Query<UserSeriesRow>("SELECT * FROM UserSeries")
                    .Select(r => new TalearrUserSeries
                    {
                        SeriesId = (int)r.SeriesId,
                        AuthorId = (int)r.AuthorId,
                        MediaType = (int)r.MediaType,
                        Title = r.Title,
                        CreatedAt = TalearrRecordingsStore.ParseDate(r.CreatedAt)
                    })
                    .ToList());
        }

        public bool AddUserSeries(TalearrUserSeries series)
        {
            if (series == null || series.SeriesId <= 0)
            {
                return false;
            }

            return Run("write a Talearr series", false, connection => connection.Execute(@"
INSERT INTO UserSeries (SeriesId, AuthorId, MediaType, Title, CreatedAt)
VALUES (@SeriesId, @AuthorId, @MediaType, @Title, @CreatedAt)
ON CONFLICT (SeriesId) DO UPDATE SET
    AuthorId = excluded.AuthorId, MediaType = excluded.MediaType, Title = excluded.Title;", new
            {
                series.SeriesId,
                series.AuthorId,
                series.MediaType,
                Title = series.Title ?? string.Empty,
                CreatedAt = TalearrRecordingsStore.FormatDate(series.CreatedAt == default ? DateTime.UtcNow : series.CreatedAt)
            }) > 0);
        }

        public bool DeleteUserSeries(int seriesId)
        {
            return Run("remove a Talearr series", false, connection =>
            {
                connection.Execute("DELETE FROM UserSeries WHERE SeriesId = @SeriesId", new { SeriesId = seriesId });
                return true;
            });
        }

        private TalearrBookEdit ToModel(EditRow row)
        {
            if (row == null || string.IsNullOrWhiteSpace(row.WorkKey))
            {
                return null;
            }

            return new TalearrBookEdit
            {
                WorkKey = row.WorkKey,
                Title = row.Title,
                Series = string.IsNullOrWhiteSpace(row.Series) ? null : Deserialize<List<TalearrSeriesEntry>>(row.Series) ?? new List<TalearrSeriesEntry>(),
                Provider = Deserialize<Dictionary<int, TalearrProviderBookValues>>(row.Provider) ?? new Dictionary<int, TalearrProviderBookValues>(),
                BookIdHint = row.BookIdHint.HasValue ? (int?)row.BookIdHint.Value : null,
                UpdatedAt = TalearrRecordingsStore.ParseDate(row.UpdatedAt)
            };
        }

        private TalearrNarratorEdit ToModel(NarratorRow row)
        {
            if (row == null || string.IsNullOrWhiteSpace(row.EditionKey))
            {
                return null;
            }

            var provider = Deserialize<NarratorProvider>(row.Provider) ?? new NarratorProvider();

            return new TalearrNarratorEdit
            {
                EditionKey = row.EditionKey,
                WorkKey = row.WorkKey,
                Narrators = Deserialize<List<string>>(row.Narrators) ?? new List<string>(),
                ProviderCaptured = provider.Captured,
                ProviderNarrator = provider.Narrator,
                ProviderNarratorNames = provider.Names ?? new List<string>(),
                BookIdHint = row.BookIdHint.HasValue ? (int?)row.BookIdHint.Value : null,
                UpdatedAt = TalearrRecordingsStore.ParseDate(row.UpdatedAt)
            };
        }

        private T Deserialize<T>(string json)
            where T : class
        {
            if (string.IsNullOrWhiteSpace(json))
            {
                return null;
            }

            try
            {
                return JsonSerializer.Deserialize<T>(json, JsonOptions);
            }
            catch (JsonException ex)
            {
                _logger.Debug(ex, "Talearr: ignoring an unreadable book detail edit value");
                return null;
            }
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
                _logger.Warn(ex, "Talearr: could not {0} in {1}; books show the provider's details", what, TalearrDatabase.FileName);
                return fallback;
            }
        }

        private sealed class NarratorProvider
        {
            public bool Captured { get; set; }
            public string Narrator { get; set; }
            public List<string> Names { get; set; }
        }

        private sealed class EditRow
        {
            public string WorkKey { get; set; }
            public string Title { get; set; }
            public string Series { get; set; }
            public string Provider { get; set; }
            public long? BookIdHint { get; set; }
            public string UpdatedAt { get; set; }
        }

        private sealed class NarratorRow
        {
            public string EditionKey { get; set; }
            public string WorkKey { get; set; }
            public string Narrators { get; set; }
            public string Provider { get; set; }
            public long? BookIdHint { get; set; }
            public string UpdatedAt { get; set; }
        }

        private sealed class UserSeriesRow
        {
            public long SeriesId { get; set; }
            public long AuthorId { get; set; }
            public long MediaType { get; set; }
            public string Title { get; set; }
            public string CreatedAt { get; set; }
        }
    }
}
