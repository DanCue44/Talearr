using System;
using System.Collections.Generic;
using System.Linq;
using NzbDrone.Core.Books;

namespace NzbDrone.Core.Talearr.Recordings
{
    // The audiobook rows of one work: the base row plus upstream's wanted and copy rows.
    public sealed class RecordingGroup
    {
        public Book Primary { get; set; }
        public List<Book> Books { get; set; } = new List<Book>();

        public IEnumerable<Book> Copies => Books.Where(b => Primary == null || b.Id != Primary.Id);
    }

    public interface ITalearrRecordingGroupService
    {
        // bookId may be any row of the work, ebook rows included. Null when the book is unknown
        // or the work has no audiobook rows.
        RecordingGroup GetGroup(int bookId);

        RecordingGroup GetGroup(Book anyRow);
    }

    // Talearr: finds a work's audiobook rows the way upstream's import routing does
    // (BookUnitDestinationService.GetIdentityGroupBooks): same author and media type, and the same
    // BaseBookId or intersecting provider ids. Read only; Talearr never forks upstream's copy logic.
    public class TalearrRecordingGroupService : ITalearrRecordingGroupService
    {
        private readonly IBookService _bookService;

        public TalearrRecordingGroupService(IBookService bookService)
        {
            _bookService = bookService;
        }

        public static bool IsCopySlug(string titleSlug)
        {
            return !string.IsNullOrWhiteSpace(titleSlug) &&
                   (titleSlug.Contains("_wanted_", StringComparison.OrdinalIgnoreCase) ||
                    titleSlug.Contains("_copy_", StringComparison.OrdinalIgnoreCase));
        }

        public RecordingGroup GetGroup(int bookId)
        {
            return GetGroup(_bookService.GetBook(bookId));
        }

        public RecordingGroup GetGroup(Book anyRow)
        {
            if (anyRow == null || anyRow.AuthorId <= 0)
            {
                return null;
            }

            var authorBooks = _bookService.GetBooksByAuthor(anyRow.AuthorId) ?? new List<Book>();
            var audiobooks = authorBooks.Where(b => b != null && b.Id > 0 && b.MediaType == BookMediaType.Audiobook).ToList();

            var seeds = new List<Book> { anyRow };
            if (anyRow.MediaType != BookMediaType.Audiobook)
            {
                // From an ebook row, start from the audiobook rows of the same work.
                seeds = audiobooks.Where(b => SameWork(anyRow, b)).ToList();
            }

            var group = audiobooks
                .Where(b => seeds.Any(seed => b.Id == seed.Id || SameGroup(seed, b)))
                .GroupBy(b => b.Id)
                .Select(g => g.First())
                .OrderBy(b => b.Id)
                .ToList();

            if (group.Count == 0)
            {
                return null;
            }

            return new RecordingGroup
            {
                Books = group,
                Primary = group.FirstOrDefault(b => !IsCopySlug(b.TitleSlug)) ?? group.First()
            };
        }

        private static bool SameGroup(Book seed, Book candidate)
        {
            if (seed.MediaType != candidate.MediaType)
            {
                return false;
            }

            if (!string.IsNullOrWhiteSpace(seed.BaseBookId) &&
                string.Equals(seed.BaseBookId, candidate.BaseBookId, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            return BookIdentity.MatchesByProviderIdIntersection(candidate, seed);
        }

        private static bool SameWork(Book ebook, Book audiobook)
        {
            if (!string.IsNullOrWhiteSpace(ebook.BaseBookId) &&
                string.Equals(ebook.BaseBookId, audiobook.BaseBookId, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            return WorkIdMatcher.WorkProviderIdMatches(ebook, audiobook);
        }
    }
}
