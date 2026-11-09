using System;
using System.Collections.Generic;
using System.Data;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using Dapper;
using NLog;
using NzbDrone.Core.Talearr.Recordings;

namespace NzbDrone.Core.Talearr.Metadata.Source
{
    public sealed class TalearrAudibleCatalog
    {
        public string Marketplace { get; set; }
        public List<AudibleCatalogItem> Items { get; set; } = new List<AudibleCatalogItem>();
        public DateTime FetchedAt { get; set; }

        // True when Audible did not answer and this is the last stored copy.
        public bool Stale { get; set; }
    }

    public interface ITalearrAudibleAuthorCatalog
    {
        // The author's audiobooks on Audible (the recordings marketplace): products that credit
        // one of the names as an author, books only, in the marketplace's language. A fresh read,
        // a cached one, or the last stored copy when Audible does not answer; null when there is
        // none of those.
        TalearrAudibleCatalog Get(string authorKey, IReadOnlyCollection<string> authorNames);
    }

    // Talearr: reads an author's catalog from Audible for the Audible fill. Up to MaxPages pages
    // of 50 products, newest first, through the recordings client (one request a second, back-off
    // on 429/503). A read is reused for FreshFor; the last good read is also kept in talearr.db
    // (AudibleAuthorCatalogs) so a restart does not refetch and an outage does not drop the books
    // Audible filled in.
    public class TalearrAudibleAuthorCatalog : ITalearrAudibleAuthorCatalog
    {
        public const int MaxPages = 20;
        public static readonly TimeSpan FreshFor = TimeSpan.FromDays(3);

        private static readonly JsonSerializerOptions JsonOptions = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase
        };

