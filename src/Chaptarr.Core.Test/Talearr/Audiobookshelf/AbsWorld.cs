using System;
using System.Collections.Generic;
using System.Linq;
using Chaptarr.Core.Test.Talearr.Recordings;
using NLog;
using NzbDrone.Common.Http;
using NzbDrone.Core.Books;
using NzbDrone.Core.MediaFiles;
using NzbDrone.Core.Messaging.Commands;
using NzbDrone.Core.Notifications.AudioBookShelf;
using NzbDrone.Core.RootFolders;
using NzbDrone.Core.Talearr.Audiobookshelf;
using NzbDrone.Core.Talearr.GrabPolicy;
using NzbDrone.Core.Talearr.Recordings;

namespace Chaptarr.Core.Test.Talearr.Audiobookshelf
{
    // A small in-memory Talearr library plus a real talearr.db, wired to the real Talearr
    // Audiobookshelf services. Upstream services are faked by method name.
    internal sealed class AbsWorld : IDisposable
    {
        public AbsWorld()
        {
            Folder = new TalearrTestFolder();
            Store = new TalearrAbsStore(Folder.CreateDatabase(), LogManager.GetCurrentClassLogger());

            var (bookService, bookFake) = InterfaceFake<IBookService>.Create();
            bookFake.On(nameof(IBookService.GetBooksByAuthor), args => Books.Values.Where(b => b.AuthorId == (int)args[0]).OrderBy(b => b.Id).ToList());
            bookFake.On(nameof(IBookService.GetBook), args => Books.TryGetValue((int)args[0], out var b) ? b : throw new InvalidOperationException("no book"));
            bookFake.On(nameof(IBookService.GetBooks), args => ((IEnumerable<int>)args[0]).Where(Books.ContainsKey).Select(id => Books[id]).ToList());
            BookService = bookService;

            var (editionService, editionFake) = InterfaceFake<IEditionService>.Create();
            editionFake.On(nameof(IEditionService.GetEditionsByBook), args => Editions.Where(e => e.BookId == (int)args[0]).ToList());
            editionFake.On(nameof(IEditionService.GetEditions), args => Editions.Where(e => ((IEnumerable<int>)args[0]).Contains(e.Id)).ToList());
            editionFake.On(nameof(IEditionService.GetEditionsByProviderAndId), args =>
            {
                var prefix = (string)args[0];
                var id = ((string)args[1]).Replace("-", string.Empty).ToUpperInvariant();
                return Editions.Where(e => prefix == "az"
                        ? string.Equals(e.Asin, id, StringComparison.OrdinalIgnoreCase) || string.Equals(e.AudibleASIN, id, StringComparison.OrdinalIgnoreCase)
                        : string.Equals(e.Isbn13, id, StringComparison.OrdinalIgnoreCase) || string.Equals(e.Isbn10, id, StringComparison.OrdinalIgnoreCase))
                    .ToList();
            });
            EditionService = editionService;

            var (authorService, authorFake) = InterfaceFake<IAuthorService>.Create();
            authorFake.On(nameof(IAuthorService.GetAllAuthors), _ => Authors.ToList());
            AuthorService = authorService;

            var (mediaFiles, fileFake) = InterfaceFake<IMediaFileService>.Create();
            fileFake.On(nameof(IMediaFileService.GetFilesByBook), args => FilesOf((int)args[0]));
            fileFake.On(nameof(IMediaFileService.GetFilesWithBasePath), args => Files.Where(f => f.Path.StartsWith(((string)args[0]).TrimEnd('/') + "/", StringComparison.Ordinal)).ToList());
            MediaFiles = mediaFiles;

            var (rootFolders, rootFake) = InterfaceFake<IRootFolderService>.Create();
            rootFake.On(nameof(IRootFolderService.All), _ => RootFolders.ToList());
            RootFolderService = rootFolders;

            var (queue, queueFake) = InterfaceFake<IManageCommandQueue>.Create();
            queueFake.On(nameof(IManageCommandQueue.Push), args =>
            {
                Commands.Add((Command)args[0]);
                return null;
            });
            CommandQueue = queue;

            Connections = new FakeConnections();
            Resolver = new FakeResolver();
            Groups = new TalearrRecordingGroupService(BookService);
            Library = new TalearrAbsLibraryService(Store, Connections, Groups, MediaFiles, EditionService, LogManager.GetCurrentClassLogger(), Resolver, GrabPolicy);

            Authors.Add(new Author { Id = 7, Name = "John Grisham" });
            Authors.Add(new Author { Id = 8, Name = "George Orwell" });
            Authors.Add(new Author { Id = 9, Name = "R. F. Kuang" });
            Authors.Add(new Author { Id = 10, Name = "Frank Herbert" });

            RootFolders.Add(new RootFolder { Id = 3, Name = "Ebooks", Path = "/ebooks" });
            RootFolders.Add(new RootFolder { Id = 4, Name = "Audiobooks", Path = "/audiobooks" });
        }

