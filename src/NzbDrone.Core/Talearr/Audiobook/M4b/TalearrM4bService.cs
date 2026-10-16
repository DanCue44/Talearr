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
    public class TalearrM4bService : IExecute<TalearrConvertToM4bCommand>, IHandle<BookImportedEvent>
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
                                 Logger logger)
        {
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
            var plans = M4bLibraryPlanner.Plan(_mediaFileService.GetFilesByBook(book.Id));
            var convertible = plans.Where(p => p.CanConvert).ToList();

            if (convertible.Count == 0)
            {
                var reason = plans.Select(p => p.SkipReason).FirstOrDefault() ?? "The book has no audio files.";
                _logger.ProgressInfo("Nothing to convert for '{0}': {1}", book.Title, reason);
                return;
            }

            if (!_converter.IsAvailable())
            {
                throw new TalearrM4bException("FFmpeg and FFprobe are needed to convert to M4B and were not found.");
            }

            var errors = new List<string>();

            foreach (var plan in convertible)
            {
                var edition = GetEdition(plan.EditionId);
                var job = BuildJob(book, author, edition, plan);
                var title = edition?.Title ?? book.Title;
                var lastPercent = -10.0;

                _logger.ProgressInfo("Converting '{0}' to M4B ({1} files)", title, plan.Files.Count);

                var outcome = _converter.Convert(job, (percent, text) =>
                {
                    if (percent - lastPercent >= 5 || percent >= 98)
                    {
                        lastPercent = percent;
                        _logger.ProgressInfo("Converting '{0}' to M4B: {1}", title, text);
                    }
                });

                if (!outcome.Success)
                {
                    _logger.Warn("TaleArr: M4B conversion of '{0}' failed, original files kept: {1}", title, outcome.Error);
                    errors.Add($"{title}: {outcome.Error}");
                    continue;
                }

                ReplaceLibraryFiles(plan, outcome, author, edition);

                _logger.ProgressInfo("Converted '{0}' to M4B: {1} chapters, {2}", title, outcome.ChapterCount, FormatDuration(outcome.DurationSeconds));
            }

            if (errors.Any())
            {
                throw new TalearrM4bException("M4B conversion failed, the original files were kept. " + string.Join(" ", errors));
            }
        }

        internal M4bConversionJob BuildJob(Book book, Author author, Edition edition, M4bLibraryPlan plan)
        {
            var title = edition?.Title;
            if (string.IsNullOrWhiteSpace(title))
            {
                title = book.Title;
            }

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
                    Author = author?.Name,
                    Narrator = narrator,
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

        private void ReplaceLibraryFiles(M4bLibraryPlan plan, M4bConversionOutcome outcome, Author author, Edition edition)
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
