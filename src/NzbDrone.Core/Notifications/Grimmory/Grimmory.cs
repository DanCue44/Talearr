using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using FluentValidation.Results;
using NLog;
using NzbDrone.Common.Extensions;
using NzbDrone.Core.Books;
using NzbDrone.Core.MediaFiles;

namespace NzbDrone.Core.Notifications.Grimmory
{
    // Talearr: Grimmory connection (Chaptarr #99). Grimmory keeps its own library of the files
    // Talearr manages; a rescan of the right library after each change keeps the two in step,
    // including deletes and upgrades, without a per-book mapping.
    public class Grimmory : NotificationBase<GrimmorySettings>
    {
        private readonly IGrimmoryProxy _proxy;
        private readonly Logger _logger;

        public Grimmory(IGrimmoryProxy proxy, Logger logger)
        {
            _proxy = proxy;
            _logger = logger;
        }

        public override string Name => "Grimmory";

        public override string Link => "https://grimmory.org/";

        public override void OnReleaseImport(BookDownloadMessage message)
        {
            Refresh(message.BookFiles?.Select(f => f.Path).Concat(message.OldFiles?.Select(f => f.Path) ?? Enumerable.Empty<string>()));
        }

        public override void OnRename(Author author, List<RenamedBookFile> renamedFiles)
        {
            Refresh(renamedFiles?.SelectMany(f => new[] { f.BookFile?.Path, f.PreviousPath }));
        }

        public override void OnBookFileDelete(BookFileDeleteMessage deleteMessage)
        {
            Refresh(new[] { deleteMessage.BookFile?.Path });
        }

        public override void OnBookDelete(BookDeleteMessage deleteMessage)
        {
            if (deleteMessage.DeletedFiles)
            {
                Refresh(deleteMessage.Book?.BookFiles?.Select(f => f.Path));
            }
        }

        public override void OnAuthorDelete(AuthorDeleteMessage deleteMessage)
        {
            if (deleteMessage.DeletedFiles)
            {
                var author = deleteMessage.Author;

                // Folders, not files: rescan whichever format libraries held this author.
                if (Settings.Ebooks)
                {
                    Refresh(new[] { author?.EbookPath ?? author?.Path }, isFolder: true);
                }

                if (Settings.Audiobooks)
                {
                    Refresh(new[] { author?.AudiobookPath ?? author?.Path }, isFolder: true);
                }
            }
        }

        public override void OnBookRetag(BookRetagMessage message)
        {
            Refresh(new[] { message.BookFile?.Path });
        }

        public override ValidationResult Test()
        {
            var failures = new List<ValidationFailure>();

            try
            {
                var libraries = _proxy.GetLibraries(Settings);

                if (libraries.Count == 0)
                {
                    failures.Add(new ValidationFailure(nameof(Settings.LibraryIds), "Grimmory has no libraries yet. Add one that points at Talearr's book folder."));
                }

                var missing = (Settings.LibraryIds ?? new List<int>()).Where(id => libraries.All(l => l.Id != id)).ToList();
                if (missing.Any())
                {
                    failures.Add(new ValidationFailure(nameof(Settings.LibraryIds), $"Grimmory has no library with id {string.Join(", ", missing)}"));
                }
            }
            catch (GrimmoryException ex)
            {
                failures.Add(new ValidationFailure(nameof(Settings.Username), ex.Message));
            }
            catch (Exception ex)
            {
                _logger.Warn(ex, "Unable to connect to Grimmory");
                failures.Add(new ValidationFailure(nameof(Settings.Url), $"Unable to connect to Grimmory: {ex.Message}"));
            }

            return new ValidationResult(failures);
        }

