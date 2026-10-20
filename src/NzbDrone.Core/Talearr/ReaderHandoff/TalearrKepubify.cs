using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using NLog;
using NzbDrone.Core.Talearr.Audiobook.M4b;

namespace NzbDrone.Core.Talearr.ReaderHandoff
{
    // Talearr: optional EPUB to KEPUB conversion for the reader hand-off copy, using the
    // kepubify binary (https://pgaskin.net/kepubify/) when it is installed. Never bundled.
    public interface ITalearrKepubify
    {
        // Full path of kepubify, or null when it is not installed.
        string FindExecutable();

        // Converts sourceEpub into destinationFile (a .kepub.epub path). Returns null on success,
        // otherwise a short reason. The source file is only read.
        string Convert(string executable, string sourceEpub, string destinationFile);
    }

    public class TalearrKepubify : ITalearrKepubify
    {
        private static readonly TimeSpan ConvertTimeout = TimeSpan.FromMinutes(5);

        private static readonly string[] KnownFolders =
        {
            "/usr/local/bin",
            "/usr/bin",
            "/bin",
            "/app/bin",
            "/config/bin"
        };

        private readonly IM4bProcessRunner _processRunner;
        private readonly Logger _logger;

        public TalearrKepubify(IM4bProcessRunner processRunner, Logger logger)
        {
            _processRunner = processRunner;
            _logger = logger;
        }

        public string FindExecutable()
        {
            return Locate(Environment.GetEnvironmentVariable("PATH"),
                File.Exists,
                RuntimeInformation.IsOSPlatform(OSPlatform.Windows));
        }

        public static string Locate(string pathVariable, Func<string, bool> fileExists, bool windows)
        {
            var name = windows ? "kepubify.exe" : "kepubify";
            var folders = (pathVariable ?? string.Empty)
                .Split(windows ? ';' : ':', StringSplitOptions.RemoveEmptyEntries)
                .Select(f => f.Trim().Trim('"'))
                .Where(f => f.Length > 0);

            if (!windows)
            {
                folders = folders.Concat(KnownFolders);
            }

            foreach (var folder in folders.Distinct(StringComparer.Ordinal))
            {
                var candidate = System.IO.Path.Combine(folder, name);

                try
                {
                    if (fileExists(candidate))
                    {
                        return candidate;
                    }
                }
                catch (Exception)
                {
                    // An unreadable PATH entry is skipped.
                }
            }

            return null;
        }

        public string Convert(string executable, string sourceEpub, string destinationFile)
        {
            // kepubify writes <name>.kepub.epub into the -o folder. A private temp folder keeps
            // half-written output out of the watch folder; the caller moves the result in place.
            var workFolder = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "talearr-kepub-" + Guid.NewGuid().ToString("N"));

            try
            {
                Directory.CreateDirectory(workFolder);

                var result = _processRunner.Run(executable,
                    new List<string> { "-o", workFolder, sourceEpub },
                    ConvertTimeout);

                if (!result.Succeeded)
                {
                    var detail = (result.StandardError ?? string.Empty).Trim();
                    if (detail.Length == 0)
                    {
                        detail = (result.StandardOutput ?? string.Empty).Trim();
                    }

                    if (detail.Length > 300)
                    {
                        detail = detail.Substring(0, 300);
                    }

                    return result.TimedOut
                        ? "kepubify timed out"
                        : $"kepubify exited with code {result.ExitCode}{(detail.Length > 0 ? ": " + detail : string.Empty)}";
                }

                var output = Directory.EnumerateFiles(workFolder, "*.epub", SearchOption.AllDirectories)
                    .OrderByDescending(f => f.EndsWith(".kepub.epub", StringComparison.OrdinalIgnoreCase))
                    .FirstOrDefault();

                if (output == null)
                {
                    return "kepubify finished without writing a file";
                }

                File.Copy(output, destinationFile, true);
                return null;
            }
            catch (Exception ex)
            {
                _logger.Debug(ex, "Talearr: kepubify conversion of '{0}' failed", sourceEpub);
                return ex.Message;
            }
            finally
            {
                try
                {
                    if (Directory.Exists(workFolder))
                    {
                        Directory.Delete(workFolder, true);
                    }
                }
                catch (Exception ex)
                {
                    _logger.Debug(ex, "Talearr: could not remove the kepubify work folder '{0}'", workFolder);
                }
            }
        }
    }
}
