using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using NzbDrone.Core.MetadataSource.BookInfo.V5;
using NzbDrone.Core.Talearr.Metadata.Source;

namespace Chaptarr.Core.Test.Talearr.Metadata.Source
{
    // The Illusionist by James A. Hunter and D.J. Bodden, as Hardcover and Audible listed it on
    // 2026-11-20 (Talearr #3): Hardcover has no audiobook editions, and Audible titles every book
    // "Viridian Gate Online", naming the book only in the subtitle (book 1), in the title after a
    // colon (book 4) or not at all (books 2 and 3, subtitle "Illusionist Series, Book 2").
    [TestFixture]
    public class TalearrAudibleSeriesPlaceFixture
    {
        private const string Narrator = "Daniel Thomas May";

        private static V5Book Book(int id, string title, string position)
        {
            return new V5Book
            {
                Id = $"hc:{id}",
                BaseBookId = $"hc:{id}",
                HardcoverBookId = $"hc:{id}",
                Title = title,
                SeriesName = "The Illusionist",
                SeriesPosition = position,
                Editions = new List<V5Edition>
                {
                    new V5Edition { Id = $"hc:edition:{id}", ReadingFormatId = TalearrHardcoverMapper.V5Ebook, Format = "ebook", Title = title, Asins = new List<string>() }
                },
                HasEbook = true
            };
        }

        private static V5AuthorResponse Hardcover(params V5Book[] extra)
        {
            // Hardcover repeats the title as the subtitle on books 2 and 4.
            var deadMans = Book(446451, "Dead Man's Tide", "2");
            deadMans.Subtitle = "Dead Man's Tide";
            var sharpers = Book(1487178, "Sharper's Coin", "4");
            sharpers.Subtitle = "Sharper's Coin";

            var books = new List<V5Book>
            {
                Book(446555, "Nomad Soul", "1"),
                deadMans,
                Book(446417, "Inquisitor’s Foil", "3"),
                sharpers
            };
            books.AddRange(extra);

            return new V5AuthorResponse
            {
                TalearrSource = true,
                Author = new V5AuthorData { Id = "hc:241277", HardcoverAuthorId = "hc:241277", Name = "James A. Hunter" },
                Books = books,
                Series = new List<V5Series>
                {
                    new V5Series
                    {
                        Id = "hc:12676",
                        HardcoverSeriesId = "hc:12676",
                        Name = "The Illusionist",
                        SeriesType = "main",
                        Books = books.Where(b => b.SeriesName == "The Illusionist")
                            .Select(b => new V5SeriesBook { BookId = b.Id, Title = b.Title, Position = b.SeriesPosition, IsPrimary = true })
                            .ToList()
                    }
                },
                Summary = new V5Summary { FormatsAvailable = new V5FormatsAvailable() }
            };
        }

        private static AudibleCatalogItem Item(string asin, string title, string subtitle, string series, string sequence)
        {
            return new AudibleCatalogItem
            {
                Asin = asin,
                Title = title,
                Subtitle = subtitle,
                Authors = new List<string> { "James A. Hunter", "D.J. Bodden" },
                Narrators = new List<string> { Narrator },
                Series = new List<AudibleCatalogSeries> { new AudibleCatalogSeries { Asin = "B07YN85S7L", Title = series, Sequence = sequence } },
                RuntimeMinutes = 620,
                ReleaseDate = "2019-11-26",
                Language = "english",
                FormatType = "unabridged",
                DeliveryType = "SinglePartBook"
            };
        }

        private static List<AudibleCatalogItem> Audible()
        {
            return new List<AudibleCatalogItem>
            {
                Item("1630155942", "Viridian Gate Online", "Nomad Soul", "Illusionist", "1"),
                Item("1630155977", "Viridian Gate Online", "Illusionist Series, Book 2", "Illusionist", "2"),
                Item("1630155985", "Viridian Gate Online", "Illusionist Series, Book 3", "Illusionist", "3"),
                Item("B0F8KJ8BZQ", "Viridian Gate Online: Sharper’s Coin", "The Illusionist, Book 4", "Illusionist", "4")
            };
        }

