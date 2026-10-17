using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using Chaptarr.Core.Test.Talearr.Recordings;
using Dapper;
using NLog;
using NUnit.Framework;
using NzbDrone.Core.Books;
using NzbDrone.Core.Books.Events;
using NzbDrone.Core.Tags;
using NzbDrone.Core.Talearr.Recordings;
using NzbDrone.Core.Talearr.Tags;

namespace Chaptarr.Core.Test.Talearr.Tags
{
    internal class TagServiceProxy : DispatchProxy
    {
        public List<Tag> Tags { get; } = new List<Tag>();

        protected override object Invoke(MethodInfo targetMethod, object[] args)
        {
            switch (targetMethod?.Name)
            {
                case nameof(ITagService.All):
                    return Tags.ToList();
                default:
                    throw new NotImplementedException($"Test proxy does not implement ITagService.{targetMethod?.Name}");
            }
        }

        public static (ITagService Service, TagServiceProxy Proxy) Create(params int[] ids)
        {
            var service = DispatchProxy.Create<ITagService, TagServiceProxy>();
            var proxy = (TagServiceProxy)(object)service;
            proxy.Tags.AddRange(ids.Select(id => new Tag { Id = id, Label = "tag" + id }));
            return (service, proxy);
        }
    }

    [TestFixture]
    public class TalearrBookTagsFixture
    {
        private TalearrTestFolder _folder;
        private TalearrDatabase _database;
        private TalearrBookTagStore _store;
        private TagServiceProxy _tags;
        private TalearrBookTagService _service;

        [SetUp]
        public void SetUp()
        {
            _folder = new TalearrTestFolder();
            _database = _folder.CreateDatabase();
            _store = new TalearrBookTagStore(_database, LogManager.GetLogger("TalearrTest"));
            var (tagService, proxy) = TagServiceProxy.Create(1, 2, 3);
            _tags = proxy;
            _service = new TalearrBookTagService(_store, tagService, LogManager.GetLogger("TalearrTest"));
        }

        [TearDown]
        public void TearDown()
        {
            _folder.Dispose();
        }

        [Test]
        public void talearr_db_schema_four_adds_the_book_tags_table()
        {
            using var connection = _database.OpenConnection();

            Assert.That(TalearrDatabase.CurrentSchemaVersion, Is.GreaterThanOrEqualTo(4));
            Assert.That(connection.ExecuteScalar<int>("SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name = 'BookTags'"), Is.EqualTo(1));
        }

        [Test]
        public void apply_adds_removes_and_replaces()
        {
            _service.Apply(new[] { 10, 11 }, new[] { 1, 2 }, TalearrTagApplyMode.Add);
            _service.Apply(new[] { 11 }, new[] { 2 }, TalearrTagApplyMode.Remove);
            _service.Apply(new[] { 12 }, new[] { 3 }, TalearrTagApplyMode.Add);
            _service.Apply(new[] { 12 }, new[] { 1 }, TalearrTagApplyMode.Replace);

            var all = _service.GetAll();
            Assert.That(all[10], Is.EqualTo(new[] { 1, 2 }));
            Assert.That(all[11], Is.EqualTo(new[] { 1 }));
            Assert.That(all[12], Is.EqualTo(new[] { 1 }));
            Assert.That(_service.Get(10), Is.EqualTo(new[] { 1, 2 }));
        }

        [Test]
        public void replacing_with_nothing_clears_the_book()
        {
            _service.Apply(new[] { 10 }, new[] { 1 }, TalearrTagApplyMode.Add);
            var result = _service.Apply(new[] { 10 }, new int[0], TalearrTagApplyMode.Replace);

            Assert.That(result[10], Is.Empty);
            Assert.That(_service.GetAll().ContainsKey(10), Is.False);
        }

        [Test]
        public void unknown_tag_ids_are_dropped()
        {
            var result = _service.Apply(new[] { 10 }, new[] { 1, 99 }, TalearrTagApplyMode.Add);

            Assert.That(result[10], Is.EqualTo(new[] { 1 }));
        }

