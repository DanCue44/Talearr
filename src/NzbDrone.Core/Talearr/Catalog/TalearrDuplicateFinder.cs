using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using Dapper;
using NzbDrone.Common.Extensions;
using NzbDrone.Core.Books;
using NzbDrone.Core.Datastore;
using NzbDrone.Core.Parser;

namespace NzbDrone.Core.Talearr.Catalog
{
    public sealed class TalearrDuplicateAuthorItem
    {
        public int Id { get; set; }
        public string Name { get; set; }
        public string Path { get; set; }
        public int BookCount { get; set; }
        public int FileCount { get; set; }
        public List<string> ProviderIds { get; set; } = new List<string>();
    }

    public sealed class TalearrDuplicateAuthorSet
    {
        public const string ExactMatch = "exact";
        public const string MiddleNameMatch = "middleName";

        public string Key { get; set; }

        // "exact": the names normalize the same. "middleName": same first and last name, one has
        // middle names or initials the other lacks ("Dietmar Wehr" ~ "Dietmar A. Wehr"). Only a
        // possible match, for a person to check.
        public string Match { get; set; } = ExactMatch;
        public List<TalearrDuplicateAuthorItem> Authors { get; set; } = new List<TalearrDuplicateAuthorItem>();
    }

    public sealed class TalearrDuplicateBookItem
    {
        public int Id { get; set; }
        public string Title { get; set; }
        public bool Monitored { get; set; }
        public int EditionCount { get; set; }
        public int FileCount { get; set; }
        public DateTime? ReleaseDate { get; set; }
        public List<string> WorkIds { get; set; } = new List<string>();

        // A separate recording copy (UnitKeyHash): merging it needs force.
        public bool RecordingCopy { get; set; }
    }

    public sealed class TalearrDuplicateBookSet
    {
        public string Key { get; set; }
        public int AuthorId { get; set; }
        public string AuthorName { get; set; }
        public string MediaType { get; set; }
        public string Title { get; set; }
        public List<TalearrDuplicateBookItem> Books { get; set; } = new List<TalearrDuplicateBookItem>();
    }

    public sealed class TalearrDuplicateCandidates
    {
        public List<TalearrDuplicateAuthorSet> Authors { get; set; } = new List<TalearrDuplicateAuthorSet>();
        public List<TalearrDuplicateBookSet> Books { get; set; } = new List<TalearrDuplicateBookSet>();
    }

    public interface ITalearrDuplicateFinder
    {
        TalearrDuplicateCandidates Find();
    }

    // Talearr: "possible duplicates" for the merge tool. Authors match on a normalized name, and
    // possibly on first + last name when only middle names or initials differ;
    // books match on author + media type + normalized title. Ebook and audiobook rows of one work
    // never group together (they are siblings). Read-only, lean column selects.
    public class TalearrDuplicateFinder : ITalearrDuplicateFinder
    {
        private readonly IMainDatabase _database;

        public TalearrDuplicateFinder(IMainDatabase database)
        {
            _database = database;
        }

        public TalearrDuplicateCandidates Find()
        {
            using var conn = _database.OpenConnection();

            var authors = conn.Query<AuthorFact>(
                @"SELECT ""Id"", ""Name"", ""Path"", ""GoodreadsAuthorId"", ""HardcoverAuthorId"", ""OpenLibraryAuthorId"", ""AudnexusAuthorId"", ""GoogleBooksAuthorId""
                  FROM ""Authors""").ToList();
            var books = conn.Query<BookFact>(
                @"SELECT ""Id"", ""AuthorId"", ""Title"", ""MediaType"", ""AudiobookMonitored"", ""EbookMonitored"", ""UnitKeyHash"", ""ReleaseDate"",
                         ""HardcoverBookId"", ""GoodreadsWorkId"", ""OpenLibraryWorkId""
                  FROM ""Books""").ToList();
            var filesByBook = conn.Query<(int BookId, int Count)>(
                    @"SELECT e.""BookId"", COUNT(f.""Id"") FROM ""BookFiles"" f JOIN ""Editions"" e ON e.""Id"" = f.""EditionId"" GROUP BY e.""BookId""")
                .ToDictionary(x => x.BookId, x => x.Count);

            // Arrays, not List<int>: the mapper serializes List<int> parameters as JSON.
            var inClause = _database.DatabaseType == DatabaseType.PostgreSQL ? @"= ANY(@Ids)" : @"IN @Ids";
            return Build(authors, books, filesByBook, ids => conn.Query<(int BookId, int Count)>(
                    $@"SELECT ""BookId"", COUNT(*) FROM ""Editions"" WHERE ""BookId"" {inClause} GROUP BY ""BookId""", new { Ids = ids.ToArray() })
                .ToDictionary(x => x.BookId, x => x.Count));
        }

