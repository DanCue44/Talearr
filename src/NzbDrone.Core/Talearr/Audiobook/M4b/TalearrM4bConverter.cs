using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using NLog;
using NzbDrone.Core.MediaFiles;

namespace NzbDrone.Core.Talearr.Audiobook.M4b
{
    public sealed class M4bConversionJob
    {
        // Source files in playing order.
        public List<string> Inputs { get; set; } = new List<string>();
        public string OutputPath { get; set; }
        public M4bMetadata Metadata { get; set; } = new M4bMetadata();

        // Optional cover image next to the book (cover.jpg). The first embedded cover is used otherwise.
        public string CoverPath { get; set; }
        public M4bEncodeSettings Encode { get; set; } = new M4bEncodeSettings();
    }

    public sealed class M4bConversionOutcome
    {
        public bool Success { get; set; }
        public string Error { get; set; }
        public string OutputPath { get; set; }
        public long OutputSize { get; set; }
        public double ExpectedDurationSeconds { get; set; }
        public double DurationSeconds { get; set; }
        public int ChapterCount { get; set; }
        public bool StreamCopied { get; set; }

        public static M4bConversionOutcome Fail(string error)
        {
            return new M4bConversionOutcome { Success = false, Error = error };
        }
    }

    public interface ITalearrM4bConverter
    {
        bool IsAvailable();
        M4bConversionOutcome Convert(M4bConversionJob job, Action<double, string> progress = null);
    }

    // TaleArr: merges audio files into one chaptered M4B with ffmpeg. The result is written to
    // a partial file in a hidden folder next to the target, checked (audio stream, chapters, duration within
    // 1% of the sources) and only then renamed into place. Source files are never touched here.
    public class TalearrM4bConverter : ITalearrM4bConverter
    {
        public const string PartialFolderName = ".talearr-m4b";

        private static readonly TimeSpan ProbeTimeout = TimeSpan.FromMinutes(2);

        private readonly IExternalToolsService _externalTools;
        private readonly IM4bProcessRunner _runner;
        private readonly Logger _logger;

        public TalearrM4bConverter(IExternalToolsService externalTools, IM4bProcessRunner runner, Logger logger)
        {
            _externalTools = externalTools;
            _runner = runner;
            _logger = logger;
        }

        public bool IsAvailable()
        {
            return _externalTools.IsFFmpegAvailable() && _externalTools.IsFFprobeAvailable();
        }

