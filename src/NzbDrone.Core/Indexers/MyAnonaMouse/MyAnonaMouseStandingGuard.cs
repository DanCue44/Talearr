using System;
using NzbDrone.Core.Parser.Model;

namespace NzbDrone.Core.Indexers.MyAnonaMouse
{
    public interface IMyAnonaMouseStandingGuard
    {
        // Null when the MAM grab may go ahead (or the release is not from MAM), otherwise why not.
        string GetRejection(RemoteBook remoteBook);
    }

    // TaleArr: seed-only and ratio guard for every MAM grab (native and Prowlarr MAM results that
    // share the native indexer's account), used by the decision engine and again at grab time in
    // DownloadService so grabs that skip the decision engine are covered too. Rules live in
    // MyAnonaMouseAccountStanding.
    public class MyAnonaMouseStandingGuard : IMyAnonaMouseStandingGuard
    {
        private readonly IMyAnonaMouseReleasePolicy _releasePolicy;

        public MyAnonaMouseStandingGuard(IMyAnonaMouseReleasePolicy releasePolicy)
        {
            _releasePolicy = releasePolicy;
        }

        // For tests.
        internal Func<DateTime> UtcNow { get; set; } = () => DateTime.UtcNow;

        public string GetRejection(RemoteBook remoteBook)
        {
            var release = remoteBook?.Release;
            if (release == null)
            {
                return null;
            }

            var settings = _releasePolicy.ResolveSettings(release);

            // No native MAM indexer means no account status to judge by: keep the old behavior.
            if (settings == null || string.IsNullOrWhiteSpace(settings.MamId))
            {
                return null;
            }

            return MyAnonaMouseAccountStanding.Evaluate(settings, IsRatioExempt(release, settings), release.Size, UtcNow());
        }

        // Freeleech for this user (global, personal or VIP), or a wedge TaleArr will request at
        // grab time while wedges are left.
        public static bool IsRatioExempt(ReleaseInfo release, MyAnonaMouseSettings settings)
        {
            if (MyAnonaMouseReleasePolicy.IsFreeleech(release))
            {
                return true;
            }

            if (settings == null ||
                settings.UseFreeleechWedge != (int)MyAnonaMouseFreeleechWedgeAction.Preferred ||
                settings.FreeleechWedges is not > 0 ||
                !HasQueryFlag(release?.DownloadUrl, "canUseToken"))
            {
                return false;
            }

            return !settings.UseFreeleechOnlyForAudiobooks || HasQueryFlag(release.DownloadUrl, "isAudiobook");
        }

        private static bool HasQueryFlag(string url, string key)
        {
            if (string.IsNullOrWhiteSpace(url) || !Uri.TryCreate(url, UriKind.Absolute, out var uri))
            {
                return false;
            }

            var value = System.Web.HttpUtility.ParseQueryString(uri.Query)[key];
            return string.Equals(value, "true", StringComparison.OrdinalIgnoreCase);
        }
    }
}
