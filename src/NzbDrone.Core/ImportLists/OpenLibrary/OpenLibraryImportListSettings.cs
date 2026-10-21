using System.Collections.Generic;
using System.Text.RegularExpressions;
using FluentValidation;
using NzbDrone.Core.Annotations;
using NzbDrone.Core.ImportLists.Goodreads;
using NzbDrone.Core.Validation;

namespace NzbDrone.Core.ImportLists.OpenLibrary
{
    public enum OpenLibraryListSource
    {
        WantToRead = 0,
        CurrentlyReading = 1,
        AlreadyRead = 2,
        List = 3
    }

    public class OpenLibraryImportListValidator : AbstractValidator<OpenLibraryImportListSettings>
    {
        public OpenLibraryImportListValidator()
        {
            RuleFor(c => c.Username).NotEmpty().Matches("^[A-Za-z0-9_.\\-]+$").WithMessage("Use the user name from your Open Library profile address");
            RuleFor(c => c.ListId).NotEmpty().Must(id => OpenLibraryImportListSettings.ListKey(id) != null)
                .When(c => c.Source == (int)OpenLibraryListSource.List)
                .WithMessage("Paste the list address or its id, for example OL95357L");
            this.AddDualMediaRules();
        }
    }

    // Talearr: Open Library reading log (want to read, currently reading, already read) or one
    // of the user's lists (Readarr #1709). The reading log must be public in Open Library.
    public class OpenLibraryImportListSettings : IGoodreadsDualMediaImportListSettings
    {
        private static readonly OpenLibraryImportListValidator Validator = new();
        private static readonly Regex ListKeyPattern = new Regex(@"OL\d+L", RegexOptions.Compiled);

        public OpenLibraryImportListSettings()
        {
            BaseUrl = OpenLibraryListClient.OpenLibraryUrl;
            Source = (int)OpenLibraryListSource.WantToRead;
            MonitorAudiobooks = true;
            MonitorEbooks = true;
        }

        public string BaseUrl { get; set; }

        [FieldDefinition(0, Label = "Username", HelpText = "Your Open Library user name, as in openlibrary.org/people/<name>")]
        public string Username { get; set; }

        [FieldDefinition(1, Label = "Source", Type = FieldType.Select, SelectOptions = typeof(OpenLibraryListSource), HelpText = "A reading log shelf (must be public in Open Library's privacy settings), or one of your lists")]
        public int Source { get; set; }

        [FieldDefinition(2, Label = "List", HelpText = "Only for Source List: the list address or id, for example OL95357L")]
        public string ListId { get; set; }

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

        public static string ListKey(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return null;
            }

            var match = ListKeyPattern.Match(value);
            return match.Success ? match.Value : null;
        }

        public NzbDroneValidationResult Validate()
        {
            return new NzbDroneValidationResult(Validator.Validate(this));
        }
    }
}
