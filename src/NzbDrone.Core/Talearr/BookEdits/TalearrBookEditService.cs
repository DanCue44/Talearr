using System;
using System.Collections.Generic;
using System.Linq;
using NLog;
using NzbDrone.Core.Books;
using NzbDrone.Core.Talearr.GrabPolicy;
using NzbDrone.Core.Talearr.Recordings;

namespace NzbDrone.Core.Talearr.BookEdits
{
    public interface ITalearrBookEditService
    {
        // API: bookId may be any row of the work.
        TalearrBookEditResult Get(int bookId);
        TalearrBookEditResult Save(int bookId, TalearrBookEditChange change);

        // Puts the provider's values back. Null or empty fields clears everything.
        TalearrBookEditResult Clear(int bookId, IEnumerable<string> fields);

        // Refresh hooks. Each one is a no-op when nothing is edited and never throws.
        // Swaps the edited title and series into a book refresh's remote copy, so the refresh
        // neither overwrites them nor sees a change, and keeps the provider's values for Clear.
        void ApplyToRemoteBook(Book local, Book remote);

        // The same for the editions a book refresh is about to compare and write.
        List<Edition> ApplyToRemoteEditions(Book local, List<Edition> remoteEditions);

        // Keeps a series refresh from adding, moving or removing links the user edited.
        void FilterSeriesLinks(Series series, List<SeriesBookLink> added, List<SeriesBookLink> updated, List<SeriesBookLink> deleted, List<SeriesBookLink> upToDate);

        // A Series row Talearr created for an edit. Metadata refresh never deletes these.
        bool IsUserSeries(int seriesId);

        // Writes the edits back onto these rows when anything else changed them.
        void Reapply(IEnumerable<Book> rows);
        void ReapplyAuthor(int authorId);
    }

    // Talearr: "Edit book details". The title and series memberships of a work and the narrators
    // of an edition, set by the user and kept through metadata refreshes. Edits live in talearr.db
    // (BookDetailEdits, NarratorEdits, UserSeries) and are written onto the existing Books,
    // Editions, Series and SeriesBookLink rows, so every existing route returns them without a
    // schema or response change. RefreshBookService and RefreshSeriesService call the hooks below
    // so a refresh keeps the edits instead of overwriting them and putting them back afterwards.
    //
    // Title and series edits are per work (TalearrBookGrabPolicyService.WorkKey), so they cover
    // the audiobook, the ebook and every recording copy. Narrator edits are per edition
    // (TalearrEditionKey): different recordings of a book have different narrators.
    public class TalearrBookEditService : ITalearrBookEditService
    {
        private static readonly TimeSpan CacheLifetime = TimeSpan.FromMinutes(5);
        private const int MaxTitleLength = 500;

        private readonly ITalearrBookEditStore _store;
        private readonly ITalearrBookEditLibrary _library;
        private readonly Logger _logger;
        private readonly object _lock = new object();

        private EditSnapshot _cache;
        private DateTime _cachedAt;

        public TalearrBookEditService(ITalearrBookEditStore store, ITalearrBookEditLibrary library, Logger logger)
        {
            _store = store;
            _library = library;
            _logger = logger;
        }

        public static string WorkKey(Book row)
        {
            return TalearrBookGrabPolicyService.WorkKey(row);
        }

        public static string EditionKey(Edition edition)
        {
            if (edition == null)
            {
                return null;
            }

            return TalearrEditionKey.For(edition) ?? (edition.Id > 0 ? $"edition:{edition.Id}" : null);
        }

        // The edition narrator edits apply to: the one the book follows, as the book routes pick it.
        public static Edition NarratorTarget(IEnumerable<Edition> editions)
        {
            var list = (editions ?? Enumerable.Empty<Edition>()).Where(e => e != null).ToList();
            return list.Where(e => e.Monitored).OrderBy(e => e.Id).FirstOrDefault() ??
                   list.OrderBy(e => e.Id).FirstOrDefault();
        }

        public TalearrBookEditResult Get(int bookId)
        {
            var row = _library.GetBook(bookId);
            if (row == null)
            {
                return NotFound(bookId);
            }

            return new TalearrBookEditResult { Status = TalearrBookEditStatus.Ok, Details = Describe(row) };
        }