        public TalearrTestFolder Folder { get; }
        public TalearrAbsStore Store { get; }
        public IBookService BookService { get; }
        public IEditionService EditionService { get; }
        public IAuthorService AuthorService { get; }
        public IMediaFileService MediaFiles { get; }
        public IRootFolderService RootFolderService { get; }
        public IManageCommandQueue CommandQueue { get; }
        public FakeConnections Connections { get; }
        public FakeResolver Resolver { get; }
        public FakeGrabPolicy GrabPolicy { get; } = new FakeGrabPolicy();
        public TalearrRecordingGroupService Groups { get; }
        public TalearrAbsLibraryService Library { get; }

        public Dictionary<int, Book> Books { get; } = new Dictionary<int, Book>();
        public List<Edition> Editions { get; } = new List<Edition>();
        public List<BookFile> Files { get; } = new List<BookFile>();
        public List<Author> Authors { get; } = new List<Author>();
        public List<RootFolder> RootFolders { get; } = new List<RootFolder>();
        public List<Command> Commands { get; } = new List<Command>();

        public TalearrAbsBookLookup Lookup => new TalearrAbsBookLookup(AuthorService, BookService, EditionService, MediaFiles, RootFolders.Select(r => r.Path).ToList());

        public Book AddBook(int id, int authorId, string title, BookMediaType mediaType, string baseBookId, string slug = null, string narrator = null)
        {
            var book = new Book
            {
                Id = id,
                AuthorId = authorId,
                Title = title,
                TitleSlug = slug ?? title.ToLowerInvariant().Replace(' ', '-'),
                BaseBookId = baseBookId,
                HardcoverBookId = baseBookId,
                MediaType = mediaType,
                Narrator = narrator,
                Monitored = true,
                AudiobookMonitored = mediaType == BookMediaType.Audiobook,
                EbookMonitored = mediaType == BookMediaType.Ebook
            };

            Books[id] = book;
            book.Editions = new List<Edition>();
            return book;
        }

        public Edition AddEdition(Book book, int id, bool monitored, string asin = null, string isbn13 = null, string isbn10 = null, int? durationSeconds = null, string narrator = null, string title = null)
        {
            var edition = new Edition
            {
                Id = id,
                BookId = book.Id,
                ForeignEditionId = $"hc:edition:{id}",
                Title = title ?? book.Title,
                Asin = asin,
                Isbn13 = isbn13,
                Isbn10 = isbn10,
                DurationSeconds = durationSeconds,
                Narrator = narrator,
                NarratorNames = narrator == null ? new List<string>() : new List<string> { narrator },
                Monitored = monitored,
                IsEbook = book.MediaType == BookMediaType.Ebook,
                Language = "eng"
            };

            Editions.Add(edition);
            book.Editions.Add(edition);
            return edition;
        }

        public BookFile AddFile(Edition edition, string path)
        {
            var file = new BookFile { Id = Files.Count + 1, EditionId = edition.Id, Path = path, Size = 1000 };
            Files.Add(file);
            return file;
        }

