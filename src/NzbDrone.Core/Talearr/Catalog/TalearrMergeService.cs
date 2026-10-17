using System;
using System.Collections.Generic;
using System.Data;
using System.Linq;
using System.Text.Json;
using Dapper;
using NLog;
using NzbDrone.Common.Extensions;
using NzbDrone.Core.Books;
using NzbDrone.Core.Books.Events;
using NzbDrone.Core.Datastore;
using NzbDrone.Core.MetadataSource;
using NzbDrone.Core.Messaging.Events;
using NzbDrone.Core.Parser;

namespace NzbDrone.Core.Talearr.Catalog
{
    public class TalearrMergeRequest
    {
        public int TargetId { get; set; }
        public List<int> SourceIds { get; set; } = new List<int>();

        // Dry run: every statement runs inside the transaction, the counts are reported, then the
        // transaction is rolled back. The preview is exactly what a real merge would do.
        public bool DryRun { get; set; }

        // Skips the "these look like different works/authors" check. Never skips the media type
        // check or the same-author check for books.
        public bool Force { get; set; }
    }

    public sealed class TalearrMergeReport
    {
        public string Kind { get; set; }
        public bool DryRun { get; set; }
        public int TargetId { get; set; }
        public string TargetName { get; set; }
        public List<int> MergedIds { get; set; } = new List<int>();

        // Rows moved or removed, per table/step ("books", "editions", "bookFiles", "history", ...).
        public Dictionary<string, int> Changes { get; set; } = new Dictionary<string, int>(StringComparer.Ordinal);
        public List<string> Notes { get; set; } = new List<string>();
    }

    public class TalearrMergeException : Exception
    {
        public TalearrMergeException(string message)
            : base(message)
        {
        }
    }

    public interface ITalearrMergeService
    {
        TalearrMergeReport MergeAuthors(TalearrMergeRequest request);
        TalearrMergeReport MergeBooks(TalearrMergeRequest request);
    }

    // TaleArr: merge duplicate authors or duplicate book rows. Only foreign keys in existing tables
    // are reassigned (no schema change), everything runs in one transaction, and files on disk are
    // never touched: book file rows keep their paths and only change the edition/author they hang
    // off. The merged-away rows are deleted directly, not through DeleteBook/DeleteAuthor, so no
    // delete-files handler can ever run.
    public class TalearrMergeService : ITalearrMergeService
    {
        private const string AuthorEntity = "Author";
        private const string BookEntity = "Book";

        private readonly IMainDatabase _database;
        private readonly IEventAggregator _eventAggregator;
        private readonly ITalearrMergeRedirectStore _redirects;
        private readonly Logger _logger;
        private readonly object _mergeLock = new object();

        public TalearrMergeService(IMainDatabase database, IEventAggregator eventAggregator, Logger logger, ITalearrMergeRedirectStore redirects = null)
        {
            _database = database;
            _eventAggregator = eventAggregator;
            _redirects = redirects;
            _logger = logger;
        }

        // Test hook: runs right before commit, inside the transaction.
        internal Action<IDbConnection, IDbTransaction> BeforeCommit { get; set; }

        public TalearrMergeReport MergeAuthors(TalearrMergeRequest request)
        {
            var sourceIds = ValidateIds(request, "author");

            lock (_mergeLock)
            {
                var report = new TalearrMergeReport { Kind = "authors", DryRun = request.DryRun, TargetId = request.TargetId };
                var removedBooks = new List<Book>();
                var redirects = new List<(int SourceId, List<TalearrMergeRedirect> Ids)>();
                AuthorRow target = null;

                Run(report, (conn, tran, tables) =>
                {
                    target = GetAuthor(conn, tran, request.TargetId) ?? throw new TalearrMergeException($"Author {request.TargetId} does not exist");
                    report.TargetName = target.Name;

                    var sources = sourceIds.Select(id => GetAuthor(conn, tran, id) ?? throw new TalearrMergeException($"Author {id} does not exist")).ToList();

                    foreach (var source in sources)
                    {
                        if (!request.Force && !LooksLikeSameAuthor(target, source))
                        {
                            throw new TalearrMergeException(
                                $"'{source.Name}' ({source.Id}) and '{target.Name}' ({target.Id}) have different names and no provider id in common. " +
                                "Send force=true if they really are the same author.");
                        }
                    }

                    foreach (var source in sources)
                    {
                        removedBooks.AddRange(conn.Query<int>(@"SELECT ""Id"" FROM ""Books"" WHERE ""AuthorId"" = @Id", new { source.Id }, tran)
                            .Select(id => new Book { Id = id, AuthorId = target.Id }));
                        redirects.Add((source.Id, AuthorRedirects(target, source)));
                        MergeAuthor(conn, tran, tables, target, source, report);
                        report.MergedIds.Add(source.Id);
                    }
                });

                if (!request.DryRun)
                {
                    _logger.Info("TaleArr: merged authors {0} into {1} '{2}' ({3})",
                        string.Join(", ", report.MergedIds), target.Id, target.Name, Describe(report));
                    RecordRedirects(TalearrMergeRedirect.Author, target.Id, redirects);
                    PublishAfterMerge(target.Id, removedBooks, new List<Book>());
                }

                return report;
            }
        }

