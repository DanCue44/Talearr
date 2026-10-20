using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using NLog;
using NzbDrone.Core.Books;
using NzbDrone.Core.CustomFormats;
using NzbDrone.Core.Download;
using NzbDrone.Core.MediaFiles;
using NzbDrone.Core.Parser.Model;
using NzbDrone.Core.Profiles.Qualities;
using NzbDrone.Core.Qualities;
using NzbDrone.Core.Talearr.GrabPolicy;
using NzbDrone.Core.Talearr.QualityLadder;

namespace NzbDrone.Core.Talearr.Recordings
{
    public interface ITalearrUpgradeGuard
    {
        // Null when the guard does not apply (off, not an audiobook, nothing replaced, manual
        // import or a forced grab, files already in the library).
        UpgradeVerdict Evaluate(Book book, Author author, IReadOnlyList<BookFile> existing, IReadOnlyList<LocalBook> incoming, DownloadClientItem downloadClientItem, QualityProfile profile);

        // For a KeepBoth verdict: the unabridged recording the download goes to (an upstream wanted
        // copy, created when needed). Null when no unabridged edition is known; the import hook then
        // falls back to upstream's own additional copy.
        KeepBothDestination ResolveKeepBothDestination(Book book);
    }

    public sealed class KeepBothDestination
    {
        public Book Book { get; set; }
        public Edition Edition { get; set; }

        // True when the row was made for this import (removed again if nothing imports into it).
        public bool Created { get; set; }
    }

    // Talearr: measures the files on both sides of an audiobook upgrade and applies
    // TalearrUpgradeRules. Called from ImportApprovedBooks before any old file is touched.
    public class TalearrUpgradeGuard : ITalearrUpgradeGuard
    {
        private readonly IMediaInfoExtractor _mediaInfoExtractor;
        private readonly ITalearrAbridgedResolver _abridgedResolver;
        private readonly ITalearrRecordingsService _recordingsService;
        private readonly IBookService _bookService;
        private readonly IEditionService _editionService;
        private readonly ICustomFormatCalculationService _formatCalculator;
        private readonly ITalearrRecordingsSettingsService _settingsService;
        private readonly ITalearrQualityLadderSettingsService _ladderSettings; // Talearr
        private readonly ITalearrUpgradeCooldown _upgradeCooldown; // Talearr
        private readonly Logger _logger;

        public TalearrUpgradeGuard(IMediaInfoExtractor mediaInfoExtractor,
                                   ITalearrAbridgedResolver abridgedResolver,
                                   ITalearrRecordingsService recordingsService,
                                   IBookService bookService,
                                   IEditionService editionService,
                                   ICustomFormatCalculationService formatCalculator,
                                   ITalearrRecordingsSettingsService settingsService,
                                   Logger logger,
                                   ITalearrQualityLadderSettingsService ladderSettings = null,
                                   ITalearrUpgradeCooldown upgradeCooldown = null)
        {
            _ladderSettings = ladderSettings;
            _upgradeCooldown = upgradeCooldown;
            _mediaInfoExtractor = mediaInfoExtractor;
            _abridgedResolver = abridgedResolver;
            _recordingsService = recordingsService;
            _bookService = bookService;
            _editionService = editionService;
            _formatCalculator = formatCalculator;
            _settingsService = settingsService;
            _logger = logger;
        }

        public UpgradeVerdict Evaluate(Book book, Author author, IReadOnlyList<BookFile> existing, IReadOnlyList<LocalBook> incoming, DownloadClientItem downloadClientItem, QualityProfile profile)
        {
            var settings = TalearrRecordingsSettingsService.Resolve(_settingsService);
            if (!settings.UpgradeGuard || book == null || book.MediaType != BookMediaType.Audiobook)
            {
                return null;
            }

            var newItems = (incoming ?? Array.Empty<LocalBook>()).Where(i => i != null && !string.IsNullOrWhiteSpace(i.Path)).ToList();
            var oldFiles = (existing ?? Array.Empty<BookFile>()).Where(f => f != null && !string.IsNullOrWhiteSpace(f.Path)).ToList();
            if (newItems.Count == 0 || oldFiles.Count == 0)
            {
                return null;
            }

            // Manual import and forced grabs override, and a library scan is not a replacement.
            if (downloadClientItem?.DownloadForced == true || newItems.Any(i => i.IsManualImport || i.ExistingFile))
            {
                return null;
            }

            // Re-importing the files that are already there is not an upgrade.
            var oldPaths = new HashSet<string>(oldFiles.Select(f => f.Path), StringComparer.OrdinalIgnoreCase);
            if (newItems.All(i => oldPaths.Contains(i.Path)))
            {
                return null;
            }

            try
            {
                var oldFacts = MeasureExisting(book, oldFiles);
                var newFacts = MeasureIncoming(newItems, downloadClientItem);
                var comparison = Compare(author, profile, oldFiles, newItems);
                var verdict = TalearrUpgradeRules.Decide(oldFacts, newFacts, comparison, settings, TalearrQualityLadderSettingsService.Resolve(_ladderSettings).AudioUpgrades);

                _logger.Debug("Talearr: upgrade guard for '{0}' (book {1}): {2} ({3}): {4}", book.Title, book.Id, verdict.Kind, verdict.Rule, verdict.Reason);

                // A rejected upgrade download: blocklist it and pause upgrades for the book, so the
                // next search does not grab the same estimate-only "upgrade" again.
                if (verdict.Kind == UpgradeVerdictKind.Reject && downloadClientItem != null)
                {
                    _upgradeCooldown?.RecordRejectedUpgrade(book, verdict.Reason, downloadClientItem);
                }

                return verdict;
            }
            catch (Exception ex)
            {
                _logger.Warn(ex, "Talearr: upgrade guard could not measure '{0}'; leaving the upgrade to upstream", book.Title);
                return null;
            }
        }

