using System;
using System.Collections.Generic;
using System.Linq;
using NLog;
using NzbDrone.Core.Books;
using NzbDrone.Core.MediaFiles;

namespace NzbDrone.Core.Talearr.ReaderHandoff
{
    public sealed class TalearrReaderHandoffFailure
    {
        public int BookFileId { get; set; }
        public int BookId { get; set; }
        public string Title { get; set; }
        public string FileName { get; set; }
        public string Error { get; set; }
        public int Attempts { get; set; }
        public DateTime UpdatedAt { get; set; }
    }

    public sealed class TalearrReaderHandoffRetryResult
    {
        public int Sent { get; set; }
        public int Failed { get; set; }
        public int Removed { get; set; }
    }

    public interface ITalearrReaderHandoffRetryService
    {
        List<TalearrReaderHandoffFailure> GetFailures();

        // Copies every failed hand-off again. Records for library files that are gone are dropped.
        TalearrReaderHandoffRetryResult RetryFailed();

        // Sends a book's ebook files to the reader folder again, even if they were sent before.
        TalearrReaderHandoffRetryResult Resend(int bookId);
    }

    // Talearr: retry and re-send for the reader hand-off, without downloading anything again.
    public class TalearrReaderHandoffRetryService : ITalearrReaderHandoffRetryService
    {
        private readonly ITalearrReaderHandoffStore _store;
        private readonly ITalearrReaderHandoffService _handoffService;
        private readonly ITalearrReaderHandoffSettingsService _settingsService;
        private readonly IMediaFileService _mediaFileService;
        private readonly IBookService _bookService;
        private readonly Logger _logger;

        public TalearrReaderHandoffRetryService(ITalearrReaderHandoffStore store,
                                                ITalearrReaderHandoffService handoffService,
                                                ITalearrReaderHandoffSettingsService settingsService,
                                                IMediaFileService mediaFileService,
                                                IBookService bookService,
                                                Logger logger)
        {
            _store = store;
            _handoffService = handoffService;
            _settingsService = settingsService;
            _mediaFileService = mediaFileService;
            _bookService = bookService;
            _logger = logger;
        }

        public List<TalearrReaderHandoffFailure> GetFailures()
        {
            return _store.GetFailed().Select(f => new TalearrReaderHandoffFailure
            {
                BookFileId = f.BookFileId,
                BookId = f.BookId,
                Title = TitleOf(f.BookId),
                FileName = System.IO.Path.GetFileName(f.SourcePath),
                Error = f.LastError,
                Attempts = f.Attempts,
                UpdatedAt = f.UpdatedAt
            }).ToList();
        }

        public TalearrReaderHandoffRetryResult RetryFailed()
        {
            var result = new TalearrReaderHandoffRetryResult();
            var settings = _settingsService.Get();

            foreach (var failure in _store.GetFailed())
            {
                var file = FindFile(failure.BookFileId);

                if (file == null)
                {
                    _store.Remove(failure.BookFileId);
                    result.Removed++;
                    continue;
                }

                Send(file.Author, new[] { file }, settings, result);
            }

            return result;
        }

        public TalearrReaderHandoffRetryResult Resend(int bookId)
        {
            var result = new TalearrReaderHandoffRetryResult();
            var files = _mediaFileService.GetFilesByBook(bookId)
                .Where(TalearrReaderHandoffService.IsEbook)
                .ToList();

            if (files.Count > 0)
            {
                Send(files[0].Author ?? _bookService.GetBook(bookId)?.Author, files, _settingsService.Get(), result);
            }

            return result;
        }

        private void Send(Author author, IList<BookFile> files, TalearrReaderHandoffSettings settings, TalearrReaderHandoffRetryResult result)
        {
            try
            {
                var written = _handoffService.HandOff(author, files, settings, force: true);
                result.Sent += written.Count;
                result.Failed += files.Count - written.Count;
            }
            catch (Exception ex)
            {
                _logger.Warn(ex, "Talearr: reader hand-off retry failed");
                result.Failed += files.Count;
            }
        }

        private BookFile FindFile(int bookFileId)
        {
            try
            {
                return _mediaFileService.Get(bookFileId);
            }
            catch (Exception)
            {
                return null;
            }
        }

        private string TitleOf(int bookId)
        {
            if (bookId <= 0)
            {
                return null;
            }

            try
            {
                return _bookService.GetBook(bookId)?.Title;
            }
            catch (Exception)
            {
                return null;
            }
        }
    }
}
