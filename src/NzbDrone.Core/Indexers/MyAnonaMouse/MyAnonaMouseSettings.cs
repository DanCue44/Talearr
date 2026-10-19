using System;
using FluentValidation;
using NzbDrone.Core.Annotations;
using NzbDrone.Core.Validation;

namespace NzbDrone.Core.Indexers.MyAnonaMouse
{
    public class MyAnonaMouseSettingsValidator : AbstractValidator<MyAnonaMouseSettings>
    {
        public MyAnonaMouseSettingsValidator()
        {
            RuleFor(c => c.BaseUrl).ValidRootUrl();
            RuleFor(c => c.MamId).NotEmpty();

            // TaleArr: bounds only keep the values working (no zero or negative intervals, no
            // overflow from a typo). How often and how hard TaleArr uses MAM is the user's choice;
            // the help texts suggest gentle values.
            RuleFor(c => c.UnsatisfiedSlotReserve).InclusiveBetween(0, MyAnonaMouseSettings.MaximumSlots);
            RuleFor(c => c.ManualGrabBuffer).InclusiveBetween(0, MyAnonaMouseSettings.MaximumSlots);
            RuleFor(c => c.AccountStatusRefreshInterval)
                .InclusiveBetween(MyAnonaMouseSettings.MinimumAccountStatusRefreshInterval, MyAnonaMouseSettings.MaximumAccountStatusRefreshInterval);
            RuleFor(c => c.SeedCriteria).SetValidator(_ => new SeedCriteriaSettingsValidator(seedTimeMinimum: 72 * 60));

            // TaleArr: dynamic seedbox updates. MAM only accepts an IP change once an hour; a
            // shorter interval is allowed (the user decides) and only adds checks.
            RuleFor(c => c.DynamicSeedboxInterval)
                .GreaterThanOrEqualTo(MyAnonaMouseSettings.MinimumDynamicSeedboxInterval)
                .LessThanOrEqualTo(MyAnonaMouseSettings.MaximumDynamicSeedboxInterval)
                .When(c => c.DynamicSeedboxEnabled);

            // TaleArr: bonus points auto-spend (MyAnonaMouseBonusSpend). VIP renewal must leave room
            // for MAM's minimum 7-day purchase under the 90-day cap; upload credit starts at 50 GiB.
            RuleFor(c => c.BonusReservePoints).GreaterThanOrEqualTo(0);
            RuleFor(c => c.BonusVipRenewDays).InclusiveBetween(1, MyAnonaMouseBonusSpendRules.VipMaxDays - MyAnonaMouseBonusSpendRules.VipMinimumPurchaseDays);
            RuleFor(c => c.BonusMinimumWedges).InclusiveBetween(1, 100);
            RuleFor(c => c.BonusMinimumRatio).GreaterThan(0).LessThanOrEqualTo(100);
            RuleFor(c => c.BonusUploadGiB).InclusiveBetween(MyAnonaMouseBonusSpendRules.MinimumUploadGiB, 10000);
            RuleFor(c => c.BonusDailyPurchaseCap).InclusiveBetween(1, MyAnonaMouseSettings.MaximumBonusDailyPurchaseCap);
            RuleFor(c => c.BonusRetryAfterFailureHours).InclusiveBetween(1, MyAnonaMouseSettings.MaximumPauseHours);

            // TaleArr: ratio guard and freeleech seeding filler.
            RuleFor(c => c.MinimumRatioAfterGrab).GreaterThanOrEqualTo(0);
            RuleFor(c => c.FillerSlotMargin).InclusiveBetween(0, MyAnonaMouseSettings.MaximumSlots);
            RuleFor(c => c.FillerMaxGrabsPerRun).InclusiveBetween(1, MyAnonaMouseSettings.MaximumFillerGrabsPerRun);
            RuleFor(c => c.FillerIntervalHours).InclusiveBetween(1, MyAnonaMouseSettings.MaximumFillerIntervalHours);
            RuleFor(c => c.FillerMaxSizeMb).InclusiveBetween(1, MyAnonaMouseSettings.MaximumFillerSizeMb);
            RuleFor(c => c.FillerMinLeechers).InclusiveBetween(0, MyAnonaMouseSettings.MaximumPeers);
            RuleFor(c => c.FillerMaxSeeders).InclusiveBetween(0, MyAnonaMouseSettings.MaximumPeers);
            RuleFor(c => c.FillerSearchPages).InclusiveBetween(1, MyAnonaMouseSettings.MaximumFillerSearchPages);
            RuleFor(c => c.FillerPreferredSearchPages).InclusiveBetween(0, MyAnonaMouseSettings.MaximumFillerSearchPages);
            RuleFor(c => c.FillerErrorPauseHours).InclusiveBetween(1, MyAnonaMouseSettings.MaximumPauseHours);
            RuleFor(c => c.FillerSeedDays).InclusiveBetween(3, 365);
            RuleFor(c => c.FillerCategory)
                .NotEmpty()
                .Matches(@"^([^\\\/](\/?[^\\\/])*)?$")
                .When(c => c.FillerEnabled && !c.FillerImport);
        }
    }

