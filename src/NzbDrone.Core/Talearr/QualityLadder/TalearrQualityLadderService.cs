using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using NLog;
using NzbDrone.Core.Books;
using NzbDrone.Core.MediaFiles;
using NzbDrone.Core.Parser.Model;
using NzbDrone.Core.Profiles.Qualities;
using NzbDrone.Core.Qualities;
using NzbDrone.Core.Talearr.GrabPolicy;
using NzbDrone.Core.Talearr.Recordings;

namespace NzbDrone.Core.Talearr.QualityLadder
{
    public interface ITalearrQualityLadder
    {
        TalearrQualityLadderSettings GetSettings();

        // The book's known runtime: its monitored edition, then cached Audible facts, then the
        // duration TaleArr recorded for the row. Null when nothing is known.
        int? RuntimeSeconds(Book book);

        // Audiobook releases only (AudioQualityEstimate.None otherwise).
        AudioQualityEstimate EstimateRelease(RemoteBook remoteBook);

        // Null when the release is not a ladder upgrade of the book's files, otherwise why it is.
        // Only called for releases the quality profile ranks equal to the files.
        string UpgradeReason(RemoteBook subject, Book book, QualityProfile profile, int newFormatScore, int currentFormatScore);
    }

    // TaleArr: applies the audio quality ladder and the ebook format preference to the decision
    // engine (ranking in DownloadDecisionComparer, upgrades in UpgradeDiskSpecification and
    // CutoffSpecification). Read only: it never writes to the database.
    public class TalearrQualityLadderService : ITalearrQualityLadder
    {
        private static readonly TimeSpan RuntimeCacheTtl = TimeSpan.FromMinutes(5);

        private readonly ITalearrQualityLadderSettingsService _settingsService;
        private readonly ITalearrRecordingsSettingsService _recordingsSettings;
        private readonly IEditionService _editionService;
        private readonly ITalearrAudibleFactsService _audibleFacts;
        private readonly ITalearrAbridgedResolver _abridgedResolver;
        private readonly Logger _logger;
        private readonly ConcurrentDictionary<int, (int? Seconds, DateTime At)> _runtimes = new ConcurrentDictionary<int, (int?, DateTime)>();

        public TalearrQualityLadderService(ITalearrQualityLadderSettingsService settingsService,
                                           Logger logger,
                                           ITalearrRecordingsSettingsService recordingsSettings = null,
                                           IEditionService editionService = null,
                                           ITalearrAudibleFactsService audibleFacts = null,
                                           ITalearrAbridgedResolver abridgedResolver = null)
        {
            _settingsService = settingsService;
            _recordingsSettings = recordingsSettings;
            _editionService = editionService;
            _audibleFacts = audibleFacts;
            _abridgedResolver = abridgedResolver;
            _logger = logger;
        }

        public TalearrQualityLadderSettings GetSettings()
        {
            return TalearrQualityLadderSettingsService.Resolve(_settingsService);
        }

        public int? RuntimeSeconds(Book book)
        {
            if (book == null)
            {
                return null;
            }

            if (book.Id > 0 && _runtimes.TryGetValue(book.Id, out var cached) && DateTime.UtcNow - cached.At < RuntimeCacheTtl)
            {
                return cached.Seconds;
            }

            var seconds = LookupRuntime(book);
            if (book.Id > 0)
            {
                _runtimes[book.Id] = (seconds, DateTime.UtcNow);
            }

            return seconds;
        }

        public AudioQualityEstimate EstimateRelease(RemoteBook remoteBook)
        {
            return EstimateRelease(remoteBook, null);
        }

        public string UpgradeReason(RemoteBook subject, Book book, QualityProfile profile, int newFormatScore, int currentFormatScore)
        {
            try
            {
                var candidate = subject?.ParsedBookInfo?.Quality;
                var files = (book?.BookFiles ?? new List<BookFile>()).Where(f => f?.Quality?.Quality != null).ToList();
                if (candidate?.Quality == null || profile == null || !profile.UpgradeAllowed || files.Count == 0 || newFormatScore < currentFormatScore)
                {
                    return null;
                }

                // Same place in the quality profile and no older revision: the profile has no
                // opinion, so the ladder may decide.
                var comparer = new QualityModelComparer(profile);
                var revision = candidate.Revision ?? new Revision();
                if (files.Any(f => comparer.Compare(candidate.Quality, f.Quality.Quality) != 0 || revision.CompareTo(f.Quality.Revision ?? new Revision()) < 0))
                {
                    return null;
                }

                var settings = GetSettings();

                if (TalearrEbookPreference.IsEbook(candidate.Quality))
                {
                    if (!settings.EbookUpgrades || !TalearrEbookPreference.IsPreferred(candidate.Quality, files.Select(f => f.Quality.Quality), settings.EbookFormats))
                    {
                        return null;
                    }

                    return $"TaleArr: {candidate.Quality.Name} is preferred over {string.Join(", ", files.Select(f => f.Quality.Quality.Name).Distinct())}";
                }

                return AudioUpgradeReason(subject, book, files, settings);
            }
            catch (Exception ex)
            {
                _logger.Debug(ex, "TaleArr: quality ladder upgrade check failed; leaving it to the quality profile");
                return null;
            }
        }

