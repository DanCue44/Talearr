using FluentValidation;
using NzbDrone.Core.Annotations;
using NzbDrone.Core.ThingiProvider;
using NzbDrone.Core.Validation;

namespace NzbDrone.Core.Notifications.MediaBrowser
{
    public class MediaBrowserSettingsValidator : AbstractValidator<MediaBrowserSettings>
    {
        public MediaBrowserSettingsValidator()
        {
            RuleFor(c => c.Host).ValidHost();
            RuleFor(c => c.Port).InclusiveBetween(1, 65535);
            RuleFor(c => c.ApiKey).NotEmpty();
        }
    }

    // Talearr: Emby and Jellyfin share the MediaBrowser API (Chaptarr #53).
    public class MediaBrowserSettings : IProviderConfig
    {
        private static readonly MediaBrowserSettingsValidator Validator = new MediaBrowserSettingsValidator();

        public MediaBrowserSettings()
        {
            Port = 8096;
            UpdateLibrary = true;
        }

        [FieldDefinition(0, Label = "Host")]
        public string Host { get; set; }

        [FieldDefinition(1, Label = "Port")]
        public int Port { get; set; }

        [FieldDefinition(2, Label = "Use SSL", Type = FieldType.Checkbox, HelpText = "Connect to Emby or Jellyfin over HTTPS instead of HTTP")]
        public bool UseSsl { get; set; }

        [FieldDefinition(3, Label = "URL Base", Type = FieldType.Textbox, Advanced = true, HelpText = "Adds a prefix to the server URL, such as /jellyfin")]
        public string UrlBase { get; set; }

        [FieldDefinition(4, Label = "API Key", Privacy = PrivacyLevel.ApiKey, HelpText = "Create one in the server's dashboard under API Keys")]
        public string ApiKey { get; set; }

        [FieldDefinition(5, Label = "Update Library", Type = FieldType.Checkbox, HelpText = "Tell the server to rescan the changed folder on import, rename and delete")]
        public bool UpdateLibrary { get; set; }

        [FieldDefinition(6, Label = "Map Paths From", Type = FieldType.Textbox, Advanced = true, HelpText = "Path as Talearr sees it, when the server sees the library under another path")]
        public string MapFrom { get; set; }

        [FieldDefinition(7, Label = "Map Paths To", Type = FieldType.Textbox, Advanced = true, HelpText = "The same folder as the server sees it")]
        public string MapTo { get; set; }

        public NzbDroneValidationResult Validate()
        {
            return new NzbDroneValidationResult(Validator.Validate(this));
        }
    }
}
