using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using NzbDrone.Core.Books;
using NzbDrone.Core.Indexers;
using NzbDrone.Core.MediaFiles;
using NzbDrone.Core.Parser.Model;

namespace NzbDrone.Core.Talearr.Soulseek
{
    // One thing TaleArr could ask a peer for: a whole audiobook folder, or one ebook file.
    public class TalearrSoulseekCandidate
    {
        public string Key { get; set; }
        public string Username { get; set; }

        // The remote folder (Soulseek paths use backslashes) and, for disc folders, the parent.
        public string Directory { get; set; }
        public string Title { get; set; }
        public BookMediaType MediaType { get; set; }
        public List<SlskdFile> Files { get; set; } = new List<SlskdFile>();
        public int MediaFileCount { get; set; }
        public long Size { get; set; }
        public int? BitRate { get; set; }
        public int? DurationSeconds { get; set; }
        public string Format { get; set; }
        public bool HasFreeUploadSlot { get; set; }
        public long QueueLength { get; set; }
        public int UploadSpeed { get; set; }
        public bool HasDiscFolders { get; set; }
    }

    // TaleArr: turns slskd search answers into candidates and in-memory ReleaseInfo rows, so the
    // normal decision engine (title matching, language, packs/bundles, size, quality ladder)
    // judges them. The ReleaseInfo rows are never stored: Soulseek grabs do not go through
    // DownloadService, History, Blocklist or PendingReleases.
    public static class TalearrSoulseekCandidates
    {
        public const string IndexerName = "Soulseek (slskd)";

        private static readonly HashSet<string> ExtraExtensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            ".jpg", ".jpeg", ".png", ".cue", ".nfo"
        };

        private const long MaxExtraFileSize = 20L * 1024 * 1024;

