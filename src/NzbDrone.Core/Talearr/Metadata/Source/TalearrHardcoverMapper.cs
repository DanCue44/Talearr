using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using NzbDrone.Core.Books;
using NzbDrone.Core.MetadataSource;
using NzbDrone.Core.MetadataSource.BookInfo.V5;

namespace NzbDrone.Core.Talearr.Metadata.Source
{
    // Talearr: turns a Hardcover author catalog into the metadata server's V5 author payload, so
    // the existing pipeline (BookInfoProxy.ConvertV5AuthorToDomain, the dual audiobook/ebook
    // rows, RefreshAuthorService.NormalizeRemoteBooks, metadata profiles, edition retention)
    // consumes it unchanged, the same way TalearrOpenLibraryMapper does for Open Library.
    //
    // Id shapes are the ones the metadata server already uses for Hardcover data, so a book the
    // server added keeps matching (BookIdentity work tokens, BookEditionIdentity edition ids):
    //   author   hc:{authors.id}                  (Author.HardcoverAuthorId)
    //   work     hc:{books.id}                    (V5Book.Id, BaseBookId, HardcoverBookId)
    //   edition  hc:edition:{editions.id}         (V5Edition.Id; HardcoverEditionId is the bare
    //                                              number). ConvertV5EditionToDomain appends
    //                                              -audiobook / -ebook for ForeignEditionId.
    //   series   hc:{series.id}                   (V5Series.Id and HardcoverSeriesId);
    //            members name works by hc:{books.id}
    // Hardcover duplicates (book_status_id 4 or canonical_id set) are not books of their own;
    // their ids are listed in the canonical work's provider_ids_all so a local row created from
    // the duplicate id still matches.
    public static class TalearrHardcoverMapper
    {
        public const string SourceName = "Hardcover";
        public const string SiteUrl = "https://hardcover.app";

        // V5 reading formats (V5Edition): 1 physical, 2 audio, 3 ebook.
        public const int V5Physical = 1;
        public const int V5Audio = 2;
        public const int V5Ebook = 3;

        public static string AuthorId(int id) => $"hc:{id}";
        public static string WorkId(int id) => $"hc:{id}";
        public static string EditionId(int id) => $"hc:edition:{id}";
        public static string SeriesId(int id) => $"hc:{id}";

        // Hardcover reading_format_id: 1 Physical, 2 Audio, 3 Both, 4 Ebook. "Both" is not
        // explained in the docs; it is treated as physical here (verify with a real token).
        // A missing id falls back to edition_format text.
        public static int ToV5ReadingFormat(int hardcoverFormatId, string editionFormat)
        {
            switch (hardcoverFormatId)
            {
                case 1:
                case 3:
                    return V5Physical;
                case 2:
                    return V5Audio;
                case 4:
                    return V5Ebook;
            }

            var format = editionFormat?.ToLowerInvariant() ?? string.Empty;
            if (format.Contains("audio"))
            {
                return V5Audio;
            }

            if (format.Contains("kindle") || format.Contains("ebook") || format.Contains("e-book") || format.Contains("epub") || format.Contains("digital"))
            {
                return V5Ebook;
            }

            return V5Physical;
        }

        public static string FormatName(int v5ReadingFormat)
        {
            return v5ReadingFormat switch
            {
                V5Audio => "audiobook",
                V5Ebook => "ebook",
                _ => "physical"
            };
        }

        // 1 -> "1", 1.5 -> "1.5", 2.0 -> "2".
        public static string FormatPosition(decimal? position, string details)
        {
            if (position.HasValue)
            {
                return position.Value.ToString("0.##", CultureInfo.InvariantCulture);
            }

            return string.IsNullOrWhiteSpace(details) ? null : details.Trim();
        }

