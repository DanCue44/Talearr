using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using FluentValidation.Results;
using NLog;
using NzbDrone.Common.Extensions;
using NzbDrone.Core.Books;
using NzbDrone.Core.MediaFiles;

namespace NzbDrone.Core.Notifications.MediaBrowser
{
    // Talearr: rescans the changed book folder in Emby or Jellyfin (Chaptarr #53).
    public class MediaBrowser : NotificationBase<MediaBrowserSettings>
    {
        private readonly IMediaBrowserProxy _proxy;
        private readonly Logger _logger;

        public MediaBrowser(IMediaBrowserProxy proxy, Logger logger)
        {
            _proxy = proxy;
            _logger = logger;
        }

        public override string Name => "Emby / Jellyfin";

        public override string Link => "https://jellyfin.org/";

        public override void OnReleaseImport(BookDownloadMessage message)
        {
            Update(message.BookFiles?.Select(f => f.Path), "Created");
        }

        public override void OnRename(Author author, List<RenamedBookFile> renamedFiles)
        {
            Update(renamedFiles?.Select(f => f.BookFile?.Path), "Modified");
        }

        public override void OnBookFileDelete(BookFileDeleteMessage deleteMessage)
        {
            Update(new[] { deleteMessage.BookFile?.Path }, "Deleted");
        }

        public override void OnBookDelete(BookDeleteMessage deleteMessage)
        {
            Update(deleteMessage.Book?.BookFiles?.Select(f => f.Path), "Deleted");
        }

        public override void OnBookRetag(BookRetagMessage message)
        {
            Update(new[] { message.BookFile?.Path }, "Modified");
        }

        public override ValidationResult Test()
        {
            var failures = new List<ValidationFailure>();

            try
            {
                var name = _proxy.GetServerName(Settings);
                _logger.Debug("Connected to Emby / Jellyfin server {0}", name ?? Settings.Host);
            }
            catch (Exception ex)
            {
                _logger.Error(ex, "Unable to connect to Emby / Jellyfin");
                failures.Add(new ValidationFailure("Host", $"Unable to connect to the server: {ex.Message}"));
            }

            return new ValidationResult(failures);
        }

        private void Update(IEnumerable<string> filePaths, string updateType)
        {
            if (!Settings.UpdateLibrary)
            {
                return;
            }

            var folders = GetFolders(filePaths, Settings);
            if (folders.Count == 0)
            {
                return;
            }

            try
            {
                _proxy.UpdateFolders(Settings, folders, updateType);
            }
            catch (Exception ex)
            {
                _logger.Warn(ex, "Unable to ask Emby / Jellyfin at {0} to rescan", Settings.Host);
            }
        }

        internal static List<string> GetFolders(IEnumerable<string> filePaths, MediaBrowserSettings settings)
        {
            return (filePaths ?? Enumerable.Empty<string>())
                .Where(p => p.IsNotNullOrWhiteSpace())
                .Select(p => Path.GetDirectoryName(p))
                .Where(p => p.IsNotNullOrWhiteSpace())
                .Select(p => MapPath(p, settings))
                .Distinct(StringComparer.Ordinal)
                .ToList();
        }

        internal static string MapPath(string path, MediaBrowserSettings settings)
        {
            if (settings.MapFrom.IsNullOrWhiteSpace() || settings.MapTo.IsNullOrWhiteSpace())
            {
                return path;
            }

            var from = settings.MapFrom.TrimEnd('/', '\\');
            if (!path.StartsWith(from, StringComparison.Ordinal))
            {
                return path;
            }

            var rest = path.Substring(from.Length).TrimStart('/', '\\');
            var to = settings.MapTo.TrimEnd('/', '\\');

            return rest.Length == 0 ? to : to + "/" + rest.Replace('\\', '/');
        }
    }
}
