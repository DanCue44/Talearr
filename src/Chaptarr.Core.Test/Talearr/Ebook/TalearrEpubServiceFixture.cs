using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Chaptarr.Core.Test.Talearr.Audiobook;
using NLog;
using NUnit.Framework;
using NzbDrone.Core.Books;
using NzbDrone.Core.MediaFiles;
using NzbDrone.Core.MediaFiles.Events;
using NzbDrone.Core.Messaging.Commands;
using NzbDrone.Core.Qualities;
using NzbDrone.Core.Talearr.Ebook.Epub;

namespace Chaptarr.Core.Test.Talearr.Ebook
{
    [TestFixture]
    public class TalearrEpubServiceFixture
    {
        private sealed class FakeConverter : ITalearrEpubConverter
        {
            public bool Available { get; set; } = true;
            public Func<string, string, EpubConversionOutcome> Result { get; set; }
            public List<string> Inputs { get; } = new List<string>();

            public bool IsAvailable() => Available;

            public EpubConversionOutcome Convert(string inputPath, string outputPath)
            {
                Inputs.Add(inputPath);
                return Result(inputPath, outputPath);
            }
        }

        private sealed class FakeSettings : ITalearrEpubSettingsService
        {
            public TalearrEpubSettings Settings { get; set; } = new TalearrEpubSettings();
            public TalearrEpubSettings Get() => Settings.Clone();
            public TalearrEpubSettings Save(TalearrEpubSettings settings) => Settings = settings;
        }

        private string _folder;
        private Book _book;
        private Edition _edition;
        private List<BookFile> _files;
        private List<string> _events;
        private List<BookFile> _added;
        private List<BookFile> _deletedRows;
        private List<string> _recycled;
        private List<Command> _pushed;
        private FakeConverter _converter;
        private FakeSettings _settings;

        [SetUp]
        public void SetUp()
        {
            _folder = Path.Combine(Path.GetTempPath(), "talearr-epub-svc-" + Guid.NewGuid().ToString("N"), "The Hobbit");
            Directory.CreateDirectory(_folder);

            var author = new Author { Id = 21, Name = "J.R.R. Tolkien" };
            _book = new Book { Id = 6001, Title = "The Hobbit", Author = author, AuthorId = 21, MediaType = BookMediaType.Ebook };
            _edition = new Edition { Id = 88, Title = "The Hobbit", Book = _book };

            _files = new List<BookFile> { NewFile(1, "The Hobbit.azw3", Quality.AZW3) };

            _events = new List<string>();
            _added = new List<BookFile>();
            _deletedRows = new List<BookFile>();
            _recycled = new List<string>();
            _pushed = new List<Command>();
            _converter = new FakeConverter { Result = Success };
            _settings = new FakeSettings();
        }

        [TearDown]
        public void TearDown()
        {
            var root = Path.GetDirectoryName(_folder);
            if (Directory.Exists(root))
            {
                Directory.Delete(root, true);
            }
        }

        private BookFile NewFile(int id, string name, Quality quality, int editionId = 88)
        {
            var path = Path.Combine(_folder, name);
            File.WriteAllText(path, "ebook");
            return new BookFile
            {
                Id = id,
                Path = path,
                EditionId = editionId,
                MediaType = "ebook",
                ReleaseGroup = "grp",
                Quality = new QualityModel(quality, new Revision(2))
            };
        }

        private static EpubConversionOutcome Success(string input, string output)
        {
            File.WriteAllText(output, "epub");
            return new EpubConversionOutcome { Success = true, OutputPath = output, OutputSize = 4 };
        }

        private TalearrEpubService Service()
        {
            var (books, bookFake) = Fake<IBookService>.Create();
            bookFake.On(nameof(IBookService.GetBook), _ => _book)
                .On(nameof(IBookService.GetBooksByAuthor), _ => new List<Book> { _book });

            var (authors, authorFake) = Fake<IAuthorService>.Create();
            authorFake.On(nameof(IAuthorService.GetAllAuthors), _ => new List<Author> { _book.Author });

            var (editions, editionFake) = Fake<IEditionService>.Create();
            editionFake.On(nameof(IEditionService.GetEdition), _ => _edition);

            var (media, mediaFake) = Fake<IMediaFileService>.Create();
            mediaFake.On(nameof(IMediaFileService.GetFilesByBook), _ => _files.ToList())
                .On(nameof(IMediaFileService.Add), args =>
                {
                    _events.Add("add");
                    _added.Add((BookFile)args[0]);
                    return args[0];
                })
                .On(nameof(IMediaFileService.DeleteMany), args =>
                {
                    _events.Add("deleteRows");
                    _deletedRows.AddRange((List<BookFile>)args[0]);
                    Assert.That(args[1], Is.EqualTo(DeleteMediaFileReason.Upgrade));
                    return null;
                });

            var (recycle, recycleFake) = Fake<IRecycleBinProvider>.Create();
            recycleFake.On(nameof(IRecycleBinProvider.DeleteFile), args =>
            {
                _events.Add("recycle");
                _recycled.Add((string)args[0]);
                File.Delete((string)args[0]);
                return null;
            });

            var (queue, queueFake) = Fake<IManageCommandQueue>.Create();
            queueFake.On("Push", args =>
            {
                _pushed.Add((Command)args[0]);
                return null;
            });

            return new TalearrEpubService(books, authors, editions, media, recycle, _settings, _converter, queue, LogManager.GetCurrentClassLogger());
        }

