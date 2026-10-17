using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using NLog;
using NzbDrone.Common.Exceptions;
using NzbDrone.Common.Instrumentation.Extensions;
using NzbDrone.Core.Books;
using NzbDrone.Core.Configuration;
using NzbDrone.Core.MediaFiles;
using NzbDrone.Core.MediaFiles.Events;
using NzbDrone.Core.Messaging.Commands;
using NzbDrone.Core.Messaging.Events;
using NzbDrone.Core.Organizer;
using NzbDrone.Core.Qualities;
using NzbDrone.Core.Talearr.Metadata;
using NzbDrone.Core.Talearr.Naming;

namespace NzbDrone.Core.Talearr.Audiobook.M4b
{
    public class TalearrM4bException : NzbDroneException
    {
        public TalearrM4bException(string message)
            : base(message)
        {
        }
    }

    // TaleArr: library side of the M4B merge. Runs the TalearrConvertToM4b command (the manual
    // "Convert to M4B" action and the optional after-import conversion), swaps the verified M4B
    // in for the original files and only then removes them (to the recycle bin when one is set).
    public class TalearrM4bService : IExecute<TalearrConvertToM4bCommand>, IExecute<TalearrConvertLibraryToM4bCommand>, IHandle<BookImportedEvent>
    {
        private static readonly string[] CoverNames = { "cover.jpg", "cover.jpeg", "cover.png", "folder.jpg", "folder.png" };

        private readonly IBookService _bookService;
        private readonly IAuthorService _authorService;
        private readonly IEditionService _editionService;
        private readonly IMediaFileService _mediaFileService;
        private readonly IRecycleBinProvider _recycleBinProvider;
        private readonly IConfigService _configService;
        private readonly ITalearrM4bSettingsService _settingsService;
        private readonly ITalearrM4bConverter _converter;
        private readonly IManageCommandQueue _commandQueueManager;
        private readonly IEventAggregator _eventAggregator;
        private readonly Logger _logger;

        public TalearrM4bService(IBookService bookService,
                                 IAuthorService authorService,
                                 IEditionService editionService,
                                 IMediaFileService mediaFileService,
                                 IRecycleBinProvider recycleBinProvider,
                                 IConfigService configService,
                                 ITalearrM4bSettingsService settingsService,
                                 ITalearrM4bConverter converter,
                                 IManageCommandQueue commandQueueManager,
                                 Logger logger,
                                 IEventAggregator eventAggregator = null)
        {
            _eventAggregator = eventAggregator;
            _bookService = bookService;
            _authorService = authorService;
            _editionService = editionService;
            _mediaFileService = mediaFileService;
            _recycleBinProvider = recycleBinProvider;
            _configService = configService;
            _settingsService = settingsService;
            _converter = converter;
            _commandQueueManager = commandQueueManager;
            _logger = logger;
        }

        public void Handle(BookImportedEvent message)
        {
            try
            {
                if (message?.Book == null || !_settingsService.Get().ConvertAfterImport)
                {
                    return;
                }

                var mergeable = (message.ImportedBooks ?? new List<BookFile>())
                    .Count(f => f?.Path != null && M4bLibraryPlanner.ConvertibleExtensions.Contains(Path.GetExtension(f.Path)));

                if (mergeable < 2)
                {
                    return;
                }

                _logger.Info("TaleArr: queueing M4B conversion of '{0}' after import ({1} files)", message.Book.Title, mergeable);
                _commandQueueManager.Push(new TalearrConvertToM4bCommand { BookId = message.Book.Id, AfterImport = true });
            }
            catch (Exception ex)
            {
                _logger.Warn(ex, "TaleArr: could not queue the M4B conversion after import");
            }
        }

        public void Execute(TalearrConvertToM4bCommand message)
        {
            var book = _bookService.GetBook(message.BookId);
            var author = book.Author ?? _authorService.GetAuthor(book.AuthorId);
            var result = ConvertBook(book, author, string.Empty);

            if (result.Errors.Any())
            {
                throw new TalearrM4bException("M4B conversion failed, the original files were kept. " + string.Join(" ", result.Errors));
            }
        }

