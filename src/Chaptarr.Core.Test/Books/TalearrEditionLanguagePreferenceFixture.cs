using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using NLog;
using NUnit.Framework;
using NzbDrone.Core.Books;
using NzbDrone.Core.Books.Services;
using NzbDrone.Core.Configuration;
using NzbDrone.Core.Languages;

namespace Chaptarr.Core.Test.Books
{
    // Talearr: edition choice follows the preferred language and the work title, not popularity.
    // Edition data mirrors real catalog rows from the Talearr test server (book ids in comments).
    [TestFixture]
    public class TalearrEditionLanguagePreferenceFixture
    {
        public class UiLanguageConfigProxy : DispatchProxy
        {
            public int UiLanguage { get; set; } = (int)Language.English;

            protected override object Invoke(MethodInfo targetMethod, object[] args)
            {
                if (targetMethod?.Name == "get_UILanguage")
                {
                    return UiLanguage;
                }

                throw new NotImplementedException(targetMethod?.Name);
            }
        }

        private static EditionSelector CreateSut(Language uiLanguage = null)
        {
            if (uiLanguage == null)
            {
                return new EditionSelector(LogManager.GetCurrentClassLogger());
            }

            var config = DispatchProxy.Create<IConfigService, UiLanguageConfigProxy>();
            ((UiLanguageConfigProxy)(object)config).UiLanguage = uiLanguage.Id;
            return new EditionSelector(LogManager.GetCurrentClassLogger(), config);
        }

        private static Edition E(int id, string title, string language, int readingFormat, int votes, bool monitored = false)
        {
            return new Edition
            {
                Id = id,
                BookId = 1,
                Title = title,
                Language = language,
                ReadingFormatId = readingFormat,
                Monitored = monitored,
                ForeignEditionId = $"test:{id}",
                Ratings = new Ratings { Votes = votes, Value = 4.0m }
            };
        }

        // Book 3361 "The Pelican Brief" audiobook: the German translation has more votes.
        private static List<Edition> PelicanBriefAudio(bool germanMonitored = false)
        {
            return new List<Edition>
            {
                E(26882, "The Pelican Brief", "eng", 3, 12606),
                E(26888, "Die Akte", "deu", 2, 861, germanMonitored),
                E(26884, "El informe pelícano [The Pelican Brief]", "spa", 2, 603),
                E(26861, "The Pelican Brief", "eng", 2, 377),
                E(26862, "The Pelican Brief", "eng", 2, 329)
            };
        }

        [Test]
        public void should_pick_english_native_edition_over_more_popular_translation()
        {
            var selected = CreateSut().SelectBestEdition(PelicanBriefAudio(), BookMediaType.Audiobook);

            Assert.That(selected.Id, Is.EqualTo(26861));
        }

        [Test]
        public void should_follow_the_ui_language_when_it_is_not_english()
        {
            var selected = CreateSut(Language.German).SelectBestEdition(PelicanBriefAudio(), BookMediaType.Audiobook);

            Assert.That(selected.Id, Is.EqualTo(26888));
        }

        [Test]
        public void should_prefer_unlabelled_edition_titled_like_the_work_over_a_labelled_translation()
        {
            // Book 2 "In Cold Blood": a Dutch audiobook must not win over an unlabelled one with the English title.
            var editions = new List<Edition>
            {
                E(101, "In koelen bloede: Het ware verhaal van een meervoudige moord", "nld", 2, 50),
                E(91, "In Cold Blood (Audio CD)", "und", 2, 12),
                E(97, "Kaltblütig (Audio CD)", "deu", 2, 40),
                E(24181, "In Cold Blood", "eng", 3, 26092)
            };

            var selected = CreateSut().SelectBestEdition(editions, BookMediaType.Audiobook);

            Assert.That(selected.Id, Is.EqualTo(91));
        }

        [Test]
        public void should_prefer_english_edition_over_unlabelled_one_with_more_votes()
        {
            // Book 7135 "How to Read Now": "und" (58 votes) vs "eng" (18 votes).
            var editions = new List<Edition>
            {
                E(42322, "How to Read Now", "und", 2, 58),
                E(42319, "How to Read Now", "eng", 2, 18),
                E(42320, "How To Read Now", "eng", 2, 4)
            };

            Assert.That(CreateSut().SelectBestEdition(editions, BookMediaType.Audiobook).Id, Is.EqualTo(42319));
        }

        [Test]
        public void should_pick_english_ebook_when_every_audiobook_edition_is_foreign()
        {
            // Book 3768 "Bound and Unbound": only German and Spanish audio is listed so far.
            var editions = new List<Edition>
            {
                E(30206, "Bound and Unbound: A paranormal novella duology", "eng", 3, 0),
                E(42984, "Bound & Unbound - Die Paranormal-Romance-Dilogie", "deu", 2, 0),
                E(42987, "Mortal & Inmortal: Dos romances paranormales", "spa", 2, 0),
                E(42983, "Enlace & Desenlace", "por", 3, 0)
            };

            Assert.That(CreateSut().SelectBestEdition(editions, BookMediaType.Audiobook).Id, Is.EqualTo(30206));
        }

        [Test]
        public void should_keep_unlabelled_native_edition_instead_of_switching_to_an_ebook()
        {
            var editions = new List<Edition>
            {
                E(1, "Some Audiobook", null, 2, 5),
                E(2, "Some Audiobook: The Novel", "eng", 3, 900)
            };

            Assert.That(CreateSut().SelectBestEdition(editions, BookMediaType.Audiobook).Id, Is.EqualTo(1));
        }