    public class MyAnonaMouseSettings : ITorrentIndexerSettings
    {
        private static readonly MyAnonaMouseSettingsValidator Validator = new MyAnonaMouseSettingsValidator();
        private const int LegacyRequiredWedgeAction = 2;
        private int _useFreeleechWedge;

        public MyAnonaMouseSettings()
        {
            BaseUrl = "https://www.myanonamouse.net";
            MinimumSeeders = IndexerDefaults.MINIMUM_SEEDERS;
            EnableDeepSearch = true; // Default to enabled for better metadata
            IntegrateWithAbs = false; // Default to disabled (hidden in UI)
            UseFreeleechOnlyForAudiobooks = true; // Default to audiobooks-only
            MamSsl = true; // Default to true since most sessions use mam_ssl
            ProtectUnsatisfiedSlots = true;
            UnsatisfiedSlotReserve = 5;
            ManualGrabBuffer = 0;

            // TaleArr: freeleech ranking is on by default, the stricter filter and the dynamic
            // seedbox update are opt-in.
            PreferFreeleech = true;
            FreeleechOnly = false;
            DynamicSeedboxEnabled = false;
            DynamicSeedboxInterval = DefaultDynamicSeedboxInterval;
            SeedboxProxyId = 0;

            // TaleArr: bonus points auto-spend. Every rule is off by default.
            BonusReservePoints = 10000;
            BonusKeepVip = false;
            BonusVipRenewDays = 14;
            BonusKeepWedges = false;
            BonusMinimumWedges = 2;
            BonusRatioSafetyNet = false;
            BonusMinimumRatio = 1.2;
            BonusUploadGiB = MyAnonaMouseBonusSpendRules.MinimumUploadGiB;
            BonusDailyPurchaseCap = 2;
            BonusDryRun = false;
        }

        // TaleArr: dynamic seedbox interval bounds, in minutes. The floor is the scheduler's
        // one-minute resolution, not a politeness limit.
        public const int DefaultDynamicSeedboxInterval = 30;
        public const int MinimumDynamicSeedboxInterval = 1;
        public const int MaximumDynamicSeedboxInterval = 24 * 60;

        // TaleArr: MAM account status refresh (jsonLoad.php), in minutes. 60 was the fixed value
        // before it became a setting.
        public const int DefaultAccountStatusRefreshInterval = 60;
        public const int MinimumAccountStatusRefreshInterval = 1;
        public const int MaximumAccountStatusRefreshInterval = 24 * 60;

        // TaleArr: technical ceilings for the tunables, generous enough that they never limit a
        // real choice and small enough that a typo cannot overflow anything.
        public const int MaximumSlots = 10000;
        public const int MaximumPeers = 1000000;
        public const int MaximumFillerGrabsPerRun = 1000;
        public const int MaximumFillerIntervalHours = 30 * 24;
        public const int MaximumFillerSizeMb = 10 * 1024 * 1024;
        public const int MaximumFillerSearchPages = 50;
        public const int MaximumBonusDailyPurchaseCap = 100;
        public const int MaximumPauseHours = 30 * 24;

        [FieldDefinition(0, Label = "Website URL", Hidden = HiddenType.Hidden)]
        public string BaseUrl { get; set; }

