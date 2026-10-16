using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Chaptarr.Api.V1.Books;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using NLog;
using NUnit.Framework;
using NzbDrone.Core.AuthorStats;
using NzbDrone.Core.Books;
using NzbDrone.Core.DecisionEngine.Specifications;
using NzbDrone.Core.MediaCover;
using NzbDrone.Core.Messaging.Commands;
using NzbDrone.Core.Messaging.Events;
using NzbDrone.Core.Profiles.Metadata;
using NzbDrone.Core.Profiles.Qualities;
using NzbDrone.Core.RootFolders;
using NzbDrone.Core.Validation;
using NzbDrone.SignalR;

namespace Chaptarr.Core.Test.Api
{
    // TaleArr: PUT /api/v1/book/{id} on a book with several monitored editions (book 3768 on the
    // TaleArr test server had seven) used to throw "Sequence contains more than one matching element"
    // and return a 500, so the monitored edition could not be changed.
    [TestFixture]
    public class TalearrBookControllerEditionSwitchFixture
    {
        private class ServiceProxy<T> : DispatchProxy where T : class
        {
            public Dictionary<string, Func<MethodInfo, object[], object>> Handlers { get; set; } = new();

            protected override object Invoke(MethodInfo targetMethod, object[] args)
            {
                if (Handlers.TryGetValue(targetMethod.Name, out var handler))
                {
                    return handler(targetMethod, args);
                }

                throw new NotImplementedException($"Test proxy does not implement {typeof(T).Name}.{targetMethod.Name}");
            }
        }

        private static T Proxy<T>(Dictionary<string, Func<MethodInfo, object[], object>> handlers) where T : class
        {
            var proxy = DispatchProxy.Create<T, ServiceProxy<T>>();
            ((ServiceProxy<T>)(object)proxy).Handlers = handlers;
            return proxy;
        }

        private static T ThrowingProxy<T>() where T : class
        {
            return Proxy<T>(new Dictionary<string, Func<MethodInfo, object[], object>>());
        }

        private static Edition E(int id, string title, string language, int readingFormat, bool monitored)
        {
            return new Edition
            {
                Id = id,
                BookId = 3768,
                Title = title,
                Language = language,
                ReadingFormatId = readingFormat,
                ForeignEditionId = $"test:{id}",
                Monitored = monitored,
                Ratings = new Ratings()
            };
        }

        private static (BookController Controller, Book Book, List<Edition> Saved) CreateController()
        {
            var author = new Author { Id = 7, Name = "Ali Hazelwood", SortNameLastFirst = "Hazelwood, Ali" };
            var book = new Book
            {
                Id = 3768,
                AuthorId = author.Id,
                Author = author,
                Title = "Bound and Unbound",
                TitleSlug = "bound-and-unbound",
                MediaType = BookMediaType.Audiobook,
                AnyEditionOk = true,
                Ratings = new Ratings(),
                Editions = new List<Edition>
                {
                    E(30206, "Bound and Unbound: A paranormal novella duology", "eng", 3, false),
                    E(42983, "Enlace & Desenlace", "por", 3, true),
                    E(42984, "Bound & Unbound - Die Paranormal-Romance-Dilogie", "deu", 2, true),
                    E(42985, "Bound & Unbound - Die Paranormal-Romance-Dilogie", "deu", 2, true),
                    E(42987, "Mortal & Inmortal: Dos romances paranormales", "spa", 2, true)
                }
            };

            var saved = new List<Edition>();
            var controller = new BookController(
                authorService: ThrowingProxy<IAuthorService>(),
                bookService: Proxy<IBookService>(new Dictionary<string, Func<MethodInfo, object[], object>>
                {
                    [nameof(IBookService.GetBook)] = (_, _) => book,
                    [nameof(IBookService.UpdateBook)] = (_, args) => args[0]
                }),
                addBookService: null,
                editionService: Proxy<IEditionService>(new Dictionary<string, Func<MethodInfo, object[], object>>
                {
                    [nameof(IEditionService.UpdateMany)] = (_, args) =>
                    {
                        saved.AddRange((IEnumerable<Edition>)args[0]);
                        return null;
                    }
                }),
                editionSelector: new EditionSelector(LogManager.GetCurrentClassLogger()),
                seriesBookLinkService: ThrowingProxy<ISeriesBookLinkService>(),
                authorStatisticsService: Proxy<IAuthorStatisticsService>(new Dictionary<string, Func<MethodInfo, object[], object>>
                {
                    [nameof(IAuthorStatisticsService.AuthorStatistics)] = (method, _) =>
                        method.ReturnType == typeof(AuthorStatistics)
                            ? new AuthorStatistics()
                            : new List<AuthorStatistics>()
                }),
                mediaFileService: null,
                coverMapper: Proxy<IMapCoversToLocal>(new Dictionary<string, Func<MethodInfo, object[], object>>
                {
                    [nameof(IMapCoversToLocal.ConvertToLocalUrls)] = (_, _) => null
                }),
                upgradableSpecification: ThrowingProxy<IUpgradableSpecification>(),
                signalRBroadcaster: Proxy<IBroadcastSignalRMessage>(new Dictionary<string, Func<MethodInfo, object[], object>>
                {
                    ["get_IsConnected"] = (_, _) => false
                }),
                commandQueueManager: ThrowingProxy<IManageCommandQueue>(),
                eventAggregator: ThrowingProxy<IEventAggregator>(),
                metadataProfileService: ThrowingProxy<IMetadataProfileService>(),
                qualityProfileService: ThrowingProxy<IQualityProfileService>(),
                rootFolderService: ThrowingProxy<IRootFolderService>(),
                qualityProfileExistsValidator: new QualityProfileExistsValidator(new TestQualityProfileService()),
                metadataProfileExistsValidator: new MetadataProfileExistsValidator(new TestMetadataProfileService()),
                logger: LogManager.GetCurrentClassLogger());

            controller.ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() };
            return (controller, book, saved);
        }

