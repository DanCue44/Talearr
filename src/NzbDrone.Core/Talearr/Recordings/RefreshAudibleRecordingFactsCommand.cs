using System.Collections.Generic;
using NzbDrone.Core.Messaging.Commands;

namespace NzbDrone.Core.Talearr.Recordings
{
    // Talearr: refreshes Audible facts for audiobook editions. Scheduled every 12 hours (cheap and
    // incremental: only missing or stale cache rows, a capped number per run), and queued on
    // demand for one book group with Force set. The class name is what System > Tasks shows
    // ("Refresh Audible Recording Facts"), so it carries no brand word.
    public class RefreshAudibleRecordingFactsCommand : Command
    {
        public RefreshAudibleRecordingFactsCommand()
        {
        }

        public RefreshAudibleRecordingFactsCommand(List<int> bookIds, bool force)
        {
            BookIds = bookIds ?? new List<int>();
            Force = force;
        }

        // Empty: the scheduled run over the library. Otherwise only these book rows.
        public List<int> BookIds { get; set; } = new List<int>();

        // Ignore cache ages.
        public bool Force { get; set; }

        public override bool SendUpdatesToClient => true;
        public override bool UpdateScheduledTask => BookIds == null || BookIds.Count == 0;
        public override string CompletionMessage => "Audible recording facts refreshed";
    }
}
