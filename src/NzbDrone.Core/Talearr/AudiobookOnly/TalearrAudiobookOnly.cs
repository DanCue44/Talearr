using System;
using System.Collections.Generic;
using System.Linq;
using NLog;
using NzbDrone.Common.Extensions;
using NzbDrone.Core.Books;

namespace NzbDrone.Core.Talearr.AudiobookOnly
{
    // Talearr: Audiobook Only. A view and wanted filter, never a delete or an unmonitor.
    //
    // While it is on, a book row counts only when it is an audiobook row with a known audiobook
    // edition: an edition with the audio reading format, an Audible ASIN, the "Audiobook" format
    // or a running time, or an Audible ASIN on the book itself. Ebook rows and audiobook rows of
    // books that only exist in print or as ebooks are left out of Wanted Missing, the missing
    // searches, the calendar, the author and series counts and the author and book lists, and
    // automatic grabs skip them. A row with files is always kept: it is in the library already.
    //
    // Everything is worked out from the stored editions on every query, so when a metadata refresh
    // brings in an audiobook edition the book shows again (and is wanted again if it is monitored)
    // with nothing else to do.
    public static class TalearrAudiobookOnly
    {
        public const int AudioReadingFormatId = 2;

        // Audio evidence for the book row referenced by bookReference (a quoted table name or alias).
        public static string AudioEvidenceSql(string bookReference)
        {
            return $@"(COALESCE({bookReference}.""AudibleASIN"", '') <> '' OR EXISTS (
                        SELECT 1 FROM ""Editions"" aol_e
                        WHERE aol_e.""BookId"" = {bookReference}.""Id""
                          AND (aol_e.""ReadingFormatId"" = {AudioReadingFormatId}
                               OR COALESCE(aol_e.""AudibleASIN"", '') <> ''
                               OR LOWER(COALESCE(aol_e.""Format"", '')) = 'audiobook'
                               OR COALESCE(aol_e.""DurationSeconds"", 0) > 0)))";
        }

        public static string HasFilesSql(string bookReference)
        {
            return $@"EXISTS (
                        SELECT 1 FROM ""Editions"" aol_fe
                        INNER JOIN ""BookFiles"" aol_bf ON aol_bf.""EditionId"" = aol_fe.""Id""
                        WHERE aol_fe.""BookId"" = {bookReference}.""Id"")";
        }

        // Rows that can be missing and searched: audiobook rows with a known audiobook edition.
        public static string WantedSql(string bookReference)
        {
            return $@"({bookReference}.""MediaType"" = {(int)BookMediaType.Audiobook} AND {AudioEvidenceSql(bookReference)})";
        }

        // Rows that are shown: the wanted ones plus any row that already has files.
        public static string VisibleSql(string bookReference)
        {
            return $@"({WantedSql(bookReference)} OR {HasFilesSql(bookReference)})";
        }

        public static bool IsAudioEdition(Edition edition)
        {
            if (edition == null)
            {
                return false;
            }

            return edition.ReadingFormatId == AudioReadingFormatId ||
                   edition.AudibleASIN.IsNotNullOrWhiteSpace() ||
                   string.Equals(edition.Format?.Trim(), "audiobook", StringComparison.OrdinalIgnoreCase) ||
                   (edition.DurationSeconds ?? 0) > 0;
        }

        // The in-memory twin of WantedSql, for books whose editions are already loaded.
        public static bool HasAudiobookEdition(Book book, IEnumerable<Edition> editions)
        {
            if (book == null || book.MediaType != BookMediaType.Audiobook)
            {
                return false;
            }

            return book.AudibleASIN.IsNotNullOrWhiteSpace() ||
                   (editions ?? Enumerable.Empty<Edition>()).Any(IsAudioEdition);
        }
    }

    public interface ITalearrAudiobookOnlyService
    {
        bool Enabled { get; }

        // The given rows that Audiobook Only hides. Empty while the setting is off.
        HashSet<int> HiddenBookIds(IEnumerable<int> bookIds);

        // The rows that are shown, in their original order. The list itself while the setting is off.
        List<Book> Visible(List<Book> books);
    }

    public class TalearrAudiobookOnlyService : ITalearrAudiobookOnlyService
    {
        private readonly IBookRepository _bookRepository;
        private readonly ITalearrAudiobookOnlySettingsService _settings;
        private readonly Logger _logger;

        // Reads through the book repository: Talearr code does not query chaptarr.db itself.
        public TalearrAudiobookOnlyService(IBookRepository bookRepository, ITalearrAudiobookOnlySettingsService settings, Logger logger)
        {
            _bookRepository = bookRepository;
            _settings = settings;
            _logger = logger;
        }

        public bool Enabled => TalearrAudiobookOnlySettingsService.Resolve(_settings);

        public HashSet<int> HiddenBookIds(IEnumerable<int> bookIds)
        {
            var hidden = new HashSet<int>();
            var ids = bookIds?.Where(id => id > 0).Distinct().ToList() ?? new List<int>();

            if (ids.Count == 0 || !Enabled)
            {
                return hidden;
            }

            try
            {
                hidden.UnionWith(_bookRepository.GetAudiobookOnlyHiddenIds(ids) ?? new List<int>());
            }
            catch (Exception ex)
            {
                // Showing too much beats hiding books by mistake.
                _logger.Warn(ex, "Talearr: Audiobook Only could not check {0} book(s); showing them", ids.Count);
                hidden.Clear();
            }

            return hidden;
        }

        public List<Book> Visible(List<Book> books)
        {
            if (books == null || books.Count == 0 || !Enabled)
            {
                return books;
            }

            var hidden = HiddenBookIds(books.Where(b => b != null).Select(b => b.Id));
            return hidden.Count == 0 ? books : books.Where(b => b == null || !hidden.Contains(b.Id)).ToList();
        }
    }
}
