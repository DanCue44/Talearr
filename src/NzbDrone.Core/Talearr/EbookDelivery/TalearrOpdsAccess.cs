using System;
using System.Security.Cryptography;
using System.Text;

namespace NzbDrone.Core.Talearr.EbookDelivery
{
    public enum TalearrOpdsAccessResult
    {
        // The feed is off: answer 404, as if it did not exist.
        Disabled,
        Allowed,

        // Ask for HTTP Basic credentials.
        NoCredentials,
        Rejected
    }

    // Talearr: who may read /opds. Follows Talearr's own authentication settings:
    // - the API key, as ?apikey= or the X-Api-Key header (like the calendar feed);
    // - HTTP Basic with a Talearr user (Forms or Basic authentication, with the same failed-login
    //   lockout as the login page), or any user name with the API key as the password, for readers
    //   that only speak Basic while Talearr signs in through Plex, OIDC or a proxy;
    // - no credentials from a local address when Authentication Required is
    //   "Disabled for Local Addresses", exactly like the web UI.
    public static class TalearrOpdsAccess
    {
        public static TalearrOpdsAccessResult Decide(bool enabled,
                                                     string apiKey,
                                                     string providedApiKey,
                                                     string basicUser,
                                                     string basicPassword,
                                                     bool localWithoutLogin,
                                                     Func<string, string, bool> login)
        {
            if (!enabled)
            {
                return TalearrOpdsAccessResult.Disabled;
            }

            if (!string.IsNullOrEmpty(providedApiKey))
            {
                return KeyMatches(apiKey, providedApiKey) ? TalearrOpdsAccessResult.Allowed : TalearrOpdsAccessResult.Rejected;
            }

            if (basicUser != null || basicPassword != null)
            {
                if (KeyMatches(apiKey, basicPassword))
                {
                    return TalearrOpdsAccessResult.Allowed;
                }

                if (!string.IsNullOrEmpty(basicUser) && login != null && login(basicUser, basicPassword ?? string.Empty))
                {
                    return TalearrOpdsAccessResult.Allowed;
                }

                return localWithoutLogin ? TalearrOpdsAccessResult.Allowed : TalearrOpdsAccessResult.Rejected;
            }

            return localWithoutLogin ? TalearrOpdsAccessResult.Allowed : TalearrOpdsAccessResult.NoCredentials;
        }

        public static bool KeyMatches(string apiKey, string provided)
        {
            if (string.IsNullOrEmpty(apiKey) || string.IsNullOrEmpty(provided))
            {
                return false;
            }

            var expected = Encoding.UTF8.GetBytes(apiKey);
            var actual = Encoding.UTF8.GetBytes(provided);

            return expected.Length == actual.Length && CryptographicOperations.FixedTimeEquals(expected, actual);
        }

        // Parses an "Authorization: Basic ..." header. Returns false when it is not Basic.
        public static bool TryParseBasic(string header, out string user, out string password)
        {
            user = null;
            password = null;

            if (string.IsNullOrWhiteSpace(header) || !header.StartsWith("Basic ", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            try
            {
                var decoded = Encoding.UTF8.GetString(Convert.FromBase64String(header.Substring(6).Trim()));
                var split = decoded.IndexOf(':');

                if (split < 0)
                {
                    return false;
                }

                user = decoded.Substring(0, split);
                password = decoded.Substring(split + 1);
                return true;
            }
            catch (FormatException)
            {
                return false;
            }
        }
    }
}
