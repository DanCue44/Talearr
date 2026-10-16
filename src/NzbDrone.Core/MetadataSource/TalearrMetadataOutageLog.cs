using System;
using System.Collections.Concurrent;
using NLog;

namespace NzbDrone.Core.MetadataSource
{
    // TaleArr: one log line per metadata server outage instead of one (with a stack trace)
    // per author. While the metadata server circuit is open, a library refresh would
    // otherwise write an error for every author it skips. The first skipped item of an
    // outage is logged as a warning that says existing data is kept; the rest go to debug.
    // MetadataServerHealthService.ReportSuccess calls Reset when the server recovers, so
    // the next outage gets its own warning.
    public static class TalearrMetadataOutageLog
    {
        private sealed class OutageState
        {
            public bool NoticeLogged;
            public int Suppressed;
        }

        private static readonly ConcurrentDictionary<string, OutageState> States =
            new ConcurrentDictionary<string, OutageState>(StringComparer.OrdinalIgnoreCase);

        // Returns false when the metadata server is not in an outage (the caller should log
        // as usual). Returns true when the failure belongs to an outage and was logged here.
        public static bool TryLog(IMetadataServerHealthGate gate, Logger logger, string skipped, string detail)
        {
            if (gate == null || logger == null)
            {
                return false;
            }

            if (gate.CanAttemptWithoutProbe(out var retryAfter))
            {
                return false;
            }

            var sourceName = gate.SourceName ?? "metadata server";
            var state = States.GetOrAdd(sourceName, _ => new OutageState());
            bool first;

            lock (state)
            {
                first = !state.NoticeLogged;
                state.NoticeLogged = true;

                if (!first)
                {
                    state.Suppressed++;
                }
            }

            if (first)
            {
                logger.Warn("Metadata server {0} is unavailable ({1}). Keeping existing authors and books unchanged and skipping {2}; retrying in {3}. Further failures are logged at debug level until it recovers.",
                            sourceName,
                            string.IsNullOrWhiteSpace(detail) ? "no response" : detail,
                            skipped,
                            MetadataServerHealthGate.FormatRetryAfter(retryAfter));
            }
            else
            {
                logger.Debug("Metadata server {0} still unavailable; skipped {1} ({2})", sourceName, skipped, detail);
            }

            return true;
        }

        public static int GetSuppressedCount(string sourceName)
        {
            return sourceName != null && States.TryGetValue(sourceName, out var state) ? state.Suppressed : 0;
        }

        // Called when the server is available again. Returns how many log lines were folded
        // into the outage notice.
        public static int Reset(string sourceName)
        {
            if (sourceName == null || !States.TryRemove(sourceName, out var state))
            {
                return 0;
            }

            return state.NoticeLogged ? state.Suppressed : 0;
        }
    }
}
