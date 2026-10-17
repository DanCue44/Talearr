using System;
using NzbDrone.Core.Indexers.MyAnonaMouse;

namespace NzbDrone.Core.Talearr.Seeding
{
    // TaleArr: MAM promotes a member to Power User automatically (within 15 minutes) once they
    // have been a member for 4 weeks, uploaded over 25 GiB (bought upload credit counts) and hold
    // an overall ratio of 2.0; dropping below 2.0 demotes again, and buying VIP with points needs
    // Power User first. Below 1.0 MAM demotes to Mouse (seed only). Shown on Activity > Seeding.
    public static class TalearrMamPowerUserProgress
    {
        public const int MembershipDays = 28;
        public const long UploadTargetBytes = 25L * 1024 * 1024 * 1024;
        public const double RatioTarget = 2.0;

        public static void Apply(TalearrSeedingMamStatus status, MyAnonaMouseSettings settings, DateTime now)
        {
            if (status == null || settings == null)
            {
                return;
            }

            status.MemberSince = settings.MemberSinceUtc.HasValue ? DateTime.SpecifyKind(settings.MemberSinceUtc.Value, DateTimeKind.Utc) : null;
            status.UploadedBytes = settings.UploadedBytes;
            status.DownloadedBytes = settings.DownloadedBytes;
            status.DaysUntilEligible = DaysUntilEligible(settings.MemberSinceUtc, now);
            status.UploadedRemainingBytes = settings.UploadedBytes.HasValue ? Math.Max(0, UploadTargetBytes - settings.UploadedBytes.Value) : null;
            status.RatioOk = RatioOk(settings);
        }

        public static int? DaysUntilEligible(DateTime? memberSince, DateTime now)
        {
            if (!memberSince.HasValue || memberSince.Value > now.AddDays(1))
            {
                return null;
            }

            var remaining = memberSince.Value.AddDays(MembershipDays) - now;
            return remaining <= TimeSpan.Zero ? 0 : (int)Math.Ceiling(remaining.TotalDays);
        }

        public static bool? RatioOk(MyAnonaMouseSettings settings)
        {
            if (settings.Ratio.HasValue)
            {
                return settings.Ratio.Value >= RatioTarget;
            }

            // Nothing downloaded yet: MAM shows an infinite ratio.
            if (settings.UploadedBytes.HasValue && settings.DownloadedBytes == 0)
            {
                return true;
            }

            return null;
        }
    }
}
