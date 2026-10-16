using System;

namespace NzbDrone.Common.Exceptions
{
    public class ChaptarrStartupException : NzbDroneException
    {
        public ChaptarrStartupException(string message, params object[] args)
            : base("TaleArr failed to start: " + string.Format(message, args))
        {
        }

        public ChaptarrStartupException(string message)
            : base("TaleArr failed to start: " + message)
        {
        }

        public ChaptarrStartupException()
            : base("TaleArr failed to start")
        {
        }

        public ChaptarrStartupException(Exception innerException, string message, params object[] args)
            : base("TaleArr failed to start: " + string.Format(message, args), innerException)
        {
        }

        public ChaptarrStartupException(Exception innerException, string message)
            : base("TaleArr failed to start: " + message, innerException)
        {
        }

        public ChaptarrStartupException(Exception innerException)
            : base("TaleArr failed to start: " + innerException.Message)
        {
        }
    }
}
