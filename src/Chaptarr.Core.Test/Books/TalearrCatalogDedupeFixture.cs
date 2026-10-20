using System;
using System.Collections.Generic;
using System.Linq;
using NLog;
using NUnit.Framework;
using NzbDrone.Core.Books;
using NzbDrone.Core.Books.Services;
using NzbDrone.Core.ImportLists.Exclusions;
using NzbDrone.Core.Parser;
using NzbDrone.Core.Profiles.Metadata;

namespace Chaptarr.Core.Test.Books
{
    // Talearr: Audible-only (az:) pockets are folded into the real work on add and refresh.
    // Shapes come from the Talearr test server: Animal Farm 5003 (hc:356467) vs 5064 (az:B002ZVK8HM),
    // 1984 5002 (hc:379760) vs eight az: "1984" audiobook rows, and the Ryan Becker "True Crime Stories" books.
    [TestFixture]
    public class TalearrCatalogDedupeFixture
    {
        private static Book Work(string title, string hardcoverId, BookMediaType mediaType = BookMediaType.Audiobook, params Edition[] editions)
        {
            return new Book
            {
                Title = title,
                HardcoverBookId = hardcoverId,
                BaseBookId = hardcoverId,
                MediaType = mediaType,
                Editions = editions.Length > 0 ? editions.ToList() : new List<Edition> { Ed($"{hardcoverId}-edition", title) }
            };
        }

        private static Book Audible(string title, string asin, BookMediaType mediaType = BookMediaType.Audiobook, string narrator = null)
        {
            return new Book
            {
                Title = title,
                BaseBookId = $"az:{asin}",
                ASIN = asin,
                MediaType = mediaType,
                Editions = new List<Edition> { Ed($"az:{asin}-audiobook", title, narrator) }
            };
        }

        private static Edition Ed(string foreignEditionId, string title, string narrator = null)
        {
            return new Edition { ForeignEditionId = foreignEditionId, Title = title, Language = "eng", ReadingFormatId = 2, Narrator = narrator };
        }

        [Test]
        public void should_fold_audible_only_duplicate_into_the_work_with_the_same_title()
        {
            var work = Work("Animal Farm", "hc:356467");
            var audible = Audible("Animal Farm", "B002ZVK8HM", narrator: "Simon Callow");

            var result = TalearrCatalogDedupe.CollapseAudibleOnlyPockets(new List<Book> { work, audible });

            Assert.That(result, Is.EqualTo(new[] { work }));
            Assert.That(work.Editions.Select(e => e.ForeignEditionId), Does.Contain("az:B002ZVK8HM-audiobook"));
            Assert.That(work.Editions.Single(e => e.ForeignEditionId == "az:B002ZVK8HM-audiobook").Narrator, Is.EqualTo("Simon Callow"));
        }

        [Test]
        public void should_fold_many_audible_rows_and_not_duplicate_known_editions()
        {
            var work = Work("1984", "hc:379760", BookMediaType.Audiobook, Ed("hc:edition:1-audiobook", "1984"), Ed("az:B00DW3K6OC-audiobook", "1984"));
            var books = new List<Book>
            {
                work,
                Audible("1984", "B00DW3K6OC"),
                Audible("1984", "3837154270"),
                Audible("1984", "B0D63D91KB")
            };

            var result = TalearrCatalogDedupe.CollapseAudibleOnlyPockets(books);

            Assert.That(result, Has.Count.EqualTo(1));
            Assert.That(work.Editions.Select(e => e.ForeignEditionId), Is.EquivalentTo(new[]
            {
                "hc:edition:1-audiobook", "az:B00DW3K6OC-audiobook", "az:3837154270-audiobook", "az:B0D63D91KB-audiobook"
            }));
        }

        [Test]
        public void should_fold_audible_title_without_subtitle_into_the_single_matching_work()
        {
            var work = Work("Steve Jobs: The Exclusive Biography", "hc:374018");
            var audible = Audible("Steve Jobs", "B005FRGT44");

            var result = TalearrCatalogDedupe.CollapseAudibleOnlyPockets(new List<Book> { work, audible });

            Assert.That(result, Is.EqualTo(new[] { work }));
        }

        [Test]
        public void should_prefer_the_exact_title_when_a_translation_shares_the_main_title()
        {
            var work = Work("Steve Jobs", "hc:374018");
            var spanish = Work("Steve Jobs: la biografía", "hc:218742516");
            var audible = Audible("Steve Jobs", "8499923402");

            TalearrCatalogDedupe.CollapseAudibleOnlyPockets(new List<Book> { work, spanish, audible });

            Assert.That(work.Editions, Has.Count.EqualTo(2));
            Assert.That(spanish.Editions, Has.Count.EqualTo(1));
        }

