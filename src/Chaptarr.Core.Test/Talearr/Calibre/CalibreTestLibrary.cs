using System;
using System.Collections.Generic;
using System.IO;
using Microsoft.Data.Sqlite;
using NzbDrone.Core.Talearr.Calibre;

namespace Chaptarr.Core.Test.Talearr.Calibre
{
    // A small Calibre library on disk: metadata.db with Calibre's real table and column names.
    public sealed class CalibreTestLibrary : IDisposable
    {
        private int _nextLink = 1;

        public CalibreTestLibrary()
        {
            Root = Path.Combine(Path.GetTempPath(), "talearr-calibre-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Root);
            MetadataPath = Path.Combine(Root, "metadata.db");

            Execute(@"
CREATE TABLE books (id INTEGER PRIMARY KEY AUTOINCREMENT, title TEXT NOT NULL DEFAULT 'Unknown', sort TEXT,
    timestamp TIMESTAMP, pubdate TIMESTAMP, series_index REAL NOT NULL DEFAULT 1.0, author_sort TEXT,
    isbn TEXT DEFAULT '', lccn TEXT DEFAULT '', path TEXT NOT NULL DEFAULT '', flags INTEGER NOT NULL DEFAULT 1,
    uuid TEXT, has_cover BOOL DEFAULT 0, last_modified TIMESTAMP);
CREATE TABLE authors (id INTEGER PRIMARY KEY, name TEXT NOT NULL COLLATE NOCASE, sort TEXT COLLATE NOCASE, link TEXT NOT NULL DEFAULT '', UNIQUE(name));
CREATE TABLE books_authors_link (id INTEGER PRIMARY KEY, book INTEGER NOT NULL, author INTEGER NOT NULL, UNIQUE(book, author));
CREATE TABLE identifiers (id INTEGER PRIMARY KEY, book INTEGER NOT NULL, type TEXT NOT NULL DEFAULT 'isbn' COLLATE NOCASE, val TEXT NOT NULL COLLATE NOCASE, UNIQUE(book, type));
CREATE TABLE languages (id INTEGER PRIMARY KEY, lang_code TEXT NOT NULL COLLATE NOCASE, link TEXT NOT NULL DEFAULT '', UNIQUE(lang_code));
CREATE TABLE books_languages_link (id INTEGER PRIMARY KEY, book INTEGER NOT NULL, lang_code INTEGER NOT NULL, item_order INTEGER NOT NULL DEFAULT 0, UNIQUE(book, lang_code));
CREATE TABLE data (id INTEGER PRIMARY KEY, book INTEGER NOT NULL, format TEXT NOT NULL COLLATE NOCASE, uncompressed_size INTEGER NOT NULL, name TEXT NOT NULL, UNIQUE(book, format));
");
        }

        public string Root { get; }
        public string MetadataPath { get; }

        // Adds a book the way Calibre lays it out: <Author>/<Title> (<id>)/<Title> - <Author>.<ext>.
        public int AddBook(string title, string author, string isbn, string language, params string[] formats)
        {
            int id;
            using (var connection = Open())
            {
                using (var insert = connection.CreateCommand())
                {
                    insert.CommandText = "INSERT INTO books (title, sort, author_sort) VALUES ($title, $title, $author); SELECT last_insert_rowid();";
                    insert.Parameters.AddWithValue("$title", title);
                    insert.Parameters.AddWithValue("$author", author);
                    id = Convert.ToInt32(insert.ExecuteScalar());
                }

                var bookPath = $"{author}/{title} ({id})";
                var fileName = $"{title} - {author}";
                Run(connection, "UPDATE books SET path = $p WHERE id = $id", ("$p", bookPath), ("$id", id));
                Run(connection, "INSERT OR IGNORE INTO authors (name, sort) VALUES ($n, $n)", ("$n", author));
                Run(connection, "INSERT INTO books_authors_link (id, book, author) SELECT $l, $id, id FROM authors WHERE name = $n", ("$l", _nextLink++), ("$id", id), ("$n", author));

                if (isbn != null)
                {
                    Run(connection, "INSERT INTO identifiers (book, type, val) VALUES ($id, 'isbn', $v)", ("$id", id), ("$v", isbn));
                }

                if (language != null)
                {
                    Run(connection, "INSERT OR IGNORE INTO languages (lang_code) VALUES ($c)", ("$c", language));
                    Run(connection, "INSERT INTO books_languages_link (book, lang_code, item_order) SELECT $id, id, 0 FROM languages WHERE lang_code = $c", ("$id", id), ("$c", language));
                }

                foreach (var format in formats)
                {
                    Run(connection, "INSERT INTO data (book, format, uncompressed_size, name) VALUES ($id, $f, 1000, $n)", ("$id", id), ("$f", format.ToUpperInvariant()), ("$n", fileName));
                }
            }

            return id;
        }

        // Where Calibre keeps a book's file inside the library.
        public string FilePath(int id, string title, string author, string extension)
        {
            return Path.Combine(Root, author, $"{title} ({id})", $"{title} - {author}.{extension}");
        }

        public SqliteConnection Open()
        {
            var connection = new SqliteConnection(new SqliteConnectionStringBuilder
            {
                DataSource = MetadataPath,
                Mode = SqliteOpenMode.ReadWriteCreate,
                Pooling = false
            }.ToString());
            connection.Open();
            return connection;
        }

        public ITalearrCalibreSettingsService Settings(bool enabled = true)
        {
            return new StubSettings(enabled ? Root : string.Empty);
        }

        public void Dispose()
        {
            try
            {
                Directory.Delete(Root, true);
            }
            catch (Exception)
            {
            }
        }

        private void Execute(string sql)
        {
            using var connection = Open();
            using var command = connection.CreateCommand();
            command.CommandText = sql;
            command.ExecuteNonQuery();
        }

        private static void Run(SqliteConnection connection, string sql, params (string Name, object Value)[] parameters)
        {
            using var command = connection.CreateCommand();
            command.CommandText = sql;
            foreach (var (name, value) in parameters)
            {
                command.Parameters.AddWithValue(name, value);
            }

            command.ExecuteNonQuery();
        }

        public sealed class StubSettings : ITalearrCalibreSettingsService
        {
            private TalearrCalibreSettings _settings;

            public StubSettings(string path)
            {
                _settings = new TalearrCalibreSettings { LibraryPath = path };
            }

            public TalearrCalibreSettings Get() => _settings.Clone();

            public TalearrCalibreSettings Save(TalearrCalibreSettings settings)
            {
                _settings = settings.Clone();
                return Get();
            }
        }
    }
}