        public TalearrBookEditResult Save(int bookId, TalearrBookEditChange change)
        {
            change ??= new TalearrBookEditChange();

            var row = _library.GetBook(bookId);
            if (row == null)
            {
                return NotFound(bookId);
            }

            // Check everything before writing anything.
            var resets = NormalizeFields(change.Reset, out var badField);
            if (badField != null)
            {
                return Invalid(bookId, $"reset can only name {string.Join(", ", TalearrBookEditFields.All)}");
            }

            string title = null;
            if (change.Title != null)
            {
                title = change.Title.Trim();
                if (title.Length == 0)
                {
                    return Invalid(bookId, "title cannot be empty; reset it to go back to the provider's title");
                }

                if (title.Length > MaxTitleLength)
                {
                    return Invalid(bookId, $"title is longer than {MaxTitleLength} characters");
                }
            }

            List<TalearrSeriesEntry> series = null;
            if (change.Series != null)
            {
                series = NormalizeSeries(change.Series, out var seriesProblem);
                if (seriesProblem != null)
                {
                    return Invalid(bookId, seriesProblem);
                }
            }

            List<string> narrators = null;
            Edition narratorEdition = null;
            if (change.Narrators != null)
            {
                if (row.MediaType != BookMediaType.Audiobook)
                {
                    return Invalid(bookId, "narrators can only be set on an audiobook");
                }

                narratorEdition = NarratorTarget(_library.GetEditions(row.Id));
                if (EditionKey(narratorEdition) == null)
                {
                    return Invalid(bookId, "this audiobook has no edition to set narrators on");
                }

                narrators = NormalizeNarrators(change.Narrators);
            }

            var key = WorkKey(row);
            lock (_lock)
            {
                var rows = WorkRows(row);

                if (resets.Count > 0)
                {
                    ClearCore(row, rows, resets);
                }

                if (title != null || series != null)
                {
                    var edit = _store.Get(key) ?? new TalearrBookEdit { WorkKey = key };
                    if (title != null)
                    {
                        edit.Title = title;
                    }

                    if (series != null)
                    {
                        edit.Series = series;
                    }

                    edit.BookIdHint = row.Id;
                    edit.UpdatedAt = DateTime.UtcNow;
                    if (!_store.Upsert(edit))
                    {
                        _logger.Warn("Talearr: the details edit for '{0}' could not be saved to {1}", row.Title, TalearrDatabase.FileName);
                    }
                }

                if (narrators != null)
                {
                    var editionKey = EditionKey(narratorEdition);
                    var existing = _store.GetNarratorEdits().FirstOrDefault(n => string.Equals(n.EditionKey, editionKey, StringComparison.OrdinalIgnoreCase));
                    var edit = existing ?? new TalearrNarratorEdit { EditionKey = editionKey };
                    edit.WorkKey = key;
                    edit.Narrators = narrators;
                    edit.BookIdHint = row.Id;
                    edit.UpdatedAt = DateTime.UtcNow;
                    if (!_store.UpsertNarratorEdit(edit))
                    {
                        _logger.Warn("Talearr: the narrator edit for '{0}' could not be saved to {1}", row.Title, TalearrDatabase.FileName);
                    }
                }

                Invalidate();
                ApplyWork(key, rows);
                RemoveUnusedUserSeries();
            }

            _logger.Info("Talearr: details of '{0}' ({1}) edited:{2}{3}{4}{5}",
                row.Title,
                key,
                title != null ? " title" : string.Empty,
                series != null ? " series" : string.Empty,
                narrators != null ? " narrators" : string.Empty,
                resets.Count > 0 ? " reset " + string.Join("/", resets) : string.Empty);

            return Get(bookId);
        }

        public TalearrBookEditResult Clear(int bookId, IEnumerable<string> fields)
        {
            var row = _library.GetBook(bookId);
            if (row == null)
            {
                return NotFound(bookId);
            }

            var list = NormalizeFields(fields, out var badField);
            if (badField != null)
            {
                return Invalid(bookId, $"field can only be {string.Join(", ", TalearrBookEditFields.All)}");
            }

            if (list.Count == 0)
            {
                list = TalearrBookEditFields.All.ToList();
            }

            lock (_lock)
            {
                ClearCore(row, WorkRows(row), list);
                RemoveUnusedUserSeries();
            }

            _logger.Info("Talearr: details of '{0}' ({1}) reset to the provider's: {2}", row.Title, WorkKey(row), string.Join(", ", list));

            return Get(bookId);
        }

