using NzbDrone.Core.Books.Services;

namespace NzbDrone.Core.Books
{
    // Talearr: safe defaults when one book is requested for an author (UI "Add book" or
    // POST /api/v1/book, which Allseerr uses).
    //
    // Chaptarr already limits the one-time initial selection to the requested book unless the
    // request asks for addOptions.monitor = "all". What it did not pin down is the ongoing
    // "monitor new items" policy: when the request leaves it out, the author inherited the
    // root folder default, so one request could start monitoring every future release by that
    // author. Here an unspecified policy for the requested side becomes "none". Anything the
    // client sends (monitorNewItems, audiobookMonitorNewItems, ebookMonitorNewItems) is kept.
    //
    // For an author that already exists the value only fills an empty side
    // (UpdateAuthorProgressiveSettings never overwrites), so existing authors keep their policy.
    public static class TalearrAddMonitoringDefaults
    {
        public const NewItemMonitorTypes SingleBookMonitorNewItems = NewItemMonitorTypes.None;

        public static MonitoringConfig ForSingleBook(MonitoringConfig config, BookMediaType mediaType)
        {
            if (config == null)
            {
                return null;
            }

            if (mediaType == BookMediaType.Audiobook)
            {
                config.AudiobookMonitorNewItems ??= SingleBookMonitorNewItems;
            }
            else if (mediaType == BookMediaType.Ebook)
            {
                config.EbookMonitorNewItems ??= SingleBookMonitorNewItems;
            }

            return config;
        }
    }
}
