using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using NzbDrone.Common.Extensions;
using NzbDrone.Core.Books;

namespace NzbDrone.Core.Parser
{
    // TaleArr: cheap identity checks shared by the grab decision (search and bundle last resort)
    // and the import of a tracked download. Real wrong matches they stop:
    //   "Craig Rice - 45 Murderers- A Collection of True Crime Stories (retail) (epub)" was grabbed
    //     and imported for "True Crime Stories: 48 Terrifying True Crime Murder Cases" by Ryan Becker
    //     (another author, another number);
    //   "Boost Your Marketing Game with This All-in-One Bundle" was grabbed for "All In" by Jennifer
    //     Lynn Barnes (short title, no author, a marketing pack).
    public static class TalearrReleaseIdentityGuard
    {
        // A short title ("All In", "Bride", "Deep End", "Two Can Play") appears inside too many
        // unrelated release names to stand on its own.
        public const int ShortTitleMaxSignificantWords = 3;
        public const int ShortTitleMinLength = 12;

        private static readonly Regex AuthorSegmentSeparatorRegex = new Regex("\\s+[-\u2013\u2014]\\s+", RegexOptions.Compiled);
        private static readonly Regex NameTokenRegex = new Regex(@"^(?:\p{L}\.?){1,3}$|^\p{L}[\p{L}'’.]*$", RegexOptions.Compiled);
        private static readonly Regex YearRegex = new Regex(@"^(?:18|19|20|21)\d{2}$", RegexOptions.Compiled);

        private static readonly HashSet<string> Stopwords = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "a", "an", "and", "or", "the", "of", "in", "on", "at", "by", "for", "with", "to", "from", "as", "is"
        };

