using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using NzbDrone.Common.Extensions;
using NzbDrone.Core.Books;
using NzbDrone.Core.MediaCover;
using NzbDrone.Core.MetadataSource;
using NzbDrone.Core.MetadataSource.BookInfo.V5;
using NzbDrone.Core.Parser;

namespace NzbDrone.Core.Talearr.MetadataFallback
{
    // Talearr: maps Open Library JSON to the shapes the rest of Talearr already understands.
    //
    // Identity rules (see docs in TalearrMetadataFallbackService):
    // - Open Library ids are stored in the existing OpenLibrary* provider id columns with the
    //   canonical "ol:" prefix (ol:OL239800A, ol:OL1992198W). They never collide with hc:/gr:/az:.
    // - The id the user asked for (hc:/gr:/az: from a normal search result) is kept on the author,
    //   so the author keeps the metadata server's identity and a normal refresh upgrades it.
    // - Goodreads and Amazon author ids listed in Open Library's remote_ids are copied for an
    //   author found through Open Library itself, so the metadata server can resolve it later.
    public static class TalearrOpenLibraryMapper
    {
        public const string SourceName = "Open Library";

        public static string WorkProviderId(string workKey)
        {
            var key = OpenLibraryJson.BareKey(workKey);
            return key == null ? null : ProviderIdHelper.WithPrefix("ol", key);
        }

        public static string AuthorProviderId(string authorKey)
        {
            var key = OpenLibraryJson.BareKey(authorKey);
            return key == null ? null : ProviderIdHelper.WithPrefix("ol", key);
        }

        public static string OpenLibraryUrl(string bareKey)
        {
            if (string.IsNullOrWhiteSpace(bareKey))
            {
                return null;
            }

            var kind = bareKey.EndsWith("A", StringComparison.OrdinalIgnoreCase) ? "authors"
                : bareKey.EndsWith("W", StringComparison.OrdinalIgnoreCase) ? "works"
                : "books";
            return $"{OpenLibraryClient.BaseUrl}/{kind}/{bareKey}";
        }

        // "30 September 1924" / "1924-09-30" / "1924" -> "1924-09-30" (or null when only partial).
        public static string ToIsoDate(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return null;
            }

