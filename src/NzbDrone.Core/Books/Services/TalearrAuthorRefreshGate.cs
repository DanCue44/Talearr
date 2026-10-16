using System;
using System.Threading;
using NLog;

namespace NzbDrone.Core.Books.Services
{
    /// <summary>
    /// TaleArr: lets the add path's author reconcile (AuthorLibraryService ->
    /// RefreshAuthorService.ReconcileAuthorBlob) share the gate upstream already uses for queued
    /// author refreshes.
    ///
    /// Before this, adding a second media type for an existing author ran the reconcile on the
    /// request thread while the RefreshAuthorCommand queued by the same add (AuthorEditedEvent,
    /// AuthorAddedEvent) ran on a command thread. Both diffed the author against the same database
    /// state, both found the new media type's books missing, and both inserted them: one author add
    /// stored every audiobook twice (2026-10-16, Riley Sager: "The Only One Left" as 7501 and 7516,
    /// the bundles as 7511/7526, ...), and the add itself then failed with "resolves to multiple
    /// local Audiobook books". With the reconcile inside the gate the queued refresh starts after
    /// it and matches the rows it created.
    ///
    /// The wait is bounded. If the gate is not free within Timeout (a very large author refresh, or
    /// a nested call that already holds it), the reconcile goes ahead as before and logs a warning,
    /// so a request can never hang on it.
    /// </summary>
    public static class TalearrAuthorRefreshGate
    {
        public static TimeSpan Timeout { get; set; } = TimeSpan.FromSeconds(90);

        public static IDisposable Enter(SemaphoreSlim gate, Logger logger, string context)
        {
            if (gate == null)
            {
                return new Releaser(null);
            }

            bool acquired;
            try
            {
                acquired = gate.Wait(Timeout);
            }
            catch (ObjectDisposedException)
            {
                acquired = false;
            }

            if (!acquired)
            {
                logger?.Warn("TaleArr: author refresh still running after {0}s; continuing {1} without waiting (duplicate rows are folded by the next refresh)",
                    (int)Timeout.TotalSeconds, context);
                return new Releaser(null);
            }

            return new Releaser(gate);
        }

        private sealed class Releaser : IDisposable
        {
            private SemaphoreSlim _gate;

            public Releaser(SemaphoreSlim gate)
            {
                _gate = gate;
            }

            public void Dispose()
            {
                Interlocked.Exchange(ref _gate, null)?.Release();
            }
        }
    }
}
