using System;
using System.Threading;
using NzbDrone.Common.Disk;

namespace NzbDrone.Core.Talearr.Import
{
    // How a copy-mode import puts a file into the library. Default follows the media management
    // setting "Use Hardlinks instead of Copy"; the other two are an explicit choice made for one
    // manual import.
    public enum TalearrTransferChoice
    {
        Default = 0,
        Hardlink = 1,
        Copy = 2
    }

    // Talearr: carries the manual import's transfer choice (hardlink or plain copy) down to
    // BookFileMovingService without threading a new parameter through the whole import pipeline.
    // Only copy-mode transfers read it; moves are unaffected. The value is per async flow, so a
    // concurrent automatic import never sees another command's choice.
    public static class TalearrTransferScope
    {
        private static readonly AsyncLocal<TalearrTransferChoice> CurrentChoice = new AsyncLocal<TalearrTransferChoice>();

        public static TalearrTransferChoice Current => CurrentChoice.Value;

        public static IDisposable Begin(TalearrTransferChoice choice)
        {
            var previous = CurrentChoice.Value;
            CurrentChoice.Value = choice;
            return new Restore(previous);
        }

        // Accepts the API strings "hardlink" and "copy" (any case); anything else is Default.
        public static TalearrTransferChoice Parse(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return TalearrTransferChoice.Default;
            }

            switch (value.Trim().ToLowerInvariant())
            {
                case "hardlink":
                    return TalearrTransferChoice.Hardlink;
                case "copy":
                    return TalearrTransferChoice.Copy;
                default:
                    return TalearrTransferChoice.Default;
            }
        }

        public static bool PreferHardlinks(bool copyUsingHardlinks)
        {
            return PreferHardlinks(Current, copyUsingHardlinks);
        }

        public static bool PreferHardlinks(TalearrTransferChoice choice, bool copyUsingHardlinks)
        {
            switch (choice)
            {
                case TalearrTransferChoice.Hardlink:
                    return true;
                case TalearrTransferChoice.Copy:
                    return false;
                default:
                    return copyUsingHardlinks;
            }
        }

        public static TransferMode CopyMode(bool copyUsingHardlinks)
        {
            return PreferHardlinks(copyUsingHardlinks) ? TransferMode.HardLinkOrCopy : TransferMode.Copy;
        }

        private sealed class Restore : IDisposable
        {
            private readonly TalearrTransferChoice _previous;
            private bool _disposed;

            public Restore(TalearrTransferChoice previous)
            {
                _previous = previous;
            }

            public void Dispose()
            {
                if (_disposed)
                {
                    return;
                }

                _disposed = true;
                CurrentChoice.Value = _previous;
            }
        }
    }
}
