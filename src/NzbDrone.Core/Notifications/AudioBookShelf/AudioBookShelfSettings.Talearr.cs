using System;
using NzbDrone.Common.Extensions;
using NzbDrone.Core.Annotations;

namespace NzbDrone.Core.Notifications.AudioBookShelf
{
    // Talearr: Audiobookshelf library awareness (docs/TALEARR_AUDIOBOOKSHELF.md).
    //
    // These are extra keys in this connection's own Settings JSON (talearrSyncLibrary,
    // talearrSkipOwned, talearrLinkUrl). Stock Chaptarr deserializes the same JSON into its
    // AudioBookShelfSettings with System.Text.Json, which skips keys it does not know, so a
    // rollback reads the connection unchanged and keeps using it for its own scans. If Chaptarr
    // saves the connection again the keys are dropped, and Talearr then falls back to the
    // defaults below. Nothing here touches chaptarr.db's schema or config.xml.
    public partial class AudioBookShelfSettings
    {
        [FieldDefinition(20, Label = "Sync Library", Type = FieldType.Checkbox, HelpText = "Talearr reads the mapped Audiobookshelf libraries every hour (read only) so it knows which books are already there, including ones added outside Talearr")]
        public bool TalearrSyncLibrary { get; set; } = true;

        [FieldDefinition(21, Label = "Skip Books Already in Audiobookshelf", Type = FieldType.Checkbox, HelpText = "Do not grab an audiobook or ebook that Audiobookshelf already has and Talearr has no files for. An abridged copy does not count for a wanted unabridged recording. Interactive search shows these releases as 'In Audiobookshelf' and can still grab them")]
        public bool TalearrSkipOwned { get; set; } = true;

        [FieldDefinition(22, Label = "Link URL", Type = FieldType.Textbox, Advanced = true, Placeholder = "http://192.168.1.10:13378", HelpText = "Address your browser uses to open Audiobookshelf, for the 'In Audiobookshelf' links on book pages. Leave empty to build it from Host, Port and Url Base")]
        public string TalearrLinkUrl { get; set; }

        // The browser-facing base URL for item links, without a trailing slash. Only absolute
        // http(s) URLs are accepted from the setting; anything else falls back to the API address.
        public string GetTalearrWebBaseUrl()
        {
            if (TalearrLinkUrl.IsNotNullOrWhiteSpace() &&
                Uri.TryCreate(TalearrLinkUrl.Trim(), UriKind.Absolute, out var uri) &&
                (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps) &&
                string.IsNullOrEmpty(uri.UserInfo))
            {
                return uri.GetLeftPart(UriPartial.Path).TrimEnd('/');
            }

            if (Host.IsNullOrWhiteSpace())
            {
                return null;
            }

            var scheme = UseSsl ? "https" : "http";
            var urlBase = UrlBase.IsNullOrWhiteSpace() ? string.Empty : "/" + UrlBase.Trim().Trim('/');

            return $"{scheme}://{Host.Trim().ToUrlHost()}:{Port}{urlBase}";
        }
    }
}