        private string _mamId;

        [FieldDefinition(1, Label = "mam_id", Type = FieldType.Password, Privacy = PrivacyLevel.Password, HelpText = "The mam_id is found under Preferences > Security")]
        public string MamId
        {
            get => _mamId;
            set => _mamId = string.IsNullOrEmpty(value) ? value : value.Trim();
        }

        [FieldDefinition(1, Label = "mam_ssl", Type = FieldType.Checkbox, HelpText = "Set if your MAM session uses the mam_ssl cookie (most accounts do). This adds mam_ssl=1 to requests.", Advanced = true)]
        public bool MamSsl { get; set; }

        [FieldDefinition(2, Type = FieldType.Select, Label = "Freeleech Wedges", SelectOptions = typeof(MyAnonaMouseFreeleechWedgeAction), HelpText = "Follow your MAM site preference, or ask MAM to apply a personal freeleech wedge when grabbing an eligible torrent. Prefer Wedge falls back to a normal download if MAM cannot apply one.")]
        public int UseFreeleechWedge
        {
            get => _useFreeleechWedge;
            set => _useFreeleechWedge = value == LegacyRequiredWedgeAction
                ? (int)MyAnonaMouseFreeleechWedgeAction.Never
                : value;
        }

        [FieldDefinition(3, Type = FieldType.Checkbox, Label = "Only Use Wedges for Audiobooks", HelpText = "Do not request a wedge for ebook torrents. Chaptarr also avoids requesting wedges for torrents already marked free, personal freeleech, or VIP.")]
        public bool UseFreeleechOnlyForAudiobooks { get; set; }

        [FieldDefinition(4, Type = FieldType.Textbox, Label = "Minimum Seeders", HelpText = "Minimum number of seeders required. Suggested: 1. 0 is allowed, but a torrent nobody seeds can sit in an unsatisfied slot without finishing.")]
        public int MinimumSeeders { get; set; }

        [FieldDefinition(5, Type = FieldType.Checkbox, Label = "Enable Deep Search", HelpText = "Use JSON API with enhanced metadata including title, description, narrator, and duration information when available", Hidden = HiddenType.Hidden)]
        public bool EnableDeepSearch { get; set; }

        [FieldDefinition(6, Type = FieldType.Checkbox, Label = "Integrate with Audiobookshelf", HelpText = "If you've connected ABS this will enhance your MAM searching", Hidden = HiddenType.Hidden)]
        public bool IntegrateWithAbs { get; set; }

        [FieldDefinition(7, Type = FieldType.Number, Label = "Seed Time", Unit = "hours", HelpText = "Number of hours to seed after download completes. MAM requires 72 hours within 30 days; values below 72 hours will warn.")]
        public int? SeedTimeHours { get; set; }

        [FieldDefinition(8, Type = FieldType.Number, Label = "Early Download Limit", Unit = "days", HelpText = "Time before release date Chaptarr will download from this indexer, empty is no limit", Advanced = true)]
        public int? EarlyReleaseLimit { get; set; }

        // VIP Status - populated during connection test, hidden from editing (display-only elsewhere)
        [FieldDefinition(9, Type = FieldType.Textbox, Label = "MAM User Class", HelpText = "Detected on connection test", Hidden = HiddenType.Hidden)]
        public string UserClass { get; set; }

        [FieldDefinition(10, Type = FieldType.Checkbox, Label = "VIP Member", HelpText = "Detected on connection test", Hidden = HiddenType.Hidden)]
        public bool IsVip { get; set; }

        [FieldDefinition(11, Type = FieldType.Checkbox, Label = "Protect Unsatisfied Slots", HelpText = "Pause MAM grabs before your unsatisfied-torrent limit is reached. Disabling this can trigger MAM's 24-hour download suspension.", Advanced = true)]
        public bool ProtectUnsatisfiedSlots { get; set; }

