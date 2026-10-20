using System;
using System.Collections.Generic;
using System.Data;
using System.Linq;
using Dapper;
using NLog;
using NzbDrone.Core.Talearr.Recordings;

namespace NzbDrone.Core.Talearr.Tags
{
    public interface ITalearrBookTagStore
    {
        // Every tagged book, book row id to sorted tag ids. Empty when talearr.db is unusable.
        Dictionary<int, List<int>> GetAll();
        List<int> Get(int bookId);

        // Replaces the tags of each book with the given set. False when the write failed.
        bool Set(IDictionary<int, List<int>> tagsByBookId);
        void RemoveBooks(IEnumerable<int> bookIds);
        void RemoveTagsExcept(IEnumerable<int> knownTagIds);
    }

    // Talearr: reads and writes the BookTags table in talearr.db. Chaptarr keeps tags on authors
    // only; book tags never touch chaptarr.db. Every call is best effort like the other talearr.db
    // stores: a failure is logged and reads as "no tags".
    public class TalearrBookTagStore : ITalearrBookTagStore
    {
        private readonly ITalearrDatabase _database;
        private readonly Logger _logger;

        public TalearrBookTagStore(ITalearrDatabase database, Logger logger)
        {
            _database = database;
            _logger = logger;
        }

        public Dictionary<int, List<int>> GetAll()
        {
            return Run("read book tags", new Dictionary<int, List<int>>(), connection =>
                connection.Query<Row>("SELECT BookId, TagId FROM BookTags")
                    .GroupBy(r => (int)r.BookId)
                    .ToDictionary(g => g.Key, g => g.Select(r => (int)r.TagId).Distinct().OrderBy(t => t).ToList()));
        }

        public List<int> Get(int bookId)
        {
            return Run("read book tags", new List<int>(), connection =>
                connection.Query<long>("SELECT TagId FROM BookTags WHERE BookId = @BookId ORDER BY TagId", new { BookId = bookId })
                    .Select(t => (int)t)
                    .ToList());
        }

        public bool Set(IDictionary<int, List<int>> tagsByBookId)
        {
            if (tagsByBookId == null || tagsByBookId.Count == 0)
            {
                return true;
            }

            return Run("write book tags", false, connection =>
            {
                using var transaction = connection.BeginTransaction();
                foreach (var pair in tagsByBookId)
                {
                    connection.Execute("DELETE FROM BookTags WHERE BookId = @BookId", new { BookId = pair.Key }, transaction);

                    var rows = (pair.Value ?? new List<int>())
                        .Where(t => t > 0)
                        .Distinct()
                        .Select(t => new { BookId = pair.Key, TagId = t })
                        .ToList();

                    if (rows.Any())
                    {
                        connection.Execute("INSERT INTO BookTags (BookId, TagId) VALUES (@BookId, @TagId)", rows, transaction);
                    }
                }

                transaction.Commit();
                return true;
            });
        }

        public void RemoveBooks(IEnumerable<int> bookIds)
        {
            var ids = (bookIds ?? Enumerable.Empty<int>()).Distinct().ToList();
            if (!ids.Any())
            {
                return;
            }

            // Ids are inlined: Chaptarr registers Dapper type handlers for int lists (JSON), which
            // would turn an IN @list parameter into a single string.
            Run("remove book tags", 0, connection => connection.Execute($"DELETE FROM BookTags WHERE BookId IN ({string.Join(", ", ids)})"));
        }

        public void RemoveTagsExcept(IEnumerable<int> knownTagIds)
        {
            var known = (knownTagIds ?? Enumerable.Empty<int>()).Distinct().ToList();

            Run("prune book tags", 0, connection => known.Any()
                ? connection.Execute($"DELETE FROM BookTags WHERE TagId NOT IN ({string.Join(", ", known)})")
                : connection.Execute("DELETE FROM BookTags"));
        }

        private T Run<T>(string what, T fallback, Func<IDbConnection, T> action)
        {
            try
            {
                using var connection = _database.OpenConnection();
                return action(connection);
            }
            catch (Exception ex) when (_database.IsCorruption(ex))
            {
                _database.Reset($"corruption found during {what}");
                return fallback;
            }
            catch (Exception ex)
            {
                _logger.Warn(ex, "Talearr: could not {0} in {1}", what, TalearrDatabase.FileName);
                return fallback;
            }
        }

        private sealed class Row
        {
            public long BookId { get; set; }
            public long TagId { get; set; }
        }
    }
}