        public static V5AuthorResponse BuildAuthorResponse(HardcoverAuthorCatalog catalog)
        {
            if (catalog?.Author == null)
            {
                return null;
            }

            var authorData = BuildAuthor(catalog.Author);

            var aliasesByCanonical = (catalog.DuplicateBooks ?? new List<HardcoverBookRecord>())
                .Where(d => d.CanonicalId.HasValue && d.CanonicalId.Value != d.Id)
                .GroupBy(d => d.CanonicalId.Value)
                .ToDictionary(g => g.Key, g => g.Select(d => d.Id).Distinct().ToList());

            var books = new List<V5Book>();
            var seen = new HashSet<int>();
            foreach (var record in catalog.Books ?? new List<HardcoverBookRecord>())
            {
                if (record == null || !seen.Add(record.Id))
                {
                    continue;
                }

                aliasesByCanonical.TryGetValue(record.Id, out var aliases);
                var book = BuildBook(record, catalog.Series, aliases);
                if (book != null)
                {
                    books.Add(book);
                }
            }

            var series = BuildSeries(catalog);

            var lastUpdated = new[] { catalog.Author.UpdatedAt }
                .Concat((catalog.Books ?? new List<HardcoverBookRecord>()).Select(b => b.UpdatedAt))
                .Where(d => d.HasValue)
                .Select(d => d.Value)
                .DefaultIfEmpty(DateTime.MinValue)
                .Max();

            return new V5AuthorResponse
            {
                // Not the server's ETag; a stamp of the newest Hardcover updated_at, for logs.
                ETag = lastUpdated > DateTime.MinValue ? $"hc-{lastUpdated.ToUniversalTime().Ticks}" : null,
                Author = authorData,
                Books = books,
                Series = series,
                Summary = new V5Summary
                {
                    TotalBooks = books.Count,
                    TotalSeries = series.Count,
                    FormatsAvailable = new V5FormatsAvailable
                    {
                        Audiobook = books.Count(b => b.HasAudiobook),
                        Ebook = books.Count(b => b.HasEbook),
                        Physical = books.Count(b => b.HasPhysical)
                    }
                }
            };
        }

        internal static V5AuthorData BuildAuthor(HardcoverAuthorRecord record)
        {
            var id = AuthorId(record.Id);
            var authorData = new V5AuthorData
            {
                Id = id,
                HardcoverAuthorId = id,
                Name = record.Name,
                SortName = record.Name,
                Slug = record.Slug,
                Bio = string.IsNullOrWhiteSpace(record.Bio) ? null : record.Bio.Trim(),
                BirthDate = record.BornDate,
                DeathDate = record.DeathDate,
                Pseudonyms = record.AlternateNames?.ToList() ?? new List<string>(),
                LastUpdated = record.UpdatedAt?.ToUniversalTime().ToString("o", CultureInfo.InvariantCulture),
                Isni = FirstIdentifier(record, "isni"),
                Viaf = FirstIdentifier(record, "viaf"),
                ProviderUrls = new ProviderUrlMap()
            };

            var hardcoverIds = new List<string> { id };
            if (record.CanonicalId.HasValue && record.CanonicalId.Value > 0 && record.CanonicalId.Value != record.Id)
            {
                hardcoverIds.Add(AuthorId(record.CanonicalId.Value));
            }

            authorData.ProviderIdsAllSnake["hc"] = hardcoverIds;

            // identifiers (jsonb) may name the same author elsewhere. Assumed keys: goodreads,
            // openlibrary / open_library, amazon. Values that do not look like ids are ignored.
            var goodreads = FirstIdentifier(record, "goodreads");
            if (goodreads != null && goodreads.All(char.IsDigit))
            {
                authorData.GoodreadsAuthorId = ProviderIdHelper.WithPrefix("gr", goodreads);
            }

            var openLibrary = FirstIdentifier(record, "openlibrary") ?? FirstIdentifier(record, "open_library");
            if (openLibrary != null)
            {
                var key = openLibrary.Trim().TrimEnd('/');
                key = key.Substring(key.LastIndexOf('/') + 1);
                if (key.StartsWith("OL", StringComparison.OrdinalIgnoreCase) && key.EndsWith("A", StringComparison.OrdinalIgnoreCase))
                {
                    authorData.OpenLibraryAuthorId = ProviderIdHelper.WithPrefix("ol", key.ToUpperInvariant());
                }
            }

            var amazon = FirstIdentifier(record, "amazon");
            if (amazon != null && ProviderIdHelper.TryNormalize($"az:{amazon}", "az", out var amazonId))
            {
                authorData.AudnexusAuthorId = amazonId;
            }

            if (!string.IsNullOrWhiteSpace(record.Slug))
            {
                authorData.ProviderUrls.SetNormalized("hardcover", $"{SiteUrl}/authors/{record.Slug}");
            }

            if (!string.IsNullOrWhiteSpace(record.ImageUrl))
            {
                authorData.Photos.Add(new V5AuthorPhoto { Url = record.ImageUrl, Provider = "hardcover", ProviderId = id, IsPrimary = true });
            }

            return authorData;
        }

