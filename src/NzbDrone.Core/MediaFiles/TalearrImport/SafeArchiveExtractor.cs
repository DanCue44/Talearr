using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using SharpCompress.Archives;
using SharpCompress.Common;
using SharpCompress.Readers;

namespace NzbDrone.Core.MediaFiles.TalearrImport
{
    // Talearr: guard rails for unpacking downloaded archives. Sizes are counted from the bytes
    // actually written, never from archive headers, because a hostile archive can lie about them.
    public class ArchiveExtractionLimits
    {
        public static ArchiveExtractionLimits Default => new ArchiveExtractionLimits();

        // Most nested level unpacked (zip -> rar -> 7z is depth 3).
        public int MaxDepth { get; set; } = 3;

        // Entries across the whole unpack, all levels.
        public int MaxEntries { get; set; } = 10000;

        // Bytes written across the whole unpack, all levels. Large enough for audiobook sets.
        public long MaxTotalBytes { get; set; } = 50L * 1024 * 1024 * 1024;

        // Bytes a single archive may expand to, relative to its size on disk...
        public long MaxExpansionRatio { get; set; } = 100;

        // ...but small archives may always expand to at least this much.
        public long MinExpansionAllowanceBytes { get; set; } = 256L * 1024 * 1024;

        public long GetAllowedBytes(long archiveBytes)
        {
            var byRatio = archiveBytes > long.MaxValue / Math.Max(1, MaxExpansionRatio)
                ? long.MaxValue
                : archiveBytes * MaxExpansionRatio;

            return Math.Max(byRatio, MinExpansionAllowanceBytes);
        }
    }

    public class ArchiveExtractionException : Exception
    {
        // A problem with the release itself (zip bomb, unsafe paths, password, no book inside):
        // unpacking the same files again can never work.
        public ArchiveExtractionException(string message)
            : base(message)
        {
            ReleaseProblem = true;
        }

        private ArchiveExtractionException(string message, Exception innerException, bool releaseProblem)
            : base(message, innerException)
        {
            ReleaseProblem = releaseProblem;
        }

        public bool ReleaseProblem { get; }

        // A problem that may go away (a volume still being moved, disk space, a read error).
        public static ArchiveExtractionException Transient(string message, Exception innerException = null)
        {
            return new ArchiveExtractionException(message, innerException, false);
        }
    }

    public class ArchiveExtractionBudget
    {
        public ArchiveExtractionBudget(ArchiveExtractionLimits limits)
        {
            Limits = limits ?? ArchiveExtractionLimits.Default;
        }

        public ArchiveExtractionLimits Limits { get; }
        public long BytesWritten { get; private set; }
        public int EntriesWritten { get; private set; }

        public void AddEntry()
        {
            EntriesWritten++;
            if (EntriesWritten > Limits.MaxEntries)
            {
                throw new ArchiveExtractionException($"archive has more than {Limits.MaxEntries} entries");
            }
        }

        public void AddBytes(long count)
        {
            BytesWritten += count;
            if (BytesWritten > Limits.MaxTotalBytes)
            {
                throw new ArchiveExtractionException($"archive expands to more than {FormatBytes(Limits.MaxTotalBytes)} in total");
            }
        }

        public static string FormatBytes(long bytes)
        {
            if (bytes >= 1L << 30)
            {
                return $"{bytes / (double)(1L << 30):0.#} GiB";
            }

            if (bytes >= 1L << 20)
            {
                return $"{bytes / (double)(1L << 20):0.#} MiB";
            }

            return $"{bytes} bytes";
        }
    }

    public static class SafeArchiveExtractor
    {
        private static readonly HashSet<string> ArchiveExtensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            ".zip", ".rar", ".7z"
        };

        private static readonly Regex RarPartRegex = new Regex(@"\.part(?<number>\d+)\.rar$", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        private static readonly Regex OldRarVolumeRegex = new Regex(@"\.(?:r|s)\d{2,3}$", RegexOptions.IgnoreCase | RegexOptions.Compiled);

        /// <summary>
        /// True for the file that opens an archive set: a .zip, a .7z, a single .rar, the .rar of an
        /// old-style .rar/.r00 set, or the first .partN.rar of a new-style set. Later volumes are
        /// opened through their first volume, never on their own.
        /// </summary>
        public static bool IsFirstArchiveVolume(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                return false;
            }

            var fileName = Path.GetFileName(path);
            if (fileName.StartsWith("._", StringComparison.Ordinal))
            {
                return false;
            }

            var extension = Path.GetExtension(fileName);
            if (!ArchiveExtensions.Contains(extension))
            {
                return false;
            }

            var partMatch = RarPartRegex.Match(fileName);
            if (partMatch.Success)
            {
                return int.TryParse(partMatch.Groups["number"].Value, out var number) && number == 1;
            }

            return true;
        }

        /// <summary>
        /// True for any file that belongs to an archive set, including later volumes.
        /// </summary>
        public static bool IsArchivePart(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                return false;
            }

