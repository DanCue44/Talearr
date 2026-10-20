using System;
using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using NzbDrone.Core.Books;
using NzbDrone.Core.Talearr.Catalog;

namespace Chaptarr.Core.Test.Talearr.Catalog
{
    // Talearr: author page groups (docs/TALEARR_AUTHOR_PAGE.md).
    [TestFixture]
    public class TalearrAuthorPageGroupsFixture
    {
        private const string Orwell = "George Orwell";

        private static int _nextId;

        private static TalearrAuthorPageEntry Row(
            string title,
            bool kept = false,
            int votes = 0,
            string language = "eng",
            BookMediaType mediaType = BookMediaType.Ebook,
            bool isOmnibus = false,
            DateTime? released = null,
            List<string> genres = null,
            List<string> workIds = null)
        {
            return new TalearrAuthorPageEntry
            {
                Id = ++_nextId,
                Title = title,
                IsKept = kept,
                Votes = votes,
                Language = language,
                MediaType = mediaType,
                IsOmnibus = isOmnibus,
                ReleaseDate = released,
                Genres = genres ?? new List<string> { "Fiction", "Dystopia" },
                WorkIds = workIds ?? new List<string>()
            };
        }

        private static Dictionary<int, string> Classify(params TalearrAuthorPageEntry[] rows)
        {
            var preferred = TalearrAuthorPageClassifier.ResolvePreferredLanguages(null, rows);
            return TalearrAuthorPageClassifier.Classify(rows, Orwell, preferred);
        }

        [TestCase("York Notes on Nineteen Eighty-Four", "guide")]
        [TestCase("SparkNotes: 1984", "guide")]
        [TestCase("1984 (CliffsNotes)", "guide")]
        [TestCase("Study Guide: George Orwell's 1984", "guide")]
        [TestCase("Summary of 1984 by George Orwell", "guide")]
        [TestCase("An Analysis of George Orwell's Animal Farm", "guide")]
        [TestCase("Brodie's Notes on George Orwell's Animal Farm", "guide")]
        [TestCase("Level 4: 1984 KPF with Integrated Audio", "guide")]
        [TestCase("Animal Farm (Penguin Readers Level 3)", "guide")]
        [TestCase("1984 / Animal Farm", "omnibus")]
        [TestCase("The Complete Works of George Orwell", "omnibus")]
        [TestCase("Collected Works of George Orwell", "omnibus")]
        [TestCase("George Orwell Collection", "omnibus")]
        [TestCase("George Orwell 3 Books Set", "omnibus")]
        [TestCase("George Orwell Box Set: Animal Farm and 1984", "omnibus")]
        [TestCase("Six Novels", "omnibus")]
        [TestCase("The Wheel of Time Volumes 1-3", "omnibus")]
        [TestCase("Nineteen Eighty-Four: The Graphic Novel", "adaptation")]
        [TestCase("Animal Farm: The Official Graphic Novel", "adaptation")]
        [TestCase("1984 (Manga Edition)", "adaptation")]
        [TestCase("Classics Illustrated: Animal Farm", "adaptation")]
        public void title_rules(string title, string expected)
        {
            Assert.That(TalearrAuthorPageClassifier.ClassifyByTitle(title, false), Is.EqualTo(expected));
        }

        [TestCase("1984")]
        [TestCase("Nineteen Eighty-Four")]
        [TestCase("Animal Farm")]
        [TestCase("Notes on Nationalism")]
        [TestCase("Down and Out in Paris and London")]
        [TestCase("Animal Farm (Illustrated)")]
        [TestCase("Homage to Catalonia")]
        public void plain_titles_have_no_title_rule(string title)
        {
            Assert.That(TalearrAuthorPageClassifier.ClassifyByTitle(title, false), Is.Null);
        }

        [Test]
        public void omnibus_flag_is_omnibus()
        {
            Assert.That(TalearrAuthorPageClassifier.ClassifyByTitle("Orwell's England", true), Is.EqualTo("omnibus"));
        }

        [TestCase("George Orwell 1984", "1984")]
        [TestCase("George Orwell's 1984", "1984")]
        [TestCase("1984: A Novel", "1984")]
        [TestCase("1984 (Penguin Modern Classics)", "1984")]
        [TestCase("1984 (Resimli Yeni Çeviri)", "1984")]
        [TestCase("The Road to Wigan Pier", "road to wigan pier")]
        [TestCase("Animal Farm: 75th Anniversary Edition", "animal farm")]
        [TestCase("Animal Farm (Illustrated)", "animal farm")]
        public void normalize_title(string title, string expected)
        {
            Assert.That(TalearrAuthorPageClassifier.NormalizeTitle(title, Orwell), Is.EqualTo(expected));
        }