        // Folder names that say nothing about the book.
        private static readonly HashSet<string> GenericSegments = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "audiobooks", "audiobook", "audio books", "audio book", "books", "book", "ebooks", "ebook",
            "e-books", "music", "shared", "share", "shares", "downloads", "download", "complete",
            "completed", "media", "library", "unabridged", "abridged", "mp3", "m4b", "epub", "new folder",
            "soulseek", "slsk", "files", "public", "incoming"
        };

        private static readonly Regex DiscFolderRegex = new Regex(@"^(?:cd|dis[ck]|part|pt)[\s._-]*\d{1,3}(?:\s*(?:of|-)\s*\d{1,3})?$", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        private static readonly Regex PartFileRegex = new Regex(@"(?:\b(?:part|pt|cd|dis[ck])[\s._-]*\d{1,3}\b)|(?:\b\d{1,3}\s*of\s*\d{1,3}\b)", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        private static readonly Regex DriveOrShareRoot = new Regex(@"^(?:[a-z]:|@@[^\\/]*)$", RegexOptions.IgnoreCase | RegexOptions.Compiled);

        private static readonly string[] EbookFormatOrder = { ".epub", ".kepub", ".azw3", ".azw", ".mobi", ".pdf" };

        public static List<TalearrSoulseekCandidate> Build(IEnumerable<SlskdSearchResponse> responses,
                                                           BookMediaType mediaType,
                                                           TalearrSoulseekSettings settings,
                                                           ICollection<string> excludedPeers = null)
        {
            settings ??= TalearrSoulseekSettings.Default;
            var candidates = new List<TalearrSoulseekCandidate>();

            foreach (var response in responses ?? Enumerable.Empty<SlskdSearchResponse>())
            {
                if (response == null || string.IsNullOrWhiteSpace(response.Username) || !PassesEtiquette(response, settings))
                {
                    continue;
                }

                if (excludedPeers != null && excludedPeers.Contains(response.Username))
                {
                    continue;
                }

                var files = (response.Files ?? new List<SlskdFile>())
                    .Where(f => f != null && !f.IsLocked && !string.IsNullOrWhiteSpace(f.Filename))
                    .ToList();

                candidates.AddRange(mediaType == BookMediaType.Ebook
                    ? BuildEbookCandidates(response, files)
                    : BuildAudiobookCandidates(response, files));
            }

            return candidates
                .GroupBy(c => c.Key)
                .Select(g => g.First())
                .ToList();
        }

        public static bool PassesEtiquette(SlskdSearchResponse response, TalearrSoulseekSettings settings)
        {
            if (settings.RequireFreeUploadSlot && !response.HasFreeUploadSlot)
            {
                return false;
            }

            if (settings.MaxPeerQueueLength > 0 && response.QueueLength > settings.MaxPeerQueueLength)
            {
                return false;
            }

            if (settings.MinUploadSpeedKbps > 0 && response.UploadSpeed < settings.MinUploadSpeedKbps * 1024L)
            {
                return false;
            }

            return true;
        }

        public static ReleaseInfo ToRelease(TalearrSoulseekCandidate candidate)
        {
            return new ReleaseInfo
            {
                Guid = "slskd-" + candidate.Key,
                Title = candidate.Title,
                Size = candidate.Size,
                Indexer = IndexerName,
                IndexerId = 0,

                // Unknown (0) is a value every Chaptarr build knows. It only lives in memory here.
                DownloadProtocol = DownloadProtocol.Unknown,
                PublishDate = DateTime.UtcNow,
                Categories = new List<int> { candidate.MediaType == BookMediaType.Ebook ? 7020 : 3030 },
                Duration = candidate.DurationSeconds > 0 ? TimeSpan.FromSeconds(candidate.DurationSeconds.Value).ToString(@"hh\:mm\:ss") : null
            };
        }

        private static IEnumerable<TalearrSoulseekCandidate> BuildAudiobookCandidates(SlskdSearchResponse response, List<SlskdFile> files)
        {
            // A folder with audio files that are locked to us (buddies only) would arrive incomplete.
            var lockedDirectories = new HashSet<string>(
                (response.LockedFiles ?? new List<SlskdFile>())
                    .Where(f => f != null && IsAudio(f))
                    .Select(f => GroupDirectory(f.Filename)),
                StringComparer.OrdinalIgnoreCase);

            // Disc folders (CD1, Disc 2, Part 03) belong to their parent folder.
            var groups = files
                .Where(f => IsAudio(f) || IsExtra(f))
                .GroupBy(f => GroupDirectory(f.Filename), StringComparer.OrdinalIgnoreCase);

            foreach (var group in groups)
            {
                var audio = group.Where(IsAudio).ToList();
                if (audio.Count == 0 || lockedDirectories.Contains(group.Key))
                {
                    continue;
                }

                var hasDiscFolders = group.Any(f => !string.Equals(DirectoryOf(f.Filename), group.Key, StringComparison.OrdinalIgnoreCase));

                // slskd puts a grab's files into one folder, so CD1\01.mp3 and CD2\01.mp3 would
                // collide. Such a release is skipped rather than half imported.
                if (hasDiscFolders && audio.Select(f => FileName(f.Filename)).Distinct(StringComparer.OrdinalIgnoreCase).Count() != audio.Count)
                {
                    continue;
                }

                // A folder of several single-file books (Book One.m4b, Book Two.m4b) offers each
                // file on its own, unless the names say they are parts of one book.
                var m4b = audio.Where(f => Extension(f) == ".m4b").ToList();
                if (m4b.Count > 1 && m4b.Count == audio.Count && !m4b.All(f => PartFileRegex.IsMatch(Path.GetFileNameWithoutExtension(FileName(f.Filename)))))
                {
                    foreach (var file in m4b)
                    {
                        yield return Create(response, group.Key, new List<SlskdFile> { file }, BookMediaType.Audiobook, false, Path.GetFileNameWithoutExtension(FileName(file.Filename)));
                    }

                    continue;
                }

                var extras = group.Where(f => IsExtra(f) && f.Size <= MaxExtraFileSize).ToList();
                yield return Create(response, group.Key, audio.Concat(extras).ToList(), BookMediaType.Audiobook, hasDiscFolders, null);
            }
        }

        private static IEnumerable<TalearrSoulseekCandidate> BuildEbookCandidates(SlskdSearchResponse response, List<SlskdFile> files)
        {
            // Ebooks are single files, often in a folder holding many books: each book is its own
            // candidate, in the best format the folder has for that name.
            var ebooks = files.Where(f => MediaFileExtensions.TextExtensions.Contains(Extension(f)));

            foreach (var book in ebooks.GroupBy(f => DirectoryOf(f.Filename) + "\n" + Path.GetFileNameWithoutExtension(FileName(f.Filename)), StringComparer.OrdinalIgnoreCase))
            {
                var best = book
                    .OrderBy(f => FormatRank(Extension(f)))
                    .ThenByDescending(f => f.Size)
                    .First();

                yield return Create(response, DirectoryOf(best.Filename), new List<SlskdFile> { best }, BookMediaType.Ebook, false, Path.GetFileNameWithoutExtension(FileName(best.Filename)));
            }
        }

        private static TalearrSoulseekCandidate Create(SlskdSearchResponse response, string directory, List<SlskdFile> files, BookMediaType mediaType, bool hasDiscFolders, string singleName)
        {
            var media = files.Where(f => mediaType == BookMediaType.Ebook ? MediaFileExtensions.TextExtensions.Contains(Extension(f)) : IsAudio(f)).ToList();
            var format = media
                .GroupBy(Extension)
                .OrderByDescending(g => g.Count())
                .Select(g => g.Key.TrimStart('.').ToUpperInvariant())
                .FirstOrDefault();

            var bitRates = media.Where(f => f.BitRate > 0).Select(f => f.BitRate.Value).ToList();
            int? bitRate = bitRates.Count > 0 ? (int)Math.Round(bitRates.Average()) : null;
            var lengths = media.Where(f => f.Length > 0).Select(f => f.Length.Value).ToList();
            int? duration = lengths.Count > 0 ? lengths.Sum() : null;

            var candidate = new TalearrSoulseekCandidate
            {
                Username = response.Username,
                Directory = directory,
                MediaType = mediaType,
                Files = files,
                MediaFileCount = media.Count,
                Size = files.Sum(f => Math.Max(0, f.Size)),
                BitRate = bitRate,
                DurationSeconds = duration,
                Format = format,
                HasFreeUploadSlot = response.HasFreeUploadSlot,
                QueueLength = response.QueueLength,
                UploadSpeed = response.UploadSpeed,
                HasDiscFolders = hasDiscFolders
            };

            candidate.Title = BuildTitle(directory, singleName, candidate);
            candidate.Key = Hash(response.Username + "\n" + directory + "\n" + string.Join("\n", files.Select(f => f.Filename).OrderBy(f => f, StringComparer.OrdinalIgnoreCase)));

            return candidate;
        }

        // "Brandon Sanderson - Mistborn 01 - The Final Empire [MP3 64kbps]": the meaningful folder
        // names (and the file name for a single file), plus a format hint the quality parser reads.
        public static string BuildTitle(string directory, string singleName, TalearrSoulseekCandidate candidate)
        {
            var segments = Segments(directory)
                .Where(s => !DriveOrShareRoot.IsMatch(s) && !GenericSegments.Contains(s.Trim()) && !DiscFolderRegex.IsMatch(s.Trim()))
                .ToList();

            var parts = new List<string>();
            var take = singleName == null ? 2 : 1;
            parts.AddRange(segments.Skip(Math.Max(0, segments.Count - take)));

            if (!string.IsNullOrWhiteSpace(singleName))
            {
                // Drop a folder name the file name already contains ("Dune\Dune.epub").
                parts.RemoveAll(p => singleName.Contains(p, StringComparison.OrdinalIgnoreCase));
                parts.Add(singleName);
            }

            // A parent that the leaf already contains adds nothing ("Sanderson\Sanderson - Elantris").
            if (parts.Count == 2 && parts[1].Contains(parts[0], StringComparison.OrdinalIgnoreCase))
            {
                parts.RemoveAt(0);
            }

            var title = string.Join(" - ", parts.Select(p => p.Replace('_', ' ').Trim()).Where(p => p.Length > 0));
            if (title.Length == 0)
            {
                title = singleName ?? Segments(directory).LastOrDefault() ?? "Soulseek release";
            }

            var hint = FormatHint(candidate);
            return hint == null || title.Contains(hint, StringComparison.OrdinalIgnoreCase) ? title : $"{title} [{hint}]";
        }

        private static string FormatHint(TalearrSoulseekCandidate candidate)
        {
            if (candidate.Format == null)
            {
                return null;
            }

            if (candidate.MediaType == BookMediaType.Audiobook && candidate.Format == "MP3" && candidate.BitRate > 0)
            {
                return $"MP3 {candidate.BitRate}kbps";
            }

            return candidate.Format;
        }

        public static string GroupDirectory(string filename)
        {
            var directory = DirectoryOf(filename);
            var leaf = Segments(directory).LastOrDefault();

            if (leaf != null && DiscFolderRegex.IsMatch(leaf.Trim()))
            {
                var parent = DirectoryOf(directory);
                if (!string.IsNullOrEmpty(parent))
                {
                    return parent;
                }
            }

            return directory;
        }

        public static string DirectoryOf(string path)
        {
            if (string.IsNullOrEmpty(path))
            {
                return string.Empty;
            }

            var index = path.LastIndexOfAny(new[] { '\\', '/' });
            return index <= 0 ? string.Empty : path.Substring(0, index);
        }

        public static string FileName(string path)
        {
            if (string.IsNullOrEmpty(path))
            {
                return string.Empty;
            }

            var index = path.LastIndexOfAny(new[] { '\\', '/' });
            return index < 0 ? path : path.Substring(index + 1);
        }

        public static List<string> Segments(string path)
        {
            return (path ?? string.Empty)
                .Split(new[] { '\\', '/' }, StringSplitOptions.RemoveEmptyEntries)
                .ToList();
        }

        private static bool IsAudio(SlskdFile file)
        {
            return MediaFileExtensions.AudioExtensions.Contains(Extension(file));
        }

        private static bool IsExtra(SlskdFile file)
        {
            return ExtraExtensions.Contains(Extension(file));
        }

        private static string Extension(SlskdFile file)
        {
            var name = FileName(file?.Filename);
            var ext = Path.GetExtension(name);
            return string.IsNullOrEmpty(ext) ? string.Empty : ext.ToLowerInvariant();
        }

        private static int FormatRank(string extension)
        {
            var index = Array.IndexOf(EbookFormatOrder, extension);
            return index < 0 ? EbookFormatOrder.Length : index;
        }

        private static string Hash(string value)
        {
            var bytes = SHA1.HashData(Encoding.UTF8.GetBytes(value));
            return Convert.ToHexString(bytes, 0, 8).ToLowerInvariant();
        }
    }
}