        [Test]
        public void should_keep_audible_pocket_when_the_title_fits_several_works()
        {
            // Ryan Becker has several "True Crime Stories: ..." books; az:B075V8LJKL is just "True Crime Stories".
            var books = new List<Book>
            {
                Work("True Crime Stories: 48 Terrifying True Crime Murder Cases", "hc:2120585"),
                Work("True Crime Stories: 12 Terrifying True Crime Murder Cases", "hc:1348108"),
                Audible("True Crime Stories", "B075V8LJKL")
            };

            var result = TalearrCatalogDedupe.CollapseAudibleOnlyPockets(books);

            Assert.That(result, Has.Count.EqualTo(3));
        }

        [TestCase("Animal Farm: Adapted for Children")]
        [TestCase("Animal Farm (Veritas Book 1)")]
        [TestCase("Burmese Days")]
        public void should_keep_audible_pocket_that_is_a_different_product(string audibleTitle)
        {
            var work = Work("Animal Farm: A Fairy Story", "hc:356467");
            var audible = Audible(audibleTitle, "B0DVHPLGL7");

            var result = TalearrCatalogDedupe.CollapseAudibleOnlyPockets(new List<Book> { work, audible });

            Assert.That(result, Has.Count.EqualTo(2));
        }

        [TestCase("Animal Farm: Adapted for Children")]
        [TestCase("Animal Farm: The Graphic Novel")]
        public void should_keep_audible_pocket_that_adds_its_own_subtitle(string audibleTitle)
        {
            var work = Work("Animal Farm", "hc:356467");
            var audible = Audible(audibleTitle, "B0DVHPLGL7");

            var result = TalearrCatalogDedupe.CollapseAudibleOnlyPockets(new List<Book> { work, audible });

            Assert.That(result, Has.Count.EqualTo(2));
        }

        [Test]
        public void should_not_fold_across_media_types()
        {
            var ebookWork = Work("Animal Farm", "hc:356467", BookMediaType.Ebook);
            var audible = Audible("Animal Farm", "B002ZVK8HM", BookMediaType.Audiobook);

            var result = TalearrCatalogDedupe.CollapseAudibleOnlyPockets(new List<Book> { ebookWork, audible });

            Assert.That(result, Has.Count.EqualTo(2));
        }

        [Test]
        public void should_not_treat_books_with_a_work_id_as_audible_only()
        {
            var book = Audible("Animal Farm [Dramatised]", "B00B1V3CG6");
            book.GoodreadsWorkId = "gr:269626327";

            Assert.That(TalearrCatalogDedupe.IsAudibleOnly(book), Is.False);
            Assert.That(TalearrCatalogDedupe.IsAudibleOnly(Audible("Animal Farm", "B002ZVK8HM")), Is.True);
        }

        [Test]
        public void normalize_remote_books_should_fold_audible_duplicates_during_refresh()
        {
            var logger = LogManager.GetCurrentClassLogger();
            var localAuthor = new Author { Id = 11, Name = "George Orwell", AudiobookMetadataProfileId = 1 };
            var work = Work("Animal Farm", "hc:356467", BookMediaType.Audiobook, Ed("hc:edition:33157483-audiobook", "Animal Farm", "Ralph Cosham"));
            var audible = Audible("Animal Farm", "B002ZVK8HM", narrator: "Simon Callow");

            var result = RefreshAuthorService.NormalizeRemoteBooks(
                localAuthor,
                new[] { work, audible },
                Array.Empty<Series>(),
                new PassThroughMetadataProfileService(),
                new EmptyImportListExclusionService(),
                logger,
                audiobookMetadataProfileIdOverride: null,
                ebookMetadataProfileIdOverride: null,
                editionSelector: new EditionSelector(logger),
                retainEditions: true,
                logContext: "test");

            Assert.That(result, Has.Count.EqualTo(1));
            Assert.That(result[0].HardcoverBookId, Is.EqualTo("hc:356467"));
            Assert.That(result[0].Editions.Select(e => e.ForeignEditionId), Is.EquivalentTo(new[] { "hc:edition:33157483-audiobook", "az:B002ZVK8HM-audiobook" }));
        }

