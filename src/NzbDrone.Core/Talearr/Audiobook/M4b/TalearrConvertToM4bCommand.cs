using NzbDrone.Core.Messaging.Commands;

namespace NzbDrone.Core.Talearr.Audiobook.M4b
{
    // Talearr: merge one book's audio files into a single chaptered M4B.
    // POST /api/v1/command { "name": "TalearrConvertToM4b", "bookId": 123 }
    public class TalearrConvertToM4bCommand : Command
    {
        // Shown in System > Tasks instead of the split class name ("Talearr Convert To M4b").
        public static string DisplayName(string commandName)
        {
            switch (commandName)
            {
                case "TalearrConvertToM4b":
                    return "Convert to M4B";
                case "TalearrConvertLibraryToM4b":
                    return "Convert library to M4B";
                default:
                    return null;
            }
        }

        public int BookId { get; set; }

        // Set by the after-import hook so the log says why the conversion ran.
        public bool AfterImport { get; set; }

        // Not a disk-access command on purpose: that would hold up imports for the whole
        // encode. The converter writes to a hidden folder that disk scans skip.
        public override bool SendUpdatesToClient => true;
        public override bool IsLongRunning => true;
    }
}