        // TaleArr: the existing library. Finds every audiobook with several mp3/m4a/m4b files of one
        // edition and converts them one after another; a failed book keeps its files and the run
        // goes on. Needs FFmpeg up front, so nothing is half done when it is missing.
        public void Execute(TalearrConvertLibraryToM4bCommand message)
        {
            if (!_converter.IsAvailable())
            {
                throw new TalearrM4bException("FFmpeg and FFprobe are needed to convert to M4B and were not found. Nothing was changed.");
            }

            _logger.ProgressInfo("Looking for multi-file audiobooks to convert to M4B");

            var candidates = FindLibraryCandidates(message.AuthorId);
            if (candidates.Count == 0)
            {
                _logger.ProgressInfo("No multi-file audiobooks to convert to M4B");
                return;
            }

            var converted = 0;
            var failed = new List<string>();

            for (var i = 0; i < candidates.Count; i++)
            {
                var (book, author) = candidates[i];
                var prefix = string.Format(CultureInfo.InvariantCulture, "[{0}/{1}] ", i + 1, candidates.Count);

                try
                {
                    var result = ConvertBook(book, author, prefix);
                    converted += result.Converted;
                    failed.AddRange(result.Errors);
                }
                catch (Exception ex)
                {
                    _logger.Warn(ex, "TaleArr: M4B conversion of '{0}' failed, original files kept", book.Title);
                    failed.Add($"{book.Title}: {ex.Message}");
                }
            }

            var summary = string.Format(CultureInfo.InvariantCulture, "Converted {0} of {1} audiobooks to M4B", converted, candidates.Count);
            if (failed.Any())
            {
                throw new TalearrM4bException($"{summary}; {failed.Count} failed and kept their original files. " + string.Join(" ", failed));
            }

            _logger.ProgressInfo(summary);
        }

        internal List<(Book Book, Author Author)> FindLibraryCandidates(int authorId)
        {
            var authors = authorId > 0
                ? new List<Author> { _authorService.GetAuthor(authorId) }
                : _authorService.GetAllAuthors() ?? new List<Author>();

            var candidates = new List<(Book, Author)>();
            foreach (var author in authors.Where(a => a != null).OrderBy(a => a.Name, StringComparer.OrdinalIgnoreCase))
            {
                var books = (_bookService.GetBooksByAuthor(author.Id) ?? new List<Book>())
                    .Where(b => b != null && b.MediaType == BookMediaType.Audiobook)
                    .OrderBy(b => b.Title, StringComparer.OrdinalIgnoreCase);

                foreach (var book in books)
                {
                    if (M4bLibraryPlanner.Plan(_mediaFileService.GetFilesByBook(book.Id)).Any(p => p.CanConvert))
                    {
                        book.Author ??= author;
                        candidates.Add((book, author));
                    }
                }
            }

            return candidates;
        }

        private (int Converted, List<string> Errors) ConvertBook(Book book, Author author, string prefix)
        {
            var plans = M4bLibraryPlanner.Plan(_mediaFileService.GetFilesByBook(book.Id));
            var convertible = plans.Where(p => p.CanConvert).ToList();
            var errors = new List<string>();

            if (convertible.Count == 0)
            {
                var reason = plans.Select(p => p.SkipReason).FirstOrDefault() ?? "The book has no audio files.";
                _logger.ProgressInfo("{0}Nothing to convert for '{1}': {2}", prefix, book.Title, reason);
                return (0, errors);
            }

            if (!_converter.IsAvailable())
            {
                throw new TalearrM4bException("FFmpeg and FFprobe are needed to convert to M4B and were not found.");
            }

            var converted = 0;

            foreach (var plan in convertible)
            {
                var edition = GetEdition(plan.EditionId);
                var job = BuildJob(book, author, edition, plan);
                var title = edition?.Title ?? book.Title;
                var lastPercent = -10.0;

                _logger.ProgressInfo("{0}Converting '{1}' to M4B ({2} files)", prefix, title, plan.Files.Count);

                var outcome = _converter.Convert(job, (percent, text) =>
                {
                    if (percent - lastPercent >= 5 || percent >= 98)
                    {
                        lastPercent = percent;
                        _logger.ProgressInfo("{0}Converting '{1}' to M4B: {2}", prefix, title, text);
                    }
                });

                // TaleArr: the converter already checked the length; check its answer again before
                // any original is touched, so a converter bug can never cost the library its files.
                if (outcome.Success && !M4bFfmpegCommand.IsDurationWithinTolerance(outcome.ExpectedDurationSeconds, outcome.DurationSeconds))
                {
                    TryDelete(outcome.OutputPath);
                    outcome = M4bConversionOutcome.Fail(string.Format(CultureInfo.InvariantCulture,
                        "The M4B length ({0:0} s) does not match the source files ({1:0} s).", outcome.DurationSeconds, outcome.ExpectedDurationSeconds));
                }

                if (!outcome.Success)
                {
                    _logger.Warn("TaleArr: M4B conversion of '{0}' failed, original files kept: {1}", title, outcome.Error);
                    errors.Add($"{title}: {outcome.Error}");
                    continue;
                }

                var merged = ReplaceLibraryFiles(plan, outcome, author, edition);
                converted++;

                _logger.ProgressInfo("{0}Converted '{1}' to M4B: {2} chapters, {3}", prefix, title, outcome.ChapterCount, FormatDuration(outcome.DurationSeconds));

                PublishImported(author, book, merged, plan.Files);
            }

            return (converted, errors);
        }

