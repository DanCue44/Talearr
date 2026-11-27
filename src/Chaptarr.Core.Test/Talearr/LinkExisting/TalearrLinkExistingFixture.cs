using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using NLog;
using NUnit.Framework;
using NzbDrone.Common.Disk;
using NzbDrone.Core.Books;
using NzbDrone.Core.MediaFiles;
using NzbDrone.Core.MediaFiles.BookImport;
using NzbDrone.Core.MediaFiles.BookImport.Manual;
using NzbDrone.Core.Messaging.Commands;
using NzbDrone.Core.Talearr.Audiobookshelf;
using NzbDrone.Core.Talearr.LinkExisting;

namespace Chaptarr.Core.Test.Talearr.LinkExisting
{
    // Talearr: Link Existing Files in the Add Book modal (issue #13).
    [TestFixture]
    public class TalearrLinkExistingFixture
    {
        private const string BookTitle = "The Constable: The Complete Series: The Constable, Books 1-2";

        private string _root;
        private string _absFolder;
        private string _authorFolder;
        private List<TalearrAbsItem> _absItems;
        private List<string> _knownFiles;
        private List<Command> _pushed;
        private Book _book;
        private List<Edition> _editions;

        public class ServiceProxy<T> : DispatchProxy
            where T : class
        {
            public Func<MethodInfo, object[], object> Handler { get; set; }

            protected override object Invoke(MethodInfo targetMethod, object[] args)
            {
                return Handler(targetMethod, args);
            }
        }

        private static T Proxy<T>(Func<MethodInfo, object[], object> handler)
            where T : class
        {
            var proxy = DispatchProxy.Create<T, ServiceProxy<T>>();
            ((ServiceProxy<T>)(object)proxy).Handler = handler;
            return proxy;
        }

        [SetUp]
        public void SetUp()
        {
            _root = Path.Combine(TestContext.CurrentContext.WorkDirectory, $"talearr_link_{Guid.NewGuid():N}");
            _absFolder = Path.Combine(_root, "abs", "Sam Author", "The Constable - The Complete Series");
            _authorFolder = Path.Combine(_root, "audiobooks", "Sam Author");

            Directory.CreateDirectory(_absFolder);
            File.WriteAllText(Path.Combine(_absFolder, "Part 01.mp3"), "a");
            File.WriteAllText(Path.Combine(_absFolder, "Part 02.mp3"), "b");
            File.WriteAllText(Path.Combine(_absFolder, "cover.jpg"), "c");

            Directory.CreateDirectory(Path.Combine(_authorFolder, "The Constable Complete Series"));
            File.WriteAllText(Path.Combine(_authorFolder, "The Constable Complete Series", "book.m4b"), "d");
            Directory.CreateDirectory(Path.Combine(_authorFolder, "Some Other Book"));
            File.WriteAllText(Path.Combine(_authorFolder, "Some Other Book", "other.m4b"), "e");

            _absItems = new List<TalearrAbsItem>
            {
                new TalearrAbsItem
                {
                    ConnectionId = 7,
                    ItemId = "li_1",
                    Title = "The Constable: The Complete Series",
                    Authors = "Sam Author",
                    Narrators = "Pat Reader",
                    DurationSeconds = 50400,
                    HasAudio = true,
                    Path = "/audiobooks/Sam Author/The Constable - The Complete Series",
                    LocalPath = _absFolder
                },
                new TalearrAbsItem
                {
                    ConnectionId = 7,
                    ItemId = "li_2",
                    Title = "Unrelated Story",
                    Authors = "Sam Author",
                    HasAudio = true,
                    LocalPath = _absFolder
                },
                new TalearrAbsItem
                {
                    ConnectionId = 7,
                    ItemId = "li_3",
                    Title = "The Constable: The Complete Series",
                    Authors = "Someone Else",
                    HasAudio = true,
                    LocalPath = _absFolder
                }
            };

            _knownFiles = new List<string>();
            _pushed = new List<Command>();
            _book = new Book { Id = 42, AuthorId = 3, Title = BookTitle, MediaType = BookMediaType.Audiobook };
            _editions = new List<Edition>
            {
                new Edition { Id = 100, BookId = 42, Monitored = false },
                new Edition { Id = 101, BookId = 42, Monitored = true }
            };
        }

        [TearDown]
        public void TearDown()
        {
            try
            {
                Directory.Delete(_root, true);
            }
            catch
            {
            }
        }