        public M4bConversionOutcome Convert(M4bConversionJob job, Action<double, string> progress = null)
        {
            if (job?.Inputs == null || job.Inputs.Count == 0 || string.IsNullOrWhiteSpace(job.OutputPath))
            {
                return M4bConversionOutcome.Fail("Nothing to convert.");
            }

            if (!IsAvailable())
            {
                return M4bConversionOutcome.Fail("FFmpeg and FFprobe are required to convert to M4B and were not found.");
            }

            if (File.Exists(job.OutputPath))
            {
                return M4bConversionOutcome.Fail($"'{job.OutputPath}' already exists. Move or delete it, then convert again.");
            }

            var ffmpeg = _externalTools.GetFFmpegPath();
            var ffprobe = _externalTools.GetFFprobePath();

            progress?.Invoke(0, "Reading source files");

            var tracks = new List<M4bSourceTrack>();
            foreach (var input in job.Inputs)
            {
                if (!File.Exists(input))
                {
                    return M4bConversionOutcome.Fail($"Source file is missing: {input}");
                }

                var track = Probe(ffprobe, input);
                if (track == null || track.DurationSeconds <= 0 || string.IsNullOrWhiteSpace(track.Codec))
                {
                    return M4bConversionOutcome.Fail($"FFprobe could not read the audio of {input}");
                }

                tracks.Add(track);
            }

            var expected = tracks.Sum(t => t.DurationSeconds);
            var chapters = M4bChapterPlanner.Plan(tracks);
            var streamCopy = M4bFfmpegCommand.CanStreamCopy(tracks) && !(job.Encode?.Mono ?? false);
            var bitrate = M4bFfmpegCommand.GetTargetBitrateKbps(tracks, job.Encode);

            var outputFolder = Path.GetDirectoryName(job.OutputPath);
            // A hidden work folder next to the target keeps the final rename on the same disk, and
            // disk scans skip both dot folders and "*.partial~" files while ffmpeg is writing.
            var partialFolder = Path.Combine(outputFolder ?? string.Empty, PartialFolderName);
            var partialPath = Path.Combine(partialFolder, Path.GetFileName(job.OutputPath) + ".partial~");
            var workFolder = Path.Combine(Path.GetTempPath(), "talearr-m4b-" + Guid.NewGuid().ToString("N"));

            try
            {
                Directory.CreateDirectory(workFolder);
                Directory.CreateDirectory(partialFolder);

                var concatPath = Path.Combine(workFolder, "inputs.ffconcat");
                var metadataPath = Path.Combine(workFolder, "metadata.txt");
                File.WriteAllText(concatPath, M4bFfmpegCommand.BuildConcatList(job.Inputs));
                File.WriteAllText(metadataPath, M4bFfmpegCommand.BuildFfMetadata(job.Metadata, chapters));

                var cover = ResolveCover(ffmpeg, job, tracks, workFolder);

                DeleteQuietly(partialPath);

                var arguments = M4bFfmpegCommand.BuildArguments(concatPath, metadataPath, cover, partialPath, streamCopy, bitrate, job.Encode?.Mono ?? false);
                var timeout = TimeSpan.FromSeconds(Math.Max(3600, expected * 2));
                var lastReported = -1.0;

                _logger.Debug("TaleArr M4B: ffmpeg {0}", string.Join(" ", arguments));
                progress?.Invoke(1, streamCopy ? "Merging (no re-encode)" : string.Format(CultureInfo.InvariantCulture, "Encoding to AAC {0} kbps", bitrate));

                var result = _runner.Run(ffmpeg, arguments, timeout, line =>
                {
                    var seconds = ParseProgressSeconds(line);
                    if (seconds.HasValue && expected > 0)
                    {
                        var percent = Math.Min(97, Math.Max(1, seconds.Value / expected * 97));
                        if (percent - lastReported >= 1)
                        {
                            lastReported = percent;
                            progress?.Invoke(percent, string.Format(CultureInfo.InvariantCulture, "{0:0}%", percent));
                        }
                    }
                });

                if (!result.Succeeded)
                {
                    DeleteQuietly(partialPath);
                    var reason = result.TimedOut ? "timed out" : "failed: " + FirstLine(result.StandardError);
                    return M4bConversionOutcome.Fail("FFmpeg " + reason);
                }

                progress?.Invoke(98, "Verifying M4B");

                var verification = Verify(ffprobe, partialPath, expected, chapters.Count);
                if (!verification.Success)
                {
                    DeleteQuietly(partialPath);
                    return verification;
                }

                if (File.Exists(job.OutputPath))
                {
                    DeleteQuietly(partialPath);
                    return M4bConversionOutcome.Fail($"'{job.OutputPath}' appeared while converting. Nothing was changed.");
                }

                File.Move(partialPath, job.OutputPath);

                verification.OutputPath = job.OutputPath;
                verification.OutputSize = new FileInfo(job.OutputPath).Length;
                verification.StreamCopied = streamCopy;
                progress?.Invoke(100, "M4B verified");
                return verification;
            }
            catch (Exception ex)
            {
                _logger.Warn(ex, "TaleArr M4B conversion failed for {0}", job.OutputPath);
                DeleteQuietly(partialPath);
                return M4bConversionOutcome.Fail(ex.Message);
            }
            finally
            {
                try
                {
                    if (Directory.Exists(partialFolder) && !Directory.EnumerateFileSystemEntries(partialFolder).Any())
                    {
                        Directory.Delete(partialFolder);
                    }
                }
                catch (Exception ex)
                {
                    _logger.Debug(ex, "Could not remove {0}", partialFolder);
                }

                try
                {
                    if (Directory.Exists(workFolder))
                    {
                        Directory.Delete(workFolder, true);
                    }
                }
                catch (Exception ex)
                {
                    _logger.Debug(ex, "Could not remove TaleArr M4B work folder {0}", workFolder);
                }
            }
        }

