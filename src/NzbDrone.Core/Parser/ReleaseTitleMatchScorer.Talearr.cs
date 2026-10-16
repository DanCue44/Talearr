using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using NzbDrone.Common.Extensions;
using NzbDrone.Core.Books;

namespace NzbDrone.Core.Parser
{
    /// <summary>
    /// TaleArr: looser release title matching.
    ///
    /// Releases often carry a shorter or reshuffled form of the book title:
    ///   "Ryan Becker - Murderous Minds (2017) [MP3]"   for "Murderous Minds: Stories of Real Life Murderers..."
    ///   "John Grisham - Pelican Brief, The (epub)"      for "The Pelican Brief"
    ///   "Ali Hazelwood - Love Hypothesis (2021)"        for "The Love Hypothesis"
    ///   "Brandon Sanderson - The Final Empire"          for "Mistborn: The Final Empire" (series prefix)
    ///   "Alex Aster - Skyshade"                         for "Skyshade (Lightlark, #3)"
    ///
    /// Each loose variant remembers the text it dropped. Guards keep different books by the same
    /// author apart:
    ///   * a loose variant is not used when another book in the author's catalog has the same short
    ///     form ("True Crime Stories" when the author has several "True Crime Stories: ..." books);
    ///   * after a subtitle-less match, the release may only continue with format noise, a year, the
    ///     author, the series, or the start of the real subtitle. A different subtitle or an
    ///     unexplained number ("True Crime Stories 12 ...") is a SubtitleMismatch;
    ///   * a title that is only the series name followed by "Series" ("Bride Series 02 Mate") is a
    ///     SeriesNameOnly problem, so it does not match the book "Bride".
    /// </summary>
    public static partial class ReleaseTitleMatchScorer
    {
        private static readonly Regex TalearrTrailingBracketRegex = new Regex(@"\s*[\(\[](?<label>[^\)\]]+)[\)\]]\s*$", RegexOptions.Compiled);
        private static readonly Regex TalearrSeriesBracketRegex = new Regex(@"(#\s*\d|\b(?:book|bk|vol|volume|tome|part)\s*\.?\s*\d)", RegexOptions.Compiled | RegexOptions.IgnoreCase);

        private static readonly HashSet<string> TalearrArticles = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "the", "a", "an"
        };

