using NzbDrone.Core.Messaging.Commands;

namespace NzbDrone.Core.Talearr.Audiobook.M4b
{
    // TaleArr: merge every multi-file audiobook already in the library (or one author's) into one
    // chaptered M4B, one book after another, with the same checks as Convert to M4B. Manual only.
    // POST /api/v1/command { "name": "TalearrConvertLibraryToM4b", "authorId": 0 }
    public class TalearrConvertLibraryToM4bCommand : Command
    {
        // 0 converts the whole library.
        public int AuthorId { get; set; }

        public override bool SendUpdatesToClient => true;
        public override bool IsLongRunning => true;
    }
}
