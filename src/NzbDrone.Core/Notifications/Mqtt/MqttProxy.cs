using System;
using System.Collections.Generic;
using System.Threading;
using MQTTnet;
using MQTTnet.Protocol;
using NLog;

namespace NzbDrone.Core.Notifications.Mqtt
{
    public sealed class MqttMessage
    {
        public MqttMessage(string topic, string payload, bool retain)
        {
            Topic = topic;
            Payload = payload;
            Retain = retain;
        }

        public string Topic { get; }
        public string Payload { get; }
        public bool Retain { get; }
    }

    public interface IMqttProxy
    {
        // Connects, publishes the messages in order and disconnects. Throws MqttPublishException.
        void Publish(MqttSettings settings, IEnumerable<MqttMessage> messages);
    }

    // Talearr: one short-lived connection per batch. Events are rare, so a held connection (and its
    // reconnect logic) is not worth it, and a broker restart never leaves Talearr stuck.
    public class MqttProxy : IMqttProxy
    {
        private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(15);

        private readonly Logger _logger;

        public MqttProxy(Logger logger)
        {
            _logger = logger;
        }

        public void Publish(MqttSettings settings, IEnumerable<MqttMessage> messages)
        {
            try
            {
                PublishAsync(settings, messages).GetAwaiter().GetResult();
            }
            catch (MqttPublishException)
            {
                throw;
            }
            catch (Exception ex)
            {
                throw new MqttPublishException($"Unable to publish to MQTT broker {settings.Host}:{settings.Port}: {ex.Message}", ex);
            }
        }

        private async System.Threading.Tasks.Task PublishAsync(MqttSettings settings, IEnumerable<MqttMessage> messages)
        {
            using var cancellation = new CancellationTokenSource(Timeout);
            var factory = new MqttClientFactory();
            using var client = factory.CreateMqttClient();

            var builder = new MqttClientOptionsBuilder()
                .WithTcpServer(settings.Host.Trim(), settings.Port)
                .WithClientId(MqttTopics.ClientId(settings))
                .WithCleanSession()
                .WithTimeout(Timeout)
                .WithWillTopic(MqttTopics.Availability(settings))
                .WithWillPayload("offline")
                .WithWillRetain();

            if (!string.IsNullOrWhiteSpace(settings.Username))
            {
                builder = builder.WithCredentials(settings.Username, settings.Password ?? string.Empty);
            }

            if (settings.UseTls)
            {
                builder = builder.WithTlsOptions(o => o.UseTls());
            }

            var connect = await client.ConnectAsync(builder.Build(), cancellation.Token);

            if (connect.ResultCode != MqttClientConnectResultCode.Success)
            {
                throw new MqttPublishException($"The MQTT broker refused the connection: {connect.ResultCode} {connect.ReasonString}".Trim());
            }

            var qos = (MqttQualityOfServiceLevel)Math.Clamp(settings.QualityOfService, 0, 2);
            var count = 0;

            foreach (var message in messages)
            {
                var applicationMessage = new MqttApplicationMessageBuilder()
                    .WithTopic(message.Topic)
                    .WithPayload(message.Payload ?? string.Empty)
                    .WithQualityOfServiceLevel(qos)
                    .WithRetainFlag(message.Retain)
                    .Build();

                var result = await client.PublishAsync(applicationMessage, cancellation.Token);

                if (!result.IsSuccess)
                {
                    throw new MqttPublishException($"The MQTT broker did not accept '{message.Topic}': {result.ReasonCode} {result.ReasonString}".Trim());
                }

                count++;
            }

            await client.DisconnectAsync(new MqttClientDisconnectOptionsBuilder().Build(), cancellation.Token);
            _logger.Trace("Published {0} MQTT messages to {1}:{2}", count, settings.Host, settings.Port);
        }
    }

    public class MqttPublishException : Exception
    {
        public MqttPublishException(string message, Exception innerException = null)
            : base(message, innerException)
        {
        }
    }
}