        internal static TalearrDuplicateCandidates Build(List<AuthorFact> authors,
                                                         List<BookFact> books,
                                                         Dictionary<int, int> filesByBook,
                                                         Func<List<int>, Dictionary<int, int>> editionCounts)
        {
            var result = new TalearrDuplicateCandidates();
            var authorNames = authors.ToDictionary(a => a.Id, a => a.Name);
            var booksByAuthor = books.GroupBy(b => b.AuthorId).ToDictionary(g => g.Key, g => g.ToList());

            List<TalearrDuplicateAuthorItem> AuthorItems(IEnumerable<AuthorFact> group)
            {
                return group.Select(author =>
                {
                    var own = booksByAuthor.TryGetValue(author.Id, out var list) ? list : new List<BookFact>();
                    return new TalearrDuplicateAuthorItem
                    {
                        Id = author.Id,
                        Name = author.Name,
                        Path = author.Path,
                        BookCount = own.Count,
                        FileCount = own.Sum(b => filesByBook.TryGetValue(b.Id, out var c) ? c : 0),
                        ProviderIds = author.ProviderIds()
                    };
                })
                .OrderByDescending(a => a.FileCount)
                .ThenByDescending(a => a.BookCount)
                .ThenBy(a => a.Id)
                .ToList();
            }

            var exactGroups = authors
                .Select(a => (Key: NormalizeAuthorName(a.Name), Author: a))
                .Where(x => x.Key.Length > 0)
                .GroupBy(x => x.Key, StringComparer.Ordinal)
                .ToList();

            foreach (var group in exactGroups.Where(g => g.Count() > 1))
            {
                result.Authors.Add(new TalearrDuplicateAuthorSet
                {
                    Key = group.Key,
                    Authors = AuthorItems(group.Select(x => x.Author))
                });
            }

            // Looser sets: exact groups whose names agree on first and last name and whose middle
            // names do not conflict. Listed as possible matches next to the exact ones.
            var namesByKey = exactGroups.ToDictionary(g => g.Key, g => g.First().Author.Name, StringComparer.Ordinal);
            foreach (var looseSet in FindMiddleNameSets(namesByKey))
            {
                result.Authors.Add(new TalearrDuplicateAuthorSet
                {
                    Key = "~" + string.Join("|", looseSet),
                    Match = TalearrDuplicateAuthorSet.MiddleNameMatch,
                    Authors = AuthorItems(exactGroups.Where(g => looseSet.Contains(g.Key)).SelectMany(g => g.Select(x => x.Author)))
                });
            }

            var bookGroups = books
                .Select(b => (Key: BookKey(b), Book: b))
                .Where(x => x.Key != null)
                .GroupBy(x => x.Key, StringComparer.Ordinal)
                .Where(g => g.Count() > 1)
                .ToList();

            var candidateIds = bookGroups.SelectMany(g => g.Select(x => x.Book.Id)).ToList();
            var editions = new Dictionary<int, int>();
            foreach (var chunk in candidateIds.Chunk(500))
            {
                foreach (var kv in editionCounts(chunk.ToList()))
                {
                    editions[kv.Key] = kv.Value;
                }
            }

            foreach (var group in bookGroups)
            {
                var first = group.First().Book;
                result.Books.Add(new TalearrDuplicateBookSet
                {
                    Key = group.Key,
                    AuthorId = first.AuthorId,
                    AuthorName = authorNames.TryGetValue(first.AuthorId, out var name) ? name : null,
                    MediaType = first.MediaType == (int)BookMediaType.Ebook ? "ebook" : "audiobook",
                    Title = first.Title,
                    Books = group.Select(x => new TalearrDuplicateBookItem
                    {
                        Id = x.Book.Id,
                        Title = x.Book.Title,
                        Monitored = x.Book.AudiobookMonitored || x.Book.EbookMonitored,
                        EditionCount = editions.TryGetValue(x.Book.Id, out var e) ? e : 0,
                        FileCount = filesByBook.TryGetValue(x.Book.Id, out var f) ? f : 0,
                        ReleaseDate = x.Book.ReleaseDate,
                        WorkIds = x.Book.WorkIds(),
                        RecordingCopy = x.Book.UnitKeyHash.IsNotNullOrWhiteSpace()
                    })
                    .OrderByDescending(b => b.FileCount)
                    .ThenByDescending(b => b.Monitored)
                    .ThenBy(b => b.Id)
                    .ToList()
                });
            }

            result.Authors = result.Authors
                .OrderBy(a => a.Key.TrimStart('~'), StringComparer.Ordinal)
                .ThenBy(a => a.Match == TalearrDuplicateAuthorSet.ExactMatch ? 0 : 1)
                .ToList();
            result.Books = result.Books
                .OrderBy(b => b.AuthorName, StringComparer.OrdinalIgnoreCase)
                .ThenBy(b => b.Title, StringComparer.OrdinalIgnoreCase)
                .ThenBy(b => b.MediaType, StringComparer.Ordinal)
                .ToList();
            return result;
        }

