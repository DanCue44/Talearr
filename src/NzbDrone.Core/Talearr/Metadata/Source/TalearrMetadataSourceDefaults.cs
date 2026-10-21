using NLog;
using NzbDrone.Core.Books;
using NzbDrone.Core.Configuration;
using NzbDrone.Core.Lifecycle;
using NzbDrone.Core.Messaging.Events;

namespace NzbDrone.Core.Talearr.Metadata.Source
{
    // Talearr: a fresh install (no authors yet) starts on the Talearr (Hardcover) source, which
    // takes effect once a free Hardcover token is saved; until then the health check asks for one
    // and the metadata server is used. An existing library, including a Chaptarr database moved
    // over, is pinned to the metadata server, because switching prunes listings and should only
    // happen when the user chooses it in Settings > Metadata.
    public class TalearrMetadataSourceDefaults : IHandle<ApplicationStartedEvent>
    {
        private readonly ITalearrMetadataSourceSettingsService _settings;
        private readonly IAuthorService _authorService;
        private readonly IConfigRepository _configRepository;
        private readonly Logger _logger;

        public TalearrMetadataSourceDefaults(ITalearrMetadataSourceSettingsService settings,
                                             IAuthorService authorService,
                                             IConfigRepository configRepository,
                                             Logger logger)
        {
            _settings = settings;
            _authorService = authorService;
            _configRepository = configRepository;
            _logger = logger;
        }

        public void Handle(ApplicationStartedEvent message)
        {
            if (_configRepository.Get(TalearrMetadataSourceSettingsService.SourceKey) != null)
            {
                return;
            }

            var isFreshInstall = _authorService.AllAuthorPaths().Count == 0;
            var source = isFreshInstall ? TalearrMetadataSource.Talearr : TalearrMetadataSource.Chaptarr;

            _settings.SaveSource(source);
            _logger.Info("Talearr: metadata source set to {0} ({1})",
                TalearrMetadataSourceSettingsService.ToApiValue(source),
                isFreshInstall ? "new install" : "existing library kept on the metadata server");
        }
    }
}
