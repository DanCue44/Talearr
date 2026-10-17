using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Xml.Linq;
using Chaptarr.Http.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using NLog;
using NzbDrone.Core.MediaCover;
using NzbDrone.Core.Organizer;
using NzbDrone.Core.Talearr.EbookDelivery;
using NzbDrone.Core.Talearr.ReaderHandoff;
using IOFile = System.IO.File;

namespace Chaptarr.Api.V1.Talearr
{
    // TaleArr: read-only OPDS 1.2 catalog of the ebook library, for KOReader, Panels, Thorium,
    // Moon+ Reader and other OPDS readers. Off by default (404). Signs in with HTTP Basic or
    // ?apikey= (see TalearrOpdsAccess). New routes outside /api; nothing upstream changes.
    //   GET /opds                       root: recent, authors, series, all books, search
    //   GET /opds/opensearch.xml        OpenSearch description
    //   GET /opds/recent|books[?page=]  acquisition feeds
    //   GET /opds/authors|series        navigation feeds, then /opds/authors/{id}, /opds/series/{id}
    //   GET /opds/search?q=             search by title, author or series
    //   GET /opds/file/{fileId}[/kepub] the ebook file (KEPUB converted on the fly when offered)
    //   GET /opds/cover/{bookId}        cover image
    [Route("opds")]
    [Authorize(AuthenticationSchemes = TalearrOpdsAuthenticationHandler.SchemeName)]
    public class TalearrOpdsController : Controller
    {
        private static readonly Logger Logger = LogManager.GetCurrentClassLogger();

        private readonly ITalearrOpdsSettingsService _settingsService;
        private readonly ITalearrOpdsLibrary _library;
        private readonly ITalearrKepubify _kepubify;
        private readonly IMapCoversToLocal _covers;

        public TalearrOpdsController(ITalearrOpdsSettingsService settingsService,
                                     ITalearrOpdsLibrary library,
                                     ITalearrKepubify kepubify,
                                     IMapCoversToLocal covers)
        {
            _settingsService = settingsService;
            _library = library;
            _kepubify = kepubify;
            _covers = covers;
        }

        [HttpGet("")]
        public IActionResult Root()
        {
            return Feed(TalearrOpdsFeedWriter.Root(Links(), _library.Get()), TalearrOpdsFeedWriter.NavigationType);
        }

        [HttpGet("opensearch.xml")]
        public IActionResult OpenSearch()
        {
            return Feed(TalearrOpdsFeedWriter.OpenSearchDescription(Links()), TalearrOpdsFeedWriter.OpenSearchType);
        }

        [HttpGet("recent")]
        public IActionResult Recent([FromQuery] int page = 1)
        {
            var snapshot = _library.Get();
            return Books("urn:talearr:opds:recent", "Recently added", "recent", snapshot.Recent(), page, snapshot.Updated, null);
        }

        [HttpGet("books")]
        public IActionResult AllBooks([FromQuery] int page = 1)
        {
            var snapshot = _library.Get();
            return Books("urn:talearr:opds:books", "All books", "books", snapshot.AllBooks(), page, snapshot.Updated, null);
        }

        [HttpGet("authors")]
        public IActionResult Authors([FromQuery] int page = 1)
        {
            var snapshot = _library.Get();
            var groups = TalearrOpdsPage<TalearrOpdsGroup>.Of(snapshot.Authors, page, _settingsService.Get().PageSize);

            return Feed(TalearrOpdsFeedWriter.Groups(Links(), "urn:talearr:opds:authors", "Authors", "authors", groups, "authors", snapshot.Updated), TalearrOpdsFeedWriter.NavigationType);
        }

        [HttpGet("authors/{authorId:int}")]
        public IActionResult Author(int authorId, [FromQuery] int page = 1)
        {
            var snapshot = _library.Get();
            var author = snapshot.Authors.FirstOrDefault(a => a.Id == authorId);

            if (author == null)
            {
                return NotFound();
            }

            return Books($"urn:talearr:opds:authors:{authorId}", author.Title, $"authors/{authorId}", snapshot.ByAuthor(authorId), page, author.Updated, Links().To("authors"));
        }

        [HttpGet("series")]
        public IActionResult Series([FromQuery] int page = 1)
        {
            var snapshot = _library.Get();
            var groups = TalearrOpdsPage<TalearrOpdsGroup>.Of(snapshot.Series, page, _settingsService.Get().PageSize);

            return Feed(TalearrOpdsFeedWriter.Groups(Links(), "urn:talearr:opds:series", "Series", "series", groups, "series", snapshot.Updated), TalearrOpdsFeedWriter.NavigationType);
        }

        [HttpGet("series/{seriesId:int}")]
        public IActionResult OneSeries(int seriesId, [FromQuery] int page = 1)
        {
            var snapshot = _library.Get();
            var series = snapshot.Series.FirstOrDefault(s => s.Id == seriesId);

            if (series == null)
            {
                return NotFound();
            }

            return Books($"urn:talearr:opds:series:{seriesId}", series.Title, $"series/{seriesId}", snapshot.BySeries(seriesId), page, series.Updated, Links().To("series"));
        }

