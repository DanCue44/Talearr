using System;
using System.Collections.Generic;
using NzbDrone.Core.Parser;

namespace NzbDrone.Core.IndexerSearch.Definitions
{
    public class BookSearchCriteria : SearchCriteriaBase
    {
        public string BookTitle { get; set; }
        public int BookYear { get; set; }
        public string BookIsbn { get; set; }
        public string Disambiguation { get; set; }

        // Title-only query: most indexers accept author/title separately or combine them themselves.
        // Search the main title section; the matcher still validates against the monitored edition title.
        // Including the author here can double-apply author terms (hurting recall) and breaks book-search endpoints
        // that expect a clean title (e.g., Newznab's t=book&author=...&title=...).
        public string BookQuery => GetQueryTitle(GetMainSearchTitle(BookTitle, Author?.Name));

        internal static string GetMainSearchTitle(string title, string author)
        {
            var titleWithoutAuthor = RemoveLeadingAuthorPrefix(title, author);
            if (string.IsNullOrWhiteSpace(titleWithoutAuthor))
            {
                return titleWithoutAuthor;
            }

            var mainTitle = titleWithoutAuthor.SplitBookTitle(author).Item1;
            mainTitle = string.IsNullOrWhiteSpace(mainTitle) ? titleWithoutAuthor : mainTitle;
            return RemoveDashSubtitle(mainTitle);
        }

        // Talearr: "Men Who Hate Women - The Extremism Nobody is Talking About" found nothing,
        // because indexers want every word. A spaced dash marks a subtitle the same way a colon
        // does ("Spider-Man" has no spaces and is kept).
        internal static string RemoveDashSubtitle(string title)
        {
            if (string.IsNullOrWhiteSpace(title))
            {
                return title;
            }

            foreach (var separator in new[] { " - ", " \u2013 ", " \u2014 " })
            {
                var index = title.IndexOf(separator, StringComparison.Ordinal);
                if (index > 0)
                {
                    var main = title.Substring(0, index).Trim();
                    if (main.Length >= 3)
                    {
                        return main;
                    }
                }
            }

            return title;
        }

        internal static string RemoveLeadingAuthorPrefix(string title, string author)
        {
            if (string.IsNullOrWhiteSpace(title) || string.IsNullOrWhiteSpace(author))
            {
                return title;
            }

            var trimmedTitle = title.Trim();
            var trimmedAuthor = author.Trim();
            var prefixes = new[]
            {
                $"{trimmedAuthor}:",
                $"{trimmedAuthor} -",
                $"{trimmedAuthor} \u2013",
                $"{trimmedAuthor} \u2014"
            };

            foreach (var prefix in prefixes)
            {
                if (trimmedTitle.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                {
                    return trimmedTitle.Substring(prefix.Length).Trim();
                }
            }

            return title;
        }

        public override string ToString()
        {
            return $"[{Author.Name} - {BookTitle}]";
        }
    }
}
