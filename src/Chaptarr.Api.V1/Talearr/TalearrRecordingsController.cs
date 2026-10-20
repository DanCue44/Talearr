using System.Collections.Generic;
using System.Linq;
using Chaptarr.Http;
using Microsoft.AspNetCore.Mvc;
using NzbDrone.Core.Books;
using NzbDrone.Core.Messaging.Commands;
using NzbDrone.Core.Talearr.Recordings;

namespace Chaptarr.Api.V1.Talearr
{
    public class TalearrAddRecordingResource
    {
        public int BookId { get; set; }

        // unabridged | abridged | narrator | edition
        public string Kind { get; set; }
        public int? EditionId { get; set; }
        public string Narrator { get; set; }

        // Omitted: the talearr.recordings.searchonadd setting decides (on by default).
        public bool? Search { get; set; }

        // ui | allseerr | api | ...; kept as provenance in talearr.db.
        public string RequestedBy { get; set; }
        public string ExternalRef { get; set; }
    }

    public class TalearrAddRecordingResult
    {
        // created | pinned | existing
        public string Action { get; set; }
        public bool SearchQueued { get; set; }
        public RecordingInfo Recording { get; set; }
    }

    public class TalearrRecordingProblem
    {
        public string Message { get; set; }
        public List<RecordingCandidateInfo> Candidates { get; set; } = new List<RecordingCandidateInfo>();
    }

    // Talearr: recordings routes under /api/v1/talearr/recordings. New routes only; no existing
    // route, field or status code changes. Local ids in responses are handles; clients should
    // store baseBookId / foreignEditionId / asin.
    [V1ApiController("talearr/recordings")]
    public class TalearrRecordingsController : Controller
    {
        private readonly ITalearrRecordingsService _recordingsService;
        private readonly ITalearrRecordingGroupService _groupService;
        private readonly IBookService _bookService;
        private readonly IManageCommandQueue _commandQueueManager;

        public TalearrRecordingsController(ITalearrRecordingsService recordingsService,
                                           ITalearrRecordingGroupService groupService,
                                           IBookService bookService,
                                           IManageCommandQueue commandQueueManager)
        {
            _recordingsService = recordingsService;
            _groupService = groupService;
            _bookService = bookService;
            _commandQueueManager = commandQueueManager;
        }

        public static readonly global::System.TimeSpan FirstLookupTimeout = global::System.TimeSpan.FromSeconds(8);

        // bookId may be any row of the work, ebook rows included; or providerId (hc:..., gr:..., ...).
        [HttpGet]
        public ActionResult<RecordingsView> GetRecordings([FromQuery] int? bookId, [FromQuery] string providerId, [FromQuery] bool allLanguages = false)
        {
            var book = FindBook(bookId, providerId);
            if (book == null)
            {
                return NotFound(new TalearrRecordingProblem { Message = "Book not found" });
            }

            // Talearr: a book added a moment ago gets its first Audible lookup here (at most a few
            // seconds), so AllSeerr sees abridged candidates right after the add.
            var view = _recordingsService.GetRecordingsWithFacts(book, allLanguages, FirstLookupTimeout);
            if (view == null)
            {
                return NotFound(new TalearrRecordingProblem { Message = "This work has no audiobook recordings" });
            }

            return view;
        }

        // 201 created (or pinned, when the book had no files yet), 200 existing, 404 no edition fits,
        // 409 several fit equally, 400 bad input or an ebook-only work.
        [HttpPost]
        public IActionResult AddRecording([FromBody] TalearrAddRecordingResource resource)
        {
            var outcome = _recordingsService.AddRecording(new AddRecordingRequest
            {
                BookId = resource?.BookId ?? 0,
                Kind = resource?.Kind,
                EditionId = resource?.EditionId,
                Narrator = resource?.Narrator,
                Search = resource?.Search,
                RequestedBy = resource?.RequestedBy,
                ExternalRef = resource?.ExternalRef
            });

            switch (outcome.Status)
            {
                case AddRecordingStatus.Created:
                case AddRecordingStatus.Pinned:
                    return StatusCode(201, Result(outcome));
                case AddRecordingStatus.Existing:
                    return Ok(Result(outcome));
                case AddRecordingStatus.NoMatch:
                    return NotFound(Problem(outcome));
                case AddRecordingStatus.Ambiguous:
                    return Conflict(Problem(outcome));
                default:
                    return BadRequest(Problem(outcome));
            }
        }

        // Manual "remove this recording": deletes an additional recording's book row (and its files
        // when asked). The primary recording is refused; nothing here ever runs automatically.
        [HttpDelete("{bookId:int}")]
        public IActionResult RemoveRecording(int bookId, [FromQuery] bool deleteFiles = false)
        {
            var outcome = _recordingsService.RemoveRecording(bookId, deleteFiles);
            switch (outcome.Status)
            {
                case RemoveRecordingStatus.Removed:
                    return Ok(new { removed = bookId, deleteFiles });
                case RemoveRecordingStatus.Refused:
                    return Conflict(new TalearrRecordingProblem { Message = outcome.Message });
                default:
                    return NotFound(new TalearrRecordingProblem { Message = outcome.Message });
            }
        }

        // Refetches Audible facts for every recording of the book's work, ignoring the cache.
        [HttpPost("refresh")]
        public IActionResult Refresh([FromQuery] int bookId)
        {
            var group = bookId > 0 ? _groupService.GetGroup(bookId) : null;
            if (group == null)
            {
                return NotFound(new TalearrRecordingProblem { Message = "No audiobook recordings for that book" });
            }

            var command = _commandQueueManager.Push(new RefreshAudibleRecordingFactsCommand(group.Books.Select(b => b.Id).ToList(), force: true));

            return Accepted(new { commandId = command?.Id, bookIds = group.Books.Select(b => b.Id).ToList() });
        }

        private Book FindBook(int? bookId, string providerId)
        {
            if (bookId.HasValue && bookId.Value > 0)
            {
                return _bookService.GetBook(bookId.Value);
            }

            if (string.IsNullOrWhiteSpace(providerId) || !providerId.Contains(':'))
            {
                return null;
            }

            var colon = providerId.IndexOf(':');
            var provider = providerId.Substring(0, colon).Trim();
            var id = providerId.Substring(colon + 1).Trim();

            return _bookService.FindAllByProviderId(provider, id, BookMediaType.Audiobook).OrderBy(b => b.Id).FirstOrDefault()
                   ?? _bookService.FindAllByProviderId(provider, id, BookMediaType.Ebook).OrderBy(b => b.Id).FirstOrDefault();
        }

        private static TalearrAddRecordingResult Result(AddRecordingOutcome outcome)
        {
            return new TalearrAddRecordingResult
            {
                Action = outcome.Status.ToString().ToLowerInvariant(),
                SearchQueued = outcome.SearchQueued,
                Recording = outcome.Recording
            };
        }

        private static TalearrRecordingProblem Problem(AddRecordingOutcome outcome)
        {
            return new TalearrRecordingProblem { Message = outcome.Message, Candidates = outcome.Candidates };
        }
    }
}
