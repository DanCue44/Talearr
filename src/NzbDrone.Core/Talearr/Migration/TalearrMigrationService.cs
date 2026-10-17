using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Newtonsoft.Json;
using NLog;
using NzbDrone.Common.EnvironmentInfo;
using NzbDrone.Common.Extensions;
using NzbDrone.Common.Instrumentation.Extensions;
using NzbDrone.Core.Books;
using NzbDrone.Core.Books.Commands;
using NzbDrone.Core.Books.Services;
using NzbDrone.Core.Configuration;
using NzbDrone.Core.Download;
using NzbDrone.Core.Indexers;
using NzbDrone.Core.IndexerSearch;
using NzbDrone.Core.MediaFiles;
using NzbDrone.Core.MediaFiles.Commands;
using NzbDrone.Core.Messaging.Commands;
using NzbDrone.Core.MetadataSource;
using NzbDrone.Core.Profiles.Metadata;
using NzbDrone.Core.Profiles.Qualities;
using NzbDrone.Core.RootFolders;
using NzbDrone.Core.Tags;
using NzbDrone.Core.Talearr.Wishlists;

namespace NzbDrone.Core.Talearr.Migration
{
    public interface ITalearrMigrationService
    {
        TalearrMigrationStatus Status();
        TalearrMigrationStatus StartPreview(TalearrMigrationRequest request);
        TalearrMigrationStatus StartApply();
        void Discard();
        string SaveUpload(string fileName, Stream content);
    }

    // TaleArr: guided migration from Readarr, Bookshelf or LazyLibrarian (docs/TALEARR_MIGRATION.md).
    // Preview reads the source (API or a readarr.db / backup zip), matches every monitored book to a
    // metadata book (Goodreads id, ASIN, ISBN, then title and author) and writes a dry-run plan:
    // what will be created, what already exists, what did not match and what conflicts. Apply
    // carries out that saved plan with upstream's own services: tags, root folders, download
    // clients and indexers (disabled unless asked), then authors through the pending author import
    // with only the source's monitored books monitored, then a rescan so files on disk are matched.
    // Nothing is ever deleted or changed in the source, and nothing here touches the schema: the
    // plan is a JSON file in <config>/talearr-migration/.
    public class TalearrMigrationService : ITalearrMigrationService, IExecute<TalearrMigrationCommand>
    {
        public const string FolderName = "talearr-migration";
        private const string PlanFile = "plan.json";
        private const string LastReportFile = "last-report.json";

        private readonly ITalearrMigrationApiReader _apiReader;
        private readonly ITalearrMigrationBackupReader _backupReader;
        private readonly ISearchForNewBook _bookSearch;
        private readonly ISearchForNewAuthor _authorSearch;
        private readonly IBookService _bookService;
        private readonly IAuthorService _authorService;
        private readonly IRootFolderService _rootFolderService;
        private readonly IRootFolderSettingsResolver _rootFolderSettingsResolver;
        private readonly IConfigService _configService;
        private readonly IQualityProfileService _qualityProfileService;
        private readonly IMetadataProfileService _metadataProfileService;
        private readonly ITagService _tagService;
        private readonly IDownloadClientFactory _downloadClientFactory;
        private readonly IIndexerFactory _indexerFactory;
        private readonly IPendingAuthorImportService _pendingAuthorImportService;
        private readonly IManageCommandQueue _commandQueueManager;
        private readonly IAppFolderInfo _appFolderInfo;
        private readonly Logger _logger;

        private readonly object _lock = new object();
        private TalearrMigrationStatus _status = new TalearrMigrationStatus();

        // Kept in memory only (the API key never goes to disk); a restart needs a new preview.
        private TalearrMigrationRequest _pendingRequest;

        public TalearrMigrationService(ITalearrMigrationApiReader apiReader,
                                       ITalearrMigrationBackupReader backupReader,
                                       ISearchForNewBook bookSearch,
                                       ISearchForNewAuthor authorSearch,
                                       IBookService bookService,
                                       IAuthorService authorService,
                                       IRootFolderService rootFolderService,
                                       IRootFolderSettingsResolver rootFolderSettingsResolver,
                                       IConfigService configService,
                                       IQualityProfileService qualityProfileService,
                                       IMetadataProfileService metadataProfileService,
                                       ITagService tagService,
                                       IDownloadClientFactory downloadClientFactory,
                                       IIndexerFactory indexerFactory,
                                       IPendingAuthorImportService pendingAuthorImportService,
                                       IManageCommandQueue commandQueueManager,
                                       IAppFolderInfo appFolderInfo,
                                       Logger logger)
        {
            _apiReader = apiReader;
            _backupReader = backupReader;
            _bookSearch = bookSearch;
            _authorSearch = authorSearch;
            _bookService = bookService;
            _authorService = authorService;
            _rootFolderService = rootFolderService;
            _rootFolderSettingsResolver = rootFolderSettingsResolver;
            _configService = configService;
            _qualityProfileService = qualityProfileService;
            _metadataProfileService = metadataProfileService;
            _tagService = tagService;
            _downloadClientFactory = downloadClientFactory;
            _indexerFactory = indexerFactory;
            _pendingAuthorImportService = pendingAuthorImportService;
            _commandQueueManager = commandQueueManager;
            _appFolderInfo = appFolderInfo;
            _logger = logger;
        }

        public TalearrMigrationStatus Status()
        {
            lock (_lock)
            {
                var plan = LoadPlan();
                return new TalearrMigrationStatus
                {
                    State = _status.State,
                    Message = _status.Message,
                    Done = _status.Done,
                    Total = _status.Total,
                    HasPlan = plan != null,
                    Report = plan?.Report ?? LoadLastReport()
                };
            }
        }

        public TalearrMigrationStatus StartPreview(TalearrMigrationRequest request)
        {
            request = Normalize(request);
            lock (_lock)
            {
                EnsureIdle();
                _pendingRequest = request;
                _status = new TalearrMigrationStatus { State = "queued", Message = "Reading the source" };
            }

            _commandQueueManager.Push(new TalearrMigrationCommand { Apply = false });
            return Status();
        }

        public TalearrMigrationStatus StartApply()
        {
            lock (_lock)
            {
                EnsureIdle();
                if (LoadPlan() == null)
                {
                    throw new TalearrMigrationException("Run a preview first.");
                }

                _status = new TalearrMigrationStatus { State = "queued", Message = "Applying the plan" };
            }

            _commandQueueManager.Push(new TalearrMigrationCommand { Apply = true });
            return Status();
        }

