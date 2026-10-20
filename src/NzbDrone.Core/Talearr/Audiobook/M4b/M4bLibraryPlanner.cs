using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using NzbDrone.Core.MediaFiles;

namespace NzbDrone.Core.Talearr.Audiobook.M4b
{
    public sealed class M4bLibraryPlan
    {
        public int EditionId { get; set; }

        // Library files of one edition, in playing order.
        public List<BookFile> Files { get; set; } = new List<BookFile>();

        // Folder the M4B is written to: the folder that holds all the files.
        public string Folder { get; set; }

        // Null when the edition can be converted, otherwise why not.
        public string SkipReason { get; set; }

        public bool CanConvert => SkipReason == null;
    }

    // Talearr: decides which audiobook files of a book can be merged into one M4B. One plan per
    // edition, so two recordings of the same book are never merged together.
    public static class M4bLibraryPlanner
    {
        public static readonly IReadOnlySet<string> ConvertibleExtensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            ".mp3", ".m4a", ".m4b"
        };

        public static List<M4bLibraryPlan> Plan(IEnumerable<BookFile> files)
        {
            var audio = (files ?? Enumerable.Empty<BookFile>())
                .Where(f => f != null && !string.IsNullOrWhiteSpace(f.Path) && IsAudio(f))
                .ToList();

            var plans = new List<M4bLibraryPlan>();

            foreach (var edition in audio.GroupBy(f => f.EditionId).OrderBy(g => g.Key))
            {
                var ordered = Order(edition.ToList());
                var plan = new M4bLibraryPlan
                {
                    EditionId = edition.Key,
                    Files = ordered,
                    Folder = AudiobookTrackOrder.GetCommonFolder(ordered.Select(f => f.Path).ToList())
                };

                if (ordered.Count < 2)
                {
                    plan.SkipReason = "Already a single file.";
                }
                else if (ordered.Any(f => !ConvertibleExtensions.Contains(Path.GetExtension(f.Path))))
                {
                    var other = ordered.Select(f => Path.GetExtension(f.Path).ToLowerInvariant())
                        .Where(e => !ConvertibleExtensions.Contains(e))
                        .Distinct();
                    plan.SkipReason = "Only mp3, m4a and m4b files are merged (found " + string.Join(", ", other) + ").";
                }
                else if (string.IsNullOrWhiteSpace(plan.Folder))
                {
                    plan.SkipReason = "The files do not share a folder.";
                }

                plans.Add(plan);
            }

            return plans;
        }

        // Stored part numbers decide when they are complete and unique (they came from the
        // import's track order); otherwise the tags and paths are ordered again.
        private static List<BookFile> Order(List<BookFile> files)
        {
            var parts = files.Select(f => f.Part).ToList();
            if (parts.All(p => p > 0) && parts.Distinct().Count() == parts.Count)
            {
                return files.OrderBy(f => f.Part).ToList();
            }

            return AudiobookTrackOrder.Order(files, f => f.Path, f => f.AllTags);
        }

        private static bool IsAudio(BookFile file)
        {
            var extension = Path.GetExtension(file.Path);
            if (!MediaFileExtensions.AudioExtensions.Contains(extension))
            {
                return false;
            }

            return string.IsNullOrWhiteSpace(file.MediaType) ||
                   string.Equals(file.MediaType, "audiobook", StringComparison.OrdinalIgnoreCase);
        }
    }
}
