using System;
using System.Collections.Generic;
using System.Linq;
using NLog;
using NzbDrone.Common.Extensions;
using NzbDrone.Core.Books.Calibre;
using NzbDrone.Core.Books.Services;
using NzbDrone.Core.Configuration;

namespace NzbDrone.Core.Books
{
    public interface IEditionSelector
    {
        Edition SelectBestEdition(IEnumerable<Edition> editions, BookMediaType mediaType);
        EditionRetentionSelection SelectRetainedEditions(
            BookMediaType instanceMediaType,
            IReadOnlyList<Edition> editions);
        bool EnsureSingleMonitoredEdition(List<Edition> editions, IReadOnlyDictionary<int, int> fileCountsByEditionId = null, BookMediaType? mediaType = null);

        // TaleArr: clears an automatically chosen edition in a non-preferred language so the
        // single-monitored repair can pick the preferred-language edition instead.
        bool ReleaseForeignAutoSelectedEdition(List<Edition> editions, IReadOnlyDictionary<int, int> fileCountsByEditionId, BookMediaType? mediaType);
    }

    public sealed record EditionRetentionSelection(
        IReadOnlyList<Edition> RetainedEditions,
        IReadOnlyList<string> Warnings);

    public class EditionSelector : IEditionSelector
    {
        private readonly Logger _logger;
        private readonly IConfigService _configService;

        public EditionSelector(Logger logger)
        {
            _logger = logger;
        }

        // TaleArr: the UI language (existing Config table key UILanguage) is the preferred edition language.
        public EditionSelector(Logger logger, IConfigService configService)
            : this(logger)
        {
            _configService = configService;
        }

        internal string PreferredLanguage
        {
            get
            {
                int? uiLanguage = null;
                try
                {
                    uiLanguage = _configService?.UILanguage;
                }
                catch (Exception ex)
                {
                    _logger?.Debug(ex, "Unable to read UI language; preferring English editions");
                }

                return TalearrEditionPreference.ResolvePreferredLanguage(uiLanguage);
            }
        }

        internal static Edition SelectByNativeFormatThenRatings(IEnumerable<Edition> editions, BookMediaType mediaType)
        {
            return SelectByNativeFormatThenRatings(editions, mediaType, TalearrEditionPreference.DefaultLanguage);
        }

        internal static Edition SelectByNativeFormatThenRatings(IEnumerable<Edition> editions, BookMediaType mediaType, string preferredLanguage)
        {
            if (editions == null)
            {
                return null;
            }

            var candidates = editions
                .Where(e => e != null)
                .ToList();

            if (candidates.Count == 0)
            {
                return null;
            }

            var nativeFormat = mediaType == BookMediaType.Audiobook ? 2 : 3;
            var nativeFormatCandidates = candidates
                .Where(e => e.ReadingFormatId == nativeFormat)
                .ToList();

            var referenceTitles = TalearrEditionPreference.BuildReferenceTitles(candidates, preferredLanguage);

            if (nativeFormatCandidates.Count == 0)
            {
                return SelectRepresentativeFallback(candidates, mediaType, preferredLanguage, referenceTitles);
            }

            // TaleArr: native format still wins, ranked by language before popularity. Only when every
            // native edition is labelled with a foreign language does a preferred-language fallback
            // (ebook/print) win, so an English request is not tied to a German audiobook just because
            // no English audio is listed yet. Unlabelled native editions keep the native path.
            if (nativeFormatCandidates.All(e => TalearrEditionPreference.IsKnownForeignLanguage(e, preferredLanguage)))
            {
                var preferredFallback = SelectRepresentativeFallback(
                    candidates.Where(e => TalearrEditionPreference.GetLanguageRank(e, preferredLanguage, referenceTitles) <= TalearrEditionPreference.PreferredRankLimit),
                    mediaType,
                    preferredLanguage,
                    referenceTitles);
                if (preferredFallback != null)
                {
                    return preferredFallback;
                }
            }

            return SelectMostRated(nativeFormatCandidates, preferredLanguage, referenceTitles);
        }

