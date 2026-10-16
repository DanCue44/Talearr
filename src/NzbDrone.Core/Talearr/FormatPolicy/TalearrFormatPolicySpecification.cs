using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using NLog;
using NzbDrone.Core.Books;
using NzbDrone.Core.DecisionEngine;
using NzbDrone.Core.DecisionEngine.Specifications;
using NzbDrone.Core.IndexerSearch.Definitions;
using NzbDrone.Core.MediaFiles;
using NzbDrone.Core.Parser.Model;
using NzbDrone.Core.Qualities;
using NzbDrone.Core.Queue;
using NzbDrone.Core.Talearr.Recordings;

namespace NzbDrone.Core.Talearr.FormatPolicy
{
    // TaleArr: the format policy at grab time (docs/TALEARR_FORMAT_POLICY.md). A soft filter, so
    // interactive search shows the release as "Format policy" and can still grab it.
    //
    //   Audio only   ebook releases are skipped
    //   Ebook only   audiobook releases are skipped
    //   Prefer audio an ebook release is skipped while the book's audiobook is downloaded, being
    //                downloaded, or wanted and missing for less than the fallback time; after
    //                that the ebook is grabbed, and the audiobook stays wanted, so it is still
    //                grabbed when it appears (the ebook is kept)
    public class TalearrFormatPolicySpecification : IDecisionEngineSpecification
    {
        public const string Category = "Format policy";

        private static readonly TimeSpan CatalogCacheTime = TimeSpan.FromMinutes(2);

        private readonly ITalearrFormatPolicyService _policyService;
        private readonly IBookService _bookService;
        private readonly IAuthorService _authorService;
        private readonly IMediaFileService _mediaFileService;
        private readonly IQueueService _queueService;
        private readonly Logger _logger;
        private readonly ConcurrentDictionary<int, (DateTime At, List<Book> Books)> _catalogs = new ConcurrentDictionary<int, (DateTime, List<Book>)>();

        public TalearrFormatPolicySpecification(ITalearrFormatPolicyService policyService,
                                                IBookService bookService,
                                                IAuthorService authorService,
                                                IMediaFileService mediaFileService,
                                                IQueueService queueService,
                                                Logger logger)
        {
            _policyService = policyService;
            _bookService = bookService;
            _authorService = authorService;
            _mediaFileService = mediaFileService;
            _queueService = queueService;
            _logger = logger;
        }

        public SpecificationPriority Priority => SpecificationPriority.Default;
        public RejectionType Type => RejectionType.Permanent;

        public Func<DateTime> Now { get; set; } = () => DateTime.UtcNow;

        public Decision IsSatisfiedBy(RemoteBook subject, SearchCriteriaBase searchCriteria)
        {
            var books = subject?.Books?.Where(b => b != null).ToList();
            if (books == null || books.Count == 0)
            {
                return Decision.Accept();
            }

            var settings = TalearrFormatPolicyService.Resolve(_policyService);
            if (settings.Default == FormatPolicyMode.Both && (settings.Authors == null || settings.Authors.Values.All(m => m == FormatPolicyMode.Both)))
            {
                return Decision.Accept();
            }

            var releaseType = QualityMediaTypeHelper.DetectMediaType(subject.ParsedBookInfo?.Quality?.Quality, subject.Release);
            var targets = releaseType.HasValue ? books.Where(b => b.MediaType == releaseType.Value).ToList() : books;
            if (!targets.Any())
            {
                targets = books;
            }

            foreach (var book in targets)
            {
                var mode = settings.For(book.AuthorId);
                var reason = Check(book, mode, settings.FallbackHours);
                if (reason != null)
                {
                    _logger.Debug("Release {0} rejected: {1}", subject.Release?.Title, reason);
                    return Decision.RejectSoftFilter(reason, Category);
                }
            }

            return Decision.Accept();
        }

        internal string Check(Book book, FormatPolicyMode mode, int fallbackHours)
        {
            switch (mode)
            {
                case FormatPolicyMode.AudioOnly when book.MediaType == BookMediaType.Ebook:
                    return "Format policy: audiobooks only for this author";
                case FormatPolicyMode.EbookOnly when book.MediaType == BookMediaType.Audiobook:
                    return "Format policy: ebooks only for this author";
                case FormatPolicyMode.PreferAudio when book.MediaType == BookMediaType.Ebook:
                    return CheckPreferAudio(book, fallbackHours);
                default:
                    return null;
            }
        }

        private string CheckPreferAudio(Book ebook, int fallbackHours)
        {
            try
            {
                var audio = AudiobookOf(ebook);
                if (audio == null)
                {
                    return null;
                }

                if (_mediaFileService.GetFilesByBook(audio.Id)?.Any() == true)
                {
                    return "Format policy: prefer audiobook, and the audiobook is downloaded";
                }

                if (IsQueued(audio.Id))
                {
                    return "Format policy: prefer audiobook, and the audiobook is downloading";
                }

                var author = audio.Author ?? _authorService.GetAuthor(audio.AuthorId);
                if (!audio.IsMonitoredWithAuthor(author))
                {
                    // The audiobook is not wanted, so the ebook is the book.
                    return null;
                }

                var now = Now();
                var since = audio.Added > DateTime.MinValue ? audio.Added : now;
                var searched = audio.LastSearchTime.HasValue;
                var until = since.AddHours(fallbackHours);

                if (searched && now >= until)
                {
                    return null;
                }

                return searched
                    ? $"Format policy: prefer audiobook, waiting for the audiobook until {until:yyyy-MM-dd HH:mm} UTC before accepting the ebook"
                    : "Format policy: prefer audiobook, the audiobook has not been searched yet";
            }
            catch (Exception ex)
            {
                _logger.Debug(ex, "TaleArr: format policy check failed for book {0}; allowing the release", ebook.Id);
                return null;
            }
        }

        private Book AudiobookOf(Book ebook)
        {
            if (string.IsNullOrWhiteSpace(ebook.BaseBookId) || ebook.AuthorId <= 0)
            {
                return null;
            }

            var catalog = _catalogs.TryGetValue(ebook.AuthorId, out var cached) && Now() - cached.At < CatalogCacheTime
                ? cached.Books
                : null;

            if (catalog == null)
            {
                catalog = _bookService.GetBooksByAuthor(ebook.AuthorId) ?? new List<Book>();
                _catalogs[ebook.AuthorId] = (Now(), catalog);
            }

            return catalog
                .Where(b => b.MediaType == BookMediaType.Audiobook &&
                            string.Equals(b.BaseBookId, ebook.BaseBookId, StringComparison.OrdinalIgnoreCase) &&
                            !TalearrRecordingGroupService.IsCopySlug(b.TitleSlug))
                .OrderBy(b => b.Id)
                .FirstOrDefault();
        }

        private bool IsQueued(int bookId)
        {
            return (_queueService.GetQueue() ?? new List<Queue.Queue>())
                .Any(q => q.Book?.Id == bookId || q.RemoteBook?.Books?.Any(b => b.Id == bookId) == true);
        }
    }
}
