using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;

namespace NzbDrone.Core.Talearr.MetadataProfiles
{
    // Talearr: Hardcover leaves the language empty on many translated editions, so a profile that
    // allows "eng, null" let in Der Lehrer, El intercambio and Cyrillic or Arabic titles. This
    // guesses from the title alone and only answers yes when it is obvious: another script, a
    // letter English does not use, or more common foreign words than English ones.
    // Anything unsure ("Kwijt", "Retratos") stays unknown and is kept.
    public static class TalearrTitleLanguageGuess
    {
        private static readonly HashSet<string> EnglishWords = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "the", "of", "and", "to", "for", "with", "my", "your", "how", "what", "from", "by", "his", "her",
            "our", "you", "who", "why", "when", "this", "that", "is", "an", "novel", "story", "stories",
            "book", "guide", "life", "true", "collection"
        };

        private static readonly HashSet<string> ForeignWords = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            // German, Dutch
            "der", "das", "und", "ein", "eine", "einer", "mit", "von", "vom", "zum", "zur", "auf", "het", "een", "alle",

            // Spanish, Portuguese, Italian, French
            "el", "los", "las", "del", "una", "uno", "un", "por", "para", "sobre", "con", "como", "de", "um", "uma",
            "dos", "que", "il", "di", "della", "dei", "degli", "gli", "la", "le", "les", "du", "des", "et", "au", "aux",

            // Scandinavian, Polish, Turkish
            "och", "og", "om", "nie", "jak", "ve", "bir",

            // "Love on the Brain: Nederlandse Editie", "Deutsche Ausgabe"
            "editie", "edicion", "edición", "edizione", "edição", "ausgabe", "wydanie", "baskı",
            "nederlandse", "deutsche", "española", "espanol", "español", "française", "francaise", "italiana"
        };

        // Common English title words. Only used for bare listings (no ISBN or ASIN), where a
        // title with none of these and none of EnglishWords is most likely a translation
        // somebody typed in ("Nauczyciele", "Idealny syn", "Menajera").
        private static readonly HashSet<string> CommonEnglishTitleWords = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "a", "i", "in", "on", "at", "it", "we", "me", "be", "no", "not", "all", "one", "two", "three", "last", "first",
            "love", "girl", "girls", "boy", "boys", "man", "men", "woman", "women", "wife", "husband", "mother", "father",
            "daughter", "son", "sister", "brother", "family", "friend", "friends", "house", "home", "room", "door", "night",
            "day", "days", "dark", "light", "dead", "death", "die", "kill", "killer", "killing", "murder", "murders", "crime",
            "crimes", "case", "cases", "secret", "secrets", "lie", "lies", "lost", "found", "little", "big", "new", "old",
            "time", "war", "world", "king", "queen", "prince", "princess", "blood", "heart", "hearts", "city", "town",
            "summer", "winter", "spring", "fall", "game", "games", "end", "never", "always", "after", "before", "under",
            "over", "into", "out", "up", "down", "back", "again", "here", "there", "where", "every", "nothing", "something",
            "good", "bad", "best", "perfect", "wrong", "right", "true", "real", "only", "other", "another", "us", "them",
            "he", "she", "they", "him", "girlfriend", "boyfriend", "wedding", "bride", "mate", "date", "deal", "play", "can",
            "do", "did", "dont", "know", "want", "need", "ever", "brain", "mind", "body", "doctor", "nurse",
            "teacher", "student", "school", "inmate", "prisoner", "guest", "stranger", "neighbor", "neighbors", "nanny",
            "housemaid", "maid", "tenant", "coworker", "boss", "intruder", "witch", "magic", "dragon", "fire", "ice",
            "sea", "sky", "star", "stars", "moon", "sun", "storm", "shadow", "shadows", "rose", "roses", "gold", "silver",
            "black", "white", "red", "blue", "green", "island", "road", "street", "train", "trip", "party", "dinner",
            "christmas", "holiday", "baby", "child", "children", "kids", "dog", "cat", "rules", "lessons", "lesson", "art",
            "history", "people", "power", "money", "free", "wild", "dream", "dreams", "beautiful", "happy", "dear", "med"
        };

        // Names and places that start with a foreign article but appear in English titles.
        private static readonly string[] EnglishPhrases =
        {
            "los angeles", "las vegas", "el paso", "la la land", "le mans", "des moines", "de niro"
        };

        // Accented letters English borrows (cafe, naive, Bronte, facade) are not enough on their own.
        private const string BorrowedAccents = "éèêëïôâàç";

        public static bool LooksNonEnglish(string title)
        {
            if (string.IsNullOrWhiteSpace(title))
            {
                return false;
            }

            if (title.Any(IsOtherScript))
            {
                return true;
            }

            var lowered = title.ToLowerInvariant();
            foreach (var phrase in EnglishPhrases)
            {
                lowered = lowered.Replace(phrase, " ");
            }

            // "To nie moje imię" has one English-looking word, but more of it is Polish.
            var words = Tokenize(lowered);
            var english = words.Count(EnglishWords.Contains);
            var foreign = words.Count(ForeignWords.Contains) + (title.Any(IsForeignLetter) ? 1 : 0);

            return foreign > english;
        }

        /// <summary>
        /// Talearr: a bare listing (no ISBN or ASIN) whose title has no common English word. Used
        /// only for editions without a language in English-only profiles.
        /// </summary>
        public static bool LooksLikeBareTranslation(string title, string isbn13, string isbn10, string asin)
        {
            if (string.IsNullOrWhiteSpace(title) ||
                !string.IsNullOrWhiteSpace(isbn13) || !string.IsNullOrWhiteSpace(isbn10) || !string.IsNullOrWhiteSpace(asin))
            {
                return false;
            }

            var words = Tokenize(title.ToLowerInvariant());
            return words.Count > 0 && !words.Any(w => EnglishWords.Contains(w) || CommonEnglishTitleWords.Contains(w));
        }

        private static List<string> Tokenize(string value)
        {
            var words = new List<string>();
            var current = new StringBuilder();

            foreach (var c in value)
            {
                if (char.IsLetter(c))
                {
                    current.Append(c);
                    continue;
                }

                if (current.Length > 0)
                {
                    words.Add(current.ToString());
                    current.Clear();
                }
            }

            if (current.Length > 0)
            {
                words.Add(current.ToString());
            }

            return words;
        }

        // A letter outside the Latin blocks (Cyrillic, Greek, Arabic, CJK, Bengali...) or a
        // non-ASCII digit such as the Arabic-Indic ones in "١٩٨٤".
        private static bool IsOtherScript(char c)
        {
            if (char.IsDigit(c))
            {
                return c > '9';
            }

            if (!char.IsLetter(c) || c < 0x0250)
            {
                return false;
            }

            // Latin Extended Additional covers Vietnamese, which IsForeignLetter handles.
            return c < 0x1E00 || c > 0x1EFF;
        }

        private static bool IsForeignLetter(char c)
        {
            if (c < 0x80 || !char.IsLetter(c) || BorrowedAccents.IndexOf(char.ToLowerInvariant(c)) >= 0)
            {
                return false;
            }

            if (c == 'ß' || c == 'ı' || c == 'ł' || c == 'ø' || c == 'æ' || c == 'đ')
            {
                return true;
            }

            var decomposed = c.ToString().Normalize(NormalizationForm.FormD);
            return decomposed.Length > 1 && decomposed.Skip(1).Any(m => CharUnicodeInfo.GetUnicodeCategory(m) == UnicodeCategory.NonSpacingMark);
        }
    }
}
