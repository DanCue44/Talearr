using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Text.RegularExpressions;
using NzbDrone.Core.Books;
using NzbDrone.Core.MetadataSource;
using NzbDrone.Core.MetadataSource.BookInfo.V5;
using NzbDrone.Core.Parser;

namespace NzbDrone.Core.Talearr.Metadata.Source
{
    public sealed class TalearrAudibleMergeStats
    {
        public int Matched { get; set; }
        public int EditionsAdded { get; set; }
        public int AsinsFilled { get; set; }
        public int SeriesFilled { get; set; }
        public int SeriesCreated { get; set; }
        public int BooksAdded { get; set; }

        public bool Changed => EditionsAdded + AsinsFilled + SeriesFilled + SeriesCreated + BooksAdded > 0;

        public override string ToString()
        {
            return $"{Matched} matched, {EditionsAdded} audiobook editions, {AsinsFilled} ASINs, {SeriesFilled} series places, {SeriesCreated} new series, {BooksAdded} new books";
        }
    }

    // Talearr: adds an author's Audible catalog to a payload from Talearr's source (Hardcover).
    //
    // Fill (every author): a Hardcover book with no audiobook edition gets Audible's editions; a
    // book in no series gets Audible's series and place; a numbered place Hardcover left empty is
    // filled; a lone audiobook edition without an ASIN gets Audible's when the narrator agrees.
    // Hardcover's own values are never replaced.
    //
    // Audible leads (authors set to Audible): every matched book also gets Audible's editions,
    // ranked first for edition selection; Audible's series and places win; audiobooks Hardcover
    // does not list for the author are added as books of their own (az:{ASIN}), collections as
    // omnibus books.
    //
    // Kept (books added from an Audible search result, TalearrAudibleSourceSettings.GetKeptBooks):
    // in fill mode too, a kept listing Hardcover does not have is added as its own book, so the
    // added book stays in the author's payload.
    //
    // Ids: an Audible edition is az:{ASIN} (ConvertV5EditionToDomain appends -audiobook), an
    // Audible-only book az:{ASIN} of its first listing, a series Hardcover does not have
    // az:{series ASIN} (Series.AmazonSeriesAsin). Hardcover ids are never changed, so local rows
    // keep matching. Books are matched by ASIN, then by title (TalearrTitleNormalizer).
    public static class TalearrAudibleMerge
    {
        public const string SourceName = "Audible";
        public const string EditionFormat = "Audible Audio";

        private static readonly Regex CollectionRegex = new Regex(
            @"\b(?:box(?:ed)?\s+set|omnibus|collection|compilation|books?\s+\d+(?:\.\d+)?\s*(?:-|–|to|through|&|and)\s*\d+)\b",
            RegexOptions.Compiled | RegexOptions.IgnoreCase);

        private static readonly Regex SeriesLabelRegex = new Regex(
            @"^(?:.*[,:]\s*)?(?:book|volume|vol\.?|part|episode|novel)\s*\d+(?:\.\d+)?$",
            RegexOptions.Compiled | RegexOptions.IgnoreCase);

        private static readonly Regex TagRegex = new Regex(@"<[^>]+>", RegexOptions.Compiled);
        private static readonly Regex BracketRegex = new Regex(@"\s*[\(\[](?<label>[^\)\]]*)[\)\]]", RegexOptions.Compiled);

