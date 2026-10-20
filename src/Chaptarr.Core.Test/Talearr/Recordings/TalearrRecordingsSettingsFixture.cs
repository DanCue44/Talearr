using System.Linq;
using Chaptarr.Api.V1.Config;
using Chaptarr.Core.Test.Talearr.GrabPolicy;
using NLog;
using NUnit.Framework;
using NzbDrone.Core.Talearr.Recordings;

namespace Chaptarr.Core.Test.Talearr.Recordings
{
    [TestFixture]
    public class TalearrRecordingsSettingsFixture
    {
        [Test]
        public void should_use_the_agreed_defaults_without_writing_anything()
        {
            var (repository, proxy) = ConfigRepositoryProxy.Create();
            var service = new TalearrRecordingsSettingsService(repository, LogManager.GetCurrentClassLogger());

            var settings = service.Get();

            Assert.Multiple(() =>
            {
                Assert.That(settings.Marketplace, Is.EqualTo("us"));
                Assert.That(settings.Provider, Is.EqualTo(TalearrAudibleProvider.Audible));
                Assert.That(settings.AudnexusFallback, Is.False, "Audnexus is opt in");
                Assert.That(settings.Enrich, Is.EqualTo(TalearrEnrichScope.Monitored));
                Assert.That(settings.SearchOnAdd, Is.True, "adding a recording searches right away");
                Assert.That(settings.KeepBoth, Is.True, "an unabridged arrival never replaces the abridged recording");
                Assert.That(settings.UpgradeGuard, Is.True);
                Assert.That(settings.MinBitrateGainKbps, Is.EqualTo(16));
                Assert.That(settings.RuntimeTolerancePct, Is.EqualTo(3));
                Assert.That(proxy.Values, Is.Empty, "defaults are not written until the user saves");
            });
        }

        [Test]
        public void should_round_trip_through_talearr_config_rows()
        {
            var (repository, proxy) = ConfigRepositoryProxy.Create();
            var service = new TalearrRecordingsSettingsService(repository, LogManager.GetCurrentClassLogger());

            service.Save(new TalearrRecordingsSettings
            {
                Marketplace = "UK",
                Provider = TalearrAudibleProvider.Audnexus,
                AudnexusFallback = true,
                Enrich = TalearrEnrichScope.All,
                SearchOnAdd = false,
                KeepBoth = false
            });

            var reloaded = new TalearrRecordingsSettingsService(repository, LogManager.GetCurrentClassLogger()).Get();

            Assert.Multiple(() =>
            {
                Assert.That(reloaded.Marketplace, Is.EqualTo("uk"));
                Assert.That(reloaded.Provider, Is.EqualTo(TalearrAudibleProvider.Audnexus));
                Assert.That(reloaded.AudnexusFallback, Is.True);
                Assert.That(reloaded.Enrich, Is.EqualTo(TalearrEnrichScope.All));
                Assert.That(reloaded.SearchOnAdd, Is.False);
                Assert.That(reloaded.KeepBoth, Is.False);
                Assert.That(proxy.Values.Keys.All(k => k.StartsWith("talearr.recordings.") || k.StartsWith("talearr.upgrade.")), Is.True);
                Assert.That(proxy.Values.Keys.All(k => k == k.ToLowerInvariant()), Is.True, "keys follow the grab policy's lowercase style");
                Assert.That(proxy.Values[TalearrRecordingsSettingsService.ProviderKey], Is.EqualTo("audnexus"));
            });
        }

        [Test]
        public void should_fall_back_to_defaults_for_unreadable_values()
        {
            var (repository, proxy) = ConfigRepositoryProxy.Create();
            proxy.Values[TalearrRecordingsSettingsService.MarketplaceKey] = "atlantis";
            proxy.Values[TalearrRecordingsSettingsService.ProviderKey] = "7";
            proxy.Values[TalearrRecordingsSettingsService.EnrichKey] = "sometimes";
            proxy.Values[TalearrRecordingsSettingsService.SearchOnAddKey] = "maybe";
            proxy.Values[TalearrRecordingsSettingsService.KeepBothKey] = "";

            var settings = new TalearrRecordingsSettingsService(repository, LogManager.GetCurrentClassLogger()).Get();

            Assert.Multiple(() =>
            {
                Assert.That(settings.Marketplace, Is.EqualTo("us"));
                Assert.That(settings.Provider, Is.EqualTo(TalearrAudibleProvider.Audible));
                Assert.That(settings.Enrich, Is.EqualTo(TalearrEnrichScope.Monitored));
                Assert.That(settings.SearchOnAdd, Is.True);
                Assert.That(settings.KeepBoth, Is.True);
            });
        }

