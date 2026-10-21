using System;
using System.IO;
using NzbDrone.Core.Localization;
using NzbDrone.Core.Talearr.Comics;

namespace NzbDrone.Core.HealthCheck.Checks
{
    // Talearr: System > Status error when the comics library (talearr-comics.db) cannot be opened.
    // Books are not affected. Quiet until someone has used comics (no file yet).
    public class TalearrComicsDatabaseCheck : HealthCheckBase
    {
        private readonly ITalearrComicsDatabase _database;

        public TalearrComicsDatabaseCheck(ITalearrComicsDatabase database, ILocalizationService localizationService)
            : base(localizationService)
        {
            _database = database;
        }

        public override HealthCheck Check()
        {
            if (!File.Exists(_database.DatabasePath))
            {
                return new HealthCheck(GetType());
            }

            try
            {
                _database.OpenConnection().Dispose();
                return new HealthCheck(GetType());
            }
            catch (Exception)
            {
                return new HealthCheck(GetType(), HealthCheckResult.Error,
                    $"Comics are off: {_database.UnavailableReason ?? "the comics library cannot be opened"} Books are not affected.");
            }
        }
    }
}
