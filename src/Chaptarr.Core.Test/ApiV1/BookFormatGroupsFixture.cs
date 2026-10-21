using System.Collections.Generic;
using System.Linq;
using Chaptarr.Api.V1.Books;
using NUnit.Framework;
using NzbDrone.Core.Books;

namespace Chaptarr.Core.Test.ApiV1
{
    // Talearr: the All formats book index groups a work's ebook and audiobook rows.
    // Shapes from the Talearr test server: Bad Blood is hc:105267 as ebook 7701 and audiobook 7745.
    [TestFixture]
    public class BookFormatGroupsFixture
    {
        private static Book Row(int id, int authorId, BookMediaType mediaType, string hardcoverBookId = null, bool monitored = false)
        {
            return new Book
            {
                Id = id,
                AuthorId = authorId,
                Title = $"Book {id}",
                MediaType = mediaType,
                HardcoverBookId = hardcoverBookId,
                AudiobookMonitored = mediaType == BookMediaType.Audiobook && monitored,
                EbookMonitored = mediaType == BookMediaType.Ebook && monitored
            };
        }

        private static List<List<Book>> Group(params Book[] sortedRows)
        {
            return BookFormatGroups.Group(sortedRows, BookResourceMapper.GetNativeForeignBookId);
        }

        [Test]
        public void ebook_and_audiobook_of_one_work_form_one_group()
        {
            var groups = Group(
                Row(7701, 20, BookMediaType.Ebook, "hc:105267"),
                Row(7745, 20, BookMediaType.Audiobook, "hc:105267"));

            Assert.That(groups, Has.Count.EqualTo(1));
            Assert.That(groups[0].Select(b => b.Id), Is.EqualTo(new[] { 7701, 7745 }));
        }

        [Test]
        public void a_group_sits_where_its_first_row_was()
        {
            var groups = Group(
                Row(1, 20, BookMediaType.Ebook, "hc:1"),
                Row(2, 20, BookMediaType.Audiobook, "hc:2"),
                Row(3, 20, BookMediaType.Audiobook, "hc:1"));

            Assert.That(groups.Select(g => g[0].Id), Is.EqualTo(new[] { 1, 2 }));
            Assert.That(groups[0].Select(b => b.Id), Is.EqualTo(new[] { 1, 3 }));
        }

        [Test]
        public void the_same_work_id_under_another_author_stays_apart()
        {
            var groups = Group(
                Row(1, 20, BookMediaType.Ebook, "hc:1"),
                Row(2, 21, BookMediaType.Audiobook, "hc:1"));

            Assert.That(groups, Has.Count.EqualTo(2));
        }

        [Test]
        public void rows_without_a_work_id_stay_alone()
        {
            var groups = Group(
                Row(1, 20, BookMediaType.Ebook),
                Row(2, 20, BookMediaType.Audiobook));

            Assert.That(groups, Has.Count.EqualTo(2));
        }

        [Test]
        public void primary_is_the_format_with_files_then_the_audiobook()
        {
            var ebook = Row(1, 20, BookMediaType.Ebook, "hc:1");
            var audiobook = Row(2, 20, BookMediaType.Audiobook, "hc:1");
            var group = new List<Book> { ebook, audiobook };

            Assert.That(BookFormatGroups.PickPrimary(group, b => b.Id == 1), Is.SameAs(ebook));
            Assert.That(BookFormatGroups.PickPrimary(group, b => false), Is.SameAs(audiobook));
        }

        [Test]
        public void formats_list_the_ebook_first_with_files_and_monitoring()
        {
            var formats = BookFormatGroups.ToFormats(
                new[]
                {
                    Row(2, 20, BookMediaType.Audiobook, "hc:1", monitored: true),
                    Row(1, 20, BookMediaType.Ebook, "hc:1")
                },
                b => b.Id == 2);

            Assert.That(formats.Select(f => f.MediaType), Is.EqualTo(new[] { "ebook", "audiobook" }));
            Assert.That(formats.Select(f => f.BookId), Is.EqualTo(new[] { 1, 2 }));
            Assert.That(formats.Select(f => f.HasFile), Is.EqualTo(new[] { false, true }));
            Assert.That(formats.Select(f => f.Monitored), Is.EqualTo(new[] { false, true }));
        }

        [Test]
        public void group_statistics_add_up_both_formats()
        {
            var sum = BookFormatGroups.SumStatistics(new[]
            {
                new BookStatisticsResource { BookCount = 1, BookFileCount = 1, TotalBookCount = 1, SizeOnDisk = 100 },
                new BookStatisticsResource { BookCount = 1, BookFileCount = 24, TotalBookCount = 1, SizeOnDisk = 900 },
                null
            });

            Assert.That(sum.BookCount, Is.EqualTo(2));
            Assert.That(sum.BookFileCount, Is.EqualTo(25));
            Assert.That(sum.TotalBookCount, Is.EqualTo(2));
            Assert.That(sum.SizeOnDisk, Is.EqualTo(1000));
        }
    }
}