        public KeepBothDestination ResolveKeepBothDestination(Book book)
        {
            if (book == null || _recordingsService == null)
            {
                return null;
            }

            var outcome = _recordingsService.AddRecording(new AddRecordingRequest
            {
                BookId = book.Id,
                Kind = RecordingKinds.Unabridged,
                Search = false,
                RequestedBy = "import"
            });

            if (outcome.Status == AddRecordingStatus.Ambiguous && outcome.Candidates.Count > 0)
            {
                outcome = _recordingsService.AddRecording(new AddRecordingRequest
                {
                    BookId = book.Id,
                    Kind = RecordingKinds.Edition,
                    EditionId = outcome.Candidates[0].EditionId,
                    Search = false,
                    RequestedBy = "import"
                });
            }

            var bookId = outcome.Recording?.BookId ?? 0;
            if (bookId <= 0 || bookId == book.Id || (outcome.Status != AddRecordingStatus.Created && outcome.Status != AddRecordingStatus.Existing))
            {
                return null;
            }

            var target = _bookService.GetBook(bookId);
            var editions = _editionService.GetEditionsByBook(bookId) ?? new List<Edition>();
            var edition = editions.Where(e => e.ManualAdd && e.Monitored).OrderBy(e => e.Id).FirstOrDefault()
                          ?? editions.Where(e => e.Monitored).OrderBy(e => e.Id).FirstOrDefault();
            if (target == null || edition == null)
            {
                return null;
            }

            target.Author ??= book.Author;
            edition.Book = target;
            return new KeepBothDestination { Book = target, Edition = edition, Created = outcome.Status == AddRecordingStatus.Created };
        }

