using System;
using System.Collections.Generic;
using System.Linq;

namespace NzbDrone.Core.Talearr.Recordings
{
    // Talearr: one Audible marketplace (store country). Lookups go to that store's catalog
    // host, and links point at that store's web site.
    public sealed class TalearrAudibleMarketplace
    {
        public TalearrAudibleMarketplace(string code, string name, string domain, string acceptLanguage)
        {
            Code = code;
            Name = name;
            Domain = domain;
            AcceptLanguage = acceptLanguage;
        }

        // Short code stored in settings and in talearr.db (us, uk, ca, ...). Also the Audnexus region.
        public string Code { get; }

        public string Name { get; }

        // audible.com, audible.co.uk, ...
        public string Domain { get; }

        public string AcceptLanguage { get; }

        public string ApiBaseUrl => $"https://api.{Domain}";

        public string ProductUrl(string asin) => $"https://www.{Domain}/pd/{asin}";
    }

    public static class TalearrAudibleMarketplaces
    {
        public const string Default = "us";

        public static readonly IReadOnlyList<TalearrAudibleMarketplace> All = new List<TalearrAudibleMarketplace>
        {
            new TalearrAudibleMarketplace("us", "United States", "audible.com", "en-US,en;q=0.9"),
            new TalearrAudibleMarketplace("uk", "United Kingdom", "audible.co.uk", "en-GB,en;q=0.9"),
            new TalearrAudibleMarketplace("ca", "Canada", "audible.ca", "en-CA,en;q=0.9,fr-CA;q=0.8"),
            new TalearrAudibleMarketplace("au", "Australia", "audible.com.au", "en-AU,en;q=0.9"),
            new TalearrAudibleMarketplace("fr", "France", "audible.fr", "fr-FR,fr;q=0.9,en;q=0.5"),
            new TalearrAudibleMarketplace("de", "Germany", "audible.de", "de-DE,de;q=0.9,en;q=0.5"),
            new TalearrAudibleMarketplace("it", "Italy", "audible.it", "it-IT,it;q=0.9,en;q=0.5"),
            new TalearrAudibleMarketplace("es", "Spain", "audible.es", "es-ES,es;q=0.9,en;q=0.5"),
            new TalearrAudibleMarketplace("jp", "Japan", "audible.co.jp", "ja-JP,ja;q=0.9,en;q=0.5"),
            new TalearrAudibleMarketplace("in", "India", "audible.in", "en-IN,en;q=0.9")
        };

        public static bool IsKnown(string code)
        {
            return Find(code) != null;
        }

        public static TalearrAudibleMarketplace Find(string code)
        {
            if (string.IsNullOrWhiteSpace(code))
            {
                return null;
            }

            var normalized = code.Trim().ToLowerInvariant();
            if (normalized == "gb")
            {
                normalized = "uk";
            }

            return All.FirstOrDefault(m => m.Code.Equals(normalized, StringComparison.Ordinal));
        }

        // Unknown or empty codes fall back to the default marketplace.
        public static TalearrAudibleMarketplace Get(string code)
        {
            return Find(code) ?? Find(Default);
        }
    }
}