        private static readonly HashSet<string> BookDeliveryTypes = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "SinglePartBook",
            "MultiPartBook"
        };

        private readonly ITalearrAudibleClient _client;
        private readonly ITalearrRecordingsSettingsService _recordingsSettings;
        private readonly ITalearrDatabase _database;
        private readonly Logger _logger;
        private readonly Func<DateTime> _clock;
        private readonly Dictionary<string, TalearrAudibleCatalog> _memory = new Dictionary<string, TalearrAudibleCatalog>(StringComparer.OrdinalIgnoreCase);

        public TalearrAudibleAuthorCatalog(ITalearrAudibleClient client,
                                           ITalearrRecordingsSettingsService recordingsSettings,
                                           ITalearrDatabase database,
                                           Logger logger)
            : this(client, recordingsSettings, database, logger, () => DateTime.UtcNow)
        {
        }

        internal TalearrAudibleAuthorCatalog(ITalearrAudibleClient client,
                                             ITalearrRecordingsSettingsService recordingsSettings,
                                             ITalearrDatabase database,
                                             Logger logger,
                                             Func<DateTime> clock)
        {
            _client = client;
            _recordingsSettings = recordingsSettings;
            _database = database;
            _logger = logger;
            _clock = clock;
        }

        public TalearrAudibleCatalog Get(string authorKey, IReadOnlyCollection<string> authorNames)
        {
            var names = (authorNames ?? Array.Empty<string>())
                .Where(n => !string.IsNullOrWhiteSpace(n))
                .Select(n => n.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            if (string.IsNullOrWhiteSpace(authorKey) || names.Count == 0)
            {
                return null;
            }

            var marketplace = TalearrRecordingsSettingsService.Resolve(_recordingsSettings).Marketplace;
            var cacheKey = $"{authorKey.Trim().ToLowerInvariant()}|{marketplace}";
            var now = _clock();

            lock (_memory)
            {
                if (_memory.TryGetValue(cacheKey, out var cached) && now - cached.FetchedAt < FreshFor)
                {
                    return cached;
                }
            }

            var stored = Load(authorKey, marketplace);
            if (stored != null && now - stored.FetchedAt < FreshFor)
            {
                Remember(cacheKey, stored);
                return stored;
            }

            var items = Fetch(marketplace, names);
            if (items == null)
            {
                if (stored != null)
                {
                    stored.Stale = true;
                    _logger.Debug("[TALEARR-AUDIBLE] Audible did not answer for {0}; using the copy from {1:yyyy-MM-dd}", names[0], stored.FetchedAt);
                }

                return stored;
            }

            var fresh = new TalearrAudibleCatalog
            {
                Marketplace = marketplace,
                Items = items,
                FetchedAt = now
            };

            Save(authorKey, fresh);
            Remember(cacheKey, fresh);
            return fresh;
        }

        // Null when a page went unanswered.
        private List<AudibleCatalogItem> Fetch(string marketplace, List<string> names)
        {
            var wanted = new HashSet<string>(names.Select(TalearrSourceGapFill.Normalize).Where(n => n.Length > 0), StringComparer.Ordinal);
            var language = MarketplaceLanguage(marketplace);
            var byAsin = new Dictionary<string, AudibleCatalogItem>(StringComparer.OrdinalIgnoreCase);

            for (var page = 0; page < MaxPages; page++)
            {
                var result = _client.SearchAuthor(marketplace, names[0], page);
                if (!result.Answered)
                {
                    return null;
                }

                foreach (var item in result.Items)
                {
                    if (IsAuthorsBook(item, wanted, language) && !byAsin.ContainsKey(item.Asin))
                    {
                        byAsin[item.Asin] = item;
                    }
                }

                if (result.Items.Count < TalearrAudibleClient.AuthorPageSize ||
                    (page + 1) * TalearrAudibleClient.AuthorPageSize >= result.TotalResults)
                {
                    break;
                }
            }

            _logger.Debug("[TALEARR-AUDIBLE] {0}: {1} audiobooks on Audible {2}", names[0], byAsin.Count, marketplace);
            return byAsin.Values.ToList();
        }

        internal static bool IsAuthorsBook(AudibleCatalogItem item, HashSet<string> wantedNames, string language)
        {
            if (item == null || string.IsNullOrWhiteSpace(item.Asin) || string.IsNullOrWhiteSpace(item.Title))
            {
                return false;
            }

            if (!string.IsNullOrWhiteSpace(item.DeliveryType) && !BookDeliveryTypes.Contains(item.DeliveryType))
            {
                return false;
            }

            if (language != null && !string.IsNullOrWhiteSpace(item.Language) && !string.Equals(item.Language, language, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            return item.Authors.Any(a => wantedNames.Contains(TalearrSourceGapFill.Normalize(a)));
        }

        // The language Audible reports for the marketplace's own books.
        internal static string MarketplaceLanguage(string marketplace)
        {
            return (marketplace ?? string.Empty).ToLowerInvariant() switch
            {
                "fr" => "french",
                "de" => "german",
                "it" => "italian",
                "es" => "spanish",
                "jp" => "japanese",
                _ => "english"
            };
        }

        private void Remember(string cacheKey, TalearrAudibleCatalog catalog)
        {
            lock (_memory)
            {
                _memory[cacheKey] = catalog;
            }
        }

        private TalearrAudibleCatalog Load(string authorKey, string marketplace)
        {
            return Run("read an Audible author catalog", null, connection =>
            {
                var row = connection.QueryFirstOrDefault<CatalogRow>(
                    "SELECT * FROM AudibleAuthorCatalogs WHERE AuthorKey = @AuthorKey AND Marketplace = @Marketplace",
                    new { AuthorKey = authorKey.Trim(), Marketplace = marketplace });

                if (row == null ||
                    !DateTime.TryParse(row.FetchedAt, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var fetchedAt))
                {
                    return null;
                }

                return new TalearrAudibleCatalog
                {
                    Marketplace = marketplace,
                    Items = JsonSerializer.Deserialize<List<AudibleCatalogItem>>(row.Items, JsonOptions) ?? new List<AudibleCatalogItem>(),
                    FetchedAt = DateTime.SpecifyKind(fetchedAt, DateTimeKind.Utc)
                };
            });
        }

        private void Save(string authorKey, TalearrAudibleCatalog catalog)
        {
            Run("store an Audible author catalog", 0, connection => connection.Execute(@"
INSERT INTO AudibleAuthorCatalogs (AuthorKey, Marketplace, Items, FetchedAt)
VALUES (@AuthorKey, @Marketplace, @Items, @FetchedAt)
ON CONFLICT (AuthorKey, Marketplace) DO UPDATE SET Items = excluded.Items, FetchedAt = excluded.FetchedAt",
                new
                {
                    AuthorKey = authorKey.Trim(),
                    catalog.Marketplace,
                    Items = JsonSerializer.Serialize(catalog.Items, JsonOptions),
                    FetchedAt = catalog.FetchedAt.ToUniversalTime().ToString("o", CultureInfo.InvariantCulture)
                }));
        }

        private T Run<T>(string what, T fallback, Func<IDbConnection, T> action)
        {
            if (_database == null)
            {
                return fallback;
            }

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

        private sealed class CatalogRow
        {
            public string AuthorKey { get; set; }
            public string Marketplace { get; set; }
            public string Items { get; set; }
            public string FetchedAt { get; set; }
        }
    }
}
