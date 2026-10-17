using System;
using System.Linq;
using System.Net;
using NLog;
using NUnit.Framework;
using NzbDrone.Core.Indexers;
using NzbDrone.Core.Indexers.MyAnonaMouse;
using NzbDrone.Core.Messaging.Commands;

namespace Chaptarr.Core.Test.Talearr.Mam
{
    [TestFixture]
    public class MamSessionMonitorFixture
    {
        private FakeMamIndexerFactory _factory;
        private IIndexerFactory _factoryProxy;
        private FakeEventAggregator _events;
        private MyAnonaMouseSessionMonitor _monitor;

        [SetUp]
        public void SetUp()
        {
            _factory = FakeMamIndexerFactory.Create(out _factoryProxy);
            _events = new FakeEventAggregator();
            _monitor = new MyAnonaMouseSessionMonitor(new Lazy<IIndexerFactory>(() => _factoryProxy), _events, LogManager.GetCurrentClassLogger());
        }

        private IndexerDefinition AddDefinition(MyAnonaMouseSettings settings, int id = 9)
        {
            var definition = new IndexerDefinition { Id = id, Name = "MAM", EnableAutomaticSearch = true, Settings = settings };
            _factory.Definitions.Add(definition);
            return definition;
        }

        [Test]
        public void rotated_mam_id_should_be_saved_to_the_indexer()
        {
            var definition = AddDefinition(new MyAnonaMouseSettings { MamId = "OLD" });

            _monitor.RecordRotatedCookie(9, MyAnonaMouseCookieField.MamId, "OLD", "NEW");

            Assert.That(((MyAnonaMouseSettings)definition.Settings).MamId, Is.EqualTo("NEW"));
            Assert.That(_factory.Updated.Single(), Is.SameAs(definition));
        }

        [Test]
        public void rotated_seedbox_mam_id_should_be_saved_to_its_own_field()
        {
            var definition = AddDefinition(new MyAnonaMouseSettings { MamId = "MAIN", SeedboxMamId = "SEED" });

            _monitor.RecordRotatedCookie(9, MyAnonaMouseCookieField.SeedboxMamId, "SEED", "SEEDNEW");

            var settings = (MyAnonaMouseSettings)definition.Settings;
            Assert.That(settings.SeedboxMamId, Is.EqualTo("SEEDNEW"));
            Assert.That(settings.MamId, Is.EqualTo("MAIN"));
        }

        [Test]
        public void rotation_should_not_overwrite_a_mam_id_the_user_changed()
        {
            AddDefinition(new MyAnonaMouseSettings { MamId = "PASTED" });

            _monitor.RecordRotatedCookie(9, MyAnonaMouseCookieField.MamId, "OLD", "NEW");

            Assert.That(_factory.Updated, Is.Empty);
        }

        [Test]
        public void rejection_should_publish_once_and_clear_when_accepted_again()
        {
            _monitor.RecordSessionRejected(9, "OLD", "search");
            _monitor.RecordSessionRejected(9, "OLD", "search");

            Assert.That(_monitor.Get(9).RejectedMamId, Is.EqualTo("OLD"));
            Assert.That(_events.Published.Count, Is.EqualTo(1));

            _monitor.RecordSessionAccepted(9, "OLD");

            Assert.That(_monitor.Get(9).RejectedMamId, Is.Null);
            Assert.That(_events.Published.Count, Is.EqualTo(2));
        }

        [Test]
        public void accepting_a_different_mam_id_should_not_clear_the_old_rejection()
        {
            _monitor.RecordSessionRejected(9, "OLD", "search");

            _monitor.RecordSessionAccepted(9, "OTHER");

            Assert.That(_monitor.Get(9).RejectedMamId, Is.EqualTo("OLD"));
        }

        [Test]
        public void seedbox_result_should_publish_only_when_it_changes()
        {
            _monitor.RecordSeedboxResult(9, "MAIN", new MyAnonaMouseSeedboxResult { Outcome = MyAnonaMouseSeedboxOutcome.NoChange, Message = "No change" });
            _monitor.RecordSeedboxResult(9, "MAIN", new MyAnonaMouseSeedboxResult { Outcome = MyAnonaMouseSeedboxOutcome.NoChange, Message = "No change" });
            _monitor.RecordSeedboxResult(9, "MAIN", new MyAnonaMouseSeedboxResult { Outcome = MyAnonaMouseSeedboxOutcome.NetworkMismatch, Message = "Invalid session - ASN mismatch" });

            Assert.That(_events.Published.Count, Is.EqualTo(2));
            Assert.That(_monitor.Get(9).Seedbox.Outcome, Is.EqualTo(MyAnonaMouseSeedboxOutcome.NetworkMismatch));
        }
    }

