using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using NLog;
using NUnit.Framework;
using NzbDrone.Core.Books;
using NzbDrone.Core.Configuration;
using NzbDrone.Core.MediaFiles;
using NzbDrone.Core.MediaFiles.Events;
using NzbDrone.Core.Messaging.Commands;
using NzbDrone.Core.Qualities;
using NzbDrone.Core.Talearr.Audiobook.M4b;

namespace Chaptarr.Core.Test.Talearr.Audiobook
{
    [TestFixture]
    public class TalearrM4bServiceFixture
    {
        private sealed class FakeConverter : ITalearrM4bConverter
        {
            public bool Available { get; set; } = true;
            public Func<M4bConversionJob, M4bConversionOutcome> Result { get; set; }
            public List<M4bConversionJob> Jobs { get; } = new List<M4bConversionJob>();

            public bool IsAvailable() => Available;

            public M4bConversionOutcome Convert(M4bConversionJob job, Action<double, string> progress = null)
            {
                Jobs.Add(job);
                progress?.Invoke(50, "50%");
                return Result(job);
            }
        }

        private sealed class FakeSettings : ITalearrM4bSettingsService
        {
            public TalearrM4bSettings Settings { get; set; } = new TalearrM4bSettings();
            public TalearrM4bSettings Get() => Settings.Clone();
            public TalearrM4bSettings Save(TalearrM4bSettings settings) => Settings = settings;
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
        private Func<BookFile, BookFile> _add;

