using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using NLog;
using NzbDrone.Common.Disk;
using NzbDrone.Core.Configuration;

namespace NzbDrone.Core.MediaFiles.TalearrImport
{
    public interface IDownloadArchiveUnpacker
    {
        /// <summary>
        /// Unpacks a download that holds its book only inside archives (zip, rar sets, 7z, and
        /// archives inside those) into a work folder next to the download. Returns null when there
        /// is nothing to unpack: the feature is off, the payload already has book files, or it has
        /// no archives. The original download is only ever read.
        /// </summary>
        ArchiveUnpackResult Unpack(string downloadPath);

        /// <summary>
        /// Deletes the work folder of an unpack. Never touches the original download.
        /// </summary>
        void Cleanup(ArchiveUnpackResult result);
    }

    public class ArchiveUnpackResult
    {
        public string SourcePath { get; set; }
        public string WorkFolder { get; set; }
        public bool Success { get; set; }
        public string Error { get; set; }
        public List<string> ArchivesUnpacked { get; set; } = new List<string>();
        public List<string> MediaFiles { get; set; } = new List<string>();
        public bool Reused { get; set; }
    }

    public class DownloadArchiveUnpacker : IDownloadArchiveUnpacker
    {
        public const string WorkFolderPrefix = ".talearr-unpack.";
        public const string CompleteMarkerName = ".talearr-unpack-complete";

        // Work folders whose download was removed before they were cleaned up.
        private static readonly TimeSpan StaleWorkFolderAge = TimeSpan.FromDays(2);

        private readonly IDiskProvider _diskProvider;
        private readonly IConfigService _configService;
        private readonly Logger _logger;

        public DownloadArchiveUnpacker(IDiskProvider diskProvider, IConfigService configService, Logger logger)
        {
            _diskProvider = diskProvider;
            _configService = configService;
            _logger = logger;
        }

        public ArchiveExtractionLimits Limits { get; set; } = ArchiveExtractionLimits.Default;

        public ArchiveUnpackResult Unpack(string downloadPath)
        {
            if (string.IsNullOrWhiteSpace(downloadPath) || !IsEnabled())
            {
                return null;
            }

            List<string> payloadFiles;
            bool isFolder;

            if (Directory.Exists(downloadPath))
            {
                isFolder = true;
                payloadFiles = SafeEnumerateFiles(downloadPath);
            }
            else if (File.Exists(downloadPath))
            {
                isFolder = false;
                payloadFiles = new List<string> { downloadPath };
            }
            else
            {
                return null;
            }

            if (IsInsideWorkFolder(downloadPath))
            {
                return null;
            }

            if (payloadFiles.Any(IsMediaFile))
            {
                return null;
            }

            var archives = payloadFiles
                .Where(SafeArchiveExtractor.IsFirstArchiveVolume)
                .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
                .ToList();

            if (archives.Count == 0)
            {
                return null;
            }

            var result = new ArchiveUnpackResult
            {
                SourcePath = downloadPath,
                WorkFolder = GetWorkFolder(downloadPath, isFolder)
            };

            SweepStaleWorkFolders(Path.GetDirectoryName(result.WorkFolder), result.WorkFolder);

            if (TryReuse(result))
            {
                return result;
            }

            try
            {
                if (!PrepareWorkFolder(result))
                {
                    // An earlier run already unpacked this download into the fallback folder.
                    return result;
                }

                CheckFreeSpace(result.WorkFolder, archives);
                UnpackNested(archives, result);

                result.MediaFiles = SafeEnumerateFiles(result.WorkFolder).Where(IsMediaFile).ToList();
                if (result.MediaFiles.Count == 0)
                {
                    throw new ArchiveExtractionException("the archives did not contain any supported audio or ebook files");
                }

                File.WriteAllText(Path.Combine(result.WorkFolder, CompleteMarkerName), downloadPath);
                result.Success = true;

                _logger.Info("Unpacked {0} archive(s) from {1} into {2} ({3} book file(s))",
                    result.ArchivesUnpacked.Count,
                    downloadPath,
                    result.WorkFolder,
                    result.MediaFiles.Count);
            }
            catch (Exception ex) when (ex is ArchiveExtractionException || ex is IOException || ex is UnauthorizedAccessException)
            {
                result.Success = false;
                result.Error = ex.Message;
                _logger.Warn("TaleArr could not unpack {0}: {1}", downloadPath, ex.Message);
                Cleanup(result);
            }

            return result;
        }

