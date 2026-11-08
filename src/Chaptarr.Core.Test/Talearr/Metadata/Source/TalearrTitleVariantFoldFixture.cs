using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using NzbDrone.Core.Books;
using NzbDrone.Core.Books.Services;
using NzbDrone.Core.Parser;
using NzbDrone.Core.Talearr.Metadata.Source;
using NzbDrone.Core.Talearr.MetadataProfiles;

namespace Chaptarr.Core.Test.Talearr.Metadata.Source
{
    [TestFixture]
    public class TalearrTitleVariantFoldFixture
    {
        private static HardcoverBookRecord Book(int id, string title, int readers, params int[] editionIds)
        {
            return new HardcoverBookRecord
            {
                Id = id,
                Title = title,
                UsersCount = readers,
                Editions = editionIds.Select(e => new HardcoverEditionRecord { Id = e, Title = title }).ToList()
            };
        }

        [TestCase("Bound (Unabridged)", "Bound")]
        [TestCase("The Housemaid (Unabridged Audiobook)", "The Housemaid")]
        [TestCase("Skyshade (Audible Australia)", "Skyshade")]
        [TestCase("Grim (Dueling Crowns Edition)", "Grim")]
        [TestCase("First (Kickstarter edition)", "First")]
        [TestCase("Bad in Bed (Original_recording)", "Bad in Bed")]
        [TestCase("Nineteen Eighty-Four (MP3 Book)", "Nineteen Eighty-Four")]
        [TestCase("Blackshirts and Reds: Rational Fascism and the Overthrow of Communism (05:29 hh:mm)", "Blackshirts and Reds: Rational Fascism and the Overthrow of Communism")]
        [TestCase("Animal Farm (Veritas Book 1)", "Animal Farm (Veritas Book 1)")]
        [TestCase("Lightlark, Book 1", "Lightlark, Book 1")]
        public void display_title_drops_format_labels(string title, string expected)
        {
            Assert.That(TalearrTitleNormalizer.CleanDisplayTitle(title), Is.EqualTo(expected));
        }

        [TestCase("Summer in the City", "Summer in the City: A Novel", true)]
        [TestCase("Death Row", "Death Row: A Novella", true)]
        [TestCase("Bound", "Bound (Unabridged)", true)]
        [TestCase("Bound", "Unbound (Unabridged)", false)]
        [TestCase("Bound", "Bound and Unbound", false)]
        [TestCase("The Housemaid", "The Housemaid's Secret (Unabridged)", false)]
        [TestCase("Animal Farm", "Animal Farm: The Graphic Novel", false)]
        public void variants_share_a_work_title(string left, string right, bool same)
        {
            Assert.That(TalearrTitleNormalizer.NormalizeFull(left) == TalearrTitleNormalizer.NormalizeFull(right), Is.EqualTo(same));
        }

        [Test]
        public void variants_fold_into_the_most_read_book()
        {
            var aliases = new Dictionary<int, List<int>>();
            var folded = TalearrHardcoverMapper.FoldTitleVariants(new[]
            {
                Book(1, "Bound (Unabridged)", 3, 11),
                Book(2, "Bound", 40, 21, 22),
                Book(3, "Unbound (Unabridged)", 2, 31),
                Book(4, "Bound and Unbound", 16, 41)
            }, aliases);

            Assert.That(folded.Select(b => b.Id), Is.EqualTo(new[] { 2, 3, 4 }));

            var bound = folded.Single(b => b.Id == 2);
            Assert.That(bound.Title, Is.EqualTo("Bound"));
            Assert.That(bound.Editions.Select(e => e.Id), Is.EquivalentTo(new[] { 11, 21, 22 }));
            Assert.That(bound.UsersCount, Is.EqualTo(43));
            Assert.That(aliases[2], Is.EqualTo(new[] { 1 }));
        }

        [Test]
        public void folding_does_not_change_the_cached_records()
        {
            var primary = Book(2, "Moderation", 10, 21);
            var other = Book(1, "Moderation: A Novel", 1, 11);

            TalearrHardcoverMapper.FoldTitleVariants(new[] { primary, other }, new Dictionary<int, List<int>>());

            Assert.That(primary.Editions.Select(e => e.Id), Is.EqualTo(new[] { 21 }));
            Assert.That(primary.UsersCount, Is.EqualTo(10));
        }

