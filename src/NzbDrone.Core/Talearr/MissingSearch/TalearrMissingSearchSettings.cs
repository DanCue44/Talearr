using NzbDrone.Common.Messaging;
using NzbDrone.Core.Messaging.Commands;

namespace NzbDrone.Core.Talearr.MissingSearch
{
    // Talearr: how often the scheduled missing-books search runs.
    public enum MissingSearchSchedule
    {
        Off = 0,
        Weekly = 1,
        Daily = 2
    }

    // Talearr: scheduled missing-books search. Readarr and Chaptarr search a book when it is added
    // and afterwards only RSS catches new uploads, so a book requested before an indexer was added
    // is never found. Off by default. Stored as talearr.missingsearch.* rows in the Config table,
    // which Chaptarr ignores.
    public sealed class TalearrMissingSearchSettings
    {
        public const int DefaultBooksPerRun = 20;
        public const int MaxBooksPerRun = 200;
        public const int DefaultStaggerSeconds = 30;
        public const int MaxStaggerSeconds = 600;

        public MissingSearchSchedule Schedule { get; set; } = MissingSearchSchedule.Off;
        public int BooksPerRun { get; set; } = DefaultBooksPerRun;
        public int StaggerSeconds { get; set; } = DefaultStaggerSeconds;

        public static TalearrMissingSearchSettings Default => new TalearrMissingSearchSettings();

        public int EffectiveBooksPerRun => BooksPerRun < 1 ? DefaultBooksPerRun : BooksPerRun > MaxBooksPerRun ? MaxBooksPerRun : BooksPerRun;

        public int EffectiveStaggerSeconds => StaggerSeconds < 0 ? DefaultStaggerSeconds : StaggerSeconds > MaxStaggerSeconds ? MaxStaggerSeconds : StaggerSeconds;

        // Scheduled task interval in minutes; 0 disables the task.
        public int IntervalMinutes
        {
            get
            {
                switch (Schedule)
                {
                    case MissingSearchSchedule.Daily:
                        return 24 * 60;
                    case MissingSearchSchedule.Weekly:
                        return 7 * 24 * 60;
                    default:
                        return 0;
                }
            }
        }

        public TalearrMissingSearchSettings Clone()
        {
            return new TalearrMissingSearchSettings
            {
                Schedule = Schedule,
                BooksPerRun = BooksPerRun,
                StaggerSeconds = StaggerSeconds
            };
        }
    }

    // Shown in System > Tasks as "Scheduled Missing Book Search".
    public class ScheduledMissingBookSearchCommand : Command
    {
        public override bool SendUpdatesToClient => true;
        public override string CompletionMessage => "Scheduled missing book search completed";
    }

    public class TalearrMissingSearchSettingsSavedEvent : IEvent
    {
        public TalearrMissingSearchSettingsSavedEvent(TalearrMissingSearchSettings settings, bool wasOff)
        {
            Settings = settings;
            WasOff = wasOff;
        }

        public TalearrMissingSearchSettings Settings { get; }
        public bool WasOff { get; }
    }
}
