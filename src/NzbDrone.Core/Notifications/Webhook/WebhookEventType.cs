using Newtonsoft.Json;
using Newtonsoft.Json.Converters;
using Newtonsoft.Json.Serialization;

namespace NzbDrone.Core.Notifications.Webhook
{
    // TODO: In v4 this will likely be changed to the default camel case.
    [JsonConverter(typeof(StringEnumConverter), converterParameters: typeof(DefaultNamingStrategy))]
    public enum WebhookEventType
    {
        Test,
        Grab,
        Download,
        Rename,
        AuthorAdded,
        BookAdded,
        AuthorDelete,
        BookDelete,
        BookFileDelete,
        Health,
        Retag,
        ApplicationUpdate,

        // TaleArr: Sonarr/Radarr parity, appended so existing values keep their names.
        DownloadFailure,
        ImportFailure,
        ManualInteractionRequired,
        HealthRestored
    }
}
