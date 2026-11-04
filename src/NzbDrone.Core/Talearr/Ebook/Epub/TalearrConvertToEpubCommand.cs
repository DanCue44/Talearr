using NzbDrone.Core.Messaging.Commands;

namespace NzbDrone.Core.Talearr.Ebook.Epub
{
    // Talearr: make an EPUB copy of one book's Kindle ebook (AZW3, MOBI).
    // POST /api/v1/command { "name": "TalearrConvertToEpub", "bookId": 123 }
    public class TalearrConvertToEpubCommand : Command
    {
        // Shown in System > Tasks instead of the split class name ("Talearr Convert To Epub").
        public static string DisplayName(string commandName)
        {
            switch (commandName)
            {
                case "TalearrConvertToEpub":
                    return "Convert to EPUB";
                case "TalearrConvertLibraryToEpub":
                    return "Convert library to EPUB";
                default:
                    return null;
            }
        }

        public int BookId { get; set; }

        // Set by the after-import hook so the log says why the conversion ran.
        public bool AfterImport { get; set; }

        public override bool SendUpdatesToClient => true;
    }
}
