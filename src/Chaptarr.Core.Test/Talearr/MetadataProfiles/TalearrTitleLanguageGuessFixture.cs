using System.Collections.Generic;
using NUnit.Framework;
using NzbDrone.Core.Books;
using NzbDrone.Core.Books.Services;
using NzbDrone.Core.Talearr.MetadataProfiles;

namespace Chaptarr.Core.Test.Talearr.MetadataProfiles
{
    [TestFixture]
    public class TalearrTitleLanguageGuessFixture
    {
        [TestCase("Der Lehrer")]
        [TestCase("Love on the Brain: Nederlandse Editie")]
        [TestCase("El intercambio")]
        [TestCase("Die verschlossene Tür")]
        [TestCase("To nie moje imię")]
        [TestCase("Steve Jobs: En biografi om manden bag Apple")]
        [TestCase("La fattoria degli animali; Il graphic novel")]
        [TestCase("Máu lạnh")]
        [TestCase("İtiraf")]
        [TestCase("冷血")]
        [TestCase("Илон Мъск")]
        [TestCase("Μουσική για χαμαιλέοντες")]
        [TestCase("محامي الشوارع")]
        [TestCase("١٩٨٤")]
        public void should_spot_titles_that_are_plainly_not_english(string title)
        {
            Assert.That(TalearrTitleLanguageGuess.LooksNonEnglish(title), Is.True);
        }

        [TestCase("1984")]
        [TestCase("Kwijt")]
        [TestCase("Retratos")]
        [TestCase("Unbound")]
        [TestCase("Wes Bennett's Vivid Dream")]
        [TestCase("Murders in Los Angeles")]
        [TestCase("Gödel, Escher, Bach: An Eternal Golden Braid")]
        [TestCase("Café Society")]
        [TestCase("The Dinner Party")]
        [TestCase("")]
        [TestCase(null)]
        public void should_keep_english_or_unsure_titles(string title)
        {
            Assert.That(TalearrTitleLanguageGuess.LooksNonEnglish(title), Is.False);
        }

        [Test]
        public void unknown_language_editions_with_foreign_titles_fail_english_only_profiles()
        {
            var englishOnly = new HashSet<string> { "eng" };
            var foreign = new Edition { Title = "Der Lehrer" };
            var english = new Edition { Title = "The Teacher" };

            Assert.That(EditionMetadataProfileFilter.IsAllowedLanguage(foreign, englishOnly, true, true), Is.False);
            Assert.That(EditionMetadataProfileFilter.IsAllowedLanguage(english, englishOnly, true, true), Is.True);
        }

        [Test]
        public void the_guess_is_not_used_when_other_languages_are_allowed()
        {
            var englishAndGerman = new HashSet<string> { "eng", "ger" };
            var unknownOnly = new HashSet<string>();
            var foreign = new Edition { Title = "Der Lehrer" };

            Assert.That(EditionMetadataProfileFilter.IsAllowedLanguage(foreign, englishAndGerman, true, true), Is.True);
            Assert.That(EditionMetadataProfileFilter.IsAllowedLanguage(foreign, unknownOnly, true, true), Is.True);
        }
    }
}
