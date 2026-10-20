using System;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using Newtonsoft.Json.Linq;

namespace NzbDrone.Core.Indexers.MyAnonaMouse
{
    // Talearr: MAM account standing (seed-only state and share ratio) from the jsonLoad.php account
    // refresh. A new account starts with 0 upload, so a few normal (non-freeleech) grabs drop the
    // ratio to 0 and MAM demotes the account to Mouse and makes it seed-only. These rules keep
    // Talearr from causing that:
    //   1. a seed-only account (class Mouse, or a seed-only / download-disabled flag) gets no MAM
    //      grabs at all, freeleech included;
    //   2. a non-freeleech grab needs uploaded / (downloaded + release size) to stay at or above
    //      the indexer's Minimum Ratio After Grab (default 2.0, the Power User line);
    //   3. with no fresh status (older than 3 hours, plus however much the Account Status
    //      Refresh exceeds an hour, or never read), only freeleech grabs pass.
    // Every field is read defensively: anything MAM does not send stays unknown.
    public static class MyAnonaMouseAccountStanding
    {
        public static readonly TimeSpan MaximumStatusAge = TimeSpan.FromHours(3);
        public const double DefaultMinimumRatioAfterGrab = 2.0;
        public const string SeedOnlyMessage = "MAM account is seed-only: buy upload credit in the Bonus Points Store";

        private static readonly string[] SeedOnlyClasses = { "Mouse" };
        private static readonly string[] SeedOnlyKeys =
        {
            "seedonly", "seed_only", "seedOnly", "seed_only_mode", "download_disabled", "downloads_disabled",
            "downloadDisabled", "downloadsDisabled", "dl_disabled", "leech_disabled", "leechDisabled"
        };

        private static readonly string[] CanDownloadKeys = { "can_download", "canDownload", "can_leech", "canLeech", "download_allowed" };
        private static readonly Regex SizeRegex = new Regex(@"^\s*([0-9][0-9,]*(?:\.[0-9]+)?)\s*([KMGTP]?i?B|bytes?)?\s*$", RegexOptions.IgnoreCase | RegexOptions.Compiled);

        // Fills in, from a jsonLoad.php reply, the standing fields MyAnonaMouseAccountExtras left
        // unknown: upload/download totals sent only as text ("1.2 GiB"), seed-only or
        // download-disabled flags under other names, and the join date. Never throws.
        public static void Apply(MyAnonaMouseSettings settings, string json)
        {
            if (settings == null)
            {
                return;
            }

            JObject root;
            try
            {
                root = string.IsNullOrWhiteSpace(json) ? null : JObject.Parse(json);
            }
            catch (Exception)
            {
                root = null;
            }

            if (root == null)
            {
                return;
            }

            try
            {
                settings.UploadedBytes ??= ReadBytes(root, "uploaded_bytes", "uploaded");
                settings.DownloadedBytes ??= ReadBytes(root, "downloaded_bytes", "downloaded");

                if (!settings.Ratio.HasValue && settings.UploadedBytes.HasValue && settings.DownloadedBytes is > 0)
                {
                    settings.Ratio = (double)settings.UploadedBytes.Value / settings.DownloadedBytes.Value;
                }

                var seedOnly = ReadSeedOnly(root);
                if (seedOnly == true || !settings.SeedOnly.HasValue)
                {
                    settings.SeedOnly = seedOnly ?? settings.SeedOnly;
                }

                settings.MemberSinceUtc = ReadMemberSince(root);
            }
            catch (Exception)
            {
                // Optional data only.
            }
        }

        // The account's join date. jsonLoad's top-level "created" is the join date only when the
        // snatch summary (whose own "created" is the snapshot time) is a separate object.
        internal static DateTime? ReadMemberSince(JObject root)
        {
            foreach (var key in new[] { "join_date", "joined", "member_since", "joindate" })
            {
                var date = MyAnonaMouseAccountExtras.ReadDate(root[key]);
                if (date.HasValue)
                {
                    return date;
                }
            }

            return root["snatch_summary"] is JObject ? MyAnonaMouseAccountExtras.ReadDate(root["created"]) : null;
        }

        public static bool IsSeedOnlyClass(string userClass)
        {
            return !string.IsNullOrWhiteSpace(userClass) &&
                   SeedOnlyClasses.Any(c => string.Equals(c, userClass.Trim(), StringComparison.OrdinalIgnoreCase));
        }

        // Uses the last stored status even when it is old: a seed-only account stays blocked until
        // a refresh shows otherwise.
        public static bool IsSeedOnly(MyAnonaMouseSettings settings)
        {
            return settings != null && (settings.SeedOnly == true || IsSeedOnlyClass(settings.UserClass));
        }

        public static bool HasFreshStatus(MyAnonaMouseSettings settings, DateTime now)
        {
            var refreshed = settings?.UnsatisfiedStatusRefreshedUtc;
            return settings != null &&
                   !string.IsNullOrWhiteSpace(settings.UserClass) &&
                   refreshed.HasValue &&
                   refreshed.Value <= now.AddMinutes(5) &&
                   now - refreshed.Value <= settings.ResolveStatusMaxAge(MaximumStatusAge);
        }

