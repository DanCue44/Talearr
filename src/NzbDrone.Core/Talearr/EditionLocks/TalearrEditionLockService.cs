using System;
using System.Collections.Generic;
using System.Linq;
using NLog;
using NzbDrone.Core.Books;
using NzbDrone.Core.Books.Events;
using NzbDrone.Core.Messaging.Events;
using NzbDrone.Core.Talearr.Recordings;

namespace NzbDrone.Core.Talearr.EditionLocks
{
    public sealed class TalearrEditionLockDetails
    {
        public int BookId { get; set; }
        public bool Locked { get; set; }

        // The locked edition, else the monitored one.
        public int? EditionId { get; set; }
        public string EditionTitle { get; set; }
        public bool AnyEditionOk { get; set; }
    }

    public enum TalearrEditionLockStatus
    {
        Ok,
        NotFound,
        Invalid
    }

    public sealed class TalearrEditionLockResult
    {
        public TalearrEditionLockStatus Status { get; set; }
        public string Message { get; set; }
        public TalearrEditionLockDetails Details { get; set; }
    }

    public interface ITalearrEditionLockService
    {
        TalearrEditionLockDetails Get(int bookId);
        TalearrEditionLockResult Set(int bookId, bool locked);

        bool IsLocked(int bookId);

        // EditionService.SetMonitored for automatic (not user) selections: false keeps the locked
        // edition. A file that really landed on another edition (and none on the locked one)
        // wins, and the lock moves with it, so the book is not searched again.
        bool AllowsAutomaticSwitch(Edition target);

        // A user picked another edition while the book is locked: the lock follows the choice.
        void FollowManualSelection(int bookId, int editionId);

        // Book refresh: puts the monitored flag back on the locked edition. True when it changed
        // the given (unsaved) editions.
        bool KeepLockedEdition(List<Edition> editions);

        // After a refresh: a locked book never accepts any edition.
        void Reapply(IEnumerable<Book> rows);
    }

    // Talearr: Edit book > Lock edition. Builds on Chaptarr's Automatically Switch Edition
    // (Book.AnyEditionOk): locking turns that off, which already limits import matching to the
    // monitored edition and keeps the edition pinned (EditionPinPolicy), and also keeps the
    // edition through the paths that still switch it: metadata refresh repairs, automatic
    // selection during imports and scans, import lists and the abridged grab policy. The lock
    // lives in talearr.db (EditionLocks); chaptarr.db only sees AnyEditionOk = false and the
    // monitored edition, both values stock Chaptarr uses itself.
    public class TalearrEditionLockService : ITalearrEditionLockService, IHandle<BookDeletedEvent>
    {
        private static readonly TimeSpan CacheLifetime = TimeSpan.FromMinutes(5);

        private readonly ITalearrEditionLockStore _store;
        private readonly ITalearrEditionLockLibrary _library;
        private readonly Logger _logger;
        private readonly object _lock = new object();

        private Dictionary<int, TalearrEditionLock> _cache;
        private DateTime _cachedAt;

        public TalearrEditionLockService(ITalearrEditionLockStore store, ITalearrEditionLockLibrary library, Logger logger)
        {
            _store = store;
            _library = library;
            _logger = logger;
        }

        public TalearrEditionLockDetails Get(int bookId)
        {
            var book = _library.GetBook(bookId);
            return book == null ? null : Describe(book);
        }

