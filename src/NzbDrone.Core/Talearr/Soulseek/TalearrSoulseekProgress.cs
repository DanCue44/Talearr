using System;
using System.Collections.Generic;
using System.Linq;

namespace NzbDrone.Core.Talearr.Soulseek
{
    public enum TalearrSoulseekProgressStatus
    {
        Missing,
        Queued,
        Downloading,
        Completed,
        Failed
    }

    public class TalearrSoulseekProgress
    {
        private static readonly string[] FailureFlags = { "Errored", "Rejected", "TimedOut", "Cancelled", "Aborted" };

        public TalearrSoulseekProgressStatus Status { get; set; }
        public string Message { get; set; }

        // TaleArr: reads slskd's per-file transfer states ("Queued, Remotely", "InProgress",
        // "Completed, Succeeded", "Completed, Errored", ...) into one state for the grab.
        public static TalearrSoulseekProgress Evaluate(IReadOnlyCollection<SlskdTransfer> transfers, int expectedFiles)
        {
            transfers ??= Array.Empty<SlskdTransfer>();

            if (transfers.Count == 0)
            {
                return new TalearrSoulseekProgress { Status = TalearrSoulseekProgressStatus.Missing, Message = "Waiting for slskd" };
            }

            var failed = transfers.FirstOrDefault(t => HasAny(t.State, FailureFlags));
            if (failed != null)
            {
                var reason = string.IsNullOrWhiteSpace(failed.Exception) ? failed.State : failed.Exception;
                return new TalearrSoulseekProgress
                {
                    Status = TalearrSoulseekProgressStatus.Failed,
                    Message = $"{TalearrSoulseekCandidates.FileName(failed.Filename)}: {reason}"
                };
            }

            var succeeded = transfers.Count(t => HasAny(t.State, "Succeeded"));
            if (succeeded == transfers.Count && transfers.Count >= expectedFiles)
            {
                return new TalearrSoulseekProgress { Status = TalearrSoulseekProgressStatus.Completed, Message = "Downloaded" };
            }

            var started = transfers.Any(t => HasAny(t.State, "InProgress", "Initializing") || HasAny(t.State, "Succeeded"));
            var total = transfers.Sum(t => Math.Max(0, t.Size));
            var done = transfers.Sum(t => Math.Max(0, t.BytesTransferred));
            var percent = total > 0 ? (int)Math.Floor(done * 100.0 / total) : 0;

            if (started)
            {
                return new TalearrSoulseekProgress
                {
                    Status = TalearrSoulseekProgressStatus.Downloading,
                    Message = $"{succeeded} of {Math.Max(expectedFiles, transfers.Count)} files, {percent}%"
                };
            }

            var place = transfers.Where(t => t.PlaceInQueue > 0).Select(t => t.PlaceInQueue.Value).DefaultIfEmpty(0).Min();
            return new TalearrSoulseekProgress
            {
                Status = TalearrSoulseekProgressStatus.Queued,
                Message = place > 0 ? $"Queued by the peer (place {place})" : "Queued"
            };
        }

        private static bool HasAny(string state, params string[] flags)
        {
            if (string.IsNullOrWhiteSpace(state))
            {
                return false;
            }

            var parts = state.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            return parts.Any(p => flags.Any(f => string.Equals(p, f, StringComparison.OrdinalIgnoreCase)));
        }
    }
}