        public static TalearrAudibleMergeStats Apply(V5AuthorResponse response, IEnumerable<AudibleCatalogItem> items, bool audibleLeads, string marketplace, IReadOnlyCollection<string> keptAsins = null)
        {
            var stats = new TalearrAudibleMergeStats();
            if (response?.Books == null)
            {
                return stats;
            }

            response.Series ??= new List<V5Series>();
            var domain = TalearrAudibleMarketplacesDomain(marketplace);

            var works = GroupWorks(items);
            var claimed = new HashSet<V5Book>();
            var matches = new List<(Work Work, V5Book Book)>();
            var unmatched = new List<Work>();

            // Exact matches (ASIN, then the full title) first, so a looser title match never takes
            // a book another listing names exactly.
            foreach (var work in works.Where(w => !w.IsCollection))
            {
                var book = MatchExact(response.Books, work);
                if (book != null && claimed.Add(book))
                {
                    matches.Add((work, book));
                }
                else if (book == null)
                {
                    unmatched.Add(work);
                }
            }

            var places = SeriesPlaces(response);
            foreach (var work in unmatched.ToList())
            {
                var book = MatchLoose(response.Books.Where(b => !claimed.Contains(b) && !OtherPlace(b, work, places)), work);
                if (book != null && claimed.Add(book))
                {
                    matches.Add((work, book));
                    unmatched.Remove(work);
                }
            }

            // Last, the same main title in the same place of the same series ("Formation: Age of
            // Expansion" and "Formation", both The Ghost Squadron #1). "Sunkiller: Odyssey" is
            // book 2 and stays apart from "Sunkiller".
            foreach (var work in unmatched.ToList())
            {
                var book = MatchByPlace(response.Books.Where(b => !claimed.Contains(b)), work, places);
                if (book != null && claimed.Add(book))
                {
                    matches.Add((work, book));
                    unmatched.Remove(work);
                }
            }

            foreach (var (work, book) in matches)
            {
                stats.Matched++;
                FillBook(response, book, work, audibleLeads, domain, stats);
            }

            var kept = new HashSet<string>(keptAsins ?? Array.Empty<string>(), StringComparer.OrdinalIgnoreCase);
            if (audibleLeads || kept.Count > 0)
            {
                foreach (var work in unmatched.Concat(works.Where(w => w.IsCollection))
                             .Where(w => audibleLeads || w.Items.Any(i => kept.Contains(i.Asin))))
                {
                    if (response.Books.Any(b => b?.Editions?.Any(e => SharesAsin(e, work)) == true))
                    {
                        continue;
                    }

                    var book = NewBook(work, domain);
                    response.Books.Add(book);
                    stats.BooksAdded++;
                    PlaceInSeries(response, book, work, audibleLeads: true, stats);
                }
            }

            if (stats.Changed && response.Summary != null)
            {
                response.Summary.TotalBooks = response.Books.Count;
                response.Summary.TotalSeries = response.Series.Count;
                if (response.Summary.FormatsAvailable != null)
                {
                    response.Summary.FormatsAvailable.Audiobook = response.Books.Count(b => b.HasAudiobook);
                }
            }

            return stats;
        }

        // One work: the Audible listings of the same title (a re-recording, an abridged version).
        internal sealed class Work
        {
            public List<AudibleCatalogItem> Items { get; } = new List<AudibleCatalogItem>();
            public AudibleCatalogItem Lead { get; set; }
            public string Title { get; set; }
            public string Subtitle { get; set; }
            public bool IsCollection { get; set; }

            public string FullTitle => string.IsNullOrWhiteSpace(Subtitle) ? Title : $"{Title}: {Subtitle}";
        }

        internal static List<Work> GroupWorks(IEnumerable<AudibleCatalogItem> items)
        {
            var works = new List<Work>();
            var byKey = new Dictionary<string, Work>(StringComparer.Ordinal);

            foreach (var item in (items ?? Enumerable.Empty<AudibleCatalogItem>())
                         .Where(i => i != null && !string.IsNullOrWhiteSpace(i.Asin) && !string.IsNullOrWhiteSpace(i.Title))
                         .GroupBy(i => i.Asin, StringComparer.OrdinalIgnoreCase)
                         .Select(g => g.First()))
            {
                var subtitle = WorkSubtitle(item);
                var title = WorkTitle(item);
                var fullTitle = string.IsNullOrWhiteSpace(subtitle) ? title : $"{title}: {subtitle}";
                var collection = IsCollection(item);
                var key = (collection ? "c|" : "w|") + TalearrTitleNormalizer.NormalizeFull(fullTitle);

                if (!byKey.TryGetValue(key, out var work))
                {
                    work = new Work { Title = title, Subtitle = subtitle, IsCollection = collection };
                    byKey[key] = work;
                    works.Add(work);
                }

                work.Items.Add(item);
            }

            foreach (var work in works)
            {
                // The original unabridged recording leads; abridged and later ones are editions.
                work.Items.Sort((a, b) =>
                {
                    var unabridged = IsUnabridged(b).CompareTo(IsUnabridged(a));
                    return unabridged != 0 ? unabridged : string.CompareOrdinal(a.ReleaseDate ?? "9999", b.ReleaseDate ?? "9999");
                });
                work.Lead = work.Items[0];
            }

            return works;
        }

