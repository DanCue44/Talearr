using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using NzbDrone.Core.MediaFiles;
using NzbDrone.Core.Parser.Model;
using NzbDrone.Core.Qualities;
using NzbDrone.Core.Talearr.Audiobook;

namespace Chaptarr.Core.Test.Talearr.Audiobook
{
    [TestFixture]
    public class AudiobookTrackOrderFixture
    {
        private sealed class Track
        {
            public string Path { get; set; }
            public Dictionary<string, List<string>> Tags { get; set; }

            public override string ToString() => Path;
        }

        private static Track T(string path, int? track = null, int? disc = null)
        {
            var tags = new Dictionary<string, List<string>>();
            if (track.HasValue)
            {
                tags["TRACKNUMBER"] = new List<string> { track.Value.ToString() };
            }

            if (disc.HasValue)
            {
                tags["DISCNUMBER"] = new List<string> { disc.Value.ToString() };
            }

            return new Track { Path = path, Tags = tags };
        }

        private static List<string> Order(params Track[] tracks)
        {
            return AudiobookTrackOrder.Order(tracks, t => t.Path, t => t.Tags).Select(t => t.Path).ToList();
        }

        [TestCase("CD1", 1)]
        [TestCase("CD 02", 2)]
        [TestCase("cd10", 10)]
        [TestCase("Disc 1", 1)]
        [TestCase("Disk_03", 3)]
        [TestCase("Part 01", 1)]
        [TestCase("Part One", 1)]
        [TestCase("Part Three", 3)]
        [TestCase("Part IX", 9)]
        [TestCase("Disc 3 of 7", 3)]
        [TestCase("Ender's Game - CD 2", 2)]
        [TestCase("Ender's Game (Disc 4)", 4)]
        [TestCase("Side B", 2)]
        public void recognises_disc_folders(string name, int expected)
        {
            Assert.That(AudiobookDiscFolder.TryGetDiscNumber(name, out var disc), Is.True);
            Assert.That(disc, Is.EqualTo(expected));
        }

        [TestCase("Steve Jobs")]
        [TestCase("Side Effects")]
        [TestCase("Part Time Lover")]
        [TestCase("Discworld")]
        [TestCase("1984")]
        [TestCase("")]
        public void ignores_other_folders(string name)
        {
            Assert.That(AudiobookDiscFolder.IsDiscFolder(name), Is.False);
        }

        [TestCase("Part 2", true)]
        [TestCase("CD 3 of 4", true)]
        [TestCase("The Dark Tower Part Two", false)]
        [TestCase("Ender's Game - CD 2", false)]
        public void label_only_mode_rejects_titles_ending_in_a_disc_word(string name, bool expected)
        {
            Assert.That(AudiobookDiscFolder.IsDiscFolder(name, labelOnly: true), Is.EqualTo(expected));
        }

        [Test]
        public void nested_part_folders_in_order_real_1984_layout()
        {
            var order = Order(
                T("/dl/1984/Part 3/Chapter 6.mp3"),
                T("/dl/1984/Part 2/Chapter 9.mp3"),
                T("/dl/1984/Part 1/Chapter 6-8.mp3"),
                T("/dl/1984/Part 2/Chapter 1-3.mp3"),
                T("/dl/1984/Part 1/Chapter 1-2.mp3"),
                T("/dl/1984/Part 3/Chapter 1-2.mp3"),
                T("/dl/1984/Part 2/Chapter 4-6.mp3"),
                T("/dl/1984/Part 1/Chapter 3-5.mp3"),
                T("/dl/1984/Part 3/Chapter 3-5.mp3"),
                T("/dl/1984/Part 2/Chapter 7-8.mp3"));

            Assert.That(order, Is.EqualTo(new[]
            {
                "/dl/1984/Part 1/Chapter 1-2.mp3",
                "/dl/1984/Part 1/Chapter 3-5.mp3",
                "/dl/1984/Part 1/Chapter 6-8.mp3",
                "/dl/1984/Part 2/Chapter 1-3.mp3",
                "/dl/1984/Part 2/Chapter 4-6.mp3",
                "/dl/1984/Part 2/Chapter 7-8.mp3",
                "/dl/1984/Part 2/Chapter 9.mp3",
                "/dl/1984/Part 3/Chapter 1-2.mp3",
                "/dl/1984/Part 3/Chapter 3-5.mp3",
                "/dl/1984/Part 3/Chapter 6.mp3"
            }));
        }

