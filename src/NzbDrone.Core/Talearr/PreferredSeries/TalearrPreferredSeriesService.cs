using System;
using System.Collections.Generic;
using System.Linq;
using NLog;
using NzbDrone.Core.Books;
using NzbDrone.Core.Talearr.BookEdits;

namespace NzbDrone.Core.Talearr.PreferredSeries
{
    public interface ITalearrPreferredSeriesService
    {
        // API: bookId may be any row of the work.
        TalearrPreferredSeriesDetails Get(int bookId);

        // Null or empty seriesTitle goes back to the metadata's main series.
        TalearrPreferredSeriesResult Set(int bookId, string seriesTitle);

        // Refresh hooks. No-ops when nothing is stored; they never throw.
        // Puts the preferred series into a book refresh's remote copy so the refresh neither
        // overwrites it nor sees a change, and keeps the provider's main series for a reset.
        void ApplyToRemoteBook(Book local, Book remote);

        // Writes the preference back onto these rows when anything else changed them.
        void Reapply(IEnumerable<Book> rows);
        void ReapplyAuthor(int authorId);
    }

    // Talearr: the preferred series of a book in more than one series (Edit book > Preferred
    // series). Chaptarr keeps a book's main series in Books.SeriesId, SeriesName and
    // SeriesPosition; the naming tokens ({Book Series}, {Book SeriesPosition}, {Book SeriesTitle},
    // {Series Folder}) and the book's series title (index sorting, book page) read those columns.
    // The choice is kept in talearr.db and written onto those columns from one of the book's
    // existing series links, so stock Chaptarr reads a valid value. Without a choice nothing
    // changes. The series list itself still follows the metadata (or Edit book details).
    public class TalearrPreferredSeriesService : ITalearrPreferredSeriesService
    {
        private static readonly TimeSpan CacheLifetime = TimeSpan.FromMinutes(5);

        private readonly ITalearrPreferredSeriesStore _store;
        private readonly ITalearrBookEditLibrary _library;
        private readonly Logger _logger;
        private readonly object _lock = new object();

        private Dictionary<string, TalearrPreferredSeriesEntry> _cache;
        private DateTime _cachedAt;

        public TalearrPreferredSeriesService(ITalearrPreferredSeriesStore store, ITalearrBookEditLibrary library, Logger logger)
        {
            _store = store;
            _library = library;
            _logger = logger;
        }

        public TalearrPreferredSeriesDetails Get(int bookId)
        {
            var row = _library.GetBook(bookId);
            return row == null ? null : Describe(row);
        }

        public TalearrPreferredSeriesResult Set(int bookId, string seriesTitle)
        {
            var row = _library.GetBook(bookId);
            if (row == null)
            {
                return new TalearrPreferredSeriesResult { Status = TalearrPreferredSeriesStatus.NotFound, Message = $"Book {bookId} not found" };
            }

            var key = TalearrBookEditService.WorkKey(row);
            var rows = WorkRows(row);
            var title = string.IsNullOrWhiteSpace(seriesTitle) ? null : seriesTitle.Trim();

            lock (_lock)
            {
                var entries = Snapshot();
                entries.TryGetValue(key ?? string.Empty, out var entry);

                if (title == null)
                {
                    if (entry != null)
                    {
                        Restore(entry, rows);
                        _store.Remove(key);
                        Invalidate();
                        _logger.Info("Talearr: '{0}' uses the metadata's main series again", row.Title);
                    }

                    return Ok(row);
                }

                var choice = Options(row).FirstOrDefault(o => SameTitle(o.Title, title));
                if (choice == null)
                {
                    return new TalearrPreferredSeriesResult
                    {
                        Status = TalearrPreferredSeriesStatus.Invalid,
                        Message = $"'{title}' is not one of the series this book is in",
                        Details = Describe(row)
                    };
                }

                entry ??= new TalearrPreferredSeriesEntry { WorkKey = key };
                entry.SeriesTitle = choice.Title;
                entry.BookIdHint = row.Id;
                entry.UpdatedAt = DateTime.UtcNow;

                ApplyRows(entry, rows);
                _store.Upsert(entry);
                Invalidate();
                _logger.Info("Talearr: '{0}' now uses '{1}' as its main series", row.Title, choice.Title);
            }

            return Ok(row);
        }