        internal static bool IsCollection(AudibleCatalogItem item)
        {
            return CollectionRegex.IsMatch(item.Title ?? string.Empty) ||
                   CollectionRegex.IsMatch(item.Subtitle ?? string.Empty) ||
                   item.Series.Any(s => (s.Sequence ?? string.Empty).IndexOfAny(new[] { '-', ',', '–' }) > 0);
        }

        // Audible puts "Symbiote Wars, Book 4" in the subtitle; that names the series place, not
        // the work.
        internal static string WorkSubtitle(AudibleCatalogItem item)
        {
            var subtitle = item.Subtitle?.Trim();
            if (string.IsNullOrEmpty(subtitle) || SeriesLabelRegex.IsMatch(subtitle))
            {
                return null;
            }

            var normalized = TalearrSourceGapFill.Normalize(subtitle);
            if (item.Series.Any(s => normalized.Contains(TalearrSourceGapFill.Normalize(s.Title), StringComparison.Ordinal)))
            {
                return null;
            }

            return subtitle;
        }

        private static V5Book MatchExact(List<V5Book> books, Work work)
        {
            var asins = new HashSet<string>(work.Items.Select(i => i.Asin), StringComparer.OrdinalIgnoreCase);
            var byAsin = books.Where(b => b?.Editions?.Any(e => EditionAsins(e).Any(asins.Contains)) == true).ToList();
            if (byAsin.Count == 1)
            {
                return byAsin[0];
            }

            var full = TalearrTitleNormalizer.NormalizeFull(work.FullTitle);
            var main = TalearrTitleNormalizer.NormalizeFull(work.Title);
            var byTitle = books
                .Where(b => b != null && (TalearrTitleNormalizer.NormalizeFull(BookTitle(b)) == full ||
                                          TalearrTitleNormalizer.NormalizeFull(b.Title) == main))
                .ToList();

            return Pick(byTitle, work);
        }

        // Audible sometimes puts the series place in brackets after the title: "Monuments to the
        // Dead (The Forgotten Ship Book 8)".
        internal static string WorkTitle(AudibleCatalogItem item)
        {
            var title = BracketRegex.Replace(item.Title ?? string.Empty, match =>
            {
                var label = match.Groups["label"].Value.Trim();
                var normalized = TalearrSourceGapFill.Normalize(label);
                return SeriesLabelRegex.IsMatch(label) ||
                       item.Series.Any(s => normalized.Contains(TalearrSourceGapFill.Normalize(s.Title), StringComparison.Ordinal))
                    ? " "
                    : match.Value;
            });

            title = Regex.Replace(title, @"\s{2,}", " ").Trim();
            return TalearrTitleNormalizer.CleanDisplayTitle(title.Length == 0 ? item.Title : title);
        }

        // Each book's places as "series|position" (normalized), from the series lists and the
        // book's own series.
        private static Dictionary<V5Book, HashSet<string>> SeriesPlaces(V5AuthorResponse response)
        {
            var byId = response.Books.Where(b => b?.Id != null).GroupBy(b => b.Id).ToDictionary(g => g.Key, g => g.First());
            var places = new Dictionary<V5Book, HashSet<string>>();

            void Add(V5Book book, string series, string position)
            {
                if (book == null || string.IsNullOrWhiteSpace(series) || !IsNumber(position))
                {
                    return;
                }

                if (!places.TryGetValue(book, out var set))
                {
                    set = new HashSet<string>(StringComparer.Ordinal);
                    places[book] = set;
                }

                set.Add($"{TalearrSourceGapFill.Normalize(series)}|{NormalizeNumber(position)}");
            }

            foreach (var series in response.Series.Where(s => s?.Books != null))
            {
                foreach (var member in series.Books.Where(m => m?.BookId != null))
                {
                    byId.TryGetValue(member.BookId, out var book);
                    Add(book, series.Name, member.Position);
                }
            }

            foreach (var book in byId.Values)
            {
                Add(book, book.SeriesName, book.SeriesPosition);
            }

            return places;
        }

        // The book holds another number in a series the listing names ("Sunkiller" #1 against
        // "Sunkiller: Odyssey", Sunkiller book 2).
        private static bool OtherPlace(V5Book book, Work work, Dictionary<V5Book, HashSet<string>> places)
        {
            if (!places.TryGetValue(book, out var bookPlaces))
            {
                return false;
            }

            foreach (var series in work.Lead.Series.Where(s => IsNumber(s.Sequence)))
            {
                var name = TalearrSourceGapFill.Normalize(series.Title) + "|";
                var held = bookPlaces.Where(p => p.StartsWith(name, StringComparison.Ordinal)).ToList();
                if (held.Count > 0 && !held.Contains(name + NormalizeNumber(series.Sequence)))
                {
                    return true;
                }
            }

            return false;
        }

