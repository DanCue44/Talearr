using System;
using System.Collections.Generic;
using Chaptarr.Core.Test.Talearr.GrabPolicy;
using NLog;
using NUnit.Framework;
using NzbDrone.Common.Cache;
using NzbDrone.Core.Books;
using NzbDrone.Core.CustomFormats;
using NzbDrone.Core.MediaFiles;
using NzbDrone.Core.Organizer;
using NzbDrone.Core.Qualities;
using NzbDrone.Core.Talearr.Audiobook;
using NzbDrone.Core.Talearr.GrabPolicy;
using NzbDrone.Core.Talearr.Recordings;

namespace Chaptarr.Core.Test.Talearr.Recordings
{
    // {Abridged} and {Abridgement} give the same answer the grab policy uses.
    [TestFixture]
    public class TalearrAbridgedNamingFixture
    {
        private TalearrTestFolder _folder;
        private TalearrRecordingsStore _store;
        private TalearrAbridgedResolver _resolver;

        [SetUp]
        public void SetUp()
        {
            _folder = new TalearrTestFolder();
            _store = _folder.CreateStore();
            var (repository, _) = ConfigRepositoryProxy.Create();
            var facts = new TalearrAudibleFactsService(null, _store, new TalearrRecordingsSettingsService(repository, LogManager.GetCurrentClassLogger()), LogManager.GetCurrentClassLogger());
            var (files, fake) = InterfaceFake<IMediaFileService>.Create();
            fake.On(nameof(IMediaFileService.GetFilesByBook), _ => new List<BookFile>());
            _resolver = new TalearrAbridgedResolver(facts, files, LogManager.GetCurrentClassLogger());
        }

        [TearDown]
        public void TearDown()
        {
            _folder.Dispose();
        }

        private void LinkAbridged(Edition edition)
        {
            _store.UpsertProducts(new[] { new AudibleProduct { Marketplace = "us", Asin = "B000MOFFET", Status = AudibleProductStatus.Ok, Title = "The Firm", FormatType = "abridged", RuntimeMinutes = 360, FetchedAt = DateTime.UtcNow } });
            _store.UpsertLink(new EditionAudibleLink { ForeignEditionId = TalearrEditionKey.For(edition), Marketplace = "us", Asin = "B000MOFFET", Method = EditionAudibleLink.MethodSearch, Confidence = 0.9, CheckedAt = DateTime.UtcNow });
        }

        private static FileNameBuilder Builder(ITalearrAbridgedResolver resolver)
        {
            var (quality, qualityFake) = InterfaceFake<IQualityDefinitionService>.Create();
            qualityFake.On(nameof(IQualityDefinitionService.Get), args => new QualityDefinition((Quality)args[0]));
            var (naming, _) = InterfaceFake<INamingConfigService>.Create();
            var (formats, _) = InterfaceFake<ICustomFormatCalculationService>.Create();

            return new FileNameBuilder(naming, quality, new CacheManager(), formats, LogManager.GetCurrentClassLogger(), resolver);
        }

        private static string Build(FileNameBuilder builder, string format, Edition edition)
        {
            var config = NamingConfig.Default;
            config.RenameBooks = true;
            config.StandardBookFormat = format;
            var file = new BookFile { Path = "/downloads/The Firm.m4b", Part = 1, PartCount = 1, Quality = new QualityModel(Quality.M4B), MediaType = "audiobook" };

            return builder.BuildBookFileName(new Author { Name = "John Grisham" }, edition, file, config, new List<CustomFormat>());
        }

        private static Edition Moffett()
        {
            var edition = FirmEditions.MoffettAbridged();
            edition.Book = new Book { Title = "The Firm", MediaType = BookMediaType.Audiobook, Author = new Author { Name = "John Grisham" }, Editions = new List<Edition>() };
            edition.Book.Editions.Add(edition);
            return edition;
        }

        [Test]
        public void abridged_token_uses_audible_facts_like_the_grab_policy()
        {
            var edition = Moffett();
            LinkAbridged(edition);

            Assert.That(TalearrNamingTokens.GetAbridgement(edition), Is.EqualTo(AbridgedMarker.Unknown), "metadata alone does not know");
            Assert.That(Build(Builder(_resolver), "{Book Title}{ (Abridged)}", edition), Is.EqualTo("The Firm (Abridged)"));
            Assert.That(Build(Builder(_resolver), "{Book Title}{ (Abridgement)}", edition), Is.EqualTo("The Firm (Abridged)"));
            Assert.That(_resolver.ResolveEdition(edition, edition.Book.Editions).Marker, Is.EqualTo(AbridgedMarker.Abridged));
        }

        [Test]
        public void without_facts_or_a_resolver_the_metadata_rules_still_apply()
        {
            var edition = Moffett();
            edition.EditionInfo = "Unabridged";

            Assert.That(Build(Builder(null), "{Book Title}{ (Abridgement)}", edition), Is.EqualTo("The Firm (Unabridged)"));
            Assert.That(Build(Builder(_resolver), "{Book Title}{ (Abridgement)}", edition), Is.EqualTo("The Firm (Unabridged)"));
            Assert.That(Build(Builder(_resolver), "{Book Title}{ (Abridgement)}", Moffett()), Is.EqualTo("The Firm"));
        }

        [Test]
        public void a_failing_resolver_falls_back_to_metadata()
        {
            var (resolver, fake) = InterfaceFake<ITalearrAbridgedResolver>.Create();
            fake.On(nameof(ITalearrAbridgedResolver.ResolveEdition), _ => throw new InvalidOperationException("talearr.db gone"));
            var edition = Moffett();
            edition.EditionInfo = "Abridged";

            Assert.That(Build(Builder(resolver), "{Book Title}{ (Abridged)}", edition), Is.EqualTo("The Firm (Abridged)"));
        }
    }
}
