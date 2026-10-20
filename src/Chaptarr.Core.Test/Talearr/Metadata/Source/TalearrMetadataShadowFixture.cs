using System;
using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using NzbDrone.Core.Books;
using NzbDrone.Core.HealthCheck.Checks;
using NzbDrone.Core.Talearr.Metadata.Source;

namespace Chaptarr.Core.Test.Talearr.Metadata.Source
{
    [TestFixture]
    public class TalearrMetadataShadowFixture
    {
        private static readonly DateTime Now = new DateTime(2026, 10, 20, 12, 0, 0, DateTimeKind.Utc);

        // Stands in for RefreshAuthorService.NormalizeRemoteBooks with a profile that skips omnibuses.
        private static List<Book> SkipOmnibus(Author local, List<Book> books, List<Series> series)
        {
            return books.Where(b => !b.IsOmnibus).ToList();
        }

        private static Book Remote(string hc, string title, BookMediaType mediaType, bool omnibus = false, string gr = null)
        {
            return new Book
            {
                Title = title,
                MediaType = mediaType,
                HardcoverBookId = hc,
                GoodreadsWorkId = gr,
                BaseBookId = hc ?? gr,
                ForeignEditionId = hc ?? gr,
                IsOmnibus = omnibus,
                Editions = new List<Edition> { new Edition { Title = title, ReadingFormatId = mediaType == BookMediaType.Audiobook ? 2 : 3 } }
            };
        }

        private static Author ServerAuthor()
        {
            var books = new List<Book>();
            foreach (var mediaType in new[] { BookMediaType.Audiobook, BookMediaType.Ebook })
            {
                books.Add(Remote("hc:100", "The Wind Book", mediaType));

                // The server does not flag the trilogy as an omnibus; Hardcover does.
                books.Add(Remote("hc:101", "The Wind Trilogy", mediaType));
                books.Add(Remote("hc:200", "Lost Tale", mediaType));
            }

            books.Add(Remote(null, "Goodreads Only", BookMediaType.Ebook, gr: "gr:555"));
            return new Author { Name = "Ursula Example", HardcoverAuthorId = "hc:42", Books = books, Series = new List<Series>() };
        }

        private static Book Local(int id, string title, BookMediaType mediaType, string hc = null, string gr = null, bool monitored = false)
        {
            var book = new Book
            {
                Id = id,
                Title = title,
                MediaType = mediaType,
                HardcoverBookId = hc,
                GoodreadsWorkId = gr,
                AnyEditionOk = true,
                Editions = new List<Edition>()
            };

            book.SetMonitored(monitored);
            return book;
        }

        private static (TalearrShadowAuthorReport Report, List<Book> Locals) Run(Func<Author> server = null, string serverError = null)
        {
            var hardcover = TalearrHardcoverMapperFixture.CreateProxy().ConvertV5AuthorResponse(TalearrHardcoverMapper.BuildAuthorResponse(HardcoverFixtures.Catalog()));
            var locals = new List<Book>
            {
                Local(1, "The Wind Book", BookMediaType.Audiobook, hc: "hc:100", monitored: true),
                Local(2, "Lost Tale", BookMediaType.Audiobook, hc: "hc:200", monitored: true),
                Local(3, "Goodreads Only", BookMediaType.Ebook, gr: "gr:555"),
                Local(4, "The Wind Trilogy", BookMediaType.Ebook, hc: "hc:101"),
                Local(5, "A Wind Novella", BookMediaType.Ebook, hc: "hc:106")
            };

            var report = TalearrMetadataShadowComparer.Compare(
                new Author { Id = 9, Name = "Ursula Example", HardcoverAuthorId = "hc:42" },
                locals,
                new HashSet<int> { 4 },
                new TalearrMetadataShadowComparer.Side { Remote = server == null ? ServerAuthor() : server(), Error = serverError },
                new TalearrMetadataShadowComparer.Side { Remote = hardcover, SeriesFromSource = 1 },
                SkipOmnibus,
                Now);

            return (report, locals);
        }

        [Test]
        public void monitored_book_missing_from_hardcover_is_flagged()
        {
            var (report, _) = Run();

            Assert.That(report.Status, Is.EqualTo("ok"));
            Assert.That(report.LostMatchesCount, Is.EqualTo(3));
            Assert.That(report.LostProtectedCount, Is.EqualTo(2));

            var lost = report.LostMatches;
            Assert.That(lost.Select(b => b.BookId), Is.EqualTo(new[] { 2, 4, 3 }), "protected rows first");

            var monitored = lost[0];
            Assert.That(monitored.Title, Is.EqualTo("Lost Tale"));
            Assert.That(monitored.Monitored, Is.True);
            Assert.That(monitored.Protected, Is.True);
            Assert.That(monitored.Reason, Is.EqualTo("missing from Hardcover"));
            Assert.That(monitored.ProviderIds, Does.Contain("hc:200"));

            var fileBacked = lost[1];
            Assert.That(fileBacked.HasFiles, Is.True);
            Assert.That(fileBacked.Reason, Is.EqualTo("filtered by profile"), "Hardcover has the work but flags it as an omnibus");

            Assert.That(lost[2].Protected, Is.False);
        }

        [Test]
        public void book_matched_on_both_sides_is_not_flagged_and_gains_are_counted()
        {
            var (report, _) = Run();

            Assert.That(report.LostMatches.Select(b => b.BookId), Has.No.Member(1));
            Assert.That(report.GainedMatchesCount, Is.EqualTo(1), "only Hardcover knows the novella");
            Assert.That(report.Server.MatchedLocalBooks, Is.EqualTo(4));
            Assert.That(report.Hardcover.MatchedLocalBooks, Is.EqualTo(2));
        }

