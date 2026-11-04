using NzbDrone.Core.Messaging.Commands;

namespace NzbDrone.Core.Talearr.Ebook.Epub
{
    // Talearr: make an EPUB copy of every Kindle ebook already in the library (or one author's)
    // that has no EPUB yet, one book after another. Manual only.
    // POST /api/v1/command { "name": "TalearrConvertLibraryToEpub", "authorId": 0 }
    public class TalearrConvertLibraryToEpubCommand : Command
    {
        // 0 converts the whole library.
        public int AuthorId { get; set; }

        public override bool SendUpdatesToClient => true;
        public override bool IsLongRunning => true;
    }
}