        private static V5Book MatchByPlace(IEnumerable<V5Book> books, Work work, Dictionary<V5Book, HashSet<string>> places)
        {
            var main = TalearrTitleNormalizer.NormalizeMain(work.Title);
            var workPlaces = work.Lead.Series
                .Where(s => IsNumber(s.Sequence))
                .Select(s => $"{TalearrSourceGapFill.Normalize(s.Title)}|{NormalizeNumber(s.Sequence)}")
                .ToList();

            if (main.Length == 0 || workPlaces.Count == 0)
            {
                return null;
            }

            var candidates = books
                .Where(b => b != null &&
                            TalearrTitleNormalizer.NormalizeMain(b.Title) == main &&
                            places.TryGetValue(b, out var bookPlaces) && workPlaces.Any(bookPlaces.Contains))
                .ToList();

            return candidates.Count == 1 ? candidates[0] : null;
        }

        private static V5Book MatchLoose(IEnumerable<V5Book> books, Work work)
        {
            var candidates = books
                .Where(b => b != null && !b.IsOmnibus && TalearrTitleNormalizer.IsSameWorkTitle(BookTitle(b), work.FullTitle))
                .ToList();

            return Pick(candidates, work);
        }

        // One candidate, or the one in the same series when there are several.
        private static V5Book Pick(List<V5Book> candidates, Work work)
        {
            if (candidates.Count <= 1)
            {
                return candidates.FirstOrDefault();
            }

            var seriesNames = work.Lead.Series.Select(s => TalearrSourceGapFill.Normalize(s.Title)).ToList();
            var inSeries = candidates
                .Where(b => seriesNames.Contains(TalearrSourceGapFill.Normalize(b.SeriesName)))
                .ToList();

            return inSeries.Count == 1 ? inSeries[0] : null;
        }

        private static void FillBook(V5AuthorResponse response, V5Book book, Work work, bool audibleLeads, string domain, TalearrAudibleMergeStats stats)
        {
            book.Editions ??= new List<V5Edition>();
            var audio = book.Editions.Where(e => e?.ReadingFormatId == TalearrHardcoverMapper.V5Audio).ToList();

            if (audio.Count == 0 || audibleLeads)
            {
                var rank = audibleLeads ? book.Editions.Select(e => e?.RatingCount ?? 0).DefaultIfEmpty(0).Max() + work.Items.Count : 0;
                foreach (var item in work.Items)
                {
                    if (book.Editions.Any(e => EditionAsins(e).Contains(item.Asin, StringComparer.OrdinalIgnoreCase)))
                    {
                        continue;
                    }

                    var edition = NewEdition(item, work, domain);
                    edition.RatingCount = rank > 0 ? rank-- : 0;
                    book.Editions.Add(edition);
                    stats.EditionsAdded++;
                }

                book.HasAudiobook = true;
                book.DurationSeconds ??= work.Lead.RuntimeMinutes * 60;
            }
            else if (audio.All(e => EditionAsins(e).Count == 0))
            {
                // Hardcover's audiobook listing often has no ASIN; the ASIN is what links an
                // Audiobookshelf item and the recordings facts to it.
                var unnamed = audio.Where(e => string.IsNullOrWhiteSpace(e.Asin)).ToList();
                if (unnamed.Count == 1 && SameNarrators(unnamed[0], work.Lead))
                {
                    unnamed[0].Asin = work.Lead.Asin;
                    unnamed[0].Asins = new List<string> { work.Lead.Asin };
                    stats.AsinsFilled++;
                }
            }

            PlaceInSeries(response, book, work, audibleLeads, stats);
        }