        public TalearrEditionLockResult Set(int bookId, bool locked)
        {
            var book = _library.GetBook(bookId);
            if (book == null)
            {
                return new TalearrEditionLockResult { Status = TalearrEditionLockStatus.NotFound, Message = $"Book {bookId} not found" };
            }

            if (!locked)
            {
                if (IsLocked(bookId))
                {
                    _store.Remove(new[] { bookId });
                    Invalidate();
                    _logger.Info("Talearr: edition of '{0}' unlocked", book.Title);
                }

                return Ok(book);
            }

            var editions = _library.GetEditions(bookId);
            var monitored = editions.Where(e => e.Monitored).OrderBy(e => e.Id).ToList();
            if (monitored.Count != 1)
            {
                return new TalearrEditionLockResult
                {
                    Status = TalearrEditionLockStatus.Invalid,
                    Message = "Pick one edition for this book before locking it",
                    Details = Describe(book)
                };
            }

            var edition = monitored[0];
            _store.Upsert(new TalearrEditionLock
            {
                BookId = bookId,
                EditionKey = TalearrEditionKey.For(edition),
                EditionIdHint = edition.Id,
                LockedAt = DateTime.UtcNow
            });
            Invalidate();

            if (book.AnyEditionOk)
            {
                book.AnyEditionOk = false;
                _library.SaveAnyEditionOk(book);
            }

            _logger.Info("Talearr: edition of '{0}' locked to {1} ({2})", book.Title, edition.Id, edition.Title);
            return Ok(book);
        }

        public bool IsLocked(int bookId)
        {
            return bookId > 0 && Snapshot().ContainsKey(bookId);
        }

        public bool AllowsAutomaticSwitch(Edition target)
        {
            try
            {
                if (target == null || !Snapshot().TryGetValue(target.BookId, out var editionLock))
                {
                    return true;
                }

                var editions = _library.GetEditions(target.BookId);
                var locked = Find(editionLock, editions);
                if (locked == null || locked.Id == target.Id)
                {
                    return true;
                }

                if (_library.FileCount(target.Id) > 0 && _library.FileCount(locked.Id) == 0)
                {
                    _logger.Info("Talearr: files of book {0} were imported for edition {1}, not the locked edition {2}; the lock moves to that edition so the book is not searched again", target.BookId, target.Id, locked.Id);
                    Move(editionLock, target);
                    return true;
                }

                _logger.Debug("Talearr: book {0} keeps its locked edition {1} instead of switching to {2}", target.BookId, locked.Id, target.Id);
                return false;
            }
            catch (Exception ex)
            {
                _logger.Warn(ex, "Talearr: could not check the edition lock of book {0}", target?.BookId);
                return true;
            }
        }

        public void FollowManualSelection(int bookId, int editionId)
        {
            try
            {
                if (editionId <= 0 || !Snapshot().TryGetValue(bookId, out var editionLock))
                {
                    return;
                }

                var edition = _library.GetEditions(bookId).FirstOrDefault(e => e.Id == editionId);
                if (edition != null && Find(editionLock, new List<Edition> { edition }) == null)
                {
                    Move(editionLock, edition);
                    _logger.Info("Talearr: locked edition of book {0} is now {1} ({2})", bookId, edition.Id, edition.Title);
                }
            }
            catch (Exception ex)
            {
                _logger.Warn(ex, "Talearr: could not move the edition lock of book {0}", bookId);
            }
        }

        public bool KeepLockedEdition(List<Edition> editions)
        {
            try
            {
                var list = (editions ?? new List<Edition>()).Where(e => e != null).ToList();
                var bookId = list.Select(e => e.BookId).FirstOrDefault(id => id > 0);
                if (bookId <= 0 || !Snapshot().TryGetValue(bookId, out var editionLock))
                {
                    return false;
                }

                var locked = Find(editionLock, list);
                if (locked == null)
                {
                    // The edition is gone from the metadata; nothing to keep.
                    return false;
                }

                var changed = false;
                foreach (var edition in list)
                {
                    var monitor = ReferenceEquals(edition, locked);
                    if (edition.Monitored != monitor)
                    {
                        edition.Monitored = monitor;
                        changed = true;
                    }
                }

                if (changed)
                {
                    _logger.Debug("Talearr: book {0} keeps its locked edition {1} through the refresh", bookId, locked.Id);
                }

                if (locked.Id > 0 && editionLock.EditionIdHint != locked.Id)
                {
                    Move(editionLock, locked);
                }

                return changed;
            }
            catch (Exception ex)
            {
                _logger.Warn(ex, "Talearr: could not keep the locked edition during refresh");
                return false;
            }
        }

