using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace NzbDrone.Core.MediaFiles.TalearrImport
{
    /// <summary>
    /// TaleArr: finds explicit language markers in a release or file name ("SWEDiSH", "Dutch",
    /// "[GER]"). Matching ignores case, so scene spellings like "DANiSH" count. Only full language
    /// names, native names and a few unambiguous scene codes are used; two-letter codes are never
    /// treated as evidence. Words that also appear in the book title, author or other protected
    /// names are skipped, so "The German Girl" is not read as German.
    /// </summary>
    public static class ReleaseNameLanguageDetector
    {
        private static readonly Regex TokenRegex = new Regex(@"[\p{L}\p{Nd}]+", RegexOptions.Compiled);

        private static readonly Dictionary<string, string> Tokens = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["english"] = "eng",
            ["eng"] = "eng",
            ["german"] = "deu",
            ["deutsch"] = "deu",
            ["ger"] = "deu",
            ["deu"] = "deu",
            ["french"] = "fra",
            ["francais"] = "fra",
            ["français"] = "fra",
            ["fre"] = "fra",
            ["fra"] = "fra",
            ["vostfr"] = "fra",
            ["spanish"] = "spa",
            ["espanol"] = "spa",
            ["español"] = "spa",
            ["castellano"] = "spa",
            ["latino"] = "spa",
            ["italian"] = "ita",
            ["italiano"] = "ita",
            ["ita"] = "ita",
            ["dutch"] = "nld",
            ["nederlands"] = "nld",
            ["flemish"] = "nld",
            ["vlaams"] = "nld",
            ["nld"] = "nld",
            ["dut"] = "nld",
            ["swedish"] = "swe",
            ["svenska"] = "swe",
            ["swe"] = "swe",
            ["danish"] = "dan",
            ["dansk"] = "dan",
            ["norwegian"] = "nor",
            ["norsk"] = "nor",
            ["finnish"] = "fin",
            ["suomi"] = "fin",
            ["icelandic"] = "isl",
            ["polski"] = "pol",
            ["russian"] = "rus",
            ["rus"] = "rus",
            ["portuguese"] = "por",
            ["portugues"] = "por",
            ["português"] = "por",
            ["brazilian"] = "por",
            ["japanese"] = "jpn",
            ["jpn"] = "jpn",
            ["chinese"] = "zho",
            ["mandarin"] = "zho",
            ["korean"] = "kor",
            ["hungarian"] = "hun",
            ["magyar"] = "hun",
            ["czech"] = "ces",
            ["cesky"] = "ces",
            ["česky"] = "ces",
            ["cze"] = "ces",
            ["slovak"] = "slk",
            ["turkish"] = "tur",
            ["greek"] = "ell",
            ["hebrew"] = "heb",
            ["arabic"] = "ara",
            ["hindi"] = "hin",
            ["romanian"] = "ron",
            ["catalan"] = "cat",
            ["ukrainian"] = "ukr",
            ["croatian"] = "hrv",
            ["serbian"] = "srp",
            ["slovenian"] = "slv",
            ["bulgarian"] = "bul",
            ["vietnamese"] = "vie",
            ["indonesian"] = "ind"
        };

        public static HashSet<string> Detect(IEnumerable<string> names, IEnumerable<string> protectedNames = null)
        {
            var protectedTokens = (protectedNames ?? Enumerable.Empty<string>())
                .Where(name => !string.IsNullOrWhiteSpace(name))
                .SelectMany(Tokenize)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            var detected = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var name in names ?? Enumerable.Empty<string>())
            {
                if (string.IsNullOrWhiteSpace(name))
                {
                    continue;
                }

                foreach (var token in Tokenize(name))
                {
                    if (protectedTokens.Contains(token))
                    {
                        continue;
                    }

                    if (Tokens.TryGetValue(token, out var code))
                    {
                        detected.Add(code);
                    }
                }
            }

            return detected;
        }

        private static IEnumerable<string> Tokenize(string value)
        {
            return TokenRegex.Matches(value ?? string.Empty)
                .Select(match => match.Value)
                .Where(token => token.Length > 0);
        }
    }
}
