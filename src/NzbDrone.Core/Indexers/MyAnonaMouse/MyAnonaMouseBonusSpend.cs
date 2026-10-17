using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Text.RegularExpressions;
using Newtonsoft.Json.Linq;
using NzbDrone.Core.Messaging.Commands;

namespace NzbDrone.Core.Indexers.MyAnonaMouse
{
    // TaleArr: bonus points auto-spend for the native MyAnonaMouse indexer. MAM lists the Bonus
    // Points Store (json/bonusBuy.php) as allowed automation, with five spend types: VIP, upload,
    // wedges, gift and sendWedge. gift and sendWedge may not be automated, so they are not part of
    // this enum and nothing in TaleArr can build a request for them. The Millionaire's Vault may
    // not be automated either and is never called.
    public enum MyAnonaMouseBonusSpendType
    {
        Vip = 0,
        Upload = 1,
        Wedges = 2
    }

    public class SpendMyAnonaMouseBonusPointsCommand : Command
    {
    }

    // Source of the purchase notifications (sent through On Health Issue); the name must start
    // with "Talearr" so notice-level messages go out with Include Health Warnings.
    public static class TalearrMyAnonaMouseBonusPurchase
    {
    }

    public class MyAnonaMouseBonusPurchase
    {
        public MyAnonaMouseBonusSpendType SpendType { get; set; }

        // Upload credit in GiB (MAM minimum 50). Only for Upload.
        public int? AmountGiB { get; set; }

        public int EstimatedCost { get; set; }
        public string Reason { get; set; }
    }

    public class MyAnonaMouseBonusPurchaseResult
    {
        public bool Success { get; set; }
        public string Message { get; set; }

        // The response body, whitespace collapsed and truncated, for the log and the purchase list.
        public string RawResponse { get; set; }
        public double? RemainingPoints { get; set; }

        public static MyAnonaMouseBonusPurchaseResult Failed(string message, string raw)
        {
            return new MyAnonaMouseBonusPurchaseResult { Success = false, Message = message, RawResponse = raw };
        }
    }

    // One attempt as stored in talearr.db (MamBonusPurchases). Dry runs are stored too but never
    // count toward the daily cap or the back-off.
    public class MyAnonaMouseBonusPurchaseRecord
    {
        public long Id { get; set; }
        public int IndexerId { get; set; }
        public DateTime AttemptedAt { get; set; }
        public string SpendType { get; set; }
        public string Parameters { get; set; }
        public int EstimatedCost { get; set; }
        public double? PointsBefore { get; set; }
        public bool DryRun { get; set; }
        public bool Success { get; set; }
        public string Message { get; set; }
        public string Response { get; set; }
        public string Reason { get; set; }
    }

    public static class MyAnonaMouseBonusRequestBuilder
    {
        public const string EndpointPath = "/json/bonusBuy.php";

        // Every spendtype value TaleArr can send. gift and sendWedge are deliberately absent.
        public static readonly IReadOnlyList<string> AllowedSpendTypes = new[] { "VIP", "upload", "wedges" };

        public static string SpendTypeValue(MyAnonaMouseBonusSpendType type)
        {
            return type switch
            {
                MyAnonaMouseBonusSpendType.Vip => "VIP",
                MyAnonaMouseBonusSpendType.Upload => "upload",
                MyAnonaMouseBonusSpendType.Wedges => "wedges",
                _ => throw new ArgumentOutOfRangeException(nameof(type), type, "Not an allowed MAM bonus spend type")
            };
        }

        public static List<KeyValuePair<string, string>> BuildQuery(MyAnonaMouseBonusPurchase purchase)
        {
            if (purchase == null)
            {
                throw new ArgumentNullException(nameof(purchase));
            }

            var query = new List<KeyValuePair<string, string>>
            {
                new KeyValuePair<string, string>("spendtype", SpendTypeValue(purchase.SpendType))
            };

            switch (purchase.SpendType)
            {
                case MyAnonaMouseBonusSpendType.Vip:
                    // The API only accepts "max": fill to the 90-day limit (at least 7 days).
                    query.Add(new KeyValuePair<string, string>("duration", "max"));
                    break;
                case MyAnonaMouseBonusSpendType.Upload:
                    if (!purchase.AmountGiB.HasValue || purchase.AmountGiB.Value < MyAnonaMouseBonusSpendRules.MinimumUploadGiB)
                    {
                        throw new ArgumentOutOfRangeException(nameof(purchase), purchase.AmountGiB, "MAM sells upload credit from 50 GiB");
                    }

                    query.Add(new KeyValuePair<string, string>("amount", purchase.AmountGiB.Value.ToString(CultureInfo.InvariantCulture)));
                    break;
            }

            return query;
        }