        public void ApplyToRemoteBook(Book local, Book remote)
        {
            if (local == null || remote == null || local.Id <= 0)
            {
                return;
            }

            try
            {
                var entries = Snapshot();
                if (entries.Count == 0 || !entries.TryGetValue(TalearrBookEditService.WorkKey(local) ?? string.Empty, out var entry))
                {
                    return;
                }

                var target = Target(local, entry.SeriesTitle);
                if (target == null)
                {
                    return;
                }

                lock (_lock)
                {
                    // Keep the provider's own main series up to date for a later reset. A refresh
                    // keeps the local series when the provider sends none (Book.UseMetadataFrom).
                    if (remote.SeriesName != null)
                    {
                        entry.Provider.TryGetValue(local.Id, out var provider);
                        if (provider == null || provider.SeriesId != remote.SeriesId || provider.SeriesName != remote.SeriesName || provider.SeriesPosition != remote.SeriesPosition)
                        {
                            entry.Provider[local.Id] = new TalearrPreferredSeriesProvider
                            {
                                SeriesId = remote.SeriesId,
                                SeriesName = remote.SeriesName,
                                SeriesPosition = remote.SeriesPosition
                            };
                            _store.Upsert(entry);
                        }
                    }
                }

                remote.SeriesId = target.Value.Series.Id;
                remote.SeriesName = target.Value.Series.Title;
                remote.SeriesPosition = EmptyToNull(target.Value.Link.Position);
            }
            catch (Exception ex)
            {
                _logger.Warn(ex, "Talearr: could not keep the preferred series of '{0}' during refresh", local.Title);
            }
        }