            var fileName = Path.GetFileName(path);
            return ArchiveExtensions.Contains(Path.GetExtension(fileName)) || OldRarVolumeRegex.IsMatch(fileName);
        }

        /// <summary>
        /// Extracts one archive (with all of its volumes) into <paramref name="destination"/>.
        /// Returns the files written. Throws <see cref="ArchiveExtractionException"/> when the
        /// archive is unreadable, encrypted, incomplete or breaks a limit; the caller removes the
        /// partial output.
        /// </summary>
        public static List<string> Extract(string archivePath, string destination, ArchiveExtractionBudget budget)
        {
            budget ??= new ArchiveExtractionBudget(ArchiveExtractionLimits.Default);

            var destinationRoot = Path.GetFullPath(destination);
            Directory.CreateDirectory(destinationRoot);

            List<FileInfo> volumes;
            try
            {
                volumes = ArchiveFactory.GetFileParts(archivePath)
                    .Select(part => new FileInfo(part))
                    .Where(part => part.Exists)
                    .ToList();
            }
            catch (Exception ex)
            {
                throw ArchiveExtractionException.Transient($"cannot read {Path.GetFileName(archivePath)}: {ex.Message}", ex);
            }

            if (volumes.Count == 0)
            {
                volumes = new List<FileInfo> { new FileInfo(archivePath) };
            }

            var archiveBytes = volumes.Sum(volume => volume.Length);
            var allowedBytes = budget.Limits.GetAllowedBytes(archiveBytes);
            var archiveBytesWritten = 0L;
            var written = new List<string>();

            try
            {
                using var archive = ArchiveFactory.OpenArchive(volumes, new ReaderOptions());

                if (archive.IsEncrypted)
                {
                    throw new ArchiveExtractionException($"{Path.GetFileName(archivePath)} is password protected");
                }

                if (!archive.IsComplete)
                {
                    throw ArchiveExtractionException.Transient($"{Path.GetFileName(archivePath)} is missing one or more volumes");
                }

                void WriteEntry(IEntry entry, Func<Stream> openStream)
                {
                    if (entry.IsDirectory)
                    {
                        return;
                    }

                    if (entry.IsEncrypted)
                    {
                        throw new ArchiveExtractionException($"{Path.GetFileName(archivePath)} is password protected");
                    }

                    // Links can point anywhere on disk; a book payload never needs one.
                    if (!string.IsNullOrEmpty(entry.LinkTarget))
                    {
                        return;
                    }

                    budget.AddEntry();

                    var target = ResolveEntryPath(destinationRoot, entry.Key);
                    Directory.CreateDirectory(Path.GetDirectoryName(target));

                    using var input = openStream();
                    using var output = new FileStream(target, FileMode.Create, FileAccess.Write, FileShare.None);
                    written.Add(target);

                    var buffer = new byte[81920];
                    int read;
                    while ((read = input.Read(buffer, 0, buffer.Length)) > 0)
                    {
                        archiveBytesWritten += read;
                        if (archiveBytesWritten > allowedBytes)
                        {
                            throw new ArchiveExtractionException(
                                $"{Path.GetFileName(archivePath)} expands to more than {ArchiveExtractionBudget.FormatBytes(allowedBytes)} " +
                                $"from {ArchiveExtractionBudget.FormatBytes(archiveBytes)}, which looks like a zip bomb");
                        }

                        budget.AddBytes(read);
                        output.Write(buffer, 0, read);
                    }
                }

                // Solid archives and 7z can only be read front to back; everything else is read
                // entry by entry so a damaged entry does not hide the rest.
                if (archive.IsSolid || archive.Type == ArchiveType.SevenZip)
                {
                    using var reader = archive.ExtractAllEntries();
                    while (reader.MoveToNextEntry())
                    {
                        WriteEntry(reader.Entry, reader.OpenEntryStream);
                    }
                }
                else
                {
                    foreach (var entry in archive.Entries)
                    {
                        WriteEntry(entry, entry.OpenEntryStream);
                    }
                }
            }
            catch (ArchiveExtractionException)
            {
                throw;
            }
            catch (Exception ex)
            {
                throw ArchiveExtractionException.Transient($"cannot unpack {Path.GetFileName(archivePath)}: {ex.Message}", ex);
            }

            return written;
        }

        /// <summary>
        /// Maps an archive entry name to a path inside <paramref name="destinationRoot"/>, refusing
        /// absolute names, drive letters and any ".." that would leave the folder.
        /// </summary>
        public static string ResolveEntryPath(string destinationRoot, string entryKey)
        {
            if (string.IsNullOrWhiteSpace(entryKey))
            {
                throw new ArchiveExtractionException("archive contains an entry without a name");
            }

            var normalized = entryKey.Replace('\\', '/');

            if (normalized.StartsWith("/", StringComparison.Ordinal) ||
                (normalized.Length >= 2 && normalized[1] == ':') ||
                normalized.Contains('\0'))
            {
                throw new ArchiveExtractionException($"archive entry '{entryKey}' uses an absolute path");
            }

            var invalidChars = Path.GetInvalidFileNameChars();
            var segments = new List<string>();
            foreach (var rawSegment in normalized.Split('/', StringSplitOptions.RemoveEmptyEntries))
            {
                if (rawSegment == ".")
                {
                    continue;
                }

                if (rawSegment == "..")
                {
                    throw new ArchiveExtractionException($"archive entry '{entryKey}' points outside the unpack folder");
                }

                var cleaned = new string(rawSegment.Select(c => invalidChars.Contains(c) ? '_' : c).ToArray()).Trim();
                if (cleaned.Length == 0 || cleaned.Trim('.').Length == 0)
                {
                    throw new ArchiveExtractionException($"archive entry '{entryKey}' has an invalid name");
                }

                segments.Add(cleaned);
            }

            if (segments.Count == 0)
            {
                throw new ArchiveExtractionException($"archive entry '{entryKey}' has an invalid name");
            }

            var root = Path.GetFullPath(destinationRoot);
            var rootWithSeparator = root.EndsWith(Path.DirectorySeparatorChar) ? root : root + Path.DirectorySeparatorChar;
            var target = Path.GetFullPath(Path.Combine(new[] { root }.Concat(segments).ToArray()));

            if (!target.StartsWith(rootWithSeparator, StringComparison.Ordinal))
            {
                throw new ArchiveExtractionException($"archive entry '{entryKey}' points outside the unpack folder");
            }

            return target;
        }
    }
}
