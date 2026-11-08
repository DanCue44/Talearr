using System;
using System.IO;
using NLog;
using NzbDrone.Core.Configuration;

namespace NzbDrone.Core.Talearr.Calibre
{
    // Talearr: an optional Calibre library that Retry Match and Manual Import may read (never
    // write) to identify ebooks. Stored as a talearr.calibre.* row in the existing Config table,
    // which Chaptarr ignores, so a rollback is unaffected.
    public sealed class TalearrCalibreSettings
    {
        // Empty = off (the default). The folder that holds Calibre's metadata.db.
        public string LibraryPath { get; set; } = string.Empty;

        public bool Enabled => !string.IsNullOrWhiteSpace(LibraryPath);

        public static TalearrCalibreSettings Default => new TalearrCalibreSettings();

        public TalearrCalibreSettings Clone()
        {
            return new TalearrCalibreSettings { LibraryPath = LibraryPath };
        }
    }

    public interface ITalearrCalibreSettingsService
    {
        TalearrCalibreSettings Get();
        TalearrCalibreSettings Save(TalearrCalibreSettings settings);
    }

    public class TalearrCalibreSettingsService : ITalearrCalibreSettingsService
    {
        public const string LibraryPathKey = "talearr.calibre.librarypath";
        public const string MetadataFileName = "metadata.db";

        private readonly IConfigRepository _repository;
        private readonly Logger _logger;
        private readonly object _lock = new object();
        private TalearrCalibreSettings _cached;

        public TalearrCalibreSettingsService(IConfigRepository repository, Logger logger)
        {
            _repository = repository;
            _logger = logger;
        }

        // The metadata.db inside a library folder.
        public static string MetadataPath(string libraryPath)
        {
            return string.IsNullOrWhiteSpace(libraryPath) ? null : Path.Combine(libraryPath.Trim(), MetadataFileName);
        }

        // Null when the folder is a readable Calibre library (or the setting is empty, which is
        // off), otherwise why it is not. Opens metadata.db read-only and counts the books.
        public static string Validate(string libraryPath)
        {
            if (string.IsNullOrWhiteSpace(libraryPath))
            {
                return null;
            }

            var folder = libraryPath.Trim();
            if (!Path.IsPathRooted(folder))
            {
                return "Use the full path of the Calibre library folder.";
            }

            if (!Directory.Exists(folder))
            {
                return $"The folder '{folder}' was not found. Mount the Calibre library into the Talearr container and use the path inside the container.";
            }

            var metadataPath = MetadataPath(folder);
            if (!File.Exists(metadataPath))
            {
                return $"No metadata.db in '{folder}'. Choose the folder that holds Calibre's metadata.db.";
            }

            try
            {
                TalearrCalibreLibraryReader.CountBooks(metadataPath);
            }
            catch (Exception ex)
            {
                return $"Could not read '{metadataPath}': {ex.Message}";
            }

            return null;
        }

        public TalearrCalibreSettings Get()
        {
            lock (_lock)
            {
                _cached ??= Load();
                return _cached.Clone();
            }
        }

        public TalearrCalibreSettings Save(TalearrCalibreSettings settings)
        {
            settings ??= TalearrCalibreSettings.Default;
            var path = settings.LibraryPath?.Trim() ?? string.Empty;

            lock (_lock)
            {
                _repository.Upsert(LibraryPathKey, path);
                _logger.Info("Talearr Calibre settings saved: libraryPath='{0}'", path);

                _cached = Load();
                return _cached.Clone();
            }
        }

        private TalearrCalibreSettings Load()
        {
            var settings = TalearrCalibreSettings.Default;

            try
            {
                settings.LibraryPath = _repository.Get(LibraryPathKey)?.Value?.Trim() ?? string.Empty;
            }
            catch (Exception ex)
            {
                _logger.Warn(ex, "Could not read the Talearr Calibre settings, using defaults");
            }

            return settings;
        }
    }
}