        private RecordingAudioFacts MeasureExisting(Book book, List<BookFile> files)
        {
            var parts = files.Select(f =>
            {
                var info = f.MediaInfo;
                if ((info == null || info.AudioBitrate <= 0) && File.Exists(f.Path))
                {
                    info = _mediaInfoExtractor.ExtractMediaInfo(f.Path) ?? info;
                }

                var seconds = f.DurationSeconds > 0 ? f.DurationSeconds.Value : info?.Duration.TotalSeconds > 0 ? (int)info.Duration.TotalSeconds : 0;
                return (Path: f.Path, Info: info, Seconds: seconds);
            }).ToList();

            var facts = Summarize(parts);
            facts.Narrators = files.Select(f => f.Narrator).Where(n => !string.IsNullOrWhiteSpace(n)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();

            AbridgedResolution resolution = null;
            try
            {
                resolution = _abridgedResolver?.Resolve(book);
            }
            catch (Exception ex)
            {
                _logger.Debug(ex, "Talearr: abridged resolution failed for book {0}", book.Id);
            }

            // The files' own length is what is being replaced; a runtime-from-files answer would just
            // compare the files with themselves, so only explicit answers count here.
            if (resolution != null && resolution.Source != AbridgedSources.Files)
            {
                facts.Abridged = resolution.Marker;
            }

            return facts;
        }

        private RecordingAudioFacts MeasureIncoming(List<LocalBook> items, DownloadClientItem downloadClientItem)
        {
            var parts = items.Select(i =>
            {
                var info = File.Exists(i.Path) ? _mediaInfoExtractor.ExtractMediaInfo(i.Path) : null;
                var seconds = i.DurationSeconds > 0 ? i.DurationSeconds.Value : info?.Duration.TotalSeconds > 0 ? (int)info.Duration.TotalSeconds : 0;
                return (Path: i.Path, Info: info, Seconds: seconds);
            }).ToList();

            var facts = Summarize(parts);
            facts.Narrators = items.Select(i => i.Narrator).Where(n => !string.IsNullOrWhiteSpace(n)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            facts.Abridged = IncomingAbridged(items, downloadClientItem);
            return facts;
        }

        // Release title, scene name or the download folder name, first explicit answer wins.
        public static AbridgedMarker IncomingAbridged(IEnumerable<LocalBook> items, DownloadClientItem downloadClientItem)
        {
            var texts = new List<string> { downloadClientItem?.Title };
            foreach (var item in items ?? Enumerable.Empty<LocalBook>())
            {
                texts.Add(item.SceneName);
                texts.Add(item.DownloadClientBookInfo?.ReleaseTitle);
                texts.Add(SafeFolderName(item.Path));
            }

            foreach (var text in texts.Where(t => !string.IsNullOrWhiteSpace(t)))
            {
                var marker = AbridgedReleaseDetector.Detect(text);
                if (marker != AbridgedMarker.Unknown)
                {
                    return marker;
                }
            }

            return AbridgedMarker.Unknown;
        }

        private static RecordingAudioFacts Summarize(List<(string Path, MediaInfoModel Info, int Seconds)> parts)
        {
            var facts = new RecordingAudioFacts
            {
                FileCount = parts.Count,
                SingleM4b = parts.Count == 1 && string.Equals(Path.GetExtension(parts[0].Path), ".m4b", StringComparison.OrdinalIgnoreCase)
            };

            var totalSeconds = parts.Sum(p => (long)p.Seconds);
            facts.DurationSeconds = totalSeconds > 0 && parts.All(p => p.Seconds > 0) ? (int?)totalSeconds : null;

            var withBitrate = parts.Where(p => p.Info?.AudioBitrate > 0).ToList();
            if (withBitrate.Count == parts.Count && withBitrate.Count > 0)
            {
                var weight = withBitrate.Sum(p => (double)Math.Max(1, p.Seconds));
                facts.BitrateKbps = (int)Math.Round(withBitrate.Sum(p => p.Info.AudioBitrate * (double)Math.Max(1, p.Seconds)) / weight);
            }

            var channels = parts.Where(p => p.Info?.AudioChannels > 0).Select(p => p.Info.AudioChannels).ToList();
            facts.Channels = channels.Count == parts.Count && channels.Count > 0 ? channels.Min() : null;

            var codecs = parts.Select(p => TalearrUpgradeRules.NormalizeCodec(p.Info?.AudioFormat)).Distinct().ToList();
            facts.Codec = codecs.Count == 1 ? codecs[0] : null;

            return facts;
        }

        private UpgradeProfileComparison Compare(Author author, QualityProfile profile, List<BookFile> oldFiles, List<LocalBook> newItems)
        {
            var comparison = new UpgradeProfileComparison();
            var newQuality = newItems[0].Quality;
            var oldQuality = oldFiles.Select(f => f.Quality).Where(q => q?.Quality != null).OrderBy(q => q.Quality.Id).FirstOrDefault();
            if (newQuality?.Quality == null || oldQuality == null)
            {
                return comparison;
            }

            comparison.Quality = profile?.Items?.Any() == true
                ? Math.Sign(new QualityModelComparer(profile).Compare(newQuality.Quality, oldQuality.Quality))
                : 0;
            comparison.Revision = Math.Sign(newQuality.Revision.CompareTo(oldQuality.Revision));

            if (profile != null && _formatCalculator != null)
            {
                try
                {
                    var item = newItems[0];
                    item.Author ??= author;
                    var newScore = profile.CalculateCustomFormatScore(_formatCalculator.ParseCustomFormat(item) ?? new List<CustomFormat>());
                    var oldScore = profile.CalculateCustomFormatScore(_formatCalculator.ParseCustomFormat(oldFiles[0], author) ?? new List<CustomFormat>());
                    comparison.CustomFormatScore = Math.Sign(newScore - oldScore);
                }
                catch (Exception ex)
                {
                    _logger.Debug(ex, "Talearr: custom format scores unavailable for the upgrade guard");
                }
            }

            return comparison;
        }

        private static string SafeFolderName(string path)
        {
            try
            {
                return string.IsNullOrWhiteSpace(path) ? null : Path.GetFileName(Path.GetDirectoryName(path));
            }
            catch (ArgumentException)
            {
                return null;
            }
        }
    }
}