        // TaleArr: tell the rest of TaleArr (book duration, statistics) and the notifications (media
        // server rescans, "upgrade" messages) about the swap, like an import that replaced files.
        private void PublishImported(Author author, Book book, BookFile merged, List<BookFile> oldFiles)
        {
            if (_eventAggregator == null)
            {
                return;
            }

            try
            {
                _eventAggregator.PublishEvent(new BookImportedEvent(author, book, new List<BookFile> { merged }, oldFiles, true, null));
            }
            catch (Exception ex)
            {
                _logger.Warn(ex, "TaleArr: the M4B was imported but the import notifications failed for '{0}'", book.Title);
            }
        }

        internal M4bConversionJob BuildJob(Book book, Author author, Edition edition, M4bLibraryPlan plan)
        {
            // The file is named like the book folder: the book's own title without format noise.
            var title = TalearrBookTitle.ForNaming(book, edition);

            var fileName = FileNameBuilder.CleanFileName(title);
            if (string.IsNullOrWhiteSpace(fileName))
            {
                fileName = "Audiobook";
            }

            var outputPath = Path.Combine(plan.Folder, fileName + ".m4b");
            if (plan.Files.Any(f => string.Equals(f.Path, outputPath, StringComparison.OrdinalIgnoreCase)))
            {
                outputPath = Path.Combine(plan.Folder, fileName + " (merged).m4b");
            }

            var narrator = edition?.Narrator;
            if (string.IsNullOrWhiteSpace(narrator) && edition?.NarratorNames?.Any() == true)
            {
                narrator = string.Join(", ", edition.NarratorNames.Where(n => !string.IsNullOrWhiteSpace(n)));
            }

            if (string.IsNullOrWhiteSpace(narrator))
            {
                narrator = plan.Files.Select(f => f.Narrator).FirstOrDefault(n => !string.IsNullOrWhiteSpace(n));
            }

            // The recording's own release year first (what players show for an audiobook), then
            // the book's first publication.
            int? year = edition?.ReleaseDate?.Year > 1 ? edition.ReleaseDate.Value.Year : TalearrNamingTokens.GetYear(edition ?? new Edition { Book = book });

            return new M4bConversionJob
            {
                Inputs = plan.Files.Select(f => f.Path).ToList(),
                OutputPath = outputPath,
                CoverPath = CoverNames.Select(n => Path.Combine(plan.Folder, n)).FirstOrDefault(File.Exists),
                Metadata = new M4bMetadata
                {
                    Title = title,
                    Author = TalearrSidecarText.Name(author?.Name),
                    Narrator = TalearrSidecarText.Name(narrator),
                    Year = year?.ToString(CultureInfo.InvariantCulture),
                    Description = StripHtml(string.IsNullOrWhiteSpace(edition?.Overview) ? book.Overview : edition.Overview),
                    Publisher = edition?.Publisher,
                    Series = book.SeriesName,
                    SeriesPosition = book.SeriesPosition,
                    Language = edition?.Language,
                    Asin = edition?.Asin,
                    Isbn = edition?.Isbn13
                },
                Encode = new M4bEncodeSettings
                {
                    BitrateKbps = _configService.AudiobookConversionMaxBitrate,
                    NoUpscale = _configService.AudiobookConversionNoUpscale,
                    Mono = string.Equals(_configService.AudiobookConversionAudioChannels, "mono", StringComparison.OrdinalIgnoreCase)
                }
            };
        }

