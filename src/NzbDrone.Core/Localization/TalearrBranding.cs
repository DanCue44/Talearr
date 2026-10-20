using System;
using System.Text.RegularExpressions;
using NzbDrone.Common.EnvironmentInfo;

namespace NzbDrone.Core.Localization
{
    // Talearr: rebrands upstream translation strings at load time instead of editing every
    // language file, so upstream localization changes merge without conflicts. Phrases that
    // refer to the upstream project itself (its servers, its Discord, its donation page)
    // are left alone.
    public static class TalearrBranding
    {
        private static readonly Regex UpstreamName = new Regex(@"\bChaptarr\b(?!'s servers| on Discord)", RegexOptions.Compiled | RegexOptions.CultureInvariant);

        public static string Apply(string key, string value)
        {
            if (string.IsNullOrEmpty(value) || key.StartsWith("Donations", StringComparison.Ordinal))
            {
                return value;
            }

            return UpstreamName.Replace(value, BuildInfo.ProductName);
        }
    }
}