        [HttpGet("search")]
        public IActionResult Search([FromQuery] string q = null, [FromQuery] int page = 1)
        {
            var snapshot = _library.Get();
            var query = (q ?? string.Empty).Trim();
            if (query.Length > 200)
            {
                query = query.Substring(0, 200);
            }

            return Books("urn:talearr:opds:search", $"Search: {query}", "search", snapshot.Search(query), page, snapshot.Updated, null, ("q", query));
        }

        [HttpGet("file/{fileId:int}")]
        public IActionResult Download(int fileId)
        {
            var (book, file) = _library.Get().FindFile(fileId);

            if (file == null || !IOFile.Exists(file.Path))
            {
                return NotFound();
            }

            return PhysicalFile(file.Path, TalearrOpdsFeedWriter.MimeType(file.Path), DownloadName(book, file.Path), true);
        }

        [HttpGet("file/{fileId:int}/kepub")]
        public IActionResult Kepub(int fileId)
        {
            var (book, file) = _library.Get().FindFile(fileId);

            if (!_settingsService.Get().OfferKepub || file == null || !TalearrReaderHandoffService.IsConvertibleEpub(file.Path) || !IOFile.Exists(file.Path))
            {
                return NotFound();
            }

            var executable = _kepubify.FindExecutable();
            if (executable == null)
            {
                // Without kepubify the plain EPUB is the best there is; Kobo reads it too.
                return PhysicalFile(file.Path, TalearrOpdsFeedWriter.MimeType(file.Path), DownloadName(book, file.Path), true);
            }

            var temp = Path.Combine(Path.GetTempPath(), "talearr-opds-" + Guid.NewGuid().ToString("N") + ".kepub.epub");
            var error = _kepubify.Convert(executable, file.Path, temp);

            if (error != null || !IOFile.Exists(temp))
            {
                Logger.Warn("TaleArr: OPDS KEPUB conversion of '{0}' failed ({1}); sending the EPUB", Path.GetFileName(file.Path), error ?? "no output");
                TryDelete(temp);
                return PhysicalFile(file.Path, TalearrOpdsFeedWriter.MimeType(file.Path), DownloadName(book, file.Path), true);
            }

            // The converted copy is deleted as soon as the response stream is closed.
            var stream = new FileStream(temp, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete, 81920, FileOptions.DeleteOnClose);
            return File(stream, TalearrOpdsFeedWriter.KepubType, Path.GetFileNameWithoutExtension(DownloadName(book, file.Path)) + ".kepub.epub");
        }

        [HttpGet("cover/{bookId:int}")]
        public IActionResult Cover(int bookId)
        {
            if (_library.Get().FindBook(bookId) == null)
            {
                return NotFound();
            }

            foreach (var height in new int?[] { 500, null })
            {
                var path = _covers.GetCoverPath(bookId, MediaCoverEntity.Book, MediaCoverTypes.Cover, ".jpg", height);
                if (IOFile.Exists(path))
                {
                    return PhysicalFile(path, "image/jpeg");
                }
            }

            return NotFound();
        }

        private IActionResult Books(string id, string title, string path, List<TalearrOpdsBook> books, int page, DateTime updated, string up, params (string Name, string Value)[] query)
        {
            var settings = _settingsService.Get();
            var paged = TalearrOpdsPage<TalearrOpdsBook>.Of(books, page, settings.PageSize);

            return Feed(TalearrOpdsFeedWriter.Books(Links(), id, title, path, paged, updated, settings.OfferKepub, up, query), TalearrOpdsFeedWriter.AcquisitionType);
        }

        private TalearrOpdsLinks Links()
        {
            // Carry ?apikey= on only when the reader used it; a Basic sign-in keeps the key out of URLs.
            var apiKey = Request.Query["apikey"].FirstOrDefault();
            return new TalearrOpdsLinks(Request.PathBase.Value + "/opds", apiKey);
        }

        private ContentResult Feed(XDocument document, string type)
        {
            return Content(TalearrOpdsFeedWriter.Serialize(document), type + "; charset=utf-8");
        }

        private static string DownloadName(TalearrOpdsBook book, string path)
        {
            var name = string.IsNullOrWhiteSpace(book?.AuthorName) ? book?.Title : $"{book.AuthorName} - {book.Title}";
            var clean = FileNameBuilder.CleanFileName(name ?? string.Empty).Trim();
            var extension = path.EndsWith(".kepub.epub", StringComparison.OrdinalIgnoreCase) ? ".kepub.epub" : Path.GetExtension(path);

            return (clean.Length == 0 ? Path.GetFileNameWithoutExtension(path) : clean) + extension;
        }

        private static void TryDelete(string path)
        {
            try
            {
                if (IOFile.Exists(path))
                {
                    IOFile.Delete(path);
                }
            }
            catch (Exception ex)
            {
                Logger.Debug(ex, "TaleArr: could not remove '{0}'", path);
            }
        }
    }
}
