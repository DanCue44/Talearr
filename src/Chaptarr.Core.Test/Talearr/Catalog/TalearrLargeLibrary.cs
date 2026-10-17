using System;
using System.Data;
using Dapper;

namespace Chaptarr.Core.Test.Talearr.Catalog
{
    // TaleArr: synthetic large library on a real migrated schema (for the benchmark and for
    // large-scale regression tests). Deterministic: the same sizes give the same rows.
    public static class TalearrLargeLibrary
    {
        public static void Seed(TalearrMigratedDatabase db, int authors, int booksPerAuthor, int editionsPerBook, double fileShare = 0.3)
        {
            using var conn = db.Open();
            conn.Execute("PRAGMA synchronous = OFF; PRAGMA journal_mode = MEMORY;");
            using var tran = conn.BeginTransaction();
            var now = new DateTime(2026, 6, 1, 0, 0, 0, DateTimeKind.Utc);

            var author = Command(conn, tran, @"INSERT INTO Authors (Id, Name, CleanName, SortName, Path, AudiobookPath, AudiobookRootFolderPath, Monitored, AudiobookMonitored, EbookMonitored, Added, HardcoverAuthorId, Images, Links, Genres, Ratings, Tags, AddOptions)
                VALUES (@Id, @Name, @CleanName, @Name, @Path, @Path, '/audiobooks', 1, 1, 1, @Added, @Hc, '[]', '[]', '[]', '{}', '[]', '{}')",
                "Id", "Name", "CleanName", "Path", "Added", "Hc");
            var book = Command(conn, tran, @"INSERT INTO Books (Id, AuthorId, Title, CleanTitle, MediaType, AudiobookMonitored, EbookMonitored, AnyEditionOk, Added, ReleaseDate, HardcoverBookId, Images, Links, Genres, Ratings, RelatedBooks, AddOptions, SeriesName)
                VALUES (@Id, @AuthorId, @Title, @Title, @MediaType, @AMon, @EMon, 1, @Added, @Release, @Hc, '[]', '[]', '[]', '{}', '[]', '{}', NULL)",
                "Id", "AuthorId", "Title", "MediaType", "AMon", "EMon", "Added", "Release", "Hc");
            var edition = Command(conn, tran, @"INSERT INTO Editions (Id, BookId, Title, ForeignEditionId, Monitored, ReleaseDate, Images, Links, Ratings)
                VALUES (@Id, @BookId, @Title, @Fei, @Monitored, @Release, '[]', '[]', '{}')",
                "Id", "BookId", "Title", "Fei", "Monitored", "Release");
            var file = Command(conn, tran, @"INSERT INTO BookFiles (Id, EditionId, Path, Size, Modified, DateAdded, Quality, MediaInfo, CalibreId, Part)
                VALUES (@Id, @EditionId, @Path, 1048576, @Added, @Added, '{""quality"":1}', '{}', 0, 1)",
                "Id", "EditionId", "Path", "Added");
            var series = Command(conn, tran, @"INSERT INTO Series (Id, Title) VALUES (@Id, @Title)", "Id", "Title");
            var link = Command(conn, tran, @"INSERT INTO SeriesBookLink (SeriesId, BookId, Position) VALUES (@SeriesId, @BookId, '1')", "SeriesId", "BookId");

            var bookId = 0;
            var editionId = 0;
            var fileId = 0;
            for (var a = 1; a <= authors; a++)
            {
                Run(author, a, $"Author {a}", $"author{a}", $"/audiobooks/Author {a}", now, $"hc:a{a}");
                Run(series, a, $"Series {a}");

                for (var b = 0; b < booksPerAuthor; b++)
                {
                    bookId++;
                    var mediaType = b % 2;
                    var release = now.AddDays((b * 37 % 900) - 600);
                    var monitored = b % 3 != 0;
                    Run(book, bookId, a, $"Book {a}-{b / 2}", mediaType, monitored && mediaType == 0, monitored && mediaType == 1, now, release, $"hc:b{a}-{b / 2}");

                    if (b % 2 == 0)
                    {
                        Run(link, a, bookId);
                    }

                    // editionsPerBook on odd rows, one more on even rows (2 gives 2.5 per book).
                    var editionCount = editionsPerBook + (b % 2 == 0 ? 1 : 0);
                    for (var e = 0; e < editionCount; e++)
                    {
                        editionId++;
                        Run(edition, editionId, bookId, $"Book {a}-{b / 2}", $"e{editionId}", e == 0, release);

                        if (e == 0 && (bookId % 10) < fileShare * 10)
                        {
                            fileId++;
                            Run(file, fileId, editionId, $"/audiobooks/Author {a}/Book {bookId}/file{fileId}.m4b", now);
                        }
                    }
                }
            }

            tran.Commit();
            conn.Execute("ANALYZE;");
        }

        private static IDbCommand Command(IDbConnection conn, IDbTransaction tran, string sql, params string[] names)
        {
            var command = conn.CreateCommand();
            command.Transaction = tran;
            command.CommandText = sql;
            foreach (var name in names)
            {
                var parameter = command.CreateParameter();
                parameter.ParameterName = "@" + name;
                command.Parameters.Add(parameter);
            }

            command.Prepare();
            return command;
        }

        private static void Run(IDbCommand command, params object[] values)
        {
            for (var i = 0; i < values.Length; i++)
            {
                var value = values[i];
                ((IDbDataParameter)command.Parameters[i]).Value = value is bool flag ? (flag ? 1 : 0) : value;
            }

            command.ExecuteNonQuery();
        }
    }
}
