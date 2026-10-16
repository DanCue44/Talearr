using System;
using System.Text.RegularExpressions;
using NzbDrone.Core.Books;

namespace NzbDrone.Core.Talearr.Recordings
{
    // TaleArr: the provider identity talearr.db keys an edition by. Upstream's AddWantedEdition
    // gives cloned editions BookEditionIdentity.GetTrustedForeignEditionId as their foreign id
    // ("hc:edition:30583648"), while the base row keeps the metadata server's id
    // ("hc:edition:30583648-audiobook"). Both come from the same provider fields, so the trusted
    // id (or the stored id without its media-type suffix) is the same for a base edition and every
    // copy of it, and survives refresh and row churn.
    public static class TalearrEditionKey
    {
        private static readonly Regex MediaSuffix = new Regex(@"-(audiobook|ebook)$", RegexOptions.Compiled | RegexOptions.IgnoreCase);

        public static string For(Edition edition)
        {
            if (edition == null)
            {
                return null;
            }

            string trusted;
            try
            {
                trusted = BookEditionIdentity.GetTrustedForeignEditionId(edition);
            }
            catch (Exception)
            {
                trusted = null;
            }

            var key = !string.IsNullOrWhiteSpace(trusted) ? trusted : edition.ForeignEditionId;
            return string.IsNullOrWhiteSpace(key) ? null : MediaSuffix.Replace(key.Trim(), string.Empty);
        }

        public static bool Same(Edition a, Edition b)
        {
            var left = For(a);
            return left != null && string.Equals(left, For(b), StringComparison.OrdinalIgnoreCase);
        }
    }
}