        // Talearr: the freshness window in hours, for messages ("3" with the default refresh).
        public static string StaleHours(MyAnonaMouseSettings settings)
        {
            var age = settings?.ResolveStatusMaxAge(MaximumStatusAge) ?? MaximumStatusAge;
            return age.TotalHours.ToString("0.#", System.Globalization.CultureInfo.InvariantCulture);
        }

        public static double ResolveMinimumRatio(MyAnonaMouseSettings settings)
        {
            var value = settings?.MinimumRatioAfterGrab ?? DefaultMinimumRatioAfterGrab;
            return double.IsNaN(value) || value < 0 ? DefaultMinimumRatioAfterGrab : value;
        }

        // Null when the grab may go ahead, otherwise the rejection reason.
        public static string Evaluate(MyAnonaMouseSettings settings, bool freeleech, long size, DateTime now)
        {
            if (settings == null)
            {
                return null;
            }

            if (IsSeedOnly(settings))
            {
                return SeedOnlyMessage;
            }

            if (!HasFreshStatus(settings, now))
            {
                return freeleech
                    ? null
                    : $"MAM account status is unknown or older than {StaleHours(settings)} hours: only freeleech MAM releases are grabbed until it refreshes";
            }

            if (freeleech)
            {
                return null;
            }

            if (!settings.UploadedBytes.HasValue || !settings.DownloadedBytes.HasValue)
            {
                return "MAM did not report upload and download totals: only freeleech MAM releases are grabbed";
            }

            var minimum = ResolveMinimumRatio(settings);
            var downloadedAfter = (double)settings.DownloadedBytes.Value + Math.Max(0, size);
            var uploaded = (double)settings.UploadedBytes.Value;

            if (downloadedAfter <= 0 || uploaded >= downloadedAfter * minimum)
            {
                return null;
            }

            var ratioAfter = uploaded / downloadedAfter;
            return string.Format(
                CultureInfo.InvariantCulture,
                "MAM ratio guard: this non-freeleech grab would leave your share ratio at {0:0.00}, below the minimum of {1:0.##}. Use freeleech or a wedge, or upload more first",
                ratioAfter,
                minimum);
        }

        internal static long? ReadBytes(JObject root, string bytesKey, string textKey)
        {
            if (root == null)
            {
                return null;
            }

            var exact = MyAnonaMouseUserDataResponse.ReadNumber(root[bytesKey]);
            if (exact.HasValue && exact.Value <= long.MaxValue)
            {
                return (long)Math.Floor(exact.Value);
            }

            var token = root[textKey];
            if (token == null)
            {
                return null;
            }

            if (token.Type is JTokenType.Integer or JTokenType.Float)
            {
                var number = MyAnonaMouseUserDataResponse.ReadNumber(token);
                return number.HasValue ? (long)Math.Floor(number.Value) : null;
            }

            return token.Type == JTokenType.String ? ParseSize(token.ToObject<string>()) : null;
        }

        internal static long? ParseSize(string text)
        {
            if (string.IsNullOrWhiteSpace(text))
            {
                return null;
            }

            var match = SizeRegex.Match(text);
            if (!match.Success ||
                !double.TryParse(match.Groups[1].Value.Replace(",", string.Empty), NumberStyles.Float, CultureInfo.InvariantCulture, out var value))
            {
                return null;
            }

            var unit = match.Groups[2].Success ? match.Groups[2].Value.ToUpperInvariant() : "B";
            var power = unit.Length > 0 && "KMGTP".IndexOf(unit[0]) >= 0 ? "KMGTP".IndexOf(unit[0]) + 1 : 0;
            var bytes = value * Math.Pow(1024, power);

            return bytes >= 0 && bytes < long.MaxValue ? (long)Math.Floor(bytes) : null;
        }

        // Infinite ratios ("Inf.", no download yet) are stored as unknown: the byte totals decide.
        internal static double? ReadRatio(JToken token)
        {
            var value = MyAnonaMouseUserDataResponse.ReadNumber(token);
            return value.HasValue && !double.IsInfinity(value.Value) && !double.IsNaN(value.Value) ? value : null;
        }

        internal static bool? ReadSeedOnly(JObject root)
        {
            if (root == null)
            {
                return null;
            }

            bool? result = null;

            foreach (var key in SeedOnlyKeys)
            {
                var flag = ReadFlag(root[key]);
                if (flag == true)
                {
                    return true;
                }

                result ??= flag;
            }

            foreach (var key in CanDownloadKeys)
            {
                var flag = ReadFlag(root[key]);
                if (flag == false)
                {
                    return true;
                }

                if (flag == true)
                {
                    result ??= false;
                }
            }

            return result;
        }

        private static bool? ReadFlag(JToken token)
        {
            if (token == null)
            {
                return null;
            }

            switch (token.Type)
            {
                case JTokenType.Boolean:
                    return token.ToObject<bool>();
                case JTokenType.Integer:
                    return token.ToObject<long>() != 0;
                case JTokenType.String:
                    var text = token.ToObject<string>()?.Trim().ToLowerInvariant();
                    return text switch
                    {
                        "1" or "true" or "yes" or "y" or "on" => true,
                        "0" or "false" or "no" or "n" or "off" or "" => false,
                        _ => null
                    };
                default:
                    return null;
            }
        }
    }
}