        [Test]
        public void disc_numbers_compare_as_numbers_not_text()
        {
            var order = Order(
                T("/b/CD10/01.mp3"),
                T("/b/CD2/01.mp3"),
                T("/b/CD1/01.mp3"),
                T("/b/CD1/02.mp3"));

            Assert.That(order, Is.EqualTo(new[] { "/b/CD1/01.mp3", "/b/CD1/02.mp3", "/b/CD2/01.mp3", "/b/CD10/01.mp3" }));
        }

        [Test]
        public void word_and_roman_disc_folders()
        {
            Assert.That(Order(T("/b/Disc Three/a.mp3"), T("/b/Disc Two/a.mp3"), T("/b/Disc One/a.mp3")),
                Is.EqualTo(new[] { "/b/Disc One/a.mp3", "/b/Disc Two/a.mp3", "/b/Disc Three/a.mp3" }));

            Assert.That(Order(T("/b/Part X/a.mp3"), T("/b/Part IX/a.mp3"), T("/b/Part IV/a.mp3"), T("/b/Part V/a.mp3")),
                Is.EqualTo(new[] { "/b/Part IV/a.mp3", "/b/Part V/a.mp3", "/b/Part IX/a.mp3", "/b/Part X/a.mp3" }));
        }

        [Test]
        public void many_mp3_files_use_natural_sort()
        {
            var tracks = Enumerable.Range(1, 120).Reverse().Select(i => T($"/b/Book/Track {i}.mp3")).ToArray();

            var order = Order(tracks);

            Assert.That(order.First(), Is.EqualTo("/b/Book/Track 1.mp3"));
            Assert.That(order[1], Is.EqualTo("/b/Book/Track 2.mp3"));
            Assert.That(order[9], Is.EqualTo("/b/Book/Track 10.mp3"));
            Assert.That(order.Last(), Is.EqualTo("/b/Book/Track 120.mp3"));
        }

        [Test]
        public void real_steve_jobs_disc_track_names_sort_in_order()
        {
            var order = Order(
                T("/a/Steve Jobs/202 - Walter Isaacson - Steve Jobs - 2011 - Apple is Born.m4a"),
                T("/a/Steve Jobs/709 - Walter Isaacson - Steve Jobs - 2011 - The Last Word.m4a"),
                T("/a/Steve Jobs/101 - Walter Isaacson - Steve Jobs - 2011 - Introduction To Series.m4a"),
                T("/a/Steve Jobs/110 - Walter Isaacson - Steve Jobs - 2011 - Reed College.m4a"));

            Assert.That(order.Select(System.IO.Path.GetFileName).Select(n => n.Substring(0, 3)), Is.EqualTo(new[] { "101", "110", "202", "709" }));
        }

        [Test]
        public void disc_and_track_tags_win_over_file_names()
        {
            var order = Order(
                T("/b/Book/Apple is Born.mp3", track: 2, disc: 1),
                T("/b/Book/Childhood.mp3", track: 1, disc: 1),
                T("/b/Book/Bono.mp3", track: 1, disc: 2));

            Assert.That(order, Is.EqualTo(new[] { "/b/Book/Childhood.mp3", "/b/Book/Apple is Born.mp3", "/b/Book/Bono.mp3" }));
        }

