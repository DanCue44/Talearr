using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using NLog;
using NzbDrone.Common.Instrumentation.Extensions;
using NzbDrone.Core.Books;
using NzbDrone.Core.Books.Events;
using NzbDrone.Core.IndexerSearch;
using NzbDrone.Core.MediaFiles;
using NzbDrone.Core.Messaging.Commands;
using NzbDrone.Core.Messaging.Events;
using NzbDrone.Core.Talearr.GrabPolicy;
using NzbDrone.Core.Talearr.Recordings;

namespace NzbDrone.Core.Talearr.SeriesMonitoring
{
    public interface ITalearrSeriesMonitorService
    {
        List<TalearrSeriesSummary> GetSeries(bool monitoredOnly = false);
        TalearrSeriesDetail GetDetail(int seriesId);
        TalearrSeriesDetail SetRule(int seriesId, TalearrSeriesRuleRequest request);
        TalearrSeriesApplyResult Apply(int seriesId);
        TalearrSeriesDetail EnableAuthors(int seriesId, string format);
        List<TalearrSeriesApplyResult> ApplyAll(int? authorId = null);
    }

    // Talearr: monitor a series (docs/TALEARR_SERIES.md). A rule says which formats of the series'
    // books Talearr wants; applying it monitors every book of the series in those formats that the
    // rule has not acted on before and searches for the new ones. New books reach the series through
    // the normal author refresh, which ends with AuthorRefreshCompleteEvent, so the rule is applied
    // again after every refresh and on a schedule.
    //
    // A rule only ever turns monitoring on, once per book and format: a book you unmonitor later
    // stays unmonitored, and removing a rule changes no book. It touches the primary row of each
    // format only; additional recordings (Talearr recordings) keep their own monitoring. Grabs go
    // through the normal search, so the grab policy (abridged, bundles, retries) applies as usual.
    public class TalearrSeriesMonitorService : ITalearrSeriesMonitorService,
                                               IExecute<ApplySeriesMonitoringCommand>,
                                               IHandleAsync<AuthorRefreshCompleteEvent>,
                                               IHandleAsync<SeriesRefreshCompleteEvent>
    {
        private static readonly Regex RangePositionRegex = new Regex(@"^\s*\d+(?:\.\d+)?\s*(?:-|–|&|,|to)\s*\d+", RegexOptions.Compiled | RegexOptions.IgnoreCase);
        private static readonly Regex FirstNumberRegex = new Regex(@"\d+(?:[.,]\d+)?", RegexOptions.Compiled);

        private readonly ISeriesService _seriesService;
        private readonly ISeriesBookLinkService _linkService;
        private readonly IBookService _bookService;
        private readonly IAuthorService _authorService;
        private readonly IMediaFileService _mediaFileService;
        private readonly ITalearrSeriesRuleStore _store;
        private readonly ITalearrGrabPolicyService _grabPolicyService;
        private readonly IManageCommandQueue _commandQueueManager;
        private readonly Logger _logger;
        private readonly object _applyLock = new object();

        public TalearrSeriesMonitorService(ISeriesService seriesService,
                                           ISeriesBookLinkService linkService,
                                           IBookService bookService,
                                           IAuthorService authorService,
                                           IMediaFileService mediaFileService,
                                           ITalearrSeriesRuleStore store,
                                           ITalearrGrabPolicyService grabPolicyService,
                                           IManageCommandQueue commandQueueManager,
                                           Logger logger)
        {
            _seriesService = seriesService;
            _linkService = linkService;
            _bookService = bookService;
            _authorService = authorService;
            _mediaFileService = mediaFileService;
            _store = store;
            _grabPolicyService = grabPolicyService;
            _commandQueueManager = commandQueueManager;
            _logger = logger;
        }

        // One identity per series across its media type rows (Chaptarr keeps an audiobook and an
        // ebook row per series): the provider series id, else the base series id, else the title.
        public static string KeyOf(Series series)
        {
            if (series == null)
            {
                return null;
            }

            var key = Prefixed("gr", series.GoodreadsSeriesId) ??
                      Prefixed("hc", series.HardcoverSeriesId) ??
                      Prefixed("ol", series.OpenLibrarySeriesId) ??
                      Prefixed("base", series.BaseSeriesId);

            if (key != null)
            {
                return key;
            }

            var title = Regex.Replace((series.Title ?? string.Empty).ToLowerInvariant(), @"[^\p{L}\p{N}]+", "-").Trim('-');
            return title.Length == 0 ? "id:" + series.Id.ToString(CultureInfo.InvariantCulture) : "title:" + title;
        }

        public static decimal? SortPositionOf(string position)
        {
            if (string.IsNullOrWhiteSpace(position))
            {
                return null;
            }

            var match = FirstNumberRegex.Match(position);
            return match.Success && decimal.TryParse(match.Value.Replace(',', '.'), NumberStyles.Number, CultureInfo.InvariantCulture, out var value)
                ? value
                : null;
        }

        public static bool IsBundleEntry(Book book, string position)
        {
            return (book?.IsOmnibus ?? false) ||
                   (!string.IsNullOrWhiteSpace(position) && RangePositionRegex.IsMatch(position)) ||
                   TalearrBundleDetector.LooksLikeBundleTitle(book?.Title);
        }

        public static bool IsExtraEntry(bool isPrimary, string position)
        {
            if (!isPrimary)
            {
                return true;
            }

            var sort = SortPositionOf(position);
            return sort.HasValue && sort.Value != decimal.Truncate(sort.Value);
        }

        public List<TalearrSeriesSummary> GetSeries(bool monitoredOnly = false)
        {
            var rules = _store.All().ToDictionary(r => r.SeriesKey, StringComparer.OrdinalIgnoreCase);
            var groups = Groups(_seriesService.GetAllSeries());

            var result = new List<TalearrSeriesSummary>();
            var authorBooks = new Dictionary<int, List<Book>>();
            foreach (var group in groups)
            {
                rules.TryGetValue(group.Key, out var rule);
                if (monitoredOnly && rule == null)
                {
                    continue;
                }

                var entries = BuildEntries(group.ToList(), rule, includeFiles: true, authorBooks);
                result.Add(Summarize(group.Key, group.ToList(), entries, rule));
            }

            return result.OrderBy(s => s.Title, StringComparer.OrdinalIgnoreCase).ToList();
        }

        public TalearrSeriesDetail GetDetail(int seriesId)
        {
            var rows = RowsFor(seriesId);
            if (rows == null)
            {
                return null;
            }

            var key = KeyOf(rows.First());
            var rule = _store.Get(key);
            var entries = BuildEntries(rows, rule, includeFiles: true);

            return new TalearrSeriesDetail
            {
                Series = Summarize(key, rows, entries, rule),
                Description = rows.Select(r => r.Description).FirstOrDefault(d => !string.IsNullOrWhiteSpace(d)),
                AllowBundles = TalearrGrabPolicyService.Resolve(_grabPolicyService).AllowBundles,
                Entries = entries
            };
        }

        public TalearrSeriesDetail SetRule(int seriesId, TalearrSeriesRuleRequest request)
        {
            var rows = RowsFor(seriesId);
            if (rows == null)
            {
                return null;
            }

            request ??= new TalearrSeriesRuleRequest();
            var key = KeyOf(rows.First());

            lock (_applyLock)
            {
                var rule = _store.Get(key);

                if (!request.Audiobook && !request.Ebook)
                {
                    if (rule != null)
                    {
                        _store.Remove(key);
                        _logger.Info("Talearr: stopped monitoring series '{0}'. Books keep their current monitoring.", rule.Title);
                    }

                    return GetDetail(seriesId);
                }

                rule ??= new TalearrSeriesRule { SeriesKey = key, AddedAt = DateTime.UtcNow };
                rule.Title = TitleOf(rows);

                foreach (var format in TalearrSeriesFormats.All)
                {
                    var wanted = format == TalearrSeriesFormats.Audiobook ? request.Audiobook : request.Ebook;
                    if (!wanted)
                    {
                        // Turning a format off forgets what the rule did for it; turning it on again
                        // starts over for that format.
                        rule.Handled.Remove(format);
                        rule.PendingSearch.Remove(format);
                    }
                }

                rule.Audiobook = request.Audiobook;
                rule.Ebook = request.Ebook;
                rule.SearchOnAdd = request.SearchOnAdd ?? rule.SearchOnAdd;
                rule.IncludeExtras = request.IncludeExtras ?? rule.IncludeExtras;

                _store.Save(rule);
                _logger.Info("Talearr: monitoring series '{0}' for {1}", rule.Title, DescribeFormats(rule));

                ApplyRule(rule, rows);
            }

            return GetDetail(seriesId);
        }

        public TalearrSeriesApplyResult Apply(int seriesId)
        {
            var rows = RowsFor(seriesId);
            if (rows == null)
            {
                return null;
            }

            lock (_applyLock)
            {
                var rule = _store.Get(KeyOf(rows.First()));
                return rule == null ? null : ApplyRule(rule, rows);
            }
        }

        // Turns the format on for the authors of the series' books whose format side is off (the
        // same switch as the author's edit dialog). Only the author gate changes: no other book of
        // the author is monitored, and "monitor new items" is left as it is.
        public TalearrSeriesDetail EnableAuthors(int seriesId, string format)
        {
            var rows = RowsFor(seriesId);
            if (rows == null)
            {
                return null;
            }

            var isAudiobook = string.Equals(format, TalearrSeriesFormats.Audiobook, StringComparison.OrdinalIgnoreCase);
            if (!isAudiobook && !string.Equals(format, TalearrSeriesFormats.Ebook, StringComparison.OrdinalIgnoreCase))
            {
                throw new ArgumentException("format must be audiobook or ebook", nameof(format));
            }

            var mediaType = isAudiobook ? BookMediaType.Audiobook : BookMediaType.Ebook;

            lock (_applyLock)
            {
                var rule = _store.Get(KeyOf(rows.First()));
                var entries = BuildEntries(rows, rule, includeFiles: false);
                var authorIds = entries
                    .Where(e => e.SkipReason == null)
                    .Where(e => (isAudiobook ? e.Audiobook : e.Ebook) is { BookId: not null, AuthorMonitored: false })
                    .Select(e => e.AuthorId)
                    .Where(id => id > 0)
                    .Distinct()
                    .ToList();

                foreach (var authorId in authorIds)
                {
                    var author = _authorService.GetAuthor(authorId);
                    if (author != null && author.ApplyMediaTypeMonitoringSettings(mediaType, true, null))
                    {
                        _authorService.UpdateAuthor(author);
                        _logger.Info("Talearr: {0} monitoring turned on for author '{1}' from series '{2}'", isAudiobook ? "Audiobook" : "Ebook", author.Name, TitleOf(rows));
                    }
                }

                if (rule != null)
                {
                    ApplyRule(rule, rows);
                }
            }

            return GetDetail(seriesId);
        }

        public List<TalearrSeriesApplyResult> ApplyAll(int? authorId = null)
        {
            var rules = _store.All();
            if (!rules.Any())
            {
                return new List<TalearrSeriesApplyResult>();
            }

            var groups = Groups(_seriesService.GetAllSeries()).ToDictionary(g => g.Key, g => g.ToList(), StringComparer.OrdinalIgnoreCase);
            var results = new List<TalearrSeriesApplyResult>();

            lock (_applyLock)
            {
                foreach (var stored in rules)
                {
                    if (!groups.TryGetValue(stored.SeriesKey, out var rows))
                    {
                        continue;
                    }

                    if (authorId.HasValue && !TouchesAuthor(rows, authorId.Value))
                    {
                        continue;
                    }

                    try
                    {
                        // Re-read under the lock: the rule may have changed since All().
                        var rule = _store.Get(stored.SeriesKey) ?? stored;
                        results.Add(ApplyRule(rule, rows));
                    }
                    catch (Exception ex)
                    {
                        _logger.Warn(ex, "Talearr: unable to apply series monitoring for '{0}'", stored.Title);
                    }
                }
            }

            return results;
        }

        public void Execute(ApplySeriesMonitoringCommand message)
        {
            var results = ApplyAll(message.AuthorId);
            var newly = results.Sum(r => r.Monitored);
            if (newly > 0)
            {
                _logger.ProgressInfo("Series monitoring: {0} books newly monitored across {1} series", newly, results.Count(r => r.Monitored > 0));
            }
        }

        public void HandleAsync(AuthorRefreshCompleteEvent message)
        {
            QueueFor(message?.Author?.Id);
        }

        public void HandleAsync(SeriesRefreshCompleteEvent message)
        {
            QueueFor(null);
        }

        private void QueueFor(int? authorId)
        {
            try
            {
                if (!_store.All().Any())
                {
                    return;
                }

                _commandQueueManager.Push(new ApplySeriesMonitoringCommand { AuthorId = authorId }, CommandPriority.Low, CommandTrigger.Scheduled);
            }
            catch (Exception ex)
            {
                _logger.Debug(ex, "Talearr: unable to queue series monitoring");
            }
        }

        internal TalearrSeriesApplyResult ApplyRule(TalearrSeriesRule rule, List<Series> rows)
        {
            var result = new TalearrSeriesApplyResult { SeriesKey = rule.SeriesKey };
            var entries = BuildEntries(rows, rule, includeFiles: true);
            var toSearch = new List<int>();

            foreach (var format in TalearrSeriesFormats.All)
            {
                if (!rule.Wants(format))
                {
                    continue;
                }

                var handled = new HashSet<string>(rule.Handled.TryGetValue(format, out var list) ? list : new List<string>(), StringComparer.OrdinalIgnoreCase);
                var pending = new HashSet<string>(rule.PendingSearch.TryGetValue(format, out var waiting) ? waiting : new List<string>(), StringComparer.OrdinalIgnoreCase);
                var toMonitor = new List<(TalearrSeriesEntry Entry, TalearrSeriesFormatState State)>();

                // Books monitored while their author's format was off: search them once it is on.
                foreach (var entry in entries.Where(e => pending.Contains(e.BaseBookId)))
                {
                    var state = format == TalearrSeriesFormats.Audiobook ? entry.Audiobook : entry.Ebook;
                    if (state?.BookId == null || !state.Monitored || state.HasFiles)
                    {
                        pending.Remove(entry.BaseBookId);
                    }
                    else if (state.AuthorMonitored)
                    {
                        pending.Remove(entry.BaseBookId);
                        if (rule.SearchOnAdd)
                        {
                            toSearch.Add(state.BookId.Value);
                        }
                    }
                }

                pending.RemoveWhere(id => entries.All(e => !string.Equals(e.BaseBookId, id, StringComparison.OrdinalIgnoreCase)));

                foreach (var entry in entries)
                {
                    if (handled.Contains(entry.BaseBookId))
                    {
                        continue;
                    }

                    if (entry.SkipReason != null)
                    {
                        result.Skipped++;
                        continue;
                    }

                    var state = format == TalearrSeriesFormats.Audiobook ? entry.Audiobook : entry.Ebook;
                    if (state?.BookId == null)
                    {
                        // Not in the library in this format yet; the next refresh may add it.
                        result.NoEntry++;
                        continue;
                    }

                    toMonitor.Add((entry, state));
                }

                var newIds = toMonitor.Where(x => !x.State.Monitored).Select(x => x.State.BookId.Value).Distinct().ToList();
                if (newIds.Any())
                {
                    _bookService.SetMonitoredForMediaType(newIds, format, true);
                }

                var now = newIds.Any()
                    ? (_bookService.GetBooks(newIds) ?? new List<Book>()).ToDictionary(b => b.Id)
                    : new Dictionary<int, Book>();

                foreach (var (entry, state) in toMonitor)
                {
                    var id = state.BookId.Value;
                    if (!state.Monitored)
                    {
                        if (!now.TryGetValue(id, out var book) || !book.IsMonitoredForMediaType(format))
                        {
                            // Upstream refuses without a root folder for the format; try again later.
                            result.NotAllowed++;
                            continue;
                        }

                        result.Monitored++;
                    }
                    else
                    {
                        result.AlreadyMonitored++;
                    }

                    handled.Add(entry.BaseBookId);

                    if (!state.AuthorMonitored)
                    {
                        result.AuthorPaused++;
                        if (!state.HasFiles)
                        {
                            pending.Add(entry.BaseBookId);
                        }

                        continue;
                    }

                    if (rule.SearchOnAdd && !state.HasFiles)
                    {
                        toSearch.Add(id);
                    }
                }

                rule.Handled[format] = handled.OrderBy(x => x, StringComparer.Ordinal).ToList();
                rule.PendingSearch[format] = pending.OrderBy(x => x, StringComparer.Ordinal).ToList();
            }

            toSearch = toSearch.Distinct().ToList();
            if (toSearch.Any())
            {
                _commandQueueManager.Push(new BookSearchCommand(toSearch), CommandPriority.Normal, CommandTrigger.Scheduled);
                result.Searched = toSearch.Count;
            }

            rule.Title = TitleOf(rows) ?? rule.Title;
            rule.LastAppliedAt = DateTime.UtcNow;
            rule.LastResult = result.Describe();
            _store.Save(rule);

            if (result.Monitored > 0 || result.Searched > 0)
            {
                _logger.Info("Talearr: series '{0}': {1}", rule.Title, rule.LastResult);
            }
            else
            {
                _logger.Debug("Talearr: series '{0}': {1}", rule.Title, rule.LastResult);
            }

            return result;
        }

        internal List<TalearrSeriesEntry> BuildEntries(List<Series> rows, TalearrSeriesRule rule, bool includeFiles, Dictionary<int, List<Book>> authorBooks = null)
        {
            var links = rows
                .SelectMany(r => _linkService.GetLinksBySeries(r.Id) ?? new List<SeriesBookLink>())
                .Where(l => l != null && l.BookId > 0)
                .ToList();

            if (!links.Any())
            {
                return new List<TalearrSeriesEntry>();
            }

            var linkedBooks = (_bookService.GetBooks(links.Select(l => l.BookId).Distinct().ToList()) ?? new List<Book>())
                .ToDictionary(b => b.Id);

            var allowBundles = TalearrGrabPolicyService.Resolve(_grabPolicyService).AllowBundles;
            var authors = new Dictionary<int, Author>();
            var entries = new List<TalearrSeriesEntry>();

            var works = links
                .Where(l => linkedBooks.ContainsKey(l.BookId))
                .GroupBy(l => WorkKey(linkedBooks[l.BookId]), StringComparer.OrdinalIgnoreCase);

            authorBooks ??= new Dictionary<int, List<Book>>();
            var workRows = works.ToDictionary(
                w => w.Key,
                w => (Links: w.ToList(), Rows: RowsOfWork(w.Key, w.Select(l => linkedBooks[l.BookId]).ToList(), authorBooks)),
                StringComparer.OrdinalIgnoreCase);

            var withFiles = includeFiles
                ? BooksWithFiles(workRows.Values.SelectMany(w => w.Rows).Select(b => b.Id).Distinct().ToList())
                : new HashSet<int>();

            foreach (var (workKey, (workLinks, rowsOfWork)) in workRows)
            {
                var display = rowsOfWork.FirstOrDefault(b => b.MediaType == BookMediaType.Ebook && !TalearrRecordingGroupService.IsCopySlug(b.TitleSlug)) ??
                              rowsOfWork.FirstOrDefault(b => !TalearrRecordingGroupService.IsCopySlug(b.TitleSlug)) ??
                              linkedBooks[workLinks.First().BookId];

                var position = workLinks.Select(l => l.Position).FirstOrDefault(p => !string.IsNullOrWhiteSpace(p));
                var isPrimary = workLinks.Any(l => l.IsPrimary);
                var isBundle = IsBundleEntry(display, position) || rowsOfWork.Any(b => b.IsOmnibus);
                var isExtra = IsExtraEntry(isPrimary, position);

                var entry = new TalearrSeriesEntry
                {
                    BaseBookId = workKey,
                    Title = display.Title,
                    AuthorId = display.AuthorId,
                    AuthorName = AuthorOf(display.AuthorId, authors)?.Name,
                    Position = position,
                    SortPosition = SortPositionOf(position),
                    IsPrimary = isPrimary,
                    IsBundle = isBundle,
                    IsExtra = isExtra,
                    SkipReason = isBundle && !allowBundles
                        ? "bundle"
                        : (isExtra && !(rule?.IncludeExtras ?? false) ? "extra" : null)
                };

                entry.Audiobook = FormatState(TalearrSeriesFormats.Audiobook, rowsOfWork, rule, workKey, authors, withFiles);
                entry.Ebook = FormatState(TalearrSeriesFormats.Ebook, rowsOfWork, rule, workKey, authors, withFiles);
                entries.Add(entry);
            }

            return entries
                .OrderBy(e => e.SortPosition ?? decimal.MaxValue)
                .ThenBy(e => e.Position, StringComparer.OrdinalIgnoreCase)
                .ThenBy(e => e.Title, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        private TalearrSeriesFormatState FormatState(string format, List<Book> rowsOfWork, TalearrSeriesRule rule, string workKey, Dictionary<int, Author> authors, HashSet<int> withFiles)
        {
            var mediaType = format == TalearrSeriesFormats.Audiobook ? BookMediaType.Audiobook : BookMediaType.Ebook;
            var ofFormat = rowsOfWork.Where(b => b.MediaType == mediaType).OrderBy(b => b.Id).ToList();
            var primary = ofFormat.FirstOrDefault(b => !TalearrRecordingGroupService.IsCopySlug(b.TitleSlug));
            var handled = rule != null && rule.Handled.TryGetValue(format, out var list) && list.Contains(workKey, StringComparer.OrdinalIgnoreCase);

            var state = new TalearrSeriesFormatState
            {
                Format = format,
                Handled = handled,
                Copies = ofFormat.Count(b => primary == null || b.Id != primary.Id)
            };

            if (primary == null)
            {
                return state;
            }

            var author = AuthorOf(primary.AuthorId, authors);
            state.BookId = primary.Id;
            state.Monitored = primary.IsMonitoredForMediaType(mediaType);
            state.AuthorMonitored = author != null && author.IsMonitoredForMediaType(mediaType == BookMediaType.Audiobook);
            state.HasFiles = withFiles.Contains(primary.Id);

            return state;
        }

        private HashSet<int> BooksWithFiles(List<int> bookIds)
        {
            try
            {
                return (_mediaFileService.GetFilesByBooks(bookIds) ?? new List<BookFile>())
                    .Select(f => f.Edition?.BookId ?? 0)
                    .Where(id => id > 0)
                    .ToHashSet();
            }
            catch (Exception ex)
            {
                _logger.Debug(ex, "Talearr: unable to read the files of {0} books", bookIds.Count);
                return new HashSet<int>();
            }
        }

        // The book rows of one work in every format: the linked rows plus the author's rows with the
        // same BaseBookId (the other format's row is not always linked to a series row).
        private List<Book> RowsOfWork(string workKey, List<Book> linked, Dictionary<int, List<Book>> authorBooks)
        {
            var rows = new Dictionary<int, Book>();
            foreach (var book in linked)
            {
                rows[book.Id] = book;
            }

            if (workKey.StartsWith("id:", StringComparison.Ordinal))
            {
                return rows.Values.ToList();
            }

            foreach (var authorId in linked.Select(b => b.AuthorId).Where(id => id > 0).Distinct())
            {
                if (!authorBooks.TryGetValue(authorId, out var books))
                {
                    try
                    {
                        books = _bookService.GetBooksByAuthor(authorId) ?? new List<Book>();
                    }
                    catch (Exception ex)
                    {
                        _logger.Debug(ex, "Talearr: unable to read the books of author {0}", authorId);
                        books = new List<Book>();
                    }

                    authorBooks[authorId] = books;
                }

                foreach (var book in books.Where(b => string.Equals(b.BaseBookId?.Trim(), workKey, StringComparison.OrdinalIgnoreCase)))
                {
                    rows[book.Id] = book;
                }
            }

            return rows.Values.ToList();
        }

        private Author AuthorOf(int authorId, Dictionary<int, Author> cache)
        {
            if (authorId <= 0)
            {
                return null;
            }

            if (!cache.TryGetValue(authorId, out var author))
            {
                try
                {
                    author = _authorService.GetAuthor(authorId);
                }
                catch (Exception)
                {
                    author = null;
                }

                cache[authorId] = author;
            }

            return author;
        }

        private static string WorkKey(Book book)
        {
            return string.IsNullOrWhiteSpace(book.BaseBookId) ? "id:" + book.Id.ToString(CultureInfo.InvariantCulture) : book.BaseBookId.Trim();
        }

        private List<Series> RowsFor(int seriesId)
        {
            Series series;
            try
            {
                series = _seriesService.GetSeries(seriesId);
            }
            catch (Exception)
            {
                series = null;
            }

            if (series == null)
            {
                return null;
            }

            var key = KeyOf(series);
            var rows = (_seriesService.GetAllSeries() ?? new List<Series>())
                .Where(s => !s.IsNarratorVariant && string.Equals(KeyOf(s), key, StringComparison.OrdinalIgnoreCase))
                .ToList();

            if (!rows.Any(r => r.Id == series.Id))
            {
                rows.Add(series);
            }

            return rows.OrderBy(r => r.MediaType).ThenBy(r => r.Id).ToList();
        }

        private static IEnumerable<IGrouping<string, Series>> Groups(IEnumerable<Series> all)
        {
            return (all ?? Enumerable.Empty<Series>())
                .Where(s => s != null && !s.IsNarratorVariant)
                .GroupBy(KeyOf, StringComparer.OrdinalIgnoreCase);
        }

        private bool TouchesAuthor(List<Series> rows, int authorId)
        {
            var bookIds = rows.SelectMany(r => _linkService.GetLinksBySeries(r.Id) ?? new List<SeriesBookLink>()).Select(l => l.BookId).Distinct().ToList();
            return bookIds.Any() && (_bookService.GetBooks(bookIds) ?? new List<Book>()).Any(b => b.AuthorId == authorId);
        }

        private TalearrSeriesSummary Summarize(string key, List<Series> rows, List<TalearrSeriesEntry> entries, TalearrSeriesRule rule)
        {
            var main = rows.FirstOrDefault(r => r.MediaType == BookMediaType.Audiobook) ?? rows.First();
            return new TalearrSeriesSummary
            {
                SeriesId = main.Id,
                SeriesIds = rows.Select(r => r.Id).ToList(),
                SeriesKey = key,
                Title = TitleOf(rows),
                Authors = entries.Select(e => e.AuthorName).Where(n => !string.IsNullOrWhiteSpace(n)).Distinct(StringComparer.OrdinalIgnoreCase).ToList(),
                BookCount = entries.Count(e => e.SkipReason == null),
                AudiobookFiles = entries.Count(e => e.Audiobook?.HasFiles == true),
                EbookFiles = entries.Count(e => e.Ebook?.HasFiles == true),
                Rule = rule
            };
        }

        private static string TitleOf(List<Series> rows)
        {
            return rows.Select(r => r.Title).FirstOrDefault(t => !string.IsNullOrWhiteSpace(t));
        }

        private static string DescribeFormats(TalearrSeriesRule rule)
        {
            if (rule.Audiobook && rule.Ebook)
            {
                return "audiobooks and ebooks";
            }

            return rule.Audiobook ? "audiobooks" : "ebooks";
        }

        private static string Prefixed(string source, string id)
        {
            if (string.IsNullOrWhiteSpace(id))
            {
                return null;
            }

            var trimmed = id.Trim().ToLowerInvariant();
            return trimmed.Contains(':') ? trimmed : source + ":" + trimmed;
        }
    }
}