        public void Discard()
        {
            lock (_lock)
            {
                EnsureIdle();
                DeleteFile(PlanFile);
                _status = new TalearrMigrationStatus();
            }
        }

        public string SaveUpload(string fileName, Stream content)
        {
            var extension = Path.GetExtension(fileName ?? string.Empty).ToLowerInvariant();
            if (extension != ".db" && extension != ".zip")
            {
                throw new TalearrMigrationException("Upload a readarr.db or a backup .zip.");
            }

            Directory.CreateDirectory(FolderPath);
            foreach (var old in Directory.GetFiles(FolderPath, "upload.*"))
            {
                File.Delete(old);
            }

            var path = Path.Combine(FolderPath, "upload" + extension);
            using (var file = File.Create(path))
            {
                content.CopyTo(file);
            }

            return path;
        }

        public void Execute(TalearrMigrationCommand message)
        {
            try
            {
                if (message.Apply)
                {
                    var plan = LoadPlan() ?? throw new TalearrMigrationException("Run a preview first.");
                    SetStatus("running", "Applying the plan", 0, plan.Report.Authors.Count);
                    var report = Apply(plan);
                    SaveFile(LastReportFile, report);
                    DeleteFile(PlanFile);
                    SetStatus("done", "Applied: " + Describe(report), report.Authors.Count, report.Authors.Count);
                    _logger.ProgressInfo("Migration applied: {0}", Describe(report));
                }
                else
                {
                    var request = _pendingRequest ?? throw new TalearrMigrationException("Start the preview again.");
                    SetStatus("running", "Reading the source", 0, 0);
                    var snapshot = ReadSource(request);
                    var plan = BuildPlan(request, snapshot);
                    SaveFile(PlanFile, plan);
                    _pendingRequest = null;
                    SetStatus("done", "Preview ready: " + Describe(plan.Report), _status.Total, _status.Total);
                }
            }
            catch (TalearrMigrationException ex)
            {
                _logger.Warn("TaleArr: migration stopped: {0}", ex.Message);
                SetStatus("failed", ex.Message, _status.Done, _status.Total);
            }
            catch (Exception ex)
            {
                _logger.Error(ex, "TaleArr: migration failed");
                SetStatus("failed", ex.Message, _status.Done, _status.Total);
            }
        }

        internal TalearrMigrationSnapshot ReadSource(TalearrMigrationRequest request)
        {
            if (!string.IsNullOrWhiteSpace(request.BackupPath))
            {
                if (request.Source == TalearrMigrationSources.LazyLibrarian)
                {
                    throw new TalearrMigrationException("LazyLibrarian is read over its API; give its URL and API key.");
                }

                return _backupReader.Read(request.BackupPath.Trim(), request.Source);
            }

            return _apiReader.Read(request);
        }

        // The dry run. Reads TaleArr's own state and looks books up in the metadata; writes nothing.
        internal TalearrMigrationPlan BuildPlan(TalearrMigrationRequest request, TalearrMigrationSnapshot snapshot)
        {
            request = Normalize(request);
            var report = new TalearrMigrationReport
            {
                DryRun = true,
                Source = snapshot.Source,
                AppName = snapshot.AppName,
                Version = snapshot.Version,
                StartedAt = DateTime.UtcNow
            };

            var context = new PlanContext(this, request, snapshot, report);
            context.PlanTags();
            context.PlanRootFolders();
            context.PlanProfiles();
            context.PlanProviders(snapshot.DownloadClients, TalearrMigrationAreas.DownloadClient, request.ImportDownloadClients);
            context.PlanProviders(snapshot.Indexers, TalearrMigrationAreas.Indexer, request.ImportIndexers);
            context.PlanAuthors();

            report.FinishedAt = DateTime.UtcNow;

            // The API key is not needed to apply (the plan holds what was read) and is not kept.
            var saved = JsonConvert.DeserializeObject<TalearrMigrationRequest>(JsonConvert.SerializeObject(request));
            saved.ApiKey = null;
            return new TalearrMigrationPlan { Request = saved, Snapshot = snapshot, Report = report };
        }

