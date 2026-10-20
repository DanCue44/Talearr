using System;
using System.Collections.Generic;
using System.Linq;
using NLog;
using NzbDrone.Core.Books;

namespace NzbDrone.Core.Talearr.Recordings
{
    // One edition with what Talearr knows about it from Audible.
    public sealed class EditionAudibleFacts
    {
        public Edition Edition { get; set; }
        public EditionAudibleLink Link { get; set; }
        public AudibleProduct Product { get; set; }

        public bool HasProduct => Product != null && Product.Found;
    }

    public sealed class AudibleResolveOptions
    {
        // Ignore cache ages (on-demand refresh).
        public bool Force { get; set; }

        // How many catalog searches this call may make. Searches are the expensive part.
        public int SearchBudget { get; set; } = 5;

        // How many Audnexus calls this call may make (one per ASIN).
        public int AudnexusBudget { get; set; } = 10;
    }

    public sealed class AudibleResolveSummary
    {
        public int Editions { get; set; }
        public int Skipped { get; set; }
        public int Matched { get; set; }
        public int Unmatched { get; set; }
        public int Searches { get; set; }
        public bool Throttled { get; set; }
    }

    public interface ITalearrAudibleFactsService
    {
        // Cache only, never touches the network. Keyed by edition id.
        Dictionary<int, EditionAudibleFacts> GetCachedFacts(IEnumerable<Edition> editions, string marketplace = null);

        // Resolves editions of one book (same title and author) to Audible products, fetching what
        // is missing or stale. Results are written to talearr.db.
        AudibleResolveSummary Resolve(IReadOnlyCollection<Edition> editions, string bookTitle, string authorName, AudibleResolveOptions options = null);

        bool NeedsRefresh(EditionAudibleLink link, DateTime nowUtc);
    }

    // Talearr: Audible facts per edition, cache first. Lookup order per edition: its stored
    // AudibleASIN and ASIN list in one batch call, then (within a search budget) a title + author
    // search constrained by narrator and runtime. Results go to talearr.db only; upstream Editions
    // columns are never written, because metadata refresh owns them.
    public class TalearrAudibleFactsService : ITalearrAudibleFactsService
    {
        public static readonly TimeSpan PositiveTtl = TimeSpan.FromDays(30);
        public static readonly TimeSpan NegativeTtl = TimeSpan.FromDays(7);

        private readonly ITalearrAudibleClient _client;
        private readonly ITalearrRecordingsStore _store;
        private readonly ITalearrRecordingsSettingsService _settingsService;
        private readonly Logger _logger;
        private readonly Func<DateTime> _clock;

        public TalearrAudibleFactsService(ITalearrAudibleClient client,
                                          ITalearrRecordingsStore store,
                                          ITalearrRecordingsSettingsService settingsService,
                                          Logger logger)
            : this(client, store, settingsService, logger, () => DateTime.UtcNow)
        {
        }

        internal TalearrAudibleFactsService(ITalearrAudibleClient client,
                                            ITalearrRecordingsStore store,
                                            ITalearrRecordingsSettingsService settingsService,
                                            Logger logger,
                                            Func<DateTime> clock)
        {
            _client = client;
            _store = store;
            _settingsService = settingsService;
            _logger = logger;
            _clock = clock;
        }

        public static bool IsFresh(AudibleProduct product, DateTime nowUtc)
        {
            if (product == null)
            {
                return false;
            }

            var ttl = product.Found ? PositiveTtl : NegativeTtl;
            return product.FetchedAt + ttl > nowUtc;
        }

        public bool NeedsRefresh(EditionAudibleLink link, DateTime nowUtc)
        {
            if (link == null)
            {
                return true;
            }

            var ttl = link.Matched ? PositiveTtl : NegativeTtl;
            return link.CheckedAt + ttl <= nowUtc;
        }

        public Dictionary<int, EditionAudibleFacts> GetCachedFacts(IEnumerable<Edition> editions, string marketplace = null)
        {
            var market = TalearrRecordingsStore.NormalizeMarketplace(marketplace ?? TalearrRecordingsSettingsService.Resolve(_settingsService).Marketplace);
            var list = (editions ?? Enumerable.Empty<Edition>()).Where(e => e != null && e.Id > 0).GroupBy(e => e.Id).Select(g => g.First()).ToList();
            var result = list.ToDictionary(e => e.Id, e => new EditionAudibleFacts { Edition = e });

            var keyed = list.Where(e => TalearrEditionKey.For(e) != null).ToList();
            if (keyed.Count == 0)
            {
                return result;
            }

            var links = _store.GetLinks(keyed.Select(TalearrEditionKey.For), market)
                .ToDictionary(l => l.ForeignEditionId, StringComparer.Ordinal);
            var products = _store.GetProducts(market, links.Values.Where(l => l.Matched).Select(l => l.Asin))
                .ToDictionary(p => p.Asin, StringComparer.Ordinal);

            foreach (var edition in keyed)
            {
                if (!links.TryGetValue(TalearrEditionKey.For(edition), out var link))
                {
                    continue;
                }

                result[edition.Id].Link = link;
                if (link.Matched && products.TryGetValue(link.Asin, out var product))
                {
                    result[edition.Id].Product = product;
                }
            }

            return result;
        }