        private TalearrLinkExistingService Subject()
        {
            var author = new Author { Id = 3, Name = "Sam Author", Path = _authorFolder, AudiobookPath = _authorFolder };

            var absStore = Proxy<ITalearrAbsStore>((m, a) => m.Name == nameof(ITalearrAbsStore.GetItems) ? _absItems : throw new NotImplementedException(m.Name));
            var connections = Proxy<ITalearrAbsConnections>((m, a) => new List<TalearrAbsConnection> { new TalearrAbsConnection { Id = 7, Name = "Home ABS" } });
            var authors = Proxy<IAuthorService>((m, a) => m.Name switch
            {
                nameof(IAuthorService.GetAuthor) => author,
                nameof(IAuthorService.FindByName) => string.Equals((string)a[0], author.Name, StringComparison.OrdinalIgnoreCase) ? author : null,
                _ => throw new NotImplementedException(m.Name)
            });
            var books = Proxy<IBookService>((m, a) => m.Name == nameof(IBookService.GetBook) ? _book : throw new NotImplementedException(m.Name));
            var editions = Proxy<IEditionService>((m, a) => m.Name == nameof(IEditionService.GetEditionsByBook) ? _editions : throw new NotImplementedException(m.Name));
            var mediaFiles = Proxy<IMediaFileService>((m, a) =>
            {
                if (m.Name == nameof(IMediaFileService.GetFileWithPath) && a[0] is List<string> paths)
                {
                    return paths.Where(p => _knownFiles.Contains(p)).Select(p => new BookFile { Path = p }).ToList();
                }

                throw new NotImplementedException(m.Name);
            });
            var disk = Proxy<IDiskProvider>((m, a) => m.Name switch
            {
                nameof(IDiskProvider.FileExists) => File.Exists((string)a[0]),
                nameof(IDiskProvider.FolderExists) => Directory.Exists((string)a[0]),
                nameof(IDiskProvider.GetDirectories) => Directory.GetDirectories((string)a[0]),
                nameof(IDiskProvider.GetFiles) => Directory.GetFiles((string)a[0], "*", (bool)a[1] ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly),
                _ => throw new NotImplementedException(m.Name)
            });
            var commands = Proxy<IManageCommandQueue>((m, a) =>
            {
                if (m.Name == nameof(IManageCommandQueue.Push) && a[0] is Command command)
                {
                    _pushed.Add(command);
                    return new CommandModel { Id = 77, Body = command };
                }

                throw new NotImplementedException(m.Name);
            });

            return new TalearrLinkExistingService(absStore, connections, authors, books, editions, mediaFiles, disk, commands, LogManager.GetCurrentClassLogger());
        }

        private static TalearrLinkCandidateRequest Request(string title = BookTitle, params string[] asins) => new TalearrLinkCandidateRequest
        {
            Title = title,
            AuthorName = "Sam Author",
            Asins = asins.ToList()
        };

        [Test]
        public void complete_series_item_is_a_strong_match_for_the_two_book_set()
        {
            var match = TalearrLinkMatch.Score(Request(), "The Constable: The Complete Series", null, "Sam Author", null, null);

            Assert.That(match, Is.Not.Null);
            Assert.That(match.Strong, Is.True);
            Assert.That(match.MatchedBy, Is.EqualTo("title"));
        }

        [Test]
        public void book_one_alone_is_not_offered_for_the_set()
        {
            Assert.That(TalearrLinkMatch.Score(Request(), "The Constable", null, "Sam Author", null, null), Is.Null);
        }

        [Test]
        public void another_author_never_matches_on_title()
        {
            Assert.That(TalearrLinkMatch.Score(Request(), "The Constable: The Complete Series", null, "Someone Else", null, null), Is.Null);
        }

        [Test]
        public void same_asin_is_strong_even_with_another_title()
        {
            var match = TalearrLinkMatch.Score(Request("Anything", "B0ASIN1"), "Different Name", null, "Someone Else", "b0asin1", null);

            Assert.That(match.Strong, Is.True);
            Assert.That(match.MatchedBy, Is.EqualTo("asin"));
        }

        [Test]
        public void a_partial_title_is_offered_but_not_preselected()
        {
            var match = TalearrLinkMatch.Score(Request("The Lich Priest: Viridian Gate Online"), "Lich Priest", null, "Sam Author", null, null);

            Assert.That(match, Is.Not.Null);
            Assert.That(match.Strong, Is.False);
        }

