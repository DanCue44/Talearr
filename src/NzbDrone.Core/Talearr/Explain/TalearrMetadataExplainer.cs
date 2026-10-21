using System;
using System.Collections.Generic;
using System.Linq;
using NzbDrone.Common.Extensions;
using NzbDrone.Core.Books;
using NzbDrone.Core.Books.Services;

namespace NzbDrone.Core.Talearr.Explain
{
    public interface ITalearrMetadataExplainer
    {
        TalearrDecisionEntry Explain(Book book, string workId, IReadOnlyList<Edition> editions, string preferredLanguage);
    }

    // Talearr: the metadata step of the "Why" story, built from the book as it is now (which work
    // it is, which edition is selected and why the others are not). Computed on request, so it
    // always matches what searches and imports will use.
    public class TalearrMetadataExplainer : ITalearrMetadataExplainer
    {
        public TalearrDecisionEntry Explain(Book book, string workId, IReadOnlyList<Edition> editions, string preferredLanguage)
        {
            var entry = new TalearrDecisionEntry
            {
                BookId = book?.Id ?? 0,
                Stage = TalearrDecisionStages.Metadata,
                CreatedAt = DateTime.UtcNow
            };

            if (book == null)
            {
                entry.Outcome = TalearrDecisionOutcomes.Skipped;
                entry.Code = "book-missing";
                entry.Summary = "This book is no longer in the library";
                return entry;
            }

            var mediaType = book.MediaType;
            var formatName = mediaType == BookMediaType.Ebook ? "ebook" : "audiobook";
            var nativeFormat = mediaType == BookMediaType.Audiobook ? 2 : 3;
            var all = (editions ?? Array.Empty<Edition>()).Where(e => e != null).ToList();
            var details = new List<string>();

            var source = DescribeSource(workId);
            details.Add(source != null
                ? $"Matched to the {source} work {workId}."
                : "Matched to a work added without a metadata provider.");

            var selected = all.Where(e => e.Monitored).OrderByDescending(e => e.ManualAdd).ThenBy(e => e.Id).FirstOrDefault();
            if (selected == null)
            {
                entry.Outcome = TalearrDecisionOutcomes.Skipped;
                entry.Code = "no-edition";
                entry.Summary = "No edition is selected";
                details.Add(all.Any()
                    ? $"There are {all.Count} known editions, but none is selected, so searches have nothing specific to match."
                    : "No editions are known yet. A refresh fetches them from the metadata provider.");
                entry.Details = details;
                return entry;
            }

            details.Add($"Selected edition: {DescribeEdition(selected)}.");

            var nativeCandidates = all.Where(e => e.ReadingFormatId == nativeFormat).ToList();
            var stub = selected.ForeignEditionId.IsNullOrWhiteSpace() && (selected.Images == null || !selected.Images.Any());

            if (stub && all.Any(e => e.Id != selected.Id && e.ForeignEditionId.IsNotNullOrWhiteSpace()))
            {
                entry.Code = "edition-placeholder";
                details.Add("The selected edition has no details from the metadata provider, while real editions are available. A refresh should pick one of them.");
            }
            else if (selected.ManualAdd)
            {
                entry.Code = "edition-pinned";
                details.Add("You picked this edition, so automatic selection leaves it alone.");
            }
            else if (selected.ReadingFormatId == nativeFormat)
            {
                var sameLanguage = nativeCandidates
                    .Where(e => SameLanguage(e, selected))
                    .ToList();
                var mostVotes = sameLanguage.Max(e => e.Ratings?.Votes ?? 0);

                if (IsLanguage(selected, preferredLanguage))
                {
                    entry.Code = "edition-native-preferred-language";
                    details.Add($"It is an {formatName} edition in your preferred language.");
                }
                else
                {
                    entry.Code = "edition-native-format";
                    details.Add($"It is an {formatName} edition. No {formatName} edition in your preferred language is listed.");
                }

                if (sameLanguage.Count > 1 && (selected.Ratings?.Votes ?? 0) >= mostVotes && mostVotes > 0)
                {
                    details.Add($"Among {sameLanguage.Count} such editions it has the most ratings ({selected.Ratings.Votes:N0}).");
                }
            }
            else
            {
                entry.Code = "edition-fallback-format";
                details.Add(nativeCandidates.Any()
                    ? $"It is not an {formatName} edition; the {formatName} editions that exist are in other languages."
                    : $"No {formatName} edition is listed for this book, so the best other edition stands in for it.");
            }

            var others = all.Where(e => e.Id != selected.Id).ToList();
            if (others.Any())
            {
                var otherNative = others.Count(e => e.ReadingFormatId == nativeFormat);
                var otherFormats = others.Count(e => e.ReadingFormatId != nativeFormat);
                var foreign = others.Count(e => e.Language.IsNotNullOrWhiteSpace() && !IsLanguage(e, preferredLanguage));

                var parts = new List<string>();
                if (otherNative > 0)
                {
                    parts.Add(otherNative == 1 ? $"1 other {formatName} edition" : $"{otherNative} other {formatName} editions");
                }

                if (otherFormats > 0)
                {
                    parts.Add(otherFormats == 1 ? "1 edition in another format" : $"{otherFormats} editions in other formats");
                }

                details.Add("Not used: " + string.Join(" and ", parts) + (foreign > 0 ? $" ({foreign} of them in other languages)." : "."));
            }

            entry.Outcome = TalearrDecisionOutcomes.Matched;
            entry.Code ??= "edition-selected";
            entry.Summary = entry.Code == "edition-placeholder"
                ? "Matched, but the selected edition is an empty placeholder"
                : $"Matched to {source ?? "the library"} and using one {formatName} edition";
            entry.Details = details;
            return entry;
        }

        internal static string DescribeSource(string foreignId)
        {
            if (foreignId.IsNullOrWhiteSpace())
            {
                return null;
            }

            var prefix = foreignId.Split(':')[0].ToLowerInvariant();
            return prefix switch
            {
                "hc" => "Hardcover",
                "gr" => "Goodreads",
                "ol" => "Open Library",
                "az" => "Audible",
                "gb" => "Google Books",
                _ => foreignId.StartsWith("OL", StringComparison.Ordinal) ? "Open Library" : "metadata provider"
            };
        }

        private static string DescribeEdition(Edition edition)
        {
            var parts = new List<string>();
            var format = edition.EditionFormat.IsNotNullOrWhiteSpace() ? edition.EditionFormat : edition.Format;
            if (format.IsNotNullOrWhiteSpace())
            {
                parts.Add(format);
            }

            if (edition.Language.IsNotNullOrWhiteSpace())
            {
                parts.Add(edition.Language);
            }

            if (edition.Publisher.IsNotNullOrWhiteSpace())
            {
                parts.Add(edition.Publisher);
            }

            if (edition.ReleaseDate.HasValue)
            {
                parts.Add(edition.ReleaseDate.Value.Year.ToString());
            }

            var title = edition.Title.IsNotNullOrWhiteSpace() ? $"\"{edition.Title}\"" : "untitled";
            return parts.Any() ? $"{title} ({string.Join(", ", parts)})" : title;
        }

        private static bool SameLanguage(Edition a, Edition b)
        {
            return string.Equals(TalearrEditionPreference.NormalizeLanguage(a.Language), TalearrEditionPreference.NormalizeLanguage(b.Language), StringComparison.OrdinalIgnoreCase);
        }

        private static bool IsLanguage(Edition edition, string preferredLanguage)
        {
            return preferredLanguage.IsNullOrWhiteSpace() || TalearrEditionPreference.IsPreferredLanguage(edition, preferredLanguage);
        }
    }
}
