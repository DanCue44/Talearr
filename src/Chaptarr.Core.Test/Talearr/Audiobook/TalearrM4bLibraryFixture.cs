using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using NLog;
using NUnit.Framework;
using NzbDrone.Common.Messaging;
using NzbDrone.Core.Books;
using NzbDrone.Core.Configuration;
using NzbDrone.Core.MediaFiles;
using NzbDrone.Core.MediaFiles.Events;
using NzbDrone.Core.Messaging.Commands;
using NzbDrone.Core.Messaging.Events;
using NzbDrone.Core.Qualities;
using NzbDrone.Core.Talearr.Audiobook.M4b;

namespace Chaptarr.Core.Test.Talearr.Audiobook
{
    // TaleArr: "Convert library to M4B" over the existing library, with fakes only (no ffmpeg).
    [TestFixture]
    public class TalearrM4bLibraryFixture
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
                return Result(job);
            }
        }

        private sealed class FakeSettings : ITalearrM4bSettingsService
        {
            public TalearrM4bSettings Get() => new TalearrM4bSettings();
            public TalearrM4bSettings Save(TalearrM4bSettings settings) => settings;
        }

        private string _root;
        private Author _author;
        private List<Book> _books;
        private Dictionary<int, List<BookFile>> _files;
        private List<BookFile> _added;
        private List<BookFile> _deletedRows;
        private List<string> _recycled;
        private List<IEvent> _events;
        private FakeConverter _converter;

        [SetUp]
        public void SetUp()
        {
            _root = Path.Combine(Path.GetTempPath(), "talearr-m4b-lib-" + Guid.NewGuid().ToString("N"));
            _author = new Author { Id = 3, Name = "Ursula K. Le Guin" };

            _books = new List<Book>
            {
                new Book { Id = 1, Title = "A Wizard of Earthsea", AuthorId = 3, Author = _author, MediaType = BookMediaType.Audiobook },
                new Book { Id = 2, Title = "The Dispossessed", AuthorId = 3, Author = _author, MediaType = BookMediaType.Audiobook },
                new Book { Id = 3, Title = "The Lathe of Heaven", AuthorId = 3, Author = _author, MediaType = BookMediaType.Audiobook },
                new Book { Id = 4, Title = "The Left Hand of Darkness", AuthorId = 3, Author = _author, MediaType = BookMediaType.Ebook }
            };

            _files = new Dictionary<int, List<BookFile>>
            {
                [1] = Files(1, "A Wizard of Earthsea", 3),
                [2] = Files(2, "The Dispossessed", 2),
                [3] = Files(3, "The Lathe of Heaven", 1),
                [4] = new List<BookFile>()
            };

            _added = new List<BookFile>();
            _deletedRows = new List<BookFile>();
            _recycled = new List<string>();
            _events = new List<IEvent>();
            _converter = new FakeConverter { Result = Success };
        }

        [TearDown]
        public void TearDown()
        {
            if (Directory.Exists(_root))
            {
                Directory.Delete(_root, true);
            }
        }

        private List<BookFile> Files(int bookId, string title, int count)
        {
            var folder = Path.Combine(_root, title);
            Directory.CreateDirectory(folder);

            return Enumerable.Range(1, count).Select(i =>
            {
                var path = Path.Combine(folder, $"{i:00}.mp3");
                File.WriteAllText(path, "audio");
                return new BookFile
                {
                    Id = (bookId * 100) + i,
                    Path = path,
                    Part = i,
                    PartCount = count,
                    EditionId = bookId * 10,
                    MediaType = "audiobook",
                    Quality = new QualityModel(Quality.MP3)
                };
            }).ToList();
        }

        private static M4bConversionOutcome Success(M4bConversionJob job)
        {
            File.WriteAllText(job.OutputPath, "m4b");
            return new M4bConversionOutcome { Success = true, OutputPath = job.OutputPath, OutputSize = 3, DurationSeconds = 3600, ExpectedDurationSeconds = 3600, ChapterCount = 2 };
        }

        private TalearrM4bService Service()
        {
            var (books, bookFake) = Fake<IBookService>.Create();
            bookFake.On(nameof(IBookService.GetBook), args => _books.Single(b => b.Id == (int)args[0]))
                .On(nameof(IBookService.GetBooksByAuthor), args => _books.Where(b => b.AuthorId == (int)args[0]).ToList());

            var (authors, authorFake) = Fake<IAuthorService>.Create();
            authorFake.On(nameof(IAuthorService.GetAllAuthors), _ => new List<Author> { _author })
                .On(nameof(IAuthorService.GetAuthor), _ => _author);

            var (editions, editionFake) = Fake<IEditionService>.Create();
            editionFake.On(nameof(IEditionService.GetEdition), _ => null);

            var (media, mediaFake) = Fake<IMediaFileService>.Create();
            mediaFake.On(nameof(IMediaFileService.GetFilesByBook), args => _files[(int)args[0]].ToList())
                .On(nameof(IMediaFileService.Add), args =>
                {
                    _added.Add((BookFile)args[0]);
                    return args[0];
                })
                .On(nameof(IMediaFileService.DeleteMany), args =>
                {
                    _deletedRows.AddRange((List<BookFile>)args[0]);
                    return null;
                });

            var (recycle, recycleFake) = Fake<IRecycleBinProvider>.Create();
            recycleFake.On(nameof(IRecycleBinProvider.DeleteFile), args =>
            {
                _recycled.Add((string)args[0]);
                File.Delete((string)args[0]);
                return null;
            });

            var (config, configFake) = Fake<IConfigService>.Create();
            configFake.On("get_" + nameof(IConfigService.AudiobookConversionMaxBitrate), _ => 64)
                .On("get_" + nameof(IConfigService.AudiobookConversionNoUpscale), _ => true)
                .On("get_" + nameof(IConfigService.AudiobookConversionAudioChannels), _ => "source");

            var (queue, _) = Fake<IManageCommandQueue>.Create();

            var (events, eventFake) = Fake<IEventAggregator>.Create();
            eventFake.On(nameof(IEventAggregator.PublishEvent), args =>
            {
                _events.Add((IEvent)args[0]);
                return null;
            });

            return new TalearrM4bService(books, authors, editions, media, recycle, config, new FakeSettings(), _converter, queue, LogManager.GetCurrentClassLogger(), events);
        }

        [Test]
        public void finds_only_multi_file_audiobooks()
        {
            var candidates = Service().FindLibraryCandidates(0);

            Assert.That(candidates.Select(c => c.Book.Id), Is.EqualTo(new[] { 1, 2 }), "single-file and ebook rows are skipped");
        }

        [Test]
        public void converts_each_candidate_and_notifies_like_an_import()
        {
            Service().Execute(new TalearrConvertLibraryToM4bCommand());

            Assert.That(_converter.Jobs.Count, Is.EqualTo(2));
            Assert.That(_added.Select(f => f.Quality.Quality), Is.All.EqualTo(Quality.M4B));
            Assert.That(_deletedRows.Count, Is.EqualTo(5));
            Assert.That(_recycled.Count, Is.EqualTo(5));

            var imported = _events.OfType<BookImportedEvent>().ToList();
            Assert.That(imported.Select(e => e.Book.Id), Is.EqualTo(new[] { 1, 2 }));
            Assert.That(imported.All(e => e.NewDownload && e.ImportedBooks.Count == 1 && e.OldFiles.Any()), Is.True);
        }

        [Test]
        public void missing_ffmpeg_changes_nothing()
        {
            _converter.Available = false;

            var ex = Assert.Throws<TalearrM4bException>(() => Service().Execute(new TalearrConvertLibraryToM4bCommand()));

            Assert.That(ex.Message, Does.Contain("Nothing was changed"));
            Assert.That(_converter.Jobs, Is.Empty);
            Assert.That(_added, Is.Empty);
            Assert.That(_recycled, Is.Empty);
        }

        [Test]
        public void a_failed_book_keeps_its_files_and_the_run_continues()
        {
            _converter.Result = job => job.Inputs.Count == 3 ? M4bConversionOutcome.Fail("FFmpeg failed: boom") : Success(job);

            var ex = Assert.Throws<TalearrM4bException>(() => Service().Execute(new TalearrConvertLibraryToM4bCommand()));

            Assert.That(ex.Message, Does.Contain("Converted 1 of 2"));
            Assert.That(_files[1].All(f => File.Exists(f.Path)), Is.True, "the failed book keeps every original");
            Assert.That(_deletedRows.Select(f => f.Id), Is.EquivalentTo(_files[2].Select(f => f.Id)));
            Assert.That(_events.OfType<BookImportedEvent>().Single().Book.Id, Is.EqualTo(2));
        }

        [Test]
        public void a_duration_mismatch_keeps_the_originals_and_removes_the_m4b()
        {
            _converter.Result = job =>
            {
                File.WriteAllText(job.OutputPath, "m4b");
                return new M4bConversionOutcome { Success = true, OutputPath = job.OutputPath, OutputSize = 3, DurationSeconds = 1200, ExpectedDurationSeconds = 3600, ChapterCount = 2 };
            };

            Assert.Throws<TalearrM4bException>(() => Service().Execute(new TalearrConvertLibraryToM4bCommand()));

            Assert.That(_added, Is.Empty);
            Assert.That(_deletedRows, Is.Empty);
            Assert.That(_recycled, Is.Empty);
            Assert.That(_converter.Jobs.All(j => !File.Exists(j.OutputPath)), Is.True, "the unverified M4B is removed");
            Assert.That(_files[1].Concat(_files[2]).All(f => File.Exists(f.Path)), Is.True);
        }

        [Test]
        public void a_database_error_on_one_book_keeps_its_files()
        {
            var calls = 0;
            _converter.Result = job =>
            {
                calls++;
                return Success(job);
            };

            // First Add throws (book 1), second succeeds (book 2).
            var (media, mediaFake) = Fake<IMediaFileService>.Create();
            mediaFake.On(nameof(IMediaFileService.GetFilesByBook), args => _files[(int)args[0]].ToList())
                .On(nameof(IMediaFileService.Add), args =>
                {
                    if (_added.Count == 0 && ((BookFile)args[0]).Path.Contains("Earthsea"))
                    {
                        throw new InvalidOperationException("db locked");
                    }

                    _added.Add((BookFile)args[0]);
                    return args[0];
                })
                .On(nameof(IMediaFileService.DeleteMany), args =>
                {
                    _deletedRows.AddRange((List<BookFile>)args[0]);
                    return null;
                });

            var service = Rebuild(media);

            var ex = Assert.Throws<TalearrM4bException>(() => service.Execute(new TalearrConvertLibraryToM4bCommand()));

            Assert.That(calls, Is.EqualTo(2));
            Assert.That(ex.Message, Does.Contain("db locked"));
            Assert.That(_files[1].All(f => File.Exists(f.Path)), Is.True);
            Assert.That(File.Exists(Path.Combine(_root, "A Wizard of Earthsea", "A Wizard of Earthsea.m4b")), Is.False);
            Assert.That(_added.Single().Path, Does.Contain("Dispossessed"));
        }

        [Test]
        public void one_author_only_when_asked()
        {
            var other = new Author { Id = 9, Name = "Other" };
            _books.Add(new Book { Id = 9, Title = "Elsewhere", AuthorId = 9, Author = other, MediaType = BookMediaType.Audiobook });
            _files[9] = Files(9, "Elsewhere", 2);

            var candidates = Service().FindLibraryCandidates(3);

            Assert.That(candidates.Select(c => c.Book.Id), Is.EqualTo(new[] { 1, 2 }));
        }

        private TalearrM4bService Rebuild(IMediaFileService media)
        {
            var (books, bookFake) = Fake<IBookService>.Create();
            bookFake.On(nameof(IBookService.GetBooksByAuthor), args => _books.Where(b => b.AuthorId == (int)args[0]).ToList());

            var (authors, authorFake) = Fake<IAuthorService>.Create();
            authorFake.On(nameof(IAuthorService.GetAllAuthors), _ => new List<Author> { _author });

            var (editions, editionFake) = Fake<IEditionService>.Create();
            editionFake.On(nameof(IEditionService.GetEdition), _ => null);

            var (recycle, recycleFake) = Fake<IRecycleBinProvider>.Create();
            recycleFake.On(nameof(IRecycleBinProvider.DeleteFile), args =>
            {
                _recycled.Add((string)args[0]);
                File.Delete((string)args[0]);
                return null;
            });

            var (config, configFake) = Fake<IConfigService>.Create();
            configFake.On("get_" + nameof(IConfigService.AudiobookConversionMaxBitrate), _ => 64)
                .On("get_" + nameof(IConfigService.AudiobookConversionNoUpscale), _ => true)
                .On("get_" + nameof(IConfigService.AudiobookConversionAudioChannels), _ => "source");

            var (queue, _) = Fake<IManageCommandQueue>.Create();
            var (events, eventFake) = Fake<IEventAggregator>.Create();
            eventFake.On(nameof(IEventAggregator.PublishEvent), args =>
            {
                _events.Add((IEvent)args[0]);
                return null;
            });

            return new TalearrM4bService(books, authors, editions, media, recycle, config, new FakeSettings(), _converter, queue, LogManager.GetCurrentClassLogger(), events);
        }
    }
}
