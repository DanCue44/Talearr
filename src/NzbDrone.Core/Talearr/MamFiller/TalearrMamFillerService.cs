using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using NLog;
using NzbDrone.Common.Extensions;
using NzbDrone.Core.Download;
using NzbDrone.Core.Indexers;
using NzbDrone.Core.Indexers.MyAnonaMouse;
using NzbDrone.Core.MediaFiles.TorrentInfo;
using NzbDrone.Core.Messaging.Commands;
using NzbDrone.Core.Parser.Model;
using NzbDrone.Core.Talearr.Seeding;

namespace NzbDrone.Core.Talearr.MamFiller
{
    public interface ITalearrMamFillerService
    {
        TalearrMamFillerStatus Run(bool manual);
    }

    // Talearr: MAM freeleech seeding filler. While unsatisfied-torrent slots are free it grabs a
    // few popular English freeleech audiobooks/ebooks and seeds them, so more torrents pass MAM's
    // 72-hour requirement (satisfied torrents earn bonus points; unsatisfied ones earn none).
    //
    // MAM policy: only MAM's documented automation endpoints are used (the JSON search, download.php
    // and, when the stored status is old, jsonLoad.php), all through the indexer's own HTTP client,
    // proxy and session handling. No wedges, no bonus points. Filler torrents go straight to the
    // torrent client in their own category: no grab history, no tracked download, no import.
    // Taste preferences (TalearrMamFillerTaste) rank preferred picks first; those can also be
    // copied into the library (TalearrMamFillerLibraryService) while they keep seeding.
    //
    // Safety: never above limit minus margin (and the slot guard's Safety Reserve and User Buffer
    // still apply through TryReserve); stops when the account status is unknown or older than 3
    // hours (stretched by a longer Account Status Refresh), when the account is seed-only, when
    // any MAM torrent is at risk, and for Filler Pause After Error (12 hours) after any MAM error.
    public class TalearrMamFillerService : ITalearrMamFillerService, IExecute<MamFreeleechFillerCommand>
    {
        // Talearr: search depth (Filler Search Pages, Filler Preferred Search Pages) and the
        // status refresh age (Account Status Refresh) are indexer settings now; they were fixed at
        // 3 pages, 2 pages and one hour.

        private readonly IIndexerFactory _indexerFactory;
        private readonly IProvideDownloadClient _downloadClientProvider;
        private readonly IMamUnsatisfiedSlotGuard _slotGuard;
        private readonly IMamUnsatisfiedSlotReservationRepository _reservations;
        private readonly ITalearrSeedingService _seedingService;
        private readonly ITalearrMamFillerStore _store;
        private readonly ITorrentFileInfoReader _torrentFileInfoReader;
        private readonly ITalearrMamFillerLibrary _library;
        private readonly Logger _logger;
        private readonly object _runLock = new object();

        public TalearrMamFillerService(IIndexerFactory indexerFactory,
                                       IProvideDownloadClient downloadClientProvider,
                                       IMamUnsatisfiedSlotGuard slotGuard,
                                       IMamUnsatisfiedSlotReservationRepository reservations,
                                       ITalearrSeedingService seedingService,
                                       ITalearrMamFillerStore store,
                                       ITorrentFileInfoReader torrentFileInfoReader,
                                       Logger logger,
                                       ITalearrMamFillerLibrary library = null)
        {
            _library = library;
            _indexerFactory = indexerFactory;
            _downloadClientProvider = downloadClientProvider;
            _slotGuard = slotGuard;
            _reservations = reservations;
            _seedingService = seedingService;
            _store = store;
            _torrentFileInfoReader = torrentFileInfoReader;
            _logger = logger;
        }

        // For tests.
        internal Func<DateTime> UtcNow { get; set; } = () => DateTime.UtcNow;

        public void Execute(MamFreeleechFillerCommand message)
        {
            Run(message?.Trigger == CommandTrigger.Manual);
        }

        public TalearrMamFillerStatus Run(bool manual)
        {
            lock (_runLock)
            {
                return RunAsync(manual).GetAwaiter().GetResult();
            }
        }

