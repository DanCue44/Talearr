using System;
using System.Collections.Generic;
using System.Linq;
using NzbDrone.Core.Books;

namespace Chaptarr.Api.V1.Books
{
    public class BookFormatResource
    {
        public string MediaType { get; set; }
        public int BookId { get; set; }
        public bool HasFile { get; set; }
        public bool Monitored { get; set; }
    }

    // Talearr: an ebook and an audiobook of the same work are separate book rows. The All formats
    // book index shows one entry per work: rows of the same author with the same foreignBookId
    // join one group, rows without a foreignBookId stay alone, and a group sits where its first
    // row was in the sorted list.
    public static class BookFormatGroups
    {
        public static string GetWorkKey(Book book, Func<Book, string> foreignBookId)
        {
            var workId = foreignBookId(book);

            return string.IsNullOrWhiteSpace(workId)
                ? $"id:{book.Id}"
                : $"{book.AuthorId}|{workId}";
        }

        public static List<List<Book>> Group(IEnumerable<Book> sortedBooks, Func<Book, string> foreignBookId)
        {
            var groups = new List<List<Book>>();
            var byKey = new Dictionary<string, List<Book>>(StringComparer.Ordinal);

            foreach (var book in sortedBooks)
            {
                var key = GetWorkKey(book, foreignBookId);

                if (byKey.TryGetValue(key, out var group))
                {
                    group.Add(book);
                    continue;
                }

                group = new List<Book> { book };
                byKey[key] = group;
                groups.Add(group);
            }

            return groups;
        }

        // The row a combined entry opens: the format with files, else the audiobook, else the first.
        public static Book PickPrimary(IReadOnlyList<Book> group, Func<Book, bool> hasFile)
        {
            return group.FirstOrDefault(hasFile) ??
                   group.FirstOrDefault(b => b.MediaType != BookMediaType.Ebook) ??
                   group[0];
        }

        public static bool IsMonitored(Book book)
        {
            return book.MediaType == BookMediaType.Ebook ? book.EbookMonitored : book.AudiobookMonitored;
        }

        // Formats in a fixed order, eBook first, like the badges on the cards.
        public static List<BookFormatResource> ToFormats(IEnumerable<Book> group, Func<Book, bool> hasFile)
        {
            return group
                .OrderBy(b => b.MediaType == BookMediaType.Ebook ? 0 : 1)
                .ThenBy(b => b.Id)
                .Select(b => new BookFormatResource
                {
                    MediaType = b.MediaType == BookMediaType.Ebook ? "ebook" : "audiobook",
                    BookId = b.Id,
                    HasFile = hasFile(b),
                    Monitored = IsMonitored(b)
                })
                .ToList();
        }

        public static BookStatisticsResource SumStatistics(IEnumerable<BookStatisticsResource> statistics)
        {
            var list = statistics.Where(s => s != null).ToList();

            return new BookStatisticsResource
            {
                BookCount = list.Sum(s => s.BookCount),
                BookFileCount = list.Sum(s => s.BookFileCount),
                AvailableBookCount = list.Sum(s => s.AvailableBookCount),
                TotalBookCount = list.Sum(s => s.TotalBookCount),
                SizeOnDisk = list.Sum(s => s.SizeOnDisk)
            };
        }
    }
}