        public TalearrMergeReport MergeBooks(TalearrMergeRequest request)
        {
            var sourceIds = ValidateIds(request, "book");

            lock (_mergeLock)
            {
                var report = new TalearrMergeReport { Kind = "books", DryRun = request.DryRun, TargetId = request.TargetId };
                BookRow target = null;
                var removed = new List<Book>();
                var redirects = new List<(int SourceId, List<TalearrMergeRedirect> Ids)>();

                Run(report, (conn, tran, tables) =>
                {
                    target = GetBook(conn, tran, request.TargetId) ?? throw new TalearrMergeException($"Book {request.TargetId} does not exist");
                    report.TargetName = target.Title;

                    var sources = sourceIds.Select(id => GetBook(conn, tran, id) ?? throw new TalearrMergeException($"Book {id} does not exist")).ToList();

                    foreach (var source in sources)
                    {
                        ValidateBookPair(target, source, request.Force);
                    }

                    foreach (var source in sources)
                    {
                        redirects.Add((source.Id, BookRedirects(target, source)));
                        MergeBook(conn, tran, tables, target, source, report);
                        report.MergedIds.Add(source.Id);
                        removed.Add(new Book { Id = source.Id, AuthorId = source.AuthorId, MediaType = (BookMediaType)source.MediaType });
                    }
                });

                if (!request.DryRun)
                {
                    _logger.Info("TaleArr: merged books {0} into {1} '{2}' ({3})",
                        string.Join(", ", report.MergedIds), target.Id, target.Title, Describe(report));
                    RecordRedirects(TalearrMergeRedirect.Book, target.Id, redirects);
                    PublishAfterMerge(target.AuthorId, removed, new List<Book> { new Book { Id = target.Id, AuthorId = target.AuthorId } });
                }

                return report;
            }
        }

        // ---- redirects ----

        // The source's provider ids the target does not have. A later refresh or add that still
        // sees one of them resolves to the target (TalearrMergeRedirectStore).
        internal static List<TalearrMergeRedirect> AuthorRedirects(AuthorRow target, AuthorRow source)
        {
            var own = new HashSet<string>(target.ProviderIds().Select(id => TalearrMergeRedirectStore.Canonical(id)).Where(id => id != null), StringComparer.OrdinalIgnoreCase);

            return source.ProviderIds()
                .Select(id => TalearrMergeRedirectStore.Canonical(id))
                .Where(id => id != null && !own.Contains(id))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Select(id => new TalearrMergeRedirect { EntityType = TalearrMergeRedirect.Author, ProviderId = id, Title = source.Name })
                .ToList();
        }

        internal static List<TalearrMergeRedirect> BookRedirects(BookRow target, BookRow source)
        {
            var result = new List<TalearrMergeRedirect>();
            var targetBook = target.AsBook();
            var sourceBook = source.AsBook();

            var ownWork = BookIdentity.GetStableWorkProviderIdentityTokens(targetBook);
            foreach (var token in BookIdentity.GetStableWorkProviderIdentityTokens(sourceBook).Where(t => !ownWork.Contains(t)))
            {
                result.Add(new TalearrMergeRedirect { EntityType = TalearrMergeRedirect.Book, MediaType = source.MediaType, ProviderId = token, Title = source.Title });
            }

            var ownEdition = BookIdentity.GetEditionProviderIdentityTokens(targetBook);
            foreach (var token in BookIdentity.GetEditionProviderIdentityTokens(sourceBook).Where(t => !ownEdition.Contains(t)))
            {
                result.Add(new TalearrMergeRedirect { EntityType = TalearrMergeRedirect.Edition, MediaType = source.MediaType, ProviderId = token, Title = source.Title });
            }

            return result;
        }