        private async Task<TalearrMamFillerStatus> RunAsync(bool manual)
        {
            var now = UtcNow();
            var status = _store.GetStatus() ?? new TalearrMamFillerStatus();

            var indexer = _indexerFactory.GetAvailableProviders()
                .OfType<MyAnonaMouse>()
                .FirstOrDefault(i => i.Definition?.Settings is MyAnonaMouseSettings { FillerEnabled: true });

            if (indexer == null)
            {
                if (status.Enabled || manual)
                {
                    status.Enabled = false;
                    return Finish(status, "Off: turn on Freeleech Seeding Filler on the MyAnonamouse indexer", manual);
                }

                return status;
            }

            var definition = (IndexerDefinition)indexer.Definition;
            var settings = (MyAnonaMouseSettings)definition.Settings;
            status.Enabled = true;
            status.DryRun = settings.FillerDryRun;

            var client = FindClient();
            if (client != null)
            {
                Housekeep(client, settings, now);
                AdvancePicks(client, settings, status, now);
            }

            if (!manual && !TalearrMamFillerRules.IsRunDue(status.LastRunAt, settings.FillerIntervalHours, now))
            {
                return status;
            }

            status.LastRunAt = now;
            status.Target = null;
            status.FreeSlots = null;

            if (status.BackoffUntil.HasValue && status.BackoffUntil.Value > now)
            {
                return Finish(status, $"Paused after a MAM error until {status.BackoffUntil.Value:yyyy-MM-dd HH:mm} UTC");
            }

            status.BackoffUntil = null;

            if (client == null)
            {
                return Finish(status, "No enabled qBittorrent download client");
            }

            // Account status: refresh once when it is older than Account Status Refresh
            // (jsonLoad.php, allowed).
            var refreshed = settings.UnsatisfiedStatusRefreshedUtc;
            if (!refreshed.HasValue || now - refreshed.Value > settings.ResolveAccountStatusRefreshInterval())
            {
                try
                {
                    var account = await indexer.RefreshAccountStatus();
                    _indexerFactory.Update(definition);
                    _slotGuard.Reconcile(indexer, account);
                }
                catch (Exception ex)
                {
                    return BackOff(status, settings, now, $"MAM account status refresh failed ({ex.Message})");
                }
            }

            if (!MyAnonaMouseAccountStanding.HasFreshStatus(settings, now) ||
                !settings.UnsatisfiedCount.HasValue ||
                !settings.UnsatisfiedLimit.HasValue)
            {
                return Finish(status, $"MAM account status is unknown or older than {MyAnonaMouseAccountStanding.StaleHours(settings)} hours");
            }

            if (MyAnonaMouseAccountStanding.IsSeedOnly(settings))
            {
                return Finish(status, MyAnonaMouseAccountStanding.SeedOnlyMessage);
            }

            TalearrSeedingReport report;
            try
            {
                report = _seedingService.GetReport(TimeSpan.Zero);
            }
            catch (Exception ex)
            {
                _logger.Warn(ex, "MAM freeleech filler: could not read the seeding status");
                return Finish(status, "Could not read the seeding status");
            }

            var atRisk = report?.Torrents?.FirstOrDefault(t => t.AtRisk && IsMamRow(t));
            if (atRisk != null)
            {
                return Finish(status, $"A MAM torrent is at risk ({atRisk.Title ?? atRisk.DownloadId}): fix it first");
            }

            var tracked = _store.All();
            var reservations = SafeReservations(definition.Id);
            var pending = settings.ProtectUnsatisfiedSlots
                ? reservations.Count
                : tracked.Count(t => settings.UnsatisfiedSnapshotUtc.HasValue &&
                                     t.AddedAt >= settings.UnsatisfiedSnapshotUtc.Value - TalearrMamFillerRules.AccountingLag);

            var limit = settings.UnsatisfiedLimit.Value;
            var unsatisfied = settings.UnsatisfiedCount.Value;
            var target = TalearrMamFillerRules.Target(limit, settings.FillerSlotMargin);
            var free = TalearrMamFillerRules.FreeSlots(limit, unsatisfied, pending, settings.FillerSlotMargin, settings.FillerMaxGrabsPerRun);
            status.Target = target;
            status.FreeSlots = free;

            if (free <= 0)
            {
                return Finish(status, $"No free slots: {unsatisfied} unsatisfied and {pending} pending of the filler target {target} ({limit} minus margin {settings.FillerSlotMargin})");
            }

            // Dedupe: anything the filler ever grabbed, anything reserved for a normal grab, and any
            // torrent with the same name already in the client.
            var knownIds = new HashSet<string>(tracked.Select(t => t.TorrentId), StringComparer.Ordinal);
            knownIds.UnionWith(reservations.Select(r => r.TorrentId));
            var clientNames = new HashSet<string>(
                (report?.Torrents ?? new List<TalearrSeedingTorrent>()).Select(t => t.Title).Where(t => t.IsNotNullOrWhiteSpace()),
                StringComparer.OrdinalIgnoreCase);

            var eligible = new List<TalearrMamFillerCandidate>();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            var skipped = new Dictionary<string, int>();
            var taste = BuildTaste(settings);
            var seenCategories = new Dictionary<int, string>();

            void Consider(List<Newtonsoft.Json.Linq.JToken> results)
            {
                foreach (var candidate in results.Select(TalearrMamFillerRules.Parse).Where(c => c != null))
                {
                    if (!seen.Add(candidate.TorrentId))
                    {
                        continue;
                    }

                    if (candidate.CategoryId > 0 && !string.IsNullOrWhiteSpace(candidate.CategoryName))
                    {
                        seenCategories[candidate.CategoryId.Value] = candidate.CategoryName;
                    }

                    var reason = TalearrMamFillerRules.Reject(candidate, settings);
                    if (reason == null && (knownIds.Contains(candidate.TorrentId) || clientNames.Contains(candidate.Title)))
                    {
                        reason = "already grabbed or in the client";
                    }

                    // Talearr: taste preferences (excluded categories, Only Grab Preferred).
                    reason ??= TalearrMamFillerTaste.Reject(candidate, taste);

                    if (reason != null)
                    {
                        skipped[reason] = skipped.TryGetValue(reason, out var count) ? count + 1 : 1;
                        continue;
                    }

                    candidate.PreferredReason = TalearrMamFillerTaste.PreferredReason(candidate, taste);
                    eligible.Add(candidate);
                }
            }

            var searchPages = Math.Clamp(settings.FillerSearchPages, 1, MyAnonaMouseSettings.MaximumFillerSearchPages);
            for (var page = 0; page < searchPages; page++)
            {
                List<Newtonsoft.Json.Linq.JToken> results;
                try
                {
                    results = await indexer.SearchFillerFreeleechAsync(page);
                }
                catch (Exception ex)
                {
                    return BackOff(status, settings, now, $"MAM freeleech search failed ({ex.Message})");
                }

                Consider(results);

                if (results.Count < MyAnonaMouse.FillerPageSize)
                {
                    break;
                }
            }

            // Talearr: the general search only sees the first pages of freeleech torrents, so the
            // preferred categories get a search of their own (MAM filters by site category id).
            var preferredIds = TalearrMamFillerTaste.PreferredCategoryIds(taste.PreferredCategories, seenCategories);
            var preferredPages = Math.Clamp(settings.FillerPreferredSearchPages, 0, MyAnonaMouseSettings.MaximumFillerSearchPages);
            for (var page = 0; preferredIds.Count > 0 && page < preferredPages; page++)
            {
                List<Newtonsoft.Json.Linq.JToken> results;
                try
                {
                    results = await indexer.SearchFillerFreeleechAsync(page, preferredIds);
                }
                catch (Exception ex)
                {
                    return BackOff(status, settings, now, $"MAM preferred category search failed ({ex.Message})");
                }

                Consider(results);

                if (results.Count < MyAnonaMouse.FillerPageSize)
                {
                    break;
                }
            }

            var picked = TalearrMamFillerRules.Rank(eligible, settings.FillerPreferAudiobooks).Take(free).ToList();
            status.PreviewIsDryRun = settings.FillerDryRun;
            status.Preview = picked.Select(ToPreview).ToList();

            var skippedText = skipped.Count == 0
                ? string.Empty
                : " (skipped: " + string.Join(", ", skipped.OrderByDescending(s => s.Value).Select(s => $"{s.Value} {s.Key}")) + ")";

            var preferredCount = picked.Count(c => c.IsPreferred);
            if (preferredCount > 0)
            {
                skippedText = $"; {preferredCount} preferred" + skippedText;
            }

            if (settings.FillerDryRun)
            {
                foreach (var candidate in picked)
                {
                    _logger.Info("MAM freeleech filler (dry run) would grab {0} '{1}' ({2} leechers, {3} seeders, {4})",
                        candidate.TorrentId, candidate.Title, candidate.Leechers, candidate.Seeders, candidate.Size.SizeSuffix());
                }

                return Finish(status, $"Dry run: would grab {picked.Count} of {eligible.Count} eligible freeleech torrents for {free} free slot(s){skippedText}");
            }

            var grabbed = 0;
            string stopReason = null;

            foreach (var candidate in picked)
            {
                var standing = MyAnonaMouseAccountStanding.Evaluate(settings, true, candidate.Size, UtcNow());
                if (standing != null)
                {
                    stopReason = standing;
                    break;
                }

                var availability = _slotGuard.TryReserve(ToRemoteBook(definition, candidate));
                if (!availability.Accepted)
                {
                    stopReason = availability.Reason;
                    break;
                }

                byte[] torrentFile;
                string hash;
                try
                {
                    torrentFile = await indexer.DownloadFillerTorrentAsync(candidate.TorrentId);
                    hash = _torrentFileInfoReader.GetHashFromTorrentFile(torrentFile)?.ToUpperInvariant();
                }
                catch (Exception ex)
                {
                    status.Preview = MarkGrabbed(status.Preview, picked, grabbed);
                    return BackOff(status, settings, now, $"Grabbed {grabbed}; MAM download of {candidate.TorrentId} failed ({ex.Message})");
                }

                var record = new TalearrFillerTorrent
                {
                    TorrentId = candidate.TorrentId,
                    Title = candidate.Title,
                    Size = candidate.Size,
                    IsAudiobook = candidate.IsAudiobook,
                    AddedAt = UtcNow(),
                    ClientHash = hash,
                    DownloadClient = ((IDownloadClient)client).Definition?.Name,
                    Category = settings.FillerImport ? client.GetLibraryCategory(candidate.IsAudiobook) : settings.FillerCategory,
                    State = TalearrFillerTorrent.StateAdded
                };

                try
                {
                    if (hash.IsNotNullOrWhiteSpace() && client.HasTorrent(hash))
                    {
                        record.State = TalearrFillerTorrent.StateDone;
                        _store.Save(record);
                        _logger.Info("MAM freeleech filler: {0} '{1}' is already in the client; not added again", candidate.TorrentId, candidate.Title);
                        continue;
                    }

                    client.AddFillerTorrent($"{candidate.TorrentId}.torrent", torrentFile, hash, record.Category, TalearrMamFillerRules.SeedTime(settings));
                }
                catch (Exception ex)
                {
                    _logger.Warn(ex, "MAM freeleech filler: the download client refused {0}", candidate.TorrentId);
                    stopReason = $"the download client refused {candidate.TorrentId} ({ex.Message})";
                    break;
                }

                _store.Save(record);
                grabbed++;
                SavePick(candidate, record, settings);
                _logger.Info("MAM freeleech filler grabbed {0} '{1}' ({2} leechers, {3} seeders, {4}) into category {5}",
                    candidate.TorrentId, candidate.Title, candidate.Leechers, candidate.Seeders, candidate.Size.SizeSuffix(), record.Category);
            }

            status.Preview = MarkGrabbed(status.Preview, picked, grabbed);

            var message = $"Grabbed {grabbed} of {picked.Count} picked freeleech torrents for {free} free slot(s){skippedText}";
            return Finish(status, stopReason == null ? message : $"{message}; stopped: {stopReason}");
        }

