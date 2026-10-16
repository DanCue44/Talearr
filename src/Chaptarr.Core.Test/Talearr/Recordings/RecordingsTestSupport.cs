using System;
using System.IO;
using Microsoft.Data.Sqlite;
using NLog;
using NUnit.Framework;
using NzbDrone.Common.EnvironmentInfo;
using NzbDrone.Core.Talearr.Recordings;

namespace Chaptarr.Core.Test.Talearr.Recordings
{
    internal sealed class TestAppFolderInfo : IAppFolderInfo
    {
        public TestAppFolderInfo(string folder)
        {
            AppDataFolder = folder;
            TempFolder = folder;
            StartUpFolder = folder;
        }

        public string AppDataFolder { get; }
        public string TempFolder { get; }
        public string StartUpFolder { get; }
    }

    // A throwaway app data folder with its own talearr.db.
    internal sealed class TalearrTestFolder : IDisposable
    {
        public TalearrTestFolder()
        {
            Folder = Path.Combine(TestContext.CurrentContext.WorkDirectory, "talearr-recordings-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Folder);
            AppFolderInfo = new TestAppFolderInfo(Folder);
        }

        public string Folder { get; }
        public IAppFolderInfo AppFolderInfo { get; }
        public string TalearrDbPath => Path.Combine(Folder, TalearrDatabase.FileName);

        public TalearrDatabase CreateDatabase()
        {
            return new TalearrDatabase(AppFolderInfo, LogManager.GetLogger("TalearrTest"));
        }

        public TalearrRecordingsStore CreateStore(ITalearrDatabase database = null)
        {
            return new TalearrRecordingsStore(database ?? CreateDatabase(), LogManager.GetLogger("TalearrTest"));
        }

        public void Dispose()
        {
            SqliteConnection.ClearAllPools();

            try
            {
                Directory.Delete(Folder, true);
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }
}