        private static BookResource Resource(Book book, Func<Edition, bool> monitored)
        {
            return new BookResource
            {
                Id = book.Id,
                Monitored = true,
                AnyEditionOk = true,
                TitleSlug = book.TitleSlug,
                Editions = book.Editions.Select(e => new EditionResource
                {
                    Id = e.Id,
                    BookId = book.Id,
                    Title = e.Title,
                    Language = e.Language,
                    ReadingFormatId = e.ReadingFormatId,
                    ForeignEditionId = e.ForeignEditionId,
                    Monitored = monitored(e)
                }).ToList()
            };
        }

        [Test]
        public void should_switch_edition_on_a_book_with_several_monitored_editions()
        {
            var (controller, book, saved) = CreateController();

            var result = controller.UpdateBook(Resource(book, e => e.Id == 30206));

            Assert.That(result.Result, Is.InstanceOf<AcceptedAtActionResult>());
            Assert.That(saved.Where(e => e.Monitored).Select(e => e.Id), Is.EqualTo(new[] { 30206 }));
        }

        [Test]
        public void should_keep_the_newly_chosen_edition_when_the_client_echoes_stale_monitored_flags()
        {
            var (controller, book, saved) = CreateController();

            // The client loaded the corrupt state and only turned the English edition on.
            controller.UpdateBook(Resource(book, e => e.Monitored || e.Id == 30206));

            Assert.That(saved.Where(e => e.Monitored).Select(e => e.Id), Is.EqualTo(new[] { 30206 }));
        }

        [Test]
        public void should_repair_to_a_single_edition_when_the_client_does_not_change_the_selection()
        {
            var (controller, book, saved) = CreateController();

            controller.UpdateBook(Resource(book, e => e.Monitored));

            Assert.That(saved.Count(e => e.Monitored), Is.EqualTo(1));
            Assert.That(saved.Single(e => e.Monitored).Id, Is.EqualTo(30206));
        }

        [Test]
        public void should_report_no_single_monitored_edition_for_corrupt_state()
        {
            var editions = new List<Edition> { E(1, "A", "eng", 2, true), E(2, "B", "eng", 2, true) };

            Assert.That(BookController.GetSingleMonitoredEditionId(editions), Is.Null);
            Assert.That(BookController.GetSingleMonitoredEditionId(editions.Take(1)), Is.EqualTo(1));
            Assert.That(BookController.GetSingleMonitoredEditionId(null), Is.Null);
        }

        [Test]
        public void should_not_narrow_when_more_than_one_edition_was_newly_turned_on()
        {
            var submitted = new List<Edition> { E(1, "A", "eng", 2, true), E(2, "B", "eng", 2, true), E(3, "C", "eng", 2, true) };

            var narrowed = BookController.ResolveSubmittedEditionSelection(new HashSet<int> { 1 }, submitted);

            Assert.That(narrowed, Is.False);
            Assert.That(submitted.Count(e => e.Monitored), Is.EqualTo(3));
        }
    }
}