        public override object RequestAction(string action, IDictionary<string, string> query)
        {
            if (action != "getLibraries")
            {
                return null;
            }

            try
            {
                if (Settings.Url.IsNullOrWhiteSpace() || Settings.Username.IsNullOrWhiteSpace() || Settings.Password.IsNullOrWhiteSpace())
                {
                    return new { options = new List<object>() };
                }

                return new
                {
                    options = _proxy.GetLibraries(Settings)
                        .OrderBy(l => l.Name, StringComparer.InvariantCultureIgnoreCase)
                        .Select(l => new { Value = (int)l.Id, Name = l.Name.IsNotNullOrWhiteSpace() ? l.Name : $"Library {l.Id}" })
                        .ToList()
                };
            }
            catch (Exception ex)
            {
                _logger.Debug(ex, "Grimmory: could not list libraries");
                return new { options = new List<object>() };
            }
        }

        // The libraries to rescan for a change to these paths: the chosen ones, or every library
        // with a folder that holds one of the (mapped) paths. Empty means nothing to do.
        public static List<long> SelectLibraries(GrimmorySettings settings, IEnumerable<GrimmoryLibrary> libraries, IEnumerable<string> talearrPaths)
        {
            var libraryList = libraries?.ToList() ?? new List<GrimmoryLibrary>();

            if (settings.LibraryIds?.Any() == true)
            {
                return settings.LibraryIds.Select(id => (long)id).Where(id => libraryList.Count == 0 || libraryList.Any(l => l.Id == id)).Distinct().ToList();
            }

            var mapped = (talearrPaths ?? Enumerable.Empty<string>())
                .Where(p => p.IsNotNullOrWhiteSpace())
                .Select(p => MapPath(settings, p))
                .ToList();

            return libraryList
                .Where(l => l.Paths.Any(lp => lp.Path.IsNotNullOrWhiteSpace() && mapped.Any(p => IsUnder(p, lp.Path))))
                .Select(l => l.Id)
                .Distinct()
                .ToList();
        }

        public static string MapPath(GrimmorySettings settings, string path)
        {
            var from = settings.TalearrPath?.Trim();
            var to = settings.GrimmoryPath?.Trim();

            if (from.IsNullOrWhiteSpace() || to.IsNullOrWhiteSpace() || !IsUnder(path, from))
            {
                return Normalize(path);
            }

            return Normalize(Normalize(to) + Normalize(path).Substring(Normalize(from).Length));
        }

        public static bool WantsPath(GrimmorySettings settings, string path, bool isFolder)
        {
            if (isFolder || path.IsNullOrWhiteSpace())
            {
                return true;
            }

            var extension = Path.GetExtension(path);
            var isEbook = MediaFileExtensions.TextExtensions.Contains(extension);

            return isEbook ? settings.Ebooks : settings.Audiobooks;
        }

        private void Refresh(IEnumerable<string> paths, bool isFolder = false)
        {
            var wanted = (paths ?? Enumerable.Empty<string>())
                .Where(p => p.IsNotNullOrWhiteSpace() && WantsPath(Settings, p, isFolder))
                .Distinct()
                .ToList();

            if (wanted.Count == 0)
            {
                return;
            }

            var libraries = Settings.LibraryIds?.Any() == true ? new List<GrimmoryLibrary>() : _proxy.GetLibraries(Settings);
            var targets = SelectLibraries(Settings, libraries, wanted);

            if (targets.Count == 0)
            {
                _logger.Warn("Grimmory: no library holds '{0}'. Pick the libraries in the connection, or set the path mapping.", wanted.First());
                return;
            }

            foreach (var id in targets)
            {
                _proxy.RefreshLibrary(Settings, id);
            }
        }

        private static bool IsUnder(string path, string folder)
        {
            var p = Normalize(path);
            var f = Normalize(folder);

            return p.Equals(f, StringComparison.OrdinalIgnoreCase) ||
                   p.StartsWith(f + "/", StringComparison.OrdinalIgnoreCase);
        }

        private static string Normalize(string path)
        {
            var normalized = (path ?? string.Empty).Trim().Replace('\\', '/');
            return normalized.Length > 1 ? normalized.TrimEnd('/') : normalized;
        }
    }
}
