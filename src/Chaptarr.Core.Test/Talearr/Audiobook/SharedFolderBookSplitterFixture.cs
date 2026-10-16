using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using NzbDrone.Core.Talearr.Audiobook;

namespace Chaptarr.Core.Test.Talearr.Audiobook
{
    [TestFixture]
    public class SharedFolderBookSplitterFixture
    {
        private sealed class F
        {
            public string Path { get; set; }
            public Dictionary<string, List<string>> Tags { get; set; }
        }

        private static F File(string path, string album, int? track = null)
        {
            var tags = new Dictionary<string, List<string>>();
            if (album != null)
            {
                tags["ALBUM"] = new List<string> { album };
            }

            if (track.HasValue)
            {
                tags["TRACKNUMBER"] = new List<string> { track.Value.ToString() };
            }

            return new F { Path = path, Tags = tags };
        }

        private static IReadOnlyDictionary<string, string> Keys(params F[] files)
        {
            return SharedFolderBookSplitter.GetBookKeys(files, f => f.Path, f => f.Tags);
        }

        [Test]
        public void two_books_in_one_folder_are_split()
        {
            var keys = Keys(
                File("/dl/Orwell/a01.mp3", "1984", 1),
                File("/dl/Orwell/a02.mp3", "1984", 2),
                File("/dl/Orwell/b01.mp3", "Animal Farm", 1),
                File("/dl/Orwell/b02.mp3", "Animal Farm", 2));

            Assert.That(keys.Count, Is.EqualTo(4));
            Assert.That(keys["/dl/Orwell/a01.mp3"], Is.EqualTo(keys["/dl/Orwell/a02.mp3"]));
            Assert.That(keys["/dl/Orwell/b01.mp3"], Is.EqualTo(keys["/dl/Orwell/b02.mp3"]));
            Assert.That(keys["/dl/Orwell/a01.mp3"], Is.Not.EqualTo(keys["/dl/Orwell/b01.mp3"]));
        }

        [Test]
        public void two_single_file_books_are_split()
        {
            var keys = Keys(File("/dl/x/Mistborn 1.mp3", "Mistborn: The Final Empire"), File("/dl/x/Mistborn 2.mp3", "The Well of Ascension"));

            Assert.That(keys.Values.Distinct().Count(), Is.EqualTo(2));
        }

        [Test]
        public void disc_and_format_words_in_the_album_keep_one_book_together()
        {
            var keys = Keys(
                File("/dl/Dune/01.mp3", "Dune (Disc 1)"),
                File("/dl/Dune/02.mp3", "Dune [Disc 2]"),
                File("/dl/Dune/03.mp3", "Dune - CD 3 of 12 (Unabridged)"),
                File("/dl/Dune/04.mp3", "DUNE"));

            Assert.That(keys, Is.Empty);
        }

        [Test]
        public void chapter_titles_in_the_album_tag_are_not_split()
        {
            var keys = Keys(
                File("/dl/b/01.mp3", "Chapter One: Loomings", 1),
                File("/dl/b/02.mp3", "Chapter Two: The Carpet-Bag", 2),
                File("/dl/b/03.mp3", "Chapter Three: The Spouter-Inn", 3));

            Assert.That(keys, Is.Empty);
        }

        [Test]
        public void a_file_without_an_album_tag_keeps_the_folder_together()
        {
            var keys = Keys(File("/dl/b/01.mp3", "Book A"), File("/dl/b/02.mp3", null), File("/dl/b/03.mp3", "Book B"));

            Assert.That(keys, Is.Empty);
        }

        [Test]
        public void many_one_file_albums_are_treated_as_bad_tagging()
        {
            var files = Enumerable.Range(1, 6).Select(i => File($"/dl/b/{i:00}.mp3", $"Random {i * 7}")).ToArray();

            Assert.That(Keys(files), Is.Empty);
        }

        [TestCase("Dune (Unabridged)", "dune")]
        [TestCase("Dune: Disc 03", "dune")]
        [TestCase("Ender's Game - Part 2 of 3", "ender s game")]
        [TestCase("The Hobbit [64kbps]", "the hobbit")]
        public void album_normalisation(string album, string expected)
        {
            Assert.That(SharedFolderBookSplitter.NormalizeAlbum(album), Is.EqualTo(expected));
        }
    }
}
