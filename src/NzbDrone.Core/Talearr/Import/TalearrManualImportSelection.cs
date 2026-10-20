using System;
using System.Collections.Generic;
using System.Linq;
using NzbDrone.Core.Books;

namespace NzbDrone.Core.Talearr.Import
{
    // Talearr: manual import accepts a provider edition id when the numeric edition id is missing
    // (Chaptarr #108). API clients and scripts usually know "hc:edition:123-audiobook" or an ASIN,
    // not Talearr's row id, and the request used to fail with "Edition must be selected".
    public static class TalearrManualImportSelection
    {
        public static Edition FindEditionByForeignId(IEnumerable<Edition> editions, string foreignEditionId)
        {
            var wanted = foreignEditionId?.Trim();
            if (string.IsNullOrWhiteSpace(wanted) || editions == null)
            {
                return null;
            }

            var list = editions.Where(edition => edition != null).ToList();

            return list.FirstOrDefault(edition => wanted.Equals(edition.ForeignEditionId?.Trim(), StringComparison.OrdinalIgnoreCase)) ??
                   list.FirstOrDefault(edition => wanted.Equals(BookEditionIdentity.GetTrustedForeignEditionId(edition), StringComparison.OrdinalIgnoreCase)) ??
                   list.FirstOrDefault(edition => wanted.Equals(edition.AudibleASIN?.Trim(), StringComparison.OrdinalIgnoreCase)) ??
                   list.FirstOrDefault(edition => wanted.Equals(edition.Isbn13?.Trim(), StringComparison.OrdinalIgnoreCase));
        }

        public static string MissingEditionReason(Book book, string foreignEditionId)
        {
            var title = book?.Title ?? "the selected book";

            return string.IsNullOrWhiteSpace(foreignEditionId)
                ? $"Edition must be selected for '{title}' (send editionId, or a foreignEditionId of one of its editions)."
                : $"Edition must be selected for '{title}': foreignEditionId '{foreignEditionId.Trim()}' is not an edition of this book. Refresh the author or send the numeric editionId.";
        }
    }
}
