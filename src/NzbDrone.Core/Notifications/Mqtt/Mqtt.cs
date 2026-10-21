using System.Collections.Generic;
using FluentValidation.Results;
using NLog;
using NzbDrone.Common.Serializer;
using NzbDrone.Core.Books;
using NzbDrone.Core.Configuration;
using NzbDrone.Core.MediaFiles;
using NzbDrone.Core.Notifications.Webhook;

namespace NzbDrone.Core.Notifications.Mqtt
{
    // Talearr: MQTT gateway with Home Assistant discovery (Chaptarr #54). Event messages carry the
    // same JSON as the Webhook connection; sensors are published by MqttSensorService.
    public class Mqtt : WebhookBase<MqttSettings>
    {
        private readonly IMqttProxy _proxy;
        private readonly IMqttSensorService _sensorService;
        private readonly Logger _logger;

        public Mqtt(IMqttProxy proxy, IMqttSensorService sensorService, IConfigFileProvider configFileProvider, Logger logger)
            : base(configFileProvider)
        {
            _proxy = proxy;
            _sensorService = sensorService;
            _logger = logger;
        }

        public override string Name => "MQTT";

        public override string Link => "https://www.home-assistant.io/integrations/mqtt/";

        public override void OnGrab(GrabMessage message)
        {
            Send(WebhookEventType.Grab, BuildOnGrabPayload(message));
        }

        public override void OnReleaseImport(BookDownloadMessage message)
        {
            var payload = BuildOnReleaseImportPayload(message);

            // Upgrades get their own topic so automations can tell them apart without parsing.
            Send(payload.IsUpgrade ? "Upgrade" : WebhookEventType.Download.ToString(), payload);
        }

        public override void OnRename(Author author, List<RenamedBookFile> renamedFiles)
        {
            Send(WebhookEventType.Rename, BuildOnRenamePayload(author, renamedFiles));
        }

        public override void OnAuthorAdded(Author author)
        {
            Send(WebhookEventType.AuthorAdded, BuildOnAuthorAdded(author));
        }

        public override void OnBookAdded(Book book)
        {
            Send(WebhookEventType.BookAdded, BuildOnBookAdded(book));
        }

        public override void OnAuthorDelete(AuthorDeleteMessage deleteMessage)
        {
            Send(WebhookEventType.AuthorDelete, BuildOnAuthorDelete(deleteMessage));
        }

        public override void OnBookDelete(BookDeleteMessage deleteMessage)
        {
            Send(WebhookEventType.BookDelete, BuildOnBookDelete(deleteMessage));
        }

        public override void OnBookFileDelete(BookFileDeleteMessage deleteMessage)
        {
            Send(WebhookEventType.BookFileDelete, BuildOnBookFileDelete(deleteMessage));
        }

        public override void OnBookRetag(BookRetagMessage message)
        {
            Send(WebhookEventType.Retag, BuildOnBookRetagPayload(message));
        }

        public override void OnHealthIssue(HealthCheck.HealthCheck healthCheck)
        {
            Send(WebhookEventType.Health, BuildHealthPayload(healthCheck));
        }

        public override void OnHealthRestored(HealthCheck.HealthCheck previousCheck)
        {
            Send(WebhookEventType.HealthRestored, BuildHealthRestoredPayload(previousCheck));
        }

        public override void OnDownloadFailure(DownloadFailedMessage message)
        {
            Send(WebhookEventType.DownloadFailure, BuildOnDownloadFailurePayload(message));
        }

        public override void OnImportFailure(BookDownloadMessage message)
        {
            var payload = BuildOnImportFailurePayload(message);
            Send(payload.EventType, payload);
        }

        public override void OnApplicationUpdate(ApplicationUpdateMessage updateMessage)
        {
            Send(WebhookEventType.ApplicationUpdate, BuildApplicationUpdatePayload(updateMessage));
        }

        public override ValidationResult Test()
        {
            var failures = new List<ValidationFailure>();

            try
            {
                var messages = new List<MqttMessage>();

                if (Settings.PublishEvents)
                {
                    messages.Add(EventMessage(WebhookEventType.Test.ToString(), BuildTestPayload()));
                }

                if (Settings.PublishSensors)
                {
                    messages.AddRange(_sensorService.BuildMessages(Settings, _sensorService.BuildState()));
                }

                if (messages.Count == 0)
                {
                    messages.Add(new MqttMessage(MqttTopics.Availability(Settings), "online", true));
                }

                _proxy.Publish(Settings, messages);
            }
            catch (MqttPublishException ex)
            {
                _logger.Warn(ex, "MQTT test failed");
                failures.Add(new ValidationFailure(nameof(Settings.Host), ex.Message));
            }

            return new ValidationResult(failures);
        }

        private void Send(WebhookEventType eventType, WebhookPayload payload)
        {
            Send(eventType.ToString(), payload);
        }

        private void Send(string topicName, WebhookPayload payload)
        {
            if (!Settings.PublishEvents)
            {
                return;
            }

            _proxy.Publish(Settings, new[] { EventMessage(topicName, payload) });
        }

        private MqttMessage EventMessage(string topicName, WebhookPayload payload)
        {
            return new MqttMessage($"{MqttTopics.Prefix(Settings)}/event/{topicName}", payload.ToJson(), false);
        }
    }
}
