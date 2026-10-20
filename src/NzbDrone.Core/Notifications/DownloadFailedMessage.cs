using System.Collections.Generic;
using NzbDrone.Core.Books;
using NzbDrone.Core.Download;
using NzbDrone.Core.Parser.Model;
using NzbDrone.Core.Qualities;

namespace NzbDrone.Core.Notifications
{
    public class DownloadFailedMessage
    {
        public string Message { get; set; }
        public string SourceTitle { get; set; }
        public QualityModel Quality { get; set; }
        public string DownloadClient { get; set; }
        public string DownloadId { get; set; }

        // Talearr: book context when the failed download is still tracked.
        public Author Author { get; set; }
        public List<Book> Books { get; set; } = new List<Book>();
        public RemoteBook RemoteBook { get; set; }
        public DownloadClientItemClientInfo DownloadClientInfo { get; set; }

        public override string ToString()
        {
            return Message;
        }
    }
}
