using System;
using System.Collections.Generic;
using System.Linq;
using NLog;
using NzbDrone.Core.Books;

namespace NzbDrone.Core.Talearr.Wishlists
{
    public interface ITalearrOwnedBooks
    {
        // True when the requested book was queued by an owned Audible library list that adds
        // books without monitoring them, for that format.
        bool IsOwnedOnly(string providerId, BookMediaType mediaType);
    }

    // Talearr: reads the books owned Audible library lists queued for adding
    // (TalearrWishlist.OwnedPending). The author import (ProcessPendingImportsCommandHandler)
    // asks it after adding a requested book, so those books stay unmonitored and are kept as
    // manual additions instead of being searched.
    public class TalearrOwnedBooks : ITalearrOwnedBooks
    {
        private readonly ITalearrWishlistStore _store;
        private readonly Logger _logger;

        public TalearrOwnedBooks(ITalearrWishlistStore store, Logger logger)
        {
            _store = store;
            _logger = logger;
        }

        public bool IsOwnedOnly(string providerId, BookMediaType mediaType)
        {
            if (string.IsNullOrWhiteSpace(providerId))
            {
                return false;
            }

            try
            {
                return _store.All().Any(w =>
                    w.Kind == TalearrWishlistKinds.AudibleLibrary &&
                    w.AddUnmonitored &&
                    (mediaType == BookMediaType.Audiobook ? w.Audiobook : w.Ebook) &&
                    (w.OwnedPending ?? new List<string>()).Contains(providerId.Trim(), StringComparer.OrdinalIgnoreCase));
            }
            catch (Exception ex)
            {
                _logger.Debug(ex, "Talearr: could not read the owned library lists");
                return false;
            }
        }
    }
}
