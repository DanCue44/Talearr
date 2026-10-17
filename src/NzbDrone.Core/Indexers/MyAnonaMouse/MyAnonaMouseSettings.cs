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

            RuleFor(c => c.UnsatisfiedSlotReserve).GreaterThanOrEqualTo(0);
            RuleFor(c => c.ManualGrabBuffer).GreaterThanOrEqualTo(0);
            RuleFor(c => c.SeedCriteria).SetValidator(_ => new SeedCriteriaSettingsValidator(seedTimeMinimum: 72 * 60));

            // TaleArr: dynamic seedbox updates. MAM only accepts an IP change once an hour, so
            // checking more often than every 5 minutes only adds load.
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
            RuleFor(c => c.BonusDailyPurchaseCap).InclusiveBetween(1, 10);
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

        // TaleArr: dynamic seedbox interval bounds, in minutes.
        public const int DefaultDynamicSeedboxInterval = 30;
        public const int MinimumDynamicSeedboxInterval = 5;
        public const int MaximumDynamicSeedboxInterval = 24 * 60;

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

        [FieldDefinition(4, Type = FieldType.Textbox, Label = "Minimum Seeders", HelpText = "Minimum number of seeders required.")]
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

        [FieldDefinition(12, Type = FieldType.Number, Label = "Safety Reserve", Unit = "slots", HelpText = "Slots Chaptarr will always keep free to protect against MAM's reporting delay and downloads started outside Chaptarr. Neither automatic nor Interactive Search grabs can use this reserve.")]
        public int UnsatisfiedSlotReserve { get; set; }

        [FieldDefinition(13, Type = FieldType.Number, Label = "User Buffer", Unit = "slots", HelpText = "Additional slots automatic grabs will keep free for releases you choose in Interactive Search or download outside Chaptarr. Interactive Search can use this buffer, but never the Safety Reserve.")]
        public int ManualGrabBuffer { get; set; }

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

        [FieldDefinition(18, Type = FieldType.Number, Label = "Dynamic Seedbox Interval", Unit = "minutes", HelpText = "How often to check the IP with MAM (5 to 1440). MAM accepts at most one IP change per hour; unchanged IPs cost nothing.", Advanced = true)]
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

        [FieldDefinition(29, Type = FieldType.Number, Label = "Bonus Purchases per Day", HelpText = "Most bonus purchases TaleArr makes for this account in any 24 hours (1 to 10).", Advanced = true)]
        public int BonusDailyPurchaseCap { get; set; }

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

        public NzbDroneValidationResult Validate()
        {
            return new NzbDroneValidationResult(Validator.Validate(this));
        }
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
