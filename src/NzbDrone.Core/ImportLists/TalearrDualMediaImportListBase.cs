using System;
using System.Collections.Generic;
using FluentValidation.Results;
using NLog;
using NzbDrone.Common.Extensions;
using NzbDrone.Core.Configuration;
using NzbDrone.Core.ImportLists.Goodreads;
using NzbDrone.Core.Parser;
using NzbDrone.Core.Parser.Model;
using NzbDrone.Core.Profiles.Metadata;
using NzbDrone.Core.Profiles.Qualities;
using NzbDrone.Core.RootFolders;
using NzbDrone.Core.Tags;

namespace NzbDrone.Core.ImportLists
{
    // Talearr: shared plumbing for book lists that use the Goodreads-style dual media settings
    // (monitor audiobooks and/or ebooks, per-format profiles, root folders and tags), so the
    // sync treats them exactly like the Goodreads lists.
    public abstract class TalearrDualMediaImportListBase<TSettings> : ImportListBase<TSettings>
        where TSettings : IGoodreadsDualMediaImportListSettings, new()
    {
        private readonly Lazy<IQualityProfileService> _qualityProfileService;
        private readonly Lazy<IMetadataProfileService> _metadataProfileService;
        private readonly Lazy<ITagService> _tagService;
        private readonly IRootFolderService _rootFolderService;
        private readonly IRootFolderSettingsResolver _rootFolderSettingsResolver;

        protected TalearrDualMediaImportListBase(IImportListStatusService importListStatusService,
                                                 IConfigService configService,
                                                 IParsingService parsingService,
                                                 Lazy<IQualityProfileService> qualityProfileService,
                                                 Lazy<IMetadataProfileService> metadataProfileService,
                                                 Lazy<ITagService> tagService,
                                                 IRootFolderService rootFolderService,
                                                 IRootFolderSettingsResolver rootFolderSettingsResolver,
                                                 Logger logger)
            : base(importListStatusService, configService, parsingService, logger)
        {
            _qualityProfileService = qualityProfileService;
            _metadataProfileService = metadataProfileService;
            _tagService = tagService;
            _rootFolderService = rootFolderService;
            _rootFolderSettingsResolver = rootFolderSettingsResolver;
        }

        public override ImportListType ListType => ImportListType.Other;

        // Adds each item through the callback; the callback returns false once the import limit
        // is reached, and the list should stop fetching.
        protected abstract void FetchItems(Func<ImportListItemInfo, bool> add);

        // Null when the list can be read, else the reason it cannot.
        protected abstract ValidationFailure TestConnection();

        public override IList<ImportListItemInfo> Fetch()
        {
            var result = new List<ImportListItemInfo>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            try
            {
                FetchItems(item =>
                {
                    GoodreadsImportListLimit.TryAdd(result, item, seen, Settings.ImportLimit);
                    return !GoodreadsImportListLimit.HasReached(result.Count, Settings.ImportLimit);
                });

                _importListStatusService.RecordSuccess(Definition.Id);
            }
            catch (Exception ex)
            {
                _logger.Warn(ex, "{0} import list '{1}' could not be read", Name, Definition?.Name);
                _importListStatusService.RecordFailure(Definition.Id);
            }

            return CleanupListItems(result);
        }

        protected override void Test(List<ValidationFailure> failures)
        {
            failures.AddIfNotNull(TestConnection());
            failures.AddRange(GoodreadsDualMediaImportListActions.TestRootFolderConfig(Settings, _rootFolderService, _rootFolderSettingsResolver));
        }

        public override object RequestAction(string action, IDictionary<string, string> query)
        {
            return GoodreadsDualMediaImportListActions.HandleRequestAction(action, _qualityProfileService, _metadataProfileService, _tagService)
                ?? base.RequestAction(action, query);
        }
    }
}
