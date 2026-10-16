using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using NzbDrone.Core.Download.TrackedDownloads;

namespace NzbDrone.Core.Talearr.GrabPolicy
{
    // TaleArr: decides whether a blocked import was caused by the release itself (so trying
    // the same files again can never work) or by something TaleArr or the user can fix.
    // Only the first kind fails the download. Deliberately conservative: anything not
    // recognised stays blocked in the queue for the user, exactly as before.
    public static class ImportRejectionClassifier
    {
        // "Completed download was grabbed for 'Bride' (BookId 3756, work hc:801806), but import
        //  matched 'Mate' (BookId 3754, work hc:1785259)."
        private static readonly Regex WrongBookRegex = new Regex(
            @"Completed download was grabbed for (?<expected>.+?), but import matched '(?<matched>.+?)' \(BookId \d+",
            RegexOptions.Compiled | RegexOptions.Singleline);

        private static readonly Regex QuotedTitleRegex = new Regex(@"'(?<title>.+?)' \(BookId \d+", RegexOptions.Compiled);

        // "No supported audio or ebook files were found in ... Unsupported extension(s): .diz, .nfo, .zip."
        private const string NoMediaFilesText = "No supported audio or ebook files were found";

        // Archives are handled by the importer (extraction); never fail a download for them here.
        private static readonly Regex ArchiveRegex = new Regex(
            @"\.(?:zip|rar|7z|tar|gz|tgz|bz2|xz|r\d{2}|s\d{2}|\d{3}|part\d+)\b",
            RegexOptions.Compiled | RegexOptions.IgnoreCase);

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

            if (message.Contains(NoMediaFilesText, StringComparison.OrdinalIgnoreCase))
            {
                if (ArchiveRegex.IsMatch(message))
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
