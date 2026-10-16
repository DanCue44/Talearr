using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using NzbDrone.Core.MediaFiles;
using NzbDrone.Core.Qualities;
using NzbDrone.Core.Talearr.Audiobook.M4b;

namespace Chaptarr.Core.Test.Talearr.Audiobook
{
    [TestFixture]
    public class M4bPlanningFixture
    {
        private static M4bSourceTrack Track(string path, double duration, string title = null, params M4bChapter[] chapters)
        {
            return new M4bSourceTrack
            {
                Path = path,
                DurationSeconds = duration,
                Title = title,
                Codec = "mp3",
                Chapters = chapters.ToList()
            };
        }

        [Test]
        public void one_chapter_per_file_at_the_file_boundaries()
        {
            var chapters = M4bChapterPlanner.Plan(new[]
            {
                Track("/b/01.mp3", 100, "Opening"),
                Track("/b/02.mp3", 250.5, "Middle"),
                Track("/b/03.mp3", 60, "End")
            });

            Assert.That(chapters.Select(c => c.Title), Is.EqualTo(new[] { "Opening", "Middle", "End" }));
            Assert.That(chapters.Select(c => c.Start), Is.EqualTo(new[] { 0, 100, 350.5 }));
            Assert.That(chapters.Last().End, Is.EqualTo(410.5));
        }

        [Test]
        public void existing_chapters_inside_a_file_are_kept_and_shifted()
        {
            var chapters = M4bChapterPlanner.Plan(new[]
            {
                Track("/b/part1.m4b", 1000, "Part 1", new M4bChapter(0, 400, "Prologue"), new M4bChapter(400, 1000, "Chapter 1")),
                Track("/b/part2.m4b", 500, "Part 2", new M4bChapter(0, 200, "Chapter 2"), new M4bChapter(200, 500, "Chapter 3"))
            });

            Assert.That(chapters.Select(c => c.Title), Is.EqualTo(new[] { "Prologue", "Chapter 1", "Chapter 2", "Chapter 3" }));
            Assert.That(chapters.Select(c => c.Start), Is.EqualTo(new[] { 0, 400, 1000, 1200 }));
            Assert.That(chapters.Last().End, Is.EqualTo(1500));
        }

        [Test]
        public void a_single_embedded_chapter_counts_as_one_file_chapter()
        {
            var chapters = M4bChapterPlanner.Plan(new[]
            {
                Track("/b/a.m4a", 10, "Alpha", new M4bChapter(0, 10, "whatever")),
                Track("/b/b.m4a", 10, "Beta")
            });

            Assert.That(chapters.Select(c => c.Title), Is.EqualTo(new[] { "Alpha", "Beta" }));
        }

        [Test]
        public void file_names_lose_track_numbers_and_shared_text()
        {
            var chapters = M4bChapterPlanner.Plan(new[]
            {
                Track("/a/Steve Jobs/101 - Walter Isaacson - Steve Jobs - 2011 - Introduction To Series.m4a", 10, "Steve Jobs"),
                Track("/a/Steve Jobs/104 - Walter Isaacson - Steve Jobs - 2011 - Childhood - Abandoned & Chosen.m4a", 10, "Steve Jobs"),
                Track("/a/Steve Jobs/202 - Walter Isaacson - Steve Jobs - 2011 - Apple is Born.m4a", 10, "Steve Jobs")
            });

            Assert.That(chapters.Select(c => c.Title), Is.EqualTo(new[] { "Introduction To Series", "Childhood - Abandoned & Chosen", "Apple is Born" }));
        }

        [Test]
        public void repeated_names_in_disc_folders_get_the_folder_in_front()
        {
            var chapters = M4bChapterPlanner.Plan(new[]
            {
                Track("/dl/1984/Part 1/Chapter 1-2.mp3", 10),
                Track("/dl/1984/Part 1/Chapter 3-5.mp3", 10),
                Track("/dl/1984/Part 3/Chapter 1-2.mp3", 10)
            });

            Assert.That(chapters.Select(c => c.Title), Is.EqualTo(new[] { "Part 1 - Chapter 1-2", "Part 1 - Chapter 3-5", "Part 3 - Chapter 1-2" }));
        }

        [Test]
        public void identical_names_become_numbered_chapters()
        {
            var chapters = M4bChapterPlanner.Plan(new[]
            {
                Track("/a/Animal Farm/01 - Animal Farm.m4a", 10, "Animal Farm"),
                Track("/a/Animal Farm/02 - Animal Farm.m4a", 10, "Animal Farm"),
                Track("/a/Animal Farm/03 - Animal Farm.m4a", 10, "Animal Farm")
            });

            Assert.That(chapters.Select(c => c.Title), Is.EqualTo(new[] { "Chapter 1", "Chapter 2", "Chapter 3" }));
        }