        public AudibleResolveSummary Resolve(IReadOnlyCollection<Edition> editions, string bookTitle, string authorName, AudibleResolveOptions options = null)
        {
            options ??= new AudibleResolveOptions();
            var summary = new AudibleResolveSummary();
            var settings = TalearrRecordingsSettingsService.Resolve(_settingsService);
            if (settings.Provider == TalearrAudibleProvider.Off)
            {
                return summary;
            }

            var market = settings.Marketplace;
            var now = _clock();
            var candidates = (editions ?? Array.Empty<Edition>())
                .Where(e => e != null && TalearrEditionKey.For(e) != null)
                .GroupBy(TalearrEditionKey.For, StringComparer.Ordinal)
                .Select(g => g.First())
                .ToList();

            summary.Editions = candidates.Count;
            var existing = _store.GetLinks(candidates.Select(TalearrEditionKey.For), market)
                .ToDictionary(l => l.ForeignEditionId, StringComparer.Ordinal);

            var pending = candidates
                .Where(e => options.Force || !existing.TryGetValue(TalearrEditionKey.For(e), out var link) || NeedsRefresh(link, now))
                .ToList();
            summary.Skipped = candidates.Count - pending.Count;
            if (pending.Count == 0)
            {
                return summary;
            }

            // 1. Every ASIN of every pending edition, in as few calls as possible.
            var asins = pending.SelectMany(TalearrAudibleMatcher.CandidateAsins).Distinct(StringComparer.Ordinal).ToList();
            var products = GetProducts(market, asins, settings, options, summary);

            var unresolved = new List<Edition>();
            foreach (var edition in pending)
            {
                var own = TalearrAudibleMatcher.CandidateAsins(edition)
                    .Select(a => products.TryGetValue(a, out var p) ? p : null)
                    .Where(p => p != null)
                    .ToList();

                var match = TalearrAudibleMatcher.MatchByAsin(edition, own);
                if (match != null)
                {
                    SaveLink(edition, market, match, now);
                    summary.Matched++;
                }
                else
                {
                    unresolved.Add(edition);
                }
            }

            // 2. One search per book serves every edition still unresolved. Audnexus has no search.
            if (unresolved.Count > 0 && settings.Provider == TalearrAudibleProvider.Audible && !summary.Throttled)
            {
                if (options.SearchBudget > 0)
                {
                    summary.Searches++;
                    var search = _client.Search(market, bookTitle, authorName);
                    if (search.Answered)
                    {
                        _store.UpsertProducts(search.Products);
                        foreach (var edition in unresolved)
                        {
                            var match = TalearrAudibleMatcher.MatchBySearch(edition, bookTitle, search.Products);
                            if (match != null)
                            {
                                SaveLink(edition, market, match, now);
                                summary.Matched++;
                            }
                            else
                            {
                                SaveLink(edition, market, null, now);
                                summary.Unmatched++;
                            }
                        }
                    }
                    else
                    {
                        summary.Throttled |= search.Throttled;
                    }
                }
            }
            else if (unresolved.Count > 0 && !summary.Throttled)
            {
                foreach (var edition in unresolved)
                {
                    SaveLink(edition, market, null, now);
                    summary.Unmatched++;
                }
            }

            _logger.Debug("Talearr: Audible facts for '{0}' ({1}): {2} editions, {3} matched, {4} unmatched, {5} cached, {6} searches{7}",
                bookTitle, market, summary.Editions, summary.Matched, summary.Unmatched, summary.Skipped, summary.Searches, summary.Throttled ? ", throttled" : string.Empty);

            return summary;
        }

        private Dictionary<string, AudibleProduct> GetProducts(string market, List<string> asins, TalearrRecordingsSettings settings, AudibleResolveOptions options, AudibleResolveSummary summary)
        {
            var now = _clock();
            var result = new Dictionary<string, AudibleProduct>(StringComparer.Ordinal);
            if (asins.Count == 0)
            {
                return result;
            }

            foreach (var cached in _store.GetProducts(market, asins))
            {
                if (!options.Force && IsFresh(cached, now))
                {
                    result[cached.Asin] = cached;
                }
            }

            var missing = asins.Where(a => !result.ContainsKey(a)).ToList();
            if (missing.Count == 0)
            {
                return result;
            }

            var fetched = new List<AudibleProduct>();
            var needAudnexus = new List<string>();

            if (settings.Provider == TalearrAudibleProvider.Audible)
            {
                var lookup = _client.GetProducts(market, missing);
                fetched.AddRange(lookup.Products);
                if (!lookup.Answered)
                {
                    var answered = lookup.Products.Select(p => p.Asin).ToHashSet(StringComparer.Ordinal);
                    var left = missing.Where(a => !answered.Contains(a)).ToList();
                    if (settings.AudnexusFallback)
                    {
                        needAudnexus.AddRange(left);
                    }
                    else
                    {
                        summary.Throttled |= lookup.Throttled || lookup.Failed;
                    }
                }
            }
            else if (settings.Provider == TalearrAudibleProvider.Audnexus)
            {
                needAudnexus.AddRange(missing);
            }

            var budget = options.AudnexusBudget;
            foreach (var asin in needAudnexus)
            {
                if (budget-- <= 0)
                {
                    summary.Throttled = true;
                    break;
                }

                var lookup = _client.GetFromAudnexus(market, asin);
                fetched.AddRange(lookup.Products);
                if (!lookup.Answered)
                {
                    summary.Throttled = true;
                    break;
                }
            }

            _store.UpsertProducts(fetched);
            foreach (var product in fetched)
            {
                result[product.Asin] = product;
            }

            return result;
        }

        private void SaveLink(Edition edition, string market, AudibleMatch match, DateTime now)
        {
            _store.UpsertLink(new EditionAudibleLink
            {
                ForeignEditionId = TalearrEditionKey.For(edition),
                Marketplace = market,
                Asin = match?.Product?.Asin,
                Method = match?.Method ?? EditionAudibleLink.MethodSearch,
                Confidence = match?.Confidence ?? 0,
                EditionIdHint = edition.Id > 0 ? edition.Id : null,
                CheckedAt = now
            });
        }
    }
}