        [Test]
        public void orwell_1984_rows_leave_one_main_work()
        {
            var main = Row("1984", votes: 4_500_000);
            var plainLessVotes = Row("1984", votes: 120);
            var plainNoVotes = Row("1984");
            var authorPrefix = Row("George Orwell 1984", votes: 30);
            var omnibus = Row("1984 / Animal Farm", votes: 9000);
            var graphic = Row("Nineteen Eighty-Four: The Graphic Novel", votes: 800);
            var yorkNotes = Row("York Notes on Nineteen Eighty-Four", votes: 50);
            var turkish = Row("1984 (Resimli Yeni Çeviri) - Klasik", votes: 400, language: "tur");
            var reader = Row("Level 4: 1984 KPF with Integrated Audio", votes: 3);
            var farm = Row("Animal Farm", votes: 3_000_000);

            var groups = Classify(main, plainLessVotes, plainNoVotes, authorPrefix, omnibus, graphic, yorkNotes, turkish, reader, farm);

            Assert.Multiple(() =>
            {
                Assert.That(groups[main.Id], Is.EqualTo(TalearrAuthorPageGroup.Main));
                Assert.That(groups[farm.Id], Is.EqualTo(TalearrAuthorPageGroup.Main));
                Assert.That(groups[plainLessVotes.Id], Is.EqualTo(TalearrAuthorPageGroup.Duplicate));
                Assert.That(groups[plainNoVotes.Id], Is.EqualTo(TalearrAuthorPageGroup.Duplicate));
                Assert.That(groups[authorPrefix.Id], Is.EqualTo(TalearrAuthorPageGroup.Duplicate));
                Assert.That(groups[omnibus.Id], Is.EqualTo(TalearrAuthorPageGroup.Omnibus));
                Assert.That(groups[graphic.Id], Is.EqualTo(TalearrAuthorPageGroup.Adaptation));
                Assert.That(groups[yorkNotes.Id], Is.EqualTo(TalearrAuthorPageGroup.Guide));
                Assert.That(groups[turkish.Id], Is.EqualTo(TalearrAuthorPageGroup.OtherLanguage));
                Assert.That(groups[reader.Id], Is.EqualTo(TalearrAuthorPageGroup.Guide));
                Assert.That(groups.Values.Count(group => group == TalearrAuthorPageGroup.Main), Is.EqualTo(2));
            });
        }

        [Test]
        public void kept_row_is_the_representative_even_with_fewer_votes()
        {
            var popular = Row("1984", votes: 4_500_000);
            var monitored = Row("1984", kept: true, votes: 10);

            var groups = Classify(popular, monitored);

            Assert.That(groups[monitored.Id], Is.EqualTo(TalearrAuthorPageGroup.Main));
            Assert.That(groups[popular.Id], Is.EqualTo(TalearrAuthorPageGroup.Duplicate));
        }

        [Test]
        public void kept_rows_are_always_main()
        {
            var guide = Row("York Notes on Nineteen Eighty-Four", kept: true);
            var omnibus = Row("1984 / Animal Farm", kept: true, isOmnibus: true);
            var foreign = Row("1984 (Resimli Yeni Çeviri)", kept: true, language: "tur");
            var first = Row("1984", kept: true, votes: 5);
            var second = Row("1984", kept: true, votes: 50);

            var groups = Classify(guide, omnibus, foreign, first, second);

            Assert.That(groups.Values, Is.All.EqualTo(TalearrAuthorPageGroup.Main));
        }

        [Test]
        public void earlier_release_breaks_a_votes_tie()
        {
            var later = Row("Burmese Days", released: new DateTime(2003, 1, 1));
            var earlier = Row("Burmese Days", released: new DateTime(1934, 10, 25));

            var groups = Classify(later, earlier);

            Assert.That(groups[earlier.Id], Is.EqualTo(TalearrAuthorPageGroup.Main));
            Assert.That(groups[later.Id], Is.EqualTo(TalearrAuthorPageGroup.Duplicate));
        }

        [Test]
        public void media_types_are_grouped_separately()
        {
            var ebook = Row("1984", votes: 100, mediaType: BookMediaType.Ebook);
            var audiobook = Row("1984", votes: 10, mediaType: BookMediaType.Audiobook);

            var groups = Classify(ebook, audiobook);

            Assert.That(groups[ebook.Id], Is.EqualTo(TalearrAuthorPageGroup.Main));
            Assert.That(groups[audiobook.Id], Is.EqualTo(TalearrAuthorPageGroup.Main));
        }

