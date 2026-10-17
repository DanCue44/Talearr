using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using NzbDrone.Core.Books;
using NzbDrone.Core.Indexers;
using NzbDrone.Core.Talearr.Soulseek;

namespace Chaptarr.Core.Test.Talearr.Soulseek
{
    [TestFixture]
    public class TalearrSoulseekCandidatesFixture
    {
        internal static SlskdFile File(string path, long size = 50_000_000, int? bitRate = 64, int? length = 3600, bool locked = false)
        {
            return new SlskdFile { Filename = path, Size = size, BitRate = bitRate, Length = length, IsLocked = locked };
        }

        internal static SlskdSearchResponse Peer(string user, params SlskdFile[] files)
        {
            return new SlskdSearchResponse
            {
                Username = user,
                HasFreeUploadSlot = true,
                QueueLength = 0,
                UploadSpeed = 1024 * 1024,
                Files = files.ToList(),
                FileCount = files.Length
            };
        }

        private static TalearrSoulseekSettings Settings()
        {
            return TalearrSoulseekSettings.Default;
        }

        [Test]
        public void an_audiobook_folder_is_one_candidate_with_cover_art()
        {
            var peer = Peer("alice",
                File(@"@@abc\Audiobooks\Brandon Sanderson\Mistborn 01 - The Final Empire\01.mp3"),
                File(@"@@abc\Audiobooks\Brandon Sanderson\Mistborn 01 - The Final Empire\02.mp3"),
                File(@"@@abc\Audiobooks\Brandon Sanderson\Mistborn 01 - The Final Empire\cover.jpg", 200_000, null, null),
                File(@"@@abc\Audiobooks\Brandon Sanderson\Mistborn 01 - The Final Empire\notes.txt", 100, null, null));

            var candidates = TalearrSoulseekCandidates.Build(new[] { peer }, BookMediaType.Audiobook, Settings());

            Assert.That(candidates, Has.Count.EqualTo(1));
            var c = candidates[0];
            Assert.That(c.MediaFileCount, Is.EqualTo(2));
            Assert.That(c.Files.Select(f => TalearrSoulseekCandidates.FileName(f.Filename)), Is.EquivalentTo(new[] { "01.mp3", "02.mp3", "cover.jpg" }));
            Assert.That(c.Title, Is.EqualTo("Brandon Sanderson - Mistborn 01 - The Final Empire [MP3 64kbps]"));
            Assert.That(c.DurationSeconds, Is.EqualTo(7200));
        }

        [Test]
        public void disc_folders_join_their_parent()
        {
            var peer = Peer("bob",
                File(@"Books\Dune\CD1\Dune 01.mp3"),
                File(@"Books\Dune\CD2\Dune 02.mp3"));

            var candidates = TalearrSoulseekCandidates.Build(new[] { peer }, BookMediaType.Audiobook, Settings());

            Assert.That(candidates, Has.Count.EqualTo(1));
            Assert.That(candidates[0].HasDiscFolders, Is.True);
            Assert.That(candidates[0].Directory, Is.EqualTo(@"Books\Dune"));
            Assert.That(candidates[0].Title, Does.StartWith("Dune"));
        }

        [Test]
        public void disc_folders_whose_file_names_collide_are_skipped()
        {
            var peer = Peer("bob",
                File(@"Books\Dune\CD1\01.mp3"),
                File(@"Books\Dune\CD2\01.mp3"));

            Assert.That(TalearrSoulseekCandidates.Build(new[] { peer }, BookMediaType.Audiobook, Settings()), Is.Empty);
        }

        [Test]
        public void a_folder_of_single_file_books_offers_each_book()
        {
            var peer = Peer("carol",
                File(@"Audio\Agatha Christie\Murder on the Orient Express.m4b"),
                File(@"Audio\Agatha Christie\Death on the Nile.m4b"));

            var candidates = TalearrSoulseekCandidates.Build(new[] { peer }, BookMediaType.Audiobook, Settings());

            Assert.That(candidates, Has.Count.EqualTo(2));
            Assert.That(candidates.Select(c => c.Title), Has.Some.EqualTo("Agatha Christie - Death on the Nile [M4B]"));
            Assert.That(candidates.All(c => c.Files.Count == 1), Is.True);
        }

