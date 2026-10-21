using System;
using System.Collections.Generic;
using System.Linq;
using FluentValidation.Results;
using Newtonsoft.Json.Linq;
using NLog;
using NzbDrone.Common.Extensions;
using NzbDrone.Core.Configuration;
using NzbDrone.Core.ImportLists.OpenLibrary;
using NzbDrone.Core.Parser;
using NzbDrone.Core.Parser.Model;
using NzbDrone.Core.Profiles.Metadata;
using NzbDrone.Core.Profiles.Qualities;
using NzbDrone.Core.RootFolders;
using NzbDrone.Core.Tags;

namespace NzbDrone.Core.ImportLists.Bookwyrm
{
    // Talearr: Bookwyrm shelf import. Bookwyrm editions carry Open Library keys and ISBNs; each
    // is resolved to an Open Library work ("ol:OL123W"), which the metadata source maps to the
    // canonical book and author: the work's own key, else the edition's, else the ISBN.
    public class BookwyrmImportList : TalearrDualMediaImportListBase<BookwyrmImportListSettings>
    {
        private const int MaxPages = 200;

        private readonly IOpenLibraryListClient _client;

        public BookwyrmImportList(IOpenLibraryListClient client,
                                  IImportListStatusService importListStatusService,
                                  IConfigService configService,
                                  IParsingService parsingService,
                                  Lazy<IQualityProfileService> qualityProfileService,
                                  Lazy<IMetadataProfileService> metadataProfileService,
                                  Lazy<ITagService> tagService,
                                  IRootFolderService rootFolderService,
                                  IRootFolderSettingsResolver rootFolderSettingsResolver,
                                  Logger logger)
            : base(importListStatusService, configService, parsingService, qualityProfileService, metadataProfileService, tagService, rootFolderService, rootFolderSettingsResolver, logger)
        {
            _client = client;
        }

        public override string Name => "Bookwyrm";

        public override TimeSpan MinRefreshInterval => TimeSpan.FromHours(6);

        protected override void FetchItems(Func<ImportListItemInfo, bool> add)
        {
            var works = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var authors = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var unresolved = 0;
            var next = ShelfPageUrl(1);

            for (var page = 1; page <= MaxPages && next.IsNotNullOrWhiteSpace(); page++)
            {
                var json = _client.GetJson(next);
                var items = json?["orderedItems"] as JArray;

                if (items == null || items.Count == 0)
                {
                    return;
                }

                foreach (var edition in items)
                {
                    var item = ToItem(edition, works, authors);

                    if (item == null)
                    {
                        unresolved++;
                        continue;
                    }

                    if (!add(item))
                    {
                        return;
                    }
                }

                next = json.Value<string>("next");
            }

            if (unresolved > 0)
            {
                _logger.Info("Bookwyrm shelf '{0}': {1} books have no Open Library key or ISBN and were skipped", Settings.Shelf, unresolved);
            }
        }

        protected override ValidationFailure TestConnection()
        {
            try
            {
                var shelf = _client.GetJson(ShelfUrl());

                if (shelf == null)
                {
                    return new ValidationFailure(nameof(Settings.Shelf), "Bookwyrm has no such user or shelf, or the shelf is not public");
                }

                return null;
            }
            catch (Exception ex)
            {
                _logger.Warn(ex, "Unable to read Bookwyrm");
                return new ValidationFailure(nameof(Settings.BaseUrl), $"Unable to read the Bookwyrm shelf: {ex.Message}");
            }
        }

        private ImportListItemInfo ToItem(JToken edition, Dictionary<string, string> works, Dictionary<string, string> authors)
        {
            var work = WorkKeyFor(edition, works);

            if (work == null)
            {
                return null;
            }

            return new ImportListItemInfo
            {
                BookProviderId = "ol:" + work,
                Book = edition.Value<string>("title"),
                Author = AuthorName((edition["authors"] as JArray)?.FirstOrDefault()?.ToString(), authors),
                Isbn13 = edition.Value<string>("isbn13").IsNotNullOrWhiteSpace() ? edition.Value<string>("isbn13") : null
            };
        }

        private string WorkKeyFor(JToken edition, Dictionary<string, string> works)
        {
            var workUrl = edition.Value<string>("work");

            if (workUrl.IsNotNullOrWhiteSpace())
            {
                if (!works.TryGetValue(workUrl, out var cached))
                {
                    cached = SafeGet(workUrl, json => OpenLibraryListClient.ExtractWorkKey(json?.Value<string>("openlibraryKey")));
                    works[workUrl] = cached;
                }

                if (cached != null)
                {
                    return cached;
                }
            }

            return _client.WorkForEdition(edition.Value<string>("openlibraryKey")) ??
                   _client.WorkForIsbn(edition.Value<string>("isbn13")) ??
                   _client.WorkForIsbn(edition.Value<string>("isbn10"));
        }

        private string AuthorName(string authorUrl, Dictionary<string, string> authors)
        {
            if (authorUrl.IsNullOrWhiteSpace())
            {
                return null;
            }

            if (!authors.TryGetValue(authorUrl, out var name))
            {
                name = SafeGet(authorUrl, json => json?.Value<string>("name"));
                authors[authorUrl] = name;
            }

            return name;
        }

        private string SafeGet(string url, Func<JToken, string> read)
        {
            try
            {
                return read(_client.GetJson(url));
            }
            catch (Exception ex)
            {
                _logger.Debug(ex, "Bookwyrm lookup of {0} failed", url);
                return null;
            }
        }

        private string ShelfUrl()
        {
            return $"{Settings.BaseUrl.Trim().TrimEnd('/')}/user/{Uri.EscapeDataString(Settings.Username.Trim().TrimStart('@'))}/books/{Settings.Shelf.Trim()}.json";
        }

        private string ShelfPageUrl(int page)
        {
            return $"{Settings.BaseUrl.Trim().TrimEnd('/')}/user/{Uri.EscapeDataString(Settings.Username.Trim().TrimStart('@'))}/books/{Settings.Shelf.Trim()}?page={page}";
        }
    }
}
