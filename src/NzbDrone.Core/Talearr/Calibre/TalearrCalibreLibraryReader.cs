using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Data.Sqlite;
using NzbDrone.Core.Talearr.Audiobookshelf;

namespace NzbDrone.Core.Talearr.Calibre
{
    public sealed class TalearrCalibreFormat
    {
        // Calibre's data.format, upper case (EPUB, PDF, AZW3, ...).
        public string Format { get; set; }

        // Calibre's data.name: the file name without the extension.
        public string Name { get; set; }
    }

    public sealed class TalearrCalibreBook
    {
        public int Id { get; set; }
        public string Title { get; set; }

        // books.path: the book folder relative to the library root, with forward slashes.
        public string Path { get; set; }
        public List<string> Authors { get; set; } = new List<string>();

        // Normalized to ISBN-13.
        public List<string> Isbns { get; set; } = new List<string>();

        // Calibre language codes (ISO 639-2, e.g. eng).
        public List<string> Languages { get; set; } = new List<string>();
        public List<TalearrCalibreFormat> Formats { get; set; } = new List<TalearrCalibreFormat>();
    }

    // Talearr: reads a Calibre metadata.db strictly read-only. Every call opens its own
    // connection in SqliteOpenMode.ReadOnly without pooling and closes it straight away, so
    // Calibre (or Calibre-Web) can keep the database open and write to it. Only SELECTs run here.
    public sealed class TalearrCalibreLibraryReader : IDisposable
    {
        private readonly SqliteConnection _connection;

        private TalearrCalibreLibraryReader(SqliteConnection connection)
        {
            _connection = connection;
        }

        public static string ConnectionString(string metadataPath)
        {
            return new SqliteConnectionStringBuilder
            {
                DataSource = metadataPath,
                Mode = SqliteOpenMode.ReadOnly,
                Cache = SqliteCacheMode.Private,
                Pooling = false,

                // Seconds to wait while Calibre holds a write lock, then give up.
                DefaultTimeout = 5
            }.ToString();
        }

        public static TalearrCalibreLibraryReader Open(string metadataPath)
        {
            var connection = new SqliteConnection(ConnectionString(metadataPath));
            try
            {
                connection.Open();

                // Belt and braces on top of the read-only open mode.
                using (var command = connection.CreateCommand())
                {
                    command.CommandText = "PRAGMA query_only = ON;";
                    command.ExecuteNonQuery();
                }

                return new TalearrCalibreLibraryReader(connection);
            }
            catch
            {
                connection.Dispose();
                throw;
            }
        }

        public static int CountBooks(string metadataPath)
        {
            using var reader = Open(metadataPath);
            using var command = reader._connection.CreateCommand();
            command.CommandText = "SELECT COUNT(*) FROM books";
            return Convert.ToInt32(command.ExecuteScalar());
        }

        // Book ids whose folder is bookPath and that hold a file named name.format.
        // Exact match first; a case-insensitive match only when it is unique.
        public List<int> FindByFile(string bookPath, string name, string format)
        {
            var exact = QueryIds(
                "SELECT DISTINCT b.id FROM books b JOIN data d ON d.book = b.id WHERE b.path = $path AND d.name = $name AND upper(d.format) = $format",
                bookPath,
                name,
                format);

            if (exact.Count > 0)
            {
                return exact;
            }

            var loose = QueryIds(
                "SELECT DISTINCT b.id FROM books b JOIN data d ON d.book = b.id WHERE b.path = $path COLLATE NOCASE AND d.name = $name COLLATE NOCASE AND upper(d.format) = $format",
                bookPath,
                name,
                format);

            return loose.Count == 1 ? loose : new List<int>();
        }

