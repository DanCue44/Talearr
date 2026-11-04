using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using NLog;
using NzbDrone.Core.Talearr.Audiobook.M4b;

namespace NzbDrone.Core.Talearr.Ebook.Epub
{
    public sealed class EpubConversionOutcome
    {
        public bool Success { get; set; }
        public string OutputPath { get; set; }
        public long OutputSize { get; set; }
        public string Error { get; set; }

        // The ebook is DRM protected; no tool may convert it.
        public bool Protected { get; set; }

        public static EpubConversionOutcome Fail(string error, bool isProtected = false)
        {
            return new EpubConversionOutcome { Success = false, Error = error, Protected = isProtected };
        }
    }

    public interface ITalearrEpubConverter
    {
        bool IsAvailable();
        EpubConversionOutcome Convert(string inputPath, string outputPath);
    }

    // Talearr: turns a Kindle ebook into an EPUB with KindleUnpack (GPL-3.0, shipped in the Docker
    // image under /opt/kindleunpack, run as the lib package it is written as). An AZW3, or a MOBI that carries the Kindle 8 (KF8) part, holds
    // the publisher's EPUB structure, which KindleUnpack rebuilds as it was. A MOBI with only the
    // old Mobipocket text is refused rather than turned into a poor EPUB, and a DRM protected file
    // is refused. The EPUB is built in a hidden folder next to the target (disk scans skip it),
    // checked, and only then moved into place. The source file is never touched here.
    public class TalearrEpubConverter : ITalearrEpubConverter
    {
        public const string WorkFolderName = ".talearr-epub";
        public const string DefaultToolFolder = "/opt/kindleunpack";
        public const string ToolFolderVariable = "TALEARR_KINDLEUNPACK";

        // KindleUnpack's modules import each other as a package (lib.*), so it runs from its folder.
        private const string Launcher = "import sys; sys.path.insert(0, sys.argv.pop(1)); from lib.kindleunpack import main; sys.exit(main(sys.argv))";

        public static readonly HashSet<string> ConvertibleExtensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            ".azw3", ".azw", ".mobi", ".prc"
        };

        private static readonly TimeSpan Timeout = TimeSpan.FromMinutes(10);

        private readonly IM4bProcessRunner _runner;
        private readonly Logger _logger;
        private bool? _pythonAvailable;

        public TalearrEpubConverter(IM4bProcessRunner runner, Logger logger)
        {
            _runner = runner;
            _logger = logger;
        }

        public static string ToolFolder
        {
            get
            {
                var configured = Environment.GetEnvironmentVariable(ToolFolderVariable);
                return string.IsNullOrWhiteSpace(configured) ? DefaultToolFolder : configured.Trim();
            }
        }

        public bool IsAvailable()
        {
            if (!File.Exists(Path.Combine(ToolFolder, "lib", "kindleunpack.py")))
            {
                return false;
            }

            if (!_pythonAvailable.HasValue)
            {
                try
                {
                    _pythonAvailable = _runner.Run("python3", new[] { "--version" }, TimeSpan.FromSeconds(20)).Succeeded;
                }
                catch (Exception ex)
                {
                    _logger.Debug(ex, "python3 was not found");
                    _pythonAvailable = false;
                }
            }

            return _pythonAvailable.Value;
        }

