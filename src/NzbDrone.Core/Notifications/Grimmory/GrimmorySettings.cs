using System.Collections.Generic;
using FluentValidation;
using NzbDrone.Core.Annotations;
using NzbDrone.Core.ThingiProvider;
using NzbDrone.Core.Validation;

namespace NzbDrone.Core.Notifications.Grimmory
{
    public class GrimmorySettingsValidator : AbstractValidator<GrimmorySettings>
    {
        public GrimmorySettingsValidator()
        {
            RuleFor(c => c.Url).IsValidUrl();
            RuleFor(c => c.Username).NotEmpty();
            RuleFor(c => c.Password).NotEmpty();
            RuleFor(c => c)
                .Must(c => c.Ebooks || c.Audiobooks)
                .WithMessage("Choose ebooks, audiobooks or both");
            RuleFor(c => c.GrimmoryPath).NotEmpty().When(c => !string.IsNullOrWhiteSpace(c.TalearrPath))
                .WithMessage("Set both paths, or neither");
            RuleFor(c => c.TalearrPath).NotEmpty().When(c => !string.IsNullOrWhiteSpace(c.GrimmoryPath))
                .WithMessage("Set both paths, or neither");
        }
    }

    // Talearr: Grimmory library refresh (Chaptarr #99). Grimmory reads the same files from a
    // shared folder, so after an import, upgrade, rename or delete Talearr asks it to rescan the
    // library that holds the book.
    public class GrimmorySettings : IProviderConfig
    {
        private static readonly GrimmorySettingsValidator Validator = new GrimmorySettingsValidator();

        public GrimmorySettings()
        {
            Ebooks = true;
            Audiobooks = true;
            LibraryIds = new List<int>();
        }

        [FieldDefinition(0, Label = "URL", Type = FieldType.Url, Placeholder = "http://grimmory:6060", HelpText = "Grimmory's address, including http:// or https:// and any URL base")]
        public string Url { get; set; }

        [FieldDefinition(1, Label = "Username", Privacy = PrivacyLevel.UserName, HelpText = "A Grimmory user that can manage libraries")]
        public string Username { get; set; }

        [FieldDefinition(2, Label = "Password", Type = FieldType.Password, Privacy = PrivacyLevel.Password)]
        public string Password { get; set; }

        [FieldDefinition(3, Label = "Libraries", Type = FieldType.TagSelect, SelectOptionsProviderAction = "getLibraries", HelpText = "Libraries to rescan. Leave empty to rescan only the library whose folder holds the book.")]
        public List<int> LibraryIds { get; set; }

        [FieldDefinition(4, Label = "Ebooks", Type = FieldType.Checkbox, HelpText = "Rescan for ebook changes")]
        public bool Ebooks { get; set; }

        [FieldDefinition(5, Label = "Audiobooks", Type = FieldType.Checkbox, HelpText = "Rescan for audiobook changes")]
        public bool Audiobooks { get; set; }

        [FieldDefinition(6, Label = "Talearr Path", Type = FieldType.Textbox, Advanced = true, HelpText = "Only if Grimmory sees the library under a different path: the folder as Talearr sees it, for example /books")]
        public string TalearrPath { get; set; }

        [FieldDefinition(7, Label = "Grimmory Path", Type = FieldType.Textbox, Advanced = true, HelpText = "The same folder as Grimmory sees it, for example /library/books")]
        public string GrimmoryPath { get; set; }

        public NzbDroneValidationResult Validate()
        {
            return new NzbDroneValidationResult(Validator.Validate(this));
        }
    }
}
