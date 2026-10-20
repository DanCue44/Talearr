using System;
using System.Collections.Generic;
using FluentValidation.Results;
using NzbDrone.Core.Books;
using NzbDrone.Core.MediaFiles;
using NzbDrone.Core.ThingiProvider;

namespace NzbDrone.Core.Notifications
{
    public abstract class NotificationBase<TSettings> : INotification
        where TSettings : IProviderConfig, new()
    {
        protected const string BOOK_GRABBED_TITLE = "Book Grabbed";
        protected const string BOOK_DOWNLOADED_TITLE = "Book Downloaded";
        protected const string AUTHOR_ADDED_TITLE = "Author Added";
        protected const string BOOK_ADDED_TITLE = "Book Added";
        protected const string AUTHOR_DELETED_TITLE = "Author Deleted";
        protected const string BOOK_DELETED_TITLE = "Book Deleted";
        protected const string BOOK_FILE_DELETED_TITLE = "Book File Deleted";
        protected const string HEALTH_ISSUE_TITLE = "Health Check Failure";
        protected const string DOWNLOAD_FAILURE_TITLE = "Download Failed";
        protected const string IMPORT_FAILURE_TITLE = "Import Failed";
        protected const string BOOK_RETAGGED_TITLE = "Book File Tags Updated";
        protected const string APPLICATION_UPDATE_TITLE = "Application Updated";

        // Talearr: Sonarr/Radarr parity events.
        protected const string HEALTH_RESTORED_TITLE = "Health Check Restored";
        protected const string BOOK_UPGRADED_TITLE = "Book Upgraded";
        protected const string MANUAL_INTERACTION_REQUIRED_TITLE = "Manual Interaction Required";
        protected const string BUNDLE_GRABBED_TITLE = "Bundle Grabbed (last resort)";

        protected const string BOOK_GRABBED_TITLE_BRANDED = "Talearr - " + BOOK_GRABBED_TITLE;
        protected const string BOOK_DOWNLOADED_TITLE_BRANDED = "Talearr - " + BOOK_DOWNLOADED_TITLE;
        protected const string AUTHOR_ADDED_TITLE_BRANDED = "Talearr - " + AUTHOR_ADDED_TITLE;
        protected const string BOOK_ADDED_TITLE_BRANDED = "Talearr - " + BOOK_ADDED_TITLE;
        protected const string AUTHOR_DELETED_TITlE_BRANDED = "Talearr - " + AUTHOR_DELETED_TITLE;
        protected const string BOOK_DELETED_TITLE_BRANDED = "Talearr - " + BOOK_DELETED_TITLE;
        protected const string BOOK_FILE_DELETED_TITLE_BRANDED = "Talearr - " + BOOK_FILE_DELETED_TITLE;
        protected const string HEALTH_ISSUE_TITLE_BRANDED = "Talearr - " + HEALTH_ISSUE_TITLE;
        protected const string DOWNLOAD_FAILURE_TITLE_BRANDED = "Talearr - " + DOWNLOAD_FAILURE_TITLE;
        protected const string IMPORT_FAILURE_TITLE_BRANDED = "Talearr - " + IMPORT_FAILURE_TITLE;
        protected const string BOOK_RETAGGED_TITLE_BRANDED = "Talearr - " + BOOK_RETAGGED_TITLE;
        protected const string APPLICATION_UPDATE_TITLE_BRANDED = "Talearr - " + APPLICATION_UPDATE_TITLE;
        protected const string HEALTH_RESTORED_TITLE_BRANDED = "Talearr - " + HEALTH_RESTORED_TITLE;
        protected const string MANUAL_INTERACTION_REQUIRED_TITLE_BRANDED = "Talearr - " + MANUAL_INTERACTION_REQUIRED_TITLE;

        public abstract string Name { get; }

        public Type ConfigContract => typeof(TSettings);

        public virtual ProviderMessage Message => null;

        public IEnumerable<ProviderDefinition> DefaultDefinitions => new List<ProviderDefinition>();

        public ProviderDefinition Definition { get; set; }
        public abstract ValidationResult Test();

        public abstract string Link { get; }

        public virtual void OnGrab(GrabMessage grabMessage)
        {
        }

        public virtual void OnReleaseImport(BookDownloadMessage message)
        {
        }

        public virtual void OnRename(Author author, List<RenamedBookFile> renamedFiles)
        {
        }

        public virtual void OnAuthorAdded(Author author)
        {
        }

        public virtual void OnBookAdded(Book book)
        {
        }

        public virtual void OnAuthorDelete(AuthorDeleteMessage deleteMessage)
        {
        }

        public virtual void OnBookDelete(BookDeleteMessage deleteMessage)
        {
        }

        public virtual void OnBookFileDelete(BookFileDeleteMessage deleteMessage)
        {
        }

        public virtual void OnHealthIssue(HealthCheck.HealthCheck healthCheck)
        {
        }

        public virtual void OnHealthRestored(HealthCheck.HealthCheck previousCheck)
        {
        }

        public virtual void OnDownloadFailure(DownloadFailedMessage message)
        {
        }

        public virtual void OnImportFailure(BookDownloadMessage message)
        {
        }

        public virtual void OnBookRetag(BookRetagMessage message)
        {
        }

        public virtual void OnApplicationUpdate(ApplicationUpdateMessage updateMessage)
        {
        }

        public virtual void ProcessQueue()
        {
        }

        public virtual bool HasPendingQueue => false;

        public bool SupportsOnGrab => HasConcreteImplementation("OnGrab");
        public bool SupportsOnRename => HasConcreteImplementation("OnRename");
        public bool SupportsOnAuthorAdded => HasConcreteImplementation("OnAuthorAdded");
        public bool SupportsOnBookAdded => HasConcreteImplementation("OnBookAdded");
        public bool SupportsOnAuthorDelete => HasConcreteImplementation("OnAuthorDelete");
        public bool SupportsOnBookDelete => HasConcreteImplementation("OnBookDelete");
        public bool SupportsOnBookFileDelete => HasConcreteImplementation("OnBookFileDelete");
        public bool SupportsOnBookFileDeleteForUpgrade => SupportsOnBookFileDelete;
        public bool SupportsOnReleaseImport => HasConcreteImplementation("OnReleaseImport");
        public bool SupportsOnUpgrade => SupportsOnReleaseImport;
        public bool SupportsOnHealthIssue => HasConcreteImplementation("OnHealthIssue");
        public bool SupportsOnHealthRestored => HasConcreteImplementation("OnHealthRestored");
        public bool SupportsOnDownloadFailure => HasConcreteImplementation("OnDownloadFailure");
        public bool SupportsOnImportFailure => HasConcreteImplementation("OnImportFailure");
        public bool SupportsOnBookRetag => HasConcreteImplementation("OnBookRetag");
        public bool SupportsOnApplicationUpdate => HasConcreteImplementation("OnApplicationUpdate");

        protected TSettings Settings => (TSettings)Definition.Settings;

        public override string ToString()
        {
            return GetType().Name;
        }

        public virtual object RequestAction(string action, IDictionary<string, string> query)
        {
            return null;
        }

        private bool HasConcreteImplementation(string methodName)
        {
            var method = GetType().GetMethod(methodName);

            if (method == null)
            {
                throw new MissingMethodException(GetType().Name, Name);
            }

            return !method.DeclaringType.IsAbstract;
        }
    }
}