        public void Reapply(IEnumerable<Book> rows)
        {
            try
            {
                var locks = Snapshot();
                if (locks.Count == 0)
                {
                    return;
                }

                foreach (var row in (rows ?? Enumerable.Empty<Book>()).Where(b => b != null && locks.ContainsKey(b.Id)).GroupBy(b => b.Id).Select(g => g.First()))
                {
                    var fresh = _library.GetBook(row.Id);
                    if (fresh != null && fresh.AnyEditionOk)
                    {
                        fresh.AnyEditionOk = false;
                        _library.SaveAnyEditionOk(fresh);
                    }

                    var editions = _library.GetEditions(row.Id);
                    var before = editions.ToDictionary(e => e.Id, e => e.Monitored);
                    if (KeepLockedEdition(editions))
                    {
                        _library.SaveEditions(editions.Where(e => before.TryGetValue(e.Id, out var was) && was != e.Monitored).ToList());
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.Warn(ex, "Talearr: could not apply edition locks after a refresh");
            }
        }

        public void Handle(BookDeletedEvent message)
        {
            var ids = (message?.DeletedBooks ?? new List<Book>())
                .Append(message?.Book)
                .Where(b => b != null && b.Id > 0)
                .Select(b => b.Id)
                .Where(IsLocked)
                .ToList();

            if (ids.Any())
            {
                _store.Remove(ids);
                Invalidate();
            }
        }

        internal static Edition Find(TalearrEditionLock editionLock, IEnumerable<Edition> editions)
        {
            var list = (editions ?? Enumerable.Empty<Edition>()).Where(e => e != null).ToList();
            if (editionLock == null || list.Count == 0)
            {
                return null;
            }

            if (!string.IsNullOrWhiteSpace(editionLock.EditionKey))
            {
                var byKey = list.Where(e => string.Equals(TalearrEditionKey.For(e), editionLock.EditionKey, StringComparison.OrdinalIgnoreCase)).ToList();
                if (byKey.Count > 0)
                {
                    return byKey.FirstOrDefault(e => e.Id == editionLock.EditionIdHint) ?? byKey.OrderBy(e => e.Id == 0 ? int.MaxValue : e.Id).First();
                }
            }

            return editionLock.EditionIdHint.HasValue ? list.FirstOrDefault(e => e.Id > 0 && e.Id == editionLock.EditionIdHint.Value) : null;
        }

        private void Move(TalearrEditionLock editionLock, Edition edition)
        {
            editionLock.EditionKey = TalearrEditionKey.For(edition) ?? editionLock.EditionKey;
            editionLock.EditionIdHint = edition.Id > 0 ? edition.Id : editionLock.EditionIdHint;
            _store.Upsert(editionLock);
        }

        private TalearrEditionLockDetails Describe(Book book)
        {
            Snapshot().TryGetValue(book.Id, out var editionLock);
            var editions = _library.GetEditions(book.Id);
            var edition = (editionLock != null ? Find(editionLock, editions) : null) ??
                          editions.Where(e => e.Monitored).OrderBy(e => e.Id).FirstOrDefault();

            return new TalearrEditionLockDetails
            {
                BookId = book.Id,
                Locked = editionLock != null,
                EditionId = edition?.Id,
                EditionTitle = edition?.Title,
                AnyEditionOk = book.AnyEditionOk
            };
        }

        private TalearrEditionLockResult Ok(Book book)
        {
            return new TalearrEditionLockResult
            {
                Status = TalearrEditionLockStatus.Ok,
                Details = Describe(_library.GetBook(book.Id) ?? book)
            };
        }

        private Dictionary<int, TalearrEditionLock> Snapshot()
        {
            lock (_lock)
            {
                if (_cache == null || DateTime.UtcNow - _cachedAt > CacheLifetime)
                {
                    _cache = _store.GetAll();
                    _cachedAt = DateTime.UtcNow;
                }

                return _cache;
            }
        }

        private void Invalidate()
        {
            lock (_lock)
            {
                _cache = null;
            }
        }
    }
}