        public static string BuildParameters(MyAnonaMouseBonusPurchase purchase)
        {
            return string.Join("&", BuildQuery(purchase).Select(p => $"{Uri.EscapeDataString(p.Key)}={Uri.EscapeDataString(p.Value)}"));
        }

        public static string BuildUrl(string baseUrl, MyAnonaMouseBonusPurchase purchase)
        {
            var root = string.IsNullOrWhiteSpace(baseUrl) ? "https://www.myanonamouse.net" : baseUrl.TrimEnd('/');
            return $"{root}{EndpointPath}?{BuildParameters(purchase)}";
        }
    }

    // MAM documents no example output for bonusBuy.php, so the reply is read defensively: only an
    // explicit success flag counts as a purchase; anything else is a failure (and backs off).
    public static class MyAnonaMouseBonusResponseParser
    {
        public const int MaxRawLength = 500;

        private static readonly Regex Whitespace = new Regex(@"\s+", RegexOptions.Compiled);
        private static readonly string[] MessageFields = { "error", "message", "msg", "status", "result", "reason" };

        public static MyAnonaMouseBonusPurchaseResult Parse(HttpStatusCode status, string body)
        {
            var raw = Truncate(body);

            if (status != HttpStatusCode.OK)
            {
                return MyAnonaMouseBonusPurchaseResult.Failed($"HTTP {(int)status} {status}", raw);
            }

            if (string.IsNullOrWhiteSpace(body))
            {
                return MyAnonaMouseBonusPurchaseResult.Failed("Empty response", raw);
            }

            JObject json;
            try
            {
                json = JToken.Parse(body) as JObject;
            }
            catch (Exception)
            {
                json = null;
            }

            if (json == null)
            {
                return MyAnonaMouseBonusPurchaseResult.Failed("Unrecognised response (not a JSON object)", raw);
            }

            var message = ReadMessage(json);
            var success = ReadFlag(json["success"] ?? json["Success"]);

            if (success == true)
            {
                return new MyAnonaMouseBonusPurchaseResult
                {
                    Success = true,
                    Message = message ?? "MAM reported success",
                    RawResponse = raw,
                    RemainingPoints = MyAnonaMouseUserDataResponse.ReadNumber(json["seedbonus"])
                };
            }

            if (success == false)
            {
                return MyAnonaMouseBonusPurchaseResult.Failed(message ?? "MAM reported failure", raw);
            }

            return MyAnonaMouseBonusPurchaseResult.Failed(message != null ? $"Unrecognised response: {message}" : "Unrecognised response", raw);
        }

        public static string Truncate(string body)
        {
            if (string.IsNullOrEmpty(body))
            {
                return body;
            }

            var collapsed = Whitespace.Replace(body, " ").Trim();
            return collapsed.Length <= MaxRawLength ? collapsed : collapsed.Substring(0, MaxRawLength) + "...";
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
                    return token.Value<bool>();
                case JTokenType.Integer:
                    return token.Value<long>() switch { 1 => true, 0 => false, _ => null };
                case JTokenType.String:
                    var text = token.Value<string>()?.Trim().ToLowerInvariant();
                    return text switch
                    {
                        "true" or "1" or "yes" or "ok" or "success" => true,
                        "false" or "0" or "no" or "error" or "failed" => false,
                        _ => null
                    };
                default:
                    return null;
            }
        }