        private static void PlaceInSeries(V5AuthorResponse response, V5Book book, Work work, bool audibleLeads, TalearrAudibleMergeStats stats)
        {
            var audibleSeries = work.Lead.Series.FirstOrDefault(s => IsNumber(s.Sequence)) ?? work.Lead.Series.FirstOrDefault();
            if (audibleSeries == null)
            {
                return;
            }

            var position = IsNumber(audibleSeries.Sequence) ? NormalizeNumber(audibleSeries.Sequence) : audibleSeries.Sequence;
            var name = TalearrSourceGapFill.Normalize(audibleSeries.Title);
            var memberships = response.Series.Where(s => s?.Books?.Any(m => m?.BookId == book.Id) == true).ToList();
            var target = response.Series.FirstOrDefault(s => s != null &&
                (TalearrSourceGapFill.Normalize(s.Name) == name ||
                 (audibleSeries.Asin != null && string.Equals(StripPrefix(s.AmazonSeriesAsin), audibleSeries.Asin, StringComparison.OrdinalIgnoreCase))));

            // Fill: a book Hardcover already places in another series keeps that.
            if (!audibleLeads && target == null && (memberships.Count > 0 || !string.IsNullOrWhiteSpace(book.SeriesName)))
            {
                return;
            }

            if (target == null)
            {
                if (audibleSeries.Asin == null)
                {
                    return;
                }

                var id = $"az:{audibleSeries.Asin}";
                target = new V5Series
                {
                    Id = id,
                    AmazonSeriesAsin = id,
                    Name = audibleSeries.Title,
                    SeriesType = "main",
                    ProviderUrls = new ProviderUrlMap()
                };
                response.Series.Add(target);
                stats.SeriesCreated++;
            }

            target.Books ??= new List<V5SeriesBook>();
            var member = target.Books.FirstOrDefault(m => m?.BookId == book.Id);
            if (member == null)
            {
                target.Books.Add(new V5SeriesBook
                {
                    BookId = book.Id,
                    Title = book.Title,
                    Position = position,
                    CoverUrl = book.CoverUrl,
                    IsPrimary = IsWholeNumber(position)
                });
                stats.SeriesFilled++;
            }
            else if (position != null && (string.IsNullOrWhiteSpace(member.Position) || (audibleLeads && member.Position != position)))
            {
                member.Position = position;
                member.IsPrimary = IsWholeNumber(position);
                stats.SeriesFilled++;
            }

            target.TotalBooks = Math.Max(target.TotalBooks, target.Books.Count);
            target.PrimaryBooks = Math.Max(target.PrimaryBooks, target.Books.Count(m => m.IsPrimary == true));
            target.Numbered |= target.Books.Any(m => !string.IsNullOrWhiteSpace(m.Position));

            if (string.IsNullOrWhiteSpace(book.SeriesName) || audibleLeads ||
                (TalearrSourceGapFill.Normalize(book.SeriesName) == TalearrSourceGapFill.Normalize(target.Name) && string.IsNullOrWhiteSpace(book.SeriesPosition)))
            {
                book.SeriesName = target.Name;
                book.SeriesPosition = position;
            }
        }

        private static V5Book NewBook(Work work, string domain)
        {
            var lead = work.Lead;
            var id = $"az:{lead.Asin}";
            var date = TalearrHardcoverMapper.ParseDate(lead.ReleaseDate);
            var editions = work.Items.Select(i => NewEdition(i, work, domain)).ToList();
            var url = $"https://www.{domain}/pd/{lead.Asin}";

            var book = new V5Book
            {
                Id = id,
                BaseBookId = id,
                Title = work.Title,
                Subtitle = work.Subtitle,
                Description = Text(lead.Summary),
                PublicationDate = date,
                PublicationYear = date?.Year,
                LanguageCode = LanguageCode(lead.Language),
                Publisher = lead.Publisher,
                DurationSeconds = lead.RuntimeMinutes * 60,
                CoverUrl = lead.ImageUrl,
                HasAudiobook = true,
                IsOmnibus = work.IsCollection,
                Editions = editions,
                Links = new Dictionary<string, string> { ["audible"] = url },
                ProviderUrls = new ProviderUrlMap()
            };

            book.ProviderUrls.SetNormalized("audible", url);
            return book;
        }