        public Edition SelectBestEdition(IEnumerable<Edition> editions, BookMediaType mediaType)
        {
            if (editions == null || !editions.Any())
            {
                return null;
            }

            var allCandidates = editions.Where(e => e != null).ToList();
            var nativeFormat = mediaType == BookMediaType.Audiobook ? 2 : 3;
            var nativeCandidates = allCandidates.Where(e => e.ReadingFormatId == nativeFormat).ToList();
            var fallbackCandidates = allCandidates.Where(e => IsRepresentativeFallback(e, mediaType)).ToList();

            var candidates = nativeCandidates.Any()
                ? nativeCandidates
                : fallbackCandidates;

            if (!candidates.Any())
            {
                _logger.Debug("No native-format or representative fallback editions available for media type {0}", mediaType);
                return null;
            }

            // 1. Manual pin wins within the native media type or representative fallback set.
            var manual = candidates.Where(e => e.ManualAdd).ToList();
            if (manual.Any())
            {
                var selected = manual.Where(e => e.Monitored).OrderBy(e => e.Id).FirstOrDefault()
                               ?? manual.OrderBy(e => e.Id).First();
                _logger.Debug("Using manually selected edition: {0} (ID: {1})", selected.Title, selected.Id);
                return selected;
            }

            // Allowed-language filtering happens upstream in EditionMetadataProfileFilter.Apply().
            // Prefer native format (audio=2, ebook=3), then pick most rated with deterministic ties.
            // TaleArr: fallbacks stay in the pool so a preferred-language ebook can win when every
            // native edition is labelled foreign (see SelectByNativeFormatThenRatings).
            var selected2 = SelectByNativeFormatThenRatings(nativeCandidates.Concat(fallbackCandidates).ToList(), mediaType, PreferredLanguage);
            if (selected2 == null)
            {
                return null;
            }

            _logger.Debug("Selected edition '{0}' (ID: {1}, Format: {2}, Language: {3}, Votes: {4}, Rating: {5})",
                selected2.Title, selected2.Id, selected2.ReadingFormatId, selected2.Language,
                selected2.Ratings?.Votes ?? 0, selected2.Ratings?.Value ?? 0m);

            return selected2;
        }

        /// <summary>
        /// Returns the retained edition set for refresh/add.
        /// Shapes an already-filtered edition set into the retained edition set for add/refresh.
        /// This method does not apply metadata-profile filtering; callers must do that upstream.
        /// </summary>
        public EditionRetentionSelection SelectRetainedEditions(
            BookMediaType instanceMediaType,
            IReadOnlyList<Edition> remoteEditions)
        {
            if (remoteEditions == null || remoteEditions.Count == 0)
            {
                return new EditionRetentionSelection(new List<Edition>(), new List<string>());
            }

            var candidates = remoteEditions
                .Where(e => e != null)
                .ToList();

            var retained = new List<Edition>();
            var seenKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var nativeFormat = instanceMediaType == BookMediaType.Audiobook ? 2 : 3;

            foreach (var languageGroup in candidates.GroupBy(e => EditionMetadataProfileFilter.NormalizeLanguageBucket(e.Language) ?? "null"))
            {
                var groupEditions = languageGroup.ToList();

                foreach (var edition in groupEditions.Where(e => e.ReadingFormatId == nativeFormat))
                {
                    AddRetainedEdition(retained, seenKeys, edition);
                }

                // Audiobook book rows always retain a non-audio representative per language
                // (best RF=3 ebook, else RF=1 print) so users have an ebook safety net for
                // self-narrated and unprovable-narrator titles even when audio editions exist.
                // Ebook book rows skip the print companion when ebooks are present.
                var hasNative = groupEditions.Any(e => e.ReadingFormatId == nativeFormat);
                if (instanceMediaType == BookMediaType.Audiobook || !hasNative)
                {
                    AddRetainedEdition(retained, seenKeys, SelectRepresentativeFallback(groupEditions, instanceMediaType));
                }
            }

            return new EditionRetentionSelection(retained, new List<string>());
        }

        /// <summary>
        /// Write-side repair selector used only to restore the single monitored-edition invariant.
        /// Read/display paths must use the persisted monitored edition directly.
        /// </summary>
        private static Edition SelectRepairEdition(IEnumerable<Edition> editions, IReadOnlyDictionary<int, int> fileCountsByEditionId = null, BookMediaType? mediaType = null, string preferredLanguage = TalearrEditionPreference.DefaultLanguage)
        {
            if (editions == null)
            {
                return null;
            }

            var list = editions.Where(e => e != null).ToList();
            if (list.Count == 0)
            {
                return null;
            }

            var manual = list.Where(e => e.ManualAdd).ToList();
            if (manual.Any())
            {
                return manual.Where(e => e.Monitored).OrderBy(e => e.Id).FirstOrDefault()
                       ?? manual.OrderBy(e => e.Id).First();
            }

            if (fileCountsByEditionId != null)
            {
                var withFiles = list
                    .Where(e => fileCountsByEditionId.TryGetValue(e.Id, out var c) && c > 0)
                    .ToList();

                if (withFiles.Any())
                {
                    return withFiles.Where(e => e.Monitored).OrderBy(e => e.Id).FirstOrDefault()
                           ?? withFiles.OrderByDescending(e => fileCountsByEditionId.GetValueOrDefault(e.Id, 0))
                                       .ThenBy(e => e.Id).First();
                }
            }

            var monitoredEditions = list.Where(e => e.Monitored).OrderBy(e => e.Id).ToList();

            // TaleArr: several monitored editions is a corrupt state (older builds monitored every newly
            // inserted edition), so the flags carry no user intent. Choose on merit from all editions
            // instead of the lowest monitored row id, which was an arbitrary, often foreign, edition.
            if (monitoredEditions.Count > 1 && mediaType.HasValue)
            {
                var best = SelectByNativeFormatThenRatings(list, mediaType.Value, preferredLanguage);
                if (best != null)
                {
                    return best;
                }
            }

            var monitored = monitoredEditions.FirstOrDefault();
            if (monitored != null)
            {
                return monitored;
            }

            return list.OrderBy(e => e.Id).First();
        }