        internal static V5Book BuildBook(HardcoverBookRecord record, IReadOnlyDictionary<int, HardcoverSeriesRecord> series, IEnumerable<int> aliasIds = null)
        {
            var workId = WorkId(record.Id);
            var bookUrl = string.IsNullOrWhiteSpace(record.Slug) ? null : $"{SiteUrl}/books/{record.Slug}";

            var editions = (record.Editions ?? new List<HardcoverEditionRecord>())
                .Where(e => e != null && (!e.CanonicalId.HasValue || e.CanonicalId.Value == e.Id))
                .GroupBy(e => e.Id)
                .Select(g => g.First())
                .OrderBy(e => e.Id)
                .Select(e => BuildEdition(e, record, bookUrl))
                .ToList();

            var featured = (record.Series ?? new List<HardcoverBookSeriesRecord>())
                .OrderByDescending(s => s.Featured)
                .ThenBy(s => s.SeriesId)
                .FirstOrDefault();

            HardcoverSeriesRecord featuredSeries = null;
            if (featured != null)
            {
                series?.TryGetValue(featured.SeriesId, out featuredSeries);
            }

            var popular = (record.Editions ?? new List<HardcoverEditionRecord>())
                .Where(e => !string.IsNullOrWhiteSpace(e.LanguageCode2) || !string.IsNullOrWhiteSpace(e.LanguageCode3))
                .OrderByDescending(e => e.UsersCount)
                .FirstOrDefault();

            var defaultAudio = record.DefaultAudioEditionId.HasValue
                ? record.Editions?.FirstOrDefault(e => e.Id == record.DefaultAudioEditionId.Value)
                : null;

            var hardcoverIds = new List<string> { workId };
            foreach (var alias in aliasIds ?? Enumerable.Empty<int>())
            {
                var aliasId = WorkId(alias);
                if (!hardcoverIds.Contains(aliasId))
                {
                    hardcoverIds.Add(aliasId);
                }
            }

            var book = new V5Book
            {
                Id = workId,
                BaseBookId = workId,
                HardcoverBookId = workId,
                Title = record.Title,
                Subtitle = record.Subtitle,
                Slug = record.Slug,
                Description = record.Description,
                PublicationDate = ParseDate(record.ReleaseDate),
                PublicationYear = record.ReleaseYear ?? ParseDate(record.ReleaseDate)?.Year,
                LanguageCode = popular?.LanguageCode2 ?? popular?.LanguageCode3,
                LanguageName = popular?.LanguageName,
                SeriesName = featuredSeries?.Name,
                SeriesPosition = featured == null ? null : FormatPosition(featured.Position, featured.Details),
                RatingAverage = record.Rating ?? 0m,
                RatingCount = record.RatingsCount,
                PageCount = record.Pages,
                DurationSeconds = record.AudioSeconds ?? defaultAudio?.AudioSeconds,
                CoverUrl = record.ImageUrl,
                Genres = record.Genres?.ToList() ?? new List<string>(),
                Moods = record.Moods?.ToList() ?? new List<string>(),
                Publisher = editions.Select(e => e.Publisher).FirstOrDefault(p => !string.IsNullOrWhiteSpace(p)),
                HasAudiobook = editions.Any(e => e.ReadingFormatId == V5Audio),
                HasEbook = editions.Any(e => e.ReadingFormatId == V5Ebook),
                HasPhysical = editions.Any(e => e.ReadingFormatId == V5Physical),

                // Hardcover's compilation flags (books.compilation, book_series.compilation).
                // book_category_id 8 (Collection) is not used: a story collection is not an omnibus.
                IsOmnibus = record.Compilation || (record.Series?.Any(s => s.Compilation) ?? false),
                Editions = editions,
                Links = new Dictionary<string, string>(),
                ProviderUrls = new ProviderUrlMap()
            };

            book.ProviderIdsAllSnake["hc"] = hardcoverIds;

            if (bookUrl != null)
            {
                book.Links["hardcover"] = bookUrl;
                book.ProviderUrls.SetNormalized("hardcover", bookUrl);
            }

            return book;
        }

        internal static V5Edition BuildEdition(HardcoverEditionRecord record, HardcoverBookRecord book, string bookUrl)
        {
            var format = ToV5ReadingFormat(record.ReadingFormatId, record.EditionFormat);
            var asin = string.IsNullOrWhiteSpace(record.Asin) ? null : record.Asin.Trim().ToUpperInvariant();

            var edition = new V5Edition
            {
                Id = EditionId(record.Id),
                HardcoverEditionId = record.Id.ToString(CultureInfo.InvariantCulture),
                ReadingFormatId = format,
                Format = FormatName(format),
                EditionFormat = record.EditionFormat,
                EditionInfo = record.EditionInformation,
                Title = string.IsNullOrWhiteSpace(record.Title) ? book?.Title : record.Title,
                Subtitle = record.Subtitle,
                Language = record.LanguageCode3 ?? record.LanguageCode2,
                LanguageCode = record.LanguageCode2,
                Isbn10 = Clean(record.Isbn10),
                Isbn13 = Clean(record.Isbn13),
                Asin = asin,
                Asins = asin == null ? new List<string>() : new List<string> { asin },
                DurationSeconds = record.AudioSeconds,
                Publisher = record.Publisher,
                PublicationDate = ParseDate(record.ReleaseDate),
                PageCount = record.Pages,
                CoverUrl = record.ImageUrl ?? book?.ImageUrl,
                RatingAverage = record.Rating ?? 0m,

                // Editions have no ratings_count on Hardcover; users_count (readers who shelved
                // this edition) stands in as the popularity signal edition selection ranks by.
                RatingCount = record.UsersCount,
                ProviderUrls = new ProviderUrlMap()
            };

            var order = 0;
            foreach (var narrator in (record.Contributors ?? new List<HardcoverContributorRecord>())
                         .Where(c => string.Equals(c.Contribution?.Trim(), "Narrator", StringComparison.OrdinalIgnoreCase)))
            {
                edition.Narrators.Add(new V5NarratorCredit
                {
                    Name = narrator.Name,
                    HardcoverNarratorId = narrator.AuthorId.HasValue ? $"hc:{narrator.AuthorId.Value}" : null,
                    Order = order,
                    IsPrimary = order == 0,
                    Role = "Narrator"
                });
                edition.NarratorNames.Add(narrator.Name);
                order++;
            }

            if (bookUrl != null)
            {
                edition.ProviderUrls.SetNormalized("hardcover", $"{bookUrl}/editions/{record.Id}");
            }

            return edition;
        }

