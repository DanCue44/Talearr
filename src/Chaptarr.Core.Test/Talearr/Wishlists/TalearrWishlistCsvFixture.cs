using System.Linq;
using NUnit.Framework;
using NzbDrone.Core.Talearr.Wishlists;

namespace Chaptarr.Core.Test.Talearr.Wishlists
{
    [TestFixture]
    public class TalearrWishlistCsvFixture
    {
        private const string Goodreads =
            "Book Id,Title,Author,Author l-f,Additional Authors,ISBN,ISBN13,My Rating,Average Rating,Publisher,Binding,Number of Pages,Year Published,Original Publication Year,Date Read,Date Added,Bookshelves,Bookshelves with positions,Exclusive Shelf,My Review,Spoiler,Private Notes,Read Count,Owned Copies\n" +
            "5470,1984,George Orwell,\"Orwell, George\",,\"=\"\"0451524934\"\"\",\"=\"\"9780451524935\"\"\",0,4.19,Signet Classic,Mass Market Paperback,328,1950,1949,,2026/10/01,to-read,to-read (#1),to-read,,,,0,0\n" +
            "7613,Animal Farm,George Orwell,\"Orwell, George\",,=\"\",=\"\",5,3.98,Signet,Paperback,141,2004,1945,2020/01/02,2019/12/01,,,read,\"Loved it, \"\"really\"\"\",,,1,0\n";

        private const string StoryGraph =
            "Title,Authors,Contributors,ISBN/UID,Format,Read Status,Date Added,Last Date Read,Dates Read,Read Count,Moods,Pace,Character- or Plot-Driven?,Strong Character Development?,Loveable Characters?,Diverse Characters?,Flawed Characters?,Star Rating,Review,Content Warnings,Content Warning Description,Tags,Owned?\n" +
            "Project Hail Mary,Andy Weir,,9780593135204,audio,to-read,2026/09/30,,,0,,,,,,,,,,,,,No\n" +
            "The Martian,Andy Weir,,9780553418026,digital,read,2025/01/01,2025/02/01,2025/01/20-2025/02/01,1,,,,,,,,4.5,,,,,Yes\n" +
            "\"Good Omens\",\"Terry Pratchett, Neil Gaiman\",,,paperback,to-read,2026/10/01,,,0,,,,,,,,,,,,,No\n";

        private const string Audible =
            "asin,title,subtitle,extended_product_description,authors,narrators,series_title,series_sequence,genres,runtime_length_min,is_finished,percent_complete,rating,num_ratings,date_added,release_date,cover_url,purchase_date\n" +
            "B08GB58KD5,Project Hail Mary,,\"A lone astronaut, with a twist\",Andy Weir,Ray Porter,,,Science Fiction,970,,,4.9,100,2026-10-01,2021-05-04,https://x/y.jpg,\n" +
            "B002V0QK4C,Dune,,,Frank Herbert,\"Scott Brick, Orlagh Cassidy\",Dune,1,Science Fiction,1263,,,4.6,50,2026-10-02,2007-01-01,,\n";

        [Test]
        public void goodreads_export_takes_the_to_read_shelf_and_unwraps_isbns()
        {
            var (format, rows) = TalearrWishlistCsv.Parse(Goodreads);

            Assert.That(format, Is.EqualTo(TalearrWishlistFormats.Goodreads));
            Assert.That(rows.Select(r => r.Title), Is.EqualTo(new[] { "1984" }));
            Assert.That(rows[0].Author, Is.EqualTo("George Orwell"));
            Assert.That(rows[0].Isbn, Is.EqualTo("9780451524935"));
            Assert.That(rows[0].Key, Is.EqualTo("isbn:9780451524935"));
        }

        [Test]
        public void every_shelf_is_taken_when_to_read_only_is_off()
        {
            var (_, rows) = TalearrWishlistCsv.Parse(Goodreads, toReadOnly: false);

            Assert.That(rows.Select(r => r.Title), Is.EqualTo(new[] { "1984", "Animal Farm" }));
            Assert.That(rows[1].Isbn, Is.Null);
            Assert.That(rows[1].Key, Is.EqualTo("title:animal farm|george orwell"));
        }