        [FieldDefinition(12, Type = FieldType.Number, Label = "Safety Reserve", Unit = "slots", HelpText = "Slots Chaptarr will always keep free to protect against MAM's reporting delay and downloads started outside Chaptarr. Neither automatic nor Interactive Search grabs can use this reserve. Suggested: 5. Fewer is allowed (even 0), but it uses more unsatisfied slots and makes MAM's 24-hour download suspension more likely.")]
        public int UnsatisfiedSlotReserve { get; set; }

        [FieldDefinition(13, Type = FieldType.Number, Label = "User Buffer", Unit = "slots", HelpText = "Additional slots automatic grabs will keep free for releases you choose in Interactive Search or download outside Chaptarr. Interactive Search can use this buffer, but never the Safety Reserve. Suggested: 0 (the default). Any value is allowed; a larger buffer leaves more slots for your own picks.")]
        public int ManualGrabBuffer { get; set; }

        // TaleArr: how often the MAM account status (unsatisfied count, class, ratio, VIP,
        // bonus points) is refreshed. Was a fixed hour.
        [FieldDefinition(13, Type = FieldType.Number, Label = "Account Status Refresh", Unit = "minutes", HelpText = "How often TaleArr reads your MAM account status (unsatisfied slots, class, ratio, bonus points) from jsonLoad.php (1 to 1440). Suggested: 60. Lower is allowed but sends MAM more requests. Higher means the slot and ratio guards work from older numbers.", Advanced = true)]
        public int AccountStatusRefreshInterval { get; set; } = DefaultAccountStatusRefreshInterval;

        [FieldDefinition(14, Type = FieldType.Checkbox, Label = "IndexerSettingsNeverMoveOnImport", HelpText = "IndexerSettingsNeverMoveOnImportHelpText")]
        public bool NeverMoveOnImport { get; set; }

        // TaleArr: freeleech awareness. Freeleech covers MAM global freeleech, personal
        // freeleech (an applied wedge) and VIP freeleech for VIP members.
        [FieldDefinition(15, Type = FieldType.Checkbox, Label = "Prefer Freeleech", HelpText = "Rank freeleech, personal freeleech and VIP freeleech MAM releases above other releases of the same quality. Also applies to MAM results that arrive through Prowlarr.")]
        public bool PreferFreeleech { get; set; }

        [FieldDefinition(16, Type = FieldType.Checkbox, Label = "Freeleech Only", HelpText = "Reject MAM releases that are not freeleech, personal freeleech or VIP freeleech for you. A wedge requested at grab time does not count, because MAM may not apply it.", Advanced = true)]
        public bool FreeleechOnly { get; set; }

        // TaleArr: dynamic seedbox. MAM must see the torrent client's public IP; with a VPN that
        // IP changes, and dynamicSeedbox.php tells MAM about the new one. The call has to leave
        // from the same IP as the torrent client.
        [FieldDefinition(17, Type = FieldType.Checkbox, Label = "Update Dynamic Seedbox IP", HelpText = "Tell MAM your torrent client's current IP on startup and on a schedule. The request must leave from the same IP as your torrent client: route it through the client's VPN with Seedbox Proxy (or this indexer's proxy). The session needs 'Allow session to set dynamic seedbox' on MAM.")]
        public bool DynamicSeedboxEnabled { get; set; }

        [FieldDefinition(18, Type = FieldType.Number, Label = "Dynamic Seedbox Interval", Unit = "minutes", HelpText = "How often to check the IP with MAM (1 to 1440). Suggested: 60, because MAM accepts at most one IP change per hour. Lower is allowed but sends MAM more requests; unchanged IPs cost nothing.", Advanced = true)]
        public int DynamicSeedboxInterval { get; set; }

        private string _seedboxMamId;

        [FieldDefinition(19, Label = "Seedbox mam_id", Type = FieldType.Password, Privacy = PrivacyLevel.Password, HelpText = "Optional separate MAM session for the dynamic seedbox update (for example one created from the VPN IP). Leave empty to use mam_id.", Advanced = true)]
        public string SeedboxMamId
        {
            get => _seedboxMamId;
            set => _seedboxMamId = string.IsNullOrEmpty(value) ? value : value.Trim();
        }

