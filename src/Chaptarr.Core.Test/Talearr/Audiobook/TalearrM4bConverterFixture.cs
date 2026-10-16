using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using NLog;
using NUnit.Framework;
using NzbDrone.Core.MediaFiles;
using NzbDrone.Core.Talearr.Audiobook.M4b;

namespace Chaptarr.Core.Test.Talearr.Audiobook
{
    // Runs the real ffmpeg on tiny generated files. Skipped when ffmpeg/ffprobe are not on PATH.
    [TestFixture]
    public class TalearrM4bConverterFixture
    {
        private string _folder;
        private string _ffmpeg;
        private string _ffprobe;

        private static string FindTool(string name)
        {
            foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? string.Empty).Split(Path.PathSeparator))
            {
                var candidate = Path.Combine(dir, name);
                if (File.Exists(candidate))
                {
                    return candidate;
                }
            }

            return null;
        }

        [SetUp]
        public void SetUp()
        {
            _ffmpeg = FindTool("ffmpeg");
            _ffprobe = FindTool("ffprobe");
            _folder = Path.Combine(Path.GetTempPath(), "talearr-m4b-test-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_folder);
        }

        [TearDown]
        public void TearDown()
        {
            if (Directory.Exists(_folder))
            {
                Directory.Delete(_folder, true);
            }
        }

        private IExternalToolsService Tools(bool available = true)
        {
            var (tools, fake) = Fake<IExternalToolsService>.Create();
            fake.On(nameof(IExternalToolsService.IsFFmpegAvailable), _ => available && _ffmpeg != null)
                .On(nameof(IExternalToolsService.IsFFprobeAvailable), _ => available && _ffprobe != null)
                .On(nameof(IExternalToolsService.GetFFmpegPath), _ => _ffmpeg)
                .On(nameof(IExternalToolsService.GetFFprobePath), _ => _ffprobe);
            return tools;
        }

        private void RequireFfmpeg()
        {
            if (_ffmpeg == null || _ffprobe == null)
            {
                Assert.Ignore("ffmpeg/ffprobe not installed");
            }
        }

        private string MakeTone(string relativePath, double seconds, string codecArgs, string title)
        {
            var path = Path.Combine(_folder, relativePath);
            Directory.CreateDirectory(Path.GetDirectoryName(path));

            var args = new List<string> { "-hide_banner", "-loglevel", "error", "-y", "-f", "lavfi", "-i", $"sine=frequency=440:sample_rate=44100:duration={seconds.ToString(System.Globalization.CultureInfo.InvariantCulture)}" };
            args.AddRange(codecArgs.Split(' '));
            args.AddRange(new[] { "-metadata", "title=" + title, path });

            var result = new M4bProcessRunner().Run(_ffmpeg, args, TimeSpan.FromMinutes(1));
            Assert.That(result.Succeeded, Is.True, result.StandardError);
            return path;
        }

        private M4bSourceTrack Probe(string path)
        {
            var result = new M4bProcessRunner().Run(_ffprobe, new[] { "-v", "error", "-print_format", "json", "-show_format", "-show_streams", "-show_chapters", path }, TimeSpan.FromMinutes(1));
            return M4bProbeParser.Parse(path, result.StandardOutput);
        }

        [Test]
        public void merges_mp3_files_into_a_chaptered_m4b_with_tags_and_cover()
        {
            RequireFfmpeg();

            var inputs = new List<string>
            {
                MakeTone("Book/Part 1/01.mp3", 3, "-c:a libmp3lame -b:a 64k", "Opening"),
                MakeTone("Book/Part 1/02.mp3", 2, "-c:a libmp3lame -b:a 64k", "Middle"),
                MakeTone("Book/Part 2/01.mp3", 4, "-c:a libmp3lame -b:a 64k", "Ending")
            };

            var cover = Path.Combine(_folder, "Book", "cover.jpg");
            var coverResult = new M4bProcessRunner().Run(_ffmpeg, new[] { "-hide_banner", "-loglevel", "error", "-y", "-f", "lavfi", "-i", "color=c=blue:s=64x64", "-frames:v", "1", cover }, TimeSpan.FromMinutes(1));
            Assert.That(coverResult.Succeeded, Is.True, coverResult.StandardError);

            var output = Path.Combine(_folder, "Book", "Book.m4b");
            var progress = new List<double>();

            var outcome = new TalearrM4bConverter(Tools(), new M4bProcessRunner(), LogManager.GetCurrentClassLogger()).Convert(new M4bConversionJob
            {
                Inputs = inputs,
                OutputPath = output,
                CoverPath = cover,
                Metadata = new M4bMetadata { Title = "The Book", Author = "An Author", Narrator = "A Narrator", Year = "2001", Series = "Saga", SeriesPosition = "2" },
                Encode = new M4bEncodeSettings { BitrateKbps = 64 }
            }, (p, _) => progress.Add(p));

            Assert.That(outcome.Success, Is.True, outcome.Error);
            Assert.That(File.Exists(output), Is.True);
            Assert.That(inputs.All(File.Exists), Is.True, "sources are never touched by the converter");
            Assert.That(Directory.Exists(Path.Combine(_folder, "Book", TalearrM4bConverter.PartialFolderName)), Is.False, "the partial folder is cleaned up");
            Assert.That(progress.Last(), Is.EqualTo(100));

            var merged = Probe(output);
            Assert.That(merged.Codec, Is.EqualTo("aac"));
            Assert.That(merged.DurationSeconds, Is.EqualTo(9).Within(0.3));
            Assert.That(merged.HasEmbeddedCover, Is.True);
            Assert.That(merged.Chapters.Select(c => c.Title), Is.EqualTo(new[] { "Opening", "Middle", "Ending" }));
            Assert.That(merged.Chapters[1].Start, Is.EqualTo(3).Within(0.1));
            Assert.That(merged.Chapters[2].Start, Is.EqualTo(5).Within(0.1));
            Assert.That(merged.Tags["title"], Is.EqualTo("The Book"));
            Assert.That(merged.Tags["artist"], Is.EqualTo("An Author"));
            Assert.That(merged.Tags["composer"], Is.EqualTo("A Narrator"));
            Assert.That(outcome.ChapterCount, Is.EqualTo(3));
        }

        [Test]
        public void copies_aac_sources_without_re_encoding_and_keeps_their_chapters()
        {
            RequireFfmpeg();

            var inputs = new List<string>
            {
                MakeTone("Book/a.m4a", 2, "-c:a aac -b:a 64k", "Alpha"),
                MakeTone("Book/b.m4a", 3, "-c:a aac -b:a 64k", "Beta")
            };

            var output = Path.Combine(_folder, "Book", "Book.m4b");

            var outcome = new TalearrM4bConverter(Tools(), new M4bProcessRunner(), LogManager.GetCurrentClassLogger()).Convert(new M4bConversionJob
            {
                Inputs = inputs,
                OutputPath = output,
                Metadata = new M4bMetadata { Title = "Copy" }
            });

            Assert.That(outcome.Success, Is.True, outcome.Error);
            Assert.That(outcome.StreamCopied, Is.True);
            Assert.That(Probe(output).Chapters.Select(c => c.Title), Is.EqualTo(new[] { "Alpha", "Beta" }));
        }

        [Test]
        public void a_duration_mismatch_fails_and_leaves_nothing_behind()
        {
            RequireFfmpeg();

            var inputs = new List<string>
            {
                MakeTone("Book/01.mp3", 3, "-c:a libmp3lame", "One"),
                MakeTone("Book/02.mp3", 3, "-c:a libmp3lame", "Two")
            };

            var output = Path.Combine(_folder, "Book", "Book.m4b");

            // ffmpeg is asked to stop after 2 seconds: the result is far shorter than the sources.
            var runner = new TruncatingRunner(new M4bProcessRunner());
            var outcome = new TalearrM4bConverter(Tools(), runner, LogManager.GetCurrentClassLogger()).Convert(new M4bConversionJob
            {
                Inputs = inputs,
                OutputPath = output,
                Metadata = new M4bMetadata { Title = "Short" }
            });

            Assert.That(outcome.Success, Is.False);
            Assert.That(outcome.Error, Does.Contain("1%"));
            Assert.That(File.Exists(output), Is.False);
            Assert.That(Directory.Exists(Path.Combine(_folder, "Book", TalearrM4bConverter.PartialFolderName)), Is.False);
            Assert.That(inputs.All(File.Exists), Is.True);
        }

        [Test]
        public void an_existing_target_is_never_overwritten()
        {
            var output = Path.Combine(_folder, "Book.m4b");
            File.WriteAllText(output, "keep me");
            var input = Path.Combine(_folder, "01.mp3");
            File.WriteAllText(input, "x");

            var outcome = new TalearrM4bConverter(Tools(), new M4bProcessRunner(), LogManager.GetCurrentClassLogger()).Convert(new M4bConversionJob
            {
                Inputs = new List<string> { input, input },
                OutputPath = output
            });

            if (_ffmpeg == null)
            {
                Assert.That(outcome.Error, Does.Contain("FFmpeg"));
            }
            else
            {
                Assert.That(outcome.Error, Does.Contain("already exists"));
            }

            Assert.That(File.ReadAllText(output), Is.EqualTo("keep me"));
        }

        [Test]
        public void missing_ffmpeg_is_reported()
        {
            var outcome = new TalearrM4bConverter(Tools(available: false), new M4bProcessRunner(), LogManager.GetCurrentClassLogger()).Convert(new M4bConversionJob
            {
                Inputs = new List<string> { "/x/1.mp3", "/x/2.mp3" },
                OutputPath = "/x/out.m4b"
            });

            Assert.That(outcome.Success, Is.False);
            Assert.That(outcome.Error, Does.Contain("FFmpeg"));
        }

        // Adds "-t 2" before the output of the merge so the M4B comes out too short.
        private sealed class TruncatingRunner : IM4bProcessRunner
        {
            private readonly IM4bProcessRunner _inner;

            public TruncatingRunner(IM4bProcessRunner inner)
            {
                _inner = inner;
            }

            public M4bProcessResult Run(string fileName, IReadOnlyList<string> arguments, TimeSpan timeout, Action<string> onOutputLine = null, CancellationToken cancellationToken = default)
            {
                var args = arguments.ToList();
                if (args.Contains("concat"))
                {
                    args.Insert(args.Count - 1, "-t");
                    args.Insert(args.Count - 1, "2");
                }

                return _inner.Run(fileName, args, timeout, onOutputLine, cancellationToken);
            }
        }
    }
}
