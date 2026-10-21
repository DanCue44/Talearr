using System;
using System.Linq;
using System.Net;
using FluentValidation.Results;
using Newtonsoft.Json.Linq;
using NLog;
using NzbDrone.Common.Extensions;
using NzbDrone.Common.Http;
using NzbDrone.Core.Configuration;
using NzbDrone.Core.Parser;
using NzbDrone.Core.Parser.Model;
using NzbDrone.Core.Profiles.Metadata;
using NzbDrone.Core.Profiles.Qualities;
using NzbDrone.Core.RootFolders;
using NzbDrone.Core.Tags;

namespace NzbDrone.Core.ImportLists.OpenLibrary
{
    // Talearr: Open Library reading log or list. Each work becomes an "ol:OL123W" book id, which
    // the metadata source maps to the canonical book and author.
    public class OpenLibraryImportList : TalearrDualMediaImportListBase<OpenLibraryImportListSettings>
    {
        private const int PageSize = 100;
        private const int MaxPages = 100;

        private readonly IOpenLibraryListClient _client;

        public OpenLibraryImportList(IOpenLibraryListClient client,
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

        public override string Name => "Open Library";

        public override TimeSpan MinRefreshInterval => TimeSpan.FromHours(6);

        public static string ShelfSlug(OpenLibraryListSource source)
        {
            return source switch
            {
                OpenLibraryListSource.CurrentlyReading => "currently-reading",
                OpenLibraryListSource.AlreadyRead => "already-read",
                _ => "want-to-read"
            };
        }

        protected override void FetchItems(Func<ImportListItemInfo, bool> add)
        {
            if ((OpenLibraryListSource)Settings.Source == OpenLibraryListSource.List)
            {
                FetchList(add);
            }
            else
            {
                FetchReadingLog(add);
            }
        }

        protected override ValidationFailure TestConnection()
        {
            try
            {
                if ((OpenLibraryListSource)Settings.Source == OpenLibraryListSource.List)
                {
                    return _client.GetJson(ListUrl()) == null
                        ? new ValidationFailure(nameof(Settings.ListId), "Open Library has no such list for this user")
                        : null;
                }

                return _client.GetJson(ReadingLogUrl(1)) == null
                    ? new ValidationFailure(nameof(Settings.Username), "Open Library has no user with this name")
                    : null;
            }
            catch (HttpException ex) when (ex.Response.StatusCode == HttpStatusCode.Forbidden)
            {
                return new ValidationFailure(nameof(Settings.Source), "This reading log is private. Make it public in Open Library's settings (Privacy), or use a list instead.");
            }
            catch (Exception ex)
            {
                _logger.Warn(ex, "Unable to read Open Library");
                return new ValidationFailure(string.Empty, $"Unable to read Open Library: {ex.Message}");
            }
        }

        private void FetchReadingLog(Func<ImportListItemInfo, bool> add)
        {
            for (var page = 1; page <= MaxPages; page++)
            {
                var json = _client.GetJson(ReadingLogUrl(page));
                var entries = json?["reading_log_entries"] as JArray;

                if (entries == null || entries.Count == 0)
                {
                    return;
                }

                foreach (var entry in entries)
                {
                    var item = FromWork(entry["work"]);

                    if (item != null && !add(item))
                    {
                        return;
                    }
                }

                if (entries.Count < PageSize || page * PageSize >= (json.Value<int?>("numFound") ?? 0))
                {
                    return;
                }
            }
        }

        private void FetchList(Func<ImportListItemInfo, bool> add)
        {
            var json = _client.GetJson(ListUrl());
            var skipped = 0;

            foreach (var seed in json?["entries"] as JArray ?? new JArray())
            {
                var type = seed.Value<string>("type");
                var url = seed.Value<string>("url");
                string work = null;

                if (type == "work")
                {
                    work = OpenLibraryListClient.ExtractWorkKey(url);
                }
                else if (type == "edition")
                {
                    work = _client.WorkForEdition(url);
                }

                if (work == null)
                {
                    // Authors and subjects can be list seeds too; only books are imported.
                    skipped++;
                    continue;
                }

                if (!add(new ImportListItemInfo { BookProviderId = "ol:" + work, Book = seed.Value<string>("title") }))
                {
                    return;
                }
            }

            if (skipped > 0)
            {
                _logger.Debug("Open Library list {0}: skipped {1} entries that are not books", Settings.ListId, skipped);
            }
        }

        public static ImportListItemInfo FromWork(JToken work)
        {
            var key = OpenLibraryListClient.ExtractWorkKey(work?.Value<string>("key"));

            if (key == null)
            {
                return null;
            }

            return new ImportListItemInfo
            {
                BookProviderId = "ol:" + key,
                Book = work.Value<string>("title"),
                Author = (work["author_names"] as JArray)?.FirstOrDefault()?.ToString()
            };
        }

        private string ReadingLogUrl(int page)
        {
            var shelf = ShelfSlug((OpenLibraryListSource)Settings.Source);
            return $"{OpenLibraryListClient.OpenLibraryUrl}/people/{Uri.EscapeDataString(Settings.Username.Trim())}/books/{shelf}.json?page={page}&limit={PageSize}";
        }

        private string ListUrl()
        {
            return $"{OpenLibraryListClient.OpenLibraryUrl}/people/{Uri.EscapeDataString(Settings.Username.Trim())}/lists/{OpenLibraryImportListSettings.ListKey(Settings.ListId)}/seeds.json";
        }
    }
}