        [Test]
        public void should_let_import_retarget_an_audible_duplicate_to_the_grabbed_book()
        {
            // The "Animal Farm - George Orwell (Unabridged)" download was grabbed for book 5003 but the
            // file matcher picked Audible-only duplicate 5524 (az:B0CLV4M8BD), and import was blocked.
            var grabbed = Work("Animal Farm", "hc:356467");
            grabbed.Id = 5003;
            grabbed.AuthorId = 11;
            var duplicate = Audible("Animal Farm", "B0CLV4M8BD", narrator: "Mark Young");
            duplicate.Id = 5524;
            duplicate.AuthorId = 11;

            Assert.That(TalearrCatalogDedupe.IsAudibleDuplicateOf(grabbed, duplicate), Is.True);
            Assert.That(NzbDrone.Core.MediaFiles.DownloadedBooksImportService.CanRetargetSameWorkMatch(grabbed, duplicate), Is.True);
        }

        [Test]
        public void should_not_retarget_to_a_different_audible_product_or_author()
        {
            var grabbed = Work("Animal Farm", "hc:356467");
            grabbed.Id = 5003;
            grabbed.AuthorId = 11;

            var adaptation = Audible("Animal Farm: Adapted for Children", "B0DVHPLGL7");
            adaptation.Id = 4475;
            adaptation.AuthorId = 11;

            var otherAuthor = Audible("Animal Farm", "B0000000XX");
            otherAuthor.Id = 9000;
            otherAuthor.AuthorId = 99;

            var ebook = Audible("Animal Farm", "B0CLV4M8BD", BookMediaType.Ebook);
            ebook.Id = 9001;
            ebook.AuthorId = 11;

            Assert.That(NzbDrone.Core.MediaFiles.DownloadedBooksImportService.CanRetargetSameWorkMatch(grabbed, adaptation), Is.False);
            Assert.That(NzbDrone.Core.MediaFiles.DownloadedBooksImportService.CanRetargetSameWorkMatch(grabbed, otherAuthor), Is.False);
            Assert.That(NzbDrone.Core.MediaFiles.DownloadedBooksImportService.CanRetargetSameWorkMatch(grabbed, ebook), Is.False);
        }

        [TestCase("Animal Farm", "Animal Farm (Audiobook)", true)]
        [TestCase("The Pelican Brief", "Pelican Brief", true)]
        [TestCase("Project Hail Mary: A Novel", "Project Hail Mary", true)]
        [TestCase("Blackshirts and Reds", "Blackshirts & Reds", true)]
        [TestCase("Steve Jobs", "Steve Jobs: The Exclusive Biography", true)]
        [TestCase("Animal Farm: A Fairy Story", "Animal Farm: Adapted for Children", false)]
        [TestCase("Animal Farm", "Animal Farm (Veritas Book 1)", false)]
        [TestCase("1984", "1984 (NHB Modern Plays)", false)]
        public void should_compare_work_titles(string left, string right, bool expected)
        {
            Assert.That(TalearrTitleNormalizer.IsSameWorkTitle(left, right), Is.EqualTo(expected));
        }

        private sealed class PassThroughMetadataProfileService : IMetadataProfileService
        {
            public MetadataProfile Add(MetadataProfile profile) => throw new NotImplementedException();
            public void Update(MetadataProfile profile) => throw new NotImplementedException();
            public void Delete(int id) => throw new NotImplementedException();
            public List<MetadataProfile> All() => throw new NotImplementedException();
            public MetadataProfile Get(int id) => new MetadataProfile { Id = id, Name = $"Profile {id}" };
            public bool Exists(int id) => true;
            public List<Book> FilterBooks(Author input, int profileId) => input?.Books?.ToList() ?? new List<Book>();
        }

        private sealed class EmptyImportListExclusionService : IImportListExclusionService
        {
            public ImportListExclusion Add(ImportListExclusion importListExclusion) => throw new NotImplementedException();
            public List<ImportListExclusion> All() => throw new NotImplementedException();
            public void Delete(int id) => throw new NotImplementedException();
            public void Delete(List<int> ids) => throw new NotImplementedException();
            public void Delete(string foreignId) => throw new NotImplementedException();
            public ImportListExclusion Get(int id) => throw new NotImplementedException();
            public ImportListExclusion FindByForeignId(string foreignId) => throw new NotImplementedException();
            public ImportListExclusion Update(ImportListExclusion importListExclusion) => throw new NotImplementedException();
            public List<ImportListExclusion> FindByForeignId(List<string> foreignIds) => new List<ImportListExclusion>();
        }
    }
}
