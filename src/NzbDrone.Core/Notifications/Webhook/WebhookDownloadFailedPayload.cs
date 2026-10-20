using System.Collections.Generic;

namespace NzbDrone.Core.Notifications.Webhook
{
    // Talearr: Sonarr/Radarr style "DownloadFailure" webhook.
    public class WebhookDownloadFailedPayload : WebhookPayload
    {
        public WebhookAuthor Author { get; set; }
        public List<WebhookBook> Books { get; set; }
        public WebhookRelease Release { get; set; }
        public string Quality { get; set; }
        public int QualityVersion { get; set; }
        public string ReleaseTitle { get; set; }
        public string DownloadClient { get; set; }
        public string DownloadClientType { get; set; }
        public string DownloadId { get; set; }
        public string Message { get; set; }
    }
}
