using System;
using System.Collections.Generic;
using System.Linq;
using NLog;
using NzbDrone.Common.Instrumentation.Extensions;
using NzbDrone.Core.Books;
using NzbDrone.Core.ImportLists.Exclusions;
using NzbDrone.Core.MediaFiles;
using NzbDrone.Core.Messaging.Commands;
using NzbDrone.Core.MetadataSource;
using NzbDrone.Core.Profiles.Metadata;

namespace NzbDrone.Core.Talearr.Metadata.Source
{
    public interface ITalearrMetadataShadowService
    {
        // Null when there is no local author with that id.
        TalearrShadowAuthorReport CompareAuthor(int authorId);

        // The last TalearrMetadataShadowCompare run (null before the first run since start).
        TalearrShadowRunReport GetLastRun();
    }

    // Talearr: Phase 3a shadow mode (docs/TALEARR_METADATA_SOURCE.md). For a local author with a
    // Hardcover id it reads the metadata server's author (the normal BookInfoProxy path) and
    // Talearr's Hardcover catalog, converts both with the same V5 conversion, filters both with
    // the author's own metadata profiles (RefreshAuthorService.NormalizeRemoteBooks) and reports
    // the differences. Strictly read-only: nothing here saves an author, book, edition, series
    // or sync row, and production refresh is not routed through it.
    public class TalearrMetadataShadowService : ITalearrMetadataShadowService, IExecute<TalearrMetadataShadowCompareCommand>
    {
        private readonly IAuthorService _authorService;
        private readonly IBookService _bookService;
        private readonly IEditionService _editionService;
        private readonly IMediaFileService _mediaFileService;
        private readonly IProvideAuthorInfo _authorInfo;
        private readonly ITalearrV5AuthorConverter _converter;
        private readonly ITalearrHardcoverMetadataClient _hardcover;
        private readonly IMetadataProfileService _metadataProfileService;
        private readonly IImportListExclusionService _importListExclusionService;
        private readonly Logger _logger;
        private readonly object _lock = new object();
        private TalearrShadowRunReport _lastRun;

        public TalearrMetadataShadowService(IAuthorService authorService,
                                            IBookService bookService,
                                            IEditionService editionService,
                                            IMediaFileService mediaFileService,
                                            IProvideAuthorInfo authorInfo,
                                            ITalearrV5AuthorConverter converter,
                                            ITalearrHardcoverMetadataClient hardcover,
                                            IMetadataProfileService metadataProfileService,
                                            IImportListExclusionService importListExclusionService,
                                            Logger logger)
        {
            _authorService = authorService;
            _bookService = bookService;
            _editionService = editionService;
            _mediaFileService = mediaFileService;
            _authorInfo = authorInfo;
            _converter = converter;
            _hardcover = hardcover;
            _metadataProfileService = metadataProfileService;
            _importListExclusionService = importListExclusionService;
            _logger = logger;
        }

        public static bool TryGetHardcoverAuthorId(Author author, out int id)
        {
            id = 0;
            var value = author?.HardcoverAuthorId?.Trim();
            if (string.IsNullOrWhiteSpace(value))
            {
                return false;
            }

            if (value.StartsWith("hc:", StringComparison.OrdinalIgnoreCase))
            {
                value = value.Substring(3);
            }

            return int.TryParse(value, out id) && id > 0;
        }

        public TalearrShadowRunReport GetLastRun()
        {
            lock (_lock)
            {
                return _lastRun;
            }
        }

        public TalearrShadowAuthorReport CompareAuthor(int authorId)
        {
            Author local;
            try
            {
                local = _authorService.GetAuthor(authorId);
            }
            catch (Datastore.ModelNotFoundException)
            {
                return null;
            }

            return local == null ? null : Compare(local);
        }