        [FieldDefinition(20, Label = "Seedbox Proxy", Type = FieldType.Select, SelectOptionsProviderAction = "getSeedboxProxies", HelpText = "Proxy that exits through your torrent client's VPN (for example the Privoxy built into a qBittorrent VPN container). Same As Indexer uses this indexer's proxy setting.", Advanced = true)]
        public int SeedboxProxyId { get; set; }

        // TaleArr: bonus points auto-spend through MAM's Bonus Points Store API (bonusBuy.php, on
        // MAM's list of allowed automation). Only VIP, upload credit and wedges are ever bought; at
        // most one purchase per run and BonusDailyPurchaseCap per 24 hours. See README.
        [FieldDefinition(21, Type = FieldType.Number, Label = "Bonus Points Reserve", Unit = "points", HelpText = "Bonus points auto-spend never leaves fewer than this many points. Applies to every rule below.", Advanced = true)]
        public int BonusReservePoints { get; set; }

        [FieldDefinition(22, Type = FieldType.Checkbox, Label = "Auto-Keep VIP", HelpText = "Buy VIP (MAM's 'max', filling to 90 days) when it has fewer days left than VIP Renew Below, or when a Power User has no VIP. About 5,000 points per 4 weeks.", Advanced = true)]
        public bool BonusKeepVip { get; set; }

        [FieldDefinition(23, Type = FieldType.Number, Label = "VIP Renew Below", Unit = "days", HelpText = "Renew VIP once fewer days than this are left (1 to 83).", Advanced = true)]
        public int BonusVipRenewDays { get; set; }

        [FieldDefinition(24, Type = FieldType.Checkbox, Label = "Auto-Buy Wedges", HelpText = "Buy one freeleech wedge (50,000 points) per run while you have fewer than Minimum Wedges.", Advanced = true)]
        public bool BonusKeepWedges { get; set; }

        [FieldDefinition(25, Type = FieldType.Number, Label = "Minimum Wedges", HelpText = "Keep at least this many freeleech wedges.", Advanced = true)]
        public int BonusMinimumWedges { get; set; }

        [FieldDefinition(26, Type = FieldType.Checkbox, Label = "Ratio Safety Net", HelpText = "Buy upload credit when your ratio drops below Minimum Ratio, or when MAM has made the account Mouse or seed-only. 500 points per GiB.", Advanced = true)]
        public bool BonusRatioSafetyNet { get; set; }

        [FieldDefinition(27, Type = FieldType.Number, Label = "Minimum Ratio", HelpText = "Ratio below which the safety net buys upload credit.", Advanced = true)]
        public double BonusMinimumRatio { get; set; }

        [FieldDefinition(28, Type = FieldType.Number, Label = "Upload Credit to Buy", Unit = "GiB", HelpText = "Upload credit bought per safety net purchase (MAM minimum 50 GiB = 25,000 points).", Advanced = true)]
        public int BonusUploadGiB { get; set; }

        [FieldDefinition(29, Type = FieldType.Number, Label = "Bonus Purchases per Day", HelpText = "Most bonus purchases TaleArr makes for this account in any 24 hours (1 to 100; at most one per hourly run). Suggested: 2. More is allowed but spends points faster and sends MAM more purchase requests.", Advanced = true)]
        public int BonusDailyPurchaseCap { get; set; }

        // TaleArr: back-off after MAM refuses a purchase. Was a fixed 24 hours.
        [FieldDefinition(29, Type = FieldType.Number, Label = "Bonus Retry After Refusal", Unit = "hours", HelpText = "After MAM refuses a purchase, wait this long before trying that kind of purchase again (1 to 720). Suggested: 24. Lower is allowed but sends MAM more purchase requests that may be refused again.", Advanced = true)]
        public int BonusRetryAfterFailureHours { get; set; } = 24;

        [FieldDefinition(30, Type = FieldType.Checkbox, Label = "Bonus Spend Dry Run", HelpText = "Only log and list what auto-spend would buy; nothing is sent to MAM.", Advanced = true)]
        public bool BonusDryRun { get; set; }

        // TaleArr: VIP expiry, ratio and seed-only state from the same account refresh (vip_until,
        // ratio or uploaded_bytes/downloaded_bytes). Null when MAM did not send them.
        public DateTime? VipUntilUtc { get; set; }
        public double? Ratio { get; set; }
        public long? UploadedBytes { get; set; }
        public long? DownloadedBytes { get; set; }
        public bool? SeedOnly { get; set; }

