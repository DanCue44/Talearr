using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using NzbDrone.Common.Serializer;
using NzbDrone.Core.Books;
using NzbDrone.Core.Configuration;
using NzbDrone.Core.Download;
using NzbDrone.Core.HealthCheck;
using NzbDrone.Core.HealthCheck.Checks;
using NzbDrone.Core.MediaCover;
using NzbDrone.Core.MediaFiles;
using NzbDrone.Core.Notifications;
using NzbDrone.Core.Notifications.Discord;
using NzbDrone.Core.Notifications.Discord.Payloads;
using NzbDrone.Core.Notifications.Pushover;
using NzbDrone.Core.Notifications.Webhook;
using NzbDrone.Core.Parser.Model;
using NzbDrone.Core.Qualities;

using HealthCheckModel = NzbDrone.Core.HealthCheck.HealthCheck;

namespace Chaptarr.Core.Test.Talearr.Notifications
{
    [TestFixture]
    public class TalearrNotificationPayloadFixture
    {
        private RecordingProxy _webhookProxy;
        private Webhook _webhook;

        [SetUp]
        public void SetUp()
        {
            var proxy = RecordingProxy.Create<IWebhookProxy>(out _webhookProxy);
            var config = RecordingProxy.Create<IConfigFileProvider>(out var configProxy);
            configProxy.Handlers["get_InstanceName"] = _ => "Talearr";

            _webhook = new Webhook(proxy, config)
            {
                Definition = new NotificationDefinition { Settings = new WebhookSettings() }
            };
        }

        private JObject LastWebhook()
        {
            var payload = _webhookProxy.Args("SendWebhook").Last();
            return JObject.Parse(payload.ToJson());
        }

        private static GrabMessage Grab(bool isBundle, bool isFallback)
        {
            var author = NotificationTestData.Author();
            var book = NotificationTestData.Audiobook(author);

            return new GrabMessage
            {
                Message = "Frank Herbert - Dune - [M4B]",
                Author = author,
                Quality = new QualityModel(Quality.M4B),
                DownloadClientName = "qBittorrent",
                RemoteBook = new RemoteBook
                {
                    Author = author,
                    Books = new List<Book> { book },
                    ParsedBookInfo = new ParsedBookInfo { Quality = new QualityModel(Quality.M4B) },
                    Release = new ReleaseInfo { Title = "Dune Box Set", Indexer = "MAM", Size = 1048576 },
                    IsBundle = isBundle,
                    IsBundleFallback = isFallback
                }
            };
        }

        [Test]
        public void grab_payload_keeps_readarr_fields_and_adds_book_details()
        {
            _webhook.OnGrab(Grab(true, true));

            var json = LastWebhook();
            Assert.That((string)json["eventType"], Is.EqualTo("Grab"));
            Assert.That((string)json["instanceName"], Is.EqualTo("Talearr"));
            Assert.That(json["author"]["name"], Is.Not.Null);
            Assert.That((string)json["release"]["quality"], Is.EqualTo("M4B"));
            Assert.That((string)json["release"]["releaseTitle"], Is.EqualTo("Dune Box Set"));
            Assert.That((bool)json["release"]["isBundle"], Is.True);
            Assert.That((bool)json["release"]["isBundleFallback"], Is.True);

            var book = json["books"][0];
            Assert.That((string)book["title"], Is.EqualTo("Dune"));
            Assert.That((string)book["coverUrl"], Is.EqualTo("https://covers.example/dune.jpg"));
            Assert.That((string)book["mediaType"], Is.EqualTo("audiobook"));
            Assert.That((string)book["narrator"], Is.EqualTo("Scott Brick, Orlagh Cassidy"));
            Assert.That((string)book["series"], Is.EqualTo("Dune #1"));
        }

