using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using NLog;
using NUnit.Framework;
using Chaptarr.Core.Test.Talearr.ContentFilter;
using NzbDrone.Core.Books;
using NzbDrone.Core.Configuration;
using NzbDrone.Core.Lifecycle;
using NzbDrone.Core.Profiles.Metadata;
using NzbDrone.Core.Talearr.MetadataProfiles;

namespace Chaptarr.Core.Test.Talearr.MetadataProfiles
{
    internal class ProfileServiceProxy : DispatchProxy
    {
        public List<MetadataProfile> Profiles { get; } = new List<MetadataProfile>();
        public List<MetadataProfile> Updated { get; } = new List<MetadataProfile>();

        protected override object Invoke(MethodInfo targetMethod, object[] args)
        {
            switch (targetMethod?.Name)
            {
                case nameof(IMetadataProfileService.All):
                    return Profiles;
                case nameof(IMetadataProfileService.Update):
                    Updated.Add((MetadataProfile)args[0]);
                    return null;
                default:
                    throw new NotImplementedException(targetMethod?.Name);
            }
        }
    }

    internal class AuthorPathsProxy : DispatchProxy
    {
        public Dictionary<int, string> Paths { get; } = new Dictionary<int, string>();

        protected override object Invoke(MethodInfo targetMethod, object[] args)
        {
            if (targetMethod?.Name == nameof(IAuthorService.AllAuthorPaths))
            {
                return Paths;
            }

            throw new NotImplementedException(targetMethod?.Name);
        }
    }

    [TestFixture]
    public class TalearrMetadataProfileDefaultsFixture
    {
        private ProfileServiceProxy _profiles;
        private AuthorPathsProxy _authors;
        private ContentFilterConfigProxy _config;
        private TalearrMetadataProfileDefaults _subject;

        [SetUp]
        public void SetUp()
        {
            var profileService = DispatchProxy.Create<IMetadataProfileService, ProfileServiceProxy>();
            var authorService = DispatchProxy.Create<IAuthorService, AuthorPathsProxy>();
            var configRepository = DispatchProxy.Create<IConfigRepository, ContentFilterConfigProxy>();
            _profiles = (ProfileServiceProxy)(object)profileService;
            _authors = (AuthorPathsProxy)(object)authorService;
            _config = (ContentFilterConfigProxy)(object)configRepository;

            _profiles.Profiles.Add(new MetadataProfile { Id = 1, Name = "Audiobook Default", Ignored = new List<string>() });
            _profiles.Profiles.Add(new MetadataProfile { Id = 2, Name = "Ebook Default", Ignored = new List<string> { "box set" } });
            _profiles.Profiles.Add(new MetadataProfile { Id = 3, Name = "None", MinPopularity = 1e10, Ignored = new List<string>() });

            _subject = new TalearrMetadataProfileDefaults(profileService, authorService, configRepository, LogManager.GetLogger("TalearrTest"));
        }

        [Test]
        public void should_apply_recommended_filters_to_default_profiles_on_a_fresh_install()
        {
            _subject.Handle(new ApplicationStartedEvent());

            Assert.That(_profiles.Updated.Select(x => x.Name), Is.EquivalentTo(TalearrMetadataProfileDefaults.DefaultProfileNames));

            var audiobook = _profiles.Profiles.Single(x => x.Id == 1);
            Assert.That(audiobook.SkipOmnibus, Is.True);
            Assert.That(audiobook.SkipPartsAndSets, Is.True);
            Assert.That(audiobook.SkipMissingDate, Is.True);
            Assert.That(audiobook.MinPopularity, Is.EqualTo(TalearrMetadataProfileDefaults.RecommendedMinPopularity));
            Assert.That(audiobook.Ignored, Is.EquivalentTo(TalearrMetadataProfileDefaults.RecommendedIgnoredTerms));

            // An existing term is kept once, compared without case
            var ebook = _profiles.Profiles.Single(x => x.Id == 2);
            Assert.That(ebook.Ignored.Count(x => string.Equals(x, "Box Set", StringComparison.OrdinalIgnoreCase)), Is.EqualTo(1));

            Assert.That(_profiles.Profiles.Single(x => x.Id == 3).MinPopularity, Is.EqualTo(1e10));
            Assert.That(_config.Values[TalearrMetadataProfileDefaults.MarkerKey], Is.EqualTo("applied"));
        }

        [Test]
        public void should_leave_profiles_alone_when_authors_already_exist()
        {
            _authors.Paths[1] = "/books/George Orwell";

            _subject.Handle(new ApplicationStartedEvent());

            Assert.That(_profiles.Updated, Is.Empty);
            Assert.That(_profiles.Profiles.Single(x => x.Id == 1).SkipOmnibus, Is.False);
            Assert.That(_config.Values[TalearrMetadataProfileDefaults.MarkerKey], Is.EqualTo("existing"));
        }

        [Test]
        public void should_run_only_once()
        {
            _subject.Handle(new ApplicationStartedEvent());
            _profiles.Updated.Clear();
            _profiles.Profiles.Single(x => x.Id == 1).SkipOmnibus = false;

            _subject.Handle(new ApplicationStartedEvent());

            Assert.That(_profiles.Updated, Is.Empty);
            Assert.That(_profiles.Profiles.Single(x => x.Id == 1).SkipOmnibus, Is.False);
        }
    }
}
