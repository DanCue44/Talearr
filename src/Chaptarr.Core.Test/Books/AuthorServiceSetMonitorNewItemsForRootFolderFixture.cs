using System;
using System.Collections.Generic;
using System.Linq;
using System.Linq.Expressions;
using System.Reflection;
using NLog;
using NUnit.Framework;
using NzbDrone.Common.Cache;
using NzbDrone.Common.Messaging;
using NzbDrone.Core.Books;
using NzbDrone.Core.Datastore;
using NzbDrone.Core.Messaging.Events;

namespace Chaptarr.Core.Test.Books
{
    // Issue #8: a root folder's Monitor New Books default can be applied to the authors already in it.
    [TestFixture]
    public class AuthorServiceSetMonitorNewItemsForRootFolderFixture
    {
        private sealed class StubEventAggregator : IEventAggregator
        {
            public void PublishEvent<TEvent>(TEvent @event)
                where TEvent : class, IEvent
            {
            }
        }

        // Serves All() and records batched single-column writes; anything else fails the test.
        private class AuthorRepositoryProxy : DispatchProxy
        {
            public List<Author> Authors { get; set; } = new List<Author>();
            public List<(List<Author> Models, List<string> Columns)> SetFieldsCalls { get; } = new List<(List<Author>, List<string>)>();

            protected override object Invoke(MethodInfo targetMethod, object[] args)
            {
                if (targetMethod?.Name == nameof(IAuthorRepository.All))
                {
                    return Authors;
                }

                if (targetMethod?.Name == nameof(IAuthorRepository.SetFields) && args[0] is IList<Author> models)
                {
                    var columns = ((Expression<Func<Author, object>>[])args[1])
                        .Select(expression => expression.GetMemberName().Name)
                        .ToList();
                    SetFieldsCalls.Add((models.ToList(), columns));
                    return null;
                }

                throw new NotImplementedException($"Test proxy does not implement IAuthorRepository.{targetMethod?.Name}");
            }
        }

        private AuthorRepositoryProxy _repository;
        private AuthorService _service;
        private Author _inBoth;
        private Author _audiobookOnly;
        private Author _otherRoot;
        private Author _trailingSlash;

        [SetUp]
        public void SetUp()
        {
            _inBoth = new Author
            {
                Id = 1,
                Name = "In Both",
                AudiobookRootFolderPath = "/library",
                EbookRootFolderPath = "/library",
                AudiobookMonitored = true,
                EbookMonitored = true,
                AudiobookMonitorNewItems = NewItemMonitorTypes.All,
                EbookMonitorNewItems = NewItemMonitorTypes.All,
                AudiobookQualityProfileId = 1,
                EbookQualityProfileId = 2
            };
            _audiobookOnly = new Author
            {
                Id = 2,
                Name = "Audiobook Only",
                AudiobookRootFolderPath = "/library",
                AudiobookMonitored = true,
                AudiobookMonitorNewItems = NewItemMonitorTypes.New,
                EbookMonitorNewItems = NewItemMonitorTypes.All
            };
            _otherRoot = new Author
            {
                Id = 3,
                Name = "Other Root",
                AudiobookRootFolderPath = "/library-two",
                EbookRootFolderPath = "/library-two",
                AudiobookMonitorNewItems = NewItemMonitorTypes.All,
                EbookMonitorNewItems = NewItemMonitorTypes.All
            };
            _trailingSlash = new Author
            {
                Id = 4,
                Name = "Trailing Slash",
                AudiobookRootFolderPath = "/other",
                EbookRootFolderPath = "/library/",
                AudiobookMonitorNewItems = NewItemMonitorTypes.All,
                EbookMonitorNewItems = NewItemMonitorTypes.All
            };

            var repository = DispatchProxy.Create<IAuthorRepository, AuthorRepositoryProxy>();
            _repository = (AuthorRepositoryProxy)(object)repository;
            _repository.Authors = new List<Author> { _inBoth, _audiobookOnly, _otherRoot, _trailingSlash };

            _service = new AuthorService(
                repository,
                new StubEventAggregator(),
                authorPathBuilder: null,
                rootFolderService: null,
                commandQueueManager: null,
                cacheManager: new CacheManager(),
                bookRepository: null,
                mediaFileService: null,
                logger: LogManager.GetCurrentClassLogger());
        }

        [Test]
        public void should_change_only_authors_in_that_root_folder_for_that_format()
        {
            var changed = _service.SetMonitorNewItemsForRootFolder("/library", BookMediaType.Audiobook, NewItemMonitorTypes.None);

            Assert.That(changed.Select(a => a.Id), Is.EquivalentTo(new[] { _inBoth.Id, _audiobookOnly.Id }));
            Assert.That(_inBoth.AudiobookMonitorNewItems, Is.EqualTo(NewItemMonitorTypes.None));
            Assert.That(_audiobookOnly.AudiobookMonitorNewItems, Is.EqualTo(NewItemMonitorTypes.None));
            Assert.That(_otherRoot.AudiobookMonitorNewItems, Is.EqualTo(NewItemMonitorTypes.All));

            // Author 4 sits in /library only for ebooks, so its audiobook value is left alone.
            Assert.That(_trailingSlash.AudiobookMonitorNewItems, Is.EqualTo(NewItemMonitorTypes.All));
        }

        [Test]
        public void should_leave_the_other_format_and_other_settings_untouched()
        {
            _service.SetMonitorNewItemsForRootFolder("/library", BookMediaType.Audiobook, NewItemMonitorTypes.None);

            Assert.That(_inBoth.EbookMonitorNewItems, Is.EqualTo(NewItemMonitorTypes.All));
            Assert.That(_audiobookOnly.EbookMonitorNewItems, Is.EqualTo(NewItemMonitorTypes.All));
            Assert.That(_inBoth.AudiobookMonitored, Is.True);
            Assert.That(_inBoth.EbookMonitored, Is.True);
            Assert.That(_inBoth.AudiobookQualityProfileId, Is.EqualTo(1));
            Assert.That(_inBoth.EbookQualityProfileId, Is.EqualTo(2));
        }

        [Test]
        public void should_write_only_the_chosen_format_column_in_one_batch()
        {
            _service.SetMonitorNewItemsForRootFolder("/library", BookMediaType.Ebook, NewItemMonitorTypes.None);

            Assert.That(_repository.SetFieldsCalls, Has.Count.EqualTo(1));
            Assert.That(_repository.SetFieldsCalls[0].Columns, Is.EqualTo(new[] { nameof(Author.EbookMonitorNewItems) }));
            Assert.That(_repository.SetFieldsCalls[0].Models.Select(a => a.Id), Is.EquivalentTo(new[] { _inBoth.Id, _trailingSlash.Id }));
            Assert.That(_inBoth.EbookMonitorNewItems, Is.EqualTo(NewItemMonitorTypes.None));
            Assert.That(_trailingSlash.EbookMonitorNewItems, Is.EqualTo(NewItemMonitorTypes.None));
            Assert.That(_inBoth.AudiobookMonitorNewItems, Is.EqualTo(NewItemMonitorTypes.All));
            Assert.That(_audiobookOnly.EbookMonitorNewItems, Is.EqualTo(NewItemMonitorTypes.All));
        }

        [Test]
        public void should_write_nothing_when_no_author_is_in_the_root_folder()
        {
            var changed = _service.SetMonitorNewItemsForRootFolder("/empty", BookMediaType.Audiobook, NewItemMonitorTypes.None);

            Assert.That(changed, Is.Empty);
            Assert.That(_repository.SetFieldsCalls, Is.Empty);
        }
    }
}