        [Test]
        public void shared_work_id_links_rows_with_different_titles()
        {
            var english = Row("Nineteen Eighty-Four", votes: 1000, workIds: new List<string> { "gr:5470" });
            var other = Row("1984", votes: 10, workIds: new List<string> { "gr:5470" });

            var groups = Classify(english, other);

            Assert.That(groups[english.Id], Is.EqualTo(TalearrAuthorPageGroup.Main));
            Assert.That(groups[other.Id], Is.EqualTo(TalearrAuthorPageGroup.Duplicate));
        }

        [Test]
        public void foreign_row_without_a_same_book_row_stays_main()
        {
            var english = Row("Animal Farm", votes: 1000);
            var spanish = Row("Rebelion en la granja", votes: 50, language: "spa");

            var groups = Classify(english, spanish);

            Assert.That(groups[spanish.Id], Is.EqualTo(TalearrAuthorPageGroup.Main));
        }

        [Test]
        public void unknown_language_is_never_other_language()
        {
            var english = Row("1984", votes: 1000);
            var unknown = Row("1984 (Resimli Yeni Çeviri)", votes: 50, language: null);

            var groups = Classify(english, unknown);

            Assert.That(groups[unknown.Id], Is.EqualTo(TalearrAuthorPageGroup.Duplicate));
        }

        [Test]
        public void graphic_genre_is_an_adaptation_only_next_to_a_prose_row()
        {
            var prose = Row("Animal Farm", votes: 1000);
            var comic = Row("Animal Farm", votes: 5000, genres: new List<string> { "Graphic Novels", "Comics" });
            var comicOnly = Row("Watchmen", votes: 5000, genres: new List<string> { "Graphic Novels" });

            var groups = Classify(prose, comic, comicOnly);

            Assert.That(groups[prose.Id], Is.EqualTo(TalearrAuthorPageGroup.Main));
            Assert.That(groups[comic.Id], Is.EqualTo(TalearrAuthorPageGroup.Adaptation));
            Assert.That(groups[comicOnly.Id], Is.EqualTo(TalearrAuthorPageGroup.Main));
        }

        [Test]
        public void profile_languages_win_over_the_row_languages()
        {
            var german = Row("1984", votes: 10, language: "ger");
            var english = Row("1984", votes: 1000, language: "eng");

            var preferred = TalearrAuthorPageClassifier.ResolvePreferredLanguages(new[] { "ger" }, new[] { german, english });
            var groups = TalearrAuthorPageClassifier.Classify(new[] { german, english }, Orwell, preferred);

            Assert.That(groups[german.Id], Is.EqualTo(TalearrAuthorPageGroup.Main));
            Assert.That(groups[english.Id], Is.EqualTo(TalearrAuthorPageGroup.OtherLanguage));
        }

        [Test]
        public void preferred_languages_fall_back_to_kept_rows_then_the_most_common()
        {
            var kept = Row("A", kept: true, language: "fre");
            var english = Row("B", language: "eng");
            var english2 = Row("C", language: "eng");

            Assert.That(TalearrAuthorPageClassifier.ResolvePreferredLanguages(null, new[] { kept, english, english2 }), Is.EquivalentTo(new[] { "fre" }));
            Assert.That(TalearrAuthorPageClassifier.ResolvePreferredLanguages(null, new[] { english, english2, Row("D", language: "fre") }), Is.EquivalentTo(new[] { "eng" }));
            Assert.That(TalearrAuthorPageClassifier.ResolvePreferredLanguages(null, new[] { Row("E", language: null) }), Is.Empty);
        }

        [Test]
        public void is_kept_follows_the_tracked_catalog_rules()
        {
            Assert.Multiple(() =>
            {
                Assert.That(TalearrAuthorPageClassifier.IsKept(new Book { AnyEditionOk = true }, false), Is.False);
                Assert.That(TalearrAuthorPageClassifier.IsKept(new Book { AnyEditionOk = true }, true), Is.True);
                Assert.That(TalearrAuthorPageClassifier.IsKept(new Book { AnyEditionOk = true, EbookMonitored = true, MediaType = BookMediaType.Ebook }, false), Is.True);
                Assert.That(TalearrAuthorPageClassifier.IsKept(new Book { AnyEditionOk = true, AddOptions = new AddBookOptions { AddType = BookAddType.Manual } }, false), Is.True);
            });
        }

        [Test]
        public void empty_input_is_empty()
        {
            Assert.That(TalearrAuthorPageClassifier.Classify(null, Orwell, null), Is.Empty);
        }
    }
}