        public void Execute(TalearrMetadataShadowCompareCommand message)
        {
            var maxRequests = message.MaxHardcoverRequests > 0 ? message.MaxHardcoverRequests : TalearrMetadataShadowCompareCommand.DefaultMaxHardcoverRequests;
            var run = new TalearrShadowRunReport { StartedAt = DateTime.UtcNow };

            var authors = _authorService.GetAllAuthors();
            if (message.AuthorIds?.Count > 0)
            {
                var wanted = new HashSet<int>(message.AuthorIds);
                authors = authors.Where(a => wanted.Contains(a.Id)).ToList();
            }

            authors = authors.OrderBy(a => a.Name, StringComparer.OrdinalIgnoreCase).ToList();
            run.AuthorsTotal = authors.Count;

            if (!_hardcover.IsConfigured)
            {
                run.StoppedEarly = true;
                run.StopReason = "No Hardcover API token is set (Settings > Metadata).";
                Finish(run);
                _logger.Warn("[TALEARR-SHADOW] Not run: {0}", run.StopReason);
                return;
            }

            _logger.ProgressInfo("Comparing metadata for {0} authors (read only)", authors.Count);

            var index = 0;
            foreach (var author in authors)
            {
                index++;
                if (run.HardcoverRequests >= maxRequests)
                {
                    run.StoppedEarly = true;
                    run.StopReason = $"Stopped after {run.HardcoverRequests} Hardcover requests (limit {maxRequests}); {authors.Count - index + 1} authors not compared.";
                    break;
                }

                var report = Compare(author);
                run.Authors.Add(report);
                run.HardcoverRequests += report.HardcoverRequests;

                switch (report.Status)
                {
                    case "ok":
                        run.AuthorsCompared++;
                        break;
                    case "skipped":
                        run.AuthorsSkipped++;
                        break;
                    default:
                        run.AuthorsFailed++;
                        break;
                }

                if (report.LostProtectedCount > 0)
                {
                    run.AuthorsWithLostProtectedBooks++;
                    _logger.Warn("[TALEARR-SHADOW] {0}", TalearrMetadataShadowComparer.Summarize(report));
                }
                else if (report.Status != "skipped")
                {
                    _logger.Info("[TALEARR-SHADOW] {0}", TalearrMetadataShadowComparer.Summarize(report));
                }

                if (report.Message?.Contains("requests left today") == true)
                {
                    run.StoppedEarly = true;
                    run.StopReason = report.Message;
                    break;
                }

                if (index % 10 == 0)
                {
                    _logger.ProgressInfo("Compared metadata for {0} of {1} authors", index, authors.Count);
                }
            }

            Finish(run);
            _logger.ProgressInfo("Metadata shadow comparison: {0} compared, {1} skipped, {2} failed, {3} with protected books that would lose their match, {4} Hardcover requests{5}",
                run.AuthorsCompared,
                run.AuthorsSkipped,
                run.AuthorsFailed,
                run.AuthorsWithLostProtectedBooks,
                run.HardcoverRequests,
                run.StoppedEarly ? $". {run.StopReason}" : string.Empty);
        }

        private void Finish(TalearrShadowRunReport run)
        {
            run.FinishedAt = DateTime.UtcNow;
            lock (_lock)
            {
                _lastRun = run;
            }
        }

