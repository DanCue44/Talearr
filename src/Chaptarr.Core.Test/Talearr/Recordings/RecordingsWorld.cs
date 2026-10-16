using System;
using System.Collections.Generic;
using System.Linq;
using Chaptarr.Core.Test.Talearr.GrabPolicy;
using NLog;
using NzbDrone.Common.Messaging;
using NzbDrone.Core.Books;
using NzbDrone.Core.IndexerSearch;
using NzbDrone.Core.MediaFiles;
using NzbDrone.Core.Messaging.Commands;
using NzbDrone.Core.Messaging.Events;
using NzbDrone.Core.Talearr.Recordings;

namespace Chaptarr.Core.Test.Talearr.Recordings
{
    // An in-memory library run through the real upstream BookService, so AddWantedEdition,
    // GetBook and the identity group behave exactly as they do in Chaptarr.
    internal sealed class RecordingsWorld : IDisposable
    {
        private int _nextBookId = 9000;
        private int _nextEditionId = 90000;

        public RecordingsWorld()
        {
            Folder = new TalearrTestFolder();
            Store = Folder.CreateStore();

            var (repository, _) = ConfigRepositoryProxy.Create();
            Settings = new TalearrRecordingsSettingsService(repository, LogManager.GetCurrentClassLogger());

            var (books, bookRepo) = InterfaceFake<IBookRepository>.Create();
            bookRepo.On(nameof(IBookRepository.Get), args => args[0] is int id ? Books.GetValueOrDefault(id) : (object)((IEnumerable<int>)args[0]).Select(i => Books.GetValueOrDefault(i)).Where(b => b != null).ToList());
            bookRepo.On(nameof(IBookRepository.Find), args => Books.GetValueOrDefault((int)args[0]));
            bookRepo.On("FindExisting", args => ((IEnumerable<int>)args[0]).Select(i => Books.GetValueOrDefault(i)).Where(b => b != null).ToList());
            bookRepo.On(nameof(IBookRepository.Upsert), args => Save((Book)args[0]));
            bookRepo.On(nameof(IBookRepository.Update), args => Save((Book)args[0]));
            bookRepo.On(nameof(IBookRepository.UpdateMany), args =>
            {
                foreach (var b in (IEnumerable<Book>)args[0])
                {
                    Save(b);
                }

                return null;
            });
            bookRepo.On(nameof(IBookRepository.GetBooksByAuthorId), args => Books.Values.Where(b => b.AuthorId == (int)args[0]).OrderBy(b => b.Id).ToList());
            bookRepo.On(nameof(IBookRepository.GetBooks), args => Books.Values.Where(b => b.AuthorId == (int)args[0]).OrderBy(b => b.Id).ToList());
            bookRepo.On(nameof(IBookRepository.All), _ => Books.Values.OrderBy(b => b.Id).ToList());
            bookRepo.On(nameof(IBookRepository.Delete), args =>
            {
                Deleted.Add(args[0] is int id ? id : ((Book)args[0]).Id);
                Books.Remove(Deleted.Last());
                return null;
            });
            bookRepo.On(nameof(IBookRepository.FindAllByProviderIdAndMediaType), args => Books.Values
                .Where(b => b.MediaType == (BookMediaType)args[2] && BookIdentity.GetProviderIdentityTokens(b).Contains(((string)args[1]).Contains(':') ? (string)args[1] : $"{args[0]}:{args[1]}", StringComparer.OrdinalIgnoreCase))
                .ToList());

            var (editions, editionFake) = InterfaceFake<IEditionService>.Create();
            editionFake.On(nameof(IEditionService.GetEditionsByBook), args => args[0] is int id
                ? Editions.Where(e => e.BookId == id).OrderBy(e => e.Id).ToList()
                : Editions.Where(e => ((IEnumerable<int>)args[0]).Contains(e.BookId)).OrderBy(e => e.Id).ToList());
            editionFake.On(nameof(IEditionService.GetEdition), args => Editions.FirstOrDefault(e => e.Id == (int)args[0]));
            editionFake.On(nameof(IEditionService.InsertMany), args =>
            {
                foreach (var e in (IEnumerable<Edition>)args[0])
                {
                    if (e.Id <= 0)
                    {
                        e.Id = _nextEditionId++;
                    }

                    Editions.Add(e);
                }

                return null;
            });
            editionFake.On(nameof(IEditionService.UpdateMany), _ => null);
            editionFake.On(nameof(IEditionService.GetEditionsByProviderAndId), _ => new List<Edition>());
            editionFake.On(nameof(IEditionService.SetMonitored), args =>
            {
                var edition = (Edition)args[0];
                foreach (var sibling in Editions.Where(e => e.BookId == edition.BookId))
                {
                    sibling.Monitored = sibling.Id == edition.Id;
                    if (sibling.Id == edition.Id && (bool)args[1])
                    {
                        sibling.ManualAdd = true;
                    }
                }

                return Editions.Where(e => e.BookId == edition.BookId).ToList();
            });

            var (authors, authorFake) = InterfaceFake<IAuthorService>.Create();
            authorFake.On(nameof(IAuthorService.GetAuthor), args => new Author { Id = (int)args[0], Name = "John Grisham" });

            var (mediaFiles, fileFake) = InterfaceFake<IMediaFileService>.Create();
            fileFake.On(nameof(IMediaFileService.GetFilesByBook), args => FilesOf((int)args[0]));
            fileFake.On(nameof(IMediaFileService.GetFilesByBooks), args => ((IEnumerable<int>)args[0]).SelectMany(FilesOf).ToList());
            MediaFiles = mediaFiles;

            var (events, eventFake) = InterfaceFake<IEventAggregator>.Create();
            eventFake.On(nameof(IEventAggregator.PublishEvent), args =>
            {
                Events.Add(args[0]);
                return null;
            });

            var (series, seriesFake) = InterfaceFake<NzbDrone.Core.Books.ISeriesBookLinkRepository>.Create();
            seriesFake.On(nameof(ISeriesBookLinkRepository.GetLinksByBook), _ => new List<SeriesBookLink>());
            seriesFake.On(nameof(ISeriesBookLinkRepository.InsertMany), _ => null);

            var (multiCopy, multiCopyFake) = InterfaceFake<IMultiCopySeriesService>.Create();
            multiCopyFake.On(nameof(IMultiCopySeriesService.GetAllVariants), _ => new List<Series>());

            BookService = new BookService(books, editions, events, authors, mediaFiles, null, series, multiCopy, LogManager.GetCurrentClassLogger());

            var (queue, queueFake) = InterfaceFake<IManageCommandQueue>.Create();
            queueFake.On(nameof(IManageCommandQueue.Push), args =>
            {
                Commands.Add((Command)args[0]);
                return null;
            });
            CommandQueue = queue;

            Groups = new TalearrRecordingGroupService(BookService);
            Facts = new TalearrAudibleFactsService(null, Store, Settings, LogManager.GetCurrentClassLogger());
            Resolver = new TalearrAbridgedResolver(Facts, mediaFiles, LogManager.GetCurrentClassLogger());
            Recordings = new TalearrRecordingsService(BookService, editions, mediaFiles, Groups, Facts, Resolver, Store, Settings, queue, LogManager.GetCurrentClassLogger());
        }