        [Test]
        public void a_word_prefix_inside_another_word_is_not_strong()
        {
            var match = TalearrLinkMatch.Score(Request("The Way of Kings"), "The Way of Kingsmen", null, "Sam Author", null, null);

            Assert.That(match?.Strong ?? false, Is.False);
        }

        [Test]
        public void author_punctuation_and_co_authors_do_not_matter()
        {
            Assert.That(TalearrLinkMatch.AuthorFits("R. F. Kuang", "R.F. Kuang"), Is.True);
            Assert.That(TalearrLinkMatch.AuthorFits("Brandon Sanderson", "Brandon Sanderson, Janci Patterson"), Is.True);
            Assert.That(TalearrLinkMatch.AuthorFits("Brandon Sanderson", "Brandon Mull"), Is.False);
        }

        [Test]
        public void candidates_come_from_audiobookshelf_and_the_author_folder_best_first()
        {
            var candidates = Subject().FindCandidates(Request());

            Assert.That(candidates.Select(c => c.Path), Is.EqualTo(new[]
            {
                _absFolder,
                Path.Combine(_authorFolder, "The Constable Complete Series")
            }));

            var abs = candidates[0];
            Assert.That(abs.Source, Is.EqualTo(TalearrLinkCandidate.SourceAudiobookshelf));
            Assert.That(abs.SourceName, Is.EqualTo("Home ABS"));
            Assert.That(abs.DurationSeconds, Is.EqualTo(50400));
            Assert.That(abs.FileCount, Is.EqualTo(2), "audio files only");
            Assert.That(abs.Strong, Is.True);

            Assert.That(candidates[1].Source, Is.EqualTo(TalearrLinkCandidate.SourceAuthorFolder));
            Assert.That(candidates[1].Strong, Is.False, "only the best strong match is preselected");
        }

        [Test]
        public void folders_already_in_the_library_are_not_offered()
        {
            _knownFiles.AddRange(Directory.GetFiles(_absFolder, "*.mp3"));

            var candidates = Subject().FindCandidates(Request());

            Assert.That(candidates.Select(c => c.Path), Does.Not.Contain(_absFolder));
        }

        [Test]
        public void nothing_matching_returns_no_candidates()
        {
            Assert.That(Subject().FindCandidates(Request("A Completely Different Book")), Is.Empty);
        }

        [Test]
        public void link_queues_a_manual_import_of_the_audio_files_to_the_monitored_edition()
        {
            _knownFiles.Add(Path.Combine(_absFolder, "Part 02.mp3"));

            var result = Subject().Link(42, new[] { _absFolder });

            Assert.That(result.FileCount, Is.EqualTo(1));
            Assert.That(result.SkippedCount, Is.EqualTo(1));
            Assert.That(result.CommandId, Is.EqualTo(77));

            var command = _pushed.Single() as ManualImportCommand;
            Assert.That(command, Is.Not.Null);
            Assert.That(command.ImportMode, Is.EqualTo(ImportMode.Copy), "the Manual Import default");
            Assert.That(command.ReplaceExistingFiles, Is.False);

            var file = command.Files.Single();
            Assert.That(file.Path, Is.EqualTo(Path.Combine(_absFolder, "Part 01.mp3")));
            Assert.That(file.AuthorId, Is.EqualTo(3));
            Assert.That(file.BookId, Is.EqualTo(42));
            Assert.That(file.EditionId, Is.EqualTo(101));
            Assert.That(file.DisableReleaseSwitching, Is.True);
            Assert.That(file.Quality.Quality, Is.EqualTo(MediaFileExtensions.GetQualityForExtension(".mp3")));
        }

        [Test]
        public void link_refuses_an_ebook()
        {
            _book.MediaType = BookMediaType.Ebook;

            Assert.Throws<ArgumentException>(() => Subject().Link(42, new[] { _absFolder }));
            Assert.That(_pushed, Is.Empty);
        }

        [Test]
        public void link_with_nothing_new_queues_nothing()
        {
            _knownFiles.AddRange(Directory.GetFiles(_absFolder, "*.mp3"));

            var result = Subject().Link(42, new[] { _absFolder });

            Assert.That(result.FileCount, Is.EqualTo(0));
            Assert.That(result.CommandId, Is.Null);
            Assert.That(_pushed, Is.Empty);
        }
    }
}
