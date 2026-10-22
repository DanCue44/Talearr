using System;
using System.Linq;
using NLog;
using NzbDrone.Core.Notifications;

namespace NzbDrone.Core.Talearr.Comics
{
    // A reader that can rescan a comics folder (Kavita, Komga).
    public interface IComicLibraryScanner
    {
        // newFolder: the series folder did not exist before this change (a reader may not know it yet)
        void ScanComicFolder(string folder, bool newFolder = false);
    }

    public interface IComicLibraryNotifier
    {
        void FolderChanged(ComicSeries series, string folder, bool newFolder = false);
    }

    public static class ComicReaderPaths
    {
        // The folder as the reader sees it, when the reader mounts the library somewhere else.
        public static string Map(string talearrPath, string readerPath, string path)
        {
            if (string.IsNullOrWhiteSpace(talearrPath) || string.IsNullOrWhiteSpace(readerPath) || !IsUnder(path, talearrPath))
            {
                return Normalize(path);
            }

            return Normalize(Normalize(readerPath) + Normalize(path).Substring(Normalize(talearrPath).Length));
        }

        public static bool IsUnder(string path, string folder)
        {
            if (string.IsNullOrWhiteSpace(path) || string.IsNullOrWhiteSpace(folder))
            {
                return false;
            }

            var p = Normalize(path);
            var f = Normalize(folder);
            return p.Equals(f, StringComparison.OrdinalIgnoreCase) || p.StartsWith(f + "/", StringComparison.OrdinalIgnoreCase);
        }

        private static string Normalize(string path)
        {
            return (path ?? string.Empty).Replace('\\', '/').TrimEnd('/');
        }
    }

    // Talearr: tells every Kavita and Komga connection (Settings > Connect) that a series folder
    // changed. Connections do not need any trigger switched on for comics.
    public class ComicLibraryNotifier : IComicLibraryNotifier
    {
        private readonly INotificationFactory _notificationFactory;
        private readonly Logger _logger;

        public ComicLibraryNotifier(INotificationFactory notificationFactory, Logger logger)
        {
            _notificationFactory = notificationFactory;
            _logger = logger;
        }

        public void FolderChanged(ComicSeries series, string folder, bool newFolder = false)
        {
            foreach (var definition in _notificationFactory.All().Where(d => d.Implementation == "Kavita" || d.Implementation == "Komga"))
            {
                try
                {
                    if (_notificationFactory.GetInstance(definition) is IComicLibraryScanner scanner)
                    {
                        scanner.ScanComicFolder(folder, newFolder);
                    }
                }
                catch (Exception ex)
                {
                    _logger.Warn(ex, "Comics: {0} could not rescan {1}", definition.Name, folder);
                }
            }
        }
    }
}
