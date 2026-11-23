using System;
using System.Collections.Generic;
using System.Linq;
using NLog;
using NzbDrone.Core.Books;
using NzbDrone.Core.Lifecycle;
using NzbDrone.Core.Messaging.Events;
using NzbDrone.Core.Talearr.GrabPolicy;
using NzbDrone.Core.Talearr.Naming;

namespace NzbDrone.Core.Talearr.Titles
{
    public sealed class TalearrTitleOverrideDetails
    {
        public int BookId { get; set; }

        // The title the user set, null when none.
        public string Title { get; set; }

        // The title the metadata gives the book, as stored.
        public string MetadataTitle { get; set; }

        // What Talearr shows without the user's title: the metadata title, cleaned when
        // Clean Series From Titles is on.
        public string AutomaticTitle { get; set; }

        public bool CleanSeriesFromTitles { get; set; }
    }

    public enum TalearrTitleOverrideStatus
    {
        Ok,
        NotFound,
        Invalid
    }

    public sealed class TalearrTitleOverrideResult
    {
        public TalearrTitleOverrideStatus Status { get; set; }
        public string Message { get; set; }
        public TalearrTitleOverrideDetails Details { get; set; }
    }

    public interface ITalearrDisplayTitleService
    {
        // The title to show and name files with for this row, given the title Talearr would use
        // without this feature. The user's title wins, then Clean Series From Titles. Returns
        // baseTitle when neither applies. Never throws.
        string Resolve(Book book, string baseTitle);

        // TalearrBookTitle.ForNaming with the user's title or the cleanup applied.
        string ForNaming(Book book, Edition edition);
        string ForNaming(Edition edition);

        // The display title when it differs from the metadata title (an Edit book title, or the
        // cleaned title), else null. Searches and release matching use it as a second title.
        string AlternateTitle(Book book);

        TalearrTitleOverrideDetails Get(int bookId);

        // Null or blank clears the user's title.
        TalearrTitleOverrideResult Set(int bookId, string title);
    }

    // Talearr: the title Talearr shows (book routes) and names files and folders with
    // ({Book Title} and its variants, metadata.json, M4B files). The Books row keeps the metadata
    // title, so imports and metadata refresh are unchanged; searches try the metadata title
    // first, then the display title, and releases named either way match (TalearrSearchTitles).
    //
    // A title set in Edit book (TitleOverrides in talearr.db, per work) wins. Otherwise, when
    // Settings > Metadata > Clean Series From Titles is on, TalearrSeriesTitleCleaner removes
    // the series from the title, using the author's other books in the same series to spot a
    // shared prefix ("Viridian Gate Online: ").
    public class TalearrDisplayTitleService : ITalearrDisplayTitleService, IHandle<ApplicationStartedEvent>
    {
        public const int MaxTitleLength = 500;

        private static readonly TimeSpan OverrideCacheLifetime = TimeSpan.FromMinutes(5);
        private static readonly TimeSpan SiblingCacheLifetime = TimeSpan.FromMinutes(1);

        private readonly ITalearrTitleSettingsService _settings;
        private readonly ITalearrTitleOverrideStore _store;
        private readonly ITalearrTitleLibrary _library;
        private readonly Logger _logger;
        private readonly object _lock = new object();

        private Dictionary<string, TalearrTitleOverride> _overrides;
        private DateTime _overridesLoadedAt;
        private readonly Dictionary<int, (DateTime LoadedAt, List<TitleRow> Books)> _authorBooks = new Dictionary<int, (DateTime, List<TitleRow>)>();

        public TalearrDisplayTitleService(ITalearrTitleSettingsService settings,
                                          ITalearrTitleOverrideStore store,
                                          ITalearrTitleLibrary library,
                                          Logger logger)
        {
            _settings = settings;
            _store = store;
            _library = library;
            _logger = logger;
        }

        public string Resolve(Book book, string baseTitle)
        {
            if (book == null)
            {
                return baseTitle;
            }

            try
            {
                var userTitle = GetOverride(book);
                if (userTitle != null)
                {
                    return userTitle;
                }

                return Clean(book, baseTitle);
            }
            catch (Exception ex)
            {
                _logger.Debug(ex, "Talearr: could not work out the display title of book {0}", book.Id);
                return baseTitle;
            }
        }