        public void ApplyToRemoteBook(Book local, Book remote)
        {
            if (local == null || remote == null || local.Id <= 0)
            {
                return;
            }

            try
            {
                var snapshot = Snapshot();
                if (!snapshot.Edits.TryGetValue(WorkKey(local) ?? string.Empty, out var edit) || edit.IsEmpty)
                {
                    return;
                }

                lock (_lock)
                {
                    edit.Provider.TryGetValue(local.Id, out var provider);
                    var dirty = false;

                    if (edit.HasTitle)
                    {
                        // A row the edit has not reached yet (a new recording copy) keeps the
                        // provider's title from this refresh before the edit replaces it.
                        if (provider == null)
                        {
                            provider = new TalearrProviderBookValues();
                            edit.Provider[local.Id] = provider;
                        }

                        if (!string.IsNullOrWhiteSpace(remote.Title) && (!provider.TitleCaptured || provider.Title != remote.Title))
                        {
                            provider.TitleCaptured = true;
                            provider.Title = remote.Title;
                            dirty = true;
                        }

                        remote.Title = edit.Title;
                    }

                    if (edit.HasSeries && provider?.SeriesCaptured == true)
                    {
                        // A refresh keeps the local series when the provider sends none (Book.UseMetadataFrom).
                        if (remote.SeriesName != null &&
                            (provider.SeriesName != remote.SeriesName || provider.SeriesPosition != remote.SeriesPosition))
                        {
                            provider.SeriesName = remote.SeriesName;
                            provider.SeriesPosition = remote.SeriesPosition;
                            dirty = true;
                        }

                        remote.SeriesId = local.SeriesId;
                        remote.SeriesName = local.SeriesName;
                        remote.SeriesPosition = local.SeriesPosition;
                    }

                    if (dirty)
                    {
                        _store.Upsert(edit);
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.Warn(ex, "Talearr: could not keep the edited details of '{0}' during refresh", local.Title);
            }
        }

        public List<Edition> ApplyToRemoteEditions(Book local, List<Edition> remoteEditions)
        {
            if (local == null || remoteEditions == null || remoteEditions.Count == 0)
            {
                return remoteEditions;
            }

            try
            {
                var snapshot = Snapshot();
                var key = WorkKey(local) ?? string.Empty;
                snapshot.Edits.TryGetValue(key, out var edit);
                if (edit?.HasTitle != true && snapshot.NarratorsByKey.Count == 0)
                {
                    return remoteEditions;
                }

                lock (_lock)
                {
                    TalearrProviderBookValues provider = null;
                    if (edit != null && local.Id > 0)
                    {
                        edit.Provider.TryGetValue(local.Id, out provider);
                    }

                    var editDirty = false;
                    var dirtyNarrators = new List<TalearrNarratorEdit>();
                    var result = new List<Edition>(remoteEditions.Count);

                    foreach (var remote in remoteEditions)
                    {
                        if (remote == null)
                        {
                            result.Add(remote);
                            continue;
                        }

                        Edition copy = null;
                        var editionKey = EditionKey(remote);

                        if (edit?.HasTitle == true)
                        {
                            if (provider != null && editionKey != null && !string.IsNullOrWhiteSpace(remote.Title) &&
                                (!provider.EditionTitles.TryGetValue(editionKey, out var known) || known != remote.Title))
                            {
                                provider.EditionTitles[editionKey] = remote.Title;
                                editDirty = true;
                            }

                            if (remote.Title != edit.Title)
                            {
                                // Remote editions are shared between rows; never change them in place.
                                copy ??= RefreshEntityCopy.CloneEdition(remote);
                                copy.Title = edit.Title;
                            }
                        }

                        if (editionKey != null && snapshot.NarratorsByKey.TryGetValue(editionKey, out var narratorEdit))
                        {
                            if (!narratorEdit.ProviderCaptured ||
                                narratorEdit.ProviderNarrator != remote.Narrator || !SameNames(narratorEdit.ProviderNarratorNames, remote.NarratorNames))
                            {
                                narratorEdit.ProviderCaptured = true;
                                narratorEdit.ProviderNarrator = remote.Narrator;
                                narratorEdit.ProviderNarratorNames = (remote.NarratorNames ?? new List<string>()).ToList();
                                dirtyNarrators.Add(narratorEdit);
                            }

                            copy ??= RefreshEntityCopy.CloneEdition(remote);
                            copy.Narrator = JoinNarrators(narratorEdit.Narrators);
                            copy.NarratorNames = narratorEdit.Narrators.ToList();
                        }

                        result.Add(copy ?? remote);
                    }

                    if (editDirty)
                    {
                        _store.Upsert(edit);
                    }

                    foreach (var narratorEdit in dirtyNarrators.Distinct())
                    {
                        _store.UpsertNarratorEdit(narratorEdit);
                    }

                    return result;
                }
            }
            catch (Exception ex)
            {
                _logger.Warn(ex, "Talearr: could not keep the edited edition details of '{0}' during refresh", local.Title);
                return remoteEditions;
            }
        }

        public void FilterSeriesLinks(Series series, List<SeriesBookLink> added, List<SeriesBookLink> updated, List<SeriesBookLink> deleted, List<SeriesBookLink> upToDate)
        {
            if (series == null)
            {
                return;
            }

            try
            {
                var snapshot = Snapshot();
                if (snapshot.SeriesByBookId.Count == 0)
                {
                    return;
                }

                bool Edited(SeriesBookLink link, out TalearrSeriesEntry entry)
                {
                    entry = null;
                    if (link == null || !snapshot.SeriesByBookId.TryGetValue(link.BookId, out var entries))
                    {
                        return false;
                    }

                    entry = entries.FirstOrDefault(e => SameTitle(e.Title, series.Title));
                    return true;
                }

                foreach (var link in (added ?? new List<SeriesBookLink>()).ToList())
                {
                    if (Edited(link, out var entry))
                    {
                        if (entry == null)
                        {
                            added.Remove(link);
                        }
                        else
                        {
                            link.Position = EmptyToNull(entry.Position);
                            link.SeriesPosition = PositionNumber(entry.Position);
                        }
                    }
                }

                foreach (var link in (updated ?? new List<SeriesBookLink>()).ToList())
                {
                    if (Edited(link, out var entry))
                    {
                        updated.Remove(link);
                        (entry == null ? deleted : upToDate)?.Add(link);
                    }
                }

                foreach (var link in (deleted ?? new List<SeriesBookLink>()).ToList())
                {
                    if (Edited(link, out var entry) && entry != null)
                    {
                        deleted.Remove(link);
                        upToDate?.Add(link);
                    }
                }

                foreach (var link in (upToDate ?? new List<SeriesBookLink>()).ToList())
                {
                    if (Edited(link, out var entry) && entry == null)
                    {
                        upToDate.Remove(link);
                        deleted?.Add(link);
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.Warn(ex, "Talearr: could not keep the edited series links of '{0}' during refresh", series.Title);
            }
        }

        public bool IsUserSeries(int seriesId)
        {
            try
            {
                return seriesId > 0 && Snapshot().UserSeriesIds.Contains(seriesId);
            }
            catch (Exception)
            {
                return false;
            }
        }

        public void Reapply(IEnumerable<Book> rows)
        {
            try
            {
                var snapshot = Snapshot();
                if (snapshot.IsEmpty)
                {
                    return;
                }

                var groups = (rows ?? Enumerable.Empty<Book>())
                    .Where(b => b != null && b.Id > 0)
                    .GroupBy(b => b.Id)
                    .Select(g => g.First())
                    .GroupBy(b => WorkKey(b), StringComparer.OrdinalIgnoreCase)
                    .Where(g => snapshot.Edits.ContainsKey(g.Key) || snapshot.NarratorsByWork.ContainsKey(g.Key))
                    .ToList();

                foreach (var group in groups)
                {
                    // Reload: the rows a refresh hands over can be older than what it saved.
                    var fresh = group.Select(b => _library.GetBook(b.Id)).Where(b => b != null).ToList();

                    lock (_lock)
                    {
                        ApplyWork(group.Key, fresh);
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.Warn(ex, "Talearr: could not apply edited book details after a refresh");
            }
        }

        public void ReapplyAuthor(int authorId)
        {
            try
            {
                if (authorId <= 0 || Snapshot().IsEmpty)
                {
                    return;
                }

                Reapply(_library.GetBooksByAuthor(authorId));
            }
            catch (Exception ex)
            {
                _logger.Warn(ex, "Talearr: could not apply edited book details for author {0}", authorId);
            }
        }

        // Writes the work's edits onto its rows. Captures the provider's values on first use.
        private void ApplyWork(string key, List<Book> rows)
        {
            var snapshot = Snapshot();
            snapshot.Edits.TryGetValue(key ?? string.Empty, out var edit);
            snapshot.NarratorsByWork.TryGetValue(key ?? string.Empty, out var narratorEdits);

            if ((edit == null || edit.IsEmpty) && (narratorEdits == null || narratorEdits.Count == 0))
            {
                return;
            }

            var resolver = new SeriesResolver(this);
            var changedBooks = new List<Book>();
            var changedEditions = new List<Edition>();
            var editDirty = false;
            var dirtyNarrators = new List<TalearrNarratorEdit>();
            var narratorsByKey = (narratorEdits ?? new List<TalearrNarratorEdit>())
                .GroupBy(n => n.EditionKey, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);

            foreach (var row in rows.Where(r => r != null && r.Id > 0))
            {
                var editions = _library.GetEditions(row.Id);
                var bookChanged = false;

                if (edit != null && !edit.IsEmpty)
                {
                    if (!edit.Provider.TryGetValue(row.Id, out var provider))
                    {
                        provider = new TalearrProviderBookValues();
                        edit.Provider[row.Id] = provider;
                        editDirty = true;
                    }

                    if (edit.HasTitle)
                    {
                        if (!provider.TitleCaptured)
                        {
                            provider.TitleCaptured = true;
                            provider.Title = row.Title;
                            provider.EditionTitles.Clear();
                            editDirty = true;
                        }

                        foreach (var edition in editions)
                        {
                            var editionKey = EditionKey(edition);
                            if (editionKey != null && !provider.EditionTitles.ContainsKey(editionKey) && edition.Title != edit.Title)
                            {
                                provider.EditionTitles[editionKey] = edition.Title;
                                editDirty = true;
                            }

                            if (edition.Title != edit.Title)
                            {
                                edition.Title = edit.Title;
                                changedEditions.Add(edition);
                            }
                        }

                        if (row.Title != edit.Title)
                        {
                            row.Title = edit.Title;
                            bookChanged = true;
                        }
                    }

                    if (edit.HasSeries)
                    {
                        var links = _library.GetLinks(row.Id).Where(IsOriginalLink).ToList();
                        if (!provider.SeriesCaptured)
                        {
                            provider.SeriesCaptured = true;
                            provider.SeriesId = row.SeriesId;
                            provider.SeriesName = row.SeriesName;
                            provider.SeriesPosition = row.SeriesPosition;
                            provider.Links = links.Select(l => new TalearrProviderSeriesLink { SeriesId = l.SeriesId, Position = l.Position }).ToList();
                            editDirty = true;
                        }

                        var desired = edit.Series
                            .Select(e => (Series: resolver.Resolve(row, e.Title, e.Position), Position: EmptyToNull(e.Position)))
                            .Where(d => d.Series != null)
                            .GroupBy(d => d.Series.Id)
                            .Select(g => g.First())
                            .ToList();

                        SyncLinks(row, links, desired.Select(d => (d.Series.Id, d.Position)).ToList());

                        var first = desired.FirstOrDefault();
                        bookChanged |= SetSeriesFields(row, first.Series?.Id, first.Series?.Title, first.Series == null ? null : first.Position);
                    }
                }

                foreach (var edition in editions)
                {
                    var editionKey = EditionKey(edition);
                    if (editionKey == null || !narratorsByKey.TryGetValue(editionKey, out var narratorEdit))
                    {
                        continue;
                    }

                    if (!narratorEdit.ProviderCaptured)
                    {
                        narratorEdit.ProviderCaptured = true;
                        narratorEdit.ProviderNarrator = edition.Narrator;
                        narratorEdit.ProviderNarratorNames = (edition.NarratorNames ?? new List<string>()).ToList();
                        dirtyNarrators.Add(narratorEdit);
                    }

                    var joined = JoinNarrators(narratorEdit.Narrators);
                    if (edition.Narrator != joined || !SameNames(edition.NarratorNames, narratorEdit.Narrators))
                    {
                        edition.Narrator = joined;
                        edition.NarratorNames = narratorEdit.Narrators.ToList();
                        changedEditions.Add(edition);
                    }
                }

                if (bookChanged)
                {
                    changedBooks.Add(row);
                }
            }

            _library.SaveBooks(changedBooks);
            _library.SaveEditions(changedEditions.Distinct().ToList());

            if (editDirty)
            {
                _store.Upsert(edit);
            }

            foreach (var narratorEdit in dirtyNarrators.Distinct())
            {
                _store.UpsertNarratorEdit(narratorEdit);
            }

            if (changedBooks.Count > 0 || changedEditions.Count > 0)
            {
                _logger.Debug("Talearr: applied edited details of {0} to {1} book row(s) and {2} edition(s)", key, changedBooks.Count, changedEditions.Distinct().Count());
            }

            if (resolver.Created)
            {
                Invalidate();
            }
        }

        private void ClearCore(Book row, List<Book> rows, ICollection<string> fields)
        {
            var key = WorkKey(row);
            var edit = _store.Get(key);
            var changedBooks = new List<Book>();
            var changedEditions = new List<Edition>();
            var editionsByRow = rows.Where(r => r != null && r.Id > 0).ToDictionary(r => r.Id, r => _library.GetEditions(r.Id));

            if (edit != null && fields.Contains(TalearrBookEditFields.Title) && edit.HasTitle)
            {
                foreach (var target in rows.Where(r => r != null && r.Id > 0))
                {
                    if (!edit.Provider.TryGetValue(target.Id, out var provider) || !provider.TitleCaptured)
                    {
                        continue;
                    }

                    if (!string.IsNullOrWhiteSpace(provider.Title) && target.Title != provider.Title)
                    {
                        target.Title = provider.Title;
                        changedBooks.Add(target);
                    }

                    foreach (var edition in editionsByRow[target.Id])
                    {
                        var editionKey = EditionKey(edition);
                        var original = editionKey != null && provider.EditionTitles.TryGetValue(editionKey, out var known) ? known : null;

                        // An edition added after the edit only ever had the edited title; give it the book's.
                        original ??= edition.Title == edit.Title ? provider.Title : null;

                        if (!string.IsNullOrWhiteSpace(original) && edition.Title != original)
                        {
                            edition.Title = original;
                            changedEditions.Add(edition);
                        }
                    }

                    provider.TitleCaptured = false;
                    provider.Title = null;
                    provider.EditionTitles.Clear();
                }

                edit.Title = null;
            }

            if (edit != null && fields.Contains(TalearrBookEditFields.Series) && edit.HasSeries)
            {
                foreach (var target in rows.Where(r => r != null && r.Id > 0))
                {
                    if (!edit.Provider.TryGetValue(target.Id, out var provider) || !provider.SeriesCaptured)
                    {
                        continue;
                    }

                    var links = _library.GetLinks(target.Id).Where(IsOriginalLink).ToList();
                    var restore = (provider.Links ?? new List<TalearrProviderSeriesLink>())
                        .Where(l => _library.GetSeries(l.SeriesId) != null)
                        .Select(l => (l.SeriesId, EmptyToNull(l.Position)))
                        .ToList();

                    SyncLinks(target, links, restore);

                    var seriesId = provider.SeriesId.HasValue && _library.GetSeries(provider.SeriesId.Value) != null ? provider.SeriesId : null;
                    if (SetSeriesFields(target, seriesId, provider.SeriesName, provider.SeriesPosition) && !changedBooks.Contains(target))
                    {
                        changedBooks.Add(target);
                    }

                    provider.SeriesCaptured = false;
                    provider.SeriesId = null;
                    provider.SeriesName = null;
                    provider.SeriesPosition = null;
                    provider.Links = new List<TalearrProviderSeriesLink>();
                }

                edit.Series = null;
            }

            if (fields.Contains(TalearrBookEditFields.Narrators))
            {
                // The narrator edits of this row's editions, put back on every row that shares them.
                var rowKeys = editionsByRow.TryGetValue(row.Id, out var own)
                    ? own.Select(EditionKey).Where(k => k != null).ToHashSet(StringComparer.OrdinalIgnoreCase)
                    : new HashSet<string>(StringComparer.OrdinalIgnoreCase);

                foreach (var narratorEdit in _store.GetNarratorEdits().Where(n => rowKeys.Contains(n.EditionKey)))
                {
                    if (narratorEdit.ProviderCaptured)
                    {
                        foreach (var edition in editionsByRow.Values.SelectMany(e => e).Where(e => string.Equals(EditionKey(e), narratorEdit.EditionKey, StringComparison.OrdinalIgnoreCase)))
                        {
                            edition.Narrator = narratorEdit.ProviderNarrator;
                            edition.NarratorNames = (narratorEdit.ProviderNarratorNames ?? new List<string>()).ToList();
                            changedEditions.Add(edition);
                        }
                    }

                    _store.DeleteNarratorEdit(narratorEdit.EditionKey);
                }
            }

            _library.SaveBooks(changedBooks.Distinct().ToList());
            _library.SaveEditions(changedEditions.Distinct().ToList());

            if (edit != null)
            {
                foreach (var stale in edit.Provider.Where(p => !p.Value.TitleCaptured && !p.Value.SeriesCaptured).Select(p => p.Key).ToList())
                {
                    edit.Provider.Remove(stale);
                }

                if (edit.IsEmpty)
                {
                    _store.Delete(key);
                }
                else
                {
                    edit.UpdatedAt = DateTime.UtcNow;
                    _store.Upsert(edit);
                }
            }

            Invalidate();
        }

        // Makes the row's original-series links exactly the wanted ones (series id, position).
        private void SyncLinks(Book row, List<SeriesBookLink> links, List<(int SeriesId, string Position)> wanted)
        {
            var add = new List<SeriesBookLink>();
            var update = new List<SeriesBookLink>();

            foreach (var (seriesId, position) in wanted)
            {
                var link = links.FirstOrDefault(l => l.SeriesId == seriesId);
                if (link == null)
                {
                    add.Add(new SeriesBookLink
                    {
                        SeriesId = seriesId,
                        BookId = row.Id,
                        Position = position,
                        SeriesPosition = PositionNumber(position),
                        IsPrimary = true,
                        SeriesInstanceType = "original"
                    });
                }
                else if (EmptyToNull(link.Position) != position || link.SeriesPosition != PositionNumber(position))
                {
                    link.Position = position;
                    link.SeriesPosition = PositionNumber(position);
                    update.Add(link);
                }
            }

            var remove = links.Where(l => wanted.All(w => w.SeriesId != l.SeriesId)).ToList();

            _library.DeleteLinks(remove);
            _library.UpdateLinks(update);
            _library.AddLinks(add);
        }

        private static bool SetSeriesFields(Book row, int? seriesId, string seriesName, string seriesPosition)
        {
            if (row.SeriesId == seriesId && row.SeriesName == seriesName && row.SeriesPosition == seriesPosition)
            {
                return false;
            }

            row.SeriesId = seriesId;
            row.SeriesName = seriesName;
            row.SeriesPosition = seriesPosition;
            return true;
        }

        // Talearr-created series that no book links to any more are removed with their record.
        private void RemoveUnusedUserSeries()
        {
            var removed = false;
            foreach (var userSeries in _store.GetUserSeries())
            {
                if (_library.GetLinksBySeries(userSeries.SeriesId).Count > 0)
                {
                    continue;
                }

                if (_library.GetSeries(userSeries.SeriesId) != null)
                {
                    _library.DeleteSeries(userSeries.SeriesId);
                }

                _store.DeleteUserSeries(userSeries.SeriesId);
                removed = true;
            }

            if (removed)
            {
                Invalidate();
            }
        }

        private List<Book> WorkRows(Book seed)
        {
            var key = WorkKey(seed);
            if (key == null || key.StartsWith("book:", StringComparison.OrdinalIgnoreCase))
            {
                return new List<Book> { seed };
            }

            var rows = _library.GetBooksByAuthor(seed.AuthorId)
                .Where(b => b != null && b.Id > 0 && string.Equals(WorkKey(b), key, StringComparison.OrdinalIgnoreCase))
                .ToList();

            if (rows.All(b => b.Id != seed.Id))
            {
                rows.Insert(0, seed);
            }

            return rows;
        }

        private TalearrBookDetails Describe(Book row)
        {
            var key = WorkKey(row);
            var snapshot = Snapshot();
            snapshot.Edits.TryGetValue(key ?? string.Empty, out var edit);
            TalearrProviderBookValues provider = null;
            edit?.Provider.TryGetValue(row.Id, out provider);

            var details = new TalearrBookDetails
            {
                BookId = row.Id,
                WorkKey = key,
                MediaType = row.MediaType == BookMediaType.Audiobook ? "audiobook" : "ebook"
            };

            details.Title = new TalearrEditedValue<string>
            {
                Edited = edit?.HasTitle == true,
                Value = edit?.HasTitle == true ? edit.Title : row.Title,
                ProviderValue = provider?.TitleCaptured == true ? provider.Title : row.Title
            };

            var currentSeries = CurrentSeries(row);
            details.Series = new TalearrEditedValue<List<TalearrSeriesEntry>>
            {
                Edited = edit?.HasSeries == true,
                Value = edit?.HasSeries == true ? WithSeriesIds(row, edit.Series) : currentSeries,
                ProviderValue = provider?.SeriesCaptured == true ? ProviderSeries(provider) : currentSeries
            };

            if (row.MediaType == BookMediaType.Audiobook)
            {
                var target = NarratorTarget(_library.GetEditions(row.Id));
                var editionKey = EditionKey(target);
                if (editionKey != null)
                {
                    snapshot.NarratorsByKey.TryGetValue(editionKey, out var narratorEdit);
                    var current = NarratorList(target.Narrator, target.NarratorNames);

                    details.NarratorEditionId = target.Id;
                    details.Narrators = new TalearrEditedValue<List<string>>
                    {
                        Edited = narratorEdit != null,
                        Value = narratorEdit != null ? narratorEdit.Narrators.ToList() : current,
                        ProviderValue = narratorEdit?.ProviderCaptured == true
                            ? NarratorList(narratorEdit.ProviderNarrator, narratorEdit.ProviderNarratorNames)
                            : current
                    };
                }
            }

            details.AuthorSeries = SeriesChoices(row, snapshot)
                .Select(s => new TalearrSeriesOption { Id = s.Id, Title = s.Title, CreatedByTalearr = snapshot.UserSeriesIds.Contains(s.Id) })
                .OrderBy(s => s.Title, StringComparer.OrdinalIgnoreCase)
                .ToList();

            return details;
        }

        private List<TalearrSeriesEntry> CurrentSeries(Book row)
        {
            var links = _library.GetLinks(row.Id).Where(IsOriginalLink).ToList();
            var result = new List<TalearrSeriesEntry>();

            foreach (var link in links)
            {
                var series = _library.GetSeries(link.SeriesId);
                if (series != null && !string.IsNullOrWhiteSpace(series.Title))
                {
                    result.Add(new TalearrSeriesEntry { SeriesId = series.Id, Title = series.Title, Position = EmptyToNull(link.Position) });
                }
            }

            // The book's own series name with no link behind it (the series row was removed, e.g. a
            // Goodreads series Hardcover does not list) groups nothing. No SeriesId tells the editor
            // so saving it links the book instead of treating it as unchanged.
            if (result.Count == 0 && !string.IsNullOrWhiteSpace(row.SeriesName))
            {
                result.Add(new TalearrSeriesEntry { Title = row.SeriesName, Position = EmptyToNull(row.SeriesPosition) });
            }

            // The book's main series first, as the book routes show it.
            return result
                .OrderByDescending(e => (row.SeriesId.HasValue && e.SeriesId == row.SeriesId) || SameTitle(e.Title, row.SeriesName))
                .ToList();
        }

        private List<TalearrSeriesEntry> ProviderSeries(TalearrProviderBookValues provider)
        {
            var result = new List<TalearrSeriesEntry>();
            foreach (var link in provider.Links ?? new List<TalearrProviderSeriesLink>())
            {
                var series = _library.GetSeries(link.SeriesId);
                if (series != null)
                {
                    result.Add(new TalearrSeriesEntry { SeriesId = series.Id, Title = series.Title, Position = EmptyToNull(link.Position) });
                }
            }

            if (result.Count == 0 && !string.IsNullOrWhiteSpace(provider.SeriesName))
            {
                result.Add(new TalearrSeriesEntry { Title = provider.SeriesName, Position = EmptyToNull(provider.SeriesPosition) });
            }

            return result
                .OrderByDescending(e => SameTitle(e.Title, provider.SeriesName))
                .ToList();
        }

        private List<TalearrSeriesEntry> WithSeriesIds(Book row, List<TalearrSeriesEntry> entries)
        {
            var linked = _library.GetLinks(row.Id)
                .Where(IsOriginalLink)
                .Select(l => _library.GetSeries(l.SeriesId))
                .Where(s => s != null)
                .ToList();

            return entries
                .Select(e => new TalearrSeriesEntry
                {
                    Title = e.Title,
                    Position = e.Position,
                    SeriesId = linked.FirstOrDefault(s => SameTitle(s.Title, e.Title))?.Id
                })
                .ToList();
        }

        private List<Series> SeriesChoices(Book row, EditSnapshot snapshot)
        {
            var result = _library.GetAuthorSeries(row.AuthorId)
                .Where(s => s != null && s.MediaType == row.MediaType && s.IsOriginal)
                .ToList();

            foreach (var userSeries in snapshot.UserSeries.Where(u => u.AuthorId == row.AuthorId && u.MediaType == (int)row.MediaType))
            {
                if (result.All(s => s.Id != userSeries.SeriesId))
                {
                    var series = _library.GetSeries(userSeries.SeriesId);
                    if (series != null)
                    {
                        result.Add(series);
                    }
                }
            }

            return result
                .GroupBy(s => s.Id)
                .Select(g => g.First())
                .ToList();
        }

        private List<TalearrSeriesEntry> NormalizeSeries(List<TalearrSeriesEntry> entries, out string problem)
        {
            problem = null;
            var result = new List<TalearrSeriesEntry>();

            foreach (var entry in entries.Where(e => e != null))
            {
                var title = entry.Title?.Trim();
                if (string.IsNullOrEmpty(title) && entry.SeriesId.HasValue)
                {
                    title = _library.GetSeries(entry.SeriesId.Value)?.Title?.Trim();
                    if (string.IsNullOrEmpty(title))
                    {
                        problem = $"series {entry.SeriesId.Value} does not exist";
                        return null;
                    }
                }

                if (string.IsNullOrEmpty(title))
                {
                    problem = "every series needs a title";
                    return null;
                }

                if (title.Length > MaxTitleLength)
                {
                    problem = $"a series title is longer than {MaxTitleLength} characters";
                    return null;
                }

                if (result.Any(r => SameTitle(r.Title, title)))
                {
                    continue;
                }

                result.Add(new TalearrSeriesEntry { Title = title, Position = EmptyToNull(entry.Position?.Trim()) });
            }

            return result;
        }

        private static List<string> NormalizeNarrators(IEnumerable<string> names)
        {
            return (names ?? Enumerable.Empty<string>())
                .Where(n => !string.IsNullOrWhiteSpace(n))
                .Select(n => n.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        private static List<string> NormalizeFields(IEnumerable<string> fields, out string bad)
        {
            bad = null;
            var result = new List<string>();
            foreach (var field in (fields ?? Enumerable.Empty<string>()).Where(f => !string.IsNullOrWhiteSpace(f)))
            {
                var match = TalearrBookEditFields.All.FirstOrDefault(f => string.Equals(f, field.Trim(), StringComparison.OrdinalIgnoreCase));
                if (match == null)
                {
                    bad = field;
                    return new List<string>();
                }

                if (!result.Contains(match))
                {
                    result.Add(match);
                }
            }

            return result;
        }

        private static List<string> NarratorList(string narrator, List<string> names)
        {
            var list = NormalizeNarrators(names);
            if (list.Count == 0 && !string.IsNullOrWhiteSpace(narrator))
            {
                list.Add(narrator.Trim());
            }

            return list;
        }

        public static string JoinNarrators(IEnumerable<string> names)
        {
            var list = (names ?? Enumerable.Empty<string>()).ToList();
            return list.Count == 0 ? null : string.Join(", ", list);
        }

        private static bool SameNames(List<string> left, List<string> right)
        {
            return (left ?? new List<string>()).SequenceEqual(right ?? new List<string>(), StringComparer.Ordinal);
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

        // SeriesBookLink.SeriesPosition is the whole-number position, 0 when there is none
        // (RefreshSeriesService does the same with the provider's position).
        private static int PositionNumber(string position)
        {
            return int.TryParse(position, out var number) ? number : 0;
        }

        private static TalearrBookEditResult NotFound(int bookId)
        {
            return new TalearrBookEditResult { Status = TalearrBookEditStatus.NotFound, Message = $"Book {bookId} not found" };
        }

        private static TalearrBookEditResult Invalid(int bookId, string message)
        {
            return new TalearrBookEditResult { Status = TalearrBookEditStatus.Invalid, Message = message };
        }

        private void Invalidate()
        {
            lock (_lock)
            {
                _cache = null;
            }
        }

        private EditSnapshot Snapshot()
        {
            lock (_lock)
            {
                if (_cache != null && DateTime.UtcNow - _cachedAt < CacheLifetime)
                {
                    return _cache;
                }

                _cache = new EditSnapshot(_store.GetAll(), _store.GetNarratorEdits(), _store.GetUserSeries());
                _cachedAt = DateTime.UtcNow;
                return _cache;
            }
        }

        // Finds or creates the Series row an entry means for one book row (same author and media
        // type, matched by title). New rows are recorded in UserSeries.
        private sealed class SeriesResolver
        {
            private readonly TalearrBookEditService _owner;
            private readonly Dictionary<string, List<Series>> _choices = new Dictionary<string, List<Series>>();

            public SeriesResolver(TalearrBookEditService owner)
            {
                _owner = owner;
            }

            public bool Created { get; private set; }

            public Series Resolve(Book row, string title, string position)
            {
                if (string.IsNullOrWhiteSpace(title))
                {
                    return null;
                }

                var key = $"{row.AuthorId}|{(int)row.MediaType}";
                if (!_choices.TryGetValue(key, out var choices))
                {
                    choices = _owner.SeriesChoices(row, _owner.Snapshot());
                    _choices[key] = choices;
                }

                // Provider series first, so an edit naming a provider series links to it.
                var userIds = _owner.Snapshot().UserSeriesIds;
                var match = choices
                    .Where(s => SameTitle(s.Title, title))
                    .OrderBy(s => userIds.Contains(s.Id) ? 1 : 0)
                    .ThenBy(s => s.Id)
                    .FirstOrDefault();

                if (match != null)
                {
                    return match;
                }

                var created = _owner._library.AddSeries(new Series
                {
                    Title = title.Trim(),
                    Description = string.Empty,
                    Numbered = !string.IsNullOrWhiteSpace(position),
                    SeriesType = "main",
                    MediaType = row.MediaType,
                    Links = new Dictionary<string, string>(),
                    ProviderUrls = new ProviderUrlMap()
                });

                if (created == null || created.Id <= 0)
                {
                    return null;
                }

                _owner._store.AddUserSeries(new TalearrUserSeries
                {
                    SeriesId = created.Id,
                    AuthorId = row.AuthorId,
                    MediaType = (int)row.MediaType,
                    Title = created.Title,
                    CreatedAt = DateTime.UtcNow
                });

                _owner._logger.Info("Talearr: created series '{0}' ({1}) for an edit of '{2}'", created.Title, row.MediaType, row.Title);

                choices.Add(created);
                Created = true;
                _owner.Invalidate();
                return created;
            }
        }

        private sealed class EditSnapshot
        {
            public EditSnapshot(List<TalearrBookEdit> edits, List<TalearrNarratorEdit> narrators, List<TalearrUserSeries> userSeries)
            {
                Edits = (edits ?? new List<TalearrBookEdit>())
                    .Where(e => e?.WorkKey != null)
                    .GroupBy(e => e.WorkKey.Trim(), StringComparer.OrdinalIgnoreCase)
                    .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);

                var narratorList = (narrators ?? new List<TalearrNarratorEdit>()).Where(n => n?.EditionKey != null && n.WorkKey != null).ToList();
                NarratorsByKey = narratorList
                    .GroupBy(n => n.EditionKey.Trim(), StringComparer.OrdinalIgnoreCase)
                    .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
                NarratorsByWork = narratorList
                    .GroupBy(n => n.WorkKey.Trim(), StringComparer.OrdinalIgnoreCase)
                    .ToDictionary(g => g.Key, g => g.ToList(), StringComparer.OrdinalIgnoreCase);

                UserSeries = userSeries ?? new List<TalearrUserSeries>();
                UserSeriesIds = UserSeries.Select(u => u.SeriesId).ToHashSet();

                // The book rows a series edit was applied to, for the series refresh filter.
                SeriesByBookId = new Dictionary<int, List<TalearrSeriesEntry>>();
                foreach (var edit in Edits.Values.Where(e => e.HasSeries))
                {
                    foreach (var pair in edit.Provider.Where(p => p.Value?.SeriesCaptured == true))
                    {
                        SeriesByBookId[pair.Key] = edit.Series;
                    }
                }
            }

            public Dictionary<string, TalearrBookEdit> Edits { get; }
            public Dictionary<string, TalearrNarratorEdit> NarratorsByKey { get; }
            public Dictionary<string, List<TalearrNarratorEdit>> NarratorsByWork { get; }
            public List<TalearrUserSeries> UserSeries { get; }
            public HashSet<int> UserSeriesIds { get; }
            public Dictionary<int, List<TalearrSeriesEntry>> SeriesByBookId { get; }

            public bool IsEmpty => Edits.Count == 0 && NarratorsByKey.Count == 0;
        }
    }
}