        [SetUp]
        public void SetUp()
        {
            _folder = Path.Combine(Path.GetTempPath(), "talearr-m4b-svc-" + Guid.NewGuid().ToString("N"), "Animal Farm");
            Directory.CreateDirectory(Path.Combine(_folder, "CD1"));
            Directory.CreateDirectory(Path.Combine(_folder, "CD2"));

            var author = new Author { Id = 11, Name = "George Orwell" };
            _book = new Book { Id = 5003, Title = "Animal Farm", Author = author, AuthorId = 11, ReleaseDate = new DateTime(1945, 8, 17), Overview = "<p>All animals are <b>equal</b>.</p>" };
            _edition = new Edition { Id = 77, Title = "Animal Farm", Book = _book, Narrator = "Ralph Cosham", Publisher = "Blackstone" };

            _files = new List<BookFile>
            {
                NewFile(Path.Combine(_folder, "CD1", "01.mp3"), 1),
                NewFile(Path.Combine(_folder, "CD2", "01.mp3"), 2)
            };

            _events = new List<string>();
            _added = new List<BookFile>();
            _deletedRows = new List<BookFile>();
            _recycled = new List<string>();
            _pushed = new List<Command>();
            _converter = new FakeConverter { Result = Success };
            _settings = new FakeSettings();
            _add = f =>
            {
                _events.Add("add");
                _added.Add(f);
                return f;
            };
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

        private BookFile NewFile(string path, int part)
        {
            File.WriteAllText(path, "audio");
            return new BookFile
            {
                Id = part,
                Path = path,
                Part = part,
                PartCount = 2,
                EditionId = 77,
                MediaType = "audiobook",
                Narrator = "Ralph Cosham",
                ReleaseGroup = "grp",
                Quality = new QualityModel(Quality.MP3, new Revision(2))
            };
        }

        private M4bConversionOutcome Success(M4bConversionJob job)
        {
            File.WriteAllText(job.OutputPath, "m4b");
            return new M4bConversionOutcome { Success = true, OutputPath = job.OutputPath, OutputSize = 3, DurationSeconds = 3600, ExpectedDurationSeconds = 3600, ChapterCount = 2 };
        }

        private TalearrM4bService Service()
        {
            var (books, bookFake) = Fake<IBookService>.Create();
            bookFake.On(nameof(IBookService.GetBook), _ => _book);

            var (authors, _) = Fake<IAuthorService>.Create();

            var (editions, editionFake) = Fake<IEditionService>.Create();
            editionFake.On(nameof(IEditionService.GetEdition), _ => _edition);

            var (media, mediaFake) = Fake<IMediaFileService>.Create();
            mediaFake.On(nameof(IMediaFileService.GetFilesByBook), _ => _files.ToList())
                .On(nameof(IMediaFileService.Add), args => _add((BookFile)args[0]))
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

            var (config, configFake) = Fake<IConfigService>.Create();
            configFake.On("get_" + nameof(IConfigService.AudiobookConversionMaxBitrate), _ => 64)
                .On("get_" + nameof(IConfigService.AudiobookConversionNoUpscale), _ => true)
                .On("get_" + nameof(IConfigService.AudiobookConversionAudioChannels), _ => "source");

            var (queue, queueFake) = Fake<IManageCommandQueue>.Create();
            queueFake.On("Push", args =>
            {
                _pushed.Add((Command)args[0]);
                return null;
            });

            return new TalearrM4bService(books, authors, editions, media, recycle, config, _settings, _converter, queue, LogManager.GetCurrentClassLogger());
        }

        [Test]
        public void merged_m4b_is_named_after_the_book_not_the_edition_listing()
        {
            _book.Title = "1984";
            _edition.Title = "Nineteen Eighty-Four (MP3 Book)";

            Service().Execute(new TalearrConvertToM4bCommand { BookId = 5003 });

            Assert.That(_added.Single().Path, Is.EqualTo(Path.Combine(_folder, "1984.m4b")));
            Assert.That(_converter.Jobs.Single().Metadata.Title, Is.EqualTo("1984"));
        }

        [Test]
        public void verified_m4b_replaces_the_files_and_originals_go_last()
        {
            Service().Execute(new TalearrConvertToM4bCommand { BookId = 5003 });

            Assert.That(_events, Is.EqualTo(new[] { "add", "deleteRows", "recycle", "recycle" }));

            var merged = _added.Single();
            Assert.That(merged.Path, Is.EqualTo(Path.Combine(_folder, "Animal Farm.m4b")));
            Assert.That(merged.Quality.Quality, Is.EqualTo(Quality.M4B));
            Assert.That(merged.Quality.Revision.Version, Is.EqualTo(2));
            Assert.That(merged.EditionId, Is.EqualTo(77));
            Assert.That(merged.Part, Is.EqualTo(1));
            Assert.That(merged.PartCount, Is.EqualTo(1));
            Assert.That(merged.DurationSeconds, Is.EqualTo(3600));
            Assert.That(merged.Narrator, Is.EqualTo("Ralph Cosham"));
            Assert.That(merged.ReleaseGroup, Is.EqualTo("grp"));

            Assert.That(_deletedRows.Select(f => f.Id), Is.EquivalentTo(new[] { 1, 2 }));
            Assert.That(_recycled, Is.EquivalentTo(_files.Select(f => f.Path)));
            Assert.That(Directory.Exists(Path.Combine(_folder, "CD1")), Is.False, "emptied disc folders are removed");
        }

        [Test]
        public void job_has_ordered_inputs_book_tags_and_the_folder_cover()
        {
            File.WriteAllText(Path.Combine(_folder, "cover.jpg"), "jpg");
            _files.Reverse();

            Service().Execute(new TalearrConvertToM4bCommand { BookId = 5003 });

            var job = _converter.Jobs.Single();
            Assert.That(job.Inputs, Is.EqualTo(new[] { Path.Combine(_folder, "CD1", "01.mp3"), Path.Combine(_folder, "CD2", "01.mp3") }));
            Assert.That(job.CoverPath, Is.EqualTo(Path.Combine(_folder, "cover.jpg")));
            Assert.That(job.Metadata.Title, Is.EqualTo("Animal Farm"));
            Assert.That(job.Metadata.Author, Is.EqualTo("George Orwell"));
            Assert.That(job.Metadata.Narrator, Is.EqualTo("Ralph Cosham"));
            Assert.That(job.Metadata.Year, Is.EqualTo("1945"));
            Assert.That(job.Metadata.Publisher, Is.EqualTo("Blackstone"));
            Assert.That(job.Metadata.Description, Is.EqualTo("All animals are equal."));
            Assert.That(job.Encode.BitrateKbps, Is.EqualTo(64));
        }

        [Test]
        public void m4b_date_is_the_recording_year_when_known()
        {
            _edition.ReleaseDate = new DateTime(2008, 3, 1);

            Service().Execute(new TalearrConvertToM4bCommand { BookId = 5003 });

            Assert.That(_converter.Jobs.Single().Metadata.Year, Is.EqualTo("2008"));
        }

        [Test]
        public void failed_conversion_keeps_every_original()
        {
            _converter.Result = _ => M4bConversionOutcome.Fail("The converted M4B is 10 s long but the source files add up to 3600 s (more than 1% apart).");

            var ex = Assert.Throws<TalearrM4bException>(() => Service().Execute(new TalearrConvertToM4bCommand { BookId = 5003 }));

            Assert.That(ex.Message, Does.Contain("original files were kept"));
            Assert.That(_events, Is.Empty);
            Assert.That(_files.All(f => File.Exists(f.Path)), Is.True);
        }

        [Test]
        public void a_database_error_keeps_the_originals_and_removes_the_new_file()
        {
            _add = _ => throw new InvalidOperationException("db locked");

            Assert.Throws<InvalidOperationException>(() => Service().Execute(new TalearrConvertToM4bCommand { BookId = 5003 }));

            Assert.That(_deletedRows, Is.Empty);
            Assert.That(_recycled, Is.Empty);
            Assert.That(File.Exists(Path.Combine(_folder, "Animal Farm.m4b")), Is.False);
            Assert.That(_files.All(f => File.Exists(f.Path)), Is.True);
        }

        [Test]
        public void single_file_books_are_left_alone()
        {
            _files = new List<BookFile> { _files[0] };

            Service().Execute(new TalearrConvertToM4bCommand { BookId = 5003 });

            Assert.That(_converter.Jobs, Is.Empty);
            Assert.That(_events, Is.Empty);
        }

        [Test]
        public void missing_ffmpeg_fails_the_command_without_touching_files()
        {
            _converter.Available = false;

            Assert.Throws<TalearrM4bException>(() => Service().Execute(new TalearrConvertToM4bCommand { BookId = 5003 }));
            Assert.That(_events, Is.Empty);
        }

        [Test]
        public void after_import_conversion_is_off_by_default()
        {
            Assert.That(new TalearrM4bSettings().ConvertAfterImport, Is.False);

            Service().Handle(new BookImportedEvent(_book.Author, _book, _files, new List<BookFile>(), true, null));

            Assert.That(_pushed, Is.Empty);
        }

        [Test]
        public void after_import_conversion_queues_a_command_when_enabled()
        {
            _settings.Settings.ConvertAfterImport = true;

            Service().Handle(new BookImportedEvent(_book.Author, _book, _files, new List<BookFile>(), true, null));

            var command = (TalearrConvertToM4bCommand)_pushed.Single();
            Assert.That(command.BookId, Is.EqualTo(5003));
            Assert.That(command.AfterImport, Is.True);
        }

        [Test]
        public void after_import_conversion_skips_a_single_m4b()
        {
            _settings.Settings.ConvertAfterImport = true;
            var single = new List<BookFile> { new BookFile { Path = Path.Combine(_folder, "Book.m4b") } };

            Service().Handle(new BookImportedEvent(_book.Author, _book, single, new List<BookFile>(), true, null));

            Assert.That(_pushed, Is.Empty);
        }
    }
}
