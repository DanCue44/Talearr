using System;
using System.Collections.Generic;
using System.Linq;
using NzbDrone.Common.Extensions;
using NzbDrone.Core.Books;
using NzbDrone.Core.Download.TrackedDownloads;
using NzbDrone.Core.MediaCover;
using NzbDrone.Core.Parser.Model;
using NzbDrone.Core.Qualities;

namespace NzbDrone.Core.Notifications
{
    // TaleArr: book and audiobook details for rich notifications (cover art, format, narrator,
    // series). Pure helpers over objects the notification already carries: no lookups.
    public class TalearrBookDetails
    {
        public string Title { get; set; }
        public string AuthorName { get; set; }
        public string CoverUrl { get; set; }
        public string MediaType { get; set; }
        public string Format { get; set; }
        public string Narrator { get; set; }
        public string Series { get; set; }
        public int? Year { get; set; }
        public string Overview { get; set; }

        // "Audiobook (M4B)", "Ebook (EPUB)" or just the media type when the quality is unknown.
        public string FormatLabel => Format.IsNullOrWhiteSpace() ? MediaType : $"{MediaType} ({Format})";
    }

    public static class TalearrNotificationDetails
    {
        public const string BundleFallbackNote = " [bundle, last resort]";
        public const string BundleNote = " [bundle]";

        private const int OverviewLength = 300;

        public static bool IsTalearrHealthCheck(HealthCheck.HealthCheck healthCheck)
        {
            return healthCheck?.Source?.Name?.StartsWith("Talearr", StringComparison.Ordinal) == true;
        }

        public static bool IsBundleFallback(RemoteBook remoteBook)
        {
            return remoteBook?.IsBundle == true && remoteBook.IsBundleFallback;
        }

        public static string WithBundleNote(string message, RemoteBook remoteBook)
        {
            if (remoteBook?.IsBundle != true)
            {
                return message;
            }

            return message + (remoteBook.IsBundleFallback ? BundleFallbackNote : BundleNote);
        }

        public static List<string> StatusMessages(IEnumerable<TrackedDownloadStatusMessage> statusMessages)
        {
            if (statusMessages == null)
            {
                return new List<string>();
            }

            return statusMessages
                .Where(s => s != null)
                .SelectMany(s => (s.Messages ?? new List<string>())
                    .Where(m => m.IsNotNullOrWhiteSpace())
                    .Select(m => s.Title.IsNullOrWhiteSpace() || s.Title == m ? m : $"{s.Title}: {m}"))
                .Distinct()
                .ToList();
        }

        public static Edition SelectEdition(Book book, Edition edition = null)
        {
            if (edition != null)
            {
                return edition;
            }

            var editions = book?.LazyEditions?.IsLoaded == true ? book.Editions : null;

            return editions?.OrderBy(e => e.Id).FirstOrDefault(e => e.Monitored) ??
                   editions?.OrderBy(e => e.Id).FirstOrDefault();
        }

        public static TalearrBookDetails Describe(Book book, Edition edition = null, QualityModel quality = null, Author author = null)
        {
            if (book == null)
            {
                return null;
            }

            edition = SelectEdition(book, edition);

            var details = new TalearrBookDetails
            {
                Title = book.Title,
                AuthorName = (author ?? SafeAuthor(book))?.Name,
                CoverUrl = CoverUrl(book, edition),
                MediaType = MediaTypeOf(book, edition, quality) == BookMediaType.Ebook ? "Ebook" : "Audiobook",
                Format = KnownQualityName(quality),
                Narrator = NarratorOf(edition),
                Series = SeriesOf(book),
                Year = book.ReleaseDate?.Year ?? book.PublicationYear,
                Overview = Shorten(edition?.Overview.IsNotNullOrWhiteSpace() == true ? edition.Overview : book.Overview)
            };

            // Narrators only mean something for audiobooks.
            if (details.MediaType == "Ebook")
            {
                details.Narrator = null;
            }

            return details;
        }

        public static string CoverUrl(Book book, Edition edition = null)
        {
            var images = new List<MediaCover.MediaCover>();

            if (edition?.Images != null)
            {
                images.AddRange(edition.Images);
            }

            if (book?.Images != null)
            {
                images.AddRange(book.Images);
            }

            // Only absolute URLs: a chat app cannot load TaleArr's local /MediaCover path.
            return images
                .Where(i => i != null)
                .OrderBy(i => i.CoverType == MediaCoverTypes.Cover ? 0 : 1)
                .Select(i => AbsoluteUrl(i.RemoteUrl) ?? AbsoluteUrl(i.Url))
                .FirstOrDefault(u => u != null);
        }

        // One line per detail, for plain-text services (Pushover, Telegram, email).
        public static string DetailLines(TalearrBookDetails details, string extra = null)
        {
            if (details == null)
            {
                return extra;
            }

            var lines = new List<string>();

            if (details.Series.IsNotNullOrWhiteSpace())
            {
                lines.Add($"Series: {details.Series}");
            }

            lines.Add($"Format: {details.FormatLabel}");

            if (details.Narrator.IsNotNullOrWhiteSpace())
            {
                lines.Add($"Narrator: {details.Narrator}");
            }

            if (extra.IsNotNullOrWhiteSpace())
            {
                lines.Add(extra);
            }

            return string.Join("\n", lines);
        }

        private static Author SafeAuthor(Book book)
        {
            try
            {
                return book.LazyAuthor?.IsLoaded == true ? book.Author : null;
            }
            catch (Exception)
            {
                return null;
            }
        }

        private static BookMediaType MediaTypeOf(Book book, Edition edition, QualityModel quality)
        {
            var fromQuality = quality?.Quality == null ? null : QualityMediaTypeHelper.GetKnownMediaType(quality.Quality);

            if (fromQuality.HasValue)
            {
                return fromQuality.Value;
            }

            if (edition != null && edition.IsEbook)
            {
                return BookMediaType.Ebook;
            }

            return book.MediaType;
        }

        private static string KnownQualityName(QualityModel quality)
        {
            var name = quality?.Quality?.Name;

            if (name.IsNullOrWhiteSpace() || name.StartsWith("Unknown", StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }

            return name;
        }

        private static string NarratorOf(Edition edition)
        {
            if (edition == null)
            {
                return null;
            }

            var names = (edition.NarratorNames ?? new List<string>())
                .Where(n => n.IsNotNullOrWhiteSpace())
                .Select(n => n.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            if (names.Any())
            {
                return string.Join(", ", names);
            }

            return edition.Narrator.IsNullOrWhiteSpace() ? null : edition.Narrator.Trim();
        }

        private static string SeriesOf(Book book)
        {
            if (book.SeriesName.IsNullOrWhiteSpace())
            {
                return null;
            }

            return book.SeriesPosition.IsNullOrWhiteSpace() ? book.SeriesName : $"{book.SeriesName} #{book.SeriesPosition}";
        }

        private static string AbsoluteUrl(string url)
        {
            if (url.IsNullOrWhiteSpace())
            {
                return null;
            }

            return Uri.TryCreate(url, UriKind.Absolute, out var uri) && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps)
                ? url
                : null;
        }

        private static string Shorten(string text)
        {
            if (text.IsNullOrWhiteSpace())
            {
                return null;
            }

            text = text.Trim();
            return text.Length <= OverviewLength ? text : text.Substring(0, OverviewLength - 3).TrimEnd() + "...";
        }
    }
}
