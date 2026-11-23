using System;
using System.Linq;
using System.Text.RegularExpressions;
using NzbDrone.Core.Books;
using NzbDrone.Core.IndexerSearch.Definitions;

namespace NzbDrone.Core.Talearr.Titles
{
    // Talearr: the display title (Edit book Display Title, or Clean Series From Titles) as a second
    // title for searches and release matching. Searches still try the metadata title first;
    // the display title is an extra query, and a release named after either title matches.
    //
    // Static because the release matcher (ReleaseTitleMatchScorer) is static. The display title
    // service registers itself at startup; until then, and in tests that do not register, there
    // is no second title.
    public static class TalearrSearchTitles
    {
        private static readonly Regex WordRegex = new Regex(@"[\p{L}\p{N}]+", RegexOptions.Compiled);

        private static volatile Func<Book, string> _resolver;

        public static void Register(Func<Book, string> resolver)
        {
            _resolver = resolver;
        }

        // The display title to search with as well, or null: only when it searches differently
        // from the metadata title. Never throws.
        public static string ForSearch(Book book, string searchedTitle)
        {
            var alternate = Resolve(book);
            return alternate != null && IsUsable(alternate) &&
                   !string.Equals(QueryKey(alternate), QueryKey(searchedTitle ?? book?.Title), StringComparison.OrdinalIgnoreCase)
                ? alternate
                : null;
        }

        // The display title to match releases against as well, or null. Never throws.
        public static string ForMatching(Book book)
        {
            var alternate = Resolve(book);
            return alternate != null && IsUsable(alternate) ? alternate : null;
        }

        // More than one short word: "It" or "Fox" alone would match far too many releases.
        public static bool IsUsable(string alternate)
        {
            if (string.IsNullOrWhiteSpace(alternate))
            {
                return false;
            }

            var words = WordRegex.Matches(alternate).Select(m => m.Value).ToList();
            return words.Count > 1 || (words.Count == 1 && words[0].Length >= 4);
        }

        private static string Resolve(Book book)
        {
            var resolver = _resolver;
            if (resolver == null || book == null)
            {
                return null;
            }

            try
            {
                var alternate = resolver(book)?.Trim();
                return string.IsNullOrEmpty(alternate) || string.Equals(alternate, book.Title?.Trim(), StringComparison.OrdinalIgnoreCase)
                    ? null
                    : alternate;
            }
            catch (Exception)
            {
                return null;
            }
        }

        // The title as a book search sends it (main title, query punctuation).
        public static string QueryKey(string title)
        {
            if (string.IsNullOrWhiteSpace(title))
            {
                return string.Empty;
            }

            var main = BookSearchCriteria.GetMainSearchTitle(title.Trim(), null);
            return string.IsNullOrWhiteSpace(main) ? string.Empty : SearchCriteriaBase.GetQueryTitle(main);
        }
    }
}