        // Carries out a plan. Every step is idempotent: what exists by name or id is left alone.
        internal TalearrMigrationReport Apply(TalearrMigrationPlan plan)
        {
            var request = Normalize(plan.Request);
            var snapshot = plan.Snapshot;
            var report = new TalearrMigrationReport
            {
                DryRun = false,
                Source = snapshot.Source,
                AppName = snapshot.AppName,
                Version = snapshot.Version,
                StartedAt = DateTime.UtcNow,
                Authors = plan.Report.Authors
            };

            var created = plan.Report.Entries.Where(e => e.Action == TalearrMigrationActions.Create).ToList();
            bool Planned(string area, string name) => created.Any(e => e.Area == area && string.Equals(e.Name, name, StringComparison.OrdinalIgnoreCase));

            // Tags.
            var tags = _tagService.All().ToDictionary(t => t.Label, t => t.Id, StringComparer.OrdinalIgnoreCase);
            foreach (var tag in snapshot.Tags.Where(t => !string.IsNullOrWhiteSpace(t.Label)))
            {
                if (tags.ContainsKey(tag.Label))
                {
                    continue;
                }

                var added = _tagService.Add(new Tag { Label = tag.Label.ToLowerInvariant() });
                tags[tag.Label] = added.Id;
                report.Add(TalearrMigrationAreas.Tag, tag.Label, TalearrMigrationActions.Create);
            }

            HashSet<int> MapTags(IEnumerable<int> sourceIds)
            {
                var labels = snapshot.Tags.Where(t => sourceIds?.Contains(t.Id) == true).Select(t => t.Label);
                return new HashSet<int>(labels.Where(l => l != null && tags.ContainsKey(l)).Select(l => tags[l]));
            }

            // Root folders (upstream starts an initial scan of each new one, which matches the files in it).
            var newRoots = new HashSet<string>(StringComparer.Ordinal);
            var existingRoots = Safe(() => _rootFolderService.All()) ?? new List<RootFolder>();
            foreach (var root in snapshot.RootFolders)
            {
                var path = MapPath(request, root.Path);
                if (!Planned(TalearrMigrationAreas.RootFolder, path) || existingRoots.Any(r => r.Path.PathEquals(path)))
                {
                    continue;
                }

                try
                {
                    var mediaType = MediaTypeFor(request, root.Path);
                    var folder = new RootFolder
                    {
                        Name = root.Name ?? Path.GetFileName(path.TrimEnd('/', '\\')),
                        Path = path,
                        FolderType = mediaType == BookMediaType.Audiobook ? FolderType.Audiobook : FolderType.Ebook,
                        DefaultTags = MapTags(root.Tags)
                    };

                    var settings = new MediaTypeSettings
                    {
                        QualityProfileId = QualityProfileFor(snapshot, root.QualityProfileId, mediaType) ?? FirstQualityProfile(mediaType),
                        MetadataProfileId = MetadataProfileFor(snapshot, root.MetadataProfileId, mediaType) ?? FirstMetadataProfile(mediaType),
                        Monitored = true,
                        MonitorExistingMode = MonitorTypes.None,
                        MonitorNewItems = ParseNewItems(root.NewItemMonitor) ?? NewItemMonitorTypes.None,
                        Tags = folder.DefaultTags.ToList()
                    };

                    if (mediaType == BookMediaType.Audiobook)
                    {
                        folder.SetAudiobookSettings(settings);
                    }
                    else
                    {
                        folder.SetEbookSettings(settings);
                    }

                    _rootFolderService.Add(folder);
                    newRoots.Add(path);
                    report.Add(TalearrMigrationAreas.RootFolder, path, TalearrMigrationActions.Create, mediaType == BookMediaType.Audiobook ? "Audiobooks" : "Ebooks");
                }
                catch (Exception ex)
                {
                    report.Add(TalearrMigrationAreas.RootFolder, path, TalearrMigrationActions.Failed, ex.Message);
                }
            }

            // Download clients and indexers.
            var clientDefaults = request.ImportDownloadClients ? _downloadClientFactory.GetDefaultDefinitions().ToList() : new List<DownloadClientDefinition>();
            var clientNames = new HashSet<string>(_downloadClientFactory.All().Select(d => d.Name), StringComparer.OrdinalIgnoreCase);
            foreach (var source in snapshot.DownloadClients.Where(p => Planned(TalearrMigrationAreas.DownloadClient, p.Name) && !clientNames.Contains(p.Name)))
            {
                try
                {
                    var definition = clientDefaults.First(d => string.Equals(d.Implementation, source.Implementation, StringComparison.OrdinalIgnoreCase));
                    PopulateSettings(definition, source.Settings);
                    definition.Name = source.Name;
                    definition.Enable = request.EnableImportedProviders && source.Enable;
                    definition.Priority = source.Priority > 0 ? source.Priority : definition.Priority;
                    definition.Tags = MapTags(source.Tags);
                    _downloadClientFactory.Create(definition);
                    clientNames.Add(source.Name);
                    report.Add(TalearrMigrationAreas.DownloadClient, source.Name, TalearrMigrationActions.Create, definition.Enable ? "Enabled" : "Added disabled; turn it on in Settings > Download Clients");
                }
                catch (Exception ex)
                {
                    report.Add(TalearrMigrationAreas.DownloadClient, source.Name, TalearrMigrationActions.Failed, ex.Message);
                }
            }

            var indexerDefaults = request.ImportIndexers ? _indexerFactory.GetDefaultDefinitions().ToList() : new List<IndexerDefinition>();
            var indexerNames = new HashSet<string>(_indexerFactory.All().Select(d => d.Name), StringComparer.OrdinalIgnoreCase);
            foreach (var source in snapshot.Indexers.Where(p => Planned(TalearrMigrationAreas.Indexer, p.Name) && !indexerNames.Contains(p.Name)))
            {
                try
                {
                    var definition = indexerDefaults.First(d => string.Equals(d.Implementation, source.Implementation, StringComparison.OrdinalIgnoreCase));
                    PopulateSettings(definition, source.Settings);
                    definition.Name = source.Name;
                    definition.EnableRss = request.EnableImportedProviders && source.EnableRss;
                    definition.EnableAutomaticSearch = request.EnableImportedProviders && source.EnableAutomaticSearch;
                    definition.EnableInteractiveSearch = request.EnableImportedProviders && source.EnableInteractiveSearch;
                    definition.Priority = source.Priority > 0 ? source.Priority : definition.Priority;
                    definition.Tags = MapTags(source.Tags);
                    _indexerFactory.Create(definition);
                    indexerNames.Add(source.Name);
                    report.Add(TalearrMigrationAreas.Indexer, source.Name, TalearrMigrationActions.Create, definition.Enable ? "Enabled" : "Added disabled; turn it on in Settings > Indexers");
                }
                catch (Exception ex)
                {
                    report.Add(TalearrMigrationAreas.Indexer, source.Name, TalearrMigrationActions.Failed, ex.Message);
                }
            }

            // Authors and books.
            var roots = Safe(() => _rootFolderService.All()) ?? new List<RootFolder>();
            var toSearch = new List<int>();
            var queued = false;
            var scan = new HashSet<string>(StringComparer.Ordinal);
            var done = 0;

            foreach (var author in plan.Report.Authors)
            {
                SetStatus("running", "Adding " + author.Name, done++, plan.Report.Authors.Count);
                var mediaType = author.MediaType == "audiobook" ? BookMediaType.Audiobook : BookMediaType.Ebook;
                var format = author.MediaType == "audiobook" ? "audiobook" : "ebook";

                try
                {
                    var missing = new List<string>();
                    var monitored = 0;
                    foreach (var book in author.Books)
                    {
                        var local = FindLocal(book.BookProviderId, mediaType);
                        if (local == null)
                        {
                            missing.Add(book.BookProviderId);
                            continue;
                        }

                        if (!local.IsMonitoredForMediaType(mediaType))
                        {
                            _bookService.SetMonitoredForMediaType(new[] { local.Id }, format, true);
                            EnsureAuthorFormat(local.AuthorId, mediaType);
                            monitored++;
                            if (request.SearchOnAdd)
                            {
                                toSearch.Add(local.Id);
                            }
                        }
                    }

                    var authorInLibrary = FindLocalAuthor(author.AuthorProviderId) != null;
                    if (missing.Any() || !authorInLibrary)
                    {
                        var root = RootFor(roots, author.RootFolderPath, mediaType, out var settings);
                        if (root == null)
                        {
                            report.Add(TalearrMigrationAreas.Author, author.Name, TalearrMigrationActions.Failed, $"No root folder for {format}s");
                            continue;
                        }

                        var config = BuildConfig(author, mediaType, missing, root, settings, author.TagLabels.Where(tags.ContainsKey).Select(l => tags[l]), request.SearchOnAdd, snapshot.AppName);
                        var pendingId = _pendingAuthorImportService.EnqueueAsync(author.AuthorProviderId, config, config.RequestedBy).GetAwaiter().GetResult();
                        if (pendingId > 0)
                        {
                            queued = true;
                            if (!newRoots.Contains(root))
                            {
                                scan.Add(root);
                            }

                            report.Add(TalearrMigrationAreas.Author, author.Name, TalearrMigrationActions.Create, missing.Count + " book(s) to monitor" + (monitored > 0 ? $", {monitored} monitored in place" : string.Empty));
                        }
                        else
                        {
                            report.Add(TalearrMigrationAreas.Author, author.Name, TalearrMigrationActions.Failed, "The author could not be queued for import");
                        }
                    }
                    else
                    {
                        report.Add(TalearrMigrationAreas.Author, author.Name, monitored > 0 ? TalearrMigrationActions.Monitor : TalearrMigrationActions.Exists, monitored > 0 ? monitored + " book(s) monitored" : "Already in the library");
                    }
                }
                catch (Exception ex)
                {
                    _logger.Debug(ex, "TaleArr: migration could not add '{0}'", author.Name);
                    report.Add(TalearrMigrationAreas.Author, author.Name, TalearrMigrationActions.Failed, ex.Message);
                }
            }

            if (toSearch.Any())
            {
                _commandQueueManager.Push(new BookSearchCommand(toSearch.Distinct().ToList()), CommandPriority.Normal, CommandTrigger.Manual);
            }

            if (queued)
            {
                _commandQueueManager.Push(new ProcessPendingImportsCommand { BatchSize = 10, ContinueUntilEmpty = true }, CommandPriority.Normal, CommandTrigger.Manual);
            }

            // Files already on disk: each added author's folder is scanned when it is added; this
            // catches the rest (new files, authors added before the migration).
            if (scan.Any())
            {
                _commandQueueManager.Push(new RescanFoldersCommand(scan.ToList(), FilterFilesType.Known, null), CommandPriority.Normal, CommandTrigger.Manual);
            }

            report.Summary.Authors = plan.Report.Summary.Authors;
            report.Summary.Books = plan.Report.Summary.Books;
            report.Summary.BooksMonitored = plan.Report.Summary.BooksMonitored;
            report.Summary.BooksMatched = plan.Report.Summary.BooksMatched;
            report.Summary.AuthorsMatched = plan.Report.Summary.AuthorsMatched;
            report.FinishedAt = DateTime.UtcNow;
            return report;
        }

