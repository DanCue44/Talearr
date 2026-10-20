using NzbDrone.Core.Messaging.Commands;

namespace NzbDrone.Core.Talearr.Audiobookshelf
{
    // Talearr: reads the Audiobookshelf libraries into talearr.db. Scheduled every hour, queued
    // when an Audiobookshelf connection is saved, and from the "Sync now" button. The class name
    // is what System > Tasks shows ("Sync Audiobookshelf Library").
    public class SyncAudiobookshelfLibraryCommand : Command
    {
        // Null: every connection with Sync Library on. Otherwise only that Notifications row.
        public int? ConnectionId { get; set; }

        public override bool SendUpdatesToClient => true;
        public override bool UpdateScheduledTask => !ConnectionId.HasValue;
        public override string CompletionMessage => "Audiobookshelf library synced";
    }
}