        // Persisted provider snapshot used by the grab guard. These are not user-editable.
        public int? UnsatisfiedCount { get; set; }
        public int? UnsatisfiedLimit { get; set; }
        public DateTime? UnsatisfiedSnapshotUtc { get; set; }
        public DateTime? UnsatisfiedStatusRefreshedUtc { get; set; }

        // TaleArr: freeleech wedge balance and bonus points from the same account refresh, for the
        // test message and the wedge health notice. Null when MAM did not send them. The
        // refresh itself is read only; spending only happens through the opt-in auto-spend rules.
        public int? FreeleechWedges { get; set; }
        public double? BonusPoints { get; set; }

        // Required by ITorrentIndexerSettings but hidden from UI
        public SeedCriteriaSettings SeedCriteria
        {
            get
            {
                var criteria = new SeedCriteriaSettings();
                if (SeedTimeHours.HasValue && SeedTimeHours.Value > 0)
                {
                    criteria.SeedTime = SeedTimeHours.Value * 60;
                }

                criteria.NeverMoveOnImport = NeverMoveOnImport;

                return criteria;
            }

            set
            {
            }
        }

        // Required by ITorrentIndexerSettings but hidden from UI
        public bool RejectBlocklistedTorrentHashesWhileGrabbing { get; set; }

        // TaleArr: the account status refresh interval, clamped so a value saved outside the
        // validator never stops or floods the refresh.
        public TimeSpan ResolveAccountStatusRefreshInterval()
        {
            var minutes = AccountStatusRefreshInterval <= 0
                ? DefaultAccountStatusRefreshInterval
                : Math.Min(AccountStatusRefreshInterval, MaximumAccountStatusRefreshInterval);

            return TimeSpan.FromMinutes(minutes);
        }

        // TaleArr: status freshness windows were sized for the one-hour refresh. A longer refresh
        // stretches them by the same amount so the guards do not pause grabs between refreshes;
        // a shorter one keeps the original window.
        public TimeSpan ResolveStatusMaxAge(TimeSpan baseAge)
        {
            var extra = ResolveAccountStatusRefreshInterval() - TimeSpan.FromMinutes(DefaultAccountStatusRefreshInterval);
            return extra > TimeSpan.Zero ? baseAge + extra : baseAge;
        }

        public NzbDroneValidationResult Validate()
        {
            return new NzbDroneValidationResult(Validator.Validate(this));
        }

        // TaleArr: account join date for the Power User progress card (Activity > Seeding). Null
        // when MAM did not send one. Not user-editable.
        public DateTime? MemberSinceUtc { get; set; }

        // TaleArr: field orders start at 39, after the bonus auto-spend settings (21 to 30).
        [FieldDefinition(39, Type = FieldType.Number, Label = "Minimum Ratio After Grab", HelpText = "A non-freeleech MAM grab only goes ahead when your share ratio after it (uploaded / (downloaded + release size)) stays at or above this. Freeleech and wedged grabs are exempt. A seed-only (Mouse) account gets no MAM grabs at all.", Advanced = true)]
        public double MinimumRatioAfterGrab { get; set; } = MyAnonaMouseAccountStanding.DefaultMinimumRatioAfterGrab;

        // TaleArr: freeleech seeding filler (Talearr/MamFiller). Off by default.
        [FieldDefinition(40, Type = FieldType.Checkbox, Label = "Freeleech Seeding Filler", Section = "Freeleech Seeding Filler", HelpText = "Fill free unsatisfied-torrent slots with popular freeleech MAM torrents so more torrents reach the 72-hour seeding requirement and earn bonus points. Uses only MAM's JSON search and download.php, never wedges or bonus points. They go to the torrent client in their own category; only preferred picks are imported (Filler Import Preferred Picks).")]
        public bool FillerEnabled { get; set; }

        [FieldDefinition(41, Type = FieldType.Checkbox, Label = "Filler Dry Run", Section = "Freeleech Seeding Filler", HelpText = "Only log and show what the filler would grab (Activity > Seeding, Filler preview). Turn off to let it grab.")]
        public bool FillerDryRun { get; set; } = true;

