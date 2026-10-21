using System.Collections.Generic;
using FluentValidation;
using NzbDrone.Core.Annotations;
using NzbDrone.Core.ImportLists.Goodreads;
using NzbDrone.Core.Validation;

namespace NzbDrone.Core.ImportLists.Bookwyrm
{
    public class BookwyrmImportListValidator : AbstractValidator<BookwyrmImportListSettings>
    {
        public BookwyrmImportListValidator()
        {
            RuleFor(c => c.BaseUrl).IsValidUrl();
            RuleFor(c => c.Username).NotEmpty();
            RuleFor(c => c.Shelf).NotEmpty().Matches("^[A-Za-z0-9_\\-]+$").WithMessage("Use the shelf's name from its address, for example to-read");
            this.AddDualMediaRules();
        }
    }

    // Talearr: a shelf on a Bookwyrm instance (Readarr #2877), read through its public
    // ActivityPub JSON. The shelf must be visible to everyone.
    public class BookwyrmImportListSettings : IGoodreadsDualMediaImportListSettings
    {
        private static readonly BookwyrmImportListValidator Validator = new();

        public BookwyrmImportListSettings()
        {
            BaseUrl = "https://bookwyrm.social";
            Shelf = "to-read";
            MonitorAudiobooks = true;
            MonitorEbooks = true;
        }

        [FieldDefinition(0, Label = "Instance URL", Type = FieldType.Url, HelpText = "Your Bookwyrm server, for example https://bookwyrm.social")]
        public string BaseUrl { get; set; }

        [FieldDefinition(1, Label = "Username", HelpText = "Your user name on that server, without the @ or the server name")]
        public string Username { get; set; }

        [FieldDefinition(2, Label = "Shelf", HelpText = "to-read, reading, read, stopped-reading, or a custom shelf's name from its address")]
        public string Shelf { get; set; }

        [FieldDefinition(3, Label = "Monitor Audiobooks", Type = FieldType.Checkbox, HelpText = "When enabled, audiobook items will be created/monitored for books from this list.")]
        public bool MonitorAudiobooks { get; set; }

        [FieldDefinition(4, Label = "Monitor Ebooks", Type = FieldType.Checkbox, HelpText = "When enabled, ebook items will be created/monitored for books from this list.")]
        public bool MonitorEbooks { get; set; }

        [FieldDefinition(5, Label = "Import Limit", Type = FieldType.Number, HelpText = "Maximum number of books to import from this list. 0 is unlimited.", Advanced = true)]
        public int ImportLimit { get; set; }

        [FieldDefinition(6, Label = "Audiobook Quality Profile", Type = FieldType.Select, SelectOptionsProviderAction = "getAudiobookQualityProfiles", HelpText = "Quality profile used when importing audiobooks from this list.")]
        public int AudiobookQualityProfileId { get; set; }

        [FieldDefinition(7, Label = "Ebook Quality Profile", Type = FieldType.Select, SelectOptionsProviderAction = "getEbookQualityProfiles", HelpText = "Quality profile used when importing ebooks from this list.")]
        public int EbookQualityProfileId { get; set; }

        [FieldDefinition(8, Label = "Audiobook Metadata Profile", Type = FieldType.Select, SelectOptionsProviderAction = "getAudiobookMetadataProfiles", HelpText = "Metadata profile used when importing audiobooks from this list.")]
        public int AudiobookMetadataProfileId { get; set; }

        [FieldDefinition(9, Label = "Ebook Metadata Profile", Type = FieldType.Select, SelectOptionsProviderAction = "getEbookMetadataProfiles", HelpText = "Metadata profile used when importing ebooks from this list.")]
        public int EbookMetadataProfileId { get; set; }

        [FieldDefinition(10, Label = "Audiobook Root Folder", Type = FieldType.Path, HelpText = "Root folder used when importing audiobooks from this list.")]
        public string AudiobookRootFolderPath { get; set; }

        [FieldDefinition(11, Label = "Ebook Root Folder", Type = FieldType.Path, HelpText = "Root folder used when importing ebooks from this list.")]
        public string EbookRootFolderPath { get; set; }

        [FieldDefinition(12, Label = "Audiobook Tags", Type = FieldType.TagSelect, SelectOptionsProviderAction = "getTags", HelpText = "Optional: tags to apply when importing audiobooks from this list.")]
        public List<int> AudiobookTags { get; set; } = new();

        [FieldDefinition(13, Label = "Ebook Tags", Type = FieldType.TagSelect, SelectOptionsProviderAction = "getTags", HelpText = "Optional: tags to apply when importing ebooks from this list.")]
        public List<int> EbookTags { get; set; } = new();

        public NzbDroneValidationResult Validate()
        {
            return new NzbDroneValidationResult(Validator.Validate(this));
        }
    }
}