        private void RecordRedirects(string entityType, int targetId, List<(int SourceId, List<TalearrMergeRedirect> Ids)> redirects)
        {
            if (_redirects == null)
            {
                return;
            }

            try
            {
                foreach (var (sourceId, ids) in redirects)
                {
                    _redirects.Record(entityType, sourceId, targetId, ids);
                }
            }
            catch (Exception ex)
            {
                // The merge is committed; without the redirect a refresh may add the duplicate again.
                _logger.Warn(ex, "TaleArr: merge committed but its redirects could not be recorded");
            }
        }

        // ---- validation ----

        private static List<int> ValidateIds(TalearrMergeRequest request, string kind)
        {
            if (request == null || request.TargetId <= 0)
            {
                throw new TalearrMergeException($"targetId must be a {kind} id");
            }

            var sourceIds = (request.SourceIds ?? new List<int>()).Where(id => id > 0).Distinct().ToList();
            if (sourceIds.Contains(request.TargetId))
            {
                throw new TalearrMergeException($"A {kind} cannot be merged into itself");
            }

            if (sourceIds.Count == 0)
            {
                throw new TalearrMergeException($"sourceIds must list at least one other {kind} id");
            }

            return sourceIds;
        }

        internal static bool LooksLikeSameAuthor(AuthorRow target, AuthorRow source)
        {
            var targetName = TalearrDuplicateFinder.NormalizeAuthorName(target.Name);
            if (targetName.Length > 0 && targetName == TalearrDuplicateFinder.NormalizeAuthorName(source.Name))
            {
                return true;
            }

            return target.ProviderIds().Intersect(source.ProviderIds(), StringComparer.OrdinalIgnoreCase).Any();
        }

        internal static void ValidateBookPair(BookRow target, BookRow source, bool force)
        {
            if (source.MediaType != target.MediaType)
            {
                throw new TalearrMergeException(
                    $"'{source.Title}' ({source.Id}) is an {MediaTypeName(source.MediaType)} row and '{target.Title}' ({target.Id}) is an {MediaTypeName(target.MediaType)} row. " +
                    "Ebook and audiobook rows of one work are siblings, not duplicates, and are never merged.");
            }

            if (source.AuthorId != target.AuthorId)
            {
                throw new TalearrMergeException(
                    $"'{source.Title}' ({source.Id}) belongs to a different author than '{target.Title}' ({target.Id}). Merge the authors first.");
            }

            if (force)
            {
                return;
            }

            if (source.UnitKeyHash.IsNotNullOrWhiteSpace() && !string.Equals(source.UnitKeyHash, target.UnitKeyHash, StringComparison.Ordinal))
            {
                throw new TalearrMergeException(
                    $"'{source.Title}' ({source.Id}) is a separate recording copy (its own narrator or production). Send force=true to merge it anyway.");
            }

            var sameTitle = TalearrTitleNormalizer.IsSameWorkTitle(source.Title, target.Title);
            var sharedWorkId = source.WorkTokens().Overlaps(target.WorkTokens());
            if (!sameTitle && !sharedWorkId)
            {
                throw new TalearrMergeException(
                    $"'{source.Title}' ({source.Id}) and '{target.Title}' ({target.Id}) look like different works (different titles, no work id in common). " +
                    "Send force=true if they really are the same book.");
            }
        }

        private static string MediaTypeName(int mediaType)
        {
            return mediaType == (int)BookMediaType.Ebook ? "ebook" : "audiobook";
        }

        // ---- transaction ----

        private void Run(TalearrMergeReport report, Action<IDbConnection, IDbTransaction, HashSet<string>> work)
        {
            using var conn = _database.OpenConnection();
            var tables = GetTables(conn);
            using var tran = conn.BeginTransaction();

            try
            {
                work(conn, tran, tables);
                BeforeCommit?.Invoke(conn, tran);

                if (report.DryRun)
                {
                    tran.Rollback();
                }
                else
                {
                    tran.Commit();
                }
            }
            catch
            {
                tran.Rollback();
                throw;
            }
        }

        private HashSet<string> GetTables(IDbConnection conn)
        {
            var sql = _database.DatabaseType == DatabaseType.PostgreSQL
                ? "SELECT table_name FROM information_schema.tables WHERE table_schema = current_schema()"
                : "SELECT name FROM sqlite_master WHERE type = 'table'";

            return conn.Query<string>(sql).ToHashSet(StringComparer.OrdinalIgnoreCase);
        }

        private static int Step(TalearrMergeReport report, string key, IDbConnection conn, IDbTransaction tran, string sql, object param)
        {
            var count = conn.Execute(sql, param, tran);
            Count(report, key, count);
            return count;
        }

        private static void Count(TalearrMergeReport report, string key, int count)
        {
            if (count <= 0)
            {
                return;
            }

            report.Changes.TryGetValue(key, out var existing);
            report.Changes[key] = existing + count;
        }