        [Test]
        public void ffmetadata_escapes_and_writes_chapters_in_milliseconds()
        {
            var text = M4bFfmpegCommand.BuildFfMetadata(
                new M4bMetadata { Title = "A=B; #1", Author = "Me", Narrator = "Ray Porter", Year = "2020", Series = "Bobiverse", SeriesPosition = "1" },
                new List<M4bChapter> { new M4bChapter(0, 1.5, "One\\Two"), new M4bChapter(1.5, 3, "Three") });

            Assert.That(text, Does.StartWith(";FFMETADATA1\n"));
            Assert.That(text, Does.Contain("title=A\\=B\\; \\#1\n"));
            Assert.That(text, Does.Contain("composer=Ray Porter\n"));
            Assert.That(text, Does.Contain("grouping=Bobiverse \\#1\n"));
            Assert.That(text, Does.Contain("media_type=2\n"));
            Assert.That(text, Does.Contain("[CHAPTER]\nTIMEBASE=1/1000\nSTART=0\nEND=1500\ntitle=One\\\\Two\n"));
            Assert.That(text, Does.Contain("START=1500\nEND=3000\ntitle=Three\n"));
        }

        [Test]
        public void concat_list_quotes_paths_with_apostrophes()
        {
            var list = M4bFfmpegCommand.BuildConcatList(new[] { "/a/Ender's Game/01.mp3" });

            Assert.That(list, Is.EqualTo("ffconcat version 1.0\nfile '/a/Ender'\\''s Game/01.mp3'\n"));
        }

        [Test]
        public void aac_sources_with_matching_format_are_copied_otherwise_encoded()
        {
            var aac = new[]
            {
                new M4bSourceTrack { Codec = "aac", SampleRate = 44100, Channels = 2 },
                new M4bSourceTrack { Codec = "aac", SampleRate = 44100, Channels = 2 }
            };

            Assert.That(M4bFfmpegCommand.CanStreamCopy(aac), Is.True);

            aac[1].SampleRate = 22050;
            Assert.That(M4bFfmpegCommand.CanStreamCopy(aac), Is.False);

            Assert.That(M4bFfmpegCommand.CanStreamCopy(new[] { new M4bSourceTrack { Codec = "mp3", SampleRate = 44100, Channels = 2 } }), Is.False);
        }

        [Test]
        public void bitrate_is_not_raised_above_the_source()
        {
            var tracks = new[] { new M4bSourceTrack { BitRate = 32000 } };

            Assert.That(M4bFfmpegCommand.GetTargetBitrateKbps(tracks, new M4bEncodeSettings { BitrateKbps = 64, NoUpscale = true }), Is.EqualTo(32));
            Assert.That(M4bFfmpegCommand.GetTargetBitrateKbps(tracks, new M4bEncodeSettings { BitrateKbps = 64, NoUpscale = false }), Is.EqualTo(64));
            Assert.That(M4bFfmpegCommand.GetTargetBitrateKbps(new[] { new M4bSourceTrack { BitRate = 128000 } }, new M4bEncodeSettings { BitrateKbps = 64 }), Is.EqualTo(64));
        }

        [Test]
        public void arguments_map_audio_metadata_chapters_and_cover()
        {
            var args = M4bFfmpegCommand.BuildArguments("/w/in.ffconcat", "/w/meta.txt", "/b/cover.jpg", "/b/out.m4b", streamCopy: false, bitrateKbps: 64, mono: true);
            var joined = string.Join(" ", args);

            Assert.That(joined, Does.Contain("-f concat -safe 0 -i /w/in.ffconcat -i /w/meta.txt -i /b/cover.jpg"));
            Assert.That(joined, Does.Contain("-map 0:a:0 -map_metadata 1 -map_chapters 1"));
            Assert.That(joined, Does.Contain("-map 2:v:0 -c:v copy -disposition:v:0 attached_pic"));
            Assert.That(joined, Does.Contain("-c:a aac -b:a 64k -ac 1"));
            Assert.That(args.Last(), Is.EqualTo("/b/out.m4b"));

            var copy = string.Join(" ", M4bFfmpegCommand.BuildArguments("/w/in", "/w/meta", null, "/b/out.m4b", streamCopy: true, bitrateKbps: 64, mono: false));
            Assert.That(copy, Does.Contain("-c:a copy"));
            Assert.That(copy, Does.Not.Contain("attached_pic"));
        }

