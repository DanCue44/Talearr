using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using NzbDrone.Core.Books;
using NzbDrone.Core.Download;
using NzbDrone.Core.Parser.Model;
using NzbDrone.Core.Qualities;
using NzbDrone.Core.Talearr.Recordings;

namespace NzbDrone.Core.Talearr.GrabPolicy
{
    public enum AbridgedMarker
    {
        Unknown = 0,
        Unabridged = 1,
        Abridged = 2
    }

    // Talearr: detects abridged and unabridged audiobook releases and editions.
    public static class AbridgedReleaseDetector
    {
        // "abridged" that is not part of "unabridged" / "un-abridged" / "un abridged".
        // Letter boundaries rather than \b so "Title_abridged_64kbps" still counts.
        private static readonly Regex AbridgedRegex = new Regex(@"(?<![a-z])(?<!un[\s._-])abridged(?![a-z])",
            RegexOptions.Compiled | RegexOptions.IgnoreCase);

        private static readonly Regex UnabridgedRegex = new Regex(@"(?<![a-z])un[\s._-]?abridged(?![a-z])",
            RegexOptions.Compiled | RegexOptions.IgnoreCase);

        // An edition is treated as abridged by runtime when it is shorter than this share of the
        // longest edition of the same book (abridgements are usually a third to a half).
        public const double AbridgedRuntimeRatio = 0.6;

        // Ignore runtimes that are too short to be a real full-length reading.
        private const int MinimumComparableDurationSeconds = 30 * 60;

        public static AbridgedMarker Detect(string text)
        {
            if (string.IsNullOrWhiteSpace(text))
            {
                return AbridgedMarker.Unknown;
            }

            if (AbridgedRegex.IsMatch(text))
            {
                return AbridgedMarker.Abridged;
            }

            return UnabridgedRegex.IsMatch(text) ? AbridgedMarker.Unabridged : AbridgedMarker.Unknown;
        }

        public static bool IsAbridgedRelease(RemoteBook remoteBook)
        {
            return DetectRelease(remoteBook?.Release) == AbridgedMarker.Abridged;
        }

        // A release's marker: the indexer's own abridged flag first, then the title.
        public static AbridgedMarker DetectRelease(ReleaseInfo release)
        {
            if (release == null)
            {
                return AbridgedMarker.Unknown;
            }

            if (release.IndexerFlags.HasFlag(IndexerFlags.Abridged))
            {
                return AbridgedMarker.Abridged;
            }

            return Detect(release.Title);
        }

        public static bool IsAudiobookRelease(RemoteBook remoteBook)
        {
            if (remoteBook == null)
            {
                return false;
            }

            var detected = QualityMediaTypeHelper.DetectMediaType(remoteBook.ParsedBookInfo?.Quality?.Quality, remoteBook.Release);
            if (detected.HasValue)
            {
                return detected.Value == BookMediaType.Audiobook;
            }

            var books = remoteBook.Books?.Where(book => book != null).ToList() ?? new List<Book>();
            return books.Any() && books.All(book => book.MediaType == BookMediaType.Audiobook);
        }

        // True when the wanted audiobook (its monitored edition) is itself abridged, judged from
        // edition metadata first and runtime second. Grabbing an abridged release is then right.
        public static bool WantsAbridged(RemoteBook remoteBook)
        {
            return WantsAbridged(remoteBook, null);
        }

        // With a resolver (Talearr recordings), the wanted edition's abridged status comes from
        // Audible facts first, then the same metadata and runtime rules as below.
        public static bool WantsAbridged(RemoteBook remoteBook, ITalearrAbridgedResolver resolver)
        {
            var books = remoteBook?.GetBooksMatchingReleaseMediaType() ?? new List<Book>();

            return books.Any(book => book != null &&
                                     book.MediaType == BookMediaType.Audiobook &&
                                     IsAbridgedBook(book, resolver));
        }

        public static bool IsAbridgedBook(Book book, ITalearrAbridgedResolver resolver)
        {
            if (resolver == null)
            {
                return IsAbridgedBook(book);
            }

            try
            {
                return resolver.Resolve(book).IsAbridged;
            }
            catch (System.Exception)
            {
                return IsAbridgedBook(book);
            }
        }

        public static bool IsAbridgedBook(Book book)
        {
            if (book == null)
            {
                return false;
            }

            if (Detect(book.Title) == AbridgedMarker.Abridged)
            {
                return true;
            }

            List<Edition> editions;
            try
            {
                editions = book.Editions?.Where(edition => edition != null).ToList();
            }
            catch (System.Exception)
            {
                editions = null;
            }

            if (editions == null || editions.Count == 0)
            {
                return false;
            }

            var wanted = editions.Where(edition => edition.Monitored).ToList();
            if (wanted.Count == 0)
            {
                return false;
            }

            if (wanted.Any(IsAbridgedByMetadata))
            {
                return true;
            }

            return wanted.Any(edition => IsAbridgedByRuntime(edition, editions));
        }

        public static bool IsAbridgedByMetadata(Edition edition)
        {
            if (edition == null)
            {
                return false;
            }

            return new[]
                {
                    edition.Title,
                    edition.Subtitle,
                    edition.Disambiguation,
                    edition.EditionInfo,
                    edition.EditionFormat,
                    edition.Format,
                    edition.AudioProductionType
                }
                .Any(value => Detect(value) == AbridgedMarker.Abridged);
        }

        public static bool IsAbridgedByRuntime(Edition edition, IEnumerable<Edition> allEditions)
        {
            var duration = edition?.DurationSeconds ?? 0;
            if (duration < MinimumComparableDurationSeconds)
            {
                return false;
            }

            var comparable = (allEditions ?? Enumerable.Empty<Edition>())
                .Where(other => other != null && !ReferenceEquals(other, edition))
                .Where(other => (other.DurationSeconds ?? 0) >= MinimumComparableDurationSeconds)
                .Where(other => SameLanguage(edition, other))
                .Where(other => !IsAbridgedByMetadata(other))
                .Select(other => other.DurationSeconds.Value)
                .ToList();

            if (comparable.Count == 0)
            {
                return false;
            }

            return duration < comparable.Max() * AbridgedRuntimeRatio;
        }

        private static bool SameLanguage(Edition a, Edition b)
        {
            if (string.IsNullOrWhiteSpace(a?.Language) || string.IsNullOrWhiteSpace(b?.Language))
            {
                return true;
            }

            return string.Equals(a.Language.Trim(), b.Language.Trim(), System.StringComparison.OrdinalIgnoreCase);
        }
    }
}
