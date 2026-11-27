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
using NzbDrone.Core.MediaFiles.Commands;
using NzbDrone.Core.Messaging.Commands;
using NzbDrone.Core.Queue;
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
        private List<Book> _otherBooks;
        private List<Edition> _otherEditions;
        private Dictionary<string, BookFile> _tracked;
        private List<BookFile> _updated;
        private List<NzbDrone.Core.Queue.Queue> _queue;
        private bool _queueFails;
        private List<CommandModel> _commands;

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

            // Issue #13: the pack's files were attached to book 1 of the series.
            _otherBooks = new List<Book>
            {
                new Book { Id = 9, AuthorId = 3, Title = "Renegade Star", MediaType = BookMediaType.Audiobook },
                new Book { Id = 11, AuthorId = 3, Title = "The Constable", MediaType = BookMediaType.Ebook }
            };
            _otherEditions = new List<Edition>
            {
                new Edition { Id = 200, BookId = 9, Monitored = true },
                new Edition { Id = 300, BookId = 11, Monitored = true }
            };

            _tracked = new Dictionary<string, BookFile>();
            _updated = new List<BookFile>();
            _queue = new List<NzbDrone.Core.Queue.Queue>();
            _queueFails = false;
            _commands = new List<CommandModel>();
        }

        private void Track(string folder, int editionId, string mediaType = "audiobook")
        {
            foreach (var path in Directory.GetFiles(folder, "*.mp3").Concat(Directory.GetFiles(folder, "*.m4b")))
            {
                _tracked[path] = new BookFile { Path = path, EditionId = editionId, MediaType = mediaType };
            }
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
            var allBooks = new[] { _book }.Concat(_otherBooks).ToList();
            var books = Proxy<IBookService>((m, a) => m.Name switch
            {
                nameof(IBookService.GetBook) => _book,
                nameof(IBookService.GetBooks) => allBooks.Where(b => ((IEnumerable<int>)a[0]).Contains(b.Id)).ToList(),
                _ => throw new NotImplementedException(m.Name)
            });
            var editions = Proxy<IEditionService>((m, a) => m.Name switch
            {
                nameof(IEditionService.GetEditionsByBook) => _editions,
                nameof(IEditionService.GetEditions) => _editions.Concat(_otherEditions).Where(e => ((IEnumerable<int>)a[0]).Contains(e.Id)).ToList(),
                _ => throw new NotImplementedException(m.Name)
            });
            var mediaFiles = Proxy<IMediaFileService>((m, a) =>
            {
                if (m.Name == nameof(IMediaFileService.GetFileWithPath) && a[0] is List<string> paths)
                {
                    // Known files sit on the book being linked unless a test tracked them elsewhere.
                    return paths
                        .Where(p => _tracked.ContainsKey(p) || _knownFiles.Contains(p))
                        .Select(p => _tracked.TryGetValue(p, out var file) ? file : new BookFile { Path = p, EditionId = 101 })
                        .ToList();
                }

                if (m.Name == nameof(IMediaFileService.Update) && a[0] is List<BookFile> updated)
                {
                    _updated.AddRange(updated);
                    return null;
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

                if (m.Name == nameof(IManageCommandQueue.All))
                {
                    return _commands;
                }

                throw new NotImplementedException(m.Name);
            });
            var queue = Proxy<IQueueService>((m, a) => m.Name == nameof(IQueueService.GetQueue)
                ? (_queueFails ? throw new InvalidOperationException("queue down") : _queue)
                : throw new NotImplementedException(m.Name));

            return new TalearrLinkExistingService(absStore, connections, authors, books, editions, mediaFiles, disk, commands, queue, LogManager.GetCurrentClassLogger());
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
        public void folders_whose_files_are_on_an_ebook_are_not_offered()
        {
            Track(_absFolder, 300, "ebook");

            var candidates = Subject().FindCandidates(Request());

            Assert.That(candidates.Select(c => c.Path), Does.Not.Contain(_absFolder));
        }

        [Test]
        public void files_on_another_audiobook_are_offered_with_where_they_are_and_not_preselected()
        {
            Track(_absFolder, 200);

            var candidates = Subject().FindCandidates(Request());

            var tracked = candidates.Single(c => c.Path == _absFolder);
            Assert.That(tracked.TrackedElsewhere, Is.True);
            Assert.That(tracked.Strong, Is.False, "tracked files are never preselected");
            Assert.That(tracked.FileCount, Is.EqualTo(2));
            Assert.That(tracked.TrackedOnLabel, Is.EqualTo("In Talearr on: Renegade Star (audiobook)"));
            Assert.That(tracked.TrackedOn.Single().BookId, Is.EqualTo(9));
            Assert.That(tracked.TrackedOn.Single().MediaType, Is.EqualTo("audiobook"));

            var untracked = candidates.Single(c => c.Path == Path.Combine(_authorFolder, "The Constable Complete Series"));
            Assert.That(untracked.TrackedElsewhere, Is.False);
            Assert.That(untracked.TrackedOnLabel, Is.Null);
        }

        [Test]
        public void tracked_candidates_do_not_take_the_preselection_from_an_untracked_strong_match()
        {
            Track(_absFolder, 200);
            Directory.Delete(Path.Combine(_authorFolder, "The Constable Complete Series"), true);
            var strongFolder = Path.Combine(_authorFolder, "The Constable - The Complete Series");
            Directory.CreateDirectory(strongFolder);
            File.WriteAllText(Path.Combine(strongFolder, "book.m4b"), "f");

            var candidates = Subject().FindCandidates(Request());

            Assert.That(candidates.Single(c => c.Path == _absFolder).Strong, Is.False);
            Assert.That(candidates.Single(c => c.Path == strongFolder).Strong, Is.True);
        }

        [Test]
        public void unmapped_files_are_offered_as_unmapped()
        {
            Track(_absFolder, 0);

            var tracked = Subject().FindCandidates(Request()).Single(c => c.Path == _absFolder);

            Assert.That(tracked.TrackedElsewhere, Is.True);
            Assert.That(tracked.TrackedOnLabel, Is.EqualTo("In Talearr as Unmapped Files"));
        }

        [Test]
        public void a_folder_with_some_untracked_files_is_offered_as_before()
        {
            _tracked[Path.Combine(_absFolder, "Part 01.mp3")] = new BookFile { Path = Path.Combine(_absFolder, "Part 01.mp3"), EditionId = 200, MediaType = "audiobook" };

            var candidate = Subject().FindCandidates(Request()).Single(c => c.Path == _absFolder);

            Assert.That(candidate.TrackedElsewhere, Is.False);
            Assert.That(candidate.FileCount, Is.EqualTo(1));
            Assert.That(candidate.Strong, Is.True);
        }

        [Test]
        public void link_moves_files_from_another_audiobook_to_the_monitored_edition()
        {
            Track(_absFolder, 200);

            var result = Subject().Link(42, new[] { _absFolder });

            Assert.That(result.MovedCount, Is.EqualTo(2));
            Assert.That(result.FileCount, Is.EqualTo(0));
            Assert.That(result.RefusedCount, Is.EqualTo(0));
            Assert.That(result.CommandId, Is.Null);
            Assert.That(_pushed, Is.Empty, "a move is database only, no import");
            Assert.That(_updated.Select(f => f.Path), Is.EquivalentTo(Directory.GetFiles(_absFolder, "*.mp3")));
            Assert.That(_updated.Select(f => f.EditionId), Is.All.EqualTo(101));
        }

        [Test]
        public void link_moves_unmapped_files()
        {
            Track(_absFolder, 0);

            var result = Subject().Link(42, new[] { _absFolder });

            Assert.That(result.MovedCount, Is.EqualTo(2));
            Assert.That(_updated.Select(f => f.EditionId), Is.All.EqualTo(101));
        }

        [Test]
        public void link_never_moves_files_off_an_ebook()
        {
            Track(_absFolder, 300);

            var result = Subject().Link(42, new[] { _absFolder });

            Assert.That(result.MovedCount, Is.EqualTo(0));
            Assert.That(result.RefusedCount, Is.EqualTo(2));
            Assert.That(_updated, Is.Empty);
        }

        [Test]
        public void link_never_moves_ebook_files()
        {
            Track(_absFolder, 200, "ebook");

            var result = Subject().Link(42, new[] { _absFolder });

            Assert.That(result.RefusedCount, Is.EqualTo(2));
            Assert.That(_updated, Is.Empty);
        }

        [Test]
        public void link_does_not_move_files_of_a_book_with_a_download_in_the_queue()
        {
            Track(_absFolder, 200);
            _queue.Add(new NzbDrone.Core.Queue.Queue { Title = "Renegade Star", Book = _otherBooks[0] });

            var result = Subject().Link(42, new[] { _absFolder });

            Assert.That(result.MovedCount, Is.EqualTo(0));
            Assert.That(result.RefusedCount, Is.EqualTo(2));
            Assert.That(result.Refusals, Has.All.Contains("download in progress"));
            Assert.That(_updated, Is.Empty);
        }

        [Test]
        public void link_does_not_move_files_with_an_import_in_progress()
        {
            Track(_absFolder, 200);
            _commands.Add(new CommandModel
            {
                Status = CommandStatus.Started,
                Body = new ManualImportCommand { Files = new List<ManualImportFile> { new ManualImportFile { Path = Path.Combine(_absFolder, "Part 01.mp3") } } }
            });
            _commands.Add(new CommandModel
            {
                Status = CommandStatus.Completed,
                Body = new DownloadedBooksScanCommand { Path = _absFolder }
            });

            var result = Subject().Link(42, new[] { _absFolder });

            Assert.That(result.MovedCount, Is.EqualTo(1));
            Assert.That(result.RefusedCount, Is.EqualTo(1));
            Assert.That(_updated.Single().Path, Is.EqualTo(Path.Combine(_absFolder, "Part 02.mp3")));
        }

        [Test]
        public void link_does_not_move_files_inside_a_running_download_scan()
        {
            Track(_absFolder, 200);
            _commands.Add(new CommandModel { Status = CommandStatus.Queued, Body = new DownloadedBooksScanCommand { Path = Path.GetDirectoryName(_absFolder) } });

            var result = Subject().Link(42, new[] { _absFolder });

            Assert.That(result.MovedCount, Is.EqualTo(0));
            Assert.That(result.Refusals, Has.All.Contains("import in progress"));
        }

        [Test]
        public void link_moves_nothing_when_the_queue_cannot_be_read()
        {
            Track(_absFolder, 200);
            _queueFails = true;

            var result = Subject().Link(42, new[] { _absFolder });

            Assert.That(result.MovedCount, Is.EqualTo(0));
            Assert.That(result.RefusedCount, Is.EqualTo(2));
            Assert.That(_updated, Is.Empty);
        }

        [Test]
        public void link_of_a_partly_tracked_folder_imports_only_the_new_files_and_moves_nothing()
        {
            _tracked[Path.Combine(_absFolder, "Part 01.mp3")] = new BookFile { Path = Path.Combine(_absFolder, "Part 01.mp3"), EditionId = 200, MediaType = "audiobook" };

            var result = Subject().Link(42, new[] { _absFolder });

            Assert.That(result.FileCount, Is.EqualTo(1));
            Assert.That(result.MovedCount, Is.EqualTo(0));
            Assert.That(_updated, Is.Empty);
            Assert.That(((ManualImportCommand)_pushed.Single()).Files.Single().Path, Is.EqualTo(Path.Combine(_absFolder, "Part 02.mp3")));
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
            Assert.That(result.MovedCount, Is.EqualTo(0));
            Assert.That(result.SkippedCount, Is.EqualTo(2), "already on this book");
            Assert.That(result.CommandId, Is.Null);
            Assert.That(_pushed, Is.Empty);
            Assert.That(_updated, Is.Empty);
        }
    }
}
