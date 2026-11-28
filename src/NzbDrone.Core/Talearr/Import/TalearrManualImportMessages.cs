using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using NzbDrone.Core.Books;
using NzbDrone.Core.MediaFiles.BookImport;

namespace NzbDrone.Core.Talearr.Import
{
    // Talearr (issue #13): the result of a Manual Import in words a user can act on. The old
    // "Manually imported 0 files (8 failed/skipped)" gave no reason and vanished after a moment.
    public static class TalearrManualImportMessages
    {
        private const int MaxReasonLength = 240;

        // "Imported 3 files" or "Imported 1 of 4 files. Not imported: 'Pack 8.m4b': <reason> (and 2 more, see System > Logs)".
        public static string Summary(int importedCount, int totalCount, IReadOnlyList<ImportResult> notImported)
        {
            if (notImported == null || notImported.Count == 0)
            {
                return $"Imported {importedCount} {Files(importedCount)}";
            }

            var first = notImported[0];
            var name = Path.GetFileName(first.ImportDecision?.Item?.Path ?? string.Empty);
            var text = $"Imported {importedCount} of {totalCount} {Files(totalCount)}. Not imported: " +
                       (string.IsNullOrWhiteSpace(name) ? string.Empty : $"'{name}': ") +
                       Reason(first);

            if (notImported.Count > 1)
            {
                text += $" (and {notImported.Count - 1} more, see System > Logs)";
            }

            return text;
        }

        // The reason a file was not imported, without the internal "Rejected: " prefix.
        public static string Reason(ImportResult result)
        {
            var reason = result?.Errors?.FirstOrDefault(e => !string.IsNullOrWhiteSpace(e)) ??
                         result?.ImportDecision?.Rejections?.Select(r => r.Reason).FirstOrDefault(r => !string.IsNullOrWhiteSpace(r)) ??
                         "no reason was given";

            const string prefix = "Rejected: ";
            if (reason.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                reason = reason.Substring(prefix.Length);
            }

            reason = reason.Trim();
            return reason.Length > MaxReasonLength ? reason.Substring(0, MaxReasonLength - 3).TrimEnd() + "..." : reason;
        }

        // A suggested book the file's own tags do not match.
        public static string SuggestedBookMismatch(string suggestedTitle, IReadOnlyCollection<Book> allowedBooks)
        {
            var title = allowedBooks?.Count == 1 ? allowedBooks.First().Title : suggestedTitle;
            var book = string.IsNullOrWhiteSpace(title) ? "the suggested book" : $"'{title}'";

            return $"Its tags do not match {book}. Pick the book in the Book column and import again.";
        }

        private static string Files(int count)
        {
            return count == 1 ? "file" : "files";
        }
    }
}