        [Test]
        public void deleted_books_lose_their_tags()
        {
            _service.Apply(new[] { 10, 11, 12 }, new[] { 1 }, TalearrTagApplyMode.Add);

            _service.Handle(new BookDeletedEvent(new Book { Id = 10 }, false, false, deletedBooks: new[] { new Book { Id = 11 } }));

            Assert.That(_service.GetAll().Keys, Is.EquivalentTo(new[] { 12 }));
        }

        [Test]
        public void deleting_a_tag_removes_it_from_books()
        {
            _service.Apply(new[] { 10 }, new[] { 1, 2 }, TalearrTagApplyMode.Add);
            _tags.Tags.RemoveAll(t => t.Id == 2);

            _service.Handle(new TagsUpdatedEvent());

            Assert.That(_service.Get(10), Is.EqualTo(new[] { 1 }));
        }

        [Test]
        public void combine_is_order_independent_and_distinct()
        {
            Assert.That(TalearrBookTagService.Combine(new[] { 3, 1 }, new[] { 1, 2 }, TalearrTagApplyMode.Add), Is.EqualTo(new[] { 1, 2, 3 }));
        }

        // Wanted page filters

        private static TalearrBookFilterRule Rule(string key, object value, string type)
        {
            return new TalearrBookFilterRule { Key = key, Value = JsonSerializer.SerializeToElement(value), Type = type };
        }

        private static TalearrBookFilterFacts Facts()
        {
            return new TalearrBookFilterFacts
            {
                BookId = 1,
                Tags = new List<int> { 5 },
                AuthorTags = new List<int> { 7 },
                Genres = new List<string> { "Fantasy", "Short stories" },
                Narrators = new List<string> { "Ray Porter" }
            };
        }

        [Test]
        public void filter_rules_match_like_the_book_index()
        {
            var facts = Facts();

            Assert.That(TalearrBookFilterService.Matches(facts, Rule("tags", new[] { 5, 6 }, "contains")), Is.True);
            Assert.That(TalearrBookFilterService.Matches(facts, Rule("tags", new[] { 6 }, "contains")), Is.False);
            Assert.That(TalearrBookFilterService.Matches(facts, Rule("tags", new[] { 6 }, "notContains")), Is.True);
            Assert.That(TalearrBookFilterService.Matches(facts, Rule("tags", new[] { 5, 6 }, "notContains")), Is.False);
            Assert.That(TalearrBookFilterService.Matches(facts, Rule("authorTags", new[] { 7 }, "contains")), Is.True);
            Assert.That(TalearrBookFilterService.Matches(facts, Rule("genres", new[] { "fantasy" }, "contains")), Is.True);
            Assert.That(TalearrBookFilterService.Matches(facts, Rule("genres", new[] { "Short stories" }, "notContains")), Is.False);
            Assert.That(TalearrBookFilterService.Matches(facts, Rule("narrator", new[] { "porter" }, "contains")), Is.True);
            Assert.That(TalearrBookFilterService.Matches(facts, Rule("narrator", "Ray Porter", "equal")), Is.True);
            Assert.That(TalearrBookFilterService.Matches(facts, Rule("narrator", new[] { "kate" }, "contains")), Is.False);
        }

        [Test]
        public void unsupported_or_broken_filters_are_ignored()
        {
            Assert.That(TalearrBookFilterService.ParseRules(null), Is.Empty);
            Assert.That(TalearrBookFilterService.ParseRules("not json"), Is.Empty);
            Assert.That(TalearrBookFilterService.ParseRules("[{\"key\":\"monitored\",\"value\":true,\"type\":\"equal\"}]"), Is.Empty);
            Assert.That(TalearrBookFilterService.ParseRules("[{\"key\":\"tags\",\"value\":[1],\"type\":\"contains\"}]"), Has.Count.EqualTo(1));
        }

        [Test]
        public void author_tags_follow_the_book_format()
        {
            var author = new Author { AudiobookTags = new HashSet<int> { 1 }, EbookTags = new HashSet<int> { 2 } };

            Assert.That(TalearrBookFilterService.AuthorTagsFor(author, BookMediaType.Audiobook), Is.EqualTo(new[] { 1 }));
            Assert.That(TalearrBookFilterService.AuthorTagsFor(author, BookMediaType.Ebook), Is.EqualTo(new[] { 2 }));
            Assert.That(TalearrBookFilterService.AuthorTagsFor(null, BookMediaType.Ebook), Is.Empty);
        }
    }
}