        private static string BookKey(BookFact book)
        {
            var title = TalearrTitleNormalizer.NormalizeFull(book.Title);
            return title.IsNullOrWhiteSpace() ? null : $"{book.AuthorId}|{book.MediaType}|{title}";
        }

        // "Sager, Riley", "riley sager" and "Riley  Sager" agree; initials are joined so
        // "J.R.R. Tolkien" and "J. R. R. Tolkien" agree too.
        public static string NormalizeAuthorName(string name)
        {
            var tokens = NameTokens(name);
            var joined = new List<string>();
            var initials = new StringBuilder();
            foreach (var token in tokens)
            {
                if (token.Length == 1)
                {
                    initials.Append(token);
                    continue;
                }

                if (initials.Length > 0)
                {
                    joined.Add(initials.ToString());
                    initials.Clear();
                }

                joined.Add(token);
            }

            if (initials.Length > 0)
            {
                joined.Add(initials.ToString());
            }

            return string.Join(" ", joined);
        }

        // Lower-case, accent-free words of a name, "Last, First" turned around. Initials stay
        // separate words here ("J. R. R. Tolkien" gives j, r, r, tolkien).
        internal static string[] NameTokens(string name)
        {
            if (name.IsNullOrWhiteSpace())
            {
                return Array.Empty<string>();
            }

            var value = name.Trim();
            var comma = value.IndexOf(',');
            if (comma > 0 && comma == value.LastIndexOf(','))
            {
                value = value.Substring(comma + 1) + " " + value.Substring(0, comma);
            }

            var decomposed = value.Normalize(NormalizationForm.FormD);
            var builder = new StringBuilder(decomposed.Length);
            foreach (var c in decomposed)
            {
                if (CharUnicodeInfo.GetUnicodeCategory(c) == UnicodeCategory.NonSpacingMark)
                {
                    continue;
                }

                builder.Append(char.IsLetterOrDigit(c) ? char.ToLowerInvariant(c) : ' ');
            }

            return builder.ToString().Split(' ', StringSplitOptions.RemoveEmptyEntries);
        }

        // Same first and last name, and the shorter list of middle names fits into the longer one
        // in order, an initial standing for any name with that letter: "Dietmar Wehr",
        // "Dietmar A. Wehr" and "Dietmar Arthur Wehr" all agree; "John A. Smith" and
        // "John B. Smith" do not.
        public static bool MiddleNamesAgree(string left, string right)
        {
            var a = NameParts(left);
            var b = NameParts(right);
            if (a == null || b == null || a.First != b.First || a.Last != b.Last)
            {
                return false;
            }

            return MiddlesFit(a.Middles, b.Middles);
        }

        // Every largest set of exact keys whose names all agree with each other. A plain name that
        // fits two conflicting middle names ("John Smith" with "John A. Smith" and
        // "John B. Smith") lands in two sets, one per reading.
        internal static List<List<string>> FindMiddleNameSets(Dictionary<string, string> namesByKey)
        {
            var sets = new List<List<string>>();

            var buckets = namesByKey
                .Select(kv => (kv.Key, Parts: NameParts(kv.Value)))
                .Where(x => x.Parts != null)
                .GroupBy(x => x.Parts.First + "|" + x.Parts.Last, StringComparer.Ordinal)
                .Where(g => g.Count() > 1);

            foreach (var bucket in buckets)
            {
                var members = bucket.OrderBy(x => x.Key, StringComparer.Ordinal).ToList();

                // One bucket is one first + last name; a very large one is a common name, not a
                // duplicate case.
                if (members.Count > 25)
                {
                    continue;
                }

                var agree = new bool[members.Count, members.Count];
                for (var i = 0; i < members.Count; i++)
                {
                    for (var k = i + 1; k < members.Count; k++)
                    {
                        agree[i, k] = agree[k, i] = MiddlesFit(members[i].Parts.Middles, members[k].Parts.Middles);
                    }
                }

                foreach (var clique in MaximalCliques(members.Count, agree).Where(c => c.Count > 1))
                {
                    sets.Add(clique.OrderBy(i => i).Select(i => members[i].Key).ToList());
                }
            }

            return sets;
        }