        public EpubConversionOutcome Convert(string inputPath, string outputPath)
        {
            if (string.IsNullOrWhiteSpace(inputPath) || !File.Exists(inputPath))
            {
                return EpubConversionOutcome.Fail("The ebook file was not found.");
            }

            var extension = Path.GetExtension(inputPath);
            if (!ConvertibleExtensions.Contains(extension))
            {
                return EpubConversionOutcome.Fail($"{extension} files cannot be converted to EPUB.");
            }

            var targetFolder = Path.GetDirectoryName(outputPath);
            if (string.IsNullOrWhiteSpace(targetFolder))
            {
                return EpubConversionOutcome.Fail("The EPUB has no folder to go in.");
            }

            var work = Path.Combine(targetFolder, WorkFolderName, Guid.NewGuid().ToString("N"));

            try
            {
                Directory.CreateDirectory(work);

                // KindleUnpack names its output after the input and needs a Kindle extension.
                var input = Path.Combine(work, "book" + extension.ToLowerInvariant());
                File.Copy(inputPath, input);
                var unpacked = Path.Combine(work, "out");

                var result = _runner.Run("python3", new[] { "-I", "-c", Launcher, ToolFolder, "--epub_version=A", input, unpacked }, Timeout);
                var output = (result.StandardOutput ?? string.Empty) + (result.StandardError ?? string.Empty);

                if (output.IndexOf("Book is encrypted", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    return EpubConversionOutcome.Fail("The ebook is DRM protected, so it cannot be converted.", true);
                }

                if (result.TimedOut)
                {
                    return EpubConversionOutcome.Fail("Converting to EPUB took longer than 10 minutes and was stopped.");
                }

                var epub = Directory.Exists(Path.Combine(unpacked, "mobi8"))
                    ? Directory.EnumerateFiles(Path.Combine(unpacked, "mobi8"), "*.epub").FirstOrDefault()
                    : null;

                if (epub == null)
                {
                    if (!result.Succeeded)
                    {
                        _logger.Debug("KindleUnpack failed for {0}: {1}", inputPath, LastLines(output));
                        return EpubConversionOutcome.Fail("KindleUnpack could not read the ebook: " + LastLines(output));
                    }

                    return EpubConversionOutcome.Fail("The ebook only has the old MOBI format (no Kindle 8 part), which is not converted to EPUB.");
                }

                var problem = CheckEpub(epub);
                if (problem != null)
                {
                    return EpubConversionOutcome.Fail("The EPUB KindleUnpack made is not usable: " + problem);
                }

                // Into place last: a partial file next to the target, then a rename.
                var partial = outputPath + ".partial";
                File.Copy(epub, partial, true);
                File.Move(partial, outputPath);

                return new EpubConversionOutcome
                {
                    Success = true,
                    OutputPath = outputPath,
                    OutputSize = new FileInfo(outputPath).Length
                };
            }
            catch (Exception ex)
            {
                _logger.Warn(ex, "Converting {0} to EPUB failed", inputPath);
                return EpubConversionOutcome.Fail(ex.Message);
            }
            finally
            {
                TryDeleteFolder(work);
                TryDeleteFolder(Path.Combine(targetFolder, WorkFolderName), onlyIfEmpty: true);
            }
        }

        // A readable EPUB: a zip whose "mimetype" says EPUB, with a container pointing at a
        // package and at least one text document.
        public static string CheckEpub(string path)
        {
            try
            {
                using var zip = ZipFile.OpenRead(path);
                var mimetype = zip.GetEntry("mimetype");
                if (mimetype == null)
                {
                    return "no mimetype entry";
                }

                using (var reader = new StreamReader(mimetype.Open(), Encoding.ASCII))
                {
                    if (reader.ReadToEnd().Trim() != "application/epub+zip")
                    {
                        return "the mimetype is not application/epub+zip";
                    }
                }

                if (zip.GetEntry("META-INF/container.xml") == null)
                {
                    return "no META-INF/container.xml";
                }

                if (!zip.Entries.Any(e => e.FullName.EndsWith(".opf", StringComparison.OrdinalIgnoreCase)))
                {
                    return "no package (.opf) file";
                }

                if (!zip.Entries.Any(e => e.Length > 0 &&
                                          (e.FullName.EndsWith(".xhtml", StringComparison.OrdinalIgnoreCase) ||
                                           e.FullName.EndsWith(".html", StringComparison.OrdinalIgnoreCase) ||
                                           e.FullName.EndsWith(".htm", StringComparison.OrdinalIgnoreCase))))
                {
                    return "no text documents";
                }

                return null;
            }
            catch (InvalidDataException)
            {
                return "it is not a zip file";
            }
        }

        private static string LastLines(string output)
        {
            var lines = (output ?? string.Empty)
                .Split('\n')
                .Select(l => l.Trim())
                .Where(l => l.Length > 0)
                .ToList();

            return lines.Count == 0 ? "no output" : string.Join(" ", lines.Skip(Math.Max(0, lines.Count - 2)));
        }

        private void TryDeleteFolder(string folder, bool onlyIfEmpty = false)
        {
            try
            {
                if (!Directory.Exists(folder))
                {
                    return;
                }

                if (onlyIfEmpty && Directory.EnumerateFileSystemEntries(folder).Any())
                {
                    return;
                }

                Directory.Delete(folder, !onlyIfEmpty);
            }
            catch (Exception ex)
            {
                _logger.Debug(ex, "Could not remove {0}", folder);
            }
        }
    }
}
