using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using NLog;
using NzbDrone.Core.Books;
using NzbDrone.Core.MediaFiles;
using NzbDrone.Core.Talearr.GrabPolicy;

namespace NzbDrone.Core.Talearr.Recordings
{
    public static class AbridgedSources
    {
        public const string Audible = "audible";
        public const string Metadata = "metadata";
        public const string Runtime = "runtime";
        public const string Files = "files";
        public const string None = "none";
    }

    public sealed class AbridgedResolution
    {
        public static readonly AbridgedResolution Unknown = new AbridgedResolution(AbridgedMarker.Unknown, AbridgedSources.None, "no Audible match, abridged marker or comparable runtime");

        public AbridgedResolution(AbridgedMarker marker, string source, string reason)
        {
            Marker = marker;
            Source = source;
            Reason = reason;
        }

        public AbridgedMarker Marker { get; }

        // audible | metadata | runtime | files | none
        public string Source { get; }

        // Short human readable explanation, for tooltips and logs.
        public string Reason { get; }

        public bool IsAbridged => Marker == AbridgedMarker.Abridged;
    }

    public interface ITalearrAbridgedResolver
    {
        // The wanted recording of an audiobook row: its monitored edition, else the row's files.
        AbridgedResolution Resolve(Book book);

        // One edition, judged against its siblings (all editions of the same row).
        AbridgedResolution ResolveEdition(Edition edition, IReadOnlyCollection<Edition> siblings, IReadOnlyCollection<BookFile> files = null);
    }

    // Talearr: decides whether a recording is abridged. First answer wins:
    //   1. Audible format_type of the edition's resolved product (talearr.db, no network),
    //   2. explicit text on the edition (title, subtitle, edition info and format),
    //   3. runtime under 60% of the longest same-language edition, using Audible runtimes where
    //      known (an edition at least 90% as long as a known unabridged one is unabridged),
    //   4. for a row with files, the files' duration against the unabridged runtime,
    //   5. otherwise unknown.
    // Feeds AbridgedReleaseDetector, so the grab policy's "the wanted edition is itself abridged"
    // rule works for copy rows and for editions the metadata server says nothing about.
    public class TalearrAbridgedResolver : ITalearrAbridgedResolver
    {
        public const double UnabridgedRuntimeRatio = 0.9;
        private const int MinimumComparableSeconds = 30 * 60;
        private static readonly TimeSpan MemoLifetime = TimeSpan.FromMinutes(2);

        private readonly ITalearrAudibleFactsService _factsService;
        private readonly IMediaFileService _mediaFileService;
        private readonly Logger _logger;
        private readonly ConcurrentDictionary<string, (DateTime At, AbridgedResolution Value)> _memo = new ConcurrentDictionary<string, (DateTime, AbridgedResolution)>();

        public TalearrAbridgedResolver(ITalearrAudibleFactsService factsService, IMediaFileService mediaFileService, Logger logger)
        {
            _factsService = factsService;
            _mediaFileService = mediaFileService;
            _logger = logger;
        }

        public AbridgedResolution Resolve(Book book)
        {
            if (book == null || book.MediaType != BookMediaType.Audiobook)
            {
                return AbridgedResolution.Unknown;
            }

            if (AbridgedReleaseDetector.Detect(book.Title) == AbridgedMarker.Abridged)
            {
                return new AbridgedResolution(AbridgedMarker.Abridged, AbridgedSources.Metadata, "book title says abridged");
            }

            var editions = SafeEditions(book);
            var wanted = editions.Where(e => e.Monitored).OrderBy(e => e.Id).FirstOrDefault();
            var key = $"{book.Id}:{wanted?.Id ?? 0}:{editions.Count}";
            if (book.Id > 0 && _memo.TryGetValue(key, out var hit) && hit.At + MemoLifetime > DateTime.UtcNow)
            {
                return hit.Value;
            }

            AbridgedResolution result;
            try
            {
                var files = book.Id > 0 ? _mediaFileService?.GetFilesByBook(book.Id) : null;
                result = wanted != null
                    ? ResolveEdition(wanted, editions, files)
                    : ResolveByFiles(editions, files, CachedFacts(editions)) ?? AbridgedResolution.Unknown;
            }
            catch (Exception ex)
            {
                _logger.Debug(ex, "Talearr: abridged resolution failed for book {0}, using metadata only", book.Id);
                result = wanted != null ? Heuristic(wanted, editions) : AbridgedResolution.Unknown;
            }

            if (book.Id > 0)
            {
                _memo[key] = (DateTime.UtcNow, result);
            }

            return result;
        }

