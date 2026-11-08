using System;
using System.Collections.Generic;
using System.Linq;
using NzbDrone.Core.MediaFiles.BookImport;

namespace NzbDrone.Core.Talearr.Calibre
{
    // Talearr: turns a Calibre identification into matching evidence and into the words shown
    // to the user. The matcher itself is unchanged: it simply sees Calibre's author, title,
    // ISBN and language in place of the file's own (often poor) embedded values.
    public static class TalearrCalibreEvidence
    {
        public const string NoProviderEditionReason = "CALIBRE_NO_PROVIDER_EDITION";

        // Identifies the files, puts Calibre's values into a copy of each identified file's tags
        // (the stored tags are never changed) and returns what was found, ambiguous ones included.
        public static Dictionary<string, TalearrCalibreIdentification> Apply(ITalearrCalibreIdentifier identifier, IReadOnlyCollection<DiscoveredFileWithMetadata> files)
        {
            if (identifier == null || files == null || files.Count == 0 || !identifier.IsEnabled)
            {
                return new Dictionary<string, TalearrCalibreIdentification>();
            }

            var found = identifier.Identify(files) ?? new Dictionary<string, TalearrCalibreIdentification>();
            foreach (var file in files)
            {
                if (file?.Path != null && found.TryGetValue(file.Path, out var identification) && identification.IsMatch)
                {
                    ApplyTo(file, identification);
                }
            }

            return found;
        }

        public static void ApplyTo(DiscoveredFileWithMetadata file, TalearrCalibreIdentification identification)
        {
            if (file == null || identification?.Book == null)
            {
                return;
            }

            var tags = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
            foreach (var pair in file.AllTags ?? new Dictionary<string, List<string>>())
            {
                if (pair.Key != null)
                {
                    tags[pair.Key] = pair.Value?.ToList() ?? new List<string>();
                }
            }

            var book = identification.Book;
            if (!string.IsNullOrWhiteSpace(book.Title))
            {
                tags["title"] = new List<string> { book.Title };
                file.ExtractedBook = book.Title;
            }

            if (book.Authors?.Count > 0)
            {
                var authors = string.Join("; ", book.Authors);
                tags["author"] = new List<string> { authors };
                tags["authors"] = new List<string> { authors };
                file.ExtractedAuthor = book.Authors[0];
            }

            var isbn = identification.PreferredIsbn;
            if (isbn != null)
            {
                tags["isbn"] = new List<string> { isbn };
                file.ISBN = isbn;
            }

            if (book.Languages?.Count > 0)
            {
                tags["language"] = book.Languages.ToList();
            }

            file.AllTags = tags;
        }

        // True when the provider offered an edition for this unmatched file.
        public static bool ProviderOfferedEdition(UnmatchedFile unmatched)
        {
            return unmatched?.PotentialAuthors?.Any(a => !string.IsNullOrWhiteSpace(a?.EditionHardcoverId)) == true;
        }

        public static string NoProviderEditionMessage(TalearrCalibreIdentification identification)
        {
            var isbn = identification.PreferredIsbn != null ? $" (ISBN {identification.PreferredIsbn})" : string.Empty;
            return $"Calibre identifies this file as {identification.Book.Title} by {identification.AuthorText}{isbn}, but the metadata provider has no matching edition. Choose the book yourself.";
        }

        // Stored as the unmapped file's match details by Retry Match.
        public static string NoProviderEditionReasonCode(TalearrCalibreIdentification identification, string originalReason)
        {
            return $"{NoProviderEditionReason}:calibreBook={identification.Book.Id}:isbn={identification.PreferredIsbn ?? "none"}:{originalReason ?? "NO_MATCH"}";
        }
    }
}
