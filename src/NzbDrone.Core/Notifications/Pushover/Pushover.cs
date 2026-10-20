using System.Collections.Generic;
using System.Linq;
using FluentValidation.Results;
using NzbDrone.Common.Extensions;
using NzbDrone.Core.Books;

namespace NzbDrone.Core.Notifications.Pushover
{
    public class Pushover : NotificationBase<PushoverSettings>
    {
        private readonly IPushoverProxy _proxy;

        public Pushover(IPushoverProxy proxy)
        {
            _proxy = proxy;
        }

        public override string Name => "Pushover";
        public override string Link => "https://pushover.net/";

        // Talearr: book events carry format, narrator and series lines plus the cover.
        public override void OnGrab(GrabMessage grabMessage)
        {
            var remoteBook = grabMessage.RemoteBook;
            var details = TalearrNotificationDetails.Describe(remoteBook?.Books?.FirstOrDefault(), null, grabMessage.Quality, grabMessage.Author);
            var title = TalearrNotificationDetails.IsBundleFallback(remoteBook) ? BUNDLE_GRABBED_TITLE : BOOK_GRABBED_TITLE;
            var indexer = remoteBook?.Release?.Indexer.IsNullOrWhiteSpace() == false ? $"Indexer: {remoteBook.Release.Indexer}" : null;

            _proxy.SendNotification(title, Body(grabMessage.Message, details, indexer), details?.CoverUrl, Settings);
        }

        public override void OnReleaseImport(BookDownloadMessage message)
        {
            var bookFile = message.BookFiles?.FirstOrDefault();
            var details = TalearrNotificationDetails.Describe(message.Book, bookFile?.Edition, bookFile?.Quality, message.Author);
            var title = message.OldFiles?.Any() == true ? BOOK_UPGRADED_TITLE : BOOK_DOWNLOADED_TITLE;

            _proxy.SendNotification(title, Body(message.Message, details), details?.CoverUrl, Settings);
        }

        public override void OnAuthorAdded(Author author)
        {
            _proxy.SendNotification(AUTHOR_ADDED_TITLE, author.Name, Settings);
        }

        public override void OnBookAdded(Book book)
        {
            var details = TalearrNotificationDetails.Describe(book);

            _proxy.SendNotification(BOOK_ADDED_TITLE, Body($"{book.Author.Name} - {book.Title}", details), details?.CoverUrl, Settings);
        }

        public override void OnAuthorDelete(AuthorDeleteMessage deleteMessage)
        {
            _proxy.SendNotification(AUTHOR_DELETED_TITLE, deleteMessage.Message, Settings);
        }

        public override void OnBookDelete(BookDeleteMessage deleteMessage)
        {
            _proxy.SendNotification(BOOK_DELETED_TITLE, deleteMessage.Message, Settings);
        }

        public override void OnBookFileDelete(BookFileDeleteMessage deleteMessage)
        {
            _proxy.SendNotification(BOOK_FILE_DELETED_TITLE, deleteMessage.Message, Settings);
        }

        public override void OnHealthIssue(HealthCheck.HealthCheck healthCheck)
        {
            _proxy.SendNotification(HEALTH_ISSUE_TITLE, healthCheck.Message, Settings);
        }

        // Talearr: Sonarr/Radarr "Health Restored".
        public override void OnHealthRestored(HealthCheck.HealthCheck previousCheck)
        {
            _proxy.SendNotification(HEALTH_RESTORED_TITLE, $"The following issue is now resolved: {previousCheck.Message}", Settings);
        }

        public override void OnDownloadFailure(DownloadFailedMessage message)
        {
            var details = TalearrNotificationDetails.Describe(message.Books?.FirstOrDefault(), null, message.Quality, message.Author);
            var text = details == null ? message.Message : $"{details.AuthorName} - {details.Title}\n{message.Message}";

            _proxy.SendNotification(DOWNLOAD_FAILURE_TITLE, Body(text, details, message.SourceTitle), details?.CoverUrl, Settings);
        }

        public override void OnImportFailure(BookDownloadMessage message)
        {
            var details = TalearrNotificationDetails.Describe(message.Book, null, message.RemoteBook?.ParsedBookInfo?.Quality, message.Author);
            var title = message.ManualInteractionRequired ? MANUAL_INTERACTION_REQUIRED_TITLE : IMPORT_FAILURE_TITLE;
            var reasons = message.StatusMessages?.Any() == true ? string.Join("\n", message.StatusMessages.Take(5)) : null;

            _proxy.SendNotification(title, Body(message.Message, details, reasons), details?.CoverUrl, Settings);
        }

        public override void OnApplicationUpdate(ApplicationUpdateMessage updateMessage)
        {
            _proxy.SendNotification(APPLICATION_UPDATE_TITLE, updateMessage.Message, Settings);
        }

        private static string Body(string message, TalearrBookDetails details, string extra = null)
        {
            var lines = TalearrNotificationDetails.DetailLines(details, extra);
            return lines.IsNullOrWhiteSpace() ? message : $"{message}\n{lines}";
        }

        public override ValidationResult Test()
        {
            var failures = new List<ValidationFailure>();

            failures.AddIfNotNull(_proxy.Test(Settings));

            return new ValidationResult(failures);
        }
    }
}
