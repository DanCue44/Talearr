using System;
using System.Linq;
using NzbDrone.Core.Datastore;
using NzbDrone.Core.Parser.Model;

namespace NzbDrone.Core.Indexers.MyAnonaMouse
{
    public interface IMyAnonaMouseReleasePolicy
    {
        // Settings that govern a MAM release, or null when the release is not from MAM. A MAM
        // release from another indexer (Prowlarr/Torznab) uses the first enabled native MAM
        // indexer's settings, or the defaults when there is none.
        MyAnonaMouseSettings ResolveSettings(ReleaseInfo release);
    }

    // Talearr: freeleech awareness shared by the ranking step and the Freeleech Only filter.
    public class MyAnonaMouseReleasePolicy : IMyAnonaMouseReleasePolicy
    {
        private static readonly MyAnonaMouseSettings Defaults = new MyAnonaMouseSettings();

        private readonly IIndexerFactory _indexerFactory;

        public MyAnonaMouseReleasePolicy(IIndexerFactory indexerFactory)
        {
            _indexerFactory = indexerFactory;
        }

        public MyAnonaMouseSettings ResolveSettings(ReleaseInfo release)
        {
            if (release == null)
            {
                return null;
            }

            IndexerDefinition own = null;
            if (release.IndexerId > 0)
            {
                try
                {
                    own = _indexerFactory.Find(release.IndexerId);
                }
                catch (ModelNotFoundException)
                {
                    own = null;
                }
            }

            if (own?.Settings is MyAnonaMouseSettings ownSettings)
            {
                return ownSettings;
            }

            if (!IsMamIndexerName(release.Indexer))
            {
                return null;
            }

            var native = _indexerFactory.All()
                .FirstOrDefault(d => d.Enable && d.Settings is MyAnonaMouseSettings);

            return native?.Settings as MyAnonaMouseSettings ?? Defaults;
        }

        // Global, personal (wedge already applied) or VIP freeleech for this user.
        public static bool IsFreeleech(ReleaseInfo release)
        {
            if (release == null)
            {
                return false;
            }

            return release.IndexerFlags.HasFlag(IndexerFlags.Freeleech) ||
                   release.IndexerFlags.HasFlag(IndexerFlags.VipFreeleech);
        }

        // 1 when the release should rank as preferred freeleech, otherwise 0.
        public static int FreeleechRank(IMyAnonaMouseReleasePolicy policy, ReleaseInfo release)
        {
            if (policy == null || !IsFreeleech(release))
            {
                return 0;
            }

            var settings = policy.ResolveSettings(release);
            return settings is { PreferFreeleech: true } ? 1 : 0;
        }

        public static bool IsMamIndexerName(string value)
        {
            return !string.IsNullOrWhiteSpace(value) &&
                   (value.IndexOf("MyAnonaMouse", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    value.Equals("MAM", StringComparison.OrdinalIgnoreCase) ||
                    value.StartsWith("MAM ", StringComparison.OrdinalIgnoreCase) ||
                    value.IndexOf("(MAM)", StringComparison.OrdinalIgnoreCase) >= 0);
        }
    }
}
