using System;
using System.Collections.Generic;
using System.Linq;
using NLog;
using NzbDrone.Core.Books;

namespace NzbDrone.Core.Talearr.Recordings
{
    // Talearr: when two audiobook editions are certainly the same recording. Stricter than the
    // candidate folding in TalearrRecordingsService (which also joins on narrators and runtime),
    // because the callers act on it without asking: the import keeps files on the book's pinned
    // edition, and the duplicate merge folds two rows into one.
    //
    // Same recording means one of:
    //   - the same edition (id) or the same provider identity (TalearrEditionKey, shared by a base
    //     edition and every upstream copy of it),
    //   - the same stored Audible ASIN,
    //   - both editions linked in talearr.db to the same Audible product in the same marketplace
    //     (a catalog search link only counts with a firm confidence).
    public static class TalearrSameRecording
    {
        public const double MinSearchConfidence = 0.75;

        public static bool Same(Edition a, Edition b, IReadOnlyDictionary<int, EditionAudibleFacts> facts = null)
        {
            if (a == null || b == null)
            {
                return false;
            }

            if (a.Id > 0 && a.Id == b.Id)
            {
                return true;
            }

            if (TalearrEditionKey.Same(a, b) || SameAsin(a.AudibleASIN, b.AudibleASIN))
            {
                return true;
            }

            var left = FirmLink(a, facts);
            var right = FirmLink(b, facts);

            return left != null && right != null &&
                   string.Equals(left.Marketplace, right.Marketplace, StringComparison.OrdinalIgnoreCase) &&
                   SameAsin(left.Asin, right.Asin);
        }

        // The protected (pinned) edition of an audiobook row when the edition an import matched is
        // the same recording: the import then belongs on the pinned edition, not in a copy row.
        // Null when nothing is pinned, the pin already is the matched edition, the recordings
        // differ, or more than one edition is protected.
        public static Edition ProtectedTwin(Book book, IReadOnlyList<Edition> editions, Edition matched, IReadOnlyDictionary<int, EditionAudibleFacts> facts)
        {
            if (book == null || book.MediaType != BookMediaType.Audiobook || matched == null || matched.Id <= 0)
            {
                return null;
            }

            var conflict = EditionPinPolicy.FindConflictingProtectedEdition(book, editions, matched.Id);
            if (conflict == null || !Same(conflict, matched, facts))
            {
                return null;
            }

            return EditionPinPolicy.FindConflictingProtectedEdition(book, editions, conflict.Id) == null ? conflict : null;
        }

        // ProtectedTwin with the Audible links read from talearr.db only when a pin is in the way.
        // Never throws: any failure keeps upstream's behaviour (a copy row).
        public static Edition ProtectedTwin(Book book, IReadOnlyList<Edition> editions, Edition matched, ITalearrAudibleFactsService factsService, Logger logger)
        {
            try
            {
                if (book == null || book.MediaType != BookMediaType.Audiobook || matched == null ||
                    EditionPinPolicy.FindConflictingProtectedEdition(book, editions, matched.Id) == null)
                {
                    return null;
                }

                var all = (editions ?? new List<Edition>()).Append(matched).Where(e => e != null && e.Id > 0).ToList();
                var facts = factsService?.GetCachedFacts(all);
                return ProtectedTwin(book, editions, matched, facts);
            }
            catch (Exception ex)
            {
                logger?.Debug(ex, "Talearr: could not compare the matched edition with the pinned edition of '{0}'", book?.Title);
                return null;
            }
        }

        private static EditionAudibleLink FirmLink(Edition edition, IReadOnlyDictionary<int, EditionAudibleFacts> facts)
        {
            if (facts == null || edition.Id <= 0 || !facts.TryGetValue(edition.Id, out var found) || found?.Link?.Matched != true)
            {
                return null;
            }

            var link = found.Link;
            return link.Method == EditionAudibleLink.MethodSearch && link.Confidence < MinSearchConfidence ? null : link;
        }

        private static bool SameAsin(string a, string b)
        {
            return !string.IsNullOrWhiteSpace(a) && !string.IsNullOrWhiteSpace(b) &&
                   string.Equals(a.Trim(), b.Trim(), StringComparison.OrdinalIgnoreCase);
        }
    }
}
