using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using NLog;
using NzbDrone.Core.MetadataSource.BookInfo;

namespace NzbDrone.Core.Talearr.Metadata.Source
{
    public interface ITalearrSourceMatcher
    {
        // Candidates in the shape the metadata server's /api/v5/match returns, best first.
        List<V5MatchedAuthor> Match(string query, IDictionary<string, List<string>> tags);
    }

    // Talearr: import matching against Hardcover in place of the metadata server's /api/v5/match
    // when Talearr's source is selected. ISBNs and ASINs found in the file tags are looked up
    // first (an exact edition), then Hardcover's book search runs on the same query the server
    // would have received. Callers keep their own checks (the author must appear in the tags),
    // so a loose search hit cannot import a file under the wrong author.
    public class TalearrSourceMatcher : ITalearrSourceMatcher
    {
        public const int SearchLimit = 5;

        private static readonly Regex IsbnPattern = new Regex(@"(?<![\dX])(97[89]\d{10}|\d{9}[\dX])(?![\dX])", RegexOptions.Compiled | RegexOptions.IgnoreCase);
        private static readonly Regex AsinPattern = new Regex(@"\b(B0[0-9A-Z]{8})\b", RegexOptions.Compiled);

        private readonly ITalearrHardcoverMetadataClient _hardcover;
        private readonly Logger _logger;

        public TalearrSourceMatcher(ITalearrHardcoverMetadataClient hardcover, Logger logger)
        {
            _hardcover = hardcover;
            _logger = logger;
        }

        public List<V5MatchedAuthor> Match(string query, IDictionary<string, List<string>> tags)
        {
            var result = new List<V5MatchedAuthor>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            try
            {
                ExtractIdentifiers(tags, out var isbns, out var asins);
                if (isbns.Count > 0 || asins.Count > 0)
                {
                    var editions = _hardcover.FindEditions(isbns, asins);
                    var authorIds = new Dictionary<int, HardcoverBookAuthors>();
                    foreach (var edition in editions)
                    {
                        if (!authorIds.TryGetValue(edition.BookId, out var credits))
                        {
                            credits = _hardcover.GetBookAuthors(edition.BookId);
                            authorIds[edition.BookId] = credits;
                        }

                        if (credits?.AuthorIds.Count > 0)
                        {
                            Add(result, seen, credits.AuthorIds[0], null, credits.BookId, null, edition.EditionId);
                        }
                    }

                    // Identifier hits carry no names; one request names them all.
                    var unnamed = result.Where(r => string.IsNullOrWhiteSpace(r.name)).ToList();
                    if (unnamed.Count > 0)
                    {
                        var names = _hardcover.GetAuthorSignatures(unnamed.Select(r => int.Parse(r.id.Substring(3))).Distinct().ToList());
                        foreach (var match in unnamed)
                        {
                            if (names.TryGetValue(int.Parse(match.id.Substring(3)), out var signature))
                            {
                                match.name = signature.Name;
                            }
                        }
                    }
                }

                if (!string.IsNullOrWhiteSpace(query))
                {
                    foreach (var hit in _hardcover.SearchBooks(query, SearchLimit))
                    {
                        Add(result, seen, hit.AuthorId, hit.AuthorName, hit.BookId, hit.Title, null);
                    }
                }
            }
            catch (TalearrHardcoverException ex)
            {
                _logger.Debug("[TALEARR-MATCH] Hardcover matching failed for '{0}': {1}", query, ex.Message);
            }

            _logger.Debug("[TALEARR-MATCH] '{0}': {1} candidate(s) from Hardcover", query, result.Count);
            return result.Where(r => !string.IsNullOrWhiteSpace(r.name)).ToList();
        }

        internal static void ExtractIdentifiers(IDictionary<string, List<string>> tags, out List<string> isbns, out List<string> asins)
        {
            isbns = new List<string>();
            asins = new List<string>();
            foreach (var value in (tags ?? new Dictionary<string, List<string>>()).SelectMany(kvp => kvp.Value ?? new List<string>()))
            {
                if (string.IsNullOrWhiteSpace(value) || value.Length > 200)
                {
                    continue;
                }

                var compact = value.Replace("-", string.Empty).Replace(" ", string.Empty);
                foreach (Match match in IsbnPattern.Matches(compact))
                {
                    var isbn = match.Value.ToUpperInvariant();
                    if (IsValidIsbn(isbn) && !isbns.Contains(isbn))
                    {
                        isbns.Add(isbn);
                    }
                }

                foreach (Match match in AsinPattern.Matches(value.ToUpperInvariant()))
                {
                    if (!asins.Contains(match.Value))
                    {
                        asins.Add(match.Value);
                    }
                }
            }
        }

        internal static bool IsValidIsbn(string isbn)
        {
            if (isbn.Length == 13)
            {
                var sum = 0;
                for (var i = 0; i < 13; i++)
                {
                    sum += (isbn[i] - '0') * (i % 2 == 0 ? 1 : 3);
                }

                return sum % 10 == 0;
            }

            if (isbn.Length == 10)
            {
                var sum = 0;
                for (var i = 0; i < 10; i++)
                {
                    var digit = i == 9 && isbn[i] == 'X' ? 10 : isbn[i] - '0';
                    if (digit < 0 || digit > 10)
                    {
                        return false;
                    }

                    sum += digit * (10 - i);
                }

                return sum % 11 == 0;
            }

            return false;
        }

        private static void Add(List<V5MatchedAuthor> result, HashSet<string> seen, int authorId, string authorName, int bookId, string title, int? editionId)
        {
            var key = $"{authorId}:{bookId}";
            if (authorId <= 0 || bookId <= 0 || !seen.Add(key))
            {
                return;
            }

            result.Add(new V5MatchedAuthor
            {
                id = TalearrHardcoverMapper.AuthorId(authorId),
                name = authorName,
                work_id = TalearrHardcoverMapper.WorkId(bookId),
                work_title = title,
                edition_hardcover_id = editionId?.ToString(System.Globalization.CultureInfo.InvariantCulture)
            });
        }
    }
}