        private static readonly HashSet<string> TalearrStopwords = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "a", "an", "and", "or", "the", "of", "in", "on", "at", "by", "for", "with", "to", "from", "as", "is"
        };

        private static readonly HashSet<string> TalearrPositionMarkers = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "book", "bk", "vol", "volume", "tome", "part", "pt", "no", "number", "episode"
        };

        private static void AddTalearrLooseVariants(BookTitleMatchContext context, Book book, IEnumerable<Book> authorCatalogBooks)
        {
            if (context == null || string.IsNullOrWhiteSpace(context.PrimaryTitle))
            {
                return;
            }

            var candidates = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var (variant, dropped) in GetTalearrLooseVariants(context.PrimaryTitle, context.SeriesName, book?.Title))
            {
                if (!candidates.ContainsKey(variant))
                {
                    candidates[variant] = dropped;
                }
            }

            if (candidates.Count == 0)
            {
                return;
            }

            var primaryKeys = new HashSet<string>(context.PrimaryVariants.Select(TalearrTokenKey), StringComparer.Ordinal);
            var otherBooks = (authorCatalogBooks ?? Enumerable.Empty<Book>())
                .Where(other => CanContradict(book, other))
                .ToList();

            foreach (var candidate in candidates)
            {
                var key = TalearrTokenKey(candidate.Key);
                if (key.Length == 0 || primaryKeys.Contains(key))
                {
                    continue;
                }

                // A short form shared with another book by this author is ambiguous: never use it.
                var articleFreeKey = StripLeadingArticleKey(key);
                if (otherBooks.Any(other =>
                    {
                        var otherKeys = GetTalearrBookKeys(other);
                        return otherKeys.Contains(key) || otherKeys.Contains(articleFreeKey);
                    }))
                {
                    continue;
                }

                if (!context.LooseVariants.ContainsKey(candidate.Key))
                {
                    context.LooseVariants[candidate.Key] = candidate.Value;
                    AddTokens(context.PrefixAllowanceTokens, candidate.Value);
                }
            }
        }

        internal static IEnumerable<(string Variant, string Dropped)> GetTalearrLooseVariants(string title, string seriesName, string workTitle = null)
        {
            if (string.IsNullOrWhiteSpace(title))
            {
                yield break;
            }

            var bases = new List<(string Text, string Dropped)> { (title.Trim(), string.Empty) };

            // "Skyshade (Lightlark, #3)" / "The Final Empire [Mistborn 1]" -> drop the series bracket.
            var bracket = TalearrTrailingBracketRegex.Match(title);
            if (bracket.Success && bracket.Index > 0)
            {
                var label = bracket.Groups["label"].Value;
                if (TalearrSeriesBracketRegex.IsMatch(label) ||
                    (seriesName.IsNotNullOrWhiteSpace() && Tokenize(label).Take(Tokenize(seriesName).Count).SequenceEqual(Tokenize(seriesName), StringComparer.OrdinalIgnoreCase)) ||
                    TalearrTitleNormalizer.IsFormatQualifier(label))
                {
                    var withoutBracket = title.Substring(0, bracket.Index).Trim();
                    if (IsUsableLooseVariant(withoutBracket, allowSingleWord: true))
                    {
                        bases.Add((withoutBracket, label));
                    }
                }
            }

            foreach (var (text, dropped) in bases.ToList())
            {
                var main = TalearrTitleNormalizer.GetMainTitle(text);
                var subtitle = TalearrTitleNormalizer.GetSubtitle(text);
                if (main.Length == 0 || subtitle.Length == 0)
                {
                    continue;
                }

                var subtitleIsWorkTitle = workTitle.IsNotNullOrWhiteSpace() &&
                                          !TokenizedEquals(workTitle, text) &&
                                          TokenizedEquals(TalearrTitleNormalizer.StripAllBrackets(workTitle), subtitle);

                if ((seriesName.IsNotNullOrWhiteSpace() && TokenizedEquals(main, seriesName)) || subtitleIsWorkTitle)
                {
                    // Series or character prefix: "Mistborn: The Final Empire" is released as
                    // "The Final Empire", and "Sherlock Holmes: The Speckled Band" must not match a
                    // release called just "Sherlock Holmes".
                    if (Tokenize(subtitle).Count >= 2)
                    {
                        bases.Add((subtitle, string.Empty));
                    }

                    continue;
                }

                if (TalearrTitleNormalizer.HasSubtitle(subtitle))
                {
                    // "Pocket Potters: Harry Potter: Little Guides ...": the first segment of a
                    // multi-level title is a collection name, not the book.
                    continue;
                }

                // A single word is only enough when the dropped part is series information
                // ("Mate: Bride, Vol. 2"); "Dune: The Duke of Caladan" must not become "Dune".
                if (IsUsableLooseVariant(main, allowSingleWord: IsSeriesInformation(subtitle, seriesName)))
                {
                    bases.Add((main, JoinDropped(subtitle, dropped)));
                }
            }

            foreach (var (text, dropped) in bases)
            {
                if (!string.Equals(text, title.Trim(), StringComparison.OrdinalIgnoreCase))
                {
                    yield return (text, dropped);
                }

                var tokens = Tokenize(text);
                if (tokens.Count >= 2 && TalearrArticles.Contains(tokens[0]))
                {
                    var rest = string.Join(" ", tokens.Skip(1));

                    // "Pelican Brief, The" (library sort order) keeps the article at the end.
                    yield return ($"{rest}, {tokens[0]}", dropped);

                    // "Love Hypothesis" for "The Love Hypothesis"; one remaining word is too weak.
                    if (tokens.Count >= 3)
                    {
                        yield return (rest, dropped);
                    }
                }

                if (tokens.Count >= 3 && tokens[^2] == "a" && tokens[^1] == "novel")
                {
                    yield return (string.Join(" ", tokens.Take(tokens.Count - 2)), JoinDropped("a novel", dropped));
                }
            }
        }

        private static void AddTalearrProblems(List<TitleMatchProblem> problems, IReadOnlyList<string> releaseTokens, int matchedStart, int matchedEnd, string variant, string authorName, BookTitleMatchContext context)
        {
            if (problems == null || releaseTokens == null)
            {
                return;
            }

            var seriesProblem = GetTalearrSeriesNameOnlyProblem(releaseTokens, matchedEnd, variant, context);
            if (seriesProblem != null && !problems.Any(p => p.Code == seriesProblem.Code))
            {
                problems.Add(seriesProblem);
            }

            if (context?.LooseVariants == null || !context.LooseVariants.TryGetValue(variant ?? string.Empty, out var dropped))
            {
                return;
            }

            var mismatch = GetTalearrSubtitleMismatch(releaseTokens, matchedEnd, dropped, authorName, context);
            if (mismatch != null && !problems.Any(p => p.Code == mismatch.Code))
            {
                problems.Add(mismatch);
            }
        }

        private static TitleMatchProblem GetTalearrSeriesNameOnlyProblem(IReadOnlyList<string> releaseTokens, int matchedEnd, string variant, BookTitleMatchContext context)
        {
            var next = matchedEnd + 1;
            if (next >= releaseTokens.Count ||
                !string.Equals(releaseTokens[next], "series", StringComparison.OrdinalIgnoreCase) ||
                string.IsNullOrWhiteSpace(context?.SeriesName) ||
                !TokenizedEquals(variant, context.SeriesName))
            {
                return null;
            }

            return new TitleMatchProblem
            {
                Code = TitleMatchProblemCode.SeriesNameOnly,
                Value = $"{context.SeriesName} series"
            };
        }

        private static TitleMatchProblem GetTalearrSubtitleMismatch(IReadOnlyList<string> releaseTokens, int matchedEnd, string dropped, string authorName, BookTitleMatchContext context)
        {
            var droppedTokens = Tokenize(dropped)
                .SkipWhile(token => TalearrArticles.Contains(token))
                .ToList();
            var authorTokens = new HashSet<string>(Tokenize(authorName), StringComparer.OrdinalIgnoreCase);
            var seriesTokens = new HashSet<string>(Tokenize(context?.SeriesName), StringComparer.OrdinalIgnoreCase);

            for (var index = matchedEnd + 1; index < releaseTokens.Count; index++)
            {
                var token = releaseTokens[index];

                if (YearTokenRegex.IsMatch(token) || IsLanguageToken(token))
                {
                    continue;
                }

                if (IsNumericToken(token) || TalearrPositionMarkers.Contains(token))
                {
                    if (IsNumericToken(token) && SeriesPositionMatches(context, token))
                    {
                        continue;
                    }

                    if (TalearrPositionMarkers.Contains(token) && index + 1 < releaseTokens.Count && SeriesPositionMatches(context, releaseTokens[index + 1]))
                    {
                        index++;
                        continue;
                    }

                    if (droppedTokens.Count > 0 && TokensMatch(droppedTokens[0], token))
                    {
                        return null;
                    }

                    return CreateSubtitleMismatch(token);
                }

                if (MetadataTokens.Contains(token) && !TalearrStopwords.Contains(token))
                {
                    // A format tag ("m4b", "epub", "unabridged") closes the title part of a release
                    // name; what follows is narrator, bitrate or release group.
                    return null;
                }

                if (TalearrStopwords.Contains(token))
                {
                    // "- The Extremism Nobody is Talking About": look at the word after the article.
                    continue;
                }

                if (authorTokens.Contains(token) || seriesTokens.Contains(token))
                {
                    return null;
                }

                if (droppedTokens.Count > 0 && TokensMatch(droppedTokens[0], token))
                {
                    return null;
                }

                return CreateSubtitleMismatch(token);
            }

            return null;
        }

        private static TitleMatchProblem CreateSubtitleMismatch(string token)
        {
            return new TitleMatchProblem
            {
                Code = TitleMatchProblemCode.SubtitleMismatch,
                Value = token
            };
        }

        private static readonly ConditionalWeakTable<Book, TalearrBookKeys> TalearrBookKeyCache = new ConditionalWeakTable<Book, TalearrBookKeys>();

        private sealed class TalearrBookKeys
        {
            public string Title { get; init; }
            public string PrimaryTitle { get; init; }
            public HashSet<string> Keys { get; init; }
        }

        // Full, main (no subtitle or brackets) and article-free token keys of a catalog book, cached
        // per book object because the same catalog is scored once per release.
        private static HashSet<string> GetTalearrBookKeys(Book book)
        {
            var title = book?.Title;
            var primaryTitle = GetPrimaryBookTitle(book);
            if (book != null &&
                TalearrBookKeyCache.TryGetValue(book, out var cached) &&
                string.Equals(cached.Title, title, StringComparison.Ordinal) &&
                string.Equals(cached.PrimaryTitle, primaryTitle, StringComparison.Ordinal))
            {
                return cached.Keys;
            }

            var keys = new HashSet<string>(StringComparer.Ordinal);
            foreach (var value in new[] { title, primaryTitle }.Where(t => t.IsNotNullOrWhiteSpace()).Distinct(StringComparer.OrdinalIgnoreCase))
            {
                var full = TalearrTokenKey(value);
                keys.Add(full);
                keys.Add(StripLeadingArticleKey(full));

                var main = TalearrTokenKey(TalearrTitleNormalizer.GetMainTitle(TalearrTitleNormalizer.StripAllBrackets(value)));
                keys.Add(main);
                keys.Add(StripLeadingArticleKey(main));
            }

            keys.Remove(string.Empty);

            if (book != null)
            {
                TalearrBookKeyCache.AddOrUpdate(book, new TalearrBookKeys { Title = title, PrimaryTitle = primaryTitle, Keys = keys });
            }

            return keys;
        }

        private static bool IsSeriesInformation(string text, string seriesName)
        {
            if (string.IsNullOrWhiteSpace(text))
            {
                return false;
            }

            if (TalearrSeriesBracketRegex.IsMatch(text))
            {
                return true;
            }

            var seriesTokens = Tokenize(seriesName);
            return seriesTokens.Count > 0 &&
                   Tokenize(text).Take(seriesTokens.Count).SequenceEqual(seriesTokens, StringComparer.OrdinalIgnoreCase);
        }

        private static bool IsUsableLooseVariant(string text, bool allowSingleWord)
        {
            var tokens = Tokenize(text);
            if (tokens.Count == 0)
            {
                return false;
            }

            if (tokens.Count >= 2)
            {
                return tokens.Any(token => !TalearrStopwords.Contains(token) && !IsNumericToken(token));
            }

            var single = tokens[0];
            return allowSingleWord && single.Length >= 4 && !TalearrStopwords.Contains(single) && !IsNumericToken(single) && !MetadataTokens.Contains(single);
        }

        private static string TalearrTokenKey(string text)
        {
            return string.Join(" ", Tokenize(text));
        }

        private static string StripLeadingArticleKey(string key)
        {
            var space = key.IndexOf(' ');
            return space > 0 && TalearrArticles.Contains(key.Substring(0, space)) ? key.Substring(space + 1) : key;
        }

        private static string JoinDropped(string first, string second)
        {
            return string.Join(" ", new[] { first, second }.Where(part => part.IsNotNullOrWhiteSpace()));
        }
    }
}
