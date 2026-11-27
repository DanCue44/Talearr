using System;
using System.Collections.Generic;
using System.Linq;
using Dapper;
using NzbDrone.Core.Books;
using NzbDrone.Core.Datastore;
using NzbDrone.Core.Talearr.AudiobookOnly;

namespace NzbDrone.Core.AuthorStats
{
    public interface IAuthorStatisticsRepository
    {
        List<BookStatistics> AuthorStatistics();
        List<BookStatistics> AuthorStatistics(int authorId);
        List<BookStatistics> AuthorStatistics(string mediaType);
        List<BookStatistics> AuthorStatistics(int authorId, string mediaType);
        List<BookStatistics> BookStatistics(IEnumerable<int> bookIds, string mediaType);
    }

    public class AuthorStatisticsRepository : IAuthorStatisticsRepository
    {
        private const string _selectTemplate = "SELECT /**select**/ FROM \"Books\" /**join**/ /**innerjoin**/ /**leftjoin**/ /**where**/ /**groupby**/ /**having**/ /**orderby**/";

        private const string _fileStatisticsJoin = "(" + BookFileStatisticsSql.GroupedByBook +
                                                   @") AS ""FileStatistics"" ON ""FileStatistics"".""BookId"" = ""Books"".""Id""";

        private readonly IMainDatabase _database;
        private readonly ITalearrAudiobookOnlySettingsService _audiobookOnly;

        public AuthorStatisticsRepository(IMainDatabase database, ITalearrAudiobookOnlySettingsService audiobookOnly = null)
        {
            _database = database;
            _audiobookOnly = audiobookOnly;
        }

        // Talearr: Audiobook Only leaves ebook rows and audiobook rows with no known audiobook
        // edition out of every count, unless they have files (TalearrAudiobookOnly).
        private SqlBuilder NewBuilder()
        {
            var builder = Builder(_database.DatabaseType);

            if (TalearrAudiobookOnlySettingsService.Resolve(_audiobookOnly))
            {
                builder.Where(TalearrAudiobookOnly.VisibleSql(@"""Books"""));
            }

            return builder;
        }

        public List<BookStatistics> AuthorStatistics()
        {
            return Query(NewBuilder());
        }

        public List<BookStatistics> AuthorStatistics(int authorId)
        {
            return Query(NewBuilder().Where<Author>(author => author.Id == authorId));
        }

        public List<BookStatistics> AuthorStatistics(string mediaType)
        {
            var builder = NewBuilder();
            ApplyMediaTypeFilter(builder, mediaType);
            return Query(builder);
        }

        public List<BookStatistics> AuthorStatistics(int authorId, string mediaType)
        {
            var builder = NewBuilder().Where<Author>(author => author.Id == authorId);
            ApplyMediaTypeFilter(builder, mediaType);
            return Query(builder);
        }

        // Talearr: the statistics of the given books only, the same rows the whole-library query
        // returns for them. A page of the book list needs its own books, not the whole library.
        public List<BookStatistics> BookStatistics(IEnumerable<int> bookIds, string mediaType)
        {
            var result = new List<BookStatistics>();

            foreach (var batch in bookIds.Distinct().Chunk(SqliteVariableLimit.MaxParameters))
            {
                var batchIds = batch.ToArray();
                var builder = NewBuilder().Where<Book>(book => Enumerable.Contains(batchIds, book.Id));
                ApplyMediaTypeFilter(builder, mediaType);
                result.AddRange(Query(builder));
            }

            return result;
        }

        internal static string BuildBaseSql(DatabaseType databaseType)
        {
            return Builder(databaseType).AddTemplate(_selectTemplate).RawSql;
        }

        private static void ApplyMediaTypeFilter(SqlBuilder builder, string mediaType)
        {
            if (mediaType == "audiobook")
            {
                builder.Where<Book>(book => book.MediaType == BookMediaType.Audiobook);
            }
            else if (mediaType == "ebook")
            {
                builder.Where<Book>(book => book.MediaType == BookMediaType.Ebook);
            }
        }

        private List<BookStatistics> Query(SqlBuilder builder)
        {
            var sql = builder.AddTemplate(_selectTemplate).LogQuery();

            using (var connection = _database.OpenConnection())
            {
                return connection.Query<BookStatistics>(sql.RawSql, sql.Parameters).ToList();
            }
        }

        private static SqlBuilder Builder(DatabaseType databaseType)
        {
            return new SqlBuilder(databaseType)
                .Select(BuildStatisticsSelect(databaseType, includeAuthorId: true))
                .Join<Book, Author>((book, author) => book.AuthorId == author.Id)
                .LeftJoin(_fileStatisticsJoin)
                .AddParameters(new { currentDate = DateTime.UtcNow });
        }

        private static string BuildStatisticsSelect(DatabaseType databaseType, bool includeAuthorId)
        {
            var trueIndicator = databaseType == DatabaseType.PostgreSQL ? "true" : "1";
            var monitoredPredicate = BuildMonitoredPredicate(trueIndicator);
            var effectiveReleaseDate = BuildEffectiveReleaseDate(trueIndicator);
            var authorColumn = includeAuthorId ? @"""Authors"".""Id"" AS ""AuthorId""," : string.Empty;

            return $@"{authorColumn}
                     ""Books"".""Id"" AS ""BookId"",
                     COALESCE(""FileStatistics"".""SizeOnDisk"", 0) AS ""SizeOnDisk"",
                     CASE WHEN {monitoredPredicate} THEN 1 ELSE 0 END AS ""TotalBookCount"",
                     CASE WHEN ({monitoredPredicate}) AND COALESCE(""FileStatistics"".""BookFileCount"", 0) > 0 THEN 1 ELSE 0 END AS ""AvailableBookCount"",
                     CASE WHEN ({monitoredPredicate}) AND
                               ({effectiveReleaseDate} IS NULL OR {effectiveReleaseDate} <= @currentDate OR COALESCE(""FileStatistics"".""BookFileCount"", 0) > 0)
                          THEN 1 ELSE 0 END AS ""BookCount"",
                     COALESCE(""FileStatistics"".""BookFileCount"", 0) AS ""BookFileCount""";
        }

        private static string BuildMonitoredPredicate(string trueIndicator)
        {
            return $@"(""Books"".""MediaType"" = 0 AND ""Books"".""AudiobookMonitored"" = {trueIndicator}) OR
                    (""Books"".""MediaType"" = 1 AND ""Books"".""EbookMonitored"" = {trueIndicator})";
        }

        private static string BuildEffectiveReleaseDate(string trueIndicator)
        {
            return $@"COALESCE(
                        (SELECT ""Editions"".""ReleaseDate""
                         FROM ""Editions""
                         WHERE ""Editions"".""BookId"" = ""Books"".""Id""
                           AND ""Editions"".""Monitored"" = {trueIndicator}
                         ORDER BY ""Editions"".""Id""
                         LIMIT 1),
                        ""Books"".""ReleaseDate"")";
        }
    }
}
