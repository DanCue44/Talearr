using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using NzbDrone.Common.Extensions;
using NzbDrone.Core.Books;
using NzbDrone.Core.Parser;

namespace NzbDrone.Core.Talearr.Audiobookshelf
{
    // What the matcher needs from TaleArr's library. TalearrAbsBookLookup is the real one; tests
    // use an in-memory one.
    public interface ITalearrAbsBookLookup
    {
        // Book rows with at least one file at or under the path.
        List<Book> GetBooksWithFilesUnder(string localPath);

        List<Book> FindBooksByAsin(string asin);
        List<Book> FindBooksByIsbn(string isbn13);
        List<Author> FindAuthors(string normalizedName);
        List<Book> GetBooksByAuthor(int authorId);
    }

    // TaleArr: matches one Audiobookshelf item to TaleArr book rows.
    //
    //   1. path: the item's folder (through the library mapping) holds files TaleArr imported.
    //      Those rows are "managed": shown as in Audiobookshelf, never a reason to skip a grab.
    //   2. asin, then isbn: an edition with the same identifier.
    //   3. title: an author with the same name, then the release title scorer
    //      (docs/TALEARR_MATCHING.md) with the item's title, and with title plus subtitle. Audiobookshelf
    //      titles are clean, so nothing may be left over: "Dune Messiah" never matches "Dune".
    //
    // A match is widened to every row of the same work and media type (the primary audiobook
    // row and its recordings, or the ebook rows), because which recording an item satisfies
    // depends on the release being judged; TalearrAbsLibraryService decides that.
    public static class TalearrAbsMatcher
    {
        public static List<TalearrAbsItemBook> Match(TalearrAbsItem item, ITalearrAbsBookLookup lookup)
        {
            var result = new List<TalearrAbsItemBook>();
            if (item == null || lookup == null)
            {
                return result;
            }

            var wanted = new HashSet<BookMediaType>();
            if (item.HasAudio)
            {
                wanted.Add(BookMediaType.Audiobook);
            }

            if (item.HasEbook)
            {
                wanted.Add(BookMediaType.Ebook);
            }

            if (wanted.Count == 0)
            {
                return result;
            }

            // 1. TaleArr's own files. A media type TaleArr has files for in this folder is settled.
            if (item.LocalPath.IsNotNullOrWhiteSpace())
            {
                var managed = (lookup.GetBooksWithFilesUnder(item.LocalPath) ?? new List<Book>())
                    .Where(book => book != null && wanted.Contains(book.MediaType))
                    .ToList();

                foreach (var book in managed)
                {
                    Add(result, item, book.Id, TalearrAbsMatchMethods.Path, true);
                }

                wanted.ExceptWith(managed.Select(book => book.MediaType));

                if (wanted.Count == 0)
                {
                    return result;
                }
            }

            // 2 and 3. Identify the work.
            var (seeds, method) = FindSeeds(item, lookup, wanted);
            if (seeds.Count == 0)
            {
                return result;
            }

            foreach (var row in ExpandToWorkRows(seeds, lookup, wanted))
            {
                Add(result, item, row.Id, method, false);
            }

            return result;
        }

        private static (List<Book> Seeds, string Method) FindSeeds(TalearrAbsItem item, ITalearrAbsBookLookup lookup, ISet<BookMediaType> wanted)
        {
            if (item.Asin.IsNotNullOrWhiteSpace())
            {
                var byAsin = Clean(lookup.FindBooksByAsin(item.Asin));
                if (byAsin.Count > 0)
                {
                    return (byAsin, TalearrAbsMatchMethods.Asin);
                }
            }

            if (item.Isbn.IsNotNullOrWhiteSpace())
            {
                var byIsbn = Clean(lookup.FindBooksByIsbn(item.Isbn));
                if (byIsbn.Count > 0)
                {
                    return (byIsbn, TalearrAbsMatchMethods.Isbn);
                }
            }

            var byTitle = FindByTitle(item, lookup, wanted);
            return (byTitle, TalearrAbsMatchMethods.Title);
        }