        // "out_time_us=1234567" (microseconds; older ffmpeg also writes out_time_ms in microseconds).
        public static double? ParseProgressSeconds(string line)
        {
            if (string.IsNullOrWhiteSpace(line))
            {
                return null;
            }

            foreach (var key in new[] { "out_time_us=", "out_time_ms=" })
            {
                if (line.StartsWith(key, StringComparison.Ordinal) &&
                    long.TryParse(line.Substring(key.Length), NumberStyles.Integer, CultureInfo.InvariantCulture, out var micros) &&
                    micros >= 0)
                {
                    return micros / 1_000_000.0;
                }
            }

            return null;
        }

        private M4bSourceTrack Probe(string ffprobe, string path)
        {
            var result = _runner.Run(ffprobe, new[]
            {
                "-v", "error",
                "-print_format", "json",
                "-show_format",
                "-show_streams",
                "-show_chapters",
                path
            }, ProbeTimeout);

            if (!result.Succeeded)
            {
                _logger.Debug("TaleArr M4B: ffprobe failed for {0}: {1}", path, FirstLine(result.StandardError));
                return null;
            }

            try
            {
                return M4bProbeParser.Parse(path, result.StandardOutput);
            }
            catch (Exception ex)
            {
                _logger.Debug(ex, "TaleArr M4B: unreadable ffprobe output for {0}", path);
                return null;
            }
        }

        private M4bConversionOutcome Verify(string ffprobe, string path, double expected, int plannedChapters)
        {
            var probe = Probe(ffprobe, path);
            if (probe == null || string.IsNullOrWhiteSpace(probe.Codec))
            {
                return M4bConversionOutcome.Fail("The converted M4B has no readable audio. The original files were kept.");
            }

            if (!M4bFfmpegCommand.IsDurationWithinTolerance(expected, probe.DurationSeconds))
            {
                return M4bConversionOutcome.Fail(string.Format(CultureInfo.InvariantCulture,
                    "The converted M4B is {0:0} s long but the source files add up to {1:0} s (more than 1% apart). The original files were kept.",
                    probe.DurationSeconds,
                    expected));
            }

            if (plannedChapters > 0 && probe.Chapters.Count == 0)
            {
                return M4bConversionOutcome.Fail("The converted M4B has no chapters. The original files were kept.");
            }

            return new M4bConversionOutcome
            {
                Success = true,
                ExpectedDurationSeconds = expected,
                DurationSeconds = probe.DurationSeconds,
                ChapterCount = probe.Chapters.Count
            };
        }

        private string ResolveCover(string ffmpeg, M4bConversionJob job, List<M4bSourceTrack> tracks, string workFolder)
        {
            if (!string.IsNullOrWhiteSpace(job.CoverPath) && File.Exists(job.CoverPath))
            {
                var extension = Path.GetExtension(job.CoverPath).ToLowerInvariant();
                if (extension == ".jpg" || extension == ".jpeg" || extension == ".png")
                {
                    return job.CoverPath;
                }
            }

            var withCover = tracks.FirstOrDefault(t => t.HasEmbeddedCover);
            if (withCover == null)
            {
                return null;
            }

            var coverPath = Path.Combine(workFolder, "cover.jpg");
            var result = _runner.Run(ffmpeg, new[]
            {
                "-hide_banner", "-nostdin", "-loglevel", "error", "-y",
                "-i", withCover.Path,
                "-map", "0:v:0",
                "-frames:v", "1",
                "-an",
                coverPath
            }, ProbeTimeout);

            return result.Succeeded && File.Exists(coverPath) && new FileInfo(coverPath).Length > 0 ? coverPath : null;
        }

        private static string FirstLine(string text)
        {
            return text?
                .Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(l => l.Trim())
                .FirstOrDefault(l => l.Length > 0) ?? "no output";
        }

        private void DeleteQuietly(string path)
        {
            try
            {
                if (!string.IsNullOrWhiteSpace(path) && File.Exists(path))
                {
                    File.Delete(path);
                }
            }
            catch (Exception ex)
            {
                _logger.Debug(ex, "Could not delete {0}", path);
            }
        }
    }
}
