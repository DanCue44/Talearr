using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Text;
using System.Threading.Tasks;
using MQTTnet.Server;
using Newtonsoft.Json.Linq;
using NLog;
using NUnit.Framework;
using NzbDrone.Core.Books;
using NzbDrone.Core.Configuration;
using NzbDrone.Core.Notifications;
using NzbDrone.Core.Notifications.Mqtt;
using NzbDrone.Core.Parser.Model;
using NzbDrone.Core.Qualities;

namespace Chaptarr.Core.Test.Notifications.Mqtt
{
    [TestFixture]
    public class MqttFixture
    {
        private MqttServer _server;
        private int _port;
        private ConcurrentQueue<(string Topic, string Payload, bool Retain)> _received;

        [SetUp]
        public async Task SetUp()
        {
            _received = new ConcurrentQueue<(string, string, bool)>();
            _port = FreePort();

            var factory = new MqttServerFactory();
            var options = factory.CreateServerOptionsBuilder()
                .WithDefaultEndpoint()
                .WithDefaultEndpointPort(_port)
                .Build();

            _server = factory.CreateMqttServer(options);
            _server.InterceptingPublishAsync += e =>
            {
                _received.Enqueue((e.ApplicationMessage.Topic,
                    Encoding.UTF8.GetString(e.ApplicationMessage.Payload),
                    e.ApplicationMessage.Retain));
                return Task.CompletedTask;
            };

            await _server.StartAsync();
        }

        [TearDown]
        public async Task TearDown()
        {
            await _server.StopAsync();
            _server.Dispose();
        }

        [Test]
        public void should_publish_grab_event_with_webhook_payload()
        {
            var subject = CreateSubject(new MqttSettings { Host = "127.0.0.1", Port = _port });

            subject.OnGrab(new GrabMessage
            {
                Author = new Author { Id = 4, Name = "Jennifer Lynn Barnes" },
                RemoteBook = new RemoteBook
                {
                    Books = new List<Book> { new Book { Id = 7, Title = "Bad Blood" } },
                    Release = new ReleaseInfo { Title = "Bad Blood epub", Indexer = "Test" },
                    ParsedBookInfo = new ParsedBookInfo()
                },
                Quality = new QualityModel(Quality.EPUB),
                DownloadClientName = "qBittorrent"
            });

            var message = _received.Single();
            Assert.That(message.Topic, Is.EqualTo("talearr/event/Grab"));
            Assert.That(message.Retain, Is.False);

            var json = JObject.Parse(message.Payload);
            Assert.That((string)json["eventType"], Is.EqualTo("Grab"));
            Assert.That((string)json["author"]["name"], Is.EqualTo("Jennifer Lynn Barnes"));
            Assert.That((string)json["books"][0]["title"], Is.EqualTo("Bad Blood"));
        }

        [Test]
        public void should_publish_upgrades_on_their_own_topic()
        {
            var subject = CreateSubject(new MqttSettings { Host = "127.0.0.1", Port = _port, TopicPrefix = "home/books" });

            subject.OnReleaseImport(new BookDownloadMessage
            {
                Author = new Author { Name = "Ali Hazelwood" },
                Book = new Book { Title = "Deep End" },
                BookFiles = new List<NzbDrone.Core.MediaFiles.BookFile> { new NzbDrone.Core.MediaFiles.BookFile { Path = "/books/Deep End.epub", Quality = new QualityModel(Quality.EPUB) } },
                OldFiles = new List<NzbDrone.Core.MediaFiles.BookFile> { new NzbDrone.Core.MediaFiles.BookFile { Path = "/books/Deep End.mobi", Quality = new QualityModel(Quality.MOBI) } }
            });

            var message = _received.Single();
            Assert.That(message.Topic, Is.EqualTo("home/books/event/Upgrade"));
            Assert.That((bool)JObject.Parse(message.Payload)["isUpgrade"], Is.True);
        }

        [Test]
        public void should_not_publish_events_when_turned_off()
        {
            var subject = CreateSubject(new MqttSettings { Host = "127.0.0.1", Port = _port, PublishEvents = false });

            subject.OnBookAdded(new Book { Title = "Bride" });

            Assert.That(_received, Is.Empty);
        }