        [FieldDefinition(42, Type = FieldType.Number, Label = "Filler Slot Margin", Unit = "slots", Section = "Freeleech Seeding Filler", HelpText = "The filler fills up to your unsatisfied limit minus this margin. Safety Reserve and User Buffer still apply. Suggested: 2. Lower is allowed but uses more slots.", Advanced = true)]
        public int FillerSlotMargin { get; set; } = 2;

        [FieldDefinition(43, Type = FieldType.Number, Label = "Filler Max Grabs Per Run", Section = "Freeleech Seeding Filler", HelpText = "Most torrents one filler run grabs (1 to 1000). Suggested: up to 8. More is allowed but sends MAM more downloads per run and fills slots faster.", Advanced = true)]
        public int FillerMaxGrabsPerRun { get; set; } = 5;

        [FieldDefinition(44, Type = FieldType.Number, Label = "Filler Run Interval", Unit = "hours", Section = "Freeleech Seeding Filler", HelpText = "How often the filler runs (1 to 720; System > Tasks, MAM Freeleech Filler, can also be run by hand). Suggested: 6. Lower is allowed but sends MAM more searches.", Advanced = true)]
        public int FillerIntervalHours { get; set; } = 6;

        // TaleArr: filler search depth and error pause. Were fixed at 3 pages, 2 pages and 12 hours.
        [FieldDefinition(44, Type = FieldType.Number, Label = "Filler Search Pages", Section = "Freeleech Seeding Filler", HelpText = "Pages of 100 freeleech results the filler reads per run (1 to 50). Suggested: 3. More is allowed but sends MAM more searches.", Advanced = true)]
        public int FillerSearchPages { get; set; } = 3;

        [FieldDefinition(44, Type = FieldType.Number, Label = "Filler Preferred Search Pages", Section = "Freeleech Seeding Filler", HelpText = "Pages of the extra search limited to Filler Preferred Categories (0 to 50, 0 turns it off). Suggested: 2. More is allowed but sends MAM more searches.", Advanced = true)]
        public int FillerPreferredSearchPages { get; set; } = 2;

        [FieldDefinition(44, Type = FieldType.Number, Label = "Filler Pause After Error", Unit = "hours", Section = "Freeleech Seeding Filler", HelpText = "How long the filler pauses after a MAM error (1 to 720). Suggested: 12. Shorter is allowed but retries MAM sooner while it may still be failing.", Advanced = true)]
        public int FillerErrorPauseHours { get; set; } = 12;

        [FieldDefinition(45, Type = FieldType.Checkbox, Label = "Filler Prefer Audiobooks", Section = "Freeleech Seeding Filler", Advanced = true)]
        public bool FillerPreferAudiobooks { get; set; } = true;

        [FieldDefinition(46, Type = FieldType.Number, Label = "Filler Max Size", Unit = "MB", Section = "Freeleech Seeding Filler", HelpText = "Largest torrent the filler grabs. Suggested: 2048. Larger is allowed but uses more disk and bandwidth, and big torrents take longer to finish.", Advanced = true)]
        public int FillerMaxSizeMb { get; set; } = 2048;

        [FieldDefinition(47, Type = FieldType.Number, Label = "Filler Min Leechers", Section = "Freeleech Seeding Filler", HelpText = "Only torrents with at least this many leechers (someone to upload to). Suggested: 1. 0 is allowed, but torrents nobody wants earn less and still use a slot.", Advanced = true)]
        public int FillerMinLeechers { get; set; } = 1;

        [FieldDefinition(48, Type = FieldType.Number, Label = "Filler Max Seeders", Section = "Freeleech Seeding Filler", HelpText = "Skip torrents that already have more seeders than this. Suggested: 30. More is allowed, but well seeded torrents earn less upload and still use a slot.", Advanced = true)]
        public int FillerMaxSeeders { get; set; } = 30;

