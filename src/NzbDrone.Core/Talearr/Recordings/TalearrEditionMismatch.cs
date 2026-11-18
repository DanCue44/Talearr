using System;
using System.Collections.Generic;
using System.Linq;
using NzbDrone.Core.Books;
using NzbDrone.Core.Talearr.GrabPolicy;

namespace NzbDrone.Core.Talearr.Recordings
{
    // Talearr: the rejection when a completed download matched another edition than the one it
    // was grabbed for. Editions of one book usually share a title, so each is named by what tells
    // them apart: the reading (abridged or unabridged), narrator, publisher, year and ISBN or ASIN,
    // plus how long the downloaded files run.
    //
    //   "Grabbed for the abridged recording (Random House Audio, 1993, ISBN 9780553712704), but
    //    these files run 14 h 20 min and match the unabridged recording narrated by John MacDonald
    //    (Random House Audio, 2024, ASIN B0DP3KS9Y4)."
    public static class TalearrEditionMismatch
    {
        public static string Message(Edition expected,
                                     Edition matched,
                                     IReadOnlyCollection<Edition> siblings,
                                     long? filesSeconds,
                                     ITalearrAbridgedResolver resolver,
                                     IReadOnlyDictionary<int, EditionAudibleFacts> facts)
        {
            var all = (siblings ?? Array.Empty<Edition>()).Where(e => e != null).ToList();
            var expectedText = Describe(expected, all, resolver, facts);
            var matchedText = Describe(matched, all, resolver, facts);

            // Nothing tells them apart: fall back to the edition ids.
            if (string.Equals(expectedText, matchedText, StringComparison.Ordinal))
            {
                expectedText = $"{expectedText} (edition {expected?.Id})";
                matchedText = $"{matchedText} (edition {matched?.Id})";
            }

            var files = filesSeconds.HasValue && filesSeconds.Value >= 60
                ? $"these files run {TalearrAbridgedRules.FormatRuntime((int)(filesSeconds.Value / 60))} and match"
                : "these files match";

            return $"Grabbed for {expectedText}, but {files} {matchedText}.";
        }

        public static string Describe(Edition edition,
                                      IReadOnlyCollection<Edition> siblings,
                                      ITalearrAbridgedResolver resolver,
                                      IReadOnlyDictionary<int, EditionAudibleFacts> facts)
        {
            if (edition == null)
            {
                return "an unknown edition";
            }

            var product = facts != null && edition.Id > 0 && facts.TryGetValue(edition.Id, out var f) && f.HasProduct ? f.Product : null;

            var reading = Reading(edition, siblings, resolver);
            var text = reading == AbridgedMarker.Abridged
                ? "the abridged recording"
                : reading == AbridgedMarker.Unabridged
                    ? "the unabridged recording"
                    : $"'{edition.Title}'";

            var narrators = (edition.NarratorNames ?? new List<string>())
                .Concat(product?.Narrators ?? new List<string>())
                .Where(n => !string.IsNullOrWhiteSpace(n))
                .Select(n => n.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Take(2)
                .ToList();
            if (narrators.Count > 0)
            {
                text += $" narrated by {string.Join(" and ", narrators)}";
            }

            var details = new List<string>();
            var publisher = !string.IsNullOrWhiteSpace(edition.Publisher) ? edition.Publisher.Trim() : product?.Publisher?.Trim();
            if (!string.IsNullOrWhiteSpace(publisher))
            {
                details.Add(publisher);
            }

            if (edition.ReleaseDate.HasValue && edition.ReleaseDate.Value.Year > 1)
            {
                details.Add(edition.ReleaseDate.Value.Year.ToString());
            }

            var asin = FirstOf(product?.Asin, edition.AudibleASIN);
            var isbn = FirstOf(edition.Isbn13, edition.Isbn10);
            if (!string.IsNullOrWhiteSpace(isbn))
            {
                details.Add($"ISBN {isbn}");
            }
            else if (!string.IsNullOrWhiteSpace(asin))
            {
                details.Add($"ASIN {asin}");
            }

            return details.Count > 0 ? $"{text} ({string.Join(", ", details)})" : text;
        }

        private static AbridgedMarker Reading(Edition edition, IReadOnlyCollection<Edition> siblings, ITalearrAbridgedResolver resolver)
        {
            if (resolver != null)
            {
                try
                {
                    return resolver.ResolveEdition(edition, siblings)?.Marker ?? AbridgedMarker.Unknown;
                }
                catch (Exception)
                {
                    // Fall back to the edition's own text.
                }
            }

            return TalearrAbridgedResolver.ExplicitText(edition);
        }

        private static string FirstOf(params string[] values)
        {
            return values.FirstOrDefault(v => !string.IsNullOrWhiteSpace(v))?.Trim();
        }
    }
}