        public TalearrAbsConnection AddConnection(int id = 1, bool sync = true, bool skipOwned = true, AudioBookShelfSettings settings = null)
        {
            settings ??= new AudioBookShelfSettings
            {
                Host = "audiobookshelf",
                Port = 13378,
                ApiKey = "test-key"
            };
            settings.TalearrSyncLibrary = sync;
            settings.TalearrSkipOwned = skipOwned;

            var connection = new TalearrAbsConnection { Id = id, Name = "Audiobookshelf", Settings = settings };
            Connections.List.Add(connection);
            return connection;
        }

        // Matches and caches items the way a sync does, without HTTP.
        public void Cache(int connectionId, params TalearrAbsItem[] items)
        {
            var lookup = Lookup;
            var result = new TalearrAbsSyncResult { ConnectionId = connectionId };
            foreach (var item in items)
            {
                item.ConnectionId = connectionId;
                result.Items.Add(item);
                result.Matches.AddRange(TalearrAbsMatcher.Match(item, lookup));
            }

            Store.ReplaceConnection(result, new TalearrAbsSyncState { ConnectionId = connectionId, LastSyncAt = DateTime.UtcNow, ItemCount = items.Length });
            Library.Invalidate();
        }

        public void Dispose()
        {
            Folder.Dispose();
        }

        private List<BookFile> FilesOf(int bookId)
        {
            var editionIds = Editions.Where(e => e.BookId == bookId).Select(e => e.Id).ToHashSet();
            return Files.Where(f => editionIds.Contains(f.EditionId)).ToList();
        }

        internal sealed class FakeConnections : ITalearrAbsConnections
        {
            public List<TalearrAbsConnection> List { get; } = new List<TalearrAbsConnection>();

            public List<TalearrAbsConnection> GetConnections()
            {
                return List.ToList();
            }
        }

        internal sealed class FakeResolver : ITalearrAbridgedResolver
        {
            public Dictionary<int, AbridgedMarker> Markers { get; } = new Dictionary<int, AbridgedMarker>();

            public AbridgedResolution Resolve(Book book)
            {
                return Markers.TryGetValue(book.Id, out var marker)
                    ? new AbridgedResolution(marker, AbridgedSources.Metadata, "test")
                    : AbridgedResolution.Unknown;
            }

            public AbridgedResolution ResolveEdition(Edition edition, IReadOnlyCollection<Edition> siblings, IReadOnlyCollection<BookFile> files = null)
            {
                return AbridgedResolution.Unknown;
            }
        }

        internal sealed class FakeGrabPolicy : ITalearrGrabPolicyService
        {
            public TalearrGrabPolicy Policy { get; set; } = new TalearrGrabPolicy();

            public TalearrGrabPolicy Get()
            {
                return Policy;
            }

            public TalearrGrabPolicy Save(TalearrGrabPolicy policy)
            {
                Policy = policy;
                return policy;
            }
        }
    }

    internal static class AbsItems
    {
        public static TalearrAbsItem Audio(string id, string title, string author, string narrator = null, double? duration = null, AbridgedMarker abridged = AbridgedMarker.Unknown, string asin = null, string isbn = null, string localPath = null)
        {
            return new TalearrAbsItem
            {
                ItemId = id,
                LibraryId = "lib-audio",
                Path = localPath,
                LocalPath = localPath,
                Title = title,
                Authors = author,
                Narrators = narrator,
                DurationSeconds = duration,
                Abridged = abridged,
                Asin = asin,
                Isbn = isbn,
                HasAudio = true,
                SyncedAt = DateTime.UtcNow
            };
        }

        public static TalearrAbsItem Ebook(string id, string title, string author, string isbn = null, string localPath = null)
        {
            return new TalearrAbsItem
            {
                ItemId = id,
                LibraryId = "lib-ebook",
                Path = localPath,
                LocalPath = localPath,
                Title = title,
                Authors = author,
                Isbn = isbn,
                HasEbook = true,
                EbookFormat = "epub",
                SyncedAt = DateTime.UtcNow
            };
        }
    }
}
