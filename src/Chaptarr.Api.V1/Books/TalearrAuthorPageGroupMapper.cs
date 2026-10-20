using System;
using System.Collections.Generic;
using System.Linq;
using NzbDrone.Core.Books;
using NzbDrone.Core.Books.Services;
using NzbDrone.Core.Profiles.Metadata;
using NzbDrone.Core.Talearr.Catalog;

namespace Chaptarr.Api.V1.Books
{
    // Talearr: fills BookResource.TalearrGroup for one author's book list
    // (GET /api/v1/book?authorId=). Computed per request from the rows already loaded; nothing
    // is written. See docs/TALEARR_AUTHOR_PAGE.md.
    public static class TalearrAuthorPageGroupMapper
    {
        public static void Apply(IList<BookResource> resources, IEnumerable<Book> books, NzbDrone.Core.Books.Author author)
        {
            if (resources == null || resources.Count == 0)
            {
                return;
            }

            var booksById = (books ?? Enumerable.Empty<Book>())
                .Where(book => book != null)
                .GroupBy(book => book.Id)
                .ToDictionary(group => group.Key, group => group.First());

            var entries = new List<TalearrAuthorPageEntry>();

            foreach (var resource in resources.Where(resource => resource != null))
            {
                booksById.TryGetValue(resource.Id, out var book);
                entries.Add(ToEntry(resource, book));
            }

            var preferred = TalearrAuthorPageClassifier.ResolvePreferredLanguages(GetProfileLanguages(author), entries);
            var groups = TalearrAuthorPageClassifier.Classify(entries, author?.Name, preferred);

            foreach (var resource in resources.Where(resource => resource != null))
            {
                resource.TalearrGroup = groups.TryGetValue(resource.Id, out var group)
                    ? group
                    : TalearrAuthorPageGroup.Main;
            }
        }

        private static TalearrAuthorPageEntry ToEntry(BookResource resource, Book book)
        {
            var hasFiles = resource.HasFiles || (resource.Statistics?.BookFileCount ?? 0) > 0;
            var mediaType = string.Equals(resource.MediaType, "ebook", StringComparison.OrdinalIgnoreCase)
                ? BookMediaType.Ebook
                : BookMediaType.Audiobook;

            var monitored = mediaType == BookMediaType.Ebook
                ? resource.EbookMonitored == true
                : resource.AudiobookMonitored == true;

            var kept = hasFiles || monitored || resource.Monitored ||
                       resource.AddOptions?.AddType == BookAddType.Manual ||
                       (book != null && TalearrAuthorPageClassifier.IsKept(book, hasFiles));

            var monitoredEdition = book?.Editions?.FirstOrDefault(edition => edition?.Monitored == true);
            var language = EditionMetadataProfileFilter.NormalizeLanguageBucket(monitoredEdition?.Language) ??
                           EditionMetadataProfileFilter.NormalizeLanguageBucket(book?.LanguageCode);

            var workIds = new List<string>();
            AddWorkId(workIds, "hc", resource.HardcoverBookId ?? book?.HardcoverBookId);
            AddWorkId(workIds, "gr", resource.GoodreadsWorkId ?? book?.GoodreadsWorkId);
            AddWorkId(workIds, "ol", resource.OpenLibraryWorkId ?? book?.OpenLibraryWorkId);

            return new TalearrAuthorPageEntry
            {
                Id = resource.Id,
                Title = resource.Title ?? book?.Title,
                MediaType = mediaType,
                IsKept = kept,
                IsOmnibus = resource.IsOmnibus || book?.IsOmnibus == true,
                Genres = resource.Genres ?? book?.Genres ?? new List<string>(),
                Language = language,
                Votes = resource.Ratings?.Votes ?? 0,
                ReleaseDate = resource.ReleaseDate,
                WorkIds = workIds
            };
        }

        private static void AddWorkId(List<string> workIds, string prefix, string id)
        {
            if (!string.IsNullOrWhiteSpace(id))
            {
                workIds.Add(prefix + ":" + id.Trim());
            }
        }

        private static IEnumerable<string> GetProfileLanguages(NzbDrone.Core.Books.Author author)
        {
            var languages = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var profile in GetProfiles(author))
            {
                EditionMetadataProfileFilter.ParseAllowedLanguages(
                    profile?.AllowedLanguages,
                    out var allowed,
                    out _,
                    out _,
                    out _);

                languages.UnionWith(allowed);
            }

            return languages;
        }

        private static IEnumerable<MetadataProfile> GetProfiles(NzbDrone.Core.Books.Author author)
        {
            var profiles = new List<MetadataProfile>();
            if (author == null)
            {
                return profiles;
            }

            // Lazy loads; a missing or unloadable profile just means no profile languages.
            try
            {
                if (author.AudiobookMetadataProfile?.Value != null)
                {
                    profiles.Add(author.AudiobookMetadataProfile.Value);
                }

                if (author.EbookMetadataProfile?.Value != null)
                {
                    profiles.Add(author.EbookMetadataProfile.Value);
                }
            }
            catch (Exception)
            {
                // Best effort only.
            }

            return profiles;
        }
    }
}
