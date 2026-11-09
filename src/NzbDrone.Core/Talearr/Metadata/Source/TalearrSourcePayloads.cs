using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using NLog;
using NzbDrone.Core.MetadataSource.BookInfo.V5;

namespace NzbDrone.Core.Talearr.Metadata.Source
{
    public interface ITalearrSourcePayloads
    {
        // True when the metadata source setting is Talearr and a Hardcover token is saved.
        bool IsActive { get; }

        // The author's full payload (Hardcover catalog, mapped, gaps filled from Open Library and
        // Audible). Null when Hardcover has no such author.
        V5AuthorResponse GetAuthor(int hardcoverAuthorId, bool useCache = true);

        // Change signatures ("ETags") for refresh, one request per 100 authors.
        Dictionary<int, string> GetSignatures(IReadOnlyCollection<int> hardcoverAuthorIds);

        // The primary author of a book, following deduped ids. Null when unknown.
        int? GetBookAuthorId(int hardcoverBookId, out int canonicalBookId);

        int? GetEditionBookId(int hardcoverEditionId);
    }

    // Talearr: when the metadata source is Talearr,
    // BookInfoProxy asks this service instead of the metadata server for authors (add and
    // refresh), the bulk change check and single-book lookups of Hardcover ids, and converts the
    // payloads with the same V5 conversion the server's payloads get.
    //
    // Change signatures replace the server's ETags: "hct-{books_count}-{updated_at ticks}-w{week}".
    // books_count changes when Hardcover links a new book to the author; updated_at when the
    // author row is edited. Edits to a book or edition do not touch either, so the signature also
    // carries a week number, offset per author so a large library does not refresh all at once:
    // every author is read in full at least once a week.
    public class TalearrSourcePayloads : ITalearrSourcePayloads
    {
        public const string SignaturePrefix = "hct-";

        private readonly ITalearrMetadataSourceSettingsService _settings;
        private readonly ITalearrHardcoverMetadataClient _hardcover;
        private readonly ITalearrSourceGapFill _gapFill;
        private readonly ITalearrAudibleFill _audibleFill;
        private readonly Logger _logger;
        private readonly Func<DateTime> _clock;

        public TalearrSourcePayloads(ITalearrMetadataSourceSettingsService settings,
                                     ITalearrHardcoverMetadataClient hardcover,
                                     ITalearrSourceGapFill gapFill,
                                     ITalearrAudibleFill audibleFill,
                                     Logger logger)
            : this(settings, hardcover, gapFill, audibleFill, logger, () => DateTime.UtcNow)
        {
        }

        internal TalearrSourcePayloads(ITalearrMetadataSourceSettingsService settings,
                                       ITalearrHardcoverMetadataClient hardcover,
                                       ITalearrSourceGapFill gapFill,
                                       ITalearrAudibleFill audibleFill,
                                       Logger logger,
                                       Func<DateTime> clock)
        {
            _settings = settings;
            _hardcover = hardcover;
            _gapFill = gapFill;
            _audibleFill = audibleFill;
            _logger = logger;
            _clock = clock;
        }

        public bool IsActive => _settings?.GetSource() == TalearrMetadataSource.Talearr && _hardcover?.IsConfigured == true;

        // "hc:123" -> 123. Edition ids ("hc:edition:5") are not author or book ids.
        public static bool TryParseId(string providerId, out int id)
        {
            id = 0;
            var value = providerId?.Trim();
            if (string.IsNullOrEmpty(value) || !value.StartsWith("hc:", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            return int.TryParse(value.Substring(3), NumberStyles.None, CultureInfo.InvariantCulture, out id) && id > 0;
        }

        // "hc:edition:5" -> 5.
        public static bool TryParseEditionId(string providerId, out int id)
        {
            id = 0;
            const string prefix = "hc:edition:";
            var value = providerId?.Trim();
            if (string.IsNullOrEmpty(value) || !value.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            // ForeignEditionId carries a -audiobook / -ebook suffix.
            var number = value.Substring(prefix.Length);
            var dash = number.IndexOf('-');
            if (dash > 0)
            {
                number = number.Substring(0, dash);
            }

            return int.TryParse(number, NumberStyles.None, CultureInfo.InvariantCulture, out id) && id > 0;
        }

        public static string Signature(HardcoverAuthorSignature signature, DateTime now)
        {
            var ticks = signature.UpdatedAt?.ToUniversalTime().Ticks ?? 0;
            var week = ((int)(now - DateTime.UnixEpoch).TotalDays + (signature.AuthorId % 7)) / 7;
            return $"{SignaturePrefix}{signature.BooksCount}-{ticks}-w{week}";
        }

        public V5AuthorResponse GetAuthor(int hardcoverAuthorId, bool useCache = true)
        {
            var catalog = _hardcover.GetAuthorCatalog(hardcoverAuthorId, useCache);
            var response = TalearrHardcoverMapper.BuildAuthorResponse(catalog);
            if (response == null)
            {
                return null;
            }

            var filled = _gapFill?.FillMissingDates(response) ?? 0;
            _audibleFill?.Apply(response);
            _logger.Debug("[TALEARR-SOURCE] Author hc:{0} ({1}): {2} works, {3} series, {4} dates from Open Library, {5} Hardcover requests",
                hardcoverAuthorId, response.Author?.Name, response.Books.Count, response.Series.Count, filled, catalog?.RequestCount ?? 0);

            return response;
        }

        public Dictionary<int, string> GetSignatures(IReadOnlyCollection<int> hardcoverAuthorIds)
        {
            var now = _clock();
            return _hardcover.GetAuthorSignatures(hardcoverAuthorIds)
                .ToDictionary(kvp => kvp.Key, kvp => Signature(kvp.Value, now));
        }

        public int? GetBookAuthorId(int hardcoverBookId, out int canonicalBookId)
        {
            canonicalBookId = hardcoverBookId;
            var credits = _hardcover.GetBookAuthors(hardcoverBookId);
            if (credits == null || credits.AuthorIds.Count == 0)
            {
                return null;
            }

            canonicalBookId = credits.BookId;
            return credits.AuthorIds[0];
        }

        public int? GetEditionBookId(int hardcoverEditionId)
        {
            return _hardcover.GetEditionBookId(hardcoverEditionId);
        }
    }
}
