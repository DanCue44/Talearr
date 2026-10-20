using System.Collections.Generic;
using System.Linq;
using NLog;
using NzbDrone.Core.Books;
using NzbDrone.Core.Configuration;
using NzbDrone.Core.Lifecycle;
using NzbDrone.Core.Messaging.Events;
using NzbDrone.Core.Profiles.Metadata;

namespace NzbDrone.Core.Talearr.MetadataProfiles
{
    // Talearr: Chaptarr seeds the "Audiobook Default" and "Ebook Default" metadata profiles with
    // every filter off, so following an author imports box sets, omnibuses, study guides and
    // listings nobody has rated. On a fresh install (no authors yet) this applies a light set of
    // filters to those two profiles once. Existing installs only get the marker, so profiles a
    // user already has are never changed. Monitored books, manual adds and books with files are
    // protected from every filter (MetadataProfileService, RefreshBookService).
    public class TalearrMetadataProfileDefaults : IHandle<ApplicationStartedEvent>
    {
        public const string MarkerKey = "talearr.metadataprofile.defaults";
        public static readonly string[] DefaultProfileNames = { "Audiobook Default", "Ebook Default" };

        // Only listings with no ratings at all are dropped; Readarr's own standard profile uses 350.
        public const double RecommendedMinPopularity = 1;

        public static readonly IReadOnlyList<string> RecommendedIgnoredTerms = new[]
        {
            "York Notes",
            "SparkNotes",
            "CliffsNotes",
            "Study Guide",
            "Summary of",
            "Books Set",
            "Box Set",
            "Boxed Set",
            "Complete Works",
            "Collected Works"
        };

        private readonly IMetadataProfileService _profileService;
        private readonly IAuthorService _authorService;
        private readonly IConfigRepository _configRepository;
        private readonly Logger _logger;

        public TalearrMetadataProfileDefaults(IMetadataProfileService profileService,
                                              IAuthorService authorService,
                                              IConfigRepository configRepository,
                                              Logger logger)
        {
            _profileService = profileService;
            _authorService = authorService;
            _configRepository = configRepository;
            _logger = logger;
        }

        public static void ApplyRecommended(MetadataProfile profile)
        {
            profile.MinPopularity = RecommendedMinPopularity;
            profile.SkipMissingDate = true;
            profile.SkipPartsAndSets = true;
            profile.SkipOmnibus = true;

            var ignored = profile.Ignored ?? new List<string>();
            foreach (var term in RecommendedIgnoredTerms)
            {
                if (!ignored.Any(x => string.Equals(x, term, System.StringComparison.OrdinalIgnoreCase)))
                {
                    ignored.Add(term);
                }
            }

            profile.Ignored = ignored;
        }

        public void Handle(ApplicationStartedEvent message)
        {
            if (_configRepository.Get(MarkerKey) != null)
            {
                return;
            }

            var isFreshInstall = _authorService.AllAuthorPaths().Count == 0;

            if (isFreshInstall)
            {
                foreach (var profile in _profileService.All().Where(x => DefaultProfileNames.Contains(x.Name)))
                {
                    ApplyRecommended(profile);
                    _profileService.Update(profile);
                    _logger.Info("Talearr: applied recommended filters to metadata profile {0}", profile.Name);
                }
            }

            _configRepository.Upsert(MarkerKey, isFreshInstall ? "applied" : "existing");
        }
    }
}