        public void Reapply(IEnumerable<Book> rows)
        {
            try
            {
                var entries = Snapshot();
                if (entries.Count == 0)
                {
                    return;
                }

                var groups = (rows ?? Enumerable.Empty<Book>())
                    .Where(b => b != null && b.Id > 0)
                    .GroupBy(b => b.Id)
                    .Select(g => g.First())
                    .GroupBy(b => TalearrBookEditService.WorkKey(b) ?? string.Empty, StringComparer.OrdinalIgnoreCase)
                    .Where(g => entries.ContainsKey(g.Key))
                    .ToList();

                foreach (var group in groups)
                {
                    // Reload: the rows a refresh hands over can be older than what it saved.
                    var fresh = group.Select(b => _library.GetBook(b.Id)).Where(b => b != null).ToList();

                    lock (_lock)
                    {
                        var entry = entries[group.Key];
                        if (ApplyRows(entry, fresh))
                        {
                            _store.Upsert(entry);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.Warn(ex, "Talearr: could not apply preferred series after a refresh");
            }
        }

        public void ReapplyAuthor(int authorId)
        {
            try
            {
                if (authorId <= 0 || Snapshot().Count == 0)
                {
                    return;
                }

                Reapply(_library.GetBooksByAuthor(authorId));
            }
            catch (Exception ex)
            {
                _logger.Warn(ex, "Talearr: could not apply preferred series for author {0}", authorId);
            }
        }

        // Writes the preferred series onto each row that has it. Returns true when the entry's
        // provider values changed (and need saving).
        private bool ApplyRows(TalearrPreferredSeriesEntry entry, List<Book> rows)
        {
            var changed = new List<Book>();
            var entryDirty = false;

            foreach (var row in rows.Where(r => r != null && r.Id > 0))
            {
                var target = Target(row, entry.SeriesTitle);
                if (target == null)
                {
                    // Not in that series (any more): the row keeps whatever the metadata says.
                    continue;
                }

                var seriesId = target.Value.Series.Id;
                var name = target.Value.Series.Title;
                var position = EmptyToNull(target.Value.Link.Position);

                if (row.SeriesId == seriesId && row.SeriesName == name && row.SeriesPosition == position)
                {
                    continue;
                }

                if (!entry.Provider.ContainsKey(row.Id))
                {
                    entry.Provider[row.Id] = new TalearrPreferredSeriesProvider
                    {
                        SeriesId = row.SeriesId,
                        SeriesName = row.SeriesName,
                        SeriesPosition = row.SeriesPosition
                    };
                    entryDirty = true;
                }

                row.SeriesId = seriesId;
                row.SeriesName = name;
                row.SeriesPosition = position;
                changed.Add(row);
            }

            _library.SaveBooks(changed);
            return entryDirty;
        }

        private void Restore(TalearrPreferredSeriesEntry entry, List<Book> rows)
        {
            var changed = new List<Book>();
            foreach (var row in rows.Where(r => r != null && r.Id > 0))
            {
                if (!entry.Provider.TryGetValue(row.Id, out var provider) || provider == null)
                {
                    continue;
                }

                if (row.SeriesId == provider.SeriesId && row.SeriesName == provider.SeriesName && row.SeriesPosition == provider.SeriesPosition)
                {
                    continue;
                }

                row.SeriesId = provider.SeriesId;
                row.SeriesName = provider.SeriesName;
                row.SeriesPosition = provider.SeriesPosition;
                changed.Add(row);
            }

            _library.SaveBooks(changed);
        }

        private (Series Series, SeriesBookLink Link)? Target(Book row, string title)
        {
            foreach (var link in _library.GetLinks(row.Id).Where(IsOriginalLink).OrderBy(l => l.Id))
            {
                var series = _library.GetSeries(link.SeriesId);
                if (series != null && SameTitle(series.Title, title))
                {
                    return (series, link);
                }
            }

            return null;
        }

        private List<TalearrPreferredSeriesOption> Options(Book row)
        {
            return _library.GetLinks(row.Id)
                .Where(IsOriginalLink)
                .OrderBy(l => l.Id)
                .Select(l => (Link: l, Series: _library.GetSeries(l.SeriesId)))
                .Where(x => x.Series != null && !string.IsNullOrWhiteSpace(x.Series.Title))
                .GroupBy(x => x.Series.Title.Trim(), StringComparer.OrdinalIgnoreCase)
                .Select(g => g.First())
                .Select(x => new TalearrPreferredSeriesOption { SeriesId = x.Series.Id, Title = x.Series.Title, Position = EmptyToNull(x.Link.Position) })
                .ToList();
        }

        private TalearrPreferredSeriesDetails Describe(Book row)
        {
            Snapshot().TryGetValue(TalearrBookEditService.WorkKey(row) ?? string.Empty, out var entry);
            return new TalearrPreferredSeriesDetails
            {
                BookId = row.Id,
                PreferredSeries = entry?.SeriesTitle,
                CurrentSeries = row.SeriesName,
                Options = Options(row)
            };
        }

        private TalearrPreferredSeriesResult Ok(Book row)
        {
            return new TalearrPreferredSeriesResult
            {
                Status = TalearrPreferredSeriesStatus.Ok,
                Details = Describe(_library.GetBook(row.Id) ?? row)
            };
        }

        private List<Book> WorkRows(Book seed)
        {
            var key = TalearrBookEditService.WorkKey(seed);
            if (key == null || key.StartsWith("book:", StringComparison.OrdinalIgnoreCase))
            {
                return new List<Book> { seed };
            }

            var rows = _library.GetBooksByAuthor(seed.AuthorId)
                .Where(b => b != null && b.Id > 0 && string.Equals(TalearrBookEditService.WorkKey(b), key, StringComparison.OrdinalIgnoreCase))
                .ToList();

            if (rows.All(b => b.Id != seed.Id))
            {
                rows.Insert(0, seed);
            }

            return rows;
        }

        private Dictionary<string, TalearrPreferredSeriesEntry> Snapshot()
        {
            lock (_lock)
            {
                if (_cache == null || DateTime.UtcNow - _cachedAt > CacheLifetime)
                {
                    _cache = _store.GetAll()
                        .GroupBy(e => e.WorkKey, StringComparer.OrdinalIgnoreCase)
                        .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
                    _cachedAt = DateTime.UtcNow;
                }

                return _cache;
            }
        }

        private void Invalidate()
        {
            lock (_lock)
            {
                _cache = null;
            }
        }

        private static bool SameTitle(string left, string right)
        {
            return !string.IsNullOrWhiteSpace(left) && !string.IsNullOrWhiteSpace(right) &&
                   string.Equals(left.Trim(), right.Trim(), StringComparison.OrdinalIgnoreCase);
        }

        private static bool IsOriginalLink(SeriesBookLink link)
        {
            return link != null &&
                   (string.IsNullOrWhiteSpace(link.SeriesInstanceType) ||
                    string.Equals(link.SeriesInstanceType, "original", StringComparison.OrdinalIgnoreCase));
        }

        private static string EmptyToNull(string value)
        {
            return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
        }
    }
}