        // Book ids that carry this ISBN (any stored form) as an isbn identifier.
        public List<int> FindByIsbn(string isbn13)
        {
            if (string.IsNullOrWhiteSpace(isbn13))
            {
                return new List<int>();
            }

            // Calibre stores ISBNs as typed (with or without hyphens, ISBN-10 or 13), so the
            // isbn identifiers are read once per reader and indexed by their ISBN-13.
            if (_isbnIndex == null)
            {
                _isbnIndex = new Dictionary<string, HashSet<int>>(StringComparer.Ordinal);
                using var command = _connection.CreateCommand();
                command.CommandText = "SELECT book, val FROM identifiers WHERE lower(type) = 'isbn'";
                using var reader = command.ExecuteReader();
                while (reader.Read())
                {
                    if (reader.IsDBNull(0) || reader.IsDBNull(1))
                    {
                        continue;
                    }

                    var normalized = TalearrAbsItemReader.NormalizeIsbn(reader.GetString(1));
                    if (normalized == null)
                    {
                        continue;
                    }

                    if (!_isbnIndex.TryGetValue(normalized, out var books))
                    {
                        books = new HashSet<int>();
                        _isbnIndex[normalized] = books;
                    }

                    books.Add(reader.GetInt32(0));
                }
            }

            return _isbnIndex.TryGetValue(isbn13, out var ids) ? ids.OrderBy(id => id).ToList() : new List<int>();
        }

        private Dictionary<string, HashSet<int>> _isbnIndex;

        public TalearrCalibreBook GetBook(int id)
        {
            TalearrCalibreBook book = null;

            using (var command = _connection.CreateCommand())
            {
                command.CommandText = "SELECT id, title, path FROM books WHERE id = $id";
                command.Parameters.AddWithValue("$id", id);
                using var reader = command.ExecuteReader();
                if (reader.Read())
                {
                    book = new TalearrCalibreBook
                    {
                        Id = reader.GetInt32(0),
                        Title = reader.IsDBNull(1) ? null : reader.GetString(1),
                        Path = reader.IsDBNull(2) ? null : reader.GetString(2)
                    };
                }
            }

            if (book == null)
            {
                return null;
            }

            book.Authors = QueryStrings(
                "SELECT a.name FROM books_authors_link l JOIN authors a ON a.id = l.author WHERE l.book = $id ORDER BY l.id",
                id);
            book.Isbns = QueryStrings("SELECT val FROM identifiers WHERE book = $id AND lower(type) = 'isbn'", id)
                .Select(TalearrAbsItemReader.NormalizeIsbn)
                .Where(isbn => isbn != null)
                .Distinct()
                .ToList();
            book.Languages = QueryStrings(
                "SELECT g.lang_code FROM books_languages_link l JOIN languages g ON g.id = l.lang_code WHERE l.book = $id ORDER BY l.item_order",
                id);

            using (var command = _connection.CreateCommand())
            {
                command.CommandText = "SELECT format, name FROM data WHERE book = $id ORDER BY id";
                command.Parameters.AddWithValue("$id", id);
                using var reader = command.ExecuteReader();
                while (reader.Read())
                {
                    book.Formats.Add(new TalearrCalibreFormat
                    {
                        Format = reader.IsDBNull(0) ? null : reader.GetString(0).ToUpperInvariant(),
                        Name = reader.IsDBNull(1) ? null : reader.GetString(1)
                    });
                }
            }

            return book;
        }

        public void Dispose()
        {
            _connection.Dispose();
        }

        private List<int> QueryIds(string sql, string path, string name, string format)
        {
            var ids = new List<int>();
            using var command = _connection.CreateCommand();
            command.CommandText = sql;
            command.Parameters.AddWithValue("$path", path ?? string.Empty);
            command.Parameters.AddWithValue("$name", name ?? string.Empty);
            command.Parameters.AddWithValue("$format", (format ?? string.Empty).ToUpperInvariant());
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                ids.Add(reader.GetInt32(0));
            }

            return ids;
        }

        private List<string> QueryStrings(string sql, int id)
        {
            var values = new List<string>();
            using var command = _connection.CreateCommand();
            command.CommandText = sql;
            command.Parameters.AddWithValue("$id", id);
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                if (!reader.IsDBNull(0) && !string.IsNullOrWhiteSpace(reader.GetString(0)))
                {
                    values.Add(reader.GetString(0).Trim());
                }
            }

            return values;
        }
    }
}