        public AbridgedResolution ResolveEdition(Edition edition, IReadOnlyCollection<Edition> siblings, IReadOnlyCollection<BookFile> files = null)
        {
            if (edition == null)
            {
                return AbridgedResolution.Unknown;
            }

            var all = (siblings ?? Array.Empty<Edition>()).Where(e => e != null).ToList();
            if (!all.Any(e => ReferenceEquals(e, edition) || (e.Id > 0 && e.Id == edition.Id)))
            {
                all.Add(edition);
            }

            var facts = CachedFacts(all);

            // 1. Audible.
            var product = ProductFor(edition, facts);
            if (product != null && product.Abridged != AbridgedMarker.Unknown)
            {
                return new AbridgedResolution(product.Abridged, AbridgedSources.Audible, $"Audible {product.Marketplace} {product.Asin} is {product.FormatType}");
            }

            // 2. and 3.
            var heuristic = Heuristic(edition, all, facts);
            if (heuristic.Marker != AbridgedMarker.Unknown)
            {
                return heuristic;
            }

            // 4. Files.
            return ResolveByFiles(all, files, facts) ?? AbridgedResolution.Unknown;
        }

        private AbridgedResolution Heuristic(Edition edition, List<Edition> all, Dictionary<int, EditionAudibleFacts> facts = null)
        {
            var text = ExplicitText(edition);
            if (text != AbridgedMarker.Unknown)
            {
                return new AbridgedResolution(text, AbridgedSources.Metadata, $"edition says {(text == AbridgedMarker.Abridged ? "abridged" : "unabridged")}");
            }

            var runtime = RuntimeSeconds(edition, facts);
            if (runtime < MinimumComparableSeconds)
            {
                return AbridgedResolution.Unknown;
            }

            var comparable = all
                .Where(other => !ReferenceEquals(other, edition) && !(other.Id > 0 && other.Id == edition.Id))
                .Where(other => SameLanguage(edition, other))
                .Where(other => ExplicitText(other) != AbridgedMarker.Abridged && ProductFor(other, facts)?.Abridged != AbridgedMarker.Abridged)
                .Select(other => (Edition: other, Seconds: RuntimeSeconds(other, facts)))
                .Where(x => x.Seconds >= MinimumComparableSeconds)
                .ToList();

            if (comparable.Count > 0 && runtime < comparable.Max(x => x.Seconds) * AbridgedReleaseDetector.AbridgedRuntimeRatio)
            {
                return new AbridgedResolution(AbridgedMarker.Abridged, AbridgedSources.Runtime,
                    $"{runtime / 60} min against {comparable.Max(x => x.Seconds) / 60} min for the longest edition");
            }

            var knownUnabridged = comparable.Where(x => ProductFor(x.Edition, facts)?.Abridged == AbridgedMarker.Unabridged).ToList();
            if (knownUnabridged.Count > 0 && runtime >= knownUnabridged.Max(x => x.Seconds) * UnabridgedRuntimeRatio)
            {
                return new AbridgedResolution(AbridgedMarker.Unabridged, AbridgedSources.Runtime, "as long as the unabridged Audible recording");
            }

            return AbridgedResolution.Unknown;
        }