        private string AudioUpgradeReason(RemoteBook subject, Book book, List<BookFile> files, TalearrQualityLadderSettings settings)
        {
            var recordings = TalearrRecordingsSettingsService.Resolve(_recordingsSettings);

            // The import side check (same recording, never worse, measured bitrate) is the upgrade
            // guard; without it a ladder upgrade could replace a different recording.
            if (!settings.AudioUpgrades || !recordings.UpgradeGuard || book.MediaType != BookMediaType.Audiobook)
            {
                return null;
            }

            if (subject.Books == null || subject.Books.Count != 1 || files.Any(f => !IsAudio(f.Quality.Quality)))
            {
                return null;
            }

            if (!SameRecording(subject, book, files))
            {
                return null;
            }

            var existing = TalearrAudioLadder.EstimateFiles(files);
            var fileSeconds = files.All(f => f.DurationSeconds > 0) ? files.Sum(f => f.DurationSeconds.Value) : 0;
            var incoming = EstimateRelease(subject, fileSeconds > 0 ? fileSeconds : null);

            if (!TalearrAudioLadder.IsClearUpgrade(existing, incoming, recordings.MinBitrateGainKbps))
            {
                return null;
            }

            return $"TaleArr: better audio of the same recording ({incoming.Describe()} against {existing.Describe()} on disk)";
        }

        // Grab time evidence only: the release's abridged marker and narrator. The upgrade guard
        // checks the runtime when the download arrives.
        private bool SameRecording(RemoteBook subject, Book book, List<BookFile> files)
        {
            var releaseMarker = AbridgedReleaseDetector.Detect(subject.Release?.Title);
            if (releaseMarker != AbridgedMarker.Unknown)
            {
                var existingMarker = files.Select(f => AbridgedReleaseDetector.Detect(f.AudioProductionType)).FirstOrDefault(m => m != AbridgedMarker.Unknown);
                if (existingMarker == AbridgedMarker.Unknown && _abridgedResolver != null)
                {
                    try
                    {
                        existingMarker = _abridgedResolver.Resolve(book)?.Marker ?? AbridgedMarker.Unknown;
                    }
                    catch (Exception ex)
                    {
                        _logger.Debug(ex, "TaleArr: abridged resolution failed for book {0}", book.Id);
                    }
                }

                if (existingMarker != AbridgedMarker.Unknown && existingMarker != releaseMarker)
                {
                    return false;
                }
            }

            var releaseNarrator = subject.Release?.Narrator ?? subject.ParsedBookInfo?.Narrator;
            var fileNarrators = files.Select(f => f.Narrator).Where(n => !string.IsNullOrWhiteSpace(n)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            if (!string.IsNullOrWhiteSpace(releaseNarrator) && fileNarrators.Count > 0 &&
                TalearrAudibleMatcher.NarratorsMatch(fileNarrators, SplitNames(releaseNarrator)) == false)
            {
                return false;
            }

            return true;
        }

        private AudioQualityEstimate EstimateRelease(RemoteBook remoteBook, int? runtimeOverride)
        {
            var quality = remoteBook?.ParsedBookInfo?.Quality?.Quality;
            if (remoteBook?.Release == null || !IsAudio(quality))
            {
                return AudioQualityEstimate.None;
            }

            // A pack has no single runtime; only its title and size floor count.
            var books = remoteBook.Books ?? new List<Book>();
            var runtime = runtimeOverride ?? (books.Count == 1 ? RuntimeSeconds(books[0]) : null);

            return TalearrAudioLadder.EstimateRelease(quality, remoteBook.Release.Title, remoteBook.Release.Size, runtime);
        }

        private int? LookupRuntime(Book book)
        {
            try
            {
                var editions = _editionService?.GetEditionsByBook(book.Id) ?? new List<Edition>();
                var monitored = editions.Where(e => e.Monitored).ToList();

                var fromEdition = monitored.Select(e => e.DurationSeconds).FirstOrDefault(s => s > 0);
                if (fromEdition > 0)
                {
                    return fromEdition;
                }

                if (_audibleFacts != null && monitored.Count > 0)
                {
                    var minutes = _audibleFacts.GetCachedFacts(monitored)?.Values
                        .Where(f => f.HasProduct && f.Product.RuntimeMinutes > 0)
                        .Select(f => f.Product.RuntimeMinutes)
                        .FirstOrDefault();
                    if (minutes > 0)
                    {
                        return minutes.Value * 60;
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.Debug(ex, "TaleArr: runtime lookup failed for book {0}", book.Id);
            }

            return book.DurationMinutes > 0 ? book.DurationMinutes.Value * 60 : null;
        }

        private static bool IsAudio(Quality quality)
        {
            return quality != null && (quality == Quality.M4B || quality == Quality.MP3 || quality == Quality.FLAC || quality == Quality.UnknownAudio);
        }

        private static IEnumerable<string> SplitNames(string names)
        {
            return names.Split(new[] { ',', '&', ';', '+' }, StringSplitOptions.RemoveEmptyEntries).Select(n => n.Trim());
        }
    }
}