        private static string Describe(TalearrMergeReport report)
        {
            return string.Join(", ", report.Changes.Select(kv => $"{kv.Key}={kv.Value}"));
        }

        private void PublishAfterMerge(int authorId, List<Book> removed, List<Book> updated)
        {
            try
            {
                _eventAggregator.PublishEvent(new CachesClearedEvent("TaleArr merge"));

                var author = new Author { Id = authorId };

                // Tracked downloads that pointed at a merged-away book re-map themselves; statistics
                // caches for the target author are dropped.
                _eventAggregator.PublishEvent(new BookInfoRefreshedEvent(author, new List<Book>(), updated, removed));
                _eventAggregator.PublishEvent(new AuthorUpdatedEvent(author));
            }
            catch (Exception ex)
            {
                // The merge is committed; a failed notification only leaves caches stale for a while.
                _logger.Warn(ex, "TaleArr: merge committed but cache refresh events failed");
            }
        }

        // ---- authors ----

        private void MergeAuthor(IDbConnection conn, IDbTransaction tran, HashSet<string> tables, AuthorRow target, AuthorRow source, TalearrMergeReport report)
        {
            var p = new { Target = target.Id, Source = source.Id };

            // Rename the source to the target's name first: on SQLite the edition FTS triggers then
            // re-index the source's editions under the target author's name before they move.
            if (!string.Equals(source.Name, target.Name, StringComparison.Ordinal))
            {
                conn.Execute(@"UPDATE ""Authors"" SET ""Name"" = @Name WHERE ""Id"" = @Source", new { target.Name, Source = source.Id }, tran);
            }

            Step(report, "books", conn, tran, @"UPDATE ""Books"" SET ""AuthorId"" = @Target WHERE ""AuthorId"" = @Source", p);
            Step(report, "history", conn, tran, @"UPDATE ""History"" SET ""AuthorId"" = @Target WHERE ""AuthorId"" = @Source", p);
            Step(report, "downloadHistory", conn, tran, @"UPDATE ""DownloadHistory"" SET ""AuthorId"" = @Target WHERE ""AuthorId"" = @Source", p);
            Step(report, "pendingReleases", conn, tran, @"UPDATE ""PendingReleases"" SET ""AuthorId"" = @Target WHERE ""AuthorId"" = @Source", p);
            Step(report, "metadataFiles", conn, tran, @"UPDATE ""MetadataFiles"" SET ""AuthorId"" = @Target WHERE ""AuthorId"" = @Source", p);
            Step(report, "extraFiles", conn, tran, @"UPDATE ""ExtraFiles"" SET ""AuthorId"" = @Target WHERE ""AuthorId"" = @Source", p);
            OptionalStep(report, tables, "OtherFiles", "otherFiles", conn, tran, @"UPDATE ""OtherFiles"" SET ""AuthorId"" = @Target WHERE ""AuthorId"" = @Source", p);
            OptionalStep(report, tables, "Blacklist", "legacyBlacklist", conn, tran, @"UPDATE ""Blacklist"" SET ""AuthorId"" = @Target WHERE ""AuthorId"" = @Source", p);
            OptionalStep(report, tables, "PendingImport", "pendingImports", conn, tran, @"UPDATE ""PendingImport"" SET ""AuthorId"" = @Target WHERE ""AuthorId"" = @Source", p);

            if (tables.Contains("AuthorSeries"))
            {
                conn.Execute(@"DELETE FROM ""AuthorSeries"" WHERE ""AuthorId"" = @Source AND ""SeriesId"" IN (SELECT ""SeriesId"" FROM ""AuthorSeries"" WHERE ""AuthorId"" = @Target)", p, tran);
                Step(report, "series", conn, tran, @"UPDATE ""AuthorSeries"" SET ""AuthorId"" = @Target WHERE ""AuthorId"" = @Source", p);
            }

            MoveBlocklist(conn, tran, report, entry => entry.AuthorIds, source.ProviderIds(), target.ProviderIds());

            // Derived rows of the removed author: rebuilt from the Authors table, never moved.
            OptionalStep(report, tables, "AuthorSyncMetadata", null, conn, tran, @"DELETE FROM ""AuthorSyncMetadata"" WHERE ""AuthorId"" = @Source", p);
            OptionalStep(report, tables, "author_trigrams", null, conn, tran, @"DELETE FROM ""author_trigrams"" WHERE ""AuthorId"" = @Source", p);
            OptionalStep(report, tables, "author_normalized", null, conn, tran, @"DELETE FROM ""author_normalized"" WHERE ""AuthorId"" = @Source", p);
            OptionalStep(report, tables, "ProviderAliasIndex", null, conn, tran, @"DELETE FROM ""ProviderAliasIndex"" WHERE ""EntityType"" = @Type AND ""EntityId"" = @Source", new { Source = source.Id, Type = AuthorEntity });

            // The target keeps its own ids and settings; it only inherits folders it does not have
            // yet (the merged books' files still live there) and monitoring is OR-ed.
            target.AudiobookPath = FirstNonBlank(target.AudiobookPath, source.AudiobookPath);
            target.EbookPath = FirstNonBlank(target.EbookPath, source.EbookPath);
            target.AudiobookRootFolderPath = FirstNonBlank(target.AudiobookRootFolderPath, source.AudiobookRootFolderPath);
            target.EbookRootFolderPath = FirstNonBlank(target.EbookRootFolderPath, source.EbookRootFolderPath);
            target.AudiobookMonitored = Or(target.AudiobookMonitored, source.AudiobookMonitored);
            target.EbookMonitored = Or(target.EbookMonitored, source.EbookMonitored);
            target.Monitored = target.Monitored || source.Monitored;

            conn.Execute(@"UPDATE ""Authors""
                           SET ""AudiobookPath"" = @AudiobookPath,
                               ""EbookPath"" = @EbookPath,
                               ""AudiobookRootFolderPath"" = @AudiobookRootFolderPath,
                               ""EbookRootFolderPath"" = @EbookRootFolderPath,
                               ""AudiobookMonitored"" = @AudiobookMonitored,
                               ""EbookMonitored"" = @EbookMonitored,
                               ""Monitored"" = @Monitored
                           WHERE ""Id"" = @Id", target, tran);

            if (source.Path.IsNotNullOrWhiteSpace() && !string.Equals(source.Path, target.Path, StringComparison.Ordinal))
            {
                report.Notes.Add($"Files of '{source.Name}' stay in {source.Path}; nothing on disk was moved or deleted.");
            }

            Step(report, "authorsRemoved", conn, tran, @"DELETE FROM ""Authors"" WHERE ""Id"" = @Source", p);
        }

        private static bool? Or(bool? left, bool? right)
        {
            if (left == true || right == true)
            {
                return true;
            }

            return left ?? right;
        }

        private static string FirstNonBlank(string left, string right)
        {
            return left.IsNotNullOrWhiteSpace() ? left : right;
        }

        // ---- books ----

        private void MergeBook(IDbConnection conn, IDbTransaction tran, HashSet<string> tables, BookRow target, BookRow source, TalearrMergeReport report)
        {
            var p = new { Target = target.Id, Source = source.Id };

            var targetEditions = GetEditions(conn, tran, target.Id);
            var sourceEditions = GetEditions(conn, tran, source.Id);
            var targetHadMonitored = targetEditions.Any(e => e.Monitored);

            foreach (var edition in sourceEditions)
            {
                var twin = edition.ForeignEditionId.IsNullOrWhiteSpace()
                    ? null
                    : targetEditions.FirstOrDefault(t => string.Equals(t.ForeignEditionId, edition.ForeignEditionId, StringComparison.OrdinalIgnoreCase));

                if (twin == null)
                {
                    Step(report, "editions", conn, tran, @"UPDATE ""Editions"" SET ""BookId"" = @Target WHERE ""Id"" = @Id", new { Target = target.Id, edition.Id });
                    continue;
                }

                // The same edition exists on both rows: fold the source copy into the target's.
                var e = new { From = edition.Id, To = twin.Id };
                Step(report, "bookFiles", conn, tran, @"UPDATE ""BookFiles"" SET ""EditionId"" = @To WHERE ""EditionId"" = @From", e);
                Step(report, "history", conn, tran, @"UPDATE ""History"" SET ""EditionId"" = @To WHERE ""EditionId"" = @From", e);
                OptionalStep(report, tables, "OtherFiles", "otherFiles", conn, tran, @"UPDATE ""OtherFiles"" SET ""EditionId"" = @To WHERE ""EditionId"" = @From", e);
                conn.Execute(@"DELETE FROM ""EditionNarratorLink"" WHERE ""EditionId"" = @From AND ""NarratorId"" IN (SELECT ""NarratorId"" FROM ""EditionNarratorLink"" WHERE ""EditionId"" = @To)", e, tran);
                conn.Execute(@"UPDATE ""EditionNarratorLink"" SET ""EditionId"" = @To WHERE ""EditionId"" = @From", e, tran);
                OptionalStep(report, tables, "narrator_normalized", null, conn, tran, @"UPDATE ""narrator_normalized"" SET ""EditionId"" = @To WHERE ""EditionId"" = @From", e);
                Step(report, "editionsFolded", conn, tran, @"DELETE FROM ""Editions"" WHERE ""Id"" = @From", e);
            }

            // Book files hang off editions, so they moved with them; count them for the report.
            if (sourceEditions.Count > 0)
            {
                var moved = sourceEditions.Where(se => !targetEditions.Any(t => se.ForeignEditionId.IsNotNullOrWhiteSpace() && string.Equals(t.ForeignEditionId, se.ForeignEditionId, StringComparison.OrdinalIgnoreCase)))
                    .Select(se => se.Id)
                    .ToList();
                foreach (var editionId in moved)
                {
                    Count(report, "bookFiles", conn.ExecuteScalar<int>(@"SELECT COUNT(*) FROM ""BookFiles"" WHERE ""EditionId"" = @Id", new { Id = editionId }, tran));
                }
            }

            KeepOneMonitoredEdition(conn, tran, target.Id, targetHadMonitored ? targetEditions.Where(t => t.Monitored).Select(t => t.Id).ToList() : null);

            Step(report, "history", conn, tran, @"UPDATE ""History"" SET ""BookId"" = @Target WHERE ""BookId"" = @Source", p);
            Step(report, "downloadHistory", conn, tran, @"UPDATE ""DownloadHistory"" SET ""BookId"" = @Target WHERE ""BookId"" = @Source", p);
            Step(report, "metadataFiles", conn, tran, @"UPDATE ""MetadataFiles"" SET ""BookId"" = @Target WHERE ""BookId"" = @Source", p);
            Step(report, "extraFiles", conn, tran, @"UPDATE ""ExtraFiles"" SET ""BookId"" = @Target WHERE ""BookId"" = @Source", p);
            OptionalStep(report, tables, "ABSLookupHistory", null, conn, tran, @"UPDATE ""ABSLookupHistory"" SET ""BookId"" = @Target WHERE ""BookId"" = @Source", p);

            conn.Execute(@"DELETE FROM ""SeriesBookLink""
                           WHERE ""BookId"" = @Source
                             AND EXISTS (SELECT 1 FROM ""SeriesBookLink"" t
                                         WHERE t.""BookId"" = @Target
                                           AND t.""SeriesId"" = ""SeriesBookLink"".""SeriesId""
                                           AND t.""SeriesInstanceType"" = ""SeriesBookLink"".""SeriesInstanceType"")", p, tran);
            Step(report, "seriesLinks", conn, tran, @"UPDATE ""SeriesBookLink"" SET ""BookId"" = @Target WHERE ""BookId"" = @Source", p);

            conn.Execute(@"DELETE FROM ""BookNarratorLink"" WHERE ""BookId"" = @Source AND ""NarratorId"" IN (SELECT ""NarratorId"" FROM ""BookNarratorLink"" WHERE ""BookId"" = @Target)", p, tran);
            Step(report, "narratorLinks", conn, tran, @"UPDATE ""BookNarratorLink"" SET ""BookId"" = @Target WHERE ""BookId"" = @Source", p);

            MoveBlocklist(conn, tran, report, entry => entry.BookIds, source.WorkTokens(), target.WorkTokens());

            OptionalStep(report, tables, "book_trigrams", null, conn, tran, @"DELETE FROM ""book_trigrams"" WHERE ""BookId"" = @Source", p);
            OptionalStep(report, tables, "book_normalized", null, conn, tran, @"DELETE FROM ""book_normalized"" WHERE ""BookId"" = @Source", p);
            OptionalStep(report, tables, "ProviderAliasIndex", null, conn, tran, @"DELETE FROM ""ProviderAliasIndex"" WHERE ""EntityType"" = @Type AND ""EntityId"" = @Source", new { Source = source.Id, Type = BookEntity });

            // Monitoring is OR-ed; every other column (foreign ids, title, dates) stays the target's.
            target.AudiobookMonitored = target.AudiobookMonitored || source.AudiobookMonitored;
            target.EbookMonitored = target.EbookMonitored || source.EbookMonitored;
            conn.Execute(@"UPDATE ""Books"" SET ""AudiobookMonitored"" = @AudiobookMonitored, ""EbookMonitored"" = @EbookMonitored WHERE ""Id"" = @Id", target, tran);

            Step(report, "booksRemoved", conn, tran, @"DELETE FROM ""Books"" WHERE ""Id"" = @Source", p);
        }

        // One monitored edition per book (EditionSelector's invariant): the target's own choice
        // wins; otherwise the merged edition with the most files, then the lowest id.
        private static void KeepOneMonitoredEdition(IDbConnection conn, IDbTransaction tran, int bookId, List<int> preferred)
        {
            var monitored = conn.Query<(int Id, int Files)>(
                    @"SELECT e.""Id"", (SELECT COUNT(*) FROM ""BookFiles"" f WHERE f.""EditionId"" = e.""Id"") AS ""Files""
                      FROM ""Editions"" e
                      WHERE e.""BookId"" = @BookId AND e.""Monitored"" = @True",
                    new { BookId = bookId, True = true },
                    tran)
                .ToList();

            if (monitored.Count <= 1)
            {
                return;
            }

            var keep = preferred?.FirstOrDefault(id => monitored.Any(m => m.Id == id)) ?? 0;
            if (keep == 0)
            {
                keep = monitored.OrderByDescending(m => m.Files).ThenBy(m => m.Id).First().Id;
            }

            conn.Execute(@"UPDATE ""Editions"" SET ""Monitored"" = @False WHERE ""BookId"" = @BookId AND ""Monitored"" = @True AND ""Id"" <> @Keep",
                new { BookId = bookId, Keep = keep, True = true, False = false },
                tran);
        }

        // ---- shared ----

        private static void OptionalStep(TalearrMergeReport report, HashSet<string> tables, string table, string key, IDbConnection conn, IDbTransaction tran, string sql, object param)
        {
            if (!tables.Contains(table))
            {
                return;
            }

            var count = conn.Execute(sql, param, tran);
            if (key != null)
            {
                Count(report, key, count);
            }
        }

        private sealed class BlocklistEntry
        {
            public int Id { get; set; }
            public List<string> AuthorIds { get; set; }
            public List<string> BookIds { get; set; }
            public string AuthorJson { get; set; }
            public string BookJson { get; set; }
        }

        // Blocklist rows match by provider ids, not local ids: rows that matched the merged-away
        // entity also get the target's ids so they keep blocking the same releases.
        private static void MoveBlocklist(IDbConnection conn, IDbTransaction tran, TalearrMergeReport report, Func<BlocklistEntry, List<string>> select, ICollection<string> sourceIds, ICollection<string> targetIds)
        {
            if (sourceIds.Count == 0 || targetIds.Count == 0)
            {
                return;
            }

            var rows = conn.Query<(int Id, string AuthorProviderIds, string BookProviderIds)>(
                @"SELECT ""Id"", ""AuthorProviderIds"", ""BookProviderIds"" FROM ""Blocklist""", transaction: tran);

            foreach (var row in rows)
            {
                var entry = new BlocklistEntry
                {
                    Id = row.Id,
                    AuthorIds = ParseIds(row.AuthorProviderIds),
                    BookIds = ParseIds(row.BookProviderIds)
                };

                var ids = select(entry);
                if (!ids.Intersect(sourceIds, StringComparer.OrdinalIgnoreCase).Any())
                {
                    continue;
                }

                var missing = targetIds.Where(t => !ids.Contains(t, StringComparer.OrdinalIgnoreCase)).ToList();
                if (missing.Count == 0)
                {
                    continue;
                }

                ids.AddRange(missing);
                conn.Execute(@"UPDATE ""Blocklist"" SET ""AuthorProviderIds"" = @AuthorIds, ""BookProviderIds"" = @BookIds WHERE ""Id"" = @Id",
                    new { entry.Id, AuthorIds = JsonSerializer.Serialize(entry.AuthorIds), BookIds = JsonSerializer.Serialize(entry.BookIds) },
                    tran);
                Count(report, "blocklist", 1);
            }
        }

        private static List<string> ParseIds(string json)
        {
            if (json.IsNullOrWhiteSpace())
            {
                return new List<string>();
            }

            try
            {
                return JsonSerializer.Deserialize<List<string>>(json) ?? new List<string>();
            }
            catch (JsonException)
            {
                return new List<string>();
            }
        }

        private static AuthorRow GetAuthor(IDbConnection conn, IDbTransaction tran, int id)
        {
            return conn.Query<AuthorRow>(
                @"SELECT ""Id"", ""Name"", ""Path"", ""AudiobookPath"", ""EbookPath"", ""AudiobookRootFolderPath"", ""EbookRootFolderPath"",
                         ""Monitored"", ""AudiobookMonitored"", ""EbookMonitored"",
                         ""GoodreadsAuthorId"", ""HardcoverAuthorId"", ""AudnexusAuthorId"", ""OpenLibraryAuthorId"", ""GoogleBooksAuthorId""
                  FROM ""Authors"" WHERE ""Id"" = @Id",
                new { Id = id },
                tran).SingleOrDefault();
        }

        private static BookRow GetBook(IDbConnection conn, IDbTransaction tran, int id)
        {
            return conn.Query<BookRow>(
                @"SELECT ""Id"", ""AuthorId"", ""Title"", ""MediaType"", ""AudiobookMonitored"", ""EbookMonitored"", ""UnitKeyHash"",
                         ""HardcoverBookId"", ""GoodreadsWorkId"", ""OpenLibraryWorkId"",
                         ""GoodreadsBookId"", ""OpenLibraryEditionId"", ""GoogleBooksId"", ""ASIN"", ""AudibleASIN""
                  FROM ""Books"" WHERE ""Id"" = @Id",
                new { Id = id },
                tran).SingleOrDefault();
        }

        private static List<EditionRow> GetEditions(IDbConnection conn, IDbTransaction tran, int bookId)
        {
            return conn.Query<EditionRow>(
                @"SELECT ""Id"", ""ForeignEditionId"", ""Monitored"" FROM ""Editions"" WHERE ""BookId"" = @BookId ORDER BY ""Id""",
                new { BookId = bookId },
                tran).ToList();
        }

        internal sealed class EditionRow
        {
            public int Id { get; set; }
            public string ForeignEditionId { get; set; }
            public bool Monitored { get; set; }
        }

        internal sealed class AuthorRow
        {
            public int Id { get; set; }
            public string Name { get; set; }
            public string Path { get; set; }
            public string AudiobookPath { get; set; }
            public string EbookPath { get; set; }
            public string AudiobookRootFolderPath { get; set; }
            public string EbookRootFolderPath { get; set; }
            public bool Monitored { get; set; }
            public bool? AudiobookMonitored { get; set; }
            public bool? EbookMonitored { get; set; }
            public string GoodreadsAuthorId { get; set; }
            public string HardcoverAuthorId { get; set; }
            public string AudnexusAuthorId { get; set; }
            public string OpenLibraryAuthorId { get; set; }
            public string GoogleBooksAuthorId { get; set; }

            // Same list (and format) BlocklistRepository matches on.
            public List<string> ProviderIds()
            {
                return new[] { GoodreadsAuthorId, HardcoverAuthorId, OpenLibraryAuthorId, AudnexusAuthorId, GoogleBooksAuthorId }
                    .Where(id => id.IsNotNullOrWhiteSpace())
                    .Select(id => id.Trim())
                    .ToList();
            }
        }

        internal sealed class BookRow
        {
            public int Id { get; set; }
            public int AuthorId { get; set; }
            public string Title { get; set; }
            public int MediaType { get; set; }
            public bool AudiobookMonitored { get; set; }
            public bool EbookMonitored { get; set; }
            public string UnitKeyHash { get; set; }
            public string HardcoverBookId { get; set; }
            public string GoodreadsWorkId { get; set; }
            public string OpenLibraryWorkId { get; set; }
            public string GoodreadsBookId { get; set; }
            public string OpenLibraryEditionId { get; set; }
            public string GoogleBooksId { get; set; }
            public string ASIN { get; set; }
            public string AudibleASIN { get; set; }

            // The ids the refresh matches on (BookIdentity), as an unsaved Book.
            public Book AsBook()
            {
                return new Book
                {
                    MediaType = (BookMediaType)MediaType,
                    HardcoverBookId = HardcoverBookId,
                    GoodreadsWorkId = GoodreadsWorkId,
                    OpenLibraryWorkId = OpenLibraryWorkId,
                    GoodreadsBookId = GoodreadsBookId,
                    OpenLibraryEditionId = OpenLibraryEditionId,
                    GoogleBooksId = GoogleBooksId,
                    ASIN = ASIN,
                    AudibleASIN = AudibleASIN,
                    Editions = new List<Edition>()
                };
            }

            // Work-level ids in the format BlocklistService writes.
            public HashSet<string> WorkTokens()
            {
                var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                if (GoodreadsWorkId.IsNotNullOrWhiteSpace())
                {
                    ids.Add($"grw:{ProviderIdHelper.StripPrefix(GoodreadsWorkId)}");
                }

                if (HardcoverBookId.IsNotNullOrWhiteSpace())
                {
                    ids.Add(ProviderIdHelper.Canonicalize(HardcoverBookId, "hc"));
                }

                if (OpenLibraryWorkId.IsNotNullOrWhiteSpace())
                {
                    ids.Add($"olw:{ProviderIdHelper.StripPrefix(OpenLibraryWorkId)}");
                }

                ids.RemoveWhere(id => id.IsNullOrWhiteSpace());
                return ids;
            }
        }
    }
}
