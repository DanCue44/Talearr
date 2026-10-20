using System;
using System.Collections.Generic;
using System.Threading;

namespace NzbDrone.Core.Talearr.Metadata.Source
{
    // Talearr: counts why metadata profile rules dropped rows, for the shadow report. Off unless a
    // caller opens a scope, so normal refreshes pay nothing. Keys look like "Book: release date is
    // missing" or "Edition: edition language not allowed".
    public static class TalearrFilterTrace
    {
        private static readonly AsyncLocal<Dictionary<string, int>> Current = new AsyncLocal<Dictionary<string, int>>();

        public static IDisposable Begin(Dictionary<string, int> sink)
        {
            var previous = Current.Value;
            Current.Value = sink;
            return new Scope(previous);
        }

        public static void Add(string kind, string reason, int count)
        {
            var sink = Current.Value;
            if (sink == null || count <= 0)
            {
                return;
            }

            var key = $"{kind}: {reason}";
            sink[key] = sink.TryGetValue(key, out var existing) ? existing + count : count;
        }

        private sealed class Scope : IDisposable
        {
            private readonly Dictionary<string, int> _previous;

            public Scope(Dictionary<string, int> previous)
            {
                _previous = previous;
            }

            public void Dispose()
            {
                Current.Value = _previous;
            }
        }
    }
}