        [Test]
        public void m4b_parts_of_one_book_stay_together()
        {
            var peer = Peer("carol",
                File(@"Audio\Stephen King\The Stand\The Stand Part 1.m4b"),
                File(@"Audio\Stephen King\The Stand\The Stand Part 2.m4b"));

            var candidates = TalearrSoulseekCandidates.Build(new[] { peer }, BookMediaType.Audiobook, Settings());

            Assert.That(candidates, Has.Count.EqualTo(1));
            Assert.That(candidates[0].Files, Has.Count.EqualTo(2));
        }

        [Test]
        public void a_folder_with_locked_audio_is_not_offered()
        {
            var peer = Peer("dave", File(@"A\Book\01.mp3"));
            peer.LockedFiles = new List<SlskdFile> { File(@"A\Book\02.mp3", locked: true) };
            peer.LockedFileCount = 1;

            Assert.That(TalearrSoulseekCandidates.Build(new[] { peer }, BookMediaType.Audiobook, Settings()), Is.Empty);
        }

        [Test]
        public void locked_files_are_never_requested()
        {
            var peer = Peer("dave", File(@"A\Book\01.mp3"), File(@"A\Book\02.mp3", locked: true));

            var candidates = TalearrSoulseekCandidates.Build(new[] { peer }, BookMediaType.Audiobook, Settings());

            Assert.That(candidates.Single().Files.Select(f => f.Filename), Is.EqualTo(new[] { @"A\Book\01.mp3" }));
        }

        [Test]
        public void etiquette_skips_busy_slow_and_slotless_peers()
        {
            var settings = Settings();
            settings.MinUploadSpeedKbps = 100;

            var noSlot = Peer("noslot", File(@"A\Book\01.mp3"));
            noSlot.HasFreeUploadSlot = false;
            var busy = Peer("busy", File(@"B\Book\01.mp3"));
            busy.QueueLength = 500;
            var slow = Peer("slow", File(@"C\Book\01.mp3"));
            slow.UploadSpeed = 10 * 1024;
            var fine = Peer("fine", File(@"D\Book\01.mp3"));

            var candidates = TalearrSoulseekCandidates.Build(new[] { noSlot, busy, slow, fine }, BookMediaType.Audiobook, settings);

            Assert.That(candidates.Select(c => c.Username), Is.EqualTo(new[] { "fine" }));
        }

        [Test]
        public void slotless_peers_are_offered_when_the_setting_is_off()
        {
            var settings = Settings();
            settings.RequireFreeUploadSlot = false;
            var noSlot = Peer("noslot", File(@"A\Book\01.mp3"));
            noSlot.HasFreeUploadSlot = false;

            Assert.That(TalearrSoulseekCandidates.Build(new[] { noSlot }, BookMediaType.Audiobook, settings), Has.Count.EqualTo(1));
        }

        [Test]
        public void excluded_peers_are_skipped()
        {
            var peer = Peer("failed-before", File(@"A\Book\01.mp3"));

            Assert.That(TalearrSoulseekCandidates.Build(new[] { peer }, BookMediaType.Audiobook, Settings(), new HashSet<string> { "failed-before" }), Is.Empty);
        }

        [Test]
        public void ebooks_are_single_files_in_the_best_format()
        {
            var peer = Peer("erin",
                File(@"Ebooks\Frank Herbert - Dune.mobi", 2_000_000, null, null),
                File(@"Ebooks\Frank Herbert - Dune.epub", 1_000_000, null, null),
                File(@"Ebooks\Frank Herbert - Children of Dune.pdf", 5_000_000, null, null),
                File(@"Ebooks\cover.jpg", 1000, null, null));

            var candidates = TalearrSoulseekCandidates.Build(new[] { peer }, BookMediaType.Ebook, Settings());

            Assert.That(candidates, Has.Count.EqualTo(2));
            var dune = candidates.Single(c => c.Title.Contains("- Dune"));
            Assert.That(dune.Files.Single().Filename, Does.EndWith(".epub"));
            Assert.That(dune.Title, Is.EqualTo("Frank Herbert - Dune [EPUB]"));
        }

