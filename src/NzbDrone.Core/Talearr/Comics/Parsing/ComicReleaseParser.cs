using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace NzbDrone.Core.Talearr.Comics.Parsing
{
    public enum ComicNumberKind
    {
        None,
        Volume,
        Chapter,
        Issue,

        // A bare number ("Witch Hat Atelier 12"): a volume or a chapter, decided against what the
        // series is known to have.
        Bare
    }

    public class ParsedComicRelease
    {
        public string OriginalTitle { get; set; }
        public string SeriesTitle { get; set; }
        public string Publisher { get; set; }
        public ComicNumberKind NumberKind { get; set; }
        public decimal? Number { get; set; }
        public decimal? NumberEnd { get; set; }
        public string VolumeTitle { get; set; }

        // Words between a known series title and the number ("Kitchen" in "Witch Hat Atelier
        // Kitchen v04"): usually a spin-off or a different series that starts the same way.
        public string ExtraTitleWords { get; set; }

        public bool MatchedKnownTitle { get; set; }
        public int? Year { get; set; }
        public string Source { get; set; } = ComicSources.Unknown;
        public bool IsFixed { get; set; }
        public string Format { get; set; } = ComicFormats.Unknown;
        public string Group { get; set; }

        // omnibus, deluxe, perfect, 3-in-1, compendium, box set... Null for a regular edition.
        public string Edition { get; set; }

        // Null when English or not stated, otherwise the language named in the release.
        public string Language { get; set; }
        public bool IsScanlation { get; set; }
        public bool IsAnnual { get; set; }
        public bool IsAdultHint { get; set; }

        public bool IsPack => Number.HasValue && NumberEnd.HasValue && NumberEnd > Number;
    }

    // Talearr: reads comic and manga release names. Built from real indexer results (notes:
    // TALEARR_COMICS_PLAN.md and the parser tests), which come in three shapes:
    //   Saga v01 (2012) (Digital) (F) (Asgard-Empire).cbr               paren tags, Empire groups
    //   Jujutsu.Kaisen.211.[2023].[Digital].[anadius].cbz-xpost         dotted, bracket tags
    //   VIZ.Media.One.Piece.Vol.95.Oden.s.Adventure.2020.Hybrid.Comic.eBook-BitBook   scene
    // Pass the series' known titles when checking a release for a series: names like "Kaiju No. 8"
    // or "Mob Psycho 100" hold numbers, and knowing where the title ends is what keeps them right.
    public static class ComicReleaseParser
    {
        private static readonly Regex NoiseSuffix = new Regex(@"(?:[-\s]*\[?N-Z-B\]?)?(?:-xpost|\s+xpost)$|\s*\[[0-9a-f]{7,10}\]\s*", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        private static readonly Regex ExtensionSuffix = new Regex(@"\.(cbz|cbr|cb7|pdf|epub|zip|rar|7z)$", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        private static readonly Regex BracketTag = new Regex(@"[\(\[\{]([^\(\)\[\]\{\}]*)[\)\]\}]", RegexOptions.Compiled);
        private static readonly Regex SceneGroup = new Regex(@"-(?<group>[A-Za-z0-9]{2,20})$", RegexOptions.Compiled);

        private static readonly Regex Chapter = new Regex(@"(?<![a-z])(?:chapter|chap|ch\.?|c(?=\d))\s*(?<n>\d{1,4}(?:\.\d)?)(?:\s*-\s*(?:chapter|ch\.?|c)?\s*(?<e>\d{1,4}))?(?!\d)", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        private static readonly Regex Volume = new Regex(@"(?<![a-z])(?:volume|vol\.?|tome|tpb\s+vol\.?|tpb|v(?=\d)|t(?=\d))\s*\.*\s*(?<n>\d{1,4}(?:\.\d)?)(?:\s*-\s*(?:volume|vol\.?|v|t)?\s*(?<e>\d{1,4}))?(?!\d)", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        private static readonly Regex Issue = new Regex(@"(?:#|(?<![a-z])no\.?\s+)(?<n>\d{1,4}(?:\.\d)?)(?!\d)", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        private static readonly Regex Bare = new Regex(@"(?<![\w.])(?<n>\d{1,4}(?:\.\d)?)(?:\s*-\s*(?<e>\d{1,4}))?(?![\w])", RegexOptions.Compiled);
        private static readonly Regex YearToken = new Regex(@"^(?<y>(?:19|20)\d{2})(?:\s*-\s*(?:19|20)?\d{2})?$", RegexOptions.Compiled);
        private static readonly Regex YearInText = new Regex(@"(?<![\d])(?<y>(?:19|20)\d{2})(?![\d])", RegexOptions.Compiled);

        private static readonly string[] Publishers =
        {
            "VIZ Media", "Viz", "DC Comics", "Marvel Comics", "Marvel", "Image Comics", "Titan Comics", "Oni Press", "Dark Horse Comics",
            "Dark Horse", "IDW Publishing", "IDW", "Boom Studios", "BOOM! Studios", "Yen Press", "Kodansha Comics", "Kodansha USA", "Kodansha",
            "Seven Seas Entertainment", "Seven Seas", "Vertical Comics", "Vertical", "Square Enix Manga", "Square Enix", "Udon Entertainment",
            "Tokyopop", "Dynamite Entertainment", "Dynamite", "Archie Comics", "Fantagraphics", "Drawn and Quarterly", "First Second",
            "Scholastic", "Graphix", "Mad Cave Studios", "Lion Forge Comics", "Ghost Ship"
        };

        private static readonly (string Pattern, string Edition)[] Editions =
        {
            ("omnibus", "omnibus"),
            ("deluxe edition", "deluxe"),
            ("deluxe", "deluxe"),
            ("perfect edition", "perfect"),
            ("collector's edition", "collector"),
            ("collectors edition", "collector"),
            ("3-in-1", "3-in-1"),
            ("3 in 1", "3-in-1"),
            ("2-in-1", "2-in-1"),
            ("compendium", "compendium"),
            ("box set", "box set"),
            ("boxset", "box set"),
            ("kanzenban", "kanzenban"),
            ("fullmetal edition", "fullmetal"),
            ("master edition", "master"),
            ("library edition", "library"),
            ("absolute edition", "absolute"),
            ("big edition", "big")
        };

        private static readonly Dictionary<string, string> Languages = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["french"] = "fr", ["vf"] = "fr", ["vostfr"] = "fr", ["german"] = "de", ["deutsch"] = "de", ["italian"] = "it", ["ita"] = "it",
            ["spanish"] = "es", ["esp"] = "es", ["castellano"] = "es", ["espanol"] = "es", ["portuguese"] = "pt", ["pt-br"] = "pt", ["ptbr"] = "pt",
            ["russian"] = "ru", ["polish"] = "pl", ["dutch"] = "nl", ["japanese"] = "ja", ["raw"] = "ja", ["chinese"] = "zh", ["korean"] = "ko",
            ["indonesian"] = "id", ["vietnamese"] = "vi", ["thai"] = "th", ["turkish"] = "tr", ["swedish"] = "sv", ["danish"] = "da",
            ["finnish"] = "fi", ["norwegian"] = "no", ["czech"] = "cs", ["hungarian"] = "hu", ["arabic"] = "ar"
        };

        private static readonly HashSet<string> SceneWords = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "hybrid", "retail", "comic", "comics", "ebook", "internal", "proper", "repack", "cbz", "cbr", "cb7", "pdf", "epub", "webp", "jpg",
            "graphic", "novel", "digital", "hd", "sd", "mobile", "webrip", "web", "scan", "scans", "c2c"
        };

        public static ParsedComicRelease Parse(string releaseTitle, IEnumerable<string> knownTitles = null)
        {
            var result = new ParsedComicRelease { OriginalTitle = releaseTitle };
            if (string.IsNullOrWhiteSpace(releaseTitle))
            {
                return result;
            }

            var text = releaseTitle.Trim();
            text = NoiseSuffix.Replace(text, string.Empty).Trim();

            var extension = ExtensionSuffix.Match(text);
            if (extension.Success)
            {
                result.Format = ComicFormats.FromExtension(extension.Groups[1].Value);
                text = text.Substring(0, extension.Index);
            }

            text = NoiseSuffix.Replace(text, string.Empty).Trim();

            // Dots stand for spaces in usenet names ("Jujutsu.Kaisen.211"); keep real spaced names.
            var dotted = text.Count(c => c == '.') > text.Count(c => c == ' ');
            if (dotted)
            {
                text = Regex.Replace(text, @"(?<=\w)'(?=s\b)", "'");
                text = text.Replace('.', ' ').Replace('_', ' ');
            }

            text = text.Replace("×", " x ");

            var tags = new List<string>();
            text = BracketTag.Replace(text, m =>
            {
                tags.Add(m.Groups[1].Value.Trim());
                return " ";
            });

            // Scene names end in -GROUP ("eBook-BitBook").
            var sceneGroup = SceneGroup.Match(text.TrimEnd());
            if (sceneGroup.Success && dotted)
            {
                result.Group = sceneGroup.Groups["group"].Value;
                text = text.Substring(0, sceneGroup.Index);
            }

            text = Collapse(text);

            ReadTags(result, tags);

            // Words outside the brackets that are really tags (scene style).
            var words = text.Split(' ', StringSplitOptions.RemoveEmptyEntries).ToList();
            ReadInlineTags(result, words);
            text = string.Join(" ", words);

            text = StripPublisher(result, text);

            ReadEdition(result, text, tags);

            if (Regex.IsMatch(releaseTitle, @"\bannual\b", RegexOptions.IgnoreCase))
            {
                result.IsAnnual = true;
            }

            if (Regex.IsMatch(releaseTitle, @"[぀-ヿ一-鿿]") || Regex.IsMatch(releaseTitle, @"\(C\d{2,3}\)|COMIC1|doujin|hentai", RegexOptions.IgnoreCase))
            {
                result.IsAdultHint = true;
            }

            var rest = text;
            var matchedTitle = MatchKnownTitle(text, knownTitles, out var afterTitle);
            if (matchedTitle != null)
            {
                result.SeriesTitle = matchedTitle;
                result.MatchedKnownTitle = true;
                rest = afterTitle;
            }

            var numberMatch = FindNumber(rest, out var kind);
            if (numberMatch != null)
            {
                result.NumberKind = kind;
                result.Number = ParseDecimal(numberMatch.Groups["n"].Value);
                if (numberMatch.Groups["e"].Success)
                {
                    result.NumberEnd = ParseDecimal(numberMatch.Groups["e"].Value);
                }

                if (result.SeriesTitle == null)
                {
                    result.SeriesTitle = TrimTitle(rest.Substring(0, numberMatch.Index));
                }
                else
                {
                    result.ExtraTitleWords = TrimTitle(rest.Substring(0, numberMatch.Index));
                }

                var after = rest.Substring(numberMatch.Index + numberMatch.Length);
                result.VolumeTitle = TrimTitle(RemoveYears(after, result));
            }
            else if (result.SeriesTitle == null)
            {
                result.SeriesTitle = TrimTitle(RemoveYears(rest, result));
            }
            else
            {
                result.VolumeTitle = TrimTitle(RemoveYears(rest, result));
            }

            if (!result.Year.HasValue)
            {
                var year = YearInText.Match(rest);
                if (year.Success)
                {
                    result.Year = int.Parse(year.Groups["y"].Value, CultureInfo.InvariantCulture);
                }
            }

            if (string.IsNullOrWhiteSpace(result.VolumeTitle))
            {
                result.VolumeTitle = null;
            }

            return result;
        }

        // Lowercase, no accents, no punctuation, "&" as "and": how titles are compared.
        public static string NormalizeTitle(string title)
        {
            if (string.IsNullOrWhiteSpace(title))
            {
                return string.Empty;
            }

            var decomposed = title.Replace("×", " x ").Replace("&", " and ").Normalize(NormalizationForm.FormD);
            var builder = new StringBuilder();
            foreach (var c in decomposed)
            {
                if (CharUnicodeInfo.GetUnicodeCategory(c) == UnicodeCategory.NonSpacingMark || c == '\'' || c == '’')
                {
                    continue;
                }

                builder.Append(char.IsLetterOrDigit(c) ? char.ToLowerInvariant(c) : ' ');
            }

            var normalized = Collapse(builder.ToString());
            if (normalized.StartsWith("the ", StringComparison.Ordinal))
            {
                normalized = normalized.Substring(4);
            }

            return normalized;
        }

        private static Match FindNumber(string text, out ComicNumberKind kind)
        {
            var candidates = new List<(Match Match, ComicNumberKind Kind)>();

            AddFirst(candidates, Chapter.Match(text), ComicNumberKind.Chapter);
            AddFirst(candidates, Volume.Match(text), ComicNumberKind.Volume);
            AddFirst(candidates, Issue.Match(text), ComicNumberKind.Issue);

            foreach (Match bare in Bare.Matches(text))
            {
                var n = bare.Groups["n"].Value;
                if (n.Length == 4 && YearToken.IsMatch(n))
                {
                    continue;
                }

                candidates.Add((bare, ComicNumberKind.Bare));
                break;
            }

            if (candidates.Count == 0)
            {
                kind = ComicNumberKind.None;
                return null;
            }

            // The earliest number wins; at the same place a labelled one beats a bare one.
            var best = candidates.OrderBy(c => c.Match.Index).ThenBy(c => c.Kind == ComicNumberKind.Bare ? 1 : 0).First();
            kind = best.Kind;
            return best.Match;
        }

        private static void AddFirst(List<(Match, ComicNumberKind)> candidates, Match match, ComicNumberKind kind)
        {
            if (match.Success)
            {
                candidates.Add((match, kind));
            }
        }

        private static void ReadTags(ParsedComicRelease result, List<string> tags)
        {
            string lastGroupCandidate = null;

            foreach (var raw in tags)
            {
                var tag = raw.Replace('.', ' ').Trim();
                var lower = tag.ToLowerInvariant();

                if (lower.Length == 0)
                {
                    continue;
                }

                var year = YearToken.Match(tag);
                if (year.Success)
                {
                    result.Year ??= int.Parse(year.Groups["y"].Value, CultureInfo.InvariantCulture);
                    continue;
                }

                if (lower == "f" || Regex.IsMatch(lower, @"^f\d$"))
                {
                    result.IsFixed = true;
                    continue;
                }

                if (lower.Contains("c2c"))
                {
                    result.Source = ComicSources.C2C;
                    lastGroupCandidate = GroupAfterDash(tag) ?? lastGroupCandidate;
                    continue;
                }

                if (lower.StartsWith("digital", StringComparison.Ordinal) || lower == "webrip" || lower == "web" || lower == "retail")
                {
                    result.Source = ComicSources.Digital;

                    // (digital-Empire): the group rides along.
                    var group = GroupAfterDash(tag);
                    if (group != null && !SceneWords.Contains(group))
                    {
                        lastGroupCandidate = group;
                    }

                    continue;
                }

                if (Regex.IsMatch(lower, @"\bscanlations?\b") || Regex.IsMatch(lower, @"\b(scans|translations?|tl)$") && !lower.StartsWith("scan", StringComparison.Ordinal))
                {
                    result.IsScanlation = true;
                    lastGroupCandidate = tag;
                    continue;
                }

                if (lower == "scan" || lower == "scans" || lower.StartsWith("scan ", StringComparison.Ordinal) || lower.StartsWith("scan-", StringComparison.Ordinal))
                {
                    if (result.Source != ComicSources.Digital)
                    {
                        result.Source = ComicSources.Scan;
                    }

                    continue;
                }

                if (Languages.TryGetValue(lower, out var language))
                {
                    result.Language = language;
                    continue;
                }

                if (lower == "webp" || lower == "jpg" || lower == "png" || lower.Contains("cover") || lower == "omnibus edition" || lower == "graphic novel" || lower == "series" || lower == "one-shot" || lower == "oneshot")
                {
                    continue;
                }

                if (Publishers.Any(p => string.Equals(p, tag, StringComparison.OrdinalIgnoreCase)))
                {
                    result.Publisher ??= tag;
                    continue;
                }

                if (lower == "mangadex")
                {
                    result.IsScanlation = true;
                    continue;
                }

                if (Regex.IsMatch(lower, @"^\d+(\s*covers?)?$"))
                {
                    continue;
                }

                lastGroupCandidate = raw.Trim();
            }

            result.Group ??= lastGroupCandidate;
        }

        private static void ReadInlineTags(ParsedComicRelease result, List<string> words)
        {
            for (var i = words.Count - 1; i >= 0; i--)
            {
                var word = words[i];
                var lower = word.ToLowerInvariant();

                if (Languages.TryGetValue(lower, out var language) && word.Any(char.IsUpper) && (word.All(c => !char.IsLetter(c) || char.IsUpper(c)) || i > 1))
                {
                    // FRENCH, German: only when it sits among the trailing tags, not inside a title.
                    if (i >= words.Count - 6)
                    {
                        result.Language = language;
                        words.RemoveAt(i);
                        continue;
                    }
                }

                if ((lower == "hybrid" || lower == "retail") && i > 0)
                {
                    result.Source = ComicSources.Digital;
                    words.RemoveAt(i);
                    continue;
                }

                if ((lower == "digital" || lower == "web" || lower == "webrip") && i > 0)
                {
                    result.Source = ComicSources.Digital;
                    words.RemoveAt(i);
                    continue;
                }

                if (lower == "c2c" && i > 0)
                {
                    result.Source = ComicSources.C2C;
                    words.RemoveAt(i);
                    continue;
                }

                if ((lower == "ebook" || lower == "comic" || lower == "internal" || lower == "cbz" || lower == "cbr" || lower == "proper" || lower == "repack") && i >= words.Count - 4 && i > 1)
                {
                    if (lower == "cbz" || lower == "cbr")
                    {
                        result.Format = result.Format == ComicFormats.Unknown ? ComicFormats.FromExtension(lower) : result.Format;
                    }

                    words.RemoveAt(i);
                }
            }

            // "Witch.Hat.Atelier.v13.2025.Digital.LuCaZ": a trailing group with no brackets.
            if (result.Group == null && words.Count > 2)
            {
                var last = words[words.Count - 1];
                var beforeLast = words[words.Count - 2];
                if (Regex.IsMatch(last, @"^[A-Za-z][A-Za-z0-9]{2,15}$") && !Regex.IsMatch(last, @"^(?:c|v|ch|vol|t)\d", RegexOptions.IgnoreCase) && YearToken.IsMatch(beforeLast))
                {
                    result.Group = last;
                    words.RemoveAt(words.Count - 1);
                }
            }
        }

        private static string StripPublisher(ParsedComicRelease result, string text)
        {
            foreach (var publisher in Publishers.OrderByDescending(p => p.Length))
            {
                var match = Regex.Match(text, "^" + Regex.Escape(publisher) + @"\s*(?:-\s+)?", RegexOptions.IgnoreCase);
                if (match.Success && text.Length > match.Length + 2)
                {
                    result.Publisher ??= publisher;
                    return text.Substring(match.Length).Trim();
                }
            }

            return text;
        }

        private static void ReadEdition(ParsedComicRelease result, string text, List<string> tags)
        {
            var haystack = (text + " " + string.Join(" ", tags)).Replace('.', ' ').ToLowerInvariant();
            foreach (var (pattern, edition) in Editions)
            {
                if (Regex.IsMatch(haystack, @"(?<![a-z])" + Regex.Escape(pattern) + @"(?![a-z])"))
                {
                    result.Edition = edition;
                    return;
                }
            }
        }

        // When the release starts with one of the series' names, returns that name and the text
        // after it.
        private static string MatchKnownTitle(string text, IEnumerable<string> knownTitles, out string rest)
        {
            rest = text;
            if (knownTitles == null)
            {
                return null;
            }

            var rawTokens = text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            var normTokens = rawTokens.Select(t => NormalizeTitle(t)).ToArray();

            string best = null;
            var bestEnd = -1;
            var bestLength = 0;

            foreach (var title in knownTitles.Where(t => !string.IsNullOrWhiteSpace(t)))
            {
                var wanted = NormalizeTitle(title).Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (wanted.Length == 0)
                {
                    continue;
                }

                var w = 0;
                var i = 0;
                while (i < normTokens.Length && w < wanted.Length)
                {
                    var tokenParts = normTokens[i].Split(' ', StringSplitOptions.RemoveEmptyEntries);
                    if (tokenParts.Length == 0 || (i == 0 && tokenParts.Length == 1 && tokenParts[0] == "the" && wanted[0] != "the"))
                    {
                        i++;
                        continue;
                    }

                    var matchedAll = true;
                    foreach (var part in tokenParts)
                    {
                        if (w < wanted.Length && wanted[w] == part)
                        {
                            w++;
                        }
                        else
                        {
                            matchedAll = false;
                            break;
                        }
                    }

                    if (!matchedAll)
                    {
                        break;
                    }

                    i++;
                }

                if (w == wanted.Length && wanted.Length > bestLength)
                {
                    best = title;
                    bestEnd = i;
                    bestLength = wanted.Length;
                }
            }

            if (best == null)
            {
                return null;
            }

            rest = string.Join(" ", rawTokens.Skip(bestEnd));
            return best;
        }

        private static string RemoveYears(string text, ParsedComicRelease result)
        {
            var words = text.Split(' ', StringSplitOptions.RemoveEmptyEntries).ToList();
            for (var i = words.Count - 1; i >= 0; i--)
            {
                var year = YearToken.Match(words[i]);
                if (year.Success)
                {
                    result.Year ??= int.Parse(year.Groups["y"].Value, CultureInfo.InvariantCulture);
                    words.RemoveAt(i);
                }
            }

            return string.Join(" ", words);
        }

        private static string GroupAfterDash(string tag)
        {
            var dash = tag.IndexOf('-');
            if (dash < 0 || dash == tag.Length - 1)
            {
                return null;
            }

            var group = tag.Substring(dash + 1).Trim();
            return group.Length == 0 ? null : group;
        }

        private static string TrimTitle(string text)
        {
            var trimmed = Collapse(text).Trim(' ', '-', ':', ',', '_', '.', '–');
            return trimmed.Length == 0 ? null : trimmed;
        }

        private static string Collapse(string text)
        {
            return Regex.Replace(text ?? string.Empty, @"\s+", " ").Trim();
        }

        private static decimal? ParseDecimal(string value)
        {
            return decimal.TryParse(value, NumberStyles.Number, CultureInfo.InvariantCulture, out var number) ? number : null;
        }
    }
}
