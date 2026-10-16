using System;
using System.IO;
using System.Linq;
using System.Reflection;

namespace NzbDrone.Common.EnvironmentInfo
{
    public static class BuildInfo
    {
        static BuildInfo()
        {
            var assembly = Assembly.GetExecutingAssembly();

            Version = assembly.GetName().Version;

            var attributes = assembly.GetCustomAttributes(true);

            Branch = "unknown";

            var config = attributes.OfType<AssemblyConfigurationAttribute>().FirstOrDefault();
            if (config != null)
            {
                Branch = config.Configuration;
            }

            Release = $"{Version}-{Branch}-chaptarr";

            // TaleArr: the fork's own version, stamped from src/Talearr.props. Version above
            // stays the upstream Chaptarr version so update checks, backups, metadata requests
            // and anything else keyed on it behave exactly like upstream.
            TalearrVersion = attributes.OfType<AssemblyMetadataAttribute>()
                .FirstOrDefault(a => a.Key == "TalearrVersion")?.Value ?? Version?.ToString(3);
        }

        // Technical identity (user agents, rTorrent views, plugin names, API appName).
        // Kept as upstream so TaleArr stays a drop-in replacement for Chaptarr.
        public static string AppName { get; } = "Chaptarr";

        // TaleArr: user-visible product name.
        public static string ProductName { get; } = "TaleArr";

        public static string UpstreamName { get; } = "Chaptarr";

        public static string TalearrVersion { get; }

        public static Version Version { get; }
        public static string Branch { get; }
        public static string Release { get; }

        public static DateTime BuildDateTime
        {
            get
            {
                var fileLocation = Assembly.GetCallingAssembly().Location;
                return new FileInfo(fileLocation).LastWriteTimeUtc;
            }
        }

        public static bool IsDebug
        {
            get
            {
#if DEBUG
                return true;
#else
                return false;
#endif
            }
        }
    }
}
