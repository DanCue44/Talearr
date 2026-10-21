using NzbDrone.Core.Messaging.Commands;

namespace NzbDrone.Core.Talearr.Comics
{
    // Talearr: imports one finished comic download (queued by the download monitor).
    public class ImportComicDownloadCommand : Command
    {
        public string DownloadId { get; set; }

        public override bool SendUpdatesToClient => true;
        public override string CompletionMessage => "Comic imported";
    }

    // Talearr: refreshes every comic series (new volumes, dates, covers). Scheduled daily; stock
    // Chaptarr drops the scheduled row on its next start.
    public class RefreshComicSeriesCommand : Command
    {
        // Null: every series.
        public int? SeriesId { get; set; }

        public override bool SendUpdatesToClient => true;
        public override bool UpdateScheduledTask => !SeriesId.HasValue;
        public override string CompletionMessage => "Comic series refreshed";
    }

    // Talearr: searches for missing monitored comic volumes and grabs the best releases.
    public class SearchMissingComicsCommand : Command
    {
        // Null: every monitored series.
        public int? SeriesId { get; set; }

        public override bool SendUpdatesToClient => true;
        public override bool UpdateScheduledTask => !SeriesId.HasValue;
        public override string CompletionMessage => "Comic search finished";
    }
}
