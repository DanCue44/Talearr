using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using NzbDrone.Common.Extensions;

namespace NzbDrone.Common.Disk
{
    // Talearr: the last line of defense for read-only library folders (root folders the user marked
    // "read-only": tracked, never changed). The disk provider and transfer service refuse to create,
    // move, rename, write or delete anything inside them. Services check IsReadOnly first and skip
    // with a clear message; this guard only stops what slips past them.
    public static class ReadOnlyPathGuard
    {
        private static volatile string[] _roots = Array.Empty<string>();

        public static IReadOnlyList<string> Roots => _roots;

        public static void SetRoots(IEnumerable<string> roots)
        {
            _roots = (roots ?? Enumerable.Empty<string>())
                .Where(r => r.IsNotNullOrWhiteSpace())
                .Select(r => r.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }

        public static bool IsReadOnly(string path)
        {
            var roots = _roots;
            if (roots.Length == 0 || path.IsNullOrWhiteSpace())
            {
                return false;
            }

            foreach (var root in roots)
            {
                try
                {
                    if (root.PathEquals(path) || root.IsParentPath(path))
                    {
                        return true;
                    }
                }
                catch (ArgumentException)
                {
                    // Not a usable path: it cannot be inside a library folder.
                }
            }

            return false;
        }

        public static void EnsureWritable(string path, string action)
        {
            if (IsReadOnly(path))
            {
                throw new ReadOnlyPathException(path, action);
            }
        }
    }

    public class ReadOnlyPathException : IOException
    {
        public ReadOnlyPathException(string path, string action)
            : base($"Not allowed to {action} in a read-only library folder: {path}")
        {
            Path = path;
        }

        public string Path { get; }
    }
}