        public TalearrTestFolder Folder { get; }
        public TalearrRecordingsStore Store { get; }
        public TalearrRecordingsSettingsService Settings { get; }
        public Dictionary<int, Book> Books { get; } = new Dictionary<int, Book>();
        public List<Edition> Editions { get; } = new List<Edition>();
        public List<BookFile> Files { get; } = new List<BookFile>();
        public List<object> Events { get; } = new List<object>();
        public List<Command> Commands { get; } = new List<Command>();
        public List<int> Deleted { get; } = new List<int>();
        public IMediaFileService MediaFiles { get; }
        public BookService BookService { get; }
        public IManageCommandQueue CommandQueue { get; }
        public TalearrRecordingGroupService Groups { get; }
        public TalearrAudibleFactsService Facts { get; }
        public TalearrAbridgedResolver Resolver { get; }
        public TalearrRecordingsService Recordings { get; }

        public List<int> SearchedBookIds => Commands.OfType<BookSearchCommand>().SelectMany(c => c.BookIds).ToList();

        // "The Firm": audiobook row 3401 (the given edition monitored), ebook row 3402.
        public Book AddFirm(Edition monitored, bool withFiles)
        {
            var audiobook = new Book
            {
                Id = 3401,
                AuthorId = 7,
                Title = "The Firm",
                TitleSlug = "the-firm_2",
                BaseBookId = "hc:378630",
                HardcoverBookId = "hc:378630",
                MediaType = BookMediaType.Audiobook,
                Monitored = true,
                AudiobookMonitored = true,
                AnyEditionOk = true,
                AddOptions = new AddBookOptions()
            };
            Books[audiobook.Id] = audiobook;
            Books[3402] = new Book { Id = 3402, AuthorId = 7, Title = "The Firm", TitleSlug = "the-firm", BaseBookId = "hc:378630", HardcoverBookId = "hc:378630", MediaType = BookMediaType.Ebook, Monitored = true, AddOptions = new AddBookOptions() };

            foreach (var edition in FirmEditions.All())
            {
                edition.Monitored = edition.Id == monitored.Id;
                Editions.Add(edition);
            }

            if (withFiles)
            {
                Files.Add(new BookFile { Id = 1, EditionId = monitored.Id, Path = "/audiobooks/John Grisham/The Firm - D.W. Moffett/The Firm.m4b", Size = 200_000_000, DurationSeconds = 6 * 3600, Narrator = "D.W. Moffett" });
            }

            return audiobook;
        }

        public void LinkAudible(Edition edition, string asin, string formatType, int minutes, string narrator)
        {
            Store.UpsertProducts(new[]
            {
                new AudibleProduct { Marketplace = "us", Asin = asin, Status = AudibleProductStatus.Ok, Title = "The Firm", FormatType = formatType, RuntimeMinutes = minutes, Narrators = new List<string> { narrator }, Publisher = edition.Publisher, FetchedAt = DateTime.UtcNow }
            });
            Store.UpsertLink(new EditionAudibleLink { ForeignEditionId = TalearrEditionKey.For(edition), Marketplace = "us", Asin = asin, Method = EditionAudibleLink.MethodAsinsList, Confidence = 0.9, CheckedAt = DateTime.UtcNow });
        }

        private List<BookFile> FilesOf(int bookId)
        {
            var editionIds = Editions.Where(e => e.BookId == bookId).Select(e => e.Id).ToHashSet();
            return Files.Where(f => editionIds.Contains(f.EditionId)).ToList();
        }

        private Book Save(Book book)
        {
            if (book.Id <= 0)
            {
                book.Id = _nextBookId++;
            }

            Books[book.Id] = book;
            return book;
        }

        public void Dispose()
        {
            Folder.Dispose();
        }
    }
}