        [Test]
        public void blocked_import_is_a_manual_interaction_webhook()
        {
            var author = NotificationTestData.Author();
            var book = NotificationTestData.Audiobook(author);

            _webhook.OnImportFailure(new BookDownloadMessage
            {
                Message = "Talearr failed to Import all files for Dune",
                Author = author,
                Book = book,
                Books = new List<Book> { book },
                SourceTitle = "Dune",
                DownloadId = "abc",
                ManualInteractionRequired = true,
                StatusMessages = new List<string> { "Dune.m4b: Not an upgrade" }
            });

            var json = LastWebhook();
            Assert.That((string)json["eventType"], Is.EqualTo("ManualInteractionRequired"));
            Assert.That((string)json["downloadId"], Is.EqualTo("abc"));
            Assert.That(json["statusMessages"].Values<string>(), Is.EqualTo(new[] { "Dune.m4b: Not an upgrade" }));
            Assert.That((string)json["books"][0]["title"], Is.EqualTo("Dune"));
        }

        [Test]
        public void plain_import_failure_and_download_failure_webhooks()
        {
            _webhook.OnImportFailure(new BookDownloadMessage { Message = "failed" });
            Assert.That((string)LastWebhook()["eventType"], Is.EqualTo("ImportFailure"));

            _webhook.OnDownloadFailure(new DownloadFailedMessage { Message = "gone", SourceTitle = "Dune", Quality = new QualityModel(Quality.EPUB) });
            var json = LastWebhook();
            Assert.That((string)json["eventType"], Is.EqualTo("DownloadFailure"));
            Assert.That((string)json["quality"], Is.EqualTo("EPUB"));
            Assert.That((string)json["releaseTitle"], Is.EqualTo("Dune"));
            Assert.That((string)json["message"], Is.EqualTo("gone"));
        }

        [Test]
        public void health_restored_webhook_uses_the_health_shape()
        {
            _webhook.OnHealthRestored(new HealthCheckModel(typeof(TalearrMyAnonaMouseSessionCheck), HealthCheckResult.Error, "MAM session expired"));

            var json = LastWebhook();
            Assert.That((string)json["eventType"], Is.EqualTo("HealthRestored"));
            Assert.That((string)json["type"], Is.EqualTo("TalearrMyAnonaMouseSessionCheck"));
            Assert.That((string)json["message"], Is.EqualTo("MAM session expired"));
            Assert.That((string)json["level"], Is.EqualTo("error"));
        }

        [Test]
        public void existing_event_type_names_do_not_move()
        {
            // Readarr consumers switch on these strings and on their numeric order.
            Assert.That(((WebhookEventType)0).ToString(), Is.EqualTo("Test"));
            Assert.That(((WebhookEventType)11).ToString(), Is.EqualTo("ApplicationUpdate"));
            Assert.That((int)WebhookEventType.HealthRestored, Is.GreaterThan((int)WebhookEventType.ApplicationUpdate));
        }

        [Test]
        public void webhook_and_notifiarr_now_support_the_parity_events()
        {
            Assert.That(_webhook.SupportsOnHealthRestored, Is.True);
            Assert.That(_webhook.SupportsOnDownloadFailure, Is.True);
            Assert.That(_webhook.SupportsOnImportFailure, Is.True);
        }

        [Test]
        public void discord_grab_has_cover_format_narrator_and_bundle_title()
        {
            var proxy = RecordingProxy.Create<IDiscordProxy>(out var recorder);
            var discord = new Discord(proxy) { Definition = new NotificationDefinition { Settings = new DiscordSettings() } };

            discord.OnGrab(Grab(true, true));

            var embed = ((DiscordPayload)recorder.Args("SendPayload").Single()).Embeds.Single();
            Assert.That(embed.Title, Is.EqualTo("Bundle Grabbed (last resort)"));
            Assert.That(embed.Thumbnail.Url, Is.EqualTo("https://covers.example/dune.jpg"));

            var fields = embed.Fields.ToDictionary(f => f.Name, f => f.Value);
            Assert.That(fields["Format"], Is.EqualTo("Audiobook (M4B)"));
            Assert.That(fields["Narrator"], Is.EqualTo("Scott Brick, Orlagh Cassidy"));
            Assert.That(fields["Series"], Is.EqualTo("Dune #1"));
            Assert.That(fields["Indexer"], Is.EqualTo("MAM"));
            Assert.That(fields.Values.All(v => !string.IsNullOrWhiteSpace(v)), Is.True);
        }

