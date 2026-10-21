using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using NLog;
using NzbDrone.Common.Disk;
using NzbDrone.Common.Extensions;
using NzbDrone.Core.Books;
using NzbDrone.Core.Configuration;
using NzbDrone.Core.Lifecycle;
using NzbDrone.Core.Messaging.Events;
using NzbDrone.Core.RootFolders;

namespace NzbDrone.Core.Talearr.ReadOnlyLibrary
{
    public interface ITalearrReadOnlyLibrary
    {
        IReadOnlyList<string> ReadOnlyRoots { get; }

        // True for a read-only root folder itself or anything inside it.
        bool IsReadOnly(string path);

        void SetReadOnly(string rootFolderPath, bool readOnly);

        // A path for new files: unchanged when writable; for a read-only root, the same relative
        // place under a writable root folder for that media type.
        string RedirectForWrite(string path, BookMediaType mediaType);
    }

    // Talearr: read-only library folders (Chaptarr #326). A root folder marked read-only is scanned
    // and its files count as present, but Talearr never creates, renames, moves, retags or deletes
    // anything in it; new downloads go to a writable root folder of the same type instead.
    //
    // The list of read-only root paths is one Config row (TalearrReadOnlyRootFolders), so the
    // RootFolders table keeps Chaptarr's shape. Chaptarr ignores the row: after a rollback to
    // Chaptarr the folders are ordinary root folders again.
    public class TalearrReadOnlyLibraryService : ITalearrReadOnlyLibrary, IHandle<ApplicationStartedEvent>
    {
        public const string ConfigKey = "TalearrReadOnlyRootFolders";

        private readonly IConfigRepository _configRepository;
        private readonly IRootFolderService _rootFolderService;
        private readonly IConfigService _configService;
        private readonly Logger _logger;
        private readonly object _lock = new object();
        private bool _loaded;

        public TalearrReadOnlyLibraryService(IConfigRepository configRepository,
                                             IRootFolderService rootFolderService,
                                             IConfigService configService,
                                             Logger logger)
        {
            _configRepository = configRepository;
            _rootFolderService = rootFolderService;
            _configService = configService;
            _logger = logger;
        }

        public IReadOnlyList<string> ReadOnlyRoots
        {
            get
            {
                EnsureLoaded();
                return ReadOnlyPathGuard.Roots;
            }
        }

        public void Handle(ApplicationStartedEvent message)
        {
            Load();
        }

        public bool IsReadOnly(string path)
        {
            EnsureLoaded();
            return ReadOnlyPathGuard.IsReadOnly(path);
        }

        public void SetReadOnly(string rootFolderPath, bool readOnly)
        {
            if (rootFolderPath.IsNullOrWhiteSpace())
            {
                return;
            }

            lock (_lock)
            {
                var stored = ReadStored();
                var present = stored.Any(p => p.PathEquals(rootFolderPath));

                if (readOnly == present)
                {
                    // Still apply: the folder may only now have become a root folder.
                    Apply(stored);
                    return;
                }

                if (readOnly)
                {
                    stored.Add(rootFolderPath);
                }
                else
                {
                    stored.RemoveAll(p => p.PathEquals(rootFolderPath));
                }

                _configRepository.Upsert(ConfigKey, JsonSerializer.Serialize(stored));
                _logger.Info("Talearr: root folder {0} is now {1}", rootFolderPath, readOnly ? "read-only" : "writable");
                Apply(stored);
            }
        }

        public string RedirectForWrite(string path, BookMediaType mediaType)
        {
            if (!IsReadOnly(path))
            {
                return path;
            }

            var roots = _rootFolderService.All();
            var readOnlyRoot = ReadOnlyPathGuard.Roots
                .Where(r => r.PathEquals(path) || r.IsParentPath(path))
                .OrderByDescending(r => r.Length)
                .First();

            var preferred = mediaType == BookMediaType.Ebook
                ? _configService.DefaultEbookRootFolderPath
                : _configService.DefaultAudiobookRootFolderPath;

            var target = roots
                .Where(r => !r.IsCalibreLibrary && !ReadOnlyPathGuard.IsReadOnly(r.Path) && Accepts(r, mediaType))
                .OrderByDescending(r => preferred.IsNotNullOrWhiteSpace() && r.Path.PathEquals(preferred))
                .ThenByDescending(r => r.FolderType != FolderType.Mixed)
                .ThenBy(r => r.Id)
                .FirstOrDefault();

            if (target == null)
            {
                var kind = mediaType == BookMediaType.Ebook ? "ebook" : "audiobook";
                throw new ReadOnlyPathException(path, $"import into (it is read-only, and there is no writable {kind} root folder for new downloads; add one in Settings > Media Management)");
            }

            var relative = readOnlyRoot.PathEquals(path) ? string.Empty : readOnlyRoot.GetRelativePath(path);
            var redirected = relative.IsNullOrWhiteSpace() ? target.Path : Path.Combine(target.Path, relative);

            _logger.Debug("Talearr: {0} is in a read-only library folder; writing to {1} instead", path, redirected);
            return redirected;
        }

        private static bool Accepts(RootFolder rootFolder, BookMediaType mediaType)
        {
            return rootFolder.FolderType == FolderType.Mixed ||
                   (mediaType == BookMediaType.Ebook ? rootFolder.FolderType == FolderType.Ebook : rootFolder.FolderType == FolderType.Audiobook);
        }

        private void EnsureLoaded()
        {
            if (!_loaded)
            {
                Load();
            }
        }

        private void Load()
        {
            lock (_lock)
            {
                try
                {
                    Apply(ReadStored());
                }
                catch (Exception ex)
                {
                    _logger.Warn(ex, "Talearr: could not read the read-only root folder list");
                }
            }
        }

        private void Apply(List<string> stored)
        {
            // Only folders that are still root folders count; a removed root stops being protected.
            var current = _rootFolderService.All().Select(r => r.Path).ToList();
            ReadOnlyPathGuard.SetRoots(stored.Where(p => current.Any(c => c.PathEquals(p))));
            _loaded = true;
        }

        // Whether a path is on the stored read-only list, even before it is a root folder (adding
        // a read-only root skips the write test).
        public static bool IsStoredReadOnly(IConfigRepository configRepository, string path)
        {
            if (configRepository == null || path.IsNullOrWhiteSpace())
            {
                return false;
            }

            try
            {
                var value = configRepository.Get(ConfigKey)?.Value;
                var stored = value.IsNullOrWhiteSpace() ? new List<string>() : JsonSerializer.Deserialize<List<string>>(value) ?? new List<string>();
                return stored.Any(p => p.PathEquals(path));
            }
            catch (Exception)
            {
                return false;
            }
        }

        private List<string> ReadStored()
        {
            var value = _configRepository.Get(ConfigKey)?.Value;
            if (value.IsNullOrWhiteSpace())
            {
                return new List<string>();
            }

            try
            {
                return JsonSerializer.Deserialize<List<string>>(value) ?? new List<string>();
            }
            catch (JsonException ex)
            {
                _logger.Warn(ex, "Talearr: the read-only root folder list is unreadable; treating every root folder as writable");
                return new List<string>();
            }
        }
    }
}
