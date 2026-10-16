using System;
using System.IO;
using System.Reflection;
using NLog;
using NzbDrone.Common.Instrumentation;

namespace NzbDrone.Common.EnvironmentInfo
{
    public interface IAppFolderInfo
    {
        string AppDataFolder { get; }
        string TempFolder { get; }
        string StartUpFolder { get; }
    }

    public class AppFolderInfo : IAppFolderInfo
    {
        private readonly Environment.SpecialFolder _dataSpecialFolder = Environment.SpecialFolder.CommonApplicationData;

        private static readonly Logger Logger = NzbDroneLogger.GetLogger(typeof(AppFolderInfo));

        public AppFolderInfo(IStartupContext startupContext)
        {
            if (OsInfo.IsNotWindows)
            {
                _dataSpecialFolder = Environment.SpecialFolder.ApplicationData;
            }

            if (startupContext.Args.ContainsKey(StartupContext.APPDATA))
            {
                AppDataFolder = startupContext.Args[StartupContext.APPDATA];
                Logger.Info("Data directory is being overridden to [{0}]", AppDataFolder);
            }
            else
            {
                AppDataFolder = GetDefaultAppDataFolder(Environment.GetFolderPath(_dataSpecialFolder, Environment.SpecialFolderOption.DoNotVerify));
            }

            StartUpFolder = new FileInfo(Assembly.GetExecutingAssembly().Location).Directory.FullName;
            TempFolder = Path.GetTempPath();
        }

        // TaleArr: new installs get a "TaleArr" folder, but an existing "Chaptarr" folder is
        // reused as-is so TaleArr can start on a Chaptarr install unchanged.
        private static string GetDefaultAppDataFolder(string baseFolder)
        {
            var talearrFolder = Path.Combine(baseFolder, BuildInfo.ProductName);
            var chaptarrFolder = Path.Combine(baseFolder, BuildInfo.UpstreamName);

            if (!Directory.Exists(talearrFolder) && Directory.Exists(chaptarrFolder))
            {
                return chaptarrFolder;
            }

            return talearrFolder;
        }

        public string AppDataFolder { get; }

        public string StartUpFolder { get; }

        public string TempFolder { get; }
    }
}