        private BookFile ReplaceLibraryFiles(M4bLibraryPlan plan, M4bConversionOutcome outcome, Author author, Edition edition)
        {
            var first = plan.Files[0];
            var durationSeconds = (int)Math.Round(outcome.DurationSeconds);

            var merged = new BookFile
            {
                Path = outcome.OutputPath,
                Size = outcome.OutputSize,
                Modified = File.GetLastWriteTimeUtc(outcome.OutputPath),
                DateAdded = DateTime.UtcNow,
                OriginalFilePath = first.OriginalFilePath,
                SceneName = first.SceneName,
                ReleaseGroup = first.ReleaseGroup,
                Quality = new QualityModel(Quality.M4B, first.Quality?.Revision ?? new Revision()),
                IndexerFlags = first.IndexerFlags,
                MediaInfo = MediaDuration.CreateMediaInfo(durationSeconds),
                DurationSeconds = durationSeconds,
                EditionId = plan.EditionId,
                Part = 1,
                PartCount = 1,
                IsGraphicAudio = first.IsGraphicAudio,
                AudioProductionType = first.AudioProductionType,
                Narrator = first.Narrator,
                MediaType = "audiobook",
                MatchProvenance = first.MatchProvenance,
                Author = author,
                Edition = edition
            };

            try
            {
                _mediaFileService.Add(merged);
            }
            catch (Exception)
            {
                // The originals are still the library's files; drop the unregistered M4B.
                TryDelete(outcome.OutputPath);
                throw;
            }

            _mediaFileService.DeleteMany(plan.Files, DeleteMediaFileReason.Upgrade);

            foreach (var file in plan.Files)
            {
                try
                {
                    _recycleBinProvider.DeleteFile(file.Path);
                }
                catch (Exception ex)
                {
                    _logger.Warn(ex, "TaleArr: the M4B was imported but the original file could not be removed: {0}", file.Path);
                }
            }

            RemoveEmptyDiscFolders(plan);
            return merged;
        }

        // Disc folders (CD1, Part 2) below the book folder that are empty after the merge.
        private void RemoveEmptyDiscFolders(M4bLibraryPlan plan)
        {
            var folders = plan.Files
                .Select(f => Path.GetDirectoryName(f.Path))
                .Where(f => !string.IsNullOrWhiteSpace(f))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Where(f => !string.Equals(f.TrimEnd('/', '\\'), plan.Folder.TrimEnd('/', '\\'), StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(f => f.Length);

            foreach (var folder in folders)
            {
                try
                {
                    if (Directory.Exists(folder) && !Directory.EnumerateFileSystemEntries(folder).Any())
                    {
                        Directory.Delete(folder);
                    }
                }
                catch (Exception ex)
                {
                    _logger.Debug(ex, "Could not remove empty folder {0}", folder);
                }
            }
        }

        private Edition GetEdition(int editionId)
        {
            try
            {
                return editionId > 0 ? _editionService.GetEdition(editionId) : null;
            }
            catch (Exception ex)
            {
                _logger.Debug(ex, "Edition {0} not found", editionId);
                return null;
            }
        }

        private void TryDelete(string path)
        {
            try
            {
                if (File.Exists(path))
                {
                    File.Delete(path);
                }
            }
            catch (Exception ex)
            {
                _logger.Debug(ex, "Could not delete {0}", path);
            }
        }

        private static string StripHtml(string text)
        {
            if (string.IsNullOrWhiteSpace(text))
            {
                return text;
            }

            var blocks = System.Text.RegularExpressions.Regex.Replace(text, @"<br\s*/?>|</p>|</div>|</li>", "\n", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            var stripped = System.Text.RegularExpressions.Regex.Replace(blocks, "<[^>]+>", string.Empty);
            return System.Net.WebUtility.HtmlDecode(System.Text.RegularExpressions.Regex.Replace(stripped, @"[ \t]+", " ")).Trim();
        }

        private static string FormatDuration(double seconds)
        {
            var span = TimeSpan.FromSeconds(seconds);
            return span.TotalHours >= 1
                ? string.Format(CultureInfo.InvariantCulture, "{0}h {1:00}m", (int)span.TotalHours, span.Minutes)
                : string.Format(CultureInfo.InvariantCulture, "{0}m {1:00}s", span.Minutes, span.Seconds);
        }
    }
}