        private static List<Book> FindByTitle(TalearrAbsItem item, ITalearrAbsBookLookup lookup, ISet<BookMediaType> wanted)
        {
            var seeds = new List<Book>();
            if (item.Title.IsNullOrWhiteSpace() || item.Authors.IsNullOrWhiteSpace())
            {
                return seeds;
            }

            var titles = new List<string> { item.Title };
            if (item.Subtitle.IsNotNullOrWhiteSpace() &&
                item.Title.IndexOf(item.Subtitle, StringComparison.OrdinalIgnoreCase) < 0)
            {
                titles.Add($"{item.Title}: {item.Subtitle}");
            }

            foreach (var authorName in SplitAuthors(item.Authors))
            {
                foreach (var author in lookup.FindAuthors(NormalizeName(authorName)) ?? new List<Author>())
                {
                    if (author == null)
                    {
                        continue;
                    }

                    var catalog = Clean(lookup.GetBooksByAuthor(author.Id));
                    var candidates = catalog.Where(b => wanted.Contains(b.MediaType)).ToList();
                    if (candidates.Count == 0)
                    {
                        // Only the other media type is in TaleArr: still find the work, so an
                        // audiobook in Audiobookshelf can be widened to the ebook row and back.
                        candidates = catalog;
                    }

                    foreach (var title in titles)
                    {
                        var match = ReleaseTitleMatchScorer.FindBestMatch(title, author.Name, candidates, item.Authors, catalog);
                        if (match?.IsMatch == true && match.Book != null && match.MeaningfulLeftoverCount == 0)
                        {
                            seeds.Add(match.Book);
                            break;
                        }
                    }
                }
            }

            return seeds;
        }

        // Every row of the seeds' works with one of the wanted media types.
        public static List<Book> ExpandToWorkRows(IEnumerable<Book> seeds, ITalearrAbsBookLookup lookup, ISet<BookMediaType> wanted)
        {
            var rows = new Dictionary<int, Book>();

            foreach (var seed in seeds.Where(s => s != null && s.Id > 0))
            {
                var catalog = seed.AuthorId > 0 ? Clean(lookup.GetBooksByAuthor(seed.AuthorId)) : new List<Book>();
                if (!catalog.Any(b => b.Id == seed.Id))
                {
                    catalog.Add(seed);
                }

                foreach (var candidate in catalog)
                {
                    if (!wanted.Contains(candidate.MediaType) || rows.ContainsKey(candidate.Id))
                    {
                        continue;
                    }

                    if (candidate.Id == seed.Id || SameWork(seed, candidate))
                    {
                        rows[candidate.Id] = candidate;
                    }
                }
            }

            return rows.Values.OrderBy(b => b.Id).ToList();
        }

        public static bool SameWork(Book seed, Book candidate)
        {
            if (seed == null || candidate == null)
            {
                return false;
            }

            if (seed.BaseBookId.IsNotNullOrWhiteSpace() &&
                string.Equals(seed.BaseBookId, candidate.BaseBookId, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            if (seed.MediaType == candidate.MediaType)
            {
                return BookIdentity.MatchesByProviderIdIntersection(candidate, seed);
            }

            return WorkIdMatcher.WorkProviderIdMatches(seed, candidate);
        }

        public static IEnumerable<string> SplitAuthors(string authors)
        {
            if (authors.IsNullOrWhiteSpace())
            {
                return Array.Empty<string>();
            }

            return authors
                .Split(new[] { ", ", " & ", ";" }, StringSplitOptions.RemoveEmptyEntries)
                .Select(a => a.Trim())
                .Where(a => a.Length > 0)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        // Lower case letters and digits only, accents removed: "R.F. Kuang" == "R. F. Kuang".
        public static string NormalizeName(string name)
        {
            if (name.IsNullOrWhiteSpace())
            {
                return string.Empty;
            }

            var decomposed = name.Normalize(NormalizationForm.FormD);
            var builder = new StringBuilder(decomposed.Length);
            foreach (var c in decomposed)
            {
                if (CharUnicodeInfo.GetUnicodeCategory(c) == UnicodeCategory.NonSpacingMark)
                {
                    continue;
                }

                if (char.IsLetterOrDigit(c))
                {
                    builder.Append(char.ToLowerInvariant(c));
                }
            }

            return builder.ToString();
        }

        private static List<Book> Clean(IEnumerable<Book> books)
        {
            return (books ?? Enumerable.Empty<Book>()).Where(b => b != null && b.Id > 0).GroupBy(b => b.Id).Select(g => g.First()).ToList();
        }

        private static void Add(List<TalearrAbsItemBook> result, TalearrAbsItem item, int bookId, string method, bool managed)
        {
            if (bookId <= 0 || result.Any(m => m.BookId == bookId))
            {
                return;
            }

            result.Add(new TalearrAbsItemBook
            {
                ConnectionId = item.ConnectionId,
                ItemId = item.ItemId,
                BookId = bookId,
                Method = method,
                Managed = managed
            });
        }
    }
}