        [Test]
        public void storygraph_export_takes_the_to_read_rows_and_the_first_author()
        {
            var (format, rows) = TalearrWishlistCsv.Parse(StoryGraph);

            Assert.That(format, Is.EqualTo(TalearrWishlistFormats.StoryGraph));
            Assert.That(rows.Select(r => r.Title), Is.EqualTo(new[] { "Project Hail Mary", "Good Omens" }));
            Assert.That(rows[0].Isbn, Is.EqualTo("9780593135204"));
            Assert.That(rows[1].Author, Is.EqualTo("Terry Pratchett"));
        }

        [Test]
        public void audible_wishlist_export_keeps_every_row_with_its_asin()
        {
            var (format, rows) = TalearrWishlistCsv.Parse(Audible);

            Assert.That(format, Is.EqualTo(TalearrWishlistFormats.Audible));
            Assert.That(rows.Select(r => r.Asin), Is.EqualTo(new[] { "B08GB58KD5", "B002V0QK4C" }));
            Assert.That(rows[1].Author, Is.EqualTo("Frank Herbert"));
            Assert.That(rows[0].Key, Is.EqualTo("asin:b08gb58kd5"));
        }

        [Test]
        public void a_plain_list_needs_only_a_title()
        {
            var (format, rows) = TalearrWishlistCsv.Parse("title;author\r\nThe Hobbit;J.R.R. Tolkien\r\n\r\nDune;\r\n");

            Assert.That(format, Is.EqualTo(TalearrWishlistFormats.Generic));
            Assert.That(rows.Select(r => r.Title), Is.EqualTo(new[] { "The Hobbit", "Dune" }));
            Assert.That(rows[1].Author, Is.Null);
        }

        [Test]
        public void a_file_without_a_title_column_is_refused()
        {
            Assert.Throws<System.FormatException>(() => TalearrWishlistCsv.Parse("name of thing,who\nx,y\n".Replace("name of thing", "label")));
        }

        [Test]
        public void duplicate_rows_are_folded()
        {
            var (_, rows) = TalearrWishlistCsv.Parse("Title,Author\nDune,Frank Herbert\ndune,FRANK HERBERT\n");

            Assert.That(rows.Count, Is.EqualTo(1));
        }

        [TestCase("to-read", true)]
        [TestCase("Want to Read", true)]
        [TestCase("currently-reading", false)]
        [TestCase("read", false)]
        [TestCase(null, false)]
        public void to_read_statuses(string status, bool toRead)
        {
            Assert.That(TalearrWishlistCsv.IsToRead(status), Is.EqualTo(toRead));
        }

        [Test]
        public void hardcover_list_response_carries_provider_ids()
        {
            const string json = "{\"data\":{\"lists\":[{\"id\":9,\"name\":\"Audio queue\",\"list_books\":[" +
                                "{\"book\":{\"id\":379760,\"title\":\"1984\",\"subtitle\":null,\"cached_contributors\":[{\"author\":{\"id\":204214,\"name\":\"George Orwell\"},\"contribution\":null}]}}," +
                                "{\"book\":{\"id\":428778,\"title\":\"Dune\",\"cached_contributors\":[{\"author\":{\"id\":1,\"name\":\"Someone\"},\"contribution\":\"Translator\"},{\"author\":{\"id\":2,\"name\":\"Frank Herbert\"},\"contribution\":\"Author\"}]}}]}]}}";

            var (name, rows) = TalearrHardcoverListClient.ParseResponse(json, "me", "audio-queue");

            Assert.That(name, Is.EqualTo("Audio queue"));
            Assert.That(rows.Select(r => r.BookProviderId), Is.EqualTo(new[] { "hc:379760", "hc:428778" }));
            Assert.That(rows.Select(r => r.AuthorProviderId), Is.EqualTo(new[] { "hc:204214", "hc:2" }));
            Assert.That(rows[1].Author, Is.EqualTo("Frank Herbert"));
        }

        [Test]
        public void hardcover_list_urls()
        {
            Assert.That(TalearrHardcoverListClient.ParseUrl("https://hardcover.app/@dancue/lists/audio-queue"), Is.EqualTo(("dancue", "audio-queue")));
            Assert.That(TalearrHardcoverListClient.ParseUrl("hardcover.app/@x/lists/y?tab=books"), Is.EqualTo(("x", "y")));
            Assert.That(TalearrHardcoverListClient.ParseUrl("https://hardcover.app/books/dune").User, Is.Null);
            Assert.Throws<TalearrWishlistException>(() => TalearrHardcoverListClient.ParseResponse("{\"data\":{\"lists\":[]}}", "x", "y"));
        }
    }
}