        // Words that are not part of a person's name in a leading "Author - " segment.
        private static readonly HashSet<string> NonNameWords = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "the", "a", "an", "of", "and", "book", "books", "series", "collection", "complete", "box", "set",
            "vol", "volume", "edition", "audiobook", "ebook", "epub", "mobi", "pdf", "m4b", "mp3", "unabridged",
            "abridged", "retail", "various", "unknown", "anonymous", "va", "bundle", "pack", "stories", "novel"
        };

        private static readonly HashSet<string> PlaceholderAuthors = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "unknown", "unknown author", "various", "various authors", "various artists", "anonymous", "va", "n/a", "none"
        };

        // Releases that are courses, software or marketing packs rather than books.
        private static readonly Regex NonBookPackRegex = new Regex(
            @"\b(?:all-in-one|masterclass|master\s+class|udemy|skillshare|coursera|lynda|webinars?|tutorials?|courses?|software|plugins?|presets|templates|marketing|seo|crypto\s+trading|forex)\b",
            RegexOptions.Compiled | RegexOptions.IgnoreCase);

        public static bool IsShortTitle(string bookTitle)
        {
            var primary = TalearrTitleNormalizer.GetMainTitle(TalearrTitleNormalizer.StripAllBrackets(bookTitle ?? string.Empty));
            if (primary.IsNullOrWhiteSpace())
            {
                primary = bookTitle ?? string.Empty;
            }

            var tokens = ReleaseTitleMatchScorer.Tokenize(primary);
            var significant = tokens.Count(token => !Stopwords.Contains(token));
            var length = string.Join(" ", tokens).Length;

            return significant <= ShortTitleMaxSignificantWords || length < ShortTitleMinLength;
        }

        // The words of a non-book pack the release uses that the wanted book's title does not.
        public static string FindNonBookPackWord(string releaseTitle, Book book)
        {
            if (releaseTitle.IsNullOrWhiteSpace())
            {
                return null;
            }

            var bookText = string.Join(" ", new[] { book?.Title, book?.SeriesName }.Where(value => value.IsNotNullOrWhiteSpace()));

            foreach (Match match in NonBookPackRegex.Matches(releaseTitle))
            {
                if (bookText.IsNullOrWhiteSpace() ||
                    !Regex.IsMatch(bookText, @"\b" + Regex.Escape(match.Value) + @"\b", RegexOptions.IgnoreCase))
                {
                    return match.Value;
                }
            }

            return null;
        }

        // The author a release name explicitly credits ("Craig Rice - 45 Murderers ...") when it is
        // not the expected author, an alias or a pen name. Null when the release names the expected
        // author anywhere, names nobody, or the leading segment could be the title or series.
        public static string FindOtherAuthor(string releaseTitle, Author expectedAuthor, IEnumerable<Book> books)
        {
            if (releaseTitle.IsNullOrWhiteSpace() || expectedAuthor?.Name.IsNullOrWhiteSpace() != false)
            {
                return null;
            }

            if (ReleaseIdentityEvidence.TextNamesAuthor(expectedAuthor, releaseTitle))
            {
                return null;
            }

            var cleaned = Parser.CleanReleaseTitleForParsing(releaseTitle) ?? string.Empty;
            var segments = AuthorSegmentSeparatorRegex.Split(cleaned, 2);
            if (segments.Length < 2)
            {
                return null;
            }

            var candidate = segments[0].Trim();
            return LooksLikeOtherAuthor(candidate, expectedAuthor, books) ? candidate : null;
        }

        // An author tag value from a file (ebook dc:creator, audiobook album artist) that credits
        // somebody other than the expected author.
        public static bool IsOtherAuthorTag(string tagValue, Author expectedAuthor, IEnumerable<Book> books)
        {
            if (tagValue.IsNullOrWhiteSpace() || expectedAuthor?.Name.IsNullOrWhiteSpace() != false)
            {
                return false;
            }

            var value = tagValue.Trim();
            if (PlaceholderAuthors.Contains(value) || !value.Any(char.IsLetter))
            {
                return false;
            }

            if (ReleaseIdentityEvidence.NameMatchesAuthor(expectedAuthor, value) ||
                ReleaseIdentityEvidence.TextNamesAuthor(expectedAuthor, value))
            {
                return false;
            }

            return !OverlapsBookWords(value, books) && !IsKnownNarrator(value, books);
        }

        // Numbers in the wanted title ("48 Terrifying True Crime Murder Cases") that the release
        // contradicts with a different number ("45 Murderers"). Years and the series position do
        // not count.
        public static string FindNumberMismatch(string releaseTitle, Book book)
        {
            if (releaseTitle.IsNullOrWhiteSpace() || book?.Title.IsNullOrWhiteSpace() != false)
            {
                return null;
            }

            var bookNumbers = Numbers(book.Title).ToList();
            if (bookNumbers.Count == 0)
            {
                return null;
            }

            var seriesPosition = NormalizeNumber(book.SeriesPosition);
            var releaseNumbers = Numbers(Parser.CleanReleaseTitleForParsing(releaseTitle))
                .Where(number => number != seriesPosition)
                .ToList();

            if (releaseNumbers.Count == 0 || releaseNumbers.Any(bookNumbers.Contains))
            {
                return null;
            }

            return $"{releaseNumbers[0]} (wanted {bookNumbers[0]})";
        }

        private static bool LooksLikeOtherAuthor(string candidate, Author expectedAuthor, IEnumerable<Book> books)
        {
            if (candidate.IsNullOrWhiteSpace() || candidate.IndexOfAny(new[] { '[', ']', '(', ')', ':' }) >= 0)
            {
                return false;
            }

            var words = candidate.Split(new[] { ' ', ',' }, StringSplitOptions.RemoveEmptyEntries);
            if (words.Length < 2 || words.Length > 4 ||
                words.Any(word => !NameTokenRegex.IsMatch(word) || NonNameWords.Contains(word.Trim('.'))))
            {
                return false;
            }

            if (PlaceholderAuthors.Contains(candidate) ||
                ReleaseIdentityEvidence.NameMatchesAuthor(expectedAuthor, candidate))
            {
                return false;
            }

            return !OverlapsBookWords(candidate, books) && !IsKnownNarrator(candidate, books);
        }

        // A segment that shares a significant word with the wanted title or series is more likely
        // the title than a different author ("Naturals - Jennifer ...").
        private static bool OverlapsBookWords(string candidate, IEnumerable<Book> books)
        {
            var bookTokens = new HashSet<string>(
                (books ?? Enumerable.Empty<Book>())
                    .Where(book => book != null)
                    .SelectMany(book => new[] { book.Title, book.SeriesName })
                    .Where(value => value.IsNotNullOrWhiteSpace())
                    .SelectMany(ReleaseTitleMatchScorer.Tokenize)
                    .Where(token => !Stopwords.Contains(token)),
                StringComparer.OrdinalIgnoreCase);

            return ReleaseTitleMatchScorer.Tokenize(candidate)
                .Where(token => !Stopwords.Contains(token))
                .Any(bookTokens.Contains);
        }

        private static bool IsKnownNarrator(string candidate, IEnumerable<Book> books)
        {
            var narrators = (books ?? Enumerable.Empty<Book>())
                .Where(book => book != null)
                .SelectMany(book => new[] { book.Narrator }
                    .Concat((book.Editions ?? new List<Edition>())
                        .Where(edition => edition != null)
                        .SelectMany(edition => new[] { edition.Narrator }.Concat(edition.NarratorNames ?? new List<string>()))))
                .Where(name => name.IsNotNullOrWhiteSpace());

            return narrators.Any(narrator => AuthorNameMatcher.AuthorNamesMatch(narrator, candidate));
        }

        private static IEnumerable<string> Numbers(string text)
        {
            return ReleaseTitleMatchScorer.Tokenize(text)
                .Where(token => token.All(char.IsDigit) && !YearRegex.IsMatch(token))
                .Select(NormalizeNumber)
                .Where(number => number != null)
                .Distinct();
        }

        private static string NormalizeNumber(string token)
        {
            if (token.IsNullOrWhiteSpace() ||
                !decimal.TryParse(token.Trim().TrimStart('#'), NumberStyles.Number, CultureInfo.InvariantCulture, out var number))
            {
                return null;
            }

            return number.ToString("0.############################", CultureInfo.InvariantCulture);
        }
    }
}