        private static AbridgedResolution ResolveByFiles(List<Edition> editions, IReadOnlyCollection<BookFile> files, Dictionary<int, EditionAudibleFacts> facts)
        {
            var seconds = (files ?? Array.Empty<BookFile>()).Where(f => f != null).Sum(f => (long)(f.DurationSeconds ?? 0));
            if (seconds < MinimumComparableSeconds)
            {
                return null;
            }

            var unabridged = editions
                .Where(e => (facts != null && facts.TryGetValue(e.Id, out var f) && f.Product?.Abridged == AbridgedMarker.Unabridged) ||
                            ExplicitText(e) == AbridgedMarker.Unabridged)
                .Select(e => (long)RuntimeSeconds(e, facts))
                .Where(s => s >= MinimumComparableSeconds)
                .DefaultIfEmpty(0)
                .Max();

            if (unabridged <= 0)
            {
                return null;
            }

            if (seconds < unabridged * AbridgedReleaseDetector.AbridgedRuntimeRatio)
            {
                return new AbridgedResolution(AbridgedMarker.Abridged, AbridgedSources.Files, $"files run {seconds / 60} min, the unabridged recording {unabridged / 60} min");
            }

            return seconds >= unabridged * UnabridgedRuntimeRatio
                ? new AbridgedResolution(AbridgedMarker.Unabridged, AbridgedSources.Files, "files are as long as the unabridged recording")
                : null;
        }

        private Dictionary<int, EditionAudibleFacts> CachedFacts(List<Edition> editions)
        {
            if (_factsService == null || editions.Count == 0)
            {
                return new Dictionary<int, EditionAudibleFacts>();
            }

            try
            {
                return _factsService.GetCachedFacts(editions);
            }
            catch (Exception ex)
            {
                _logger.Debug(ex, "Talearr: no cached Audible facts");
                return new Dictionary<int, EditionAudibleFacts>();
            }
        }

        private static AudibleProduct ProductFor(Edition edition, Dictionary<int, EditionAudibleFacts> facts)
        {
            if (facts == null || edition == null || edition.Id <= 0)
            {
                return null;
            }

            return facts.TryGetValue(edition.Id, out var f) && f.HasProduct ? f.Product : null;
        }

        private static int RuntimeSeconds(Edition edition, Dictionary<int, EditionAudibleFacts> facts)
        {
            var minutes = ProductFor(edition, facts)?.RuntimeMinutes;
            return minutes.HasValue && minutes.Value > 0 ? minutes.Value * 60 : edition?.DurationSeconds ?? 0;
        }

        public static AbridgedMarker ExplicitText(Edition edition)
        {
            if (edition == null)
            {
                return AbridgedMarker.Unknown;
            }

            var markers = new[]
                {
                    edition.Title,
                    edition.Subtitle,
                    edition.Disambiguation,
                    edition.EditionInfo,
                    edition.EditionFormat,
                    edition.Format,
                    edition.AudioProductionType
                }
                .Select(AbridgedReleaseDetector.Detect)
                .Where(m => m != AbridgedMarker.Unknown)
                .Distinct()
                .ToList();

            return markers.Count == 1 ? markers[0] : AbridgedMarker.Unknown;
        }

        private static bool SameLanguage(Edition a, Edition b)
        {
            if (string.IsNullOrWhiteSpace(a?.Language) || string.IsNullOrWhiteSpace(b?.Language) ||
                a.Language.Equals("und", StringComparison.OrdinalIgnoreCase) || b.Language.Equals("und", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            return string.Equals(a.Language.Trim(), b.Language.Trim(), StringComparison.OrdinalIgnoreCase);
        }

        private static List<Edition> SafeEditions(Book book)
        {
            try
            {
                return book.Editions?.Where(e => e != null).ToList() ?? new List<Edition>();
            }
            catch (Exception)
            {
                return new List<Edition>();
            }
        }
    }
}