        public bool EnsureSingleMonitoredEdition(List<Edition> editions, IReadOnlyDictionary<int, int> fileCountsByEditionId = null, BookMediaType? mediaType = null)
        {
            if (editions == null)
            {
                return false;
            }

            var list = editions.Where(e => e != null).ToList();
            if (list.Count == 0)
            {
                return false;
            }

            var hasManual = list.Any(e => e.ManualAdd);
            var hasFiles = fileCountsByEditionId != null && list.Any(e => fileCountsByEditionId.TryGetValue(e.Id, out var c) && c > 0);
            var monitoredEditions = list.Where(e => e.Monitored).OrderBy(e => e.Id).ToList();

            Edition selected;
            if (monitoredEditions.Count == 1)
            {
                selected = monitoredEditions[0];
            }
            else if (monitoredEditions.Count == 0 && !hasManual && !hasFiles && mediaType.HasValue)
            {
                selected = SelectByNativeFormatThenRatings(list, mediaType.Value, PreferredLanguage);
            }
            else
            {
                selected = SelectRepairEdition(list, fileCountsByEditionId, mediaType, PreferredLanguage);
            }

            if (selected == null)
            {
                return false;
            }

            var changed = false;
            foreach (var edition in list)
            {
                // TaleArr: compare by reference for unsaved editions. Every new edition has Id 0, so an
                // Id comparison monitored all of them at once and left books with many monitored editions.
                var shouldMonitor = ReferenceEquals(edition, selected) || (selected.Id > 0 && edition.Id == selected.Id);
                if (edition.Monitored != shouldMonitor)
                {
                    edition.Monitored = shouldMonitor;
                    changed = true;
                }
            }

            if (changed)
            {
                _logger.Info("[EditionRepair] BookId={0} chosenEditionId={1}", selected.BookId, selected.Id);
            }

            return changed;
        }

        /// <summary>
        /// TaleArr: when the only monitored edition was chosen automatically (no ManualAdd, no files on
        /// any edition) and is in a non-preferred language while a native-format edition in the
        /// preferred language exists, clear the monitored flags so EnsureSingleMonitoredEdition picks
        /// again. Explicit user selections set AnyEditionOk=false or ManualAdd and are never touched;
        /// callers only invoke this for books with AnyEditionOk.
        /// </summary>
        public bool ReleaseForeignAutoSelectedEdition(List<Edition> editions, IReadOnlyDictionary<int, int> fileCountsByEditionId, BookMediaType? mediaType)
        {
            if (editions == null || !mediaType.HasValue)
            {
                return false;
            }

            var list = editions.Where(e => e != null).ToList();
            var monitored = list.Where(e => e.Monitored).ToList();
            if (monitored.Count != 1 || list.Any(e => e.ManualAdd))
            {
                return false;
            }

            if (fileCountsByEditionId != null && list.Any(e => fileCountsByEditionId.TryGetValue(e.Id, out var count) && count > 0))
            {
                return false;
            }

            var preferredLanguage = PreferredLanguage;
            var current = monitored[0];
            if (TalearrEditionPreference.IsPreferredLanguage(current, preferredLanguage))
            {
                return false;
            }

            var best = SelectByNativeFormatThenRatings(list, mediaType.Value, preferredLanguage);
            if (best == null || ReferenceEquals(best, current) || !TalearrEditionPreference.IsPreferredLanguage(best, preferredLanguage))
            {
                return false;
            }

            var nativeFormat = mediaType.Value == BookMediaType.Audiobook ? 2 : 3;
            if (current.ReadingFormatId == nativeFormat && best.ReadingFormatId != nativeFormat)
            {
                // Never trade a native-format edition for a fallback format just for language.
                return false;
            }

            _logger.Info("[EditionLanguage] BookId={0}: auto-selected edition '{1}' (ID: {2}, language {3}) replaced by preferred-language edition '{4}' (ID: {5}, language {6})",
                current.BookId, current.Title, current.Id, current.Language ?? "unknown", best.Title, best.Id, best.Language);

            foreach (var edition in list)
            {
                edition.Monitored = false;
            }

            return true;
        }

