using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using NLog;
using NUnit.Framework;
using NzbDrone.Core.MediaFiles.BookImport;
using NzbDrone.Core.Talearr.Calibre;

namespace Chaptarr.Core.Test.Talearr.Calibre
{
    [TestFixture]
    public class TalearrCalibreIdentifierFixture
    {
        private CalibreTestLibrary _library;

        [SetUp]
        public void SetUp()
        {
            _library = new CalibreTestLibrary();
        }

        [TearDown]
        public void TearDown()
        {
            _library.Dispose();
        }

        private TalearrCalibreIdentifier Subject(bool enabled = true)
        {
            return new TalearrCalibreIdentifier(_library.Settings(enabled), LogManager.GetCurrentClassLogger());
        }

        private static DiscoveredFileWithMetadata File(string path, Dictionary<string, List<string>> tags = null)
        {
            return new DiscoveredFileWithMetadata
            {
                Path = path,
                AllTags = tags ?? new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase)
                {
                    ["title"] = new List<string> { "Microsoft Word - final_v3.docx" },
                    ["author"] = new List<string> { "jsmith" }
                }
            };
        }

        [Test]
        public void maps_a_file_inside_the_library_by_relative_path_and_format()
        {
            var id = _library.AddBook("Dune", "Frank Herbert", "978-0-441-01359-3", "eng", "PDF", "EPUB");
            _library.AddBook("Emma", "Jane Austen", null, "eng", "PDF");
            var path = _library.FilePath(id, "Dune", "Frank Herbert", "pdf");

            var result = Subject().Identify(new[] { File(path) });

            Assert.That(result.ContainsKey(path), Is.True);
            var found = result[path];
            Assert.Multiple(() =>
            {
                Assert.That(found.IsMatch, Is.True);
                Assert.That(found.Method, Is.EqualTo(TalearrCalibreMatchMethod.LibraryPath));
                Assert.That(found.Book.Id, Is.EqualTo(id));
                Assert.That(found.Book.Title, Is.EqualTo("Dune"));
                Assert.That(found.Book.Authors, Is.EqualTo(new[] { "Frank Herbert" }));
                Assert.That(found.Book.Isbns, Is.EqualTo(new[] { "9780441013593" }));
                Assert.That(found.Book.Languages, Is.EqualTo(new[] { "eng" }));
                Assert.That(found.Describe(), Is.EqualTo("Identified from Calibre (library path): Dune by Frank Herbert, ISBN 9780441013593, language eng"));
            });
        }

        [Test]
        public void a_format_calibre_does_not_hold_is_not_mapped_by_path()
        {
            var id = _library.AddBook("Dune", "Frank Herbert", null, "eng", "EPUB");
            var path = _library.FilePath(id, "Dune", "Frank Herbert", "pdf");

            var result = Subject().Identify(new[] { File(path) });

            Assert.That(result, Is.Empty);
        }

        [Test]
        public void a_download_elsewhere_matches_one_calibre_book_by_its_isbn()
        {
            var id = _library.AddBook("Dune", "Frank Herbert", "0441013597", "eng", "EPUB");
            _library.AddBook("Emma", "Jane Austen", "9780141439587", "eng", "EPUB");

            var download = Path.Combine(Path.GetTempPath(), "downloads", "scan.pdf");
            var tags = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase)
            {
                ["title"] = new List<string> { "scan" },
                ["isbn"] = new List<string> { "978-0-441-01359-3" }
            };

            var found = Subject().Identify(new[] { File(download, tags) })[download];

