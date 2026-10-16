using NLog;
using NzbDrone.Core.Books;
using NzbDrone.Core.Configuration;
using NzbDrone.Core.IndexerSearch;
using NzbDrone.Core.Messaging;
using NzbDrone.Core.Messaging.Commands;
using NzbDrone.Core.Messaging.Events;
using NzbDrone.Core.Parser.Model;
using NzbDrone.Core.Talearr.GrabPolicy;

namespace NzbDrone.Core.Download
{
    public class RedownloadFailedDownloadService : IHandle<DownloadFailedEvent>
    {
        private readonly IConfigService _configService;
        private readonly IBookService _bookService;
        private readonly IManageCommandQueue _commandQueueManager;
        private readonly Logger _logger;
        private readonly ITalearrRetryService _talearrRetryService;

        public RedownloadFailedDownloadService(IConfigService configService,
                                               IBookService bookService,
                                               IManageCommandQueue commandQueueManager,
                                               Logger logger,
                                               ITalearrRetryService talearrRetryService = null)
        {
            _configService = configService;
            _bookService = bookService;
            _commandQueueManager = commandQueueManager;
            _logger = logger;
            _talearrRetryService = talearrRetryService;
        }

        [EventHandleOrder(EventHandleOrder.Last)]
        public void Handle(DownloadFailedEvent message)
        {
            if (message.SkipRedownload)
            {
                _logger.Debug("Skip redownloading requested by user");
                _talearrRetryService?.Annotate(message, "release blocklisted, not searching again (skipped by user)");
                return;
            }

            if (!_configService.AutoRedownloadFailed)
            {
                _logger.Debug("Auto redownloading failed books is disabled");
                _talearrRetryService?.Annotate(message, "release blocklisted, automatic redownload is off");
                return;
            }

            if (message.ReleaseSource == ReleaseSourceType.InteractiveSearch && !_configService.AutoRedownloadFailedFromInteractiveSearch)
            {
                _logger.Debug("Auto redownloading failed books from interactive search is disabled");
                _talearrRetryService?.Annotate(message, "release blocklisted, automatic redownload for interactive grabs is off");
                return;
            }

            // TaleArr: cap automatic retries per book, retry user grabs as user searches (so an
            // unmonitored book or a delay profile does not silently block the replacement), and
            // record the step on the DownloadFailed history row.
            var retry = _talearrRetryService?.Evaluate(message);
            if (retry != null && !retry.Allowed)
            {
                _logger.Warn("Not searching again for {0}: {1}", message.SourceTitle, retry.Note);
                _talearrRetryService.Annotate(message, retry.Note);
                return;
            }

            var trigger = retry?.Trigger ?? CommandTrigger.Unspecified;
            _talearrRetryService?.Annotate(message, retry?.Note);

            if (message.BookIds.Count == 1)
            {
                _logger.Debug("Failed download only contains one book, searching again");

                _commandQueueManager.Push(new BookSearchCommand(message.BookIds), CommandPriority.Normal, trigger);

                return;
            }

            var booksInAuthor = _bookService.GetBooksByAuthor(message.AuthorId);

            if (message.BookIds.Count == booksInAuthor.Count)
            {
                _logger.Debug("Failed download was entire author, searching again");

                _commandQueueManager.Push(new AuthorSearchCommand
                {
                    AuthorId = message.AuthorId
                }, CommandPriority.Normal, trigger);

                return;
            }

            _logger.Debug("Failed download contains multiple books, searching again");

            _commandQueueManager.Push(new BookSearchCommand(message.BookIds), CommandPriority.Normal, trigger);
        }
    }
}