        private TalearrShadowAuthorReport Compare(Author local)
        {
            var now = DateTime.UtcNow;
            if (!TryGetHardcoverAuthorId(local, out var hardcoverId))
            {
                return new TalearrShadowAuthorReport
                {
                    AuthorId = local.Id,
                    AuthorName = local.Name,
                    ServerAuthorId = AuthorIdentity.GetPreferredProviderId(local),
                    Status = "skipped",
                    Message = "no Hardcover author id",
                    ComparedAt = now
                };
            }

            var serverId = AuthorIdentity.GetPreferredProviderId(local);
            List<Book> localBooks;
            HashSet<int> bookIdsWithFiles;
            try
            {
                (localBooks, bookIdsWithFiles) = LoadLocal(local);
            }
            catch (Exception ex)
            {
                _logger.Warn(ex, "[TALEARR-SHADOW] Unable to read local books of {0}", local.Name);
                return new TalearrShadowAuthorReport
                {
                    AuthorId = local.Id,
                    AuthorName = local.Name,
                    ServerAuthorId = serverId,
                    HardcoverAuthorId = $"hc:{hardcoverId}",
                    Status = "error",
                    Message = $"local books: {ex.Message}",
                    ComparedAt = now
                };
            }

            var server = new TalearrMetadataShadowComparer.Side();
            try
            {
                server.Remote = string.IsNullOrWhiteSpace(serverId) ? null : _authorInfo.GetAuthorInfo(serverId, true);
                server.Error = server.Remote == null ? "no author returned" : null;
            }
            catch (Exception ex)
            {
                server.Error = ex.Message;
                _logger.Debug(ex, "[TALEARR-SHADOW] Metadata server read failed for {0}", local.Name);
            }

            var hardcover = new TalearrMetadataShadowComparer.Side();
            var requestsBefore = _hardcover.GetStatus().RequestsSinceStart;
            try
            {
                var catalog = _hardcover.GetAuthorCatalog(hardcoverId);
                if (catalog == null)
                {
                    hardcover.Error = $"Hardcover has no author hc:{hardcoverId}";
                }
                else
                {
                    var response = TalearrHardcoverMapper.BuildAuthorResponse(catalog);
                    hardcover.SeriesFromSource = response.Series.Count;
                    hardcover.Remote = _converter.ConvertV5AuthorResponse(response);
                }
            }
            catch (Exception ex)
            {
                hardcover.Error = ex.Message;
                _logger.Debug(ex, "[TALEARR-SHADOW] Hardcover read failed for {0}", local.Name);
            }

            var report = TalearrMetadataShadowComparer.Compare(local, localBooks, bookIdsWithFiles, server, hardcover, Normalize, now);
            report.ServerAuthorId = serverId;
            report.HardcoverAuthorId = $"hc:{hardcoverId}";
            report.HardcoverRequests = Math.Max(0, _hardcover.GetStatus().RequestsSinceStart - requestsBefore);
            return report;
        }

        private (List<Book> Books, HashSet<int> WithFiles) LoadLocal(Author local)
        {
            var books = _bookService.GetBooksByAuthor(local.Id) ?? new List<Book>();
            var editions = _editionService.GetEditionsByAuthor(local.Id) ?? new List<Edition>();
            var editionsByBook = editions.GroupBy(e => e.BookId).ToDictionary(g => g.Key, g => g.ToList());

            foreach (var book in books)
            {
                // In memory only: attach the editions so edition ids take part in matching.
                book.Editions = editionsByBook.TryGetValue(book.Id, out var list) ? list : new List<Edition>();
            }

            var bookByEdition = editions.ToDictionary(e => e.Id, e => e.BookId);
            var withFiles = new HashSet<int>();
            foreach (var file in _mediaFileService.GetFilesByAuthor(local.Id) ?? new List<BookFile>())
            {
                if (bookByEdition.TryGetValue(file.EditionId, out var bookId))
                {
                    withFiles.Add(bookId);
                }
            }

            return (books, withFiles);
        }

        private List<Book> Normalize(Author local, List<Book> remoteBooks, List<Series> remoteSeries)
        {
            // Same call refresh makes in GetRemoteChildren (which also passes no edition selector,
            // so the default EditionSelector is used).
            return RefreshAuthorService.NormalizeRemoteBooks(
                local,
                remoteBooks,
                remoteSeries,
                _metadataProfileService,
                _importListExclusionService,
                _logger,
                audiobookMetadataProfileIdOverride: null,
                ebookMetadataProfileIdOverride: null,
                editionSelector: new EditionSelector(_logger),
                retainEditions: true,
                logContext: "TalearrShadow");
        }
    }
}