        private static string AudioAsin(V5AuthorResponse response, string id)
        {
            return response.Books.Single(b => b.Id == id).Editions
                .Where(e => e.ReadingFormatId == TalearrHardcoverMapper.V5Audio)
                .Select(e => e.Asin)
                .SingleOrDefault();
        }

        [Test]
        public void books_audible_titles_after_their_series_match_by_series_place()
        {
            var response = Hardcover();

            var stats = TalearrAudibleMerge.Apply(response, Audible(), audibleLeads: false, "us");

            Assert.That(stats.Matched, Is.EqualTo(4));
            Assert.That(AudioAsin(response, "hc:446555"), Is.EqualTo("1630155942"));
            Assert.That(AudioAsin(response, "hc:446451"), Is.EqualTo("1630155977"));
            Assert.That(AudioAsin(response, "hc:446417"), Is.EqualTo("1630155985"));
            Assert.That(AudioAsin(response, "hc:1487178"), Is.EqualTo("B0F8KJ8BZQ"));
        }

        [Test]
        public void the_recording_keeps_the_book_title_not_the_series_title()
        {
            var response = Hardcover();

            TalearrAudibleMerge.Apply(response, Audible(), audibleLeads: false, "us");

            var audio = response.Books.Single(b => b.Id == "hc:446451").Editions.Single(e => e.ReadingFormatId == TalearrHardcoverMapper.V5Audio);
            Assert.That(audio.Title, Is.EqualTo("Dead Man's Tide"));

            var sharpers = response.Books.Single(b => b.Id == "hc:1487178").Editions.Single(e => e.ReadingFormatId == TalearrHardcoverMapper.V5Audio);
            Assert.That(sharpers.Title, Is.EqualTo("Sharper's Coin"));
        }

        [Test]
        public void a_bare_hardcover_copy_outside_the_series_does_not_take_the_audiobook()
        {
            var bare = Book(2711828, "Viridian Gate Online: Nomad Soul", null);
            bare.SeriesName = null;
            bare.Subtitle = "A litRPG Adventure";
            var response = Hardcover(bare);

            TalearrAudibleMerge.Apply(response, Audible(), audibleLeads: false, "us");

            Assert.That(AudioAsin(response, "hc:446555"), Is.EqualTo("1630155942"));
            Assert.That(AudioAsin(response, "hc:2711828"), Is.Null);
        }

        [Test]
        public void audible_leading_adds_no_second_copy_of_those_books()
        {
            var response = Hardcover();

            TalearrAudibleMerge.Apply(response, Audible(), audibleLeads: true, "us");

            Assert.That(response.Books.Count, Is.EqualTo(4));
        }

        [Test]
        public void a_series_place_with_two_books_is_left_alone()
        {
            var response = Hardcover(Book(1612378, "Inquisitor's Foil (Alternate)", "3"));
            response.Series[0].Books.Add(new V5SeriesBook { BookId = "hc:1612378", Title = "Inquisitor's Foil (Alternate)", Position = "3", IsPrimary = true });

            TalearrAudibleMerge.Apply(response, Audible(), audibleLeads: false, "us");

            Assert.That(AudioAsin(response, "hc:446417"), Is.Null);
            Assert.That(AudioAsin(response, "hc:1612378"), Is.Null);
        }

        [Test]
        public void an_audible_title_naming_another_book_does_not_match_by_place()
        {
            var response = Hardcover();
            var items = new List<AudibleCatalogItem> { Item("B000000001", "Some Other Story", null, "Illusionist", "2") };

            TalearrAudibleMerge.Apply(response, items, audibleLeads: false, "us");

            Assert.That(AudioAsin(response, "hc:446451"), Is.Null);
        }
    }
}
