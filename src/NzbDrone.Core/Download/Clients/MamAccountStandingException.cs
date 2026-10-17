namespace NzbDrone.Core.Download.Clients
{
    // TaleArr: a MAM grab stopped by the seed-only / ratio guard at grab time. Derives from the
    // unsatisfied-slot exception so it is handled the same way (kept as pending, never counted as
    // a download client failure).
    public class MamAccountStandingException : MamUnsatisfiedSlotsUnavailableException
    {
        public MamAccountStandingException(string message)
            : base(message)
        {
        }
    }
}