        [Test]
        public void upgrade_guard_settings_round_trip_and_clamp()
        {
            var (repository, proxy) = ConfigRepositoryProxy.Create();
            var service = new TalearrRecordingsSettingsService(repository, LogManager.GetCurrentClassLogger());

            service.Save(new TalearrRecordingsSettings { UpgradeGuard = false, MinBitrateGainKbps = 32, RuntimeTolerancePct = 500 });
            var reloaded = new TalearrRecordingsSettingsService(repository, LogManager.GetCurrentClassLogger()).Get();

            Assert.Multiple(() =>
            {
                Assert.That(proxy.Values[TalearrRecordingsSettingsService.UpgradeGuardKey], Is.EqualTo("False"));
                Assert.That(proxy.Values[TalearrRecordingsSettingsService.MinBitrateGainKey], Is.EqualTo("32"));
                Assert.That(reloaded.UpgradeGuard, Is.False);
                Assert.That(reloaded.MinBitrateGainKbps, Is.EqualTo(32));
                Assert.That(reloaded.RuntimeTolerancePct, Is.EqualTo(TalearrRecordingsSettingsService.MaxRuntimeTolerancePct));
            });

            proxy.Values[TalearrRecordingsSettingsService.MinBitrateGainKey] = "-4";
            Assert.That(new TalearrRecordingsSettingsService(repository, LogManager.GetCurrentClassLogger()).Get().MinBitrateGainKbps, Is.EqualTo(16));
        }

        [Test]
        public void config_put_without_the_upgrade_fields_keeps_them()
        {
            var (repository, _) = ConfigRepositoryProxy.Create();
            var service = new TalearrRecordingsSettingsService(repository, LogManager.GetCurrentClassLogger());
            service.Save(new TalearrRecordingsSettings { UpgradeGuard = false, MinBitrateGainKbps = 24 });
            var controller = new TalearrRecordingsConfigController(service);

            // An older client (Allseerr) sends only the fields it knows.
            controller.SaveConfig(new TalearrRecordingsConfigResource { Id = 1, Marketplace = "us", KeepBoth = true, SearchOnAdd = true });

            var settings = service.Get();
            Assert.That(settings.UpgradeGuard, Is.False);
            Assert.That(settings.MinBitrateGainKbps, Is.EqualTo(24));
            Assert.That(settings.RuntimeTolerancePct, Is.EqualTo(3));
        }

        [Test]
        public void should_store_an_unknown_marketplace_as_the_default()
        {
            var (repository, proxy) = ConfigRepositoryProxy.Create();
            var service = new TalearrRecordingsSettingsService(repository, LogManager.GetCurrentClassLogger());

            var saved = service.Save(new TalearrRecordingsSettings { Marketplace = "zz" });

            Assert.That(saved.Marketplace, Is.EqualTo("us"));
            Assert.That(proxy.Values[TalearrRecordingsSettingsService.MarketplaceKey], Is.EqualTo("us"));
        }

        [Test]
        public void resolve_should_survive_a_missing_or_failing_service()
        {
            Assert.That(TalearrRecordingsSettingsService.Resolve(null).Marketplace, Is.EqualTo("us"));
        }

        [Test]
        public void config_resource_should_list_the_marketplaces()
        {
            var resource = TalearrRecordingsConfigController.ToResource(TalearrRecordingsSettings.Default);

            Assert.That(resource.Id, Is.EqualTo(1));
            Assert.That(resource.AvailableMarketplaces.Select(m => m.Code), Is.EqualTo(new[] { "us", "uk", "ca", "au", "fr", "de", "it", "es", "jp", "in" }));
            Assert.That(resource.AvailableMarketplaces.Single(m => m.Code == "uk").Domain, Is.EqualTo("audible.co.uk"));
        }

        [TestCase("us", "https://api.audible.com", "https://www.audible.com/pd/B002V5BSLC")]
        [TestCase("uk", "https://api.audible.co.uk", "https://www.audible.co.uk/pd/B002V5BSLC")]
        [TestCase("GB", "https://api.audible.co.uk", "https://www.audible.co.uk/pd/B002V5BSLC")]
        [TestCase("jp", "https://api.audible.co.jp", "https://www.audible.co.jp/pd/B002V5BSLC")]
        [TestCase(null, "https://api.audible.com", "https://www.audible.com/pd/B002V5BSLC")]
        public void marketplaces_should_map_to_their_hosts(string code, string api, string link)
        {
            var marketplace = TalearrAudibleMarketplaces.Get(code);

            Assert.That(marketplace.ApiBaseUrl, Is.EqualTo(api));
            Assert.That(marketplace.ProductUrl("B002V5BSLC"), Is.EqualTo(link));
        }
    }
}
