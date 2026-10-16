using System.Collections.Generic;
using NUnit.Framework;
using NzbDrone.Core.Books;
using NzbDrone.Core.Talearr.Metadata;

namespace Chaptarr.Core.Test.Talearr.Metadata
{
    // metadata.json text: the book title without format noise, real subtitles only, tidy names.
    [TestFixture]
    public class TalearrSidecarTextFixture
    {
        [TestCase("1984", "Nineteen Eighty-Four (MP3 Book)", "1984")]
        [TestCase(null, "Nineteen Eighty-Four (MP3 Book)", "Nineteen Eighty-Four")]
        [TestCase("Animal Farm (Audible Audio)", "Animal Farm", "Animal Farm")]
        [TestCase("The King of Torts [Abridged 5-CD Set]", null, "The King of Torts")]
        [TestCase("Dune  Messiah", null, "Dune Messiah")]
        [TestCase("The Hobbit (French Edition)", null, "The Hobbit (French Edition)")]
        public void title_is_the_book_title_without_format_noise(string bookTitle, string editionTitle, string expected)
        {
            var book = new Book { Title = bookTitle };
            var edition = new Edition { Title = editionTitle, Book = book };

            Assert.That(TalearrSidecarText.Title(book, edition), Is.EqualTo(expected));
        }

        [TestCase("A Love Story", "A Love Story")]
        [TestCase("  The  Lord of the Rings, Book 1 ", "The Lord of the Rings, Book 1")]
        [TestCase("Unabridged", null)]
        [TestCase("(MP3 Book)", null)]
        [TestCase("Audio CD", null)]
        [TestCase("A Novel", null)]
        [TestCase("a thriller", null)]
        [TestCase("1984", null)]
        [TestCase("", null)]
        [TestCase(null, null)]
        public void subtitle_is_kept_only_when_it_is_a_real_subtitle(string subtitle, string expected)
        {
            Assert.That(TalearrSidecarText.CleanSubtitle(subtitle, "1984"), Is.EqualTo(expected));
        }

        [Test]
        public void subtitle_prefers_the_edition_then_the_book()
        {
            var book = new Book { Title = "Dune", Subtitle = "Book One of the Dune Chronicles" };

            Assert.Multiple(() =>
            {
                Assert.That(TalearrSidecarText.Subtitle(book, new Edition { Subtitle = "Deluxe Edition" }, "Dune"), Is.EqualTo("Deluxe Edition"));
                Assert.That(TalearrSidecarText.Subtitle(book, new Edition { Subtitle = "Unabridged" }, "Dune"), Is.EqualTo("Book One of the Dune Chronicles"));
                Assert.That(TalearrSidecarText.Subtitle(new Book(), new Edition { Subtitle = "A Novel" }, "Dune"), Is.Null);
            });
        }

        [TestCase("Samuel  West", "Samuel West")]
        [TestCase(" Samuel West ", "Samuel West")]
        [TestCase("Samuel West", "Samuel West")]
        [TestCase("Samuel \t West", "Samuel West")]
        [TestCase("   ", null)]
        [TestCase(null, null)]
        public void names_have_whitespace_collapsed(string name, string expected)
        {
            Assert.That(TalearrSidecarText.Name(name), Is.EqualTo(expected));
        }

        [Test]
        public void names_are_deduplicated_after_cleaning()
        {
            var names = TalearrSidecarText.Names(new List<string> { "Samuel  West", "Samuel West", "samuel west ", "", null, "Simon Prebble" });

            Assert.That(names, Is.EqualTo(new[] { "Samuel West", "Simon Prebble" }));
        }
    }
}
