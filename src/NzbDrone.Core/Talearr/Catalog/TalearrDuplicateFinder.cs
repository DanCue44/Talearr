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
        public string Key { get; set; }
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

    // TaleArr: "possible duplicates" for the merge tool. Authors match on a normalized name;
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

            return Build(authors, books, filesByBook, ids => conn.Query<(int BookId, int Count)>(
                    @"SELECT ""BookId"", COUNT(*) FROM ""Editions"" WHERE ""BookId"" IN @Ids GROUP BY ""BookId""", new { Ids = ids })
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

            foreach (var group in authors
                         .Select(a => (Key: NormalizeAuthorName(a.Name), Author: a))
                         .Where(x => x.Key.Length > 0)
                         .GroupBy(x => x.Key, StringComparer.Ordinal)
                         .Where(g => g.Count() > 1))
            {
                result.Authors.Add(new TalearrDuplicateAuthorSet
                {
                    Key = group.Key,
                    Authors = group.Select(x =>
                    {
                        var own = booksByAuthor.TryGetValue(x.Author.Id, out var list) ? list : new List<BookFact>();
                        return new TalearrDuplicateAuthorItem
                        {
                            Id = x.Author.Id,
                            Name = x.Author.Name,
                            Path = x.Author.Path,
                            BookCount = own.Count,
                            FileCount = own.Sum(b => filesByBook.TryGetValue(b.Id, out var c) ? c : 0),
                            ProviderIds = x.Author.ProviderIds()
                        };
                    })
                    .OrderByDescending(a => a.FileCount)
                    .ThenByDescending(a => a.BookCount)
                    .ThenBy(a => a.Id)
                    .ToList()
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

            result.Authors = result.Authors.OrderBy(a => a.Key, StringComparer.Ordinal).ToList();
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
            if (name.IsNullOrWhiteSpace())
            {
                return string.Empty;
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

            var tokens = builder.ToString().Split(' ', StringSplitOptions.RemoveEmptyEntries);
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
