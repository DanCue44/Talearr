using System;
using System.Globalization;
using System.Net;
using Newtonsoft.Json.Linq;
using NzbDrone.Common.Http;

namespace NzbDrone.Core.Indexers.MyAnonaMouse
{
    // TaleArr: reads MAM responses for the two session signals that matter: a rejected mam_id
    // (expired, revoked or locked to another IP) and a rotated mam_id that MAM hands back in
    // Set-Cookie. Pure functions over HttpResponse so they can be tested with recorded replies.
    public static class MyAnonaMouseSessionDetector
    {
        public const string CookieName = "mam_id";

        // inspectBody: also look for signed-out text in the body. Only safe where MAM answers with
        // JSON when signed in (search, account status); download pages may link to login.php.
        public static bool IsSessionRejected(HttpResponse response, bool inspectBody = true)
        {
            if (response == null)
            {
                return false;
            }

            if (response.StatusCode == HttpStatusCode.Unauthorized || response.StatusCode == HttpStatusCode.Forbidden)
            {
                return true;
            }

            if (response.HasHttpRedirect && PointsToLogin(response.Headers?["Location"]))
            {
                return true;
            }

            if (!inspectBody)
            {
                return false;
            }

            string content;
            try
            {
                content = response.Content;
            }
            catch (Exception)
            {
                return false;
            }

            if (string.IsNullOrWhiteSpace(content))
            {
                return false;
            }

            if (IsJson(response, content))
            {
                return JsonReportsSignedOut(content);
            }

            return ContainsSignedOutText(content) || content.IndexOf("login.php", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        // A rotated value is a non-empty mam_id in Set-Cookie that differs from the one sent and
        // is not a deletion (MAM clears the cookie with "deleted" or an expiry in the past).
        public static bool TryGetRotatedMamId(HttpResponse response, string sentMamId, out string rotatedMamId)
        {
            rotatedMamId = null;

            if (response == null)
            {
                return false;
            }

            foreach (var header in response.GetCookieHeaders())
            {
                if (!TryParseSetCookie(header, out var name, out var value, out var deleted) ||
                    !string.Equals(name, CookieName, StringComparison.Ordinal) ||
                    deleted)
                {
                    continue;
                }

                if (!string.Equals(value, sentMamId, StringComparison.Ordinal))
                {
                    rotatedMamId = value;
                }
            }

            return rotatedMamId != null;
        }

        internal static bool TryParseSetCookie(string header, out string name, out string value, out bool deleted)
        {
            name = null;
            value = null;
            deleted = false;

            if (string.IsNullOrWhiteSpace(header))
            {
                return false;
            }

            var parts = header.Split(';');
            var pair = parts[0].Split('=', 2);
            if (pair.Length != 2)
            {
                return false;
            }

            name = pair[0].Trim();
            value = pair[1].Trim().Trim('"');

            if (value.Length == 0 || string.Equals(value, "deleted", StringComparison.OrdinalIgnoreCase))
            {
                deleted = true;
            }

            for (var i = 1; i < parts.Length; i++)
            {
                var attribute = parts[i].Split('=', 2);
                var key = attribute[0].Trim();
                var attributeValue = attribute.Length > 1 ? attribute[1].Trim() : string.Empty;

                if (key.Equals("max-age", StringComparison.OrdinalIgnoreCase) &&
                    int.TryParse(attributeValue, NumberStyles.Integer, CultureInfo.InvariantCulture, out var maxAge) &&
                    maxAge <= 0)
                {
                    deleted = true;
                }

                if (key.Equals("expires", StringComparison.OrdinalIgnoreCase) &&
                    DateTimeOffset.TryParse(attributeValue, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var expires) &&
                    expires < DateTimeOffset.UtcNow)
                {
                    deleted = true;
                }
            }

            return name.Length > 0;
        }

        private static bool PointsToLogin(string location)
        {
            return !string.IsNullOrWhiteSpace(location) &&
                   location.IndexOf("login", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private static bool IsJson(HttpResponse response, string content)
        {
            var contentType = response.Headers?.ContentType;
            if (!string.IsNullOrWhiteSpace(contentType) && contentType.IndexOf("json", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return true;
            }

            var trimmed = content.TrimStart();
            return trimmed.StartsWith("{") || trimmed.StartsWith("[");
        }

        private static bool JsonReportsSignedOut(string content)
        {
            try
            {
                if (JToken.Parse(content) is not JObject json)
                {
                    return false;
                }

                var message = json.Value<string>("error") ?? json.Value<string>("msg") ?? json.Value<string>("message");
                return !string.IsNullOrWhiteSpace(message) &&
                       (ContainsSignedOutText(message) || message.IndexOf("invalid session", StringComparison.OrdinalIgnoreCase) >= 0);
            }
            catch (Exception)
            {
                return false;
            }
        }

        private static bool ContainsSignedOutText(string text)
        {
            return text.IndexOf("not signed in", StringComparison.OrdinalIgnoreCase) >= 0 ||
                   text.IndexOf("not logged in", StringComparison.OrdinalIgnoreCase) >= 0;
        }
    }
}