            var formats = new[] { "yyyy-MM-dd", "d MMMM yyyy", "MMMM d, yyyy", "MMMM d yyyy", "d MMM yyyy", "MMM d, yyyy" };
            return DateTime.TryParseExact(value.Trim(), formats, CultureInfo.InvariantCulture, DateTimeStyles.AllowWhiteSpaces, out var parsed)
                ? parsed.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)
                : null;
        }

        public static string NormalizeName(string name)
        {
            return string.IsNullOrWhiteSpace(name) ? string.Empty : name.CleanAuthorName();
        }

        /// <summary>
        /// Builds a metadata-server shaped author payload, so the normal add pipeline (dual
        /// audiobook/ebook rows, edition retention, metadata profiles) handles it unchanged.
        /// </summary>
        public static V5AuthorResponse BuildAuthorResponse(OpenLibraryAuthorRecord record,
                                                           IEnumerable<OpenLibraryWorkDoc> works,
                                                           string requestedProviderId,
                                                           IReadOnlyDictionary<string, string> requestedWorkTitles = null)
        {
            if (record == null)
            {
                return null;
            }

            var olAuthorId = AuthorProviderId(record.Key);
            var requested = NormalizeRequestedId(requestedProviderId);
            var requestedPrefix = requested?.Substring(0, requested.IndexOf(':'));

            var authorData = new V5AuthorData
            {
                Id = requested ?? olAuthorId,
                Name = record.Name,
                SortName = record.Name,
                Slug = record.Key.ToLowerInvariant(),
                Bio = CleanBio(record.Bio),
                BirthDate = ToIsoDate(record.BirthDate),
                DeathDate = ToIsoDate(record.DeathDate),
                OpenLibraryAuthorId = olAuthorId,
                Isni = record.GetRemoteId("isni"),
                Viaf = record.GetRemoteId("viaf"),
                ProviderUrls = new ProviderUrlMap()
            };

            authorData.ProviderUrls.SetNormalized("openlibrary", OpenLibraryUrl(record.Key));

            switch (requestedPrefix)
            {
                case "hc":
                    authorData.HardcoverAuthorId = requested;
                    break;
                case "gr":
                    authorData.GoodreadsAuthorId = requested;
                    break;
                case "az":
                    authorData.AudnexusAuthorId = requested;
                    break;
                case "gb":
                    authorData.GoogleBooksAuthorId = requested;
                    break;
            }

            // Only an author found through Open Library itself borrows its remote ids. When the
            // user picked an author from another catalog, the name match may be a namesake, so no
            // third-party ids are attached to it.
            if (requested == null || requestedPrefix == "ol")
            {
                var goodreads = record.GetRemoteId("goodreads");
                if (goodreads != null && goodreads.All(char.IsDigit))
                {
                    authorData.GoodreadsAuthorId = ProviderIdHelper.WithPrefix("gr", goodreads);
                }

                var amazon = record.GetRemoteId("amazon");
                if (amazon != null && ProviderIdHelper.TryNormalize($"az:{amazon}", "az", out var amazonId))
                {
                    authorData.AudnexusAuthorId = amazonId;
                }
            }

            foreach (var photoId in record.PhotoIds.Take(3))
            {
                var url = OpenLibraryClient.AuthorPhotoUrl(photoId);
                if (url != null)
                {
                    authorData.Photos.Add(new V5AuthorPhoto { Url = url, Provider = "openlibrary", IsPrimary = authorData.Photos.Count == 0 });
                }
            }

            var books = new List<V5Book>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var work in works ?? Enumerable.Empty<OpenLibraryWorkDoc>())
            {
                if (work == null || !seen.Add(work.Key))
                {
                    continue;
                }

                // Works the author only edited or contributed to are kept when Open Library
                // lists the author at all; the primary catalog behaves the same way.
                if (work.AuthorKeys.Count > 0 && !work.AuthorKeys.Contains(record.Key, StringComparer.OrdinalIgnoreCase))
                {
                    continue;
                }

                var book = BuildBook(work);
                if (book != null)
                {
                    books.Add(book);
                }
            }

            AttachRequestedWorkIds(books, requestedWorkTitles);

            return new V5AuthorResponse
            {
                Author = authorData,
                Books = books,
                Series = new List<V5Series>(),
                Summary = new V5Summary { TotalBooks = books.Count }
            };
        }

        public static V5Book BuildBook(OpenLibraryWorkDoc work)
        {
            var workId = WorkProviderId(work?.Key);
            var editionKey = work?.EditionKey;
            if (workId == null || string.IsNullOrWhiteSpace(editionKey))
            {
                // Without an edition the add pipeline would drop the book anyway.
                return null;
            }

            var coverUrl = OpenLibraryClient.CoverUrl(work.CoverId);
            var isbn13 = work.Isbn13s.FirstOrDefault(i => i.StartsWith("978", StringComparison.Ordinal) || i.StartsWith("979", StringComparison.Ordinal));
            var isbn10 = work.Isbn10s.FirstOrDefault();
            var language = PickLanguage(work.Languages);
            var workUrl = OpenLibraryUrl(work.Key);

            var book = new V5Book
            {
                Id = workId,
                BaseBookId = workId,
                OpenLibraryWorkId = workId,
                Title = work.Title,
                Subtitle = work.Subtitle,
                Slug = work.Key.ToLowerInvariant(),
                PublicationYear = work.FirstPublishYear,
                LanguageCode = language,
                RatingAverage = work.RatingsAverage,
                RatingCount = work.RatingsCount,
                PageCount = work.PageCountMedian,
                CoverUrl = coverUrl,
                Publisher = work.Publishers.FirstOrDefault(),
                HasPhysical = true,
                Links = new Dictionary<string, string> { { "openlibrary", workUrl } },
                ProviderUrls = new ProviderUrlMap()
            };

            book.ProviderUrls.SetNormalized("openlibrary", workUrl);

            var editionId = ProviderIdHelper.WithPrefix("ol", editionKey);
            var edition = new V5Edition
            {
                Id = editionId,
                OpenLibraryEditionId = editionId,
                ReadingFormatId = 1,
                Format = "physical",
                Title = work.Title,
                Subtitle = work.Subtitle,
                LanguageCode = language,
                Isbn13 = isbn13,
                Isbn10 = isbn10,
                PageCount = work.PageCountMedian,
                Publisher = work.Publishers.FirstOrDefault(),
                PublicationDate = work.FirstPublishYear.HasValue && work.FirstPublishYear.Value > 0 && work.FirstPublishYear.Value < 10000
                    ? new DateTime(work.FirstPublishYear.Value, 1, 1, 0, 0, 0, DateTimeKind.Utc)
                    : null,
                CoverUrl = coverUrl,
                RatingAverage = work.RatingsAverage,
                RatingCount = work.RatingsCount,
                ProviderUrls = new ProviderUrlMap()
            };

            edition.ProviderUrls.SetNormalized("openlibrary", OpenLibraryUrl(editionKey));
            book.Editions.Add(edition);
            return book;
        }

        // A single-book add from a normal (hc:/gr:) search result names a work the Open Library
        // catalog does not know by id. When exactly one Open Library work has the same title, that
        // work carries the requested id too, so the add finds it and a later refresh from the
        // metadata server matches it by that id.
        internal static void AttachRequestedWorkIds(List<V5Book> books, IReadOnlyDictionary<string, string> requestedWorkTitles)
        {
            if (books == null || requestedWorkTitles == null || requestedWorkTitles.Count == 0)
            {
                return;
            }

            foreach (var kvp in requestedWorkTitles)
            {
                if (!ProviderIdHelper.TryNormalize(kvp.Key, defaultPrefix: null, out var requestedWorkId) ||
                    string.IsNullOrWhiteSpace(kvp.Value))
                {
                    continue;
                }

                var prefix = requestedWorkId.Substring(0, requestedWorkId.IndexOf(':'));
                if (prefix != "hc" && prefix != "gr")
                {
                    continue;
                }

                if (books.Any(b => string.Equals(b.Id, requestedWorkId, StringComparison.OrdinalIgnoreCase)))
                {
                    continue;
                }

                var title = TalearrTitleNormalizer.NormalizeFull(kvp.Value);
                var matches = books
                    .Where(b => string.Equals(TalearrTitleNormalizer.NormalizeFull(b.Title), title, StringComparison.Ordinal))
                    .ToList();

                if (matches.Count != 1)
                {
                    continue;
                }

                if (prefix == "hc" && string.IsNullOrWhiteSpace(matches[0].HardcoverBookId))
                {
                    matches[0].HardcoverBookId = requestedWorkId;
                }
                else if (prefix == "gr" && string.IsNullOrWhiteSpace(matches[0].GoodreadsWorkId))
                {
                    matches[0].GoodreadsWorkId = requestedWorkId;
                }
            }
        }

        public static Author MapSearchAuthor(OpenLibraryAuthorDoc doc)
        {
            var providerId = AuthorProviderId(doc?.Key);
            if (providerId == null || string.IsNullOrWhiteSpace(doc.Name))
            {
                return null;
            }

            var name = doc.Name.Trim();
            var died = ToIsoDate(doc.DeathDate).ToValidAuthorDate();
            var author = new Author
            {
                Name = name,
                TitleSlug = doc.Key.ToLowerInvariant(),
                Overview = string.IsNullOrWhiteSpace(doc.TopWork) ? string.Empty : $"Best known for {doc.TopWork}.",
                Born = ToIsoDate(doc.BirthDate).ToValidAuthorDate(),
                Died = died,
                Status = AuthorExtensions.GetLifeStatus(died),
                CleanName = name.CleanAuthorName(),
                SortName = name.ToLowerInvariant(),
                NameLastFirst = name.ToLastFirst(),
                SortNameLastFirst = name.ToLastFirst().ToLowerInvariant(),
                OpenLibraryAuthorId = providerId,
                Monitored = false,
                MetadataBookCount = doc.WorkCount > 0 ? doc.WorkCount : null,
                ProviderUrls = new ProviderUrlMap()
            };

            author.ProviderUrls.SetNormalized("openlibrary", OpenLibraryUrl(doc.Key));
            author.Links = new List<Links> { new Links { Name = "openlibrary", Url = OpenLibraryUrl(doc.Key) } };
            TalearrMetadataFallbackMarker.Mark(author, SourceName);
            return author;
        }

        public static Book MapSearchBook(OpenLibraryWorkDoc work)
        {
            var workId = WorkProviderId(work?.Key);
            if (workId == null)
            {
                return null;
            }

            var coverUrl = OpenLibraryClient.CoverUrl(work.CoverId);
            var book = new Book
            {
                Title = work.Title,
                Subtitle = work.Subtitle,
                Overview = string.Empty,
                TitleSlug = work.Key.ToLowerInvariant(),
                CleanTitle = work.Title.CleanAuthorName(),
                OpenLibraryWorkId = workId,
                AudiobookMonitored = false,
                EbookMonitored = false,
                AnyEditionOk = true,
                Ratings = new Ratings { Value = work.RatingsAverage, Votes = work.RatingsCount },
                ReleaseDate = work.FirstPublishYear.HasValue && work.FirstPublishYear.Value > 0 && work.FirstPublishYear.Value < 10000
                    ? new DateTime(work.FirstPublishYear.Value, 1, 1, 0, 0, 0, DateTimeKind.Utc)
                    : null,
                Images = new List<MediaCover.MediaCover>(),
                Links = new List<Links> { new Links { Name = "openlibrary", Url = OpenLibraryUrl(work.Key) } },
                Editions = new List<Edition>()
            };

            if (coverUrl != null)
            {
                book.Images.Add(new MediaCover.MediaCover { Url = coverUrl, CoverType = MediaCoverTypes.Cover });
            }

            var authorName = work.AuthorNames.FirstOrDefault();
            var authorId = AuthorProviderId(work.AuthorKeys.FirstOrDefault());
            if (!string.IsNullOrWhiteSpace(authorName) && authorId != null)
            {
                book.Author = new Author
                {
                    Name = authorName,
                    CleanName = authorName.CleanAuthorName(),
                    Status = AuthorStatusType.Continuing,
                    OpenLibraryAuthorId = authorId,
                    Monitored = false
                };
                TalearrMetadataFallbackMarker.Mark(book.Author, SourceName);
            }

            var edition = new Edition
            {
                Title = work.Title,
                TitleSlug = work.Key.ToLowerInvariant(),
                Overview = string.Empty,
                Monitored = true,
                ManualAdd = false,
                PageCount = work.PageCountMedian ?? 0,
                Isbn13 = work.Isbn13s.FirstOrDefault(),
                Isbn10 = work.Isbn10s.FirstOrDefault(),
                Ratings = new Ratings { Value = work.RatingsAverage, Votes = work.RatingsCount }
            };

            if (coverUrl != null)
            {
                edition.Images.Add(new MediaCover.MediaCover { Url = coverUrl, CoverType = MediaCoverTypes.Cover });
            }

            book.Editions = new List<Edition> { edition };
            TalearrMetadataFallbackMarker.Mark(book, SourceName);
            return book;
        }

        private static string NormalizeRequestedId(string requestedProviderId)
        {
            return ProviderIdHelper.TryNormalize(requestedProviderId, defaultPrefix: null, out var normalized) ? normalized : null;
        }

        private static string PickLanguage(List<string> languages)
        {
            if (languages == null || languages.Count == 0)
            {
                return null;
            }

            return languages.FirstOrDefault(l => string.Equals(l, "eng", StringComparison.OrdinalIgnoreCase)) ?? languages[0];
        }

        private static string CleanBio(string bio)
        {
            if (string.IsNullOrWhiteSpace(bio))
            {
                return null;
            }

            // Open Library bios are Markdown; keep the text readable.
            return bio.Replace("**", string.Empty).Replace("\r\n", "\n").Trim();
        }
    }
}