        // Filler torrents the client no longer has are marked removed; with Remove After Seed Time,
        // torrents that have seeded long enough (never under 72 hours) are removed with their files.
        internal void Housekeep(ITalearrMamFillerClient client, MyAnonaMouseSettings settings, DateTime now)
        {
            try
            {
                var active = _store.All()
                    .Where(t => t.State == TalearrFillerTorrent.StateAdded &&
                                t.ClientHash.IsNotNullOrWhiteSpace() &&
                                string.Equals(t.Category, settings.FillerCategory, StringComparison.OrdinalIgnoreCase))
                    .ToList();

                if (active.Count == 0)
                {
                    return;
                }

                var inClient = client.GetCategoryTorrents(settings.FillerCategory)
                    .Where(t => t.DownloadId.IsNotNullOrWhiteSpace())
                    .GroupBy(t => t.DownloadId, StringComparer.OrdinalIgnoreCase)
                    .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
                var seedSeconds = TalearrMamFillerRules.SeedTime(settings).TotalSeconds;

                foreach (var record in active)
                {
                    if (!inClient.TryGetValue(record.ClientHash, out var torrent))
                    {
                        record.State = TalearrFillerTorrent.StateRemoved;
                        record.UpdatedAt = now;
                        _store.Save(record);
                        continue;
                    }

                    if (settings.FillerRemoveAfterSeed && torrent.SeedingSeconds.HasValue && torrent.SeedingSeconds.Value >= seedSeconds)
                    {
                        client.RemoveFillerTorrent(record.ClientHash);
                        record.State = TalearrFillerTorrent.StateDone;
                        record.UpdatedAt = now;
                        _store.Save(record);
                        _logger.Info("MAM freeleech filler: removed '{0}' after seeding {1:0.#} days", record.Title, torrent.SeedingSeconds.Value / 86400.0);
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.Warn(ex, "MAM freeleech filler: could not check filler torrents in the download client");
            }
        }

        // Talearr: taste preferences. Library authors only when that option is on; a library read
        // failure just means no author preferences this run.
        private TalearrMamFillerTasteProfile BuildTaste(MyAnonaMouseSettings settings)
        {
            List<string> authors = null;
            if (settings.FillerPreferLibraryAuthors && _library != null)
            {
                try
                {
                    authors = _library.LibraryAuthorNames();
                }
                catch (Exception ex)
                {
                    _logger.Debug(ex, "MAM freeleech filler: could not read the library authors");
                }
            }

            return TalearrMamFillerTaste.Build(settings, authors);
        }

        // Only preferred picks that stay in the filler category are put into the library; with
        // Filler Import Into Library the normal category already handles every pick.
        internal static bool ShouldImport(TalearrMamFillerCandidate candidate, TalearrFillerTorrent record, MyAnonaMouseSettings settings)
        {
            return candidate.IsPreferred &&
                   settings.FillerImportPreferred &&
                   !settings.FillerImport &&
                   record.ClientHash.IsNotNullOrWhiteSpace() &&
                   string.Equals(record.Category, settings.FillerCategory, StringComparison.OrdinalIgnoreCase);
        }

        private void SavePick(TalearrMamFillerCandidate candidate, TalearrFillerTorrent record, MyAnonaMouseSettings settings)
        {
            if (_library == null || !ShouldImport(candidate, record, settings))
            {
                return;
            }

            _store.SavePick(new TalearrFillerPick
            {
                TorrentId = candidate.TorrentId,
                Title = candidate.Title,
                Author = candidate.Authors?.FirstOrDefault(),
                IsAudiobook = candidate.IsAudiobook,
                PreferredReason = candidate.PreferredReason,
                LibraryState = TalearrFillerPick.StateWaiting,
                LibraryMessage = "Waiting for the download to finish",
                UpdatedAt = record.AddedAt
            });
        }

        // Moves open library picks along once their filler torrent has finished. Runs every hour
        // with the housekeeping, not only when the filler itself is due.
        internal void AdvancePicks(ITalearrMamFillerClient client, MyAnonaMouseSettings settings, TalearrMamFillerStatus status, DateTime now)
        {
            if (_library == null || !settings.FillerImportPreferred)
            {
                return;
            }

            try
            {
                var open = _store.AllPicks().Where(p => p.IsOpen).ToList();
                if (open.Count == 0)
                {
                    return;
                }

                var records = _store.All().ToDictionary(t => t.TorrentId, StringComparer.Ordinal);
                var changed = false;

                foreach (var pick in open)
                {
                    TalearrFillerDownload download = null;
                    if (records.TryGetValue(pick.TorrentId, out var record) &&
                        record.State != TalearrFillerTorrent.StateRemoved &&
                        record.ClientHash.IsNotNullOrWhiteSpace())
                    {
                        download = client.GetFillerDownload(record.ClientHash, record.Category);
                    }

                    if (_library.Advance(pick, download, now))
                    {
                        _store.SavePick(pick);
                        changed = true;
                    }
                }

                if (changed)
                {
                    ApplyPicks(status);
                    _store.SaveStatus(status);
                }
            }
            catch (Exception ex)
            {
                _logger.Warn(ex, "MAM freeleech filler: could not import preferred picks into the library");
            }
        }

        private void ApplyPicks(TalearrMamFillerStatus status)
        {
            if (status?.Preview == null || status.Preview.Count == 0)
            {
                return;
            }

            var picks = _store.AllPicks().ToDictionary(p => p.TorrentId, StringComparer.Ordinal);
            foreach (var item in status.Preview)
            {
                if (item.TorrentId != null && picks.TryGetValue(item.TorrentId, out var pick))
                {
                    item.LibraryState = pick.LibraryState;
                    item.LibraryMessage = pick.LibraryMessage;
                }
            }
        }

        internal static RemoteBook ToRemoteBook(IndexerDefinition definition, TalearrMamFillerCandidate candidate)
        {
            return new RemoteBook
            {
                Release = new TorrentInfo
                {
                    Guid = "MAM-" + candidate.TorrentId,
                    IndexerId = definition.Id,
                    Indexer = definition.Name,
                    Title = candidate.Title,
                    Size = candidate.Size,
                    DownloadProtocol = DownloadProtocol.Torrent,
                    IndexerFlags = IndexerFlags.Freeleech
                },
                ReleaseSource = ReleaseSourceType.Rss
            };
        }

        private static bool IsMamRow(TalearrSeedingTorrent row)
        {
            return row.IsFiller || TalearrSeedingRules.IsMamIndexerName(row.Indexer);
        }

        private ITalearrMamFillerClient FindClient()
        {
            try
            {
                return _downloadClientProvider.GetDownloadClients()
                    .Where(c => c.Protocol == DownloadProtocol.Torrent)
                    .OfType<ITalearrMamFillerClient>()
                    .FirstOrDefault();
            }
            catch (Exception ex)
            {
                _logger.Debug(ex, "MAM freeleech filler: could not list download clients");
                return null;
            }
        }

        private List<MamUnsatisfiedSlotReservation> SafeReservations(int indexerId)
        {
            try
            {
                return _reservations.All().Where(r => r.IndexerId == indexerId).ToList();
            }
            catch (Exception ex)
            {
                _logger.Debug(ex, "MAM freeleech filler: could not read slot reservations");
                return new List<MamUnsatisfiedSlotReservation>();
            }
        }

        private static TalearrMamFillerPreviewItem ToPreview(TalearrMamFillerCandidate candidate)
        {
            return new TalearrMamFillerPreviewItem
            {
                TorrentId = candidate.TorrentId,
                Title = candidate.Title,
                Size = candidate.Size,
                Seeders = candidate.Seeders,
                Leechers = candidate.Leechers,
                IsAudiobook = candidate.IsAudiobook,
                Preferred = candidate.IsPreferred,
                PreferredReason = candidate.PreferredReason,
                Category = TalearrMamFillerTaste.CategoryNameOf(candidate),
                Author = candidate.Authors == null || candidate.Authors.Count == 0 ? null : string.Join(", ", candidate.Authors)
            };
        }

        private List<TalearrMamFillerPreviewItem> MarkGrabbed(List<TalearrMamFillerPreviewItem> preview, List<TalearrMamFillerCandidate> picked, int grabbed)
        {
            var added = new HashSet<string>(
                _store.All().Where(t => t.State == TalearrFillerTorrent.StateAdded).Select(t => t.TorrentId),
                StringComparer.Ordinal);

            foreach (var item in preview)
            {
                item.Grabbed = added.Contains(item.TorrentId);
            }

            return preview;
        }

        private TalearrMamFillerStatus BackOff(TalearrMamFillerStatus status, MyAnonaMouseSettings settings, DateTime now, string reason)
        {
            var pause = TalearrMamFillerRules.ErrorPause(settings);
            status.BackoffUntil = now.Add(pause);
            _logger.Warn("MAM freeleech filler: {0}; pausing for {1} hours", reason, pause.TotalHours);
            return Finish(status, $"{reason}; paused for {pause.TotalHours} hours");
        }

        private TalearrMamFillerStatus Finish(TalearrMamFillerStatus status, string result, bool save = true)
        {
            status.LastResult = result;

            try
            {
                status.Tracked = _store.All().Count(t => t.State == TalearrFillerTorrent.StateAdded);
                ApplyPicks(status);
            }
            catch (Exception)
            {
                status.Tracked = 0;
            }

            if (save)
            {
                _store.SaveStatus(status);
            }

            _logger.Info("MAM freeleech filler: {0}", result);
            return status;
        }
    }
}