        private static readonly HashSet<string> NameSuffixes = new HashSet<string>(StringComparer.Ordinal) { "jr", "sr", "ii", "iii", "iv" };

        // First name, middle names and last name (a "Jr."-style suffix stays on the last name).
        // Null for a one-word name, which has nothing to compare loosely.
        private static NamePartsResult NameParts(string name)
        {
            var tokens = NameTokens(name).ToList();
            var suffix = string.Empty;
            if (tokens.Count > 2 && NameSuffixes.Contains(tokens[tokens.Count - 1]))
            {
                suffix = " " + tokens[tokens.Count - 1];
                tokens.RemoveAt(tokens.Count - 1);
            }

            if (tokens.Count < 2)
            {
                return null;
            }

            return new NamePartsResult
            {
                First = tokens[0],
                Middles = tokens.Skip(1).Take(tokens.Count - 2).ToList(),
                Last = tokens[tokens.Count - 1] + suffix
            };
        }

        private static bool MiddlesFit(List<string> left, List<string> right)
        {
            var shorter = left.Count <= right.Count ? left : right;
            var longer = left.Count <= right.Count ? right : left;

            var j = 0;
            foreach (var token in shorter)
            {
                while (j < longer.Count && !SameNamePart(token, longer[j]))
                {
                    j++;
                }

                if (j == longer.Count)
                {
                    return false;
                }

                j++;
            }

            return true;
        }

        private static bool SameNamePart(string left, string right)
        {
            if (left == right)
            {
                return true;
            }

            return (left.Length == 1 && right[0] == left[0]) || (right.Length == 1 && left[0] == right[0]);
        }

        // Bron-Kerbosch over a small graph (one first + last name bucket).
        private static List<List<int>> MaximalCliques(int count, bool[,] agree)
        {
            var found = new List<List<int>>();

            void Expand(List<int> current, List<int> candidates, List<int> excluded)
            {
                if (candidates.Count == 0 && excluded.Count == 0)
                {
                    found.Add(current.ToList());
                    return;
                }

                foreach (var v in candidates.ToList())
                {
                    current.Add(v);
                    Expand(current, candidates.Where(u => agree[v, u]).ToList(), excluded.Where(u => agree[v, u]).ToList());
                    current.RemoveAt(current.Count - 1);
                    candidates.Remove(v);
                    excluded.Add(v);
                }
            }

            Expand(new List<int>(), Enumerable.Range(0, count).ToList(), new List<int>());
            return found;
        }

        private sealed class NamePartsResult
        {
            public string First { get; set; }
            public List<string> Middles { get; set; }
            public string Last { get; set; }
        }

        internal sealed class AuthorFact
        {
            public int Id { get; set; }
            public string Name { get; set; }
            public string Path { get; set; }
            public string GoodreadsAuthorId { get; set; }
            public string HardcoverAuthorId { get; set; }
            public string OpenLibraryAuthorId { get; set; }
            public string AudnexusAuthorId { get; set; }
            public string GoogleBooksAuthorId { get; set; }

            public List<string> ProviderIds()
            {
                return new[] { HardcoverAuthorId, GoodreadsAuthorId, OpenLibraryAuthorId, AudnexusAuthorId, GoogleBooksAuthorId }
                    .Where(id => id.IsNotNullOrWhiteSpace())
                    .ToList();
            }
        }

        internal sealed class BookFact
        {
            public int Id { get; set; }
            public int AuthorId { get; set; }
            public string Title { get; set; }
            public int MediaType { get; set; }
            public bool AudiobookMonitored { get; set; }
            public bool EbookMonitored { get; set; }
            public string UnitKeyHash { get; set; }
            public DateTime? ReleaseDate { get; set; }
            public string HardcoverBookId { get; set; }
            public string GoodreadsWorkId { get; set; }
            public string OpenLibraryWorkId { get; set; }

            public List<string> WorkIds()
            {
                return new[] { HardcoverBookId, GoodreadsWorkId, OpenLibraryWorkId }
                    .Where(id => id.IsNotNullOrWhiteSpace())
                    .ToList();
            }
        }
    }
}
