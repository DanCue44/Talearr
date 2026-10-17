using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Threading.Tasks;
using NLog;
using NUnit.Framework;
using NzbDrone.Common.Http;
using NzbDrone.Core.Indexers;
using NzbDrone.Core.Indexers.Exceptions;
using NzbDrone.Core.Indexers.MyAnonaMouse;

namespace Chaptarr.Core.Test.Talearr.Mam
{
    [TestFixture]
    public class MamIndexerSessionFixture
    {
        private const string SearchUrl = "https://www.myanonamouse.net/tor/js/loadSearchJSONbasic.php";

        private sealed class ProbeMam : NzbDrone.Core.Indexers.MyAnonaMouse.MyAnonaMouse
        {
            public ProbeMam(IIndexerHttpClientFactory factory, IMyAnonaMouseSessionMonitor monitor, MyAnonaMouseSettings settings)
                : base(factory, null, null, null, null, LogManager.GetCurrentClassLogger(), monitor)
            {
                Definition = new IndexerDefinition { Id = 9, Name = "MAM", EnableAutomaticSearch = true, Settings = settings };
            }

            public Task<IndexerResponse> Search()
            {
                var request = new HttpRequest(SearchUrl);
                request.Cookies["mam_id"] = Settings.MamId;
                return FetchIndexerResponse(new IndexerRequest(request));
            }
        }

        private FakeMamHttpClient _client;
        private FakeMamHttpClientFactory _factory;
        private IIndexerHttpClientFactory _factoryProxy;
        private RecordingSessionMonitor _monitor;

        [SetUp]
        public void SetUp()
        {
            _client = FakeMamHttpClient.Create(out var client);
            _factory = FakeMamHttpClientFactory.Create(client, out _factoryProxy);
            _monitor = new RecordingSessionMonitor();
        }

        [Test]
        public void search_with_expired_session_should_report_it_and_throw_a_clear_error()
        {
            _client.Reply(HttpStatusCode.Forbidden, "text/html", "Error, you are not signed in <br />");
            var indexer = new ProbeMam(_factoryProxy, _monitor, new MyAnonaMouseSettings { MamId = "OLD" });

            var ex = Assert.ThrowsAsync<IndexerException>(() => indexer.Search());

            Assert.That(ex.Message, Is.EqualTo(NzbDrone.Core.Indexers.MyAnonaMouse.MyAnonaMouse.SessionExpiredMessage));
            Assert.That(_monitor.Calls, Is.EquivalentTo(new[] { "rejected:9:OLD:search" }));
        }

        [Test]
        public void search_with_rotated_cookie_should_keep_the_new_mam_id()
        {
            _client.Reply(HttpStatusCode.OK, "application/json", "{\"data\":[],\"found\":0}", "mam_id=NEW; path=/; domain=.myanonamouse.net");
            var settings = new MyAnonaMouseSettings { MamId = "OLD" };
            var indexer = new ProbeMam(_factoryProxy, _monitor, settings);

            indexer.Search().GetAwaiter().GetResult();

            Assert.That(settings.MamId, Is.EqualTo("NEW"));
            Assert.That(_monitor.Calls, Does.Contain("rotated:9:MamId:OLD->NEW"));
            Assert.That(_monitor.Calls, Does.Contain("accepted:9:OLD"));
        }

        [Test]
        public void healthy_search_should_mark_the_session_accepted()
        {
            _client.Reply(HttpStatusCode.OK, "application/json", "{\"data\":[],\"found\":0}");
            var indexer = new ProbeMam(_factoryProxy, _monitor, new MyAnonaMouseSettings { MamId = "OK" });

            indexer.Search().GetAwaiter().GetResult();

            Assert.That(_monitor.Calls, Is.EquivalentTo(new[] { "accepted:9:OK" }));
        }

        [Test]
        public void account_refresh_with_expired_session_should_report_it()
        {
            _client.Reply(HttpStatusCode.Forbidden, "text/html", "Error, you are not signed in");
            var indexer = MamTestIndexers.Create(new MyAnonaMouseSettings { MamId = "OLD" }, _factoryProxy, _monitor);

            var ex = Assert.ThrowsAsync<InvalidOperationException>(() => indexer.RefreshAccountStatus());

            Assert.That(ex.Message, Is.EqualTo(NzbDrone.Core.Indexers.MyAnonaMouse.MyAnonaMouse.SessionExpiredMessage));
            Assert.That(_monitor.Calls.Single(), Does.StartWith("rejected:9:OLD"));
        }

        [Test]
        public void seedbox_update_should_call_the_endpoint_with_the_main_mam_id_by_default()
        {
            _client.Reply(HttpStatusCode.OK, "application/json", "{\"Success\":true,\"msg\":\"No change\",\"ip\":\"185.159.157.20\",\"ASN\":209103}");
            var indexer = MamTestIndexers.Create(new MyAnonaMouseSettings { MamId = "MAIN" }, _factoryProxy, _monitor);

            var result = indexer.UpdateDynamicSeedboxAsync().GetAwaiter().GetResult();

            var request = _client.Requests.Single();
            Assert.That(request.Url.FullUri, Is.EqualTo(MyAnonaMouseDynamicSeedboxParser.Endpoint));
            Assert.That(request.Cookies["mam_id"], Is.EqualTo("MAIN"));
            Assert.That(request.SuppressHttpError, Is.True);
            Assert.That(result.Outcome, Is.EqualTo(MyAnonaMouseSeedboxOutcome.NoChange));
            Assert.That(_monitor.Calls, Is.Empty);
        }

