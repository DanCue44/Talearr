using System;
using System.Collections.Generic;
using System.Linq;
using NzbDrone.Common.Extensions;
using NzbDrone.Core.Books;
using NzbDrone.Core.Parser;
using NzbDrone.Core.Parser.Model;
using NzbDrone.Core.Qualities;

namespace NzbDrone.Core.Talearr.GrabPolicy
{
    // Talearr: bundles (box sets, collections, discographies) as a last resort. With the grab
    // policy's "Allow bundles" off, a bundle is no longer rejected outright: it passes the pack
    // check flagged as a fallback, every other check still runs on it, and ProcessDownloadDecisions
    // only grabs it for a book that no single copy covered. The import then keeps the wanted book,
    // imports other books from the bundle only when they are monitored and missing, and leaves
    // the rest behind.
    public static class TalearrBundleFallback
    {
        // Grabbed history Data keys.
        public const string BundleHistoryKey = "talearrBundle";
        public const string BundleFallbackHistoryKey = "talearrBundleFallback";
        public const string BundleTargetsHistoryKey = "talearrBundleTargets";

        public const string BundleExtraPrefix = "Bundle extra: ";

        // Automatic searches (missing search, book/author search, failed-download retry) may
        // fall back to a bundle. RSS and pushed releases never do: they lack the full picture.
        public static bool IsAutomaticSearchSource(ReleaseSourceType source)
        {
            return source == ReleaseSourceType.Search || source == ReleaseSourceType.UserInvokedSearch;
        }

        // Whether a bundle that the grab policy does not allow may still be offered as a last
        // resort. Interactive search always shows it (the user decides). An automatic search
        // only takes a bundle whose title names a wanted book, so a "5 Books Collection" that
        // may not contain the book is never downloaded on a guess.
        public static bool CanFallBack(RemoteBook remoteBook)
        {
            if (remoteBook == null)
            {
                return false;
            }

            if (remoteBook.ReleaseSource == ReleaseSourceType.InteractiveSearch)
            {
                return true;
            }

            if (!IsAutomaticSearchSource(remoteBook.ReleaseSource))
            {
                return false;
            }

            return remoteBook.Books?.Any(book => book != null && BundleNamesBook(remoteBook, book)) == true;
        }

        // Talearr: whether a bundle's name really names this book. The title must appear as a whole
        // phrase (not inside "All-in-One"), the bundle must not credit another author, a short
        // title ("All In", "Bride", "Deep End") also needs the author, a course or marketing pack
        // needs the author, and a number in the wanted title must not be contradicted.
        public static bool BundleNamesBook(RemoteBook remoteBook, Book book)
        {
            return WhyBundleDoesNotNameBook(remoteBook, book) == null;
        }

        public static string WhyBundleDoesNotNameBook(RemoteBook remoteBook, Book book)
        {
            var releaseTitle = remoteBook?.Release?.Title;
            if (book == null || releaseTitle.IsNullOrWhiteSpace())
            {
                return "Bundle does not name the wanted book";
            }

            if (!TalearrBundleDetector.ReleaseMentionsTitle(releaseTitle, book.Title))
            {
                return "Bundle does not name the wanted book";
            }

            var numberMismatch = TalearrReleaseIdentityGuard.FindNumberMismatch(releaseTitle, book);
            if (numberMismatch != null)
            {
                return $"Bundle names a different number: {numberMismatch}";
            }

            var author = remoteBook.Author ?? book.Author;
            if (author?.Name.IsNullOrWhiteSpace() != false)
            {
                return null;
            }

            var releaseAuthor = remoteBook.Release.Author;
            var namesAuthor = ReleaseIdentityEvidence.TextNamesAuthor(author, releaseTitle) ||
                              ReleaseIdentityEvidence.NameMatchesAuthor(author, releaseAuthor);
            if (namesAuthor)
            {
                return null;
            }

            if (releaseAuthor.IsNotNullOrWhiteSpace())
            {
                return $"Bundle is by {releaseAuthor}, not {author.Name}";
            }

            var otherAuthor = TalearrReleaseIdentityGuard.FindOtherAuthor(releaseTitle, author, new[] { book });
            if (otherAuthor != null)
            {
                return $"Bundle is by {otherAuthor}, not {author.Name}";
            }

            if (TalearrReleaseIdentityGuard.FindNonBookPackWord(releaseTitle, book) != null)
            {
                return "Release looks like a course or marketing pack, not a book";
            }

            if (TalearrReleaseIdentityGuard.IsShortTitle(book.Title))
            {
                return $"Bundle does not name the author of the short title '{book.Title}'";
            }

            return null;
        }

        public static void AddHistoryData(RemoteBook remoteBook, IDictionary<string, string> data)
        {
            if (remoteBook?.IsBundle != true || data == null)
            {
                return;
            }

            data[BundleHistoryKey] = "true";
            data[BundleFallbackHistoryKey] = remoteBook.IsBundleFallback.ToString().ToLowerInvariant();
            data[BundleTargetsHistoryKey] = string.Join(",", (remoteBook.Books ?? new List<Book>())
                .Where(book => book != null && book.Id > 0)
                .Select(book => book.Id)
                .Distinct());
        }

        public static bool IsBundleHistory(IDictionary<string, string> data)
        {
            return TryGetFlag(data, BundleHistoryKey);
        }

        public static bool IsBundleFallbackHistory(IDictionary<string, string> data)
        {
            return TryGetFlag(data, BundleFallbackHistoryKey);
        }

        public static string BundleExtraReason(string bookLabel)
        {
            return $"{BundleExtraPrefix}{bookLabel} was not requested and is not on the wanted list, so it was left out.";
        }

        public static bool IsBundleExtraReason(string reason)
        {
            return reason != null && reason.StartsWith(BundleExtraPrefix, StringComparison.Ordinal);
        }

        // A book from a bundle that was not grabbed for it is still imported when the user wants
        // it: monitored, and no file of its own media type (each Book row is one media type).
        public static bool IsWantedExtra(Book book, QualityModel fileQuality)
        {
            if (book == null || book.Id <= 0 || !book.IsMonitored())
            {
                return false;
            }

            var fileMediaType = fileQuality?.Quality == null
                ? null
                : QualityMediaTypeHelper.GetKnownMediaType(fileQuality.Quality);
            if (fileMediaType.HasValue && fileMediaType.Value != book.MediaType)
            {
                return false;
            }

            return book.BookFiles?.Any() != true;
        }

        private static bool TryGetFlag(IDictionary<string, string> data, string key)
        {
            if (data == null)
            {
                return false;
            }

            if (!data.TryGetValue(key, out var value))
            {
                value = data.FirstOrDefault(item => string.Equals(item.Key, key, StringComparison.OrdinalIgnoreCase)).Value;
            }

            return !value.IsNullOrWhiteSpace() && bool.TryParse(value, out var flag) && flag;
        }
    }
}