        private static V5Edition NewEdition(AudibleCatalogItem item, Work work, string domain)
        {
            var url = $"https://www.{domain}/pd/{item.Asin}";
            var edition = new V5Edition
            {
                Id = $"az:{item.Asin}",
                ReadingFormatId = TalearrHardcoverMapper.V5Audio,
                Format = TalearrHardcoverMapper.FormatName(TalearrHardcoverMapper.V5Audio),
                EditionFormat = EditionFormat,
                EditionInfo = string.Equals(item.FormatType, "abridged", StringComparison.OrdinalIgnoreCase) ? "Abridged" : null,
                Title = WorkTitle(item),
                Subtitle = WorkSubtitle(item),
                Language = LanguageCode(item.Language),
                LanguageCode = LanguageCode(item.Language),
                Asin = item.Asin,
                Asins = new List<string> { item.Asin },
                DurationSeconds = item.RuntimeMinutes * 60,
                Publisher = item.Publisher,
                PublicationDate = TalearrHardcoverMapper.ParseDate(item.ReleaseDate),
                CoverUrl = item.ImageUrl ?? work.Lead.ImageUrl,
                Description = Text(item.Summary),
                ProviderUrls = new ProviderUrlMap()
            };

            var order = 0;
            foreach (var narrator in item.Narrators)
            {
                edition.Narrators.Add(new V5NarratorCredit
                {
                    Name = narrator,
                    Order = order,
                    IsPrimary = order == 0,
                    Role = "Narrator"
                });
                edition.NarratorNames.Add(narrator);
                order++;
            }

            edition.ProviderUrls.SetNormalized("audible", url);
            return edition;
        }

        private static string BookTitle(V5Book book)
        {
            return string.IsNullOrWhiteSpace(book.Subtitle) || TalearrTitleNormalizer.HasSubtitle(book.Title)
                ? book.Title
                : $"{book.Title}: {book.Subtitle}";
        }

        private static List<string> EditionAsins(V5Edition edition)
        {
            var asins = new List<string>();
            if (edition == null)
            {
                return asins;
            }

            foreach (var asin in new[] { edition.Asin }.Concat(edition.Asins ?? new List<string>()))
            {
                if (!string.IsNullOrWhiteSpace(asin) && !asins.Contains(asin.Trim(), StringComparer.OrdinalIgnoreCase))
                {
                    asins.Add(asin.Trim().ToUpperInvariant());
                }
            }

            return asins;
        }

        private static bool SharesAsin(V5Edition edition, Work work)
        {
            var asins = EditionAsins(edition);
            return work.Items.Any(i => asins.Contains(i.Asin, StringComparer.OrdinalIgnoreCase));
        }

        private static bool SameNarrators(V5Edition edition, AudibleCatalogItem item)
        {
            var names = (edition.NarratorNames ?? new List<string>())
                .Concat((edition.Narrators ?? new List<V5NarratorCredit>()).Select(n => n?.Name))
                .Select(TalearrSourceGapFill.Normalize)
                .Where(n => n.Length > 0)
                .ToList();

            return names.Count > 0 && item.Narrators.Any(n => names.Contains(TalearrSourceGapFill.Normalize(n)));
        }

        private static bool IsUnabridged(AudibleCatalogItem item)
        {
            return !string.Equals(item.FormatType, "abridged", StringComparison.OrdinalIgnoreCase);
        }

        private static bool IsNumber(string value)
        {
            return decimal.TryParse(value?.Trim(), NumberStyles.Number, CultureInfo.InvariantCulture, out _);
        }

        private static string NormalizeNumber(string value)
        {
            return decimal.Parse(value.Trim(), NumberStyles.Number, CultureInfo.InvariantCulture).ToString("0.##", CultureInfo.InvariantCulture);
        }

        private static bool IsWholeNumber(string position)
        {
            return decimal.TryParse(position, NumberStyles.Number, CultureInfo.InvariantCulture, out var number) && decimal.Truncate(number) == number;
        }

        private static string StripPrefix(string providerId)
        {
            return string.IsNullOrWhiteSpace(providerId) ? null : ProviderIdHelper.StripPrefix(providerId);
        }

        private static string Text(string html)
        {
            if (string.IsNullOrWhiteSpace(html))
            {
                return null;
            }

            var text = WebUtility.HtmlDecode(TagRegex.Replace(html, " "));
            return Regex.Replace(text, @"\s{2,}", " ").Trim();
        }

        // Audible's language names to the two-letter codes the edition languages use.
        internal static string LanguageCode(string language)
        {
            return (language ?? string.Empty).Trim().ToLowerInvariant() switch
            {
                "english" => "en",
                "french" => "fr",
                "german" => "de",
                "italian" => "it",
                "spanish" => "es",
                "japanese" => "ja",
                "portuguese" => "pt",
                "dutch" => "nl",
                _ => null
            };
        }

        private static string TalearrAudibleMarketplacesDomain(string marketplace)
        {
            return Recordings.TalearrAudibleMarketplaces.Get(marketplace).Domain;
        }
    }
}