        public string AlternateTitle(Book book)
        {
            if (book == null || string.IsNullOrWhiteSpace(book.Title))
            {
                return null;
            }

            var baseTitle = TalearrBookTitle.Strip(book.Title);
            var resolved = Resolve(book, baseTitle);

            return string.IsNullOrWhiteSpace(resolved) || string.Equals(resolved.Trim(), baseTitle?.Trim(), StringComparison.OrdinalIgnoreCase)
                ? null
                : resolved.Trim();
        }

        public void Handle(ApplicationStartedEvent message)
        {
            TalearrSearchTitles.Register(AlternateTitle);
        }

        public string ForNaming(Book book, Edition edition)
        {
            var baseTitle = TalearrBookTitle.ForNaming(book, edition);
            return Resolve(book ?? LoadBook(edition), baseTitle);
        }

        public string ForNaming(Edition edition)
        {
            if (edition == null)
            {
                return null;
            }

            return ForNaming(LoadBook(edition), edition);
        }

        public TalearrTitleOverrideDetails Get(int bookId)
        {
            var book = _library.GetBook(bookId);
            return book == null ? null : Describe(book);
        }

        public TalearrTitleOverrideResult Set(int bookId, string title)
        {
            var book = _library.GetBook(bookId);
            if (book == null)
            {
                return new TalearrTitleOverrideResult { Status = TalearrTitleOverrideStatus.NotFound, Message = $"Book {bookId} not found" };
            }

            var workKey = TalearrBookGrabPolicyService.WorkKey(book);
            var trimmed = title?.Trim();

            if (string.IsNullOrEmpty(trimmed))
            {
                _store.Remove(workKey);
                _logger.Info("Talearr: title of '{0}' (book {1}) is the metadata title again", book.Title, book.Id);
            }
            else
            {
                if (trimmed.Length > MaxTitleLength)
                {
                    return new TalearrTitleOverrideResult { Status = TalearrTitleOverrideStatus.Invalid, Message = $"title is longer than {MaxTitleLength} characters" };
                }

                _store.Upsert(new TalearrTitleOverride
                {
                    WorkKey = workKey,
                    Title = trimmed,
                    BookIdHint = book.Id,
                    UpdatedAt = DateTime.UtcNow
                });

                _logger.Info("Talearr: title of '{0}' (book {1}) set to '{2}'", book.Title, book.Id, trimmed);
            }

            lock (_lock)
            {
                _overrides = null;
            }

            return new TalearrTitleOverrideResult { Status = TalearrTitleOverrideStatus.Ok, Details = Describe(book) };
        }

        private TalearrTitleOverrideDetails Describe(Book book)
        {
            var baseTitle = TalearrBookTitle.Strip(book.Title);

            return new TalearrTitleOverrideDetails
            {
                BookId = book.Id,
                Title = GetOverride(book),
                MetadataTitle = book.Title,
                AutomaticTitle = Clean(book, baseTitle),
                CleanSeriesFromTitles = CleanEnabled()
            };
        }

        private string GetOverride(Book book)
        {
            var workKey = TalearrBookGrabPolicyService.WorkKey(book);
            if (workKey == null)
            {
                return null;
            }

            Dictionary<string, TalearrTitleOverride> overrides;
            lock (_lock)
            {
                if (_overrides == null || DateTime.UtcNow - _overridesLoadedAt > OverrideCacheLifetime)
                {
                    _overrides = _store.GetAll() ?? new Dictionary<string, TalearrTitleOverride>(StringComparer.OrdinalIgnoreCase);
                    _overridesLoadedAt = DateTime.UtcNow;
                }

                overrides = _overrides;
            }

            return overrides.TryGetValue(workKey, out var found) && !string.IsNullOrWhiteSpace(found.Title) ? found.Title.Trim() : null;
        }