        [FieldDefinition(49, Type = FieldType.Textbox, Label = "Filler Category", Section = "Freeleech Seeding Filler", HelpText = "Torrent client category for filler torrents. Keep it different from TaleArr's own categories so they are never imported.", Advanced = true)]
        public string FillerCategory { get; set; } = "mam-seed";

        [FieldDefinition(50, Type = FieldType.Number, Label = "Filler Seed Time", Unit = "days", Section = "Freeleech Seeding Filler", Advanced = true)]
        public int FillerSeedDays { get; set; } = 30;

        [FieldDefinition(51, Type = FieldType.Checkbox, Label = "Filler Remove After Seed Time", Section = "Freeleech Seeding Filler", HelpText = "Remove filler torrents and their files from the client once they have seeded for the seed time (never before 72 hours).", Advanced = true)]
        public bool FillerRemoveAfterSeed { get; set; } = true;

        [FieldDefinition(52, Type = FieldType.Checkbox, Label = "Filler Import Into Library", Section = "Freeleech Seeding Filler", HelpText = "Put filler torrents in the client's normal TaleArr category so completed download handling may import them. Off keeps them seed-only.", Advanced = true)]
        public bool FillerImport { get; set; }

        // TaleArr: filler taste preferences (Talearr/MamFiller/TalearrMamFillerTaste). Preferred
        // picks are grabbed first; empty lists keep the plain leecher/seeder ranking.
        [FieldDefinition(53, Type = FieldType.Textbox, Label = "Filler Preferred Categories", Section = "Freeleech Seeding Filler", HelpText = "Comma separated MAM category names to grab first, matched by whole words in any case: True Crime matches Audiobooks - True Crime and Ebooks - True Crime. Also runs one extra MAM search limited to these categories.", Advanced = true)]
        public string FillerPreferredCategories { get; set; } = Talearr.MamFiller.TalearrMamFillerTaste.DefaultPreferredCategories;

        [FieldDefinition(54, Type = FieldType.Checkbox, Label = "Filler Prefer Authors in My Library", Section = "Freeleech Seeding Filler", HelpText = "Torrents by an author already in your library count as preferred.", Advanced = true)]
        public bool FillerPreferLibraryAuthors { get; set; } = true;

        [FieldDefinition(55, Type = FieldType.Textbox, Label = "Filler Preferred Authors", Section = "Freeleech Seeding Filler", HelpText = "Comma separated extra author names to grab first. Spacing, dots and case do not matter.", Advanced = true)]
        public string FillerPreferredAuthors { get; set; } = string.Empty;

        [FieldDefinition(56, Type = FieldType.Textbox, Label = "Filler Excluded Categories", Section = "Freeleech Seeding Filler", HelpText = "Comma separated MAM category names the filler never grabs (whole words, any case). Pol/Soc/Relig is MAM's combined politics, society and religion category.", Advanced = true)]
        public string FillerExcludedCategories { get; set; } = Talearr.MamFiller.TalearrMamFillerTaste.DefaultExcludedCategories;

        [FieldDefinition(57, Type = FieldType.Checkbox, Label = "Filler Only Grab Preferred", Section = "Freeleech Seeding Filler", HelpText = "Leave slots empty rather than fill them with torrents that match no preferred category or author.", Advanced = true)]
        public bool FillerOnlyPreferred { get; set; }

        [FieldDefinition(58, Type = FieldType.Checkbox, Label = "Filler Import Preferred Picks", Section = "Freeleech Seeding Filler", HelpText = "Add preferred picks to the library (only that book monitored, no search) and copy or hardlink the finished download in, while the torrent keeps seeding in the filler category. Other picks stay seed-only.", Advanced = true)]
        public bool FillerImportPreferred { get; set; } = true;
    }

    public enum MyAnonaMouseFreeleechWedgeAction
    {
        [FieldOption(Label = "Follow MAM Preferences", Hint = "Do not force a wedge; let your MAM account preferences decide")]
        Never = 0,

        [FieldOption(Label = "Prefer Wedge", Hint = "Request a personal freeleech wedge when eligible, then fall back to a normal download if unavailable")]
        Preferred = 1,

        [Obsolete("MAM's zero-wedge response is not documented well enough to guarantee required-wedge behavior.")]
        Required = 2,
    }
}
