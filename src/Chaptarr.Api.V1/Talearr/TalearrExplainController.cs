using System;
using System.Collections.Generic;
using System.Linq;
using Chaptarr.Api.V1.Books;
using Chaptarr.Http;
using Microsoft.AspNetCore.Mvc;
using NzbDrone.Core.Books;
using NzbDrone.Core.Books.Services;
using NzbDrone.Core.Configuration;
using NzbDrone.Core.Talearr.Explain;

namespace Chaptarr.Api.V1.Talearr
{
    public class TalearrDecisionResource
    {
        public string Stage { get; set; }
        public string Outcome { get; set; }
        public string Code { get; set; }
        public string Summary { get; set; }
        public List<string> Details { get; set; } = new List<string>();
        public string ReleaseTitle { get; set; }
        public string DownloadId { get; set; }
        public DateTime CreatedAt { get; set; }
    }

    public class TalearrExplainResource
    {
        public int BookId { get; set; }
        public string Title { get; set; }
        public string MediaType { get; set; }
        public TalearrDecisionResource Metadata { get; set; }
        public List<TalearrDecisionResource> Steps { get; set; } = new List<TalearrDecisionResource>();
    }

    // Talearr: "Why did it grab or skip this?" under /api/v1/talearr/explain/{bookId}. The
    // metadata step is computed now; release and import steps are the recent recorded ones,
    // newest first. Paths, credentials and indexer URLs never appear in the text.
    [V1ApiController("talearr/explain")]
    public class TalearrExplainController : Controller
    {
        private readonly IBookService _bookService;
        private readonly IEditionService _editionService;
        private readonly IConfigService _configService;
        private readonly ITalearrDecisionStore _store;
        private readonly ITalearrMetadataExplainer _metadataExplainer;

        public TalearrExplainController(IBookService bookService,
                                        IEditionService editionService,
                                        IConfigService configService,
                                        ITalearrDecisionStore store,
                                        ITalearrMetadataExplainer metadataExplainer)
        {
            _bookService = bookService;
            _editionService = editionService;
            _configService = configService;
            _store = store;
            _metadataExplainer = metadataExplainer;
        }

        [HttpGet("{bookId:int}")]
        public ActionResult<TalearrExplainResource> Get(int bookId)
        {
            var book = _bookService.GetBooks(new[] { bookId }).FirstOrDefault();
            if (book == null)
            {
                return NotFound();
            }

            var editions = _editionService.GetEditionsByBook(bookId);
            var preferredLanguage = TalearrEditionPreference.ResolvePreferredLanguage(ReadUiLanguage());
            var metadata = _metadataExplainer.Explain(book, BookResourceMapper.GetNativeForeignBookId(book), editions, preferredLanguage);

            return new TalearrExplainResource
            {
                BookId = book.Id,
                Title = book.Title,
                MediaType = book.MediaType == BookMediaType.Ebook ? "ebook" : "audiobook",
                Metadata = ToResource(metadata),
                Steps = _store.GetForBook(bookId).Select(ToResource).ToList()
            };
        }

        private int? ReadUiLanguage()
        {
            try
            {
                return _configService.UILanguage;
            }
            catch (Exception)
            {
                return null;
            }
        }

        private static TalearrDecisionResource ToResource(TalearrDecisionEntry entry)
        {
            return new TalearrDecisionResource
            {
                Stage = entry.Stage,
                Outcome = entry.Outcome,
                Code = entry.Code,
                Summary = entry.Summary,
                Details = entry.Details ?? new List<string>(),
                ReleaseTitle = entry.ReleaseTitle,
                DownloadId = entry.DownloadId,
                CreatedAt = entry.CreatedAt
            };
        }
    }
}