        [Test]
        public void counts_books_per_media_type_before_and_after_filtering()
        {
            var (report, _) = Run();

            Assert.That(report.Server.BooksBeforeFilter.Audiobook, Is.EqualTo(3));
            Assert.That(report.Server.BooksBeforeFilter.Ebook, Is.EqualTo(4));
            Assert.That(report.Server.BooksAfterFilter.Total, Is.EqualTo(7));

            Assert.That(report.Hardcover.BooksBeforeFilter.Audiobook, Is.EqualTo(3));
            Assert.That(report.Hardcover.BooksBeforeFilter.Ebook, Is.EqualTo(3));
            Assert.That(report.Hardcover.BooksAfterFilter.Audiobook, Is.EqualTo(2));
            Assert.That(report.Hardcover.BooksAfterFilter.Ebook, Is.EqualTo(2));
            Assert.That(report.Hardcover.WorksAfterFilter, Is.EqualTo(2));
            Assert.That(report.Hardcover.SeriesFromSource, Is.EqualTo(1));
            Assert.That(report.Hardcover.SeriesKept, Is.EqualTo(2), "Hardcover series are kept, one row per media type");
            Assert.That(report.Hardcover.EditionsBeforeFilter.Total, Is.GreaterThan(report.Hardcover.EditionsAfterFilter.Total));
        }

        [Test]
        public void lists_works_found_on_only_one_side()
        {
            var (report, _) = Run();

            Assert.That(report.OnlyOnServerCount, Is.EqualTo(3));
            Assert.That(report.OnlyOnServer.Select(w => w.Title), Is.EqualTo(new[] { "Goodreads Only", "Lost Tale", "The Wind Trilogy" }));
            Assert.That(report.OnlyOnServer.Single(w => w.Title == "Lost Tale").MediaTypes, Is.EqualTo(new[] { "Audiobook", "Ebook" }));

            Assert.That(report.OnlyOnHardcoverCount, Is.EqualTo(1));
            Assert.That(report.OnlyOnHardcover.Single().WorkId, Is.EqualTo("hc:106"));
            Assert.That(report.OnlyOnHardcover.Single().ProviderIds, Is.EqualTo(new[] { "hc:106" }));
        }

        [Test]
        public void server_failure_is_reported_without_a_comparison()
        {
            var (report, _) = Run(server: () => null, serverError: "metadata server unavailable");

            Assert.That(report.Status, Is.EqualTo("error"));
            Assert.That(report.Message, Does.Contain("metadata server unavailable"));
            Assert.That(report.Server.Available, Is.False);
            Assert.That(report.Hardcover.Available, Is.True);
            Assert.That(report.LostMatches, Is.Empty);
        }

        [Test]
        public void summary_line_names_the_lost_protected_books_count()
        {
            var (report, _) = Run();
            var line = TalearrMetadataShadowComparer.Summarize(report);

            Assert.That(line, Does.Contain("Ursula Example (9)"));
            Assert.That(line, Does.Contain("lost matches 3 (protected 2)"));
            Assert.That(line, Does.Contain("works only on server 3, only on hardcover 1"));
            Assert.That(line, Does.Not.Contain(((char)0x2014).ToString()));
        }

        [Test]
        public void comparison_does_not_change_local_rows()
        {
            var (_, locals) = Run();

            Assert.That(locals.Select(b => b.HardcoverBookId ?? b.GoodreadsWorkId), Is.EqualTo(new[] { "hc:100", "hc:200", "gr:555", "hc:101", "hc:106" }));
            Assert.That(locals.Select(b => b.IsMonitored()), Is.EqualTo(new[] { true, true, false, false, false }));
        }

        [TestCase("hc:42", 42)]
        [TestCase("42", 42)]
        [TestCase("gr:42", 0)]
        [TestCase(null, 0)]
        public void reads_the_hardcover_author_id(string value, int expected)
        {
            var found = TalearrMetadataShadowService.TryGetHardcoverAuthorId(new Author { HardcoverAuthorId = value }, out var id);

            Assert.That(found, Is.EqualTo(expected > 0));
            Assert.That(id, Is.EqualTo(expected));
        }

        [Test]
        public void source_setting_defaults_to_the_metadata_server()
        {
            Assert.That(TalearrMetadataSourceSettingsService.DefaultSource, Is.EqualTo(TalearrMetadataSource.Chaptarr));
            Assert.That(TalearrMetadataSourceSettingsService.SourceKey, Is.EqualTo("talearr.metadata.source"));
            Assert.That(TalearrMetadataSourceSettingsService.TryParse("Talearr", out var source), Is.True);
            Assert.That(source, Is.EqualTo(TalearrMetadataSource.Talearr));
            Assert.That(TalearrMetadataSourceSettingsService.TryParse("hardcover", out _), Is.False);
            Assert.That(TalearrMetadataSourceSettingsService.ToApiValue(TalearrMetadataSource.Chaptarr), Is.EqualTo("chaptarr"));
        }

        [Test]
        public void health_notice_only_when_talearr_source_is_chosen()
        {
            Assert.That(TalearrMetadataSourceCheck.GetMessage(TalearrMetadataSource.Chaptarr, true), Is.Null);
            Assert.That(TalearrMetadataSourceCheck.GetMessage(TalearrMetadataSource.Talearr, true), Does.Contain("refreshed from Hardcover"));
            Assert.That(TalearrMetadataSourceCheck.GetMessage(TalearrMetadataSource.Talearr, false), Does.Contain("Hardcover API token"));
        }
    }
}