        [Test]
        public void audio_is_not_offered_for_an_ebook_and_the_other_way_round()
        {
            var peer = Peer("frank", File(@"A\Book\01.mp3"), File(@"B\Book.epub", 1000, null, null));

            Assert.That(TalearrSoulseekCandidates.Build(new[] { peer }, BookMediaType.Ebook, Settings()).Single().Format, Is.EqualTo("EPUB"));
            Assert.That(TalearrSoulseekCandidates.Build(new[] { peer }, BookMediaType.Audiobook, Settings()).Single().Format, Is.EqualTo("MP3"));
        }

        [Test]
        public void releases_use_the_unknown_protocol_and_no_indexer()
        {
            var candidate = TalearrSoulseekCandidates.Build(new[] { Peer("g", File(@"A\Book\01.m4b")) }, BookMediaType.Audiobook, Settings()).Single();

            var release = TalearrSoulseekCandidates.ToRelease(candidate);

            Assert.That(release.DownloadProtocol, Is.EqualTo(DownloadProtocol.Unknown));
            Assert.That((int)release.DownloadProtocol, Is.EqualTo(0));
            Assert.That(release.IndexerId, Is.EqualTo(0));
            Assert.That(release.Guid, Is.EqualTo("slskd-" + candidate.Key));
            Assert.That(release.Categories, Is.EqualTo(new[] { 3030 }));
        }

        [Test]
        public void the_key_is_stable_for_the_same_folder()
        {
            var a = TalearrSoulseekCandidates.Build(new[] { Peer("h", File(@"A\Book\01.mp3"), File(@"A\Book\02.mp3")) }, BookMediaType.Audiobook, Settings()).Single();
            var b = TalearrSoulseekCandidates.Build(new[] { Peer("h", File(@"A\Book\02.mp3"), File(@"A\Book\01.mp3")) }, BookMediaType.Audiobook, Settings()).Single();
            var other = TalearrSoulseekCandidates.Build(new[] { Peer("i", File(@"A\Book\01.mp3"), File(@"A\Book\02.mp3")) }, BookMediaType.Audiobook, Settings()).Single();

            Assert.That(a.Key, Is.EqualTo(b.Key));
            Assert.That(a.Key, Is.Not.EqualTo(other.Key));
        }

        [TestCase(@"Music\Audiobooks\Terry Pratchett\Guards! Guards!", null, "Terry Pratchett - Guards! Guards!")]
        [TestCase(@"@@xyz\complete\Sanderson\Sanderson - Elantris", null, "Sanderson - Elantris")]
        [TestCase(@"Dune", "Dune", "Dune")]
        [TestCase(@"Ebooks\Herbert", "Frank Herbert - Dune", "Frank Herbert - Dune")]
        public void titles_keep_the_meaningful_names(string directory, string singleName, string expected)
        {
            var title = TalearrSoulseekCandidates.BuildTitle(directory, singleName, new TalearrSoulseekCandidate());

            Assert.That(title, Is.EqualTo(expected));
        }

        [TestCase("Brandon Sanderson", "The Final Empire", "Brandon Sanderson The Final Empire")]
        [TestCase("J.R.R. Tolkien", "The Fellowship of the Ring: Being the First Part", "Tolkien The Fellowship of Ring Being First Part")]
        public void search_text_is_short_and_plain(string author, string title, string expected)
        {
            Assert.That(TalearrSoulseekService.BuildSearchText(author, title), Is.EqualTo(expected));
        }
    }
}
