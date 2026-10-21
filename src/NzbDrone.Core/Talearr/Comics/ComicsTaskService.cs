using NLog;
using NzbDrone.Common.Instrumentation.Extensions;
using NzbDrone.Core.Messaging.Commands;
using NzbDrone.Core.Talearr.Comics.Import;
using NzbDrone.Core.Talearr.Comics.Search;

namespace NzbDrone.Core.Talearr.Comics
{
    // Talearr: runs the comics commands (import a finished download, refresh series, search for
    // missing volumes). The scheduled ones do nothing until a comic series exists.
    public class ComicsTaskService : IExecute<ImportComicDownloadCommand>,
                                     IExecute<RefreshComicSeriesCommand>,
                                     IExecute<SearchMissingComicsCommand>
    {
        private readonly ITalearrComicsDatabase _database;
        private readonly IComicImportService _importService;
        private readonly IComicSeriesService _seriesService;
        private readonly IComicSearchService _searchService;
        private readonly Logger _logger;

        public ComicsTaskService(ITalearrComicsDatabase database,
                                 IComicImportService importService,
                                 IComicSeriesService seriesService,
                                 IComicSearchService searchService,
                                 Logger logger)
        {
            _database = database;
            _importService = importService;
            _seriesService = seriesService;
            _searchService = searchService;
            _logger = logger;
        }

        public void Execute(ImportComicDownloadCommand message)
        {
            var result = _importService.ImportDownload(message.DownloadId);
            _logger.ProgressInfo("Comics: imported {0} file(s)", result.Imported.Count);
        }

        public void Execute(RefreshComicSeriesCommand message)
        {
            if (message.SeriesId.HasValue)
            {
                _seriesService.Refresh(message.SeriesId.Value);
                return;
            }

            if (ComicsInUse())
            {
                _seriesService.RefreshAll();
            }
        }

        public void Execute(SearchMissingComicsCommand message)
        {
            if (message.SeriesId.HasValue)
            {
                var result = _searchService.AutomaticSearch(message.SeriesId.Value);
                _logger.ProgressInfo("Comics: sent {0} release(s) to the download client", result.Grabbed);
                return;
            }

            if (ComicsInUse())
            {
                var grabbed = _searchService.SearchAllMissing();
                _logger.ProgressInfo("Comics: sent {0} release(s) to the download client", grabbed);
            }
        }

        // No comics database means nobody has used comics: scheduled runs must not create it.
        private bool ComicsInUse()
        {
            return System.IO.File.Exists(_database.DatabasePath);
        }
    }
}