        [Test]
        public void per_disc_track_numbers_use_the_disc_folder_when_the_disc_tag_is_missing()
        {
            var order = Order(
                T("/b/Book/CD2/b.mp3", track: 1),
                T("/b/Book/CD1/z.mp3", track: 2),
                T("/b/Book/CD1/y.mp3", track: 1),
                T("/b/Book/CD2/a.mp3", track: 2));

            Assert.That(order, Is.EqualTo(new[] { "/b/Book/CD1/y.mp3", "/b/Book/CD1/z.mp3", "/b/Book/CD2/b.mp3", "/b/Book/CD2/a.mp3" }));
        }

        [Test]
        public void duplicate_track_tags_fall_back_to_the_path()
        {
            var order = Order(
                T("/b/Book/02.mp3", track: 1),
                T("/b/Book/01.mp3", track: 1),
                T("/b/Book/03.mp3", track: 1));

            Assert.That(order, Is.EqualTo(new[] { "/b/Book/01.mp3", "/b/Book/02.mp3", "/b/Book/03.mp3" }));
        }

        [Test]
        public void track_tags_like_3_of_12_are_read()
        {
            var tags = new Dictionary<string, List<string>> { ["ID3v2:TRCK"] = new List<string> { "3/12" } };

            Assert.That(AudiobookTrackOrder.ReadTrackNumber(tags), Is.EqualTo(3));
        }

        [Test]
        public void loose_intro_file_goes_before_disc_folders()
        {
            var order = Order(T("/b/Book/CD1/01.mp3"), T("/b/Book/Intro.mp3"), T("/b/Book/CD2/01.mp3"));

            Assert.That(order.First(), Is.EqualTo("/b/Book/Intro.mp3"));
        }

        [Test]
        public void the_shared_book_folder_name_does_not_count_as_a_disc()
        {
            var order = Order(T("/b/The Dark Tower Part Two/10.mp3"), T("/b/The Dark Tower Part Two/2.mp3"));

            Assert.That(order, Is.EqualTo(new[] { "/b/The Dark Tower Part Two/2.mp3", "/b/The Dark Tower Part Two/10.mp3" }));
        }

        [Test]
        public void part_numbers_on_import_follow_the_disc_order_and_stay_per_book()
        {
            var bookA = new NzbDrone.Core.Books.Book { Id = 1, Title = "1984" };
            var bookB = new NzbDrone.Core.Books.Book { Id = 2, Title = "Animal Farm" };

            LocalBook Local(string path, NzbDrone.Core.Books.Book book) => new LocalBook
            {
                Path = path,
                Book = book,
                Quality = new QualityModel(Quality.MP3)
            };

            var files = new List<LocalBook>
            {
                Local("/dl/1984/Part 2/Chapter 1-3.mp3", bookA),
                Local("/dl/Shared/Animal Farm 02.mp3", bookB),
                Local("/dl/1984/Part 10/Chapter 1.mp3", bookA),
                Local("/dl/1984/Part 1/Chapter 1-2.mp3", bookA),
                Local("/dl/Shared/Animal Farm 01.mp3", bookB)
            };

            PartAssignmentHelper.NormalizeLocalBooksByEdition(files);

            Assert.That(files.Single(f => f.Path.EndsWith("Part 1/Chapter 1-2.mp3")).Part, Is.EqualTo(1));
            Assert.That(files.Single(f => f.Path.EndsWith("Part 2/Chapter 1-3.mp3")).Part, Is.EqualTo(2));
            Assert.That(files.Single(f => f.Path.EndsWith("Part 10/Chapter 1.mp3")).Part, Is.EqualTo(3));
            Assert.That(files.Where(f => f.Book == bookA).All(f => f.PartCount == 3), Is.True);

            Assert.That(files.Single(f => f.Path.EndsWith("Animal Farm 01.mp3")).Part, Is.EqualTo(1));
            Assert.That(files.Single(f => f.Path.EndsWith("Animal Farm 02.mp3")).Part, Is.EqualTo(2));
            Assert.That(files.Where(f => f.Book == bookB).All(f => f.PartCount == 2), Is.True);
        }
    }
}