        [TestCase(36000, 36000, true)]
        [TestCase(36000, 36359, true)]
        [TestCase(36000, 35641, true)]
        [TestCase(36000, 36361, false)]
        [TestCase(36000, 35000, false)]
        [TestCase(0, 10, false)]
        public void duration_must_be_within_one_percent(double expected, double actual, bool ok)
        {
            Assert.That(M4bFfmpegCommand.IsDurationWithinTolerance(expected, actual), Is.EqualTo(ok));
        }

        [TestCase("out_time_us=1500000", 1.5)]
        [TestCase("out_time_ms=2000000", 2.0)]
        [TestCase("progress=continue", null)]
        [TestCase("out_time=00:00:01.500000", null)]
        public void progress_lines_are_parsed(string line, double? seconds)
        {
            Assert.That(TalearrM4bConverter.ParseProgressSeconds(line), Is.EqualTo(seconds));
        }

        [Test]
        public void ffprobe_json_is_read()
        {
            const string json = @"{
  ""streams"": [
    { ""codec_type"": ""audio"", ""codec_name"": ""aac"", ""sample_rate"": ""44100"", ""channels"": 2, ""bit_rate"": ""64000"" },
    { ""codec_type"": ""video"", ""codec_name"": ""mjpeg"", ""disposition"": { ""attached_pic"": 1 } }
  ],
  ""chapters"": [
    { ""start_time"": ""0.000000"", ""end_time"": ""12.500000"", ""tags"": { ""title"": ""One"" } },
    { ""start_time"": ""12.500000"", ""end_time"": ""30.000000"", ""tags"": { ""title"": ""Two"" } }
  ],
  ""format"": { ""duration"": ""30.000000"", ""bit_rate"": ""65000"", ""tags"": { ""title"": ""Track Title"", ""album"": ""Book"" } }
}";

            var track = M4bProbeParser.Parse("/b/x.m4a", json);

            Assert.That(track.Codec, Is.EqualTo("aac"));
            Assert.That(track.SampleRate, Is.EqualTo(44100));
            Assert.That(track.Channels, Is.EqualTo(2));
            Assert.That(track.BitRate, Is.EqualTo(64000));
            Assert.That(track.DurationSeconds, Is.EqualTo(30));
            Assert.That(track.HasEmbeddedCover, Is.True);
            Assert.That(track.Title, Is.EqualTo("Track Title"));
            Assert.That(track.Chapters.Select(c => c.Title), Is.EqualTo(new[] { "One", "Two" }));
            Assert.That(track.Chapters[1].Start, Is.EqualTo(12.5));
        }

        private static BookFile File(string path, int editionId = 1, int part = 0)
        {
            return new BookFile { Path = path, EditionId = editionId, Part = part, MediaType = "audiobook", Quality = new QualityModel(Quality.MP3) };
        }

        [Test]
        public void library_plan_is_per_edition_and_in_part_order()
        {
            var plans = M4bLibraryPlanner.Plan(new[]
            {
                File("/a/Book/02.mp3", 1, 2),
                File("/a/Book/01.mp3", 1, 1),
                File("/a/Book Other/b.m4a", 2, 2),
                File("/a/Book Other/a.m4a", 2, 1),
                new BookFile { Path = "/e/Book/book.epub", EditionId = 3, MediaType = "ebook" }
            });

            Assert.That(plans.Count, Is.EqualTo(2), "ebooks are ignored, editions are kept apart");
            Assert.That(plans[0].Files.Select(f => f.Path), Is.EqualTo(new[] { "/a/Book/01.mp3", "/a/Book/02.mp3" }));
            Assert.That(plans[0].Folder, Is.EqualTo("/a/Book"));
            Assert.That(plans[1].Files.Select(f => f.Path), Is.EqualTo(new[] { "/a/Book Other/a.m4a", "/a/Book Other/b.m4a" }));
            Assert.That(plans.All(p => p.CanConvert), Is.True);
        }

        [Test]
        public void library_plan_uses_the_book_folder_above_disc_folders()
        {
            var plan = M4bLibraryPlanner.Plan(new[] { File("/a/Book/CD2/01.mp3"), File("/a/Book/CD1/01.mp3") }).Single();

            Assert.That(plan.Folder, Is.EqualTo("/a/Book"));
            Assert.That(plan.Files.Select(f => f.Path), Is.EqualTo(new[] { "/a/Book/CD1/01.mp3", "/a/Book/CD2/01.mp3" }));
        }

        [Test]
        public void library_plan_skips_single_files_and_other_codecs()
        {
            Assert.That(M4bLibraryPlanner.Plan(new[] { File("/a/Book/book.m4b") }).Single().SkipReason, Does.Contain("single file"));
            Assert.That(M4bLibraryPlanner.Plan(new[] { File("/a/Book/1.flac"), File("/a/Book/2.flac") }).Single().SkipReason, Does.Contain(".flac"));
        }
    }
}