        /// <summary>
        /// Creates an empty work folder next to the download, or in the system temp folder when the
        /// download folder is not writable. Returns false when the fallback folder already holds a
        /// finished unpack that can be reused.
        /// </summary>
        private bool PrepareWorkFolder(ArchiveUnpackResult result)
        {
            try
            {
                ResetFolder(result.WorkFolder);
                return true;
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                var fallback = GetFallbackWorkFolder(result.SourcePath);
                _logger.Debug(ex, "Cannot create unpack folder next to {0}, using {1}", result.SourcePath, fallback);
                result.WorkFolder = fallback;

                if (TryReuse(result))
                {
                    return false;
                }

                ResetFolder(result.WorkFolder);
                return true;
            }
        }

        private static void ResetFolder(string folder)
        {
            if (Directory.Exists(folder))
            {
                // Left over from an unpack that did not finish; start again.
                Directory.Delete(folder, true);
            }

            Directory.CreateDirectory(folder);
        }

        public void Cleanup(ArchiveUnpackResult result)
        {
            var workFolder = result?.WorkFolder;
            if (string.IsNullOrWhiteSpace(workFolder) || !IsWorkFolderPath(workFolder))
            {
                return;
            }

            // Belt and braces: the work folder is never the download itself or above it.
            if (!string.IsNullOrWhiteSpace(result.SourcePath) &&
                (PathsEqual(workFolder, result.SourcePath) || IsParentOf(workFolder, result.SourcePath)))
            {
                _logger.Warn("Refusing to delete unpack folder {0}: it contains the download {1}", workFolder, result.SourcePath);
                return;
            }

            try
            {
                if (Directory.Exists(workFolder))
                {
                    Directory.Delete(workFolder, true);
                    _logger.Debug("Removed unpack folder {0}", workFolder);
                }
            }
            catch (Exception ex)
            {
                _logger.Warn(ex, "Could not remove unpack folder {0}", workFolder);
            }
        }

        private void UnpackNested(List<string> topLevelArchives, ArchiveUnpackResult result)
        {
            var budget = new ArchiveExtractionBudget(Limits);
            var pending = topLevelArchives;
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            for (var depth = 1; pending.Count > 0; depth++)
            {
                if (depth > Limits.MaxDepth)
                {
                    throw new ArchiveExtractionException($"archives are nested more than {Limits.MaxDepth} levels deep");
                }

                var produced = new List<string>();

                foreach (var archive in pending)
                {
                    if (!seen.Add(Path.GetFullPath(archive)))
                    {
                        continue;
                    }

                    var destination = GetDestinationFor(archive, result.WorkFolder, depth == 1);
                    _logger.Debug("Unpacking {0} into {1}", archive, destination);

                    produced.AddRange(SafeArchiveExtractor.Extract(archive, destination, budget));
                    result.ArchivesUnpacked.Add(archive);
                }

                // Stop as soon as a level yields book files; anything still packed beside them is
                // not needed for the import.
                if (produced.Any(IsMediaFile))
                {
                    break;
                }

                pending = produced
                    .Where(SafeArchiveExtractor.IsFirstArchiveVolume)
                    .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
                    .ToList();
            }
        }

        private static string GetDestinationFor(string archive, string workFolder, bool topLevel)
        {
            // Top-level archives land in the work folder itself; nested ones next to the archive
            // they came from, in a folder named after it so two inner archives cannot collide.
            if (topLevel)
            {
                return workFolder;
            }

            return Path.Combine(Path.GetDirectoryName(archive), StripArchiveExtension(Path.GetFileName(archive)) + ".unpacked");
        }

        private static string StripArchiveExtension(string fileName)
        {
            var name = Path.GetFileNameWithoutExtension(fileName);
            var part = name.LastIndexOf(".part", StringComparison.OrdinalIgnoreCase);
            if (part > 0 && name.Substring(part + 5).All(char.IsDigit))
            {
                name = name.Substring(0, part);
            }

            return string.IsNullOrWhiteSpace(name) ? "archive" : name;
        }

        private bool TryReuse(ArchiveUnpackResult result)
        {
            // A previous run unpacked this download and left it for a detached conversion; use it
            // as is instead of unpacking again under the conversion's feet.
            if (!File.Exists(Path.Combine(result.WorkFolder, CompleteMarkerName)))
            {
                return false;
            }

            var mediaFiles = SafeEnumerateFiles(result.WorkFolder).Where(IsMediaFile).ToList();
            if (mediaFiles.Count == 0)
            {
                return false;
            }

            result.MediaFiles = mediaFiles;
            result.Success = true;
            result.Reused = true;
            _logger.Debug("Reusing unpack folder {0} for {1}", result.WorkFolder, result.SourcePath);
            return true;
        }

