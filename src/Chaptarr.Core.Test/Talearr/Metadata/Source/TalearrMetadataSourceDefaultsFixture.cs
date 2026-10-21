using System.Reflection;
using NLog;
using NUnit.Framework;
using Chaptarr.Core.Test.Talearr.ContentFilter;
using Chaptarr.Core.Test.Talearr.MetadataProfiles;
using NzbDrone.Core.Books;
using NzbDrone.Core.Configuration;
using NzbDrone.Core.Lifecycle;
using NzbDrone.Core.Talearr.Metadata.Source;

namespace Chaptarr.Core.Test.Talearr.Metadata.Source
{
    [TestFixture]
    public class TalearrMetadataSourceDefaultsFixture
    {
        private class FakeSourceSettings : ITalearrMetadataSourceSettingsService
        {
            private readonly ContentFilterConfigProxy _config;

            public FakeSourceSettings(ContentFilterConfigProxy config)
            {
                _config = config;
            }

            public int Saves { get; private set; }

            public TalearrMetadataSource GetSource()
            {
                return _config.Values.TryGetValue(TalearrMetadataSourceSettingsService.SourceKey, out var value) &&
                       TalearrMetadataSourceSettingsService.TryParse(value, out var source)
                    ? source
                    : TalearrMetadataSourceSettingsService.DefaultSource;
            }

            public TalearrMetadataSource SaveSource(TalearrMetadataSource source)
            {
                Saves++;
                _config.Values[TalearrMetadataSourceSettingsService.SourceKey] = TalearrMetadataSourceSettingsService.ToApiValue(source);
                return source;
            }
        }

        private AuthorPathsProxy _authors;
        private ContentFilterConfigProxy _config;
        private FakeSourceSettings _settings;
        private TalearrMetadataSourceDefaults _subject;

        [SetUp]
        public void SetUp()
        {
            var authorService = DispatchProxy.Create<IAuthorService, AuthorPathsProxy>();
            var configRepository = DispatchProxy.Create<IConfigRepository, ContentFilterConfigProxy>();
            _authors = (AuthorPathsProxy)(object)authorService;
            _config = (ContentFilterConfigProxy)(object)configRepository;
            _settings = new FakeSourceSettings(_config);
            _subject = new TalearrMetadataSourceDefaults(_settings, authorService, configRepository, LogManager.GetLogger("TalearrTest"));
        }

        [Test]
        public void a_new_install_starts_on_the_talearr_source()
        {
            _subject.Handle(new ApplicationStartedEvent());

            Assert.That(_settings.GetSource(), Is.EqualTo(TalearrMetadataSource.Talearr));
        }

        [Test]
        public void an_existing_library_stays_on_the_metadata_server()
        {
            _authors.Paths[1] = "/books/George Orwell";

            _subject.Handle(new ApplicationStartedEvent());

            Assert.That(_settings.GetSource(), Is.EqualTo(TalearrMetadataSource.Chaptarr));
        }

        [Test]
        public void a_saved_choice_is_never_changed()
        {
            _config.Values[TalearrMetadataSourceSettingsService.SourceKey] = "chaptarr";

            _subject.Handle(new ApplicationStartedEvent());

            Assert.That(_settings.Saves, Is.EqualTo(0));
            Assert.That(_settings.GetSource(), Is.EqualTo(TalearrMetadataSource.Chaptarr));
        }
    }
}