        [Test]
        public void should_monitor_exactly_one_edition_when_all_editions_are_new()
        {
            // All new editions have Id 0; upstream compared ids and monitored every one of them.
            var editions = new List<Edition>
            {
                E(0, "Animal Farm", "eng", 3, 100),
                E(0, "Animal Farm", "urd", 1, 5000),
                E(0, "Farm der Tiere", "deu", 3, 300)
            };

            CreateSut().EnsureSingleMonitoredEdition(editions, null, BookMediaType.Ebook);

            Assert.That(editions.Count(e => e.Monitored), Is.EqualTo(1));
            Assert.That(editions.Single(e => e.Monitored).Language, Is.EqualTo("eng"));
        }

        [Test]
        public void should_repair_many_monitored_editions_to_the_english_one()
        {
            // Book 3768 had seven monitored editions after a refresh, none of them English.
            var editions = new List<Edition>
            {
                E(30206, "Bound and Unbound: A paranormal novella duology", "eng", 3, 0),
                E(42983, "Enlace & Desenlace", "por", 3, 0, monitored: true),
                E(42984, "Bound & Unbound - Die Paranormal-Romance-Dilogie", "deu", 2, 0, monitored: true),
                E(42985, "Bound & Unbound - Die Paranormal-Romance-Dilogie", "deu", 2, 0, monitored: true),
                E(42986, "Bound & Unbound - Die Paranormal-Romance-Dilogie", "deu", 2, 0, monitored: true),
                E(42987, "Mortal & Inmortal: Dos romances paranormales", "spa", 2, 0, monitored: true),
                E(42988, "Mortal & Inmortal", "spa", 3, 0, monitored: true),
                E(42989, "Bound and Unbound", "und", 3, 0, monitored: true)
            };

            CreateSut().EnsureSingleMonitoredEdition(editions, null, BookMediaType.Audiobook);

            Assert.That(editions.Single(e => e.Monitored).Id, Is.EqualTo(30206));
        }

        [Test]
        public void should_release_an_auto_selected_foreign_edition_so_refresh_picks_english()
        {
            var sut = CreateSut();
            var editions = PelicanBriefAudio(germanMonitored: true);

            var released = sut.ReleaseForeignAutoSelectedEdition(editions, new Dictionary<int, int>(), BookMediaType.Audiobook);
            sut.EnsureSingleMonitoredEdition(editions, new Dictionary<int, int>(), BookMediaType.Audiobook);

            Assert.That(released, Is.True);
            Assert.That(editions.Single(e => e.Monitored).Id, Is.EqualTo(26861));
        }

        [Test]
        public void should_release_foreign_print_edition_for_ebook_row()
        {
            // Book 4096 "Animal Farm" ebook ended up monitoring an Urdu print edition.
            var sut = CreateSut();
            var editions = new List<Edition>
            {
                E(33946, "Animal Farm", "urd", 1, 0, monitored: true),
                E(32886, "Animal Farm", "eng", 3, 17447),
                E(33473, "A Revolução dos Bichos: Um clássico da Literatura Inglesa", "por", 3, 49610)
            };

            Assert.That(sut.ReleaseForeignAutoSelectedEdition(editions, null, BookMediaType.Ebook), Is.True);
            sut.EnsureSingleMonitoredEdition(editions, null, BookMediaType.Ebook);
            Assert.That(editions.Single(e => e.Monitored).Id, Is.EqualTo(32886));
        }

        [Test]
        public void should_not_release_a_manual_pick_an_edition_with_files_or_an_english_edition()
        {
            var sut = CreateSut();

            var manual = PelicanBriefAudio(germanMonitored: true);
            manual.Single(e => e.Id == 26888).ManualAdd = true;
            Assert.That(sut.ReleaseForeignAutoSelectedEdition(manual, null, BookMediaType.Audiobook), Is.False);

            var withFiles = PelicanBriefAudio(germanMonitored: true);
            Assert.That(sut.ReleaseForeignAutoSelectedEdition(withFiles, new Dictionary<int, int> { [26888] = 1 }, BookMediaType.Audiobook), Is.False);

            var english = PelicanBriefAudio();
            english.Single(e => e.Id == 26862).Monitored = true;
            Assert.That(sut.ReleaseForeignAutoSelectedEdition(english, null, BookMediaType.Audiobook), Is.False);
            Assert.That(english.Single(e => e.Monitored).Id, Is.EqualTo(26862));
        }

        [Test]
        public void should_not_trade_a_native_foreign_edition_for_an_english_ebook()
        {
            var editions = new List<Edition>
            {
                E(42984, "Bound & Unbound - Die Paranormal-Romance-Dilogie", "deu", 2, 0, monitored: true),
                E(30206, "Bound and Unbound: A paranormal novella duology", "eng", 3, 0)
            };

            Assert.That(CreateSut().ReleaseForeignAutoSelectedEdition(editions, null, BookMediaType.Audiobook), Is.False);
            Assert.That(editions.Single(e => e.Monitored).Id, Is.EqualTo(42984));
        }

        [TestCase(1, "eng")]
        [TestCase(4, "deu")]
        [TestCase(2, "fra")]
        [TestCase(9999, "eng")]
        public void should_resolve_ui_language_to_iso_code(int uiLanguage, string expected)
        {
            Assert.That(TalearrEditionPreference.ResolvePreferredLanguage(uiLanguage), Is.EqualTo(expected));
        }

        [TestCase("und", null)]
        [TestCase("", null)]
        [TestCase("en", "eng")]
        [TestCase("en-US", "eng")]
        [TestCase("nld", "nld")]
        public void should_normalize_edition_language(string raw, string expected)
        {
            Assert.That(TalearrEditionPreference.NormalizeLanguage(raw), Is.EqualTo(expected));
        }
    }
}