            Assert.Multiple(() =>
            {
                Assert.That(found.IsMatch, Is.True);
                Assert.That(found.Method, Is.EqualTo(TalearrCalibreMatchMethod.Isbn));
                Assert.That(found.Book.Id, Is.EqualTo(id));
                Assert.That(found.Isbn, Is.EqualTo("9780441013593"));
            });
        }

        [Test]
        public void an_isbn_in_the_file_name_is_used_when_the_tags_have_none()
        {
            var id = _library.AddBook("Emma", "Jane Austen", "9780141439587", "eng", "PDF");
            var download = Path.Combine(Path.GetTempPath(), "downloads", "Emma 9780141439587.pdf");

            var found = Subject().Identify(new[] { File(download) })[download];

            Assert.That(found.Book.Id, Is.EqualTo(id));
        }

        [Test]
        public void an_isbn_shared_by_two_calibre_books_is_ambiguous_and_not_used()
        {
            _library.AddBook("Dune", "Frank Herbert", "9780441013593", "eng", "EPUB");
            _library.AddBook("Dune (copy)", "Frank Herbert", "9780441013593", "eng", "EPUB");
            var download = Path.Combine(Path.GetTempPath(), "downloads", "dune.epub");
            var tags = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase)
            {
                ["isbn"] = new List<string> { "9780441013593" }
            };

            var found = Subject().Identify(new[] { File(download, tags) })[download];

            Assert.Multiple(() =>
            {
                Assert.That(found.IsMatch, Is.False);
                Assert.That(found.AmbiguousCount, Is.EqualTo(2));
                Assert.That(found.Describe(), Does.Contain("Calibre has 2 books with ISBN 9780441013593"));
            });

            // Nothing from Calibre reaches the tags of an ambiguous file.
            var file = File(download, tags);
            var before = file.AllTags;
            TalearrCalibreEvidence.Apply(Subject(), new[] { file });
            Assert.That(file.AllTags, Is.SameAs(before));
        }

        [Test]
        public void the_same_format_settles_two_books_with_one_isbn()
        {
            _library.AddBook("Dune", "Frank Herbert", "9780441013593", "eng", "EPUB");
            var pdf = _library.AddBook("Dune scan", "Frank Herbert", "9780441013593", "eng", "PDF");
            var download = Path.Combine(Path.GetTempPath(), "downloads", "dune.pdf");
            var tags = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase)
            {
                ["isbn"] = new List<string> { "9780441013593" }
            };

            var found = Subject().Identify(new[] { File(download, tags) })[download];

            Assert.That(found.Book.Id, Is.EqualTo(pdf));
        }

        [Test]
        public void audiobooks_are_never_looked_up()
        {
            _library.AddBook("Dune", "Frank Herbert", "9780441013593", "eng", "EPUB");
            var audio = Path.Combine(Path.GetTempPath(), "downloads", "9780441013593.m4b");

            Assert.That(Subject().Identify(new[] { File(audio) }), Is.Empty);
        }

        [Test]
        public void reading_leaves_metadata_db_unchanged_even_while_calibre_holds_a_write_lock()
        {
            var id = _library.AddBook("Dune", "Frank Herbert", "9780441013593", "eng", "PDF");
            var before = Snapshot();

            // Calibre has the database open with a pending write (a RESERVED lock).
            using (var calibre = _library.Open())
            {
                using (var pending = calibre.CreateCommand())
                {
                    pending.CommandText = "BEGIN IMMEDIATE; UPDATE books SET title = 'Pending' WHERE id = 1;";
                    pending.ExecuteNonQuery();
                }

                var path = _library.FilePath(id, "Dune", "Frank Herbert", "pdf");
                var result = Subject().Identify(new[] { File(path) });
                Assert.That(result[path].Book.Title, Is.EqualTo("Dune"));

                using (var rollback = calibre.CreateCommand())
                {
                    rollback.CommandText = "ROLLBACK;";
                    rollback.ExecuteNonQuery();
                }
            }

            Assert.That(Snapshot(), Is.EqualTo(before));
        }

        [Test]
        public void the_reader_refuses_writes()
        {
            _library.AddBook("Dune", "Frank Herbert", null, "eng", "PDF");
            var connectionString = TalearrCalibreLibraryReader.ConnectionString(_library.MetadataPath);
            Assert.That(connectionString, Does.Contain("Mode=ReadOnly"));

            using var connection = new Microsoft.Data.Sqlite.SqliteConnection(connectionString);
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "UPDATE books SET title = 'x'";
            Assert.Throws<Microsoft.Data.Sqlite.SqliteException>(() => command.ExecuteNonQuery());
        }

        [Test]
        public void with_the_setting_off_nothing_is_read_and_tags_stay_as_they_are()
        {
            var id = _library.AddBook("Dune", "Frank Herbert", "9780441013593", "eng", "PDF");
            var file = File(_library.FilePath(id, "Dune", "Frank Herbert", "pdf"));
            var tags = file.AllTags;

            var subject = Subject(enabled: false);
            var found = TalearrCalibreEvidence.Apply(subject, new[] { file });

            Assert.Multiple(() =>
            {
                Assert.That(subject.IsEnabled, Is.False);
                Assert.That(found, Is.Empty);
                Assert.That(file.AllTags, Is.SameAs(tags));
                Assert.That(file.AllTags["title"], Is.EqualTo(new[] { "Microsoft Word - final_v3.docx" }));
            });
        }

        [Test]
        public void evidence_replaces_the_identity_tags_on_a_copy()
        {
            var id = _library.AddBook("Dune", "Frank Herbert", "9780441013593", "eng", "PDF");
            var file = File(_library.FilePath(id, "Dune", "Frank Herbert", "pdf"));
            file.AllTags["producer"] = new List<string> { "Acrobat" };
            var original = file.AllTags;

            TalearrCalibreEvidence.Apply(Subject(), new[] { file });

            Assert.Multiple(() =>
            {
                Assert.That(file.AllTags, Is.Not.SameAs(original));
                Assert.That(original["title"], Is.EqualTo(new[] { "Microsoft Word - final_v3.docx" }));
                Assert.That(file.AllTags["title"], Is.EqualTo(new[] { "Dune" }));
                Assert.That(file.AllTags["author"], Is.EqualTo(new[] { "Frank Herbert" }));
                Assert.That(file.AllTags["isbn"], Is.EqualTo(new[] { "9780441013593" }));
                Assert.That(file.AllTags["language"], Is.EqualTo(new[] { "eng" }));
                Assert.That(file.AllTags["producer"], Is.EqualTo(new[] { "Acrobat" }));
                Assert.That(file.ISBN, Is.EqualTo("9780441013593"));
            });
        }

        [Test]
        public void a_missing_library_is_skipped_without_errors()
        {
            var subject = new TalearrCalibreIdentifier(new CalibreTestLibrary.StubSettings("/nowhere/calibre"), LogManager.GetCurrentClassLogger());

            Assert.That(subject.Identify(new[] { File("/nowhere/calibre/A/B (1)/B - A.pdf") }), Is.Empty);
        }

        [Test]
        public void validate_names_what_is_wrong_with_the_folder()
        {
            Assert.Multiple(() =>
            {
                Assert.That(TalearrCalibreSettingsService.Validate(string.Empty), Is.Null);
                Assert.That(TalearrCalibreSettingsService.Validate(_library.Root), Is.Null);
                Assert.That(TalearrCalibreSettingsService.Validate(Path.Combine(_library.Root, "missing")), Does.Contain("was not found"));
                Assert.That(TalearrCalibreSettingsService.Validate(Path.GetTempPath()), Does.Contain("No metadata.db"));
                Assert.That(TalearrCalibreSettingsService.Validate("relative/calibre"), Does.Contain("full path"));
            });
        }

        private string Snapshot()
        {
            using var sha = SHA256.Create();
            var bytes = System.IO.File.ReadAllBytes(_library.MetadataPath);
            var extra = Directory.GetFiles(_library.Root).Select(Path.GetFileName).OrderBy(name => name);
            return Convert.ToHexString(sha.ComputeHash(bytes)) + "|" + string.Join(",", extra);
        }
    }
}