        private static HardcoverBookRecord InSeries(HardcoverBookRecord book, int seriesId, decimal? position)
        {
            book.Series = new List<HardcoverBookSeriesRecord> { new HardcoverBookSeriesRecord { SeriesId = seriesId, Position = position } };
            return book;
        }

        [Test]
        public void a_series_subtitle_in_the_same_place_folds_into_the_bare_title()
        {
            // Hardcover lists Matthew J. Barbeler's first Crematoria Online book twice (issue #5).
            var aliases = new Dictionary<int, List<int>>();
            var folded = TalearrHardcoverMapper.FoldTitleVariants(new[]
            {
                InSeries(Book(2327493, "Rise of the Crimson Order", 2, 11), 7, 1),
                InSeries(Book(2480401, "Rise of the Crimson Order: A Crematoria Online LitRPG Novel", 2, 21), 7, 1),
                InSeries(Book(2683716, "Ashen Legacy", 1, 31), 7, 2)
            }, aliases);

            Assert.Multiple(() =>
            {
                Assert.That(folded.Select(b => b.Id), Is.EqualTo(new[] { 2327493, 2683716 }));
                Assert.That(folded[0].Title, Is.EqualTo("Rise of the Crimson Order"));
                Assert.That(folded[0].Editions.Select(e => e.Id), Is.EquivalentTo(new[] { 11, 21 }));
                Assert.That(aliases[2327493], Is.EqualTo(new[] { 2480401 }));
            });
        }

        [Test]
        public void a_subtitle_without_the_same_series_place_stays_its_own_book()
        {
            var folded = TalearrHardcoverMapper.FoldTitleVariants(new[]
            {
                InSeries(Book(1, "Animal Farm", 50, 11), 3, 1),
                Book(2, "Animal Farm: The Graphic Novel", 5, 21),
                InSeries(Book(3, "Dune", 50, 31), 4, 1),
                InSeries(Book(4, "Dune: House Atreides", 10, 41), 4, 0.5m)
            }, new Dictionary<int, List<int>>());

            Assert.That(folded.Select(b => b.Id), Is.EqualTo(new[] { 1, 2, 3, 4 }));
        }

        [Test]
        public void compilations_are_not_folded()
        {
            var set = Book(1, "Lightlark", 5, 11);
            set.Compilation = true;

            var folded = TalearrHardcoverMapper.FoldTitleVariants(new[] { set, Book(2, "Lightlark", 50, 21) }, new Dictionary<int, List<int>>());

            Assert.That(folded.Count, Is.EqualTo(2));
        }

        [TestCase("Nauczyciele", true)]
        [TestCase("Idealny syn", true)]
        [TestCase("Menajera", true)]
        [TestCase("Kusursuz Çocuk", true)]
        [TestCase("The Housemaid", false)]
        [TestCase("Dead Med", false)]
        [TestCase("Bride", false)]
        [TestCase("Love on the Brain", false)]
        public void bare_listing_translation_guess(string title, bool expected)
        {
            Assert.That(TalearrTitleLanguageGuess.LooksLikeBareTranslation(title, null, null, null, 1), Is.EqualTo(expected));
        }

        [Test]
        public void a_listing_with_an_isbn_is_never_a_bare_translation()
        {
            Assert.That(TalearrTitleLanguageGuess.LooksLikeBareTranslation("Nauczyciele", "9788383611234", null, null, 1), Is.False);
        }

        [Test]
        public void a_bare_listing_many_people_read_is_kept()
        {
            Assert.That(TalearrTitleLanguageGuess.LooksLikeBareTranslation("Grim", null, null, null, 27), Is.False);
        }

        [Test]
        public void dutch_edition_label_reads_as_foreign()
        {
            Assert.That(TalearrTitleLanguageGuess.LooksNonEnglish("Love on the Brain: Nederlandse Editie"), Is.True);
        }

        [Test]
        public void english_only_profile_drops_a_bare_translation_but_not_with_an_isbn()
        {
            var english = new HashSet<string> { "eng" };

            Assert.That(EditionMetadataProfileFilter.IsAllowedLanguage(new Edition { Title = "Idealny syn" }, english, true, true), Is.False);
            Assert.That(EditionMetadataProfileFilter.IsAllowedLanguage(new Edition { Title = "Idealny syn", Isbn13 = "9788383611234" }, english, true, true), Is.True);
            Assert.That(EditionMetadataProfileFilter.IsAllowedLanguage(new Edition { Title = "Idealny syn" }, new HashSet<string> { "eng", "pol" }, true, true), Is.True);
        }
    }
}
