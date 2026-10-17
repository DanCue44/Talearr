using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using NLog;
using NUnit.Framework;
using NzbDrone.Core.Books;
using NzbDrone.Core.Configuration;
using NzbDrone.Core.Talearr.ContentFilter;

namespace Chaptarr.Core.Test.Talearr.ContentFilter
{
    internal class ContentFilterConfigProxy : DispatchProxy
    {
        public Dictionary<string, string> Values { get; } = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        protected override object Invoke(MethodInfo targetMethod, object[] args)
        {
            switch (targetMethod?.Name)
            {
                case nameof(IConfigRepository.Get):
                    var key = (string)args[0];
                    return Values.TryGetValue(key, out var value) ? new Config { Key = key, Value = value } : null;
                case nameof(IConfigRepository.Upsert):
                    Values[(string)args[0]] = (string)args[1];
                    return new Config { Key = (string)args[0], Value = (string)args[1] };
                case "Delete":
                    if (args[0] is Config config)
                    {
                        Values.Remove(config.Key);
                    }

                    return null;
                default:
                    throw new NotImplementedException($"Test proxy does not implement IConfigRepository.{targetMethod?.Name}");
            }
        }
    }

    [TestFixture]
    public class TalearrContentFilterFixture
    {
        private static Book Book(string title, params string[] genres)
        {
            return new Book { Title = title, Genres = genres.ToList() };
        }

        [Test]
        public void inactive_filter_keeps_everything()
        {
            Assert.That(new TalearrContentFilter().IsActive, Is.False);
            Assert.That(TalearrContentFilterRules.Rejection(Book("Anything", "Comics"), new TalearrContentFilter()), Is.Null);
        }

        [Test]
        public void blocked_genres_skip_books()
        {
            var filter = new TalearrContentFilter { BlockedGenres = new List<string> { "romance" } };

            Assert.That(TalearrContentFilterRules.Rejection(Book("A", "Contemporary Romance"), filter), Is.Not.Null);
            Assert.That(TalearrContentFilterRules.Rejection(Book("B", "Thriller"), filter), Is.Null);
            Assert.That(TalearrContentFilterRules.Rejection(Book("C"), filter), Is.Null);
        }

        [Test]
        public void allowed_genres_need_one_match_but_keep_books_without_genres()
        {
            var filter = new TalearrContentFilter { AllowedGenres = new List<string> { "Fantasy", "Science fiction" } };

            Assert.That(TalearrContentFilterRules.Rejection(Book("A", "Urban Fantasy"), filter), Is.Null);
            Assert.That(TalearrContentFilterRules.Rejection(Book("B", "Mystery", "Thriller"), filter), Is.EqualTo("no allowed genre"));
            Assert.That(TalearrContentFilterRules.Rejection(Book("C"), filter), Is.Null);
        }

        [Test]
        public void block_wins_over_allow()
        {
            var filter = new TalearrContentFilter
            {
                AllowedGenres = new List<string> { "Fantasy" },
                BlockedGenres = new List<string> { "Romantasy" }
            };

            Assert.That(TalearrContentFilterRules.Rejection(Book("A", "Fantasy", "Romantasy"), filter), Does.Contain("Romantasy"));
        }

        [TestCase("Short stories", true)]
        [TestCase("Anthologies & Short Stories", true)]
        [TestCase("Short Story Collection", true)]
        [TestCase("Fiction", false)]
        public void short_stories(string genre, bool skipped)
        {
            var filter = new TalearrContentFilter { SkipShortStories = true };

            Assert.That(TalearrContentFilterRules.Rejection(Book("A", genre), filter) != null, Is.EqualTo(skipped));
        }

        [TestCase("Comics", true)]
        [TestCase("Comics & Graphic Novels", true)]
        [TestCase("Graphic novels", true)]
        [TestCase("Manga", true)]
        [TestCase("Comic fantasy", false)]
        [TestCase("GraphicAudio", false)]
        public void graphic_novels(string genre, bool skipped)
        {
            var filter = new TalearrContentFilter { SkipGraphicNovels = true };

            Assert.That(TalearrContentFilterRules.Rejection(Book("A", genre), filter) != null, Is.EqualTo(skipped));
        }

        [Test]
        public void anthologies_by_genre_or_title_and_novellas_by_genre()
        {
            var anthologies = new TalearrContentFilter { SkipAnthologies = true };
            var novellas = new TalearrContentFilter { SkipNovellas = true };

            Assert.That(TalearrContentFilterRules.Rejection(Book("A", "Literary Collections"), anthologies), Is.EqualTo("anthology"));
            Assert.That(TalearrContentFilterRules.Rejection(Book("The Year's Best Anthology"), anthologies), Is.EqualTo("anthology"));
            Assert.That(TalearrContentFilterRules.Rejection(Book("A", "Novella"), novellas), Is.EqualTo("novella"));
            Assert.That(TalearrContentFilterRules.Rejection(Book("A", "Novels"), novellas), Is.Null);
        }

        [Test]
        public void genre_match_folds_case_accents_and_punctuation()
        {
            Assert.That(TalearrContentFilterRules.GenreMatches("Bande Dessinée", "bande dessinee"), Is.True);
            Assert.That(TalearrContentFilterRules.GenreMatches("Science-Fiction", "science fiction"), Is.True);
            Assert.That(TalearrContentFilterRules.GenreMatches("Comics", "comic"), Is.False);
        }

        [Test]
        public void same_rules_ignores_order_case_and_blanks()
        {
            var a = new TalearrContentFilter { BlockedGenres = new List<string> { "Romance", "Horror" } };
            var b = new TalearrContentFilter { BlockedGenres = new List<string> { "horror", " ", "romance" } };

            Assert.That(a.SameRulesAs(b), Is.True);
            Assert.That(a.SameRulesAs(new TalearrContentFilter()), Is.False);
        }

        [Test]
        public void store_round_trips_in_a_talearr_config_row_and_clears_when_inactive()
        {
            var repository = DispatchProxy.Create<IConfigRepository, ContentFilterConfigProxy>();
            var proxy = (ContentFilterConfigProxy)(object)repository;
            var store = new TalearrContentFilterStore(repository, LogManager.GetLogger("TalearrTest"));

            store.Save(4, new TalearrContentFilter { SkipGraphicNovels = true, BlockedGenres = new List<string> { " Romance " } });

            Assert.That(proxy.Values.Keys, Is.EquivalentTo(new[] { "talearr.metadataprofile.4" }));
            var read = store.Get(4);
            Assert.That(read.SkipGraphicNovels, Is.True);
            Assert.That(read.BlockedGenres, Is.EqualTo(new[] { "Romance" }));
            Assert.That(store.Get(5).IsActive, Is.False);

            store.Save(4, new TalearrContentFilter());
            Assert.That(proxy.Values, Is.Empty);
        }

        [Test]
        public void unreadable_row_reads_as_no_rules()
        {
            var repository = DispatchProxy.Create<IConfigRepository, ContentFilterConfigProxy>();
            ((ContentFilterConfigProxy)(object)repository).Values["talearr.metadataprofile.2"] = "{oops";

            Assert.That(new TalearrContentFilterStore(repository, LogManager.GetLogger("TalearrTest")).Get(2).IsActive, Is.False);
        }
    }
}