        internal static List<V5Series> BuildSeries(HardcoverAuthorCatalog catalog)
        {
            var result = new List<V5Series>();
            var books = catalog.Books ?? new List<HardcoverBookRecord>();

            foreach (var seriesRecord in (catalog.Series ?? new Dictionary<int, HardcoverSeriesRecord>()).Values.OrderBy(s => s.Id))
            {
                if (string.IsNullOrWhiteSpace(seriesRecord.Name))
                {
                    continue;
                }

                var members = books
                    .SelectMany(b => (b.Series ?? new List<HardcoverBookSeriesRecord>())
                        .Where(s => s.SeriesId == seriesRecord.Id)
                        .Select(s => (Book: b, Link: s)))
                    .OrderBy(m => m.Link.Position ?? decimal.MaxValue)
                    .ThenBy(m => m.Book.Id)
                    .ToList();

                var id = SeriesId(seriesRecord.Id);
                var v5Series = new V5Series
                {
                    Id = id,
                    HardcoverSeriesId = id,
                    Name = seriesRecord.Name,
                    Description = seriesRecord.Description,
                    SeriesType = "main",
                    TotalBooks = seriesRecord.BooksCount,
                    PrimaryBooks = seriesRecord.PrimaryBooksCount ?? members.Count(m => IsPrimarySlot(m.Link)),
                    Numbered = members.Any(m => m.Link.Position.HasValue),
                    ProviderUrls = new ProviderUrlMap()
                };

                if (!string.IsNullOrWhiteSpace(seriesRecord.Slug))
                {
                    var url = $"{SiteUrl}/series/{seriesRecord.Slug}";
                    v5Series.ProviderUrls.SetNormalized("hardcover", url);
                    v5Series.Links["hardcover"] = url;
                }

                foreach (var member in members)
                {
                    v5Series.Books.Add(new V5SeriesBook
                    {
                        BookId = WorkId(member.Book.Id),
                        Title = member.Book.Title,
                        Position = FormatPosition(member.Link.Position, member.Link.Details),
                        CoverUrl = member.Book.ImageUrl,
                        IsPrimary = IsPrimarySlot(member.Link)
                    });
                }

                result.Add(v5Series);
            }

            return result;
        }

        // A main entry: a whole-number position that is not a compilation. Novellas sit at
        // fractional positions (1.5) on Hardcover; unnumbered companions have none.
        internal static bool IsPrimarySlot(HardcoverBookSeriesRecord link)
        {
            return link != null && !link.Compilation && link.Position.HasValue && decimal.Truncate(link.Position.Value) == link.Position.Value;
        }

        private static string FirstIdentifier(HardcoverAuthorRecord record, string key)
        {
            return record?.Identifiers != null && record.Identifiers.TryGetValue(key, out var values)
                ? values?.FirstOrDefault(v => !string.IsNullOrWhiteSpace(v))?.Trim()
                : null;
        }

        private static string Clean(string isbn)
        {
            if (string.IsNullOrWhiteSpace(isbn))
            {
                return null;
            }

            var cleaned = new string(isbn.Where(c => char.IsDigit(c) || c == 'X' || c == 'x').ToArray()).ToUpperInvariant();
            return cleaned.Length == 0 ? null : cleaned;
        }

        internal static DateTime? ParseDate(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return null;
            }

            var formats = new[] { "yyyy-MM-dd", "yyyy-MM", "yyyy" };
            if (DateTime.TryParseExact(value.Trim(), formats, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var parsed))
            {
                return DateTime.SpecifyKind(parsed, DateTimeKind.Utc);
            }

            return DateTime.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out parsed)
                ? DateTime.SpecifyKind(parsed, DateTimeKind.Utc)
                : null;
        }
    }
}