        private static MonitoringConfig BuildConfig(TalearrMigrationAuthorPlan author, BookMediaType mediaType, List<string> books, string root, ResolvedRootFolderSettings settings, IEnumerable<int> tags, bool search, string appName)
        {
            var newItems = ParseNewItems(author.MonitorNewItems) ?? NewItemMonitorTypes.None;
            var existing = books.Any() ? MonitorTypes.SpecificBook : MonitorTypes.None;
            var tagSet = new HashSet<int>(tags.Concat(settings?.Tags ?? new List<int>()));
            var config = new MonitoringConfig
            {
                CreateAudiobook = mediaType == BookMediaType.Audiobook,
                CreateEbook = mediaType == BookMediaType.Ebook,
                QueueIfUnavailable = true,
                RequestedBy = "TaleArr migration from " + (appName ?? "Readarr"),
                AuthorName = author.Name,
                SearchForMissingBooks = search,
                AudiobookMonitorExistingMode = MonitorTypes.None,
                EbookMonitorExistingMode = MonitorTypes.None,
                MonitorMode = existing,
                SpecificBookProviderIds = books.Any() ? new HashSet<string>(books, StringComparer.OrdinalIgnoreCase) : null,
                SpecificBookMediaType = mediaType,
                Tags = tagSet
            };

            var qualityProfileId = author.QualityProfileId ?? settings?.QualityProfileId;
            var metadataProfileId = author.MetadataProfileId ?? settings?.MetadataProfileId;
            if (mediaType == BookMediaType.Audiobook)
            {
                config.AudiobookRootFolderPath = root;
                config.AudiobookQualityProfileId = qualityProfileId;
                config.AudiobookMetadataProfileId = metadataProfileId;
                config.AudiobookMonitored = author.Monitored;
                config.AudiobookMonitorNewItems = newItems;
                config.AudiobookMonitorExistingMode = existing;
                config.AudiobookBooksToMonitor = books.ToList();
                config.AudiobookBooksToSearch = search ? books.ToList() : null;
                config.AudiobookTags = tagSet;
            }
            else
            {
                config.EbookRootFolderPath = root;
                config.EbookQualityProfileId = qualityProfileId;
                config.EbookMetadataProfileId = metadataProfileId;
                config.EbookMonitored = author.Monitored;
                config.EbookMonitorNewItems = newItems;
                config.EbookMonitorExistingMode = existing;
                config.EbookBooksToMonitor = books.ToList();
                config.EbookBooksToSearch = search ? books.ToList() : null;
                config.EbookTags = tagSet;
            }

            return config;
        }

        // The provider's default settings object, overwritten by the source's values (same property
        // names: Chaptarr's providers are Readarr's). Unknown source fields are ignored.
        private static void PopulateSettings<TDefinition>(TDefinition definition, string json)
            where TDefinition : NzbDrone.Core.ThingiProvider.ProviderDefinition
        {
            if (definition.Settings == null || string.IsNullOrWhiteSpace(json))
            {
                return;
            }

            JsonConvert.PopulateObject(json, definition.Settings, new JsonSerializerSettings
            {
                ObjectCreationHandling = ObjectCreationHandling.Replace,
                Error = (_, args) => args.ErrorContext.Handled = true
            });
        }

        internal static BookMediaType MediaTypeFor(TalearrMigrationRequest request, string path)
        {
            switch ((request?.MediaType ?? "auto").ToLowerInvariant())
            {
                case "audiobook":
                    return BookMediaType.Audiobook;
                case "ebook":
                    return BookMediaType.Ebook;
                default:
                    return (path ?? string.Empty).IndexOf("audio", StringComparison.OrdinalIgnoreCase) >= 0 ? BookMediaType.Audiobook : BookMediaType.Ebook;
            }
        }

        internal static string MapPath(TalearrMigrationRequest request, string path)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                return path;
            }

            foreach (var mapping in (request?.PathMappings ?? new List<TalearrMigrationPathMapping>())
                .Where(m => !string.IsNullOrWhiteSpace(m?.From) && m.To != null)
                .OrderByDescending(m => m.From.Length))
            {
                var from = mapping.From.TrimEnd('/', '\\');
                if (path.Equals(from, StringComparison.Ordinal) || path.StartsWith(from + "/", StringComparison.Ordinal) || path.StartsWith(from + "\\", StringComparison.Ordinal))
                {
                    return mapping.To.TrimEnd('/', '\\') + path.Substring(from.Length);
                }
            }

