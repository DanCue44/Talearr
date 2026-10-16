namespace NzbDrone.Core.Talearr.GrabPolicy
{
    // TaleArr: how abridged audiobook releases are treated when searching.
    public enum AbridgedAudiobookPolicy
    {
        // Abridged releases are rejected unless the wanted edition is itself abridged.
        UnabridgedOnly = 0,

        // Abridged releases are allowed but always rank below unabridged or unmarked ones.
        PreferUnabridged = 1,

        // No abridged handling at all (upstream Chaptarr behaviour).
        Any = 2
    }

    // TaleArr: grab policy settings. Stored as talearr.* rows in the existing Config table
    // (see TalearrGrabPolicyService), which Chaptarr never reads, so a rollback to Chaptarr
    // on the same database is unaffected.
    public sealed class TalearrGrabPolicy
    {
        public const int DefaultMaxAutomaticRetries = 3;
        public const int MaxAutomaticRetriesLimit = 10;

        public AbridgedAudiobookPolicy AbridgedPolicy { get; set; } = AbridgedAudiobookPolicy.UnabridgedOnly;

        // Off by default: series bundles, omnibuses and box sets are skipped.
        public bool AllowBundles { get; set; }

        // On by default: import rejections that will never succeed fail the download, which
        // blocklists the release and (with Automatic Redownload on) searches for the next one.
        public bool FailRejectedImports { get; set; } = true;

        // Failed downloads per book (within RetryWindowDays) before automatic searching stops.
        public int MaxAutomaticRetries { get; set; } = DefaultMaxAutomaticRetries;

        // On by default: when a release cannot be sent to the download client for a transient
        // reason (429, 5xx, timeout), search the book again after 2, 10 and 30 minutes (at most
        // MaxAutomaticRetries times). See TalearrGrabRetryService.
        public bool RetryFailedGrabs { get; set; } = true;

        public const int RetryWindowDays = 7;

        public static TalearrGrabPolicy Default => new TalearrGrabPolicy();

        public int EffectiveMaxAutomaticRetries
        {
            get
            {
                if (MaxAutomaticRetries <= 0)
                {
                    return DefaultMaxAutomaticRetries;
                }

                return MaxAutomaticRetries > MaxAutomaticRetriesLimit ? MaxAutomaticRetriesLimit : MaxAutomaticRetries;
            }
        }

        public TalearrGrabPolicy Clone()
        {
            return new TalearrGrabPolicy
            {
                AbridgedPolicy = AbridgedPolicy,
                AllowBundles = AllowBundles,
                FailRejectedImports = FailRejectedImports,
                MaxAutomaticRetries = MaxAutomaticRetries,
                RetryFailedGrabs = RetryFailedGrabs
            };
        }
    }
}