    [TestFixture]
    public class MamDynamicSeedboxServiceFixture
    {
        private FakeMamHttpClient _client;
        private IIndexerHttpClientFactory _httpFactory;
        private FakeMamIndexerFactory _indexers;
        private RecordingSessionMonitor _monitor;
        private MyAnonaMouseDynamicSeedboxService _service;

        private const string NoChange = "{\"Success\":true,\"msg\":\"No change\",\"ip\":\"185.159.157.20\"}";

        [SetUp]
        public void SetUp()
        {
            _client = FakeMamHttpClient.Create(out var client);
            FakeMamHttpClientFactory.Create(client, out _httpFactory);
            _indexers = FakeMamIndexerFactory.Create(out var indexerFactory);
            _monitor = new RecordingSessionMonitor();
            _service = new MyAnonaMouseDynamicSeedboxService(indexerFactory, _monitor, LogManager.GetCurrentClassLogger());
        }

        private void AddIndexer(MyAnonaMouseSettings settings, int id = 9)
        {
            _indexers.Providers.Add(MamTestIndexers.Create(settings, _httpFactory, _monitor, id));
        }

        [Test]
        public void disabled_indexers_should_never_call_mam()
        {
            AddIndexer(new MyAnonaMouseSettings { MamId = "MAIN" });

            _service.Run(force: true, source: "test");

            Assert.That(_client.Requests, Is.Empty);
        }

        [Test]
        public void startup_run_should_update_and_record_the_result()
        {
            AddIndexer(new MyAnonaMouseSettings { MamId = "MAIN", DynamicSeedboxEnabled = true });
            _client.Reply(HttpStatusCode.OK, "application/json", NoChange);

            _service.HandleAsync(new NzbDrone.Core.Lifecycle.ApplicationStartedEvent());

            Assert.That(_client.Requests.Count, Is.EqualTo(1));
            Assert.That(_monitor.Calls, Is.EquivalentTo(new[] { "seedbox:9:MAIN:NoChange" }));
        }

        [Test]
        public void scheduled_runs_should_respect_the_interval()
        {
            AddIndexer(new MyAnonaMouseSettings { MamId = "MAIN", DynamicSeedboxEnabled = true, DynamicSeedboxInterval = 30 });
            var start = new DateTime(2026, 10, 17, 12, 0, 0, DateTimeKind.Utc);
            _client.Reply(HttpStatusCode.OK, "application/json", NoChange);
            _client.Reply(HttpStatusCode.OK, "application/json", NoChange);

            _service.Run(force: false, source: "test", nowUtc: start);
            _service.Run(force: false, source: "test", nowUtc: start.AddMinutes(5));
            _service.Run(force: false, source: "test", nowUtc: start.AddMinutes(25));
            _service.Run(force: false, source: "test", nowUtc: start.AddMinutes(30));

            Assert.That(_client.Requests.Count, Is.EqualTo(2));
        }

        [Test]
        public void manual_run_should_ignore_the_interval()
        {
            AddIndexer(new MyAnonaMouseSettings { MamId = "MAIN", DynamicSeedboxEnabled = true });
            _client.Reply(HttpStatusCode.OK, "application/json", NoChange);
            _client.Reply(HttpStatusCode.OK, "application/json", "{\"Success\":false,\"msg\":\"Last change too recent\"}");

            _service.Run(force: false, source: "test");
            _service.Execute(new UpdateMyAnonaMouseDynamicSeedboxCommand { Trigger = CommandTrigger.Manual });

            Assert.That(_client.Requests.Count, Is.EqualTo(2));
            Assert.That(_monitor.Calls.Last(), Is.EqualTo("seedbox:9:MAIN:TooRecent"));
        }

        [Test]
        public void network_errors_should_be_recorded_as_failed()
        {
            AddIndexer(new MyAnonaMouseSettings { MamId = "MAIN", DynamicSeedboxEnabled = true });
            _client.Responses.Enqueue(_ => throw new WebException("proxy refused the connection"));

            _service.Run(force: true, source: "test");

            Assert.That(_monitor.Calls.Single(), Is.EqualTo("seedbox:9:MAIN:Failed"));
            Assert.That(_monitor.Get(9).Seedbox.Message, Does.Contain("proxy refused"));
        }
    }
}
