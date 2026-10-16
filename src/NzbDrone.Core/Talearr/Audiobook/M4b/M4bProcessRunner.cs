using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text;
using System.Threading;

namespace NzbDrone.Core.Talearr.Audiobook.M4b
{
    public sealed class M4bProcessResult
    {
        public int ExitCode { get; set; }
        public bool TimedOut { get; set; }
        public string StandardOutput { get; set; }
        public string StandardError { get; set; }

        public bool Succeeded => !TimedOut && ExitCode == 0;
    }

    // TaleArr: runs ffmpeg/ffprobe with an argument list (no shell, no quoting), streams stdout
    // lines to a callback for progress and kills the process on timeout or cancellation.
    public interface IM4bProcessRunner
    {
        M4bProcessResult Run(string fileName, IReadOnlyList<string> arguments, TimeSpan timeout, Action<string> onOutputLine = null, CancellationToken cancellationToken = default);
    }

    public class M4bProcessRunner : IM4bProcessRunner
    {
        private const int MaxCapturedChars = 1024 * 1024;

        public M4bProcessResult Run(string fileName, IReadOnlyList<string> arguments, TimeSpan timeout, Action<string> onOutputLine = null, CancellationToken cancellationToken = default)
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = fileName,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                RedirectStandardInput = false,
                CreateNoWindow = true
            };

            foreach (var argument in arguments)
            {
                startInfo.ArgumentList.Add(argument);
            }

            var stdout = new StringBuilder();
            var stderr = new StringBuilder();

            using var process = new Process { StartInfo = startInfo };

            process.OutputDataReceived += (_, e) =>
            {
                if (e.Data == null)
                {
                    return;
                }

                lock (stdout)
                {
                    if (stdout.Length < MaxCapturedChars)
                    {
                        stdout.AppendLine(e.Data);
                    }
                }

                try
                {
                    onOutputLine?.Invoke(e.Data);
                }
                catch (Exception)
                {
                    // Progress reporting must never stop the conversion.
                }
            };

            process.ErrorDataReceived += (_, e) =>
            {
                if (e.Data == null)
                {
                    return;
                }

                lock (stderr)
                {
                    if (stderr.Length < MaxCapturedChars)
                    {
                        stderr.AppendLine(e.Data);
                    }
                }
            };

            process.Start();
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();

            var deadline = DateTime.UtcNow + timeout;
            var timedOut = false;

            while (!process.WaitForExit(500))
            {
                if (cancellationToken.IsCancellationRequested || DateTime.UtcNow > deadline)
                {
                    timedOut = !cancellationToken.IsCancellationRequested;
                    try
                    {
                        process.Kill(true);
                    }
                    catch (Exception)
                    {
                        // Already exited.
                    }

                    process.WaitForExit(5000);
                    break;
                }
            }

            // Flush the async readers.
            process.WaitForExit();

            return new M4bProcessResult
            {
                ExitCode = timedOut || cancellationToken.IsCancellationRequested ? -1 : process.ExitCode,
                TimedOut = timedOut,
                StandardOutput = stdout.ToString(),
                StandardError = stderr.ToString()
            };
        }
    }
}