        private void CheckFreeSpace(string workFolder, List<string> archives)
        {
            long? available;
            try
            {
                available = _diskProvider.GetAvailableSpace(workFolder);
            }
            catch (Exception ex)
            {
                _logger.Debug(ex, "Cannot read free space for {0}", workFolder);
                return;
            }

            if (!available.HasValue)
            {
                return;
            }

            var needed = archives.Sum(archive =>
            {
                try
                {
                    return new FileInfo(archive).Length;
                }
                catch
                {
                    return 0L;
                }
            });

            if (available.Value < needed)
            {
                throw new ArchiveExtractionException(
                    $"not enough free space to unpack ({ArchiveExtractionBudget.FormatBytes(available.Value)} free, " +
                    $"at least {ArchiveExtractionBudget.FormatBytes(needed)} needed)");
            }
        }

        private void SweepStaleWorkFolders(string parent, string current)
        {
            if (string.IsNullOrWhiteSpace(parent) || !Directory.Exists(parent))
            {
                return;
            }

            try
            {
                foreach (var folder in Directory.EnumerateDirectories(parent, WorkFolderPrefix + "*"))
                {
                    if (PathsEqual(folder, current))
                    {
                        continue;
                    }

                    if (DateTime.UtcNow - Directory.GetLastWriteTimeUtc(folder) > StaleWorkFolderAge)
                    {
                        _logger.Debug("Removing stale unpack folder {0}", folder);
                        Directory.Delete(folder, true);
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.Debug(ex, "Could not sweep stale unpack folders in {0}", parent);
            }
        }

        private bool IsEnabled()
        {
            try
            {
                return _configService?.TalearrUnpackArchives ?? true;
            }
            catch (Exception ex)
            {
                _logger.Debug(ex, "Cannot read TalearrUnpackArchives, using the default");
                return true;
            }
        }

        public static string GetWorkFolder(string downloadPath, bool isFolder)
        {
            var trimmed = downloadPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            var parent = Path.GetDirectoryName(trimmed);
            var name = isFolder ? Path.GetFileName(trimmed) : Path.GetFileNameWithoutExtension(trimmed);

            if (string.IsNullOrWhiteSpace(parent))
            {
                parent = trimmed;
            }

            return Path.Combine(parent, WorkFolderPrefix + SanitizeName(name));
        }

        private static string GetFallbackWorkFolder(string downloadPath)
        {
            using var sha = SHA256.Create();
            var hash = Convert.ToHexString(sha.ComputeHash(Encoding.UTF8.GetBytes(downloadPath))).Substring(0, 16).ToLowerInvariant();
            return Path.Combine(Path.GetTempPath(), "talearr-unpack", WorkFolderPrefix + hash);
        }

        private static string SanitizeName(string name)
        {
            if (string.IsNullOrWhiteSpace(name))
            {
                return "download";
            }

            var invalid = Path.GetInvalidFileNameChars();
            var cleaned = new string(name.Select(c => invalid.Contains(c) ? '_' : c).ToArray());
            return cleaned.Length > 150 ? cleaned.Substring(0, 150) : cleaned;
        }

        private static bool IsWorkFolderPath(string path)
        {
            var name = Path.GetFileName(path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
            return name.StartsWith(WorkFolderPrefix, StringComparison.Ordinal);
        }

        private static bool IsInsideWorkFolder(string path)
        {
            var current = path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            while (!string.IsNullOrEmpty(current))
            {
                if (IsWorkFolderPath(current))
                {
                    return true;
                }

                current = Path.GetDirectoryName(current);
            }

            return false;
        }

        private static bool IsMediaFile(string path)
        {
            return MediaFileExtensions.AllExtensions.Contains(Path.GetExtension(path)) &&
                   !Path.GetFileName(path).StartsWith("._", StringComparison.Ordinal);
        }

        private static List<string> SafeEnumerateFiles(string folder)
        {
            try
            {
                return Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories).ToList();
            }
            catch (Exception)
            {
                return new List<string>();
            }
        }

        private static bool PathsEqual(string left, string right)
        {
            return string.Equals(
                Path.GetFullPath(left).TrimEnd(Path.DirectorySeparatorChar),
                Path.GetFullPath(right).TrimEnd(Path.DirectorySeparatorChar),
                StringComparison.Ordinal);
        }

        private static bool IsParentOf(string parent, string child)
        {
            var parentFull = Path.GetFullPath(parent).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            return Path.GetFullPath(child).StartsWith(parentFull, StringComparison.Ordinal);
        }
    }
}
