using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using NzbDrone.Core.Download.TrackedDownloads;

namespace NzbDrone.Core.Talearr.GrabPolicy
{
    // Talearr: decides whether a blocked import was caused by the release itself (so trying
    // the same files again can never work) or by something Talearr or the user can fix.
    // Only the first kind fails the download. Deliberately conservative: anything not
    // recognised stays blocked in the queue for the user, exactly as before.
    public static class ImportRejectionClassifier
    {
        // "Completed download was grabbed for 'Bride' (BookId 3756, work hc:801806), but import
        //  matched 'Mate' (BookId 3754, work hc:1785259)."
        private static readonly Regex WrongBookRegex = new Regex(
            @"Completed download was grabbed for (?<expected>.+?), but import matched '(?<matched>.+?)' \(BookId \d+",
            RegexOptions.Compiled | RegexOptions.Singleline);

        // "Completed download is by Craig Rice, not Ryan Becker (grabbed for 'True Crime Stories: ...'
        //  (BookId 3653, ...))." (TalearrImportAuthorCheck)
        private static readonly Regex WrongAuthorRegex = new Regex(
            @"^Completed download is by (?<found>.+?), not (?<expected>.+?) \(grabbed for ",
            RegexOptions.Compiled | RegexOptions.Singleline);

        private static readonly Regex QuotedTitleRegex = new Regex(@"'(?<title>.+?)' \(BookId \d+", RegexOptions.Compiled);

        // "No supported audio or ebook files were found in ... Unsupported extension(s): .diz, .nfo, .zip."
        private const string NoMediaFilesText = "No supported audio or ebook files were found";

        // Talearr unpacks zip, rar (and its .rNN/.partN volumes) and 7z itself
        // (MediaFiles/TalearrImport), so a "no supported files" report that lists those means
        // the importer already looked inside them, or Unpack Archives is off. Formats it does
        // not unpack (tar, gzip, bzip2, xz, .001 splits) are still left for the user.
        private static readonly Regex UnhandledArchiveRegex = new Regex(
            @"\.(?:tar|gz|tgz|bz2|xz|\d{3})\b",
            RegexOptions.Compiled | RegexOptions.IgnoreCase);

        // "File language is Swedish (swe) (from EPUB dc:language), but English (eng) is wanted
        //  (editions of 'The Pelican Brief'). Talearr rejected this release so another one can be tried."
        private static readonly Regex WrongLanguageRegex = new Regex(
            @"File language is (?<found>.+?) \(from .+?\), but (?<wanted>.+?) is wanted \(.*Talearr rejected this release",
            RegexOptions.Compiled | RegexOptions.Singleline);

        // "Talearr could not unpack the archives in <path>: <error>. The release itself is unusable."
        // Only the release-problem suffix fails; the transient suffix ("Fix the cause and use Retry
        // Import.") stays blocked.
        private const string UnpackFailedText = "Talearr could not unpack the archives in";
        private const string UnpackReleaseProblemText = "The release itself is unusable.";

        private static readonly Regex StatusCodeRegex = new Regex(@"^[A-Z0-9_]+$", RegexOptions.Compiled);
        private static readonly Regex NonWordRegex = new Regex(@"[^a-z0-9]+", RegexOptions.Compiled);

        // Returns a short failure reason when every rejection is deterministic, otherwise null.
        public static string Classify(IEnumerable<TrackedDownloadStatusMessage> statusMessages)
        {
            var messages = (statusMessages ?? Enumerable.Empty<TrackedDownloadStatusMessage>())
                .Where(status => status?.Messages != null)
                .SelectMany(status => status.Messages)
                .Where(message => !string.IsNullOrWhiteSpace(message))
                .Select(message => message.Trim())
                .Where(message => !StatusCodeRegex.IsMatch(message))

                // Talearr: books a bundle holds besides the wanted one say nothing about the release.
                .Where(message => !TalearrBundleFallback.IsBundleExtraReason(message))
                .ToList();

            if (messages.Count == 0)
            {
                return null;
            }

            var reasons = new List<string>();

            foreach (var message in messages)
            {
                var reason = ClassifyMessage(message);
                if (reason == null)
                {
                    return null;
                }

                reasons.Add(reason);
            }

            return string.Join("; ", reasons.Distinct());
        }

        internal static string ClassifyMessage(string message)
        {
            var wrongBook = WrongBookRegex.Match(message);
            if (wrongBook.Success)
            {
                var matched = wrongBook.Groups["matched"].Value;
                var expected = QuotedTitleRegex.Matches(wrongBook.Groups["expected"].Value)
                    .Select(m => m.Groups["title"].Value)
                    .ToList();

                if (expected.Count == 0)
                {
                    return null;
                }

                // Same or overlapping titles usually mean duplicate metadata rows for one book,
                // which is a library problem, not a bad release. Leave those for the user.
                if (expected.Any(title => TitlesOverlap(title, matched)))
                {
                    return null;
                }

                return string.Format("release contains '{0}', not the wanted book", matched);
            }

            // Talearr: a different author is a different book, the same as a wrong-book match.
            var wrongAuthor = WrongAuthorRegex.Match(message);
            if (wrongAuthor.Success)
            {
                return string.Format("release is by {0}, not {1}, so it is not the wanted book",
                    wrongAuthor.Groups["found"].Value,
                    wrongAuthor.Groups["expected"].Value);
            }

            var wrongLanguage = WrongLanguageRegex.Match(message);
            if (wrongLanguage.Success)
            {
                return string.Format("release is in {0}, not {1}", wrongLanguage.Groups["found"].Value, wrongLanguage.Groups["wanted"].Value);
            }

            if (message.Contains(UnpackFailedText, StringComparison.Ordinal))
            {
                return message.Contains(UnpackReleaseProblemText, StringComparison.Ordinal)
                    ? "release archives are unusable"
                    : null;
            }

            if (message.Contains(NoMediaFilesText, StringComparison.OrdinalIgnoreCase))
            {
                if (UnhandledArchiveRegex.IsMatch(message))
                {
                    return null;
                }

                return "release contains no audio or ebook files";
            }

            return null;
        }

        private static bool TitlesOverlap(string a, string b)
        {
            var left = Normalize(a);
            var right = Normalize(b);

            if (left.Length == 0 || right.Length == 0)
            {
                return true;
            }

            return left.Contains(right, StringComparison.Ordinal) || right.Contains(left, StringComparison.Ordinal);
        }

        private static string Normalize(string value)
        {
            var cleaned = NonWordRegex.Replace((value ?? string.Empty).ToLowerInvariant(), " ").Trim();
            return cleaned.StartsWith("the ", StringComparison.Ordinal) ? cleaned.Substring(4) : cleaned;
        }
    }
}
