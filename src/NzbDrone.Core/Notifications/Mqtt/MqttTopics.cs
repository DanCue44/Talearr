using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace NzbDrone.Core.Notifications.Mqtt
{
    // Talearr: topic layout for the MQTT gateway.
    //   <prefix>/event/<EventType>   one message per event (not retained)
    //   <prefix>/sensors             retained JSON state for the sensors
    //   <prefix>/status              retained "online" after each publish
    //   <discovery>/sensor/<node>/<sensor>/config   retained Home Assistant discovery
    public static class MqttTopics
    {
        private static readonly Regex PrefixPattern = new Regex("^[A-Za-z0-9_\\-]+(/[A-Za-z0-9_\\-]+)*$", RegexOptions.Compiled);
        private static readonly Regex NodeInvalid = new Regex("[^a-z0-9_]+", RegexOptions.Compiled);

        public static readonly IReadOnlyList<MqttSensor> Sensors = new List<MqttSensor>
        {
            new MqttSensor("queue", "Queue", "{{ value_json.queue }}", "books", "mdi:download"),
            new MqttSensor("missing", "Missing books", "{{ value_json.missing }}", "books", "mdi:book-alert"),
            new MqttSensor("health_issues", "Health issues", "{{ value_json.healthIssues }}", null, "mdi:alert-circle"),
            new MqttSensor("last_imported", "Last imported book", "{{ value_json.lastImported.title if value_json.lastImported else 'None' }}", null, "mdi:book-check")
        };

        public static bool IsValidPrefix(string prefix)
        {
            return !string.IsNullOrWhiteSpace(prefix) && PrefixPattern.IsMatch(prefix.Trim());
        }

        public static string Prefix(MqttSettings settings)
        {
            return settings.TopicPrefix.Trim().TrimEnd('/');
        }

        public static string SensorState(MqttSettings settings)
        {
            return $"{Prefix(settings)}/sensors";
        }

        public static string Availability(MqttSettings settings)
        {
            return $"{Prefix(settings)}/status";
        }

        // A Home Assistant object id derived from the topic prefix, so two Talearr instances on one
        // broker get separate devices and entities.
        public static string NodeId(MqttSettings settings)
        {
            var node = NodeInvalid.Replace(Prefix(settings).ToLowerInvariant(), "_").Trim('_');
            return string.IsNullOrEmpty(node) ? "talearr" : node;
        }

        public static string Discovery(MqttSettings settings, MqttSensor sensor)
        {
            return $"{settings.DiscoveryPrefix.Trim().TrimEnd('/')}/sensor/{NodeId(settings)}/{sensor.Key}/config";
        }

        public static string ClientId(MqttSettings settings)
        {
            return string.IsNullOrWhiteSpace(settings.ClientId) ? NodeId(settings) + "-" + System.Environment.MachineName.ToLowerInvariant() : settings.ClientId.Trim();
        }

        public static object DiscoveryPayload(MqttSettings settings, MqttSensor sensor, string version)
        {
            var node = NodeId(settings);

            var payload = new Dictionary<string, object>
            {
                ["name"] = sensor.Name,
                ["unique_id"] = $"{node}_{sensor.Key}",
                ["object_id"] = $"{node}_{sensor.Key}",
                ["state_topic"] = SensorState(settings),
                ["value_template"] = sensor.ValueTemplate,
                ["availability_topic"] = Availability(settings),
                ["icon"] = sensor.Icon,
                ["device"] = new Dictionary<string, object>
                {
                    ["identifiers"] = new[] { node },
                    ["name"] = node == "talearr" ? "Talearr" : $"Talearr ({Prefix(settings)})",
                    ["manufacturer"] = "Talearr",
                    ["model"] = "Talearr",
                    ["sw_version"] = version
                }
            };

            if (sensor.Unit != null)
            {
                payload["unit_of_measurement"] = sensor.Unit;
                payload["state_class"] = "measurement";
            }

            if (sensor.Key == "last_imported")
            {
                payload["json_attributes_topic"] = SensorState(settings);
                payload["json_attributes_template"] = "{{ value_json.lastImported | tojson if value_json.lastImported else '{}' }}";
            }

            return payload.Where(p => p.Value != null).ToDictionary(p => p.Key, p => p.Value);
        }
    }

    public sealed class MqttSensor
    {
        public MqttSensor(string key, string name, string valueTemplate, string unit, string icon)
        {
            Key = key;
            Name = name;
            ValueTemplate = valueTemplate;
            Unit = unit;
            Icon = icon;
        }

        public string Key { get; }
        public string Name { get; }
        public string ValueTemplate { get; }
        public string Unit { get; }
        public string Icon { get; }
    }
}