        [Test]
        public void discord_upgrade_is_labelled_and_health_restored_is_supported()
        {
            var proxy = RecordingProxy.Create<IDiscordProxy>(out var recorder);
            var discord = new Discord(proxy) { Definition = new NotificationDefinition { Settings = new DiscordSettings() } };
            var author = NotificationTestData.Author();
            var book = NotificationTestData.Audiobook(author);

            discord.OnReleaseImport(new BookDownloadMessage
            {
                Message = "Frank Herbert - Dune (1 Files Imported)",
                Author = author,
                Book = book,
                BookFiles = new List<BookFile> { new BookFile { Quality = new QualityModel(Quality.M4B), Size = 1000 } },
                OldFiles = new List<BookFile> { new BookFile { Quality = new QualityModel(Quality.MP3) } }
            });

            var payload = (DiscordPayload)recorder.Args("SendPayload").Single();
            Assert.That(payload.Embeds.Single().Title, Is.EqualTo("Book Upgraded"));
            Assert.That(payload.Content, Does.StartWith("Upgraded:"));
            Assert.That(discord.SupportsOnHealthRestored, Is.True);
        }

        [Test]
        public void pushover_sends_details_and_the_cover_url()
        {
            var proxy = RecordingProxy.Create<IPushoverProxy>(out var recorder);
            var pushover = new Pushover(proxy) { Definition = new NotificationDefinition { Settings = new PushoverSettings() } };

            pushover.OnGrab(Grab(false, false));

            var call = recorder.Calls.Single(c => c.Item1 == "SendNotification");
            Assert.That(call.Item2.Length, Is.EqualTo(4));
            Assert.That(call.Item2[0], Is.EqualTo("Book Grabbed"));
            Assert.That((string)call.Item2[1], Does.Contain("Format: Audiobook (M4B)").And.Contain("Narrator: Scott Brick").And.Contain("Indexer: MAM"));
            Assert.That(call.Item2[2], Is.EqualTo("https://covers.example/dune.jpg"));
        }

        [Test]
        public void pushover_titles_manual_interaction()
        {
            var proxy = RecordingProxy.Create<IPushoverProxy>(out var recorder);
            var pushover = new Pushover(proxy) { Definition = new NotificationDefinition { Settings = new PushoverSettings() } };

            pushover.OnImportFailure(new BookDownloadMessage { Message = "failed", ManualInteractionRequired = true, StatusMessages = new List<string> { "why" } });

            var call = recorder.Calls.Single(c => c.Item1 == "SendNotification");
            Assert.That(call.Item2[0], Is.EqualTo("Manual Interaction Required"));
            Assert.That((string)call.Item2[1], Does.Contain("why"));
        }

        [Test]
        public void details_prefer_remote_covers_and_drop_narrators_for_ebooks()
        {
            var book = new Book
            {
                Title = "Dune",
                MediaType = BookMediaType.Ebook,
                Images = new List<NzbDrone.Core.MediaCover.MediaCover>
                {
                    new NzbDrone.Core.MediaCover.MediaCover(MediaCoverTypes.Poster, "https://covers.example/poster.jpg"),
                    new NzbDrone.Core.MediaCover.MediaCover(MediaCoverTypes.Cover, "/MediaCover/local.jpg")
                }
            };
            var edition = new Edition { IsEbook = true, Narrator = "Someone" };

            var details = TalearrNotificationDetails.Describe(book, edition, new QualityModel(Quality.EPUB));

            // The local cover has no remote URL, so the absolute poster wins.
            Assert.That(details.CoverUrl, Is.EqualTo("https://covers.example/poster.jpg"));
            Assert.That(details.FormatLabel, Is.EqualTo("Ebook (EPUB)"));
            Assert.That(details.Narrator, Is.Null);
        }

        [Test]
        public void details_leave_out_unknown_qualities()
        {
            var details = TalearrNotificationDetails.Describe(new Book { Title = "Dune", MediaType = BookMediaType.Audiobook }, null, new QualityModel(Quality.UnknownAudio));

            Assert.That(details.FormatLabel, Is.EqualTo("Audiobook"));
            Assert.That(details.CoverUrl, Is.Null);
        }
    }
}
