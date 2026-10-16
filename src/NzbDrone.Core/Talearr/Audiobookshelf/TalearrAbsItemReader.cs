using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using NzbDrone.Common.Extensions;
using NzbDrone.Core.Talearr.GrabPolicy;

namespace NzbDrone.Core.Talearr.Audiobookshelf
{
    // One Audiobookshelf library folder and the TaleArr root folder that holds the same files.
    public sealed class TalearrAbsFolderMapping
    {
        public string LibraryId { get; set; }
        public string LibraryFolderId { get; set; }

        // The folder as Audiobookshelf sees it ("/books").
        public string AbsFolderPath { get; set; }

        // The TaleArr root folder for it ("/ebooks").
        public string LocalRootPath { get; set; }
    }

    // TaleArr: turns Audiobookshelf API items into cache rows, and Audiobookshelf paths into the
    // paths TaleArr sees. Pure functions, no I/O.
    public static class TalearrAbsItemReader
    {
        public static TalearrAbsItem Read(AbsLibraryItem item, int connectionId, IReadOnlyCollection<TalearrAbsFolderMapping> mappings, IReadOnlyCollection<string> localRootPaths, DateTime syncedAt)
        {
            if (item == null || item.Id.IsNullOrWhiteSpace() || item.IsMissing || item.IsInvalid)
            {
                return null;
            }

            if (item.MediaType.IsNotNullOrWhiteSpace() && !string.Equals(item.MediaType, "book", StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }

            var media = item.Media ?? new AbsBookMedia();
            var metadata = media.Metadata ?? new AbsBookMetadata();
            var hasAudio = media.NumAudioFiles > 0 || media.NumTracks > 0;
            var hasEbook = media.EbookFormat.IsNotNullOrWhiteSpace();

            if ((!hasAudio && !hasEbook) || metadata.Title.IsNullOrWhiteSpace())
            {
                return null;
            }

            return new TalearrAbsItem
            {
                ConnectionId = connectionId,
                ItemId = item.Id.Trim(),
                LibraryId = item.LibraryId,
                Path = item.Path,
                LocalPath = ToLocalPath(item, mappings, localRootPaths),
                Title = metadata.Title.Trim(),
                Subtitle = metadata.Subtitle.IsNullOrWhiteSpace() ? null : metadata.Subtitle.Trim(),
                Authors = metadata.AuthorName.IsNullOrWhiteSpace() ? null : metadata.AuthorName.Trim(),
                Narrators = metadata.NarratorName.IsNullOrWhiteSpace() ? null : metadata.NarratorName.Trim(),
                Asin = NormalizeAsin(metadata.Asin),
                Isbn = NormalizeIsbn(metadata.Isbn),
                DurationSeconds = hasAudio && media.Duration > 0 ? media.Duration : null,
                Abridged = DetectAbridged(metadata, item),
                HasAudio = hasAudio,
                HasEbook = hasEbook,
                EbookFormat = hasEbook ? media.EbookFormat.Trim().ToLowerInvariant() : null,
                UpdatedAt = FromUnixMs(item.UpdatedAt),
                SyncedAt = syncedAt
            };
        }

        // Audiobookshelf's own flag first (it is false unless someone set it, so false means
        // unknown, not unabridged), then markers in the title, subtitle or folder name.
        public static AbridgedMarker DetectAbridged(AbsBookMetadata metadata, AbsLibraryItem item)
        {
            if (metadata?.Abridged == true)
            {
                return AbridgedMarker.Abridged;
            }

            foreach (var text in new[] { metadata?.Title, metadata?.Subtitle, LastSegment(item?.RelPath ?? item?.Path) })
            {
                var marker = AbridgedReleaseDetector.Detect(text);
                if (marker != AbridgedMarker.Unknown)
                {
                    return marker;
                }
            }

            return AbridgedMarker.Unknown;
        }

        public static string ToLocalPath(AbsLibraryItem item, IReadOnlyCollection<TalearrAbsFolderMapping> mappings, IReadOnlyCollection<string> localRootPaths)
        {
            var absPath = item?.Path;
            if (absPath.IsNullOrWhiteSpace())
            {
                return null;
            }

            var candidates = (mappings ?? Array.Empty<TalearrAbsFolderMapping>())
                .Where(m => m != null && m.AbsFolderPath.IsNotNullOrWhiteSpace() && m.LocalRootPath.IsNotNullOrWhiteSpace())
                .Where(m => m.LibraryId.IsNullOrWhiteSpace() || string.Equals(m.LibraryId, item.LibraryId, StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(m => m.LibraryFolderId.IsNotNullOrWhiteSpace() && string.Equals(m.LibraryFolderId, item.FolderId, StringComparison.OrdinalIgnoreCase))
                .ThenByDescending(m => m.AbsFolderPath.Length);

            foreach (var mapping in candidates)
            {
                if (TryGetRelative(mapping.AbsFolderPath, absPath, out var relative))
                {
                    return Combine(mapping.LocalRootPath, relative);
                }
            }

            // No mapping: the containers may simply see the same paths.
            foreach (var root in localRootPaths ?? Array.Empty<string>())
            {
                if (root.IsNotNullOrWhiteSpace() && TryGetRelative(root, absPath, out _))
                {
                    return absPath;
                }
            }

            return null;
        }

        public static bool TryGetRelative(string basePath, string fullPath, out string relative)
        {
            relative = null;
            if (basePath.IsNullOrWhiteSpace() || fullPath.IsNullOrWhiteSpace())
            {
                return false;
            }

            var normalizedBase = basePath.Replace('\\', '/').TrimEnd('/');
            var normalizedFull = fullPath.Replace('\\', '/').TrimEnd('/');

            if (normalizedFull.Equals(normalizedBase, StringComparison.Ordinal))
            {
                relative = string.Empty;
                return true;
            }

            if (normalizedBase.Length == 0)
            {
                relative = normalizedFull.TrimStart('/');
                return normalizedFull.StartsWith("/", StringComparison.Ordinal);
            }

            if (!normalizedFull.StartsWith(normalizedBase + "/", StringComparison.Ordinal))
            {
                return false;
            }

            relative = normalizedFull.Substring(normalizedBase.Length + 1);
            return true;
        }

        public static string Combine(string root, string relative)
        {
            var useBackslash = root.Contains('\\') && !root.Contains('/');
            var separator = useBackslash ? '\\' : '/';
            var trimmedRoot = root.TrimEnd('/', '\\');

            if (relative.IsNullOrWhiteSpace())
            {
                return trimmedRoot;
            }

            var tail = relative.Replace('\\', '/').Trim('/');
            if (useBackslash)
            {
                tail = tail.Replace('/', '\\');
            }

            return trimmedRoot + separator + tail;
        }

        public static string NormalizeAsin(string asin)
        {
            if (asin.IsNullOrWhiteSpace())
            {
                return null;
            }

            var clean = asin.Trim().ToUpperInvariant();
            return clean.Length == 10 && clean.All(char.IsLetterOrDigit) ? clean : null;
        }

        // Digits (and a final X) only. ISBN-10s are converted to ISBN-13 so both forms compare.
        public static string NormalizeIsbn(string isbn)
        {
            if (isbn.IsNullOrWhiteSpace())
            {
                return null;
            }

            var builder = new StringBuilder();
            foreach (var c in isbn.Trim().ToUpperInvariant())
            {
                if (char.IsDigit(c) || (c == 'X' && builder.Length == 9))
                {
                    builder.Append(c);
                }
            }

            var clean = builder.ToString();
            if (clean.Length == 13 && (clean.StartsWith("978", StringComparison.Ordinal) || clean.StartsWith("979", StringComparison.Ordinal)))
            {
                return clean;
            }

            return clean.Length == 10 ? Isbn10To13(clean) : null;
        }

        // Every form of an ISBN-13 TaleArr may have stored (13 digits, and the ISBN-10 for 978).
        public static List<string> IsbnLookupForms(string isbn13)
        {
            var forms = new List<string>();
            if (isbn13.IsNullOrWhiteSpace())
            {
                return forms;
            }

            forms.Add(isbn13);
            if (isbn13.Length == 13 && isbn13.StartsWith("978", StringComparison.Ordinal))
            {
                var core = isbn13.Substring(3, 9);
                var sum = 0;
                for (var i = 0; i < 9; i++)
                {
                    sum += (10 - i) * (core[i] - '0');
                }

                var check = (11 - (sum % 11)) % 11;
                forms.Add(core + (check == 10 ? "X" : check.ToString()));
            }

            return forms;
        }

        private static string Isbn10To13(string isbn10)
        {
            var core = "978" + isbn10.Substring(0, 9);
            var sum = 0;
            for (var i = 0; i < 12; i++)
            {
                sum += (core[i] - '0') * (i % 2 == 0 ? 1 : 3);
            }

            return core + ((10 - (sum % 10)) % 10);
        }

        private static string LastSegment(string path)
        {
            if (path.IsNullOrWhiteSpace())
            {
                return null;
            }

            var trimmed = path.Replace('\\', '/').TrimEnd('/');
            var index = trimmed.LastIndexOf('/');
            return index >= 0 ? trimmed.Substring(index + 1) : trimmed;
        }

        private static DateTime? FromUnixMs(long? value)
        {
            if (!value.HasValue || value.Value <= 0)
            {
                return null;
            }

            try
            {
                return DateTimeOffset.FromUnixTimeMilliseconds(value.Value).UtcDateTime;
            }
            catch (ArgumentOutOfRangeException)
            {
                return null;
            }
        }
    }
}