        private bool CleanEnabled()
        {
            try
            {
                return _settings?.Get()?.CleanSeriesFromTitles == true;
            }
            catch (Exception)
            {
                return false;
            }
        }

        private string Clean(Book book, string baseTitle)
        {
            if (string.IsNullOrWhiteSpace(baseTitle) || !CleanEnabled())
            {
                return baseTitle;
            }

            var series = SeriesOf(book);
            if (series.Count == 0)
            {
                return baseTitle;
            }

            // The other books of the series only matter for a "<prefix>: " title.
            var siblings = TalearrSeriesTitleCleaner.Prefix(baseTitle) != null
                ? SiblingTitles(book, series)
                : null;

            return TalearrSeriesTitleCleaner.Clean(baseTitle, series, siblings);
        }

        // The book's series without loading anything: the main series, then any series links
        // already loaded with their series.
        private static List<TalearrTitleSeries> SeriesOf(Book book)
        {
            var series = new List<TalearrTitleSeries>();

            if (!string.IsNullOrWhiteSpace(book.SeriesName))
            {
                series.Add(new TalearrTitleSeries(book.SeriesName.Trim(), book.SeriesPosition));
            }

            if (book.LazySeriesLinks?.IsLoaded == true && book.LazySeriesLinks.Value != null)
            {
                foreach (var link in book.LazySeriesLinks.Value)
                {
                    var name = link?.Series?.IsLoaded == true ? link.Series.Value?.Title : null;
                    if (!string.IsNullOrWhiteSpace(name) &&
                        !series.Any(s => string.Equals(s.Name, name.Trim(), StringComparison.OrdinalIgnoreCase)))
                    {
                        series.Add(new TalearrTitleSeries(name.Trim(), link.Position));
                    }
                }
            }

            return series;
        }

        // Titles of the author's other books in the book's main series (both media types).
        private List<string> SiblingTitles(Book book, List<TalearrTitleSeries> series)
        {
            var authorId = book.AuthorId;
            if (authorId <= 0)
            {
                return null;
            }

            var names = new HashSet<string>(series.Select(s => s.Name), StringComparer.OrdinalIgnoreCase);

            return AuthorBooks(authorId)
                .Where(b => b.Id != book.Id)
                .Where(b => (b.SeriesName != null && names.Contains(b.SeriesName)) ||
                            (book.SeriesId.HasValue && b.SeriesId == book.SeriesId))
                .Select(b => b.Title)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        private List<TitleRow> AuthorBooks(int authorId)
        {
            lock (_lock)
            {
                if (_authorBooks.TryGetValue(authorId, out var cached) && DateTime.UtcNow - cached.LoadedAt < SiblingCacheLifetime)
                {
                    return cached.Books;
                }
            }

            List<TitleRow> books;
            try
            {
                books = (_library.GetAuthorBooks(authorId) ?? new List<Book>())
                    .Where(b => b != null && !string.IsNullOrWhiteSpace(b.Title))
                    .Select(b => new TitleRow
                    {
                        Id = b.Id,
                        Title = TalearrBookTitle.Strip(b.Title),
                        SeriesName = string.IsNullOrWhiteSpace(b.SeriesName) ? null : b.SeriesName.Trim(),
                        SeriesId = b.SeriesId
                    })
                    .ToList();
            }
            catch (Exception ex)
            {
                _logger.Debug(ex, "Talearr: could not read the books of author {0} for title cleanup", authorId);
                books = new List<TitleRow>();
            }

            lock (_lock)
            {
                // A small cache: one author page or rename run reads the author once.
                if (_authorBooks.Count > 200)
                {
                    _authorBooks.Clear();
                }

                _authorBooks[authorId] = (DateTime.UtcNow, books);
            }

            return books;
        }

        private static Book LoadBook(Edition edition)
        {
            try
            {
                return edition?.Book;
            }
            catch (Exception)
            {
                // Lazy loaded; a detached edition has no book.
                return null;
            }
        }

        private sealed class TitleRow
        {
            public int Id { get; set; }
            public string Title { get; set; }
            public string SeriesName { get; set; }
            public int? SeriesId { get; set; }
        }
    }
}
