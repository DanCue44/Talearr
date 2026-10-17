using System;
using System.Collections.Generic;
using System.Linq;
using NLog;
using NzbDrone.Common.Extensions;
using NzbDrone.Core.Books;
using NzbDrone.Core.MetadataSource;
using NzbDrone.Core.MetadataSource.BookInfo.V5;
using NzbDrone.Core.Parser;

namespace NzbDrone.Core.Talearr.MetadataFallback
{
    /// <summary>
    /// TaleArr: gives books added from the Open Library fallback a real audiobook edition.
    ///
    /// The fallback maps each Open Library work to one print edition, so the audiobook row of the
    /// book has no narrator, runtime or abridged flag. When Open Library lists an Audible ASIN for
    /// the work (id_amazon, B0...), Audnexus is asked for that product; if its title and author
    /// match the work, an audio edition (az:ASIN, narrators, runtime, "Unabridged"/"Abridged") is
    /// added next to the print one. Audnexus has no title search, so works without an ASIN on Open
    /// Library stay print-only. This only ever adds an edition to a fallback-built response: it
    /// never changes data from the metadata server, which replaces all of it on the first refresh
    /// after recovery. Lookups are capped per author and any Audnexus failure stops enrichment
    /// for that author without failing the add.
    /// </summary>
    public static class TalearrAudnexusEnricher
    {
        public const string SourceName = "Audnexus";

        // At most this many Audnexus calls per author (one per second: about 20s worst case).
        public const int MaxLookupsPerAuthor = 20;
        public const int MaxAsinsPerWork = 2;

        public static int Enrich(V5AuthorResponse response, IEnumerable<OpenLibraryWorkDoc> works, IAudnexusClient audnexus, Logger logger = null)
        {
            if (response?.Books == null || response.Books.Count == 0 || audnexus == null)
            {
                return 0;
            }

            var asinsByWork = (works ?? Enumerable.Empty<OpenLibraryWorkDoc>())
                .Where(w => w != null)
                .GroupBy(w => TalearrOpenLibraryMapper.WorkProviderId(w.Key) ?? string.Empty, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => g.SelectMany(w => w.AmazonIds).Where(AudnexusClient.IsAudibleAsin).Select(a => a.Trim().ToUpperInvariant()).Distinct().Take(MaxAsinsPerWork).ToList(), StringComparer.OrdinalIgnoreCase);

            var authorName = response.Author?.Name;
            var lookups = 0;
            var added = 0;

            foreach (var book in response.Books)
            {
                if (book?.Editions == null || book.Editions.Any(e => e != null && e.IsAudiobook) ||
                    !asinsByWork.TryGetValue(book.OpenLibraryWorkId ?? book.Id ?? string.Empty, out var asins))
                {
                    continue;
                }

                foreach (var asin in asins)
                {
                    if (lookups >= MaxLookupsPerAuthor)
                    {
                        logger?.Debug("[TALEARR-FALLBACK] Audnexus lookup budget reached for '{0}'", authorName);
                        return added;
                    }

                    AudnexusBook product;
                    try
                    {
                        lookups++;
                        product = audnexus.GetBook(asin);
                    }
                    catch (Exception ex)
                    {
                        logger?.Debug("[TALEARR-FALLBACK] Audnexus unavailable ({0}); audiobook rows of '{1}' stay print-only", ex.Message, authorName);
                        return added;
                    }

                    if (!Matches(product, book, authorName))
                    {
                        continue;
                    }

                    book.Editions.Add(BuildEdition(product, book));
                    book.HasAudiobook = true;
                    added++;
                    logger?.Debug("[TALEARR-FALLBACK] Added Audnexus audiobook {0} to '{1}'", product.Asin, book.Title);
                    break;
                }
            }

            return added;
        }

        public static bool Matches(AudnexusBook product, V5Book book, string authorName)
        {
            if (product == null || book == null || !TalearrTitleNormalizer.IsSameWorkTitle(product.Title, book.Title))
            {
                return false;
            }

            var wanted = TalearrOpenLibraryMapper.NormalizeName(authorName);
            return wanted.Length > 0 &&
                   product.Authors.Any(a => string.Equals(TalearrOpenLibraryMapper.NormalizeName(a), wanted, StringComparison.Ordinal));
        }

        public static V5Edition BuildEdition(AudnexusBook product, V5Book book)
        {
            var print = book.Editions.FirstOrDefault(e => e != null);
            var editionId = ProviderIdHelper.WithPrefix("az", product.Asin);
            var abridgement = product.Abridged switch
            {
                true => "Abridged",
                false => "Unabridged",
                _ => null
            };

            var edition = new V5Edition
            {
                Id = editionId,
                Asin = product.Asin,
                Asins = new List<string> { product.Asin },
                ReadingFormatId = 2,
                Format = "audio",
                EditionFormat = "Audible Audio",
                EditionInfo = abridgement,
                Title = product.Title,
                Subtitle = product.Subtitle ?? book.Subtitle,

                // Same language bucket as the print edition, so edition retention keeps both.
                LanguageCode = print?.LanguageCode ?? book.LanguageCode,
                NarratorNames = product.Narrators.ToList(),
                Narrators = product.Narrators.Select(n => new V5NarratorCredit { Name = n }).ToList(),
                DurationSeconds = product.RuntimeMinutes.HasValue ? product.RuntimeMinutes.Value * 60 : null,
                Publisher = product.Publisher,
                PublicationDate = product.ReleaseDate,
                CoverUrl = product.ImageUrl.IsValidHttpUrl() ? product.ImageUrl : print?.CoverUrl,
                ProviderUrls = new ProviderUrlMap()
            };

            edition.ProviderUrls.SetNormalized("audible", $"https://www.audible.com/pd/{product.Asin}");
            return edition;
        }
    }
}
