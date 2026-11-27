using System;
using System.Collections.Generic;
using System.Linq;
using NLog;
using NzbDrone.Core.Books;
using NzbDrone.Core.Books.Events;
using NzbDrone.Core.MediaFiles;
using NzbDrone.Core.Messaging.Events;
using NzbDrone.Core.Talearr.Recordings;

namespace NzbDrone.Core.Talearr.Import
{
    public interface ITalearrPackFileRepair
    {
        // Fixes pack files sitting on single audiobooks of one author. Returns how many files it
        // moved or released. Never throws.
        int Repair(int authorId);
    }

    // Talearr (issue #14): before 0.18.72 the matcher could put multi-book pack files (Audible's
    // "Publisher's Pack" audiobooks) on a single book, often book 1, as several recordings of it.
    // After an author refresh this puts them right, database only (nothing on disk changes):
    //   - a pack file on a single book moves to the author's catalog entry for that pack when there
    //     is exactly one and it has no files yet;
    //   - otherwise a pack file whose range leaves the book out (Books 5-6 on book 1) is released:
    //     it becomes an unmapped file, ready for Manual Import;
    //   - anything less certain stays where it is (a pack that includes the book may have been
    //     grabbed for it on purpose by the bundle fallback).
    // The duplicate merge then removes the recording rows that were left empty.
    public class TalearrPackFileRepair : ITalearrPackFileRepair, IHandleAsync<AuthorRefreshCompleteEvent>
    {
        private readonly IBookService _bookService;
        private readonly IEditionService _editionService;
        private readonly IMediaFileService _mediaFileService;
        private readonly ITalearrRecordingMaintenance _maintenance;
        private readonly Logger _logger;

        public TalearrPackFileRepair(IBookService bookService,
                                     IEditionService editionService,
                                     IMediaFileService mediaFileService,
                                     Logger logger,
                                     ITalearrRecordingMaintenance maintenance = null)
        {
            _bookService = bookService;
            _editionService = editionService;
            _mediaFileService = mediaFileService;
            _maintenance = maintenance;
            _logger = logger;
        }

        public void HandleAsync(AuthorRefreshCompleteEvent message)
        {
            var authorId = message?.Author?.Id ?? 0;
            if (authorId > 0)
            {
                Repair(authorId);
            }
        }

        public int Repair(int authorId)
        {
            try
            {
                return RepairAuthor(authorId);
            }
            catch (Exception ex)
            {
                _logger.Warn(ex, "Talearr: could not check the audiobooks of author {0} for multi-book pack files", authorId);
                return 0;
            }
        }

        private int RepairAuthor(int authorId)
        {
            var catalog = _bookService.GetBooksByAuthor(authorId) ?? new List<Book>();
            var rows = catalog
                .Where(b => b != null && b.Id > 0 && b.MediaType == BookMediaType.Audiobook && TalearrPackFiles.BookPack(b) == null)
                .ToList();
            if (rows.Count == 0)
            {
                return 0;
            }

            var editions = _editionService.GetEditionsByBook(rows.Select(b => b.Id).ToList()) ?? new List<Edition>();
            var bookOfEdition = editions.GroupBy(e => e.Id).ToDictionary(g => g.Key, g => g.First().BookId);
            var suspects = (_mediaFileService.GetFilesByBooks(rows.Select(b => b.Id).ToList()) ?? new List<BookFile>())
                .Where(f => f != null && f.EditionId > 0 && bookOfEdition.ContainsKey(f.EditionId))
                .Select(f => (File: f, Evidence: TalearrPackFiles.FromPath(f.Path)))
                .Where(x => x.Evidence != null)
                .ToList();
            if (suspects.Count == 0)
            {
                return 0;
            }

            var rowsById = rows.ToDictionary(b => b.Id);
            var filled = new HashSet<int>();
            var touched = new List<Book>();
            var changed = new List<BookFile>();

            foreach (var (file, evidence) in suspects.OrderBy(x => x.File.Id))
            {
                var row = rowsById[bookOfEdition[file.EditionId]];

                var target = TalearrPackFiles.FindPackEntry(evidence, row, catalog, TalearrPackFiles.EvidenceTexts(file.Path));
                Edition targetEdition = null;
                if (target != null && !filled.Contains(target.Id) && (_mediaFileService.GetFilesByBook(target.Id) ?? new List<BookFile>()).Count == 0)
                {
                    targetEdition = TalearrPackFiles.ChooseEdition(_editionService.GetEditionsByBook(target.Id));
                }

                if (targetEdition != null)
                {
                    _logger.Info("Talearr: '{0}' holds several books ({1}); moved it from '{2}' (book row {3}) to '{4}' (book row {5})",
                        file.Path, evidence, row.Title, row.Id, target.Title, target.Id);
                    file.EditionId = targetEdition.Id;
                    file.Edition = targetEdition;
                    filled.Add(target.Id);
                }
                else if (TalearrPackFiles.Excludes(evidence, row))
                {
                    _logger.Info("Talearr: '{0}' holds several books ({1}), not '{2}' (book row {3}); released it for Manual Import",
                        file.Path, evidence, row.Title, row.Id);
                    file.EditionId = 0;
                }
                else
                {
                    continue;
                }

                changed.Add(file);
                if (!touched.Any(b => b.Id == row.Id))
                {
                    touched.Add(row);
                }
            }

            if (changed.Count == 0)
            {
                return 0;
            }

            _mediaFileService.Update(changed);

            foreach (var row in touched)
            {
                _maintenance?.MergeDuplicates(row);
            }

            return changed.Count;
        }
    }
}