            return path;
        }

        internal static NewItemMonitorTypes? ParseNewItems(string value)
        {
            switch ((value ?? string.Empty).Trim().ToLowerInvariant())
            {
                case "all": return NewItemMonitorTypes.All;
                case "new": return NewItemMonitorTypes.New;
                case "none": return NewItemMonitorTypes.None;
                default: return null;
            }
        }

        private int? QualityProfileFor(TalearrMigrationSnapshot snapshot, int? sourceId, BookMediaType mediaType)
        {
            var name = snapshot.QualityProfiles.FirstOrDefault(p => p.Id == sourceId)?.Name;
            if (string.IsNullOrWhiteSpace(name))
            {
                return null;
            }

            var type = mediaType == BookMediaType.Audiobook ? ProfileType.Audiobook : ProfileType.Ebook;
            return (Safe(() => _qualityProfileService.All()) ?? new List<QualityProfile>())
                .Where(p => string.Equals(p.Name?.Trim(), name.Trim(), StringComparison.OrdinalIgnoreCase))
                .OrderBy(p => p.ProfileType == type ? 0 : 1)
                .Select(p => (int?)p.Id)
                .FirstOrDefault();
        }

        private int? MetadataProfileFor(TalearrMigrationSnapshot snapshot, int? sourceId, BookMediaType mediaType)
        {
            var name = snapshot.MetadataProfiles.FirstOrDefault(p => p.Id == sourceId)?.Name;
            if (string.IsNullOrWhiteSpace(name))
            {
                return null;
            }

            var type = mediaType == BookMediaType.Audiobook ? MetadataProfileType.Audiobook : MetadataProfileType.Ebook;
            return (Safe(() => _metadataProfileService.All()) ?? new List<MetadataProfile>())
                .Where(p => string.Equals(p.Name?.Trim(), name.Trim(), StringComparison.OrdinalIgnoreCase))
                .Where(p => p.ProfileType == type || p.ProfileType == MetadataProfileType.General)
                .OrderBy(p => p.ProfileType == type ? 0 : 1)
                .Select(p => (int?)p.Id)
                .FirstOrDefault();
        }

        private int? FirstQualityProfile(BookMediaType mediaType)
        {
            var type = mediaType == BookMediaType.Audiobook ? ProfileType.Audiobook : ProfileType.Ebook;
            return (Safe(() => _qualityProfileService.All()) ?? new List<QualityProfile>()).Where(p => p.ProfileType == type).OrderBy(p => p.Id).Select(p => (int?)p.Id).FirstOrDefault();
        }

        private int? FirstMetadataProfile(BookMediaType mediaType)
        {
            var type = mediaType == BookMediaType.Audiobook ? MetadataProfileType.Audiobook : MetadataProfileType.Ebook;
            return (Safe(() => _metadataProfileService.All()) ?? new List<MetadataProfile>())
                .Where(p => p.ProfileType == type || p.ProfileType == MetadataProfileType.General)
                .OrderBy(p => p.ProfileType == type ? 0 : 1).ThenBy(p => p.Id)
                .Select(p => (int?)p.Id).FirstOrDefault();
        }

        // The mapped source root folder when TaleArr has it (and it takes this media type), else the
        // media type's default root folder, else the first one set up for it.
        private string RootFor(List<RootFolder> roots, string wanted, BookMediaType mediaType, out ResolvedRootFolderSettings settings)
        {
            settings = null;
            var preferred = mediaType == BookMediaType.Audiobook
                ? Safe(() => _configService.DefaultAudiobookRootFolderPath)
                : Safe(() => _configService.DefaultEbookRootFolderPath);

            var ordered = roots
                .OrderBy(f => wanted != null && f.Path.PathEquals(wanted) ? 0 : 1)
                .ThenBy(f => preferred != null && string.Equals(f.Path?.TrimEnd('/'), preferred.TrimEnd('/'), StringComparison.Ordinal) ? 0 : 1)
                .ThenBy(f => f.Id);

            foreach (var folder in ordered)
            {
                var resolved = Safe(() => _rootFolderSettingsResolver.ResolveSettings(folder, mediaType));
                if (resolved?.IsConfigured == true && resolved.QualityProfileId > 0 && resolved.MetadataProfileId > 0)
                {
                    settings = resolved;
                    return folder.Path;
                }
            }

            return null;
        }

        private Book FindLocal(string providerId, BookMediaType mediaType)
        {
            if (string.IsNullOrWhiteSpace(providerId))
            {
                return null;
            }

            var prefix = providerId.Contains(':') ? providerId.Substring(0, providerId.IndexOf(':')) : "hc";
            return Safe(() => _bookService.FindByProviderId(prefix, providerId, mediaType));
        }

        private Author FindLocalAuthor(string providerId)
        {
            if (string.IsNullOrWhiteSpace(providerId) || !providerId.Contains(':'))
            {
                return null;
            }

            var colon = providerId.IndexOf(':');
            return Safe(() => _authorService.FindByProviderId(providerId.Substring(0, colon), providerId.Substring(colon + 1)));
        }

        private void EnsureAuthorFormat(int authorId, BookMediaType mediaType)
        {
            var author = Safe(() => _authorService.GetAuthor(authorId));
            if (author != null && !author.IsMonitoredForMediaType(mediaType == BookMediaType.Audiobook) &&
                author.ApplyMediaTypeMonitoringSettings(mediaType, true, null))
            {
                _authorService.UpdateAuthor(author);
            }
        }

        private static TalearrMigrationRequest Normalize(TalearrMigrationRequest request)
        {
            request ??= new TalearrMigrationRequest();
            request.Source = (request.Source ?? TalearrMigrationSources.Readarr).Trim().ToLowerInvariant();
            if (request.Source != TalearrMigrationSources.Readarr && request.Source != TalearrMigrationSources.Bookshelf && request.Source != TalearrMigrationSources.LazyLibrarian)
            {
                throw new TalearrMigrationException("Source must be readarr, bookshelf or lazylibrarian.");
            }

            request.PathMappings ??= new List<TalearrMigrationPathMapping>();
            return request;
        }

        private void EnsureIdle()
        {
            if (_status.State == "queued" || _status.State == "running")
            {
                throw new TalearrMigrationException("A migration step is already running.");
            }
        }

        internal void SetStatus(string state, string message, int done, int total)
        {
            lock (_lock)
            {
                _status = new TalearrMigrationStatus { State = state, Message = message, Done = done, Total = total };
            }
        }

        internal static string Describe(TalearrMigrationReport report)
        {
            var s = report.Summary;
            return $"{s.AuthorsMatched}/{s.Authors} authors and {s.BooksMatched}/{s.BooksMonitored} monitored books matched, {s.Creates} to create, {s.Unmatched} unmatched, {s.Conflicts} conflicts";
        }

        private string FolderPath => Path.Combine(_appFolderInfo.AppDataFolder, FolderName);

        private TalearrMigrationPlan LoadPlan() => LoadFile<TalearrMigrationPlan>(PlanFile);

        private TalearrMigrationReport LoadLastReport() => LoadFile<TalearrMigrationReport>(LastReportFile);

        private T LoadFile<T>(string name)
            where T : class
        {
            var path = Path.Combine(FolderPath, name);
            try
            {
                return File.Exists(path) ? JsonConvert.DeserializeObject<T>(File.ReadAllText(path)) : null;
            }
            catch (Exception ex) when (ex is IOException || ex is JsonException || ex is UnauthorizedAccessException)
            {
                _logger.Debug(ex, "TaleArr: could not read {0}", path);
                return null;
            }
        }

        private void SaveFile(string name, object value)
        {
            Directory.CreateDirectory(FolderPath);
            File.WriteAllText(Path.Combine(FolderPath, name), JsonConvert.SerializeObject(value, Formatting.Indented));
        }

        private void DeleteFile(string name)
        {
            var path = Path.Combine(FolderPath, name);
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }

        private T Safe<T>(Func<T> read)
        {
            try
            {
                return read();
            }
            catch (Exception ex)
            {
                _logger.Debug(ex, "TaleArr: migration read failed");
                return default;
            }
        }

        // One dry run: compares the source with TaleArr and matches authors and books.
        private sealed class PlanContext
        {
            private readonly TalearrMigrationService _service;
            private readonly TalearrMigrationRequest _request;
            private readonly TalearrMigrationSnapshot _snapshot;
            private readonly TalearrMigrationReport _report;
            private readonly List<RootFolder> _localRoots;
            private readonly HashSet<string> _plannedRoots = new HashSet<string>(StringComparer.Ordinal);

            public PlanContext(TalearrMigrationService service, TalearrMigrationRequest request, TalearrMigrationSnapshot snapshot, TalearrMigrationReport report)
            {
                _service = service;
                _request = request;
                _snapshot = snapshot;
                _report = report;
                _localRoots = service.Safe(() => service._rootFolderService.All()) ?? new List<RootFolder>();
            }

            public void PlanTags()
            {
                var local = new HashSet<string>((_service.Safe(() => _service._tagService.All()) ?? new List<Tag>()).Select(t => t.Label), StringComparer.OrdinalIgnoreCase);
                foreach (var tag in _snapshot.Tags.Where(t => !string.IsNullOrWhiteSpace(t.Label)))
                {
                    _report.Add(TalearrMigrationAreas.Tag, tag.Label, local.Contains(tag.Label) ? TalearrMigrationActions.Exists : TalearrMigrationActions.Create);
                }
            }

            public void PlanRootFolders()
            {
                foreach (var root in _snapshot.RootFolders.Where(r => !string.IsNullOrWhiteSpace(r.Path)))
                {
                    var path = MapPath(_request, root.Path);
                    var mediaType = MediaTypeFor(_request, root.Path);
                    var label = mediaType == BookMediaType.Audiobook ? "audiobooks" : "ebooks";
                    var existing = _localRoots.FirstOrDefault(r => r.Path.PathEquals(path));
                    if (existing != null)
                    {
                        var fits = existing.FolderType == FolderType.Mixed ||
                                   (existing.FolderType == FolderType.Audiobook) == (mediaType == BookMediaType.Audiobook);
                        _report.Add(TalearrMigrationAreas.RootFolder, path, fits ? TalearrMigrationActions.Exists : TalearrMigrationActions.Conflict,
                            fits ? "Already a root folder" : $"Already a root folder for {(existing.FolderType == FolderType.Audiobook ? "audiobooks" : "ebooks")}, the source keeps {label} there");
                    }
                    else if (!_request.ImportRootFolders)
                    {
                        _report.Add(TalearrMigrationAreas.RootFolder, path, TalearrMigrationActions.Skip, "Root folders are not imported");
                    }
                    else if (Directory.Exists(path))
                    {
                        _plannedRoots.Add(path);
                        _report.Add(TalearrMigrationAreas.RootFolder, path, TalearrMigrationActions.Create, $"For {label}; its files are scanned and matched when it is added");
                    }
                    else
                    {
                        _report.Add(TalearrMigrationAreas.RootFolder, path, TalearrMigrationActions.Conflict,
                            "Not found on this server. Mount the library into TaleArr or add a path mapping (source path to TaleArr path).");
                    }
                }
            }

            public void PlanProfiles()
            {
                var used = _snapshot.Authors.Select(a => a.QualityProfileId).Concat(_snapshot.RootFolders.Select(r => r.QualityProfileId ?? 0)).ToHashSet();
                foreach (var profile in _snapshot.QualityProfiles.Where(p => used.Contains(p.Id)))
                {
                    var mediaType = MediaTypeOfProfile(a => a.QualityProfileId == profile.Id);
                    var match = _service.QualityProfileFor(_snapshot, profile.Id, mediaType);
                    _report.Add(TalearrMigrationAreas.QualityProfile, profile.Name, match.HasValue ? TalearrMigrationActions.Map : TalearrMigrationActions.Unmatched,
                        match.HasValue ? "Uses the TaleArr profile with the same name" : "No TaleArr profile with this name (qualities differ from Readarr's); the root folder's profile is used");
                }

                var usedMetadata = _snapshot.Authors.Select(a => a.MetadataProfileId).Concat(_snapshot.RootFolders.Select(r => r.MetadataProfileId ?? 0)).ToHashSet();
                foreach (var profile in _snapshot.MetadataProfiles.Where(p => usedMetadata.Contains(p.Id)))
                {
                    var mediaType = MediaTypeOfProfile(a => a.MetadataProfileId == profile.Id);
                    var match = _service.MetadataProfileFor(_snapshot, profile.Id, mediaType);
                    _report.Add(TalearrMigrationAreas.MetadataProfile, profile.Name, match.HasValue ? TalearrMigrationActions.Map : TalearrMigrationActions.Unmatched,
                        match.HasValue ? "Uses the TaleArr profile with the same name" : "No TaleArr profile with this name; the root folder's profile is used");
                }
            }

            private BookMediaType MediaTypeOfProfile(Func<TalearrSourceAuthor, bool> uses)
            {
                var author = _snapshot.Authors.FirstOrDefault(uses);
                return MediaTypeFor(_request, author == null ? null : SourceRoot(author));
            }

            public void PlanProviders(List<TalearrSourceProvider> providers, string area, bool import)
            {
                var isClient = area == TalearrMigrationAreas.DownloadClient;
                var localNames = new HashSet<string>(isClient
                    ? (_service.Safe(() => _service._downloadClientFactory.All()) ?? new List<DownloadClientDefinition>()).Select(d => d.Name)
                    : (_service.Safe(() => _service._indexerFactory.All()) ?? new List<IndexerDefinition>()).Select(d => d.Name), StringComparer.OrdinalIgnoreCase);
                var implementations = new HashSet<string>(isClient
                    ? (_service.Safe(() => _service._downloadClientFactory.GetDefaultDefinitions().ToList()) ?? new List<DownloadClientDefinition>()).Select(d => d.Implementation)
                    : (_service.Safe(() => _service._indexerFactory.GetDefaultDefinitions().ToList()) ?? new List<IndexerDefinition>()).Select(d => d.Implementation), StringComparer.OrdinalIgnoreCase);

                foreach (var provider in providers.Where(p => !string.IsNullOrWhiteSpace(p.Name)))
                {
                    if (!import)
                    {
                        _report.Add(area, provider.Name, TalearrMigrationActions.Skip, "Not imported");
                    }
                    else if (localNames.Contains(provider.Name))
                    {
                        _report.Add(area, provider.Name, TalearrMigrationActions.Exists, "TaleArr already has one with this name; left as it is");
                    }
                    else if (!isClient && (provider.Name.IndexOf("prowlarr", StringComparison.OrdinalIgnoreCase) >= 0 ||
                                       (provider.Settings ?? string.Empty).IndexOf(":9696", StringComparison.Ordinal) >= 0))
                    {
                        _report.Add(area, provider.Name, TalearrMigrationActions.Skip, "Synced by Prowlarr: add TaleArr as an app in Prowlarr instead");
                    }
                    else if (!implementations.Contains(provider.Implementation ?? string.Empty))
                    {
                        _report.Add(area, provider.Name, TalearrMigrationActions.Conflict, $"TaleArr has no '{provider.Implementation}' {(isClient ? "download client" : "indexer")}");
                    }
                    else
                    {
                        _report.Add(area, provider.Name, TalearrMigrationActions.Create,
                            provider.Implementation + (_request.EnableImportedProviders ? string.Empty : ", added disabled"));
                    }
                }
            }

            private string SourceRoot(TalearrSourceAuthor author)
            {
                if (!string.IsNullOrWhiteSpace(author.RootFolderPath))
                {
                    return author.RootFolderPath;
                }

                return _snapshot.RootFolders
                    .Where(r => !string.IsNullOrWhiteSpace(r.Path) && author.Path != null && (author.Path.StartsWith(r.Path.TrimEnd('/', '\\') + "/", StringComparison.Ordinal) || author.Path.StartsWith(r.Path.TrimEnd('/', '\\') + "\\", StringComparison.Ordinal)))
                    .OrderByDescending(r => r.Path.Length)
                    .Select(r => r.Path)
                    .FirstOrDefault() ?? author.Path;
            }

            public void PlanAuthors()
            {
                var booksByAuthor = _snapshot.Books.GroupBy(b => b.AuthorId).ToDictionary(g => g.Key, g => g.ToList());
                var isLazyLibrarian = _snapshot.Source == TalearrMigrationSources.LazyLibrarian;
                var work = new List<(TalearrSourceAuthor Author, BookMediaType MediaType, List<TalearrSourceBook> Wanted, List<TalearrSourceBook> All)>();

                foreach (var author in _snapshot.Authors)
                {
                    var books = booksByAuthor.TryGetValue(author.Id, out var list) ? list : new List<TalearrSourceBook>();
                    if (isLazyLibrarian)
                    {
                        var ebooks = books.Where(b => b.Monitored).ToList();
                        var audio = books.Where(b => b.AudiobookMonitored == true).ToList();
                        if (_request.MediaType != "audiobook" && (ebooks.Any() || !audio.Any()))
                        {
                            work.Add((author, BookMediaType.Ebook, ebooks, books));
                        }

                        if (_request.MediaType != "ebook" && audio.Any())
                        {
                            work.Add((author, BookMediaType.Audiobook, audio, books));
                        }
                    }
                    else
                    {
                        work.Add((author, MediaTypeFor(_request, SourceRoot(author)), books.Where(b => b.Monitored).ToList(), books));
                    }
                }

                _report.Summary.Authors = _snapshot.Authors.Count;
                _report.Summary.Books = _snapshot.Books.Count;
                _report.Summary.BooksMonitored = work.Sum(w => w.Wanted.Count);

                var total = work.Sum(w => Math.Max(1, w.Wanted.Count));
                var done = 0;
                var cache = new Dictionary<int, Book>();
                var plans = new Dictionary<string, TalearrMigrationAuthorPlan>(StringComparer.OrdinalIgnoreCase);
                var matchedAuthors = new HashSet<int>();

                foreach (var (author, mediaType, wanted, all) in work)
                {
                    var format = mediaType == BookMediaType.Audiobook ? "audiobook" : "ebook";
                    var matches = new List<(TalearrSourceBook Source, Book Match)>();
                    foreach (var book in wanted)
                    {
                        _service.SetStatus("running", "Matching " + author.Name, done++, total);
                        var match = Lookup(cache, book, author.Name);
                        if (match == null || TalearrWishlistService.BookProviderId(match) == null)
                        {
                            _report.Add(TalearrMigrationAreas.Book, $"{book.Title} ({author.Name})", TalearrMigrationActions.Unmatched, "No matching book in the metadata; add it by hand after the migration");
                            continue;
                        }

                        matches.Add((book, match));
                    }

                    var authorProviderId = matches
                        .Select(m => TalearrWishlistService.AuthorProviderId(m.Match.Author))
                        .Where(id => id != null)
                        .GroupBy(id => id, StringComparer.OrdinalIgnoreCase)
                        .OrderByDescending(g => g.Count())
                        .Select(g => g.Key)
                        .FirstOrDefault();

                    if (authorProviderId == null)
                    {
                        if (!wanted.Any())
                        {
                            done++;
                        }

                        authorProviderId = LookupAuthor(cache, author, all);
                    }

                    if (authorProviderId == null)
                    {
                        _report.Add(TalearrMigrationAreas.Author, author.Name, TalearrMigrationActions.Unmatched, "Not found in the metadata; add the author by hand");
                        continue;
                    }

                    matchedAuthors.Add(author.Id);
                    var sourceRoot = SourceRoot(author);
                    foreach (var group in matches.GroupBy(m => TalearrWishlistService.AuthorProviderId(m.Match.Author) ?? authorProviderId, StringComparer.OrdinalIgnoreCase)
                                                 .DefaultIfEmpty(null))
                    {
                        var providerId = group?.Key ?? authorProviderId;
                        var key = providerId + "|" + format;
                        if (!plans.TryGetValue(key, out var plan))
                        {
                            plan = new TalearrMigrationAuthorPlan
                            {
                                SourceAuthorId = author.Id,
                                Name = providerId.Equals(authorProviderId, StringComparison.OrdinalIgnoreCase) ? author.Name : group.First().Match.Author?.Name ?? author.Name,
                                AuthorProviderId = providerId,
                                MediaType = format,
                                RootFolderPath = MapPath(_request, sourceRoot),
                                Monitored = author.Monitored,
                                MonitorNewItems = author.MonitorNewItems,
                                QualityProfileId = _service.QualityProfileFor(_snapshot, author.QualityProfileId, mediaType),
                                MetadataProfileId = _service.MetadataProfileFor(_snapshot, author.MetadataProfileId, mediaType),
                                TagLabels = _snapshot.Tags.Where(t => author.Tags.Contains(t.Id)).Select(t => t.Label).ToList()
                            };
                            plans[key] = plan;
                            _report.Authors.Add(plan);
                        }

                        foreach (var (source, match) in group ?? Enumerable.Empty<(TalearrSourceBook, Book)>())
                        {
                            var bookProviderId = TalearrWishlistService.BookProviderId(match);
                            if (plan.Books.Any(b => string.Equals(b.BookProviderId, bookProviderId, StringComparison.OrdinalIgnoreCase)))
                            {
                                continue;
                            }

                            plan.Books.Add(new TalearrMigrationBookPlan { Title = match.Title ?? source.Title, BookProviderId = bookProviderId, MediaType = format });
                            _report.Summary.BooksMatched++;
                            var local = _service.FindLocal(bookProviderId, mediaType);
                            var name = $"{source.Title} ({author.Name})";
                            if (local == null)
                            {
                                _report.Add(TalearrMigrationAreas.Book, name, TalearrMigrationActions.Create, $"Monitored {format}, matched to '{match.Title}' ({bookProviderId})");
                            }
                            else if (local.IsMonitoredForMediaType(mediaType))
                            {
                                _report.Add(TalearrMigrationAreas.Book, name, TalearrMigrationActions.Exists, "Already monitored in TaleArr");
                            }
                            else
                            {
                                _report.Add(TalearrMigrationAreas.Book, name, TalearrMigrationActions.Monitor, $"In TaleArr, the {format} gets monitored");
                            }
                        }
                    }

                    var inLibrary = _service.FindLocalAuthor(authorProviderId) != null;
                    var root = MapPath(_request, sourceRoot);
                    var rootKnown = root != null && (_plannedRoots.Contains(root) || _localRoots.Any(r => r.Path.PathEquals(root)));
                    var detail = (inLibrary ? "Already in TaleArr" : $"Added for {format}s ({authorProviderId})") +
                                 (rootKnown || root == null ? string.Empty : $"; its folder '{root}' is not a TaleArr root folder, so the default {format} root folder is used and files there are not matched");
                    _report.Add(TalearrMigrationAreas.Author, author.Name, inLibrary ? TalearrMigrationActions.Exists : TalearrMigrationActions.Create, detail);
                }

                _report.Summary.AuthorsMatched = matchedAuthors.Count;
            }

            // Goodreads edition id (Readarr and LazyLibrarian; Bookshelf ids are Hardcover's), then
            // ASIN, ISBN, then title and author. An identifier hit still has to fit the author.
            private Book Lookup(Dictionary<int, Book> cache, TalearrSourceBook book, string authorName)
            {
                if (cache.TryGetValue(book.Id, out var cached))
                {
                    return cached;
                }

                var row = new TalearrWishlistRow { Title = book.Title, Author = authorName, Isbn = book.Isbn13, Asin = book.Asin };
                Book match = null;
                try
                {
                    if (_snapshot.Source != TalearrMigrationSources.Bookshelf && int.TryParse(book.ForeignEditionId, out var goodreadsId) && goodreadsId > 0)
                    {
                        match = TalearrWishlistService.Pick(_service._bookSearch.SearchByGoodreadsBookId(goodreadsId, false), row, trustIdentifier: true);
                    }

                    if (match == null && !string.IsNullOrWhiteSpace(book.Asin))
                    {
                        match = TalearrWishlistService.Pick(_service._bookSearch.SearchByAsin(book.Asin), row, trustIdentifier: true);
                    }

                    if (match == null && !string.IsNullOrWhiteSpace(book.Isbn13))
                    {
                        match = TalearrWishlistService.Pick(_service._bookSearch.SearchByIsbn(book.Isbn13), row, trustIdentifier: true);
                    }

                    if (match == null && !string.IsNullOrWhiteSpace(book.Title))
                    {
                        match = TalearrWishlistService.Pick(_service._bookSearch.SearchForNewBook(book.Title, authorName, false), row, trustIdentifier: false);
                    }
                }
                catch (Exception ex)
                {
                    _service._logger.Debug(ex, "TaleArr: migration lookup failed for '{0}'", book.Title);
                }

                cache[book.Id] = match;
                return match;
            }

            // An author without monitored books: an exact name match in the author search, else
            // the author of their first book with files (or first book).
            private string LookupAuthor(Dictionary<int, Book> cache, TalearrSourceAuthor author, List<TalearrSourceBook> books)
            {
                var wanted = TalearrWishlistCsv.Simplify(author.Name);
                try
                {
                    var found = (_service._authorSearch.SearchForNewAuthor(author.Name) ?? new List<Author>())
                        .FirstOrDefault(a => a != null && TalearrWishlistCsv.Simplify(a.Name) == wanted);
                    var id = TalearrWishlistService.AuthorProviderId(found);
                    if (id != null)
                    {
                        return id;
                    }
                }
                catch (Exception ex)
                {
                    _service._logger.Debug(ex, "TaleArr: migration author lookup failed for '{0}'", author.Name);
                }

                var probe = books.OrderByDescending(b => b.HasFile).FirstOrDefault();
                return probe == null ? null : TalearrWishlistService.AuthorProviderId(Lookup(cache, probe, author.Name)?.Author);
            }
        }
    }
}