        [Test]
        public void seedbox_update_should_prefer_the_seedbox_mam_id_and_keep_its_rotation()
        {
            _client.Reply(HttpStatusCode.OK, "application/json", "{\"Success\":true,\"msg\":\"Completed\"}", "mam_id=SEEDNEW; path=/");
            var settings = new MyAnonaMouseSettings { MamId = "MAIN", SeedboxMamId = "SEED" };
            var indexer = MamTestIndexers.Create(settings, _factoryProxy, _monitor);

            var result = indexer.UpdateDynamicSeedboxAsync().GetAwaiter().GetResult();

            Assert.That(_client.Requests.Single().Cookies["mam_id"], Is.EqualTo("SEED"));
            Assert.That(result.Outcome, Is.EqualTo(MyAnonaMouseSeedboxOutcome.Updated));
            Assert.That(settings.SeedboxMamId, Is.EqualTo("SEEDNEW"));
            Assert.That(settings.MamId, Is.EqualTo("MAIN"));
            Assert.That(_monitor.Calls, Is.EquivalentTo(new[] { "rotated:9:SeedboxMamId:SEED->SEEDNEW" }));
        }

        [Test]
        public void seedbox_refusal_should_not_mark_the_search_session_expired()
        {
            _client.Reply(HttpStatusCode.Forbidden, "application/json", "{\"Success\":false,\"msg\":\"Invalid session - ASN mismatch\"}");
            var indexer = MamTestIndexers.Create(new MyAnonaMouseSettings { MamId = "MAIN" }, _factoryProxy, _monitor);

            var result = indexer.UpdateDynamicSeedboxAsync().GetAwaiter().GetResult();

            Assert.That(result.Outcome, Is.EqualTo(MyAnonaMouseSeedboxOutcome.NetworkMismatch));
            Assert.That(_monitor.Calls, Is.Empty);
        }

        [Test]
        public void seedbox_update_without_any_mam_id_should_not_call_mam()
        {
            var indexer = MamTestIndexers.Create(new MyAnonaMouseSettings(), _factoryProxy, _monitor);

            var result = indexer.UpdateDynamicSeedboxAsync().GetAwaiter().GetResult();

            Assert.That(result.Outcome, Is.EqualTo(MyAnonaMouseSeedboxOutcome.InvalidSession));
            Assert.That(_client.Requests, Is.Empty);
        }

        [TestCase(0, 4, 4)]
        [TestCase(7, 4, 7)]
        [TestCase(-1, 4, IndexerDefinition.NoProxyOverride)]
        public void seedbox_proxy_should_pick_the_http_client(int seedboxProxyId, int indexerProxyId, int expectedProxyId)
        {
            var indexer = MamTestIndexers.Create(new MyAnonaMouseSettings { MamId = "MAIN", SeedboxProxyId = seedboxProxyId }, _factoryProxy, _monitor, proxyId: indexerProxyId);

            indexer.GetSeedboxHttpClient();

            Assert.That(_factory.RequestedProxyIds.Last(), Is.EqualTo(expectedProxyId));
        }

        [Test]
        public void seedbox_proxy_options_should_include_same_as_indexer_and_direct()
        {
            var indexer = MamTestIndexers.Create(new MyAnonaMouseSettings(), _factoryProxy, _monitor);

            var result = indexer.RequestAction("getSeedboxProxies", new Dictionary<string, string>());
            var options = ((IEnumerable<object>)result.GetType().GetProperty("options").GetValue(result)).ToList();
            var values = options.Select(o => (int)o.GetType().GetProperty("Value").GetValue(o)).ToList();

            Assert.That(values, Is.EqualTo(new[] { 0, IndexerDefinition.NoProxyOverride }));
        }

        [Test]
        public void defaults_should_prefer_freeleech_and_leave_the_rest_off()
        {
            var settings = new MyAnonaMouseSettings();

            Assert.That(settings.PreferFreeleech, Is.True);
            Assert.That(settings.FreeleechOnly, Is.False);
            Assert.That(settings.DynamicSeedboxEnabled, Is.False);
            Assert.That(settings.DynamicSeedboxInterval, Is.EqualTo(30));
            Assert.That(settings.SeedboxProxyId, Is.EqualTo(0));
        }

        [TestCase(4, false)]
        [TestCase(5, true)]
        [TestCase(30, true)]
        [TestCase(1441, false)]
        public void interval_should_be_validated_when_enabled(int minutes, bool valid)
        {
            var settings = new MyAnonaMouseSettings { MamId = "x", DynamicSeedboxEnabled = true, DynamicSeedboxInterval = minutes };

            Assert.That(settings.Validate().IsValid, Is.EqualTo(valid));
        }
    }
}
