using System;
using System.Net;
using Newtonsoft.Json.Linq;
using NzbDrone.Core.Messaging.Commands;

namespace NzbDrone.Core.Indexers.MyAnonaMouse
{
    // Talearr: scheduled dynamic seedbox update (see MyAnonaMouseDynamicSeedboxService).
    public class UpdateMyAnonaMouseDynamicSeedboxCommand : Command
    {
    }

    public enum MyAnonaMouseSeedboxOutcome
    {
        // The IP MAM sees is already registered.
        NoChange = 0,

        // MAM registered the new IP.
        Updated = 1,

        // MAM accepts one change per hour; the next check retries.
        TooRecent = 2,

        // The mam_id is missing, expired or revoked.
        InvalidSession = 3,

        // The session is locked to another IP or ASN: the call did not leave from the torrent
        // client's network, or the VPN moved to a provider network the session is not locked to.
        NetworkMismatch = 4,

        // The session exists but may not set the dynamic seedbox IP (wrong session type or the
        // "Allow session to set dynamic seedbox" box is unchecked on MAM).
        NotAllowed = 5,

        // Anything else, including network errors and unreadable replies.
        Failed = 6
    }

    public sealed class MyAnonaMouseSeedboxResult
    {
        public MyAnonaMouseSeedboxOutcome Outcome { get; set; }
        public string Message { get; set; }
        public string Ip { get; set; }
        public string Asn { get; set; }
        public DateTime CheckedUtc { get; set; }

        public bool IsProblem => Outcome != MyAnonaMouseSeedboxOutcome.NoChange &&
                                 Outcome != MyAnonaMouseSeedboxOutcome.Updated &&
                                 Outcome != MyAnonaMouseSeedboxOutcome.TooRecent;
    }

    // Talearr: reads replies from https://t.myanonamouse.net/json/dynamicSeedbox.php. MAM answers
    // with {"Success": bool, "msg": "...", "ip": "...", "ASN": n, "AS": "..."} and an HTTP status
    // that matches (200, 403, 429). Known messages: "Completed", "No change", "Last change too
    // recent", "No Session Cookie", "Invalid session", "Incorrect session type - ...", and ASN or
    // IP mismatch wording for locked sessions. Matching is by keyword so small wording changes
    // still classify.
    public static class MyAnonaMouseDynamicSeedboxParser
    {
        public const string Endpoint = "https://t.myanonamouse.net/json/dynamicSeedbox.php";

        public static MyAnonaMouseSeedboxResult Parse(HttpStatusCode statusCode, string content, DateTime checkedUtc)
        {
            var result = new MyAnonaMouseSeedboxResult { CheckedUtc = checkedUtc };

            JObject json = null;
            try
            {
                json = string.IsNullOrWhiteSpace(content) ? null : JToken.Parse(content) as JObject;
            }
            catch (Exception)
            {
                json = null;
            }

            var message = json?.Value<string>("msg") ?? json?.Value<string>("message") ?? json?.Value<string>("error");
            var success = json?["Success"] ?? json?["success"];

            result.Message = string.IsNullOrWhiteSpace(message)
                ? (json == null ? $"Unreadable reply (HTTP {(int)statusCode})" : $"HTTP {(int)statusCode}")
                : message.Trim();
            result.Ip = json?.Value<string>("ip");
            result.Asn = json?["ASN"]?.ToString() ?? json?["asn"]?.ToString();

            result.Outcome = Classify(statusCode, result.Message, success?.Type == JTokenType.Boolean && success.Value<bool>());
            return result;
        }

        private static MyAnonaMouseSeedboxOutcome Classify(HttpStatusCode statusCode, string message, bool success)
        {
            var text = message ?? string.Empty;

            if (Has(text, "no change"))
            {
                return MyAnonaMouseSeedboxOutcome.NoChange;
            }

            if (Has(text, "completed"))
            {
                return MyAnonaMouseSeedboxOutcome.Updated;
            }

            if (Has(text, "too recent") || statusCode == (HttpStatusCode)429)
            {
                return MyAnonaMouseSeedboxOutcome.TooRecent;
            }

            if (Has(text, "asn") || Has(text, "ip mismatch") || Has(text, "mismatch"))
            {
                return MyAnonaMouseSeedboxOutcome.NetworkMismatch;
            }

            if (Has(text, "session type") || Has(text, "not allowed"))
            {
                return MyAnonaMouseSeedboxOutcome.NotAllowed;
            }

            if (Has(text, "session") || Has(text, "cookie") || Has(text, "not signed in") ||
                statusCode == HttpStatusCode.Unauthorized || statusCode == HttpStatusCode.Forbidden)
            {
                return MyAnonaMouseSeedboxOutcome.InvalidSession;
            }

            if (success && statusCode == HttpStatusCode.OK)
            {
                return MyAnonaMouseSeedboxOutcome.Updated;
            }

            return MyAnonaMouseSeedboxOutcome.Failed;
        }

        private static bool Has(string text, string value)
        {
            return text.IndexOf(value, StringComparison.OrdinalIgnoreCase) >= 0;
        }
    }
}