        internal static string GetRetentionDedupeKey(Edition edition)
        {
            if (edition?.ForeignEditionId.IsNotNullOrWhiteSpace() == true)
            {
                return $"foreign:{edition.ForeignEditionId.Trim().ToLowerInvariant()}";
            }

            if (edition?.HardcoverEditionId.IsNotNullOrWhiteSpace() == true)
            {
                return $"hardcover:{edition.HardcoverEditionId.Trim().ToLowerInvariant()}";
            }

            if (edition?.GoodreadsEditionId > 0)
            {
                return $"goodreads:{edition.GoodreadsEditionId}";
            }

            if (edition?.OpenLibraryEditionId.IsNotNullOrWhiteSpace() == true)
            {
                return $"openlibrary:{edition.OpenLibraryEditionId.Trim().ToLowerInvariant()}";
            }

            if (edition?.GoogleBooksEditionId.IsNotNullOrWhiteSpace() == true)
            {
                return $"google:{edition.GoogleBooksEditionId.Trim().ToLowerInvariant()}";
            }

            if (edition?.Asin.IsNotNullOrWhiteSpace() == true)
            {
                return $"asin:{edition.Asin.Trim().ToLowerInvariant()}";
            }

            if (edition?.AudibleASIN.IsNotNullOrWhiteSpace() == true)
            {
                return $"audible:{edition.AudibleASIN.Trim().ToLowerInvariant()}";
            }

            var normalizedTitle = edition?.Title?.Trim().ToLowerInvariant() ?? string.Empty;
            var normalizedLanguage = edition?.Language?.CanonicalizeLanguage()?.Trim().ToLowerInvariant() ?? "null";
            var readingFormat = edition?.ReadingFormatId ?? 0;
            var releaseDate = edition?.ReleaseDate?.ToString("yyyyMMdd") ?? "null";

            return $"fallback:{normalizedTitle}|{normalizedLanguage}|{readingFormat}|{releaseDate}";
        }

        private static void AddRetainedEdition(List<Edition> retained, HashSet<string> seenKeys, Edition edition)
        {
            if (edition == null)
            {
                return;
            }

            var key = GetRetentionDedupeKey(edition);
            if (seenKeys.Add(key))
            {
                retained.Add(edition);
            }
        }

        private static bool IsRepresentativeFallback(Edition edition, BookMediaType mediaType)
        {
            return GetRepresentativeFallbackRank(edition, mediaType) > 0;
        }

        private static int GetRepresentativeFallbackRank(Edition edition, BookMediaType mediaType)
        {
            if (edition == null)
            {
                return 0;
            }

            if (mediaType == BookMediaType.Audiobook)
            {
                return edition.ReadingFormatId switch
                {
                    3 => 2,
                    1 => 1,
                    _ => 0
                };
            }

            return edition.ReadingFormatId == 1 ? 1 : 0;
        }

        private static Edition SelectRepresentativeFallback(IEnumerable<Edition> editions, BookMediaType mediaType)
        {
            var list = (editions ?? Enumerable.Empty<Edition>()).Where(e => e != null).ToList();
            return SelectRepresentativeFallback(list, mediaType, TalearrEditionPreference.DefaultLanguage, TalearrEditionPreference.BuildReferenceTitles(list, TalearrEditionPreference.DefaultLanguage));
        }

        private static Edition SelectRepresentativeFallback(IEnumerable<Edition> editions, BookMediaType mediaType, string preferredLanguage, IReadOnlySet<string> referenceTitles)
        {
            return (editions ?? Enumerable.Empty<Edition>())
                .Where(e => IsRepresentativeFallback(e, mediaType))
                .OrderByDescending(e => GetRepresentativeFallbackRank(e, mediaType))
                .ThenBy(e => TalearrEditionPreference.GetLanguageRank(e, preferredLanguage, referenceTitles))
                .ThenByDescending(e => e.Ratings?.Votes ?? 0)
                .ThenByDescending(e => e.Ratings?.Value ?? 0m)
                .ThenBy(e => e.Id)
                .FirstOrDefault();
        }

        // TaleArr: language rank (see TalearrEditionPreference) before popularity.
        private static Edition SelectMostRated(IEnumerable<Edition> editions, string preferredLanguage, IReadOnlySet<string> referenceTitles)
        {
            return (editions ?? Enumerable.Empty<Edition>())
                .Where(e => e != null)
                .OrderBy(e => TalearrEditionPreference.GetLanguageRank(e, preferredLanguage, referenceTitles))
                .ThenByDescending(e => e.Ratings?.Votes ?? 0)
                .ThenByDescending(e => e.Ratings?.Value ?? 0m)
                .ThenBy(e => e.Id)
                .FirstOrDefault();
        }

    }
}
