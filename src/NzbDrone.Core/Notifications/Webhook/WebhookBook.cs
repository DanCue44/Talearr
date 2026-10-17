using System;
using System.Linq;
using NzbDrone.Core.Books;

namespace NzbDrone.Core.Notifications.Webhook
{
    public class WebhookBook
    {
        public WebhookBook()
        {
        }

        public WebhookBook(Book book)
            : this(book, null)
        {
        }

        public WebhookBook(Book book, Edition edition)
        {
            Id = book.Id;
            GoodreadsId = book.GoodreadsBookId ?? book.GoodreadsWorkId;
            Title = book.Title;
            ReleaseDate = book.ReleaseDate;

            // Editions are not always hydrated on Book objects (navigation property).
            // Prefer the provided edition (eg from an imported/deleted BookFile), otherwise fall back to monitored/first edition.
            var selectedEdition = edition ??
                                  book.Editions?.OrderBy(e => e.Id).FirstOrDefault(e => e.Monitored) ??
                                  book.Editions?.OrderBy(e => e.Id).FirstOrDefault();

            if (selectedEdition != null)
            {
                Edition = new WebhookBookEdition(selectedEdition);
            }

            // TaleArr: additive details for consumers that show a card (Readarr fields unchanged).
            var details = TalearrNotificationDetails.Describe(book, selectedEdition);
            CoverUrl = details?.CoverUrl;
            MediaType = details?.MediaType?.ToLowerInvariant();
            Narrator = details?.Narrator;
            Series = details?.Series;
        }

        public int Id { get; set; }
        public string GoodreadsId { get; set; }
        public string Title { get; set; }
        public WebhookBookEdition Edition { get; set; }
        public DateTime? ReleaseDate { get; set; }
        public string CoverUrl { get; set; }
        public string MediaType { get; set; }
        public string Narrator { get; set; }
        public string Series { get; set; }
    }
}
