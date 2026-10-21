using FluentValidation;
using NzbDrone.Core.Annotations;
using NzbDrone.Core.ThingiProvider;
using NzbDrone.Core.Validation;

namespace NzbDrone.Core.Notifications.Mqtt
{
    public enum MqttQualityOfService
    {
        AtMostOnce = 0,
        AtLeastOnce = 1,
        ExactlyOnce = 2
    }

    public class MqttSettingsValidator : AbstractValidator<MqttSettings>
    {
        public MqttSettingsValidator()
        {
            RuleFor(c => c.Host).ValidHost();
            RuleFor(c => c.Port).InclusiveBetween(1, 65535);
            RuleFor(c => c.TopicPrefix).NotEmpty().Must(MqttTopics.IsValidPrefix)
                .WithMessage("Use letters, numbers, '-', '_' and '/' only, with no '+' or '#'");
            RuleFor(c => c.DiscoveryPrefix).NotEmpty().Must(MqttTopics.IsValidPrefix).When(c => c.HomeAssistantDiscovery)
                .WithMessage("Use letters, numbers, '-', '_' and '/' only, with no '+' or '#'");
            RuleFor(c => c.SensorInterval).InclusiveBetween(1, 1440).When(c => c.PublishSensors);
        }
    }

    // Talearr: MQTT gateway (Chaptarr #54). Events reuse the webhook payloads, so HTTP and MQTT
    // consumers get the same event names and fields.
    public class MqttSettings : IProviderConfig
    {
        private static readonly MqttSettingsValidator Validator = new MqttSettingsValidator();

        public MqttSettings()
        {
            Port = 1883;
            TopicPrefix = "talearr";
            QualityOfService = (int)MqttQualityOfService.AtLeastOnce;
            PublishEvents = true;
            PublishSensors = true;
            HomeAssistantDiscovery = true;
            DiscoveryPrefix = "homeassistant";
            SensorInterval = 5;
        }

        [FieldDefinition(0, Label = "Host", HelpText = "MQTT broker host name or IP address")]
        public string Host { get; set; }

        [FieldDefinition(1, Label = "Port", Type = FieldType.Number)]
        public int Port { get; set; }

        [FieldDefinition(2, Label = "Use TLS", Type = FieldType.Checkbox, HelpText = "Connect to the broker over TLS. The broker certificate is validated.")]
        public bool UseTls { get; set; }

        [FieldDefinition(3, Label = "Username", Privacy = PrivacyLevel.UserName)]
        public string Username { get; set; }

        [FieldDefinition(4, Label = "Password", Type = FieldType.Password, Privacy = PrivacyLevel.Password)]
        public string Password { get; set; }

        [FieldDefinition(5, Label = "Client ID", HelpText = "Optional. Leave empty to use one based on the topic prefix.", Advanced = true)]
        public string ClientId { get; set; }

        [FieldDefinition(6, Label = "Topic Prefix", HelpText = "Topics start with this, for example talearr/event/Download. Use a different prefix for each Talearr on the same broker.")]
        public string TopicPrefix { get; set; }

        [FieldDefinition(7, Label = "Quality of Service", Type = FieldType.Select, SelectOptions = typeof(MqttQualityOfService), Advanced = true)]
        public int QualityOfService { get; set; }

        [FieldDefinition(8, Label = "Publish Events", Type = FieldType.Checkbox, HelpText = "Publish each enabled event (grab, import, upgrade, failures, health) as JSON")]
        public bool PublishEvents { get; set; }

        [FieldDefinition(9, Label = "Publish Sensors", Type = FieldType.Checkbox, HelpText = "Publish queue, missing, health and last imported book as a retained JSON state")]
        public bool PublishSensors { get; set; }

        [FieldDefinition(10, Label = "Sensor Interval", Type = FieldType.Number, Unit = "minutes", HelpText = "How often the sensor state is refreshed", Advanced = true)]
        public int SensorInterval { get; set; }

        [FieldDefinition(11, Label = "Home Assistant Discovery", Type = FieldType.Checkbox, HelpText = "Announce the sensors to Home Assistant through MQTT discovery")]
        public bool HomeAssistantDiscovery { get; set; }

        [FieldDefinition(12, Label = "Discovery Prefix", HelpText = "Home Assistant's discovery prefix", Advanced = true)]
        public string DiscoveryPrefix { get; set; }

        public NzbDroneValidationResult Validate()
        {
            return new NzbDroneValidationResult(Validator.Validate(this));
        }
    }
}