        [Test]
        public void epub_is_added_next_to_the_kindle_file_which_is_kept_by_default()
        {
            Service().Execute(new TalearrConvertToEpubCommand { BookId = 6001 });

            Assert.That(_events, Is.EqualTo(new[] { "add" }));
            var epub = _added.Single();
            Assert.That(epub.Path, Is.EqualTo(Path.Combine(_folder, "The Hobbit.epub")));
            Assert.That(epub.Quality.Quality, Is.EqualTo(Quality.EPUB));
            Assert.That(epub.Quality.Revision.Version, Is.EqualTo(2));
            Assert.That(epub.EditionId, Is.EqualTo(88));
            Assert.That(epub.MediaType, Is.EqualTo("ebook"));
            Assert.That(epub.ReleaseGroup, Is.EqualTo("grp"));
            Assert.That(File.Exists(_files[0].Path), Is.True);
        }

        [Test]
        public void replacing_removes_the_kindle_file_only_after_the_epub_is_added()
        {
            _settings.Settings.KeepOriginal = false;

            Service().Execute(new TalearrConvertToEpubCommand { BookId = 6001 });

            Assert.That(_events, Is.EqualTo(new[] { "add", "deleteRows", "recycle" }));
            Assert.That(_deletedRows.Single().Id, Is.EqualTo(1));
            Assert.That(_recycled.Single(), Is.EqualTo(_files[0].Path));
        }

        [Test]
        public void failed_conversion_keeps_the_kindle_file_and_says_why()
        {
            _settings.Settings.KeepOriginal = false;
            _converter.Result = (_, _) => EpubConversionOutcome.Fail("The ebook is DRM protected, so it cannot be converted.", true);

            var ex = Assert.Throws<TalearrEpubException>(() => Service().Execute(new TalearrConvertToEpubCommand { BookId = 6001 }));

            Assert.That(ex.Message, Does.Contain("DRM protected"));
            Assert.That(_events, Is.Empty);
            Assert.That(File.Exists(_files[0].Path), Is.True);
        }

        [Test]
        public void an_edition_that_already_has_an_epub_is_left_alone()
        {
            _files.Add(NewFile(2, "The Hobbit.epub", Quality.EPUB));

            Service().Execute(new TalearrConvertToEpubCommand { BookId = 6001 });

            Assert.That(_converter.Inputs, Is.Empty);
            Assert.That(_added, Is.Empty);
        }

        [Test]
        public void an_epub_file_already_on_disk_is_not_overwritten()
        {
            File.WriteAllText(Path.Combine(_folder, "The Hobbit.epub"), "someone else's");

            var ex = Assert.Throws<TalearrEpubException>(() => Service().Execute(new TalearrConvertToEpubCommand { BookId = 6001 }));

            Assert.That(ex.Message, Does.Contain("already in the folder"));
            Assert.That(_converter.Inputs, Is.Empty);
            Assert.That(File.ReadAllText(Path.Combine(_folder, "The Hobbit.epub")), Is.EqualTo("someone else's"));
        }

        [Test]
        public void azw3_is_preferred_over_mobi_in_the_same_edition()
        {
            _files.Insert(0, NewFile(3, "The Hobbit.mobi", Quality.MOBI));

            var plan = TalearrEpubService.PlanSources(_files);

            Assert.That(plan.Single().Id, Is.EqualTo(1));
        }

        [Test]
        public void pdf_and_audio_files_are_not_converted()
        {
            var files = new List<BookFile>
            {
                NewFile(4, "Other.pdf", Quality.PDF, 90),
                new BookFile { Id = 5, Path = Path.Combine(_folder, "Book.mobi"), EditionId = 91, MediaType = "audiobook" }
            };

            Assert.That(TalearrEpubService.PlanSources(files), Is.Empty);
        }

        [Test]
        public void import_of_a_kindle_file_queues_a_conversion_only_when_turned_on()
        {
            var imported = new BookImportedEvent(_book.Author, _book, _files, new List<BookFile>(), true, null);

            Service().Handle(imported);
            Assert.That(_pushed, Is.Empty);

            _settings.Settings.ConvertAfterImport = true;
            Service().Handle(imported);

            var command = (TalearrConvertToEpubCommand)_pushed.Single();
            Assert.That(command.BookId, Is.EqualTo(6001));
            Assert.That(command.AfterImport, Is.True);
        }

        [Test]
        public void import_of_an_epub_does_not_queue_a_conversion()
        {
            _settings.Settings.ConvertAfterImport = true;
            var epub = NewFile(6, "The Hobbit.epub", Quality.EPUB);

            Service().Handle(new BookImportedEvent(_book.Author, _book, new List<BookFile> { epub }, new List<BookFile>(), true, null));

            Assert.That(_pushed, Is.Empty);
        }

        [Test]
        public void library_run_needs_the_converter_before_touching_anything()
        {
            _converter.Available = false;

            Assert.Throws<TalearrEpubException>(() => Service().Execute(new TalearrConvertLibraryToEpubCommand()));
            Assert.That(_converter.Inputs, Is.Empty);
        }

        [Test]
        public void library_run_converts_kindle_ebooks_without_an_epub()
        {
            Service().Execute(new TalearrConvertLibraryToEpubCommand());

            Assert.That(_converter.Inputs, Is.EqualTo(new[] { _files[0].Path }));
            Assert.That(_added.Single().Quality.Quality, Is.EqualTo(Quality.EPUB));
        }
    }
}
