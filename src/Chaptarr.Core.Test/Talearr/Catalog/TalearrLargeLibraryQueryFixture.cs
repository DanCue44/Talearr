using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Chaptarr.Api.V1.Calendar;
using Dapper;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using NLog;
using NUnit.Framework;
using NzbDrone.Core.AuthorStats;
using NzbDrone.Core.Books;
using NzbDrone.Core.Datastore;
using NzbDrone.Core.DecisionEngine.Specifications;
using NzbDrone.Core.MediaCover;

namespace Chaptarr.Core.Test.Talearr.Catalog
{
    // TaleArr: the large-library query rewrites return the same rows as before.
    [TestFixture]
    [NonParallelizable]
    public class TalearrLargeLibraryQueryFixture
    {
        private TalearrMigratedDatabase _db;

        [OneTimeSetUp]
        public void OneTimeSetUp()
        {
            if (TableMapping.Mapper.TableMap.Count == 0)
            {
                TableMapping.Map();
            }

            _db = new TalearrMigratedDatabase("talearr_large_query");

            // 2,400 books: well past the 900 bind-variable chunk size.
            TalearrLargeLibrary.Seed(_db, authors: 120, booksPerAuthor: 20, editionsPerBook: 1);
        }

        [OneTimeTearDown]
        public void OneTimeTearDown()
        {
            _db.Dispose();
        }

        [Test]
        public void series_links_for_a_large_id_set_match_the_table()
        {
            var repository = new SeriesBookLinkRepository(_db.MainDatabase, new TalearrRecordingEventAggregator());
            var ids = Enumerable.Range(1, 2400).Where(id => id % 7 != 0).ToList();

            var links = repository.GetLinksByBook(ids);

            using var conn = _db.Open();
            var expected = conn.Query<int>("SELECT Id FROM SeriesBookLink WHERE BookId % 7 != 0 ORDER BY Id").ToList();

            Assert.That(links.Select(l => l.Id).OrderBy(id => id), Is.EqualTo(expected));
            Assert.That(links.All(l => l.Series?.Value != null && l.Series.Value.Id == l.SeriesId), Is.True, "series attached");
        }

        [Test]
        public void slug_lookup_is_case_insensitive_and_returns_every_row()
        {
            using (var conn = _db.Open())
            {
                conn.Execute("UPDATE Books SET TitleSlug = 'shared-slug' WHERE Id IN (5, 6)");
                conn.Execute("UPDATE Books SET TitleSlug = 'Other-Slug' WHERE Id = 7");
            }

            var repository = new BookRepository(_db.MainDatabase, new TalearrRecordingEventAggregator());

            Assert.That(repository.FindAllBySlug("Shared-Slug").Select(b => b.Id).OrderBy(id => id), Is.EqualTo(new[] { 5, 6 }));
            Assert.That(repository.FindAllBySlug("other-slug").Select(b => b.Id), Is.EqualTo(new[] { 7 }));
            Assert.That(repository.FindAllBySlug("missing"), Is.Empty);
            Assert.That(repository.FindAllBySlug(" "), Is.Empty);
        }

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

        [Test]
        public void a_page_of_books_reads_statistics_of_its_own_authors_only()
        {
            var books = new List<Book>
            {
                new Book { Id = 1, AuthorId = 10, Title = "A", ReleaseDate = DateTime.UtcNow, Ratings = new Ratings() },
                new Book { Id = 2, AuthorId = 20, Title = "B", ReleaseDate = DateTime.UtcNow, Ratings = new Ratings() }
            };

            var perAuthorCalls = new List<int>();
            var controller = new CalendarController(
                Proxy<IBookService>(new Dictionary<string, Func<MethodInfo, object[], object>>
                {
                    [nameof(IBookService.BooksBetweenDates)] = (_, _) => books
                }),
                Proxy<ISeriesBookLinkService>(new Dictionary<string, Func<MethodInfo, object[], object>>
                {
                    [nameof(ISeriesBookLinkService.GetLinksByBook)] = (_, _) => new List<SeriesBookLink>()
                }),
                Proxy<IAuthorStatisticsService>(new Dictionary<string, Func<MethodInfo, object[], object>>
                {
                    [nameof(IAuthorStatisticsService.AuthorStatistics)] = (method, args) =>
                    {
                        Assert.That(method.ReturnType, Is.EqualTo(typeof(AuthorStatistics)), "no whole-library statistics for a page");
                        var authorId = (int)args[0];
                        perAuthorCalls.Add(authorId);
                        return new AuthorStatistics
                        {
                            AuthorId = authorId,
                            BookStatistics = new List<BookStatistics>
                            {
                                new BookStatistics { AuthorId = authorId, BookId = authorId / 10, BookFileCount = authorId / 10, SizeOnDisk = 100 }
                            }
                        };
                    }
                }),
                Proxy<IMapCoversToLocal>(new Dictionary<string, Func<MethodInfo, object[], object>>
                {
                    [nameof(IMapCoversToLocal.ConvertToLocalUrls)] = (_, _) => null
                }),
                Proxy<IUpgradableSpecification>(new Dictionary<string, Func<MethodInfo, object[], object>>()),
                null);
            controller.ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() };

            var resources = controller.GetCalendar(DateTime.UtcNow.AddDays(-1), DateTime.UtcNow.AddDays(1));

            Assert.That(perAuthorCalls.OrderBy(id => id), Is.EqualTo(new[] { 10, 20 }));
            Assert.That(resources.Single(r => r.Id == 2).Statistics.BookFileCount, Is.EqualTo(2));
        }
    }
}