        [Test]
        public void test_should_publish_discovery_sensors_and_availability()
        {
            var subject = CreateSubject(new MqttSettings { Host = "127.0.0.1", Port = _port });

            var result = subject.Test();

            Assert.That(result.IsValid, Is.True);

            var topics = _received.Select(m => m.Topic).ToList();
            Assert.That(topics, Does.Contain("talearr/event/Test"));
            Assert.That(topics, Does.Contain("homeassistant/sensor/talearr/queue/config"));
            Assert.That(topics, Does.Contain("homeassistant/sensor/talearr/last_imported/config"));
            Assert.That(topics, Does.Contain("talearr/sensors"));
            Assert.That(topics, Does.Contain("talearr/status"));

            var state = JObject.Parse(_received.Single(m => m.Topic == "talearr/sensors").Payload);
            Assert.That((int)state["queue"], Is.EqualTo(2));
            Assert.That((int)state["missing"], Is.EqualTo(5));
            Assert.That((string)state["lastImported"]["title"], Is.EqualTo("Caught Up"));
            Assert.That(_received.Single(m => m.Topic == "talearr/sensors").Retain, Is.True);

            var discovery = JObject.Parse(_received.Single(m => m.Topic == "homeassistant/sensor/talearr/queue/config").Payload);
            Assert.That((string)discovery["state_topic"], Is.EqualTo("talearr/sensors"));
            Assert.That((string)discovery["unique_id"], Is.EqualTo("talearr_queue"));
            Assert.That((string)discovery["device"]["identifiers"][0], Is.EqualTo("talearr"));
        }

        [Test]
        public void test_should_fail_when_broker_is_unreachable()
        {
            var subject = CreateSubject(new MqttSettings { Host = "127.0.0.1", Port = FreePort() });

            var result = subject.Test();

            Assert.That(result.IsValid, Is.False);
        }

        [TestCase("talearr", true)]
        [TestCase("home/talearr", true)]
        [TestCase("home/+/talearr", false)]
        [TestCase("talearr/#", false)]
        [TestCase("/talearr", false)]
        [TestCase("", false)]
        public void should_validate_topic_prefix(string prefix, bool valid)
        {
            Assert.That(MqttTopics.IsValidPrefix(prefix), Is.EqualTo(valid));
        }

        [Test]
        public void should_give_each_prefix_its_own_home_assistant_device()
        {
            var settings = new MqttSettings { TopicPrefix = "home/Talearr-2" };

            Assert.That(MqttTopics.NodeId(settings), Is.EqualTo("home_talearr_2"));
            Assert.That(MqttTopics.Discovery(settings, MqttTopics.Sensors[0]), Is.EqualTo("homeassistant/sensor/home_talearr_2/queue/config"));
        }

        private static NzbDrone.Core.Notifications.Mqtt.Mqtt CreateSubject(MqttSettings settings)
        {
            var logger = LogManager.GetCurrentClassLogger();
            var subject = new NzbDrone.Core.Notifications.Mqtt.Mqtt(
                new MqttProxy(logger),
                new FakeSensorService(),
                DispatchProxy.Create<IConfigFileProvider, DefaultsProxy>(),
                logger);

            subject.Definition = new NotificationDefinition { Id = 1, Name = "MQTT", Settings = settings };

            return subject;
        }

        private static int FreePort()
        {
            var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            var port = ((IPEndPoint)listener.LocalEndpoint).Port;
            listener.Stop();
            return port;
        }

        private class FakeSensorService : IMqttSensorService
        {
            private readonly MqttSensorService _messages = new MqttSensorService(null, null, null, null, null, null, LogManager.GetCurrentClassLogger());

            public MqttSensorState BuildState()
            {
                return new MqttSensorState
                {
                    Queue = 2,
                    Missing = 5,
                    HealthIssues = 1,
                    LastImported = new MqttLastImported { BookId = 7485, Title = "Caught Up", Author = "Navessa Allen", MediaType = "ebook", ImportedAt = DateTime.UtcNow },
                    UpdatedAt = DateTime.UtcNow
                };
            }

            public List<MqttMessage> BuildMessages(MqttSettings settings, MqttSensorState state)
            {
                return _messages.BuildMessages(settings, state);
            }

            public void PublishNow(MqttSettings settings)
            {
            }
        }

        public class DefaultsProxy : DispatchProxy
        {
            protected override object Invoke(MethodInfo targetMethod, object[] args)
            {
                if (targetMethod.Name == "get_InstanceName")
                {
                    return "Talearr";
                }

                var type = targetMethod.ReturnType;
                return type.IsValueType && type != typeof(void) ? Activator.CreateInstance(type) : null;
            }
        }
    }
}