        private static string ReadMessage(JObject json)
        {
            foreach (var field in MessageFields)
            {
                var token = json[field];
                if (token == null)
                {
                    continue;
                }

                if (token.Type == JTokenType.String && !string.IsNullOrWhiteSpace(token.Value<string>()))
                {
                    return Truncate(token.Value<string>());
                }

                if (token.Type is JTokenType.Object or JTokenType.Array)
                {
                    return Truncate(token.ToString(Newtonsoft.Json.Formatting.None));
                }
            }

            return null;
        }
    }

    // TaleArr: optional jsonLoad.php fields read for auto-spend. Every one may be missing or oddly
    // typed; a value that cannot be read is null and never fails the account refresh.
    public class MyAnonaMouseAccountExtras
    {
        private static readonly string[] SeedOnlyKeys = { "seed_only", "seedonly", "seedOnly", "seed_only_mode" };

        public DateTime? VipUntilUtc { get; set; }
        public double? Ratio { get; set; }
        public long? UploadedBytes { get; set; }
        public long? DownloadedBytes { get; set; }
        public bool? SeedOnly { get; set; }

        public static MyAnonaMouseAccountExtras Read(MyAnonaMouseUserDataResponse data)
        {
            var extras = new MyAnonaMouseAccountExtras();
            if (data == null)
            {
                return extras;
            }

            try
            {
                extras.VipUntilUtc = ReadDate(data.VipUntil);
                extras.UploadedBytes = ReadLong(data.UploadedBytes);
                extras.DownloadedBytes = ReadLong(data.DownloadedBytes);
                extras.Ratio = ReadRatio(data.Ratio, extras.UploadedBytes, extras.DownloadedBytes);

                if (data.Extra != null)
                {
                    foreach (var key in SeedOnlyKeys)
                    {
                        if (data.Extra.TryGetValue(key, out var token))
                        {
                            extras.SeedOnly = ReadBool(token);
                            if (extras.SeedOnly.HasValue)
                            {
                                break;
                            }
                        }
                    }
                }
            }
            catch (Exception)
            {
                // Optional data only.
            }

            return extras;
        }

        public static DateTime? ReadDate(JToken token)
        {
            if (token == null || token.Type is JTokenType.Null or JTokenType.Undefined)
            {
                return null;
            }

            if (token.Type == JTokenType.Date)
            {
                return DateTime.SpecifyKind(token.Value<DateTime>().ToUniversalTime(), DateTimeKind.Utc);
            }

            var number = MyAnonaMouseUserDataResponse.ReadNumber(token);
            if (token.Type is JTokenType.Integer or JTokenType.Float ||
                (number.HasValue && token.Type == JTokenType.String && Regex.IsMatch(token.Value<string>().Trim(), @"^\d+$")))
            {
                // Unix seconds; anything before 2001 is a placeholder.
                return number.HasValue && number.Value > 1_000_000_000 && number.Value < 253402300799
                    ? DateTimeOffset.FromUnixTimeSeconds((long)number.Value).UtcDateTime
                    : null;
            }

            if (token.Type == JTokenType.String &&
                DateTime.TryParse(token.Value<string>(), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var parsed) &&
                parsed.Year > 2000)
            {
                return DateTime.SpecifyKind(parsed, DateTimeKind.Utc);
            }

            return null;
        }

        public static double? ReadRatio(JToken ratio, long? uploaded, long? downloaded)
        {
            var value = MyAnonaMouseUserDataResponse.ReadNumber(ratio);
            if (value.HasValue && !double.IsInfinity(value.Value) && !double.IsNaN(value.Value))
            {
                return value;
            }

            // "Inf." and similar mean nothing downloaded yet: no ratio problem, so unknown.
            if (ratio != null && ratio.Type == JTokenType.String)
            {
                return null;
            }

            if (uploaded.HasValue && downloaded.HasValue && downloaded.Value > 0)
            {
                return (double)uploaded.Value / downloaded.Value;
            }

            return null;
        }

        private static long? ReadLong(JToken token)
        {
            var value = MyAnonaMouseUserDataResponse.ReadNumber(token);
            return value.HasValue && value.Value < long.MaxValue ? (long)Math.Floor(value.Value) : null;
        }

        private static bool? ReadBool(JToken token)
        {
            if (token == null)
            {
                return null;
            }

            switch (token.Type)
            {
                case JTokenType.Boolean:
                    return token.Value<bool>();
                case JTokenType.Integer:
                    return token.Value<long>() != 0;
                case JTokenType.String:
                    var text = token.Value<string>()?.Trim().ToLowerInvariant();
                    return text switch
                    {
                        "true" or "1" or "yes" => true,
                        "false" or "0" or "no" or "" => false,
                        _ => null
                    };
                default:
                    return null;
            }
        }
    }

    public class MyAnonaMouseBonusDecision
    {
        public MyAnonaMouseBonusPurchase Purchase { get; set; }
        public List<string> Skipped { get; } = new List<string>();
    }

    // TaleArr: the auto-spend decision, kept pure so it is tested without MAM. One purchase at most
    // per run, in this order: ratio safety net (account health), VIP, wedges.
    public static class MyAnonaMouseBonusSpendRules
    {
        public const int VipMaxDays = 90;
        public const int VipMinimumPurchaseDays = 7;
        public const int VipPointsPerFourWeeks = 5000;
        public const int WedgeCost = 50000;
        public const int PointsPerGiB = 500;
        public const int MinimumUploadGiB = 50;

        public static readonly TimeSpan FailureBackoff = TimeSpan.FromHours(24);
        public static readonly TimeSpan CapWindow = TimeSpan.FromHours(24);

        // Classes that can buy VIP on MAM's store ("requires rank Power User or VIP"). Higher
        // classes such as Elite VIP or staff are left alone.
        private static readonly string[] VipBuyerClasses = { "Power User", "VIP" };

        public static bool AnyRuleEnabled(MyAnonaMouseSettings settings)
        {
            return settings != null && (settings.BonusKeepVip || settings.BonusKeepWedges || settings.BonusRatioSafetyNet);
        }

        // MAM demotes accounts with a low ratio to Mouse, and can make them seed-only.
        public static bool IsRestricted(MyAnonaMouseSettings settings)
        {
            return settings != null &&
                   (settings.SeedOnly == true ||
                    string.Equals(settings.UserClass?.Trim(), "Mouse", StringComparison.OrdinalIgnoreCase));
        }

        public static bool CanBuyVip(string userClass)
        {
            return VipBuyerClasses.Any(c => string.Equals(c, userClass?.Trim(), StringComparison.OrdinalIgnoreCase));
        }

        public static int UploadCost(int gib)
        {
            return gib * PointsPerGiB;
        }

        // "max" fills VIP to 90 days, and buys at least 7 days.
        public static int VipCost(double daysLeft)
        {
            var days = Math.Max(VipMinimumPurchaseDays, VipMaxDays - Math.Max(0, daysLeft));
            return (int)Math.Ceiling(days * VipPointsPerFourWeeks / 28.0);
        }

        public static MyAnonaMouseBonusDecision Decide(MyAnonaMouseSettings settings,
                                                       IEnumerable<MyAnonaMouseBonusPurchaseRecord> history,
                                                       DateTime now)
        {
            var decision = new MyAnonaMouseBonusDecision();

            if (!AnyRuleEnabled(settings))
            {
                decision.Skipped.Add("no auto-spend rule is enabled");
                return decision;
            }

            if (!settings.BonusPoints.HasValue || !MamUnsatisfiedSlotGuard.HasFreshStatus(settings, now))
            {
                decision.Skipped.Add("account status is unknown or stale");
                return decision;
            }

            var sent = (history ?? Enumerable.Empty<MyAnonaMouseBonusPurchaseRecord>())
                .Where(r => !r.DryRun && r.AttemptedAt <= now.AddMinutes(5))
                .ToList();

            // After a purchase the stored balance is stale until the account is refreshed again.
            var lastSent = sent.Count == 0 ? (DateTime?)null : sent.Max(r => r.AttemptedAt);
            if (lastSent.HasValue && lastSent.Value >= settings.UnsatisfiedStatusRefreshedUtc.Value)
            {
                decision.Skipped.Add("waiting for an account refresh after the last purchase");
                return decision;
            }

            var cap = Math.Max(1, settings.BonusDailyPurchaseCap);
            var recent = sent.Count(r => now - r.AttemptedAt < CapWindow);
            if (recent >= cap)
            {
                decision.Skipped.Add($"daily cap reached ({recent} of {cap} in 24 hours)");
                return decision;
            }

            var points = Math.Floor(settings.BonusPoints.Value);
            var reserve = Math.Max(0, settings.BonusReservePoints);

            var candidates = new Func<MyAnonaMouseBonusPurchase>[]
            {
                () => RatioRule(settings, points, reserve, decision),
                () => VipRule(settings, points, reserve, now, decision),
                () => WedgeRule(settings, points, reserve, decision)
            };

            foreach (var candidate in candidates)
            {
                var purchase = candidate();
                if (purchase == null)
                {
                    continue;
                }

                var spendType = MyAnonaMouseBonusRequestBuilder.SpendTypeValue(purchase.SpendType);
                var failure = sent
                    .Where(r => string.Equals(r.SpendType, spendType, StringComparison.Ordinal))
                    .OrderByDescending(r => r.AttemptedAt)
                    .FirstOrDefault();

                if (failure != null && !failure.Success && now - failure.AttemptedAt < FailureBackoff)
                {
                    decision.Skipped.Add($"{spendType}: backing off until {failure.AttemptedAt.Add(FailureBackoff):yyyy-MM-dd HH:mm} UTC after MAM refused ({failure.Message})");
                    continue;
                }

                decision.Purchase = purchase;
                return decision;
            }

            return decision;
        }

        private static bool Affordable(double points, int cost, int reserve, string what, MyAnonaMouseBonusDecision decision)
        {
            if (points - cost >= reserve)
            {
                return true;
            }

            decision.Skipped.Add($"{what}: {points:N0} points minus {cost:N0} would go below the {reserve:N0} reserve");
            return false;
        }

        private static MyAnonaMouseBonusPurchase RatioRule(MyAnonaMouseSettings settings, double points, int reserve, MyAnonaMouseBonusDecision decision)
        {
            if (!settings.BonusRatioSafetyNet)
            {
                return null;
            }

            string reason;
            if (IsRestricted(settings))
            {
                reason = settings.SeedOnly == true ? "account is seed-only" : "account was demoted to Mouse";
            }
            else if (settings.Ratio.HasValue && settings.Ratio.Value < settings.BonusMinimumRatio)
            {
                reason = $"ratio {settings.Ratio.Value.ToString("0.###", CultureInfo.InvariantCulture)} is below {settings.BonusMinimumRatio.ToString("0.###", CultureInfo.InvariantCulture)}";
            }
            else
            {
                decision.Skipped.Add(settings.Ratio.HasValue ? "upload: ratio is fine" : "upload: ratio unknown");
                return null;
            }

            var gib = Math.Max(MinimumUploadGiB, settings.BonusUploadGiB);
            var cost = UploadCost(gib);
            if (!Affordable(points, cost, reserve, "upload", decision))
            {
                return null;
            }

            return new MyAnonaMouseBonusPurchase
            {
                SpendType = MyAnonaMouseBonusSpendType.Upload,
                AmountGiB = gib,
                EstimatedCost = cost,
                Reason = reason
            };
        }

        private static MyAnonaMouseBonusPurchase VipRule(MyAnonaMouseSettings settings, double points, int reserve, DateTime now, MyAnonaMouseBonusDecision decision)
        {
            if (!settings.BonusKeepVip)
            {
                return null;
            }

            if (!CanBuyVip(settings.UserClass))
            {
                decision.Skipped.Add($"VIP: class '{settings.UserClass}' cannot buy VIP (Power User or VIP only)");
                return null;
            }

            var isVip = string.Equals(settings.UserClass?.Trim(), "VIP", StringComparison.OrdinalIgnoreCase);
            double daysLeft;
            string reason;

            if (!isVip)
            {
                daysLeft = 0;
                reason = "VIP is not active";
            }
            else if (settings.VipUntilUtc.HasValue)
            {
                daysLeft = Math.Max(0, (settings.VipUntilUtc.Value - now).TotalDays);
                if (daysLeft >= settings.BonusVipRenewDays)
                {
                    decision.Skipped.Add($"VIP: {daysLeft:0.#} days left");
                    return null;
                }

                reason = $"VIP has {daysLeft:0.#} days left";
            }
            else
            {
                decision.Skipped.Add("VIP: expiry unknown (MAM did not send vip_until)");
                return null;
            }

            var cost = VipCost(daysLeft);
            if (!Affordable(points, cost, reserve, "VIP", decision))
            {
                return null;
            }

            return new MyAnonaMouseBonusPurchase
            {
                SpendType = MyAnonaMouseBonusSpendType.Vip,
                EstimatedCost = cost,
                Reason = reason
            };
        }

        private static MyAnonaMouseBonusPurchase WedgeRule(MyAnonaMouseSettings settings, double points, int reserve, MyAnonaMouseBonusDecision decision)
        {
            if (!settings.BonusKeepWedges)
            {
                return null;
            }

            if (!settings.FreeleechWedges.HasValue)
            {
                decision.Skipped.Add("wedges: balance unknown");
                return null;
            }

            if (settings.FreeleechWedges.Value >= settings.BonusMinimumWedges)
            {
                decision.Skipped.Add($"wedges: {settings.FreeleechWedges.Value} on hand");
                return null;
            }

            if (!Affordable(points, WedgeCost, reserve, "wedges", decision))
            {
                return null;
            }

            return new MyAnonaMouseBonusPurchase
            {
                SpendType = MyAnonaMouseBonusSpendType.Wedges,
                EstimatedCost = WedgeCost,
                Reason = $"{settings.FreeleechWedges.Value} wedges, fewer than {settings.BonusMinimumWedges}"
            };
        }
    }
}
