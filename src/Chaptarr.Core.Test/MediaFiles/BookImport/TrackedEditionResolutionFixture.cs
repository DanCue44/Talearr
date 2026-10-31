using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using NUnit.Framework;
using NzbDrone.Core.Books;
using NzbDrone.Core.Datastore;
using NzbDrone.Core.History;
using NzbDrone.Core.MediaFiles.BookImport.Services;

namespace Chaptarr.Core.Test.MediaFiles.BookImport
{
    [TestFixture]
    public class TrackedEditionResolutionFixture
    {
        private const string DownloadId = "HASH";

        private class EditionServiceProxy : DispatchProxy
        {
            public List<Edition> Editions { get; } = new();

            protected override object Invoke(MethodInfo targetMethod, object[] args)
            {
                switch (targetMethod?.Name)
                {
                    case nameof(IEditionService.GetEditionsByBook) when args[0] is int bookId:
                        return Editions.Where(e => e.BookId == bookId).ToList();

                    case nameof(IEditionService.GetEdition):
                        return Editions.SingleOrDefault(e => e.Id == (int)args[0])
                            ?? throw new ModelNotFoundException(typeof(Edition), (int)args[0]);

                    default:
                        throw new NotImplementedException(targetMethod?.Name);
                }
            }
        }

        private class HistoryServiceProxy : DispatchProxy
        {
            public List<EntityHistory> Rows { get; } = new();

            protected override object Invoke(MethodInfo targetMethod, object[] args)
            {
                switch (targetMethod?.Name)
                {
                    case nameof(IHistoryService.FindByDownloadId):
                        return Rows.Where(r => r.DownloadId == (string)args[0]).ToList();

                    default:
                        throw new NotImplementedException(targetMethod?.Name);
                }
            }
        }

        private IEditionService _editionService;
        private IHistoryService _historyService;
        private Book _book;

        [SetUp]
        public void SetUp()
        {
            _editionService = DispatchProxy.Create<IEditionService, EditionServiceProxy>();
            _historyService = DispatchProxy.Create<IHistoryService, HistoryServiceProxy>();
            _book = new Book { Id = 8, Title = "Book" };
        }

        private void GivenEdition(int id, bool monitored, int bookId = 8)
        {
            ((EditionServiceProxy)(object)_editionService).Editions.Add(
                new Edition { Id = id, BookId = bookId, Title = "Book", Monitored = monitored });
        }

        private void GivenGrabbed(int editionId)
        {
            ((HistoryServiceProxy)(object)_historyService).Rows.Add(new EntityHistory
            {
                EventType = EntityHistoryEventType.Grabbed,
                DownloadId = DownloadId,
                BookId = _book.Id,
                EditionId = editionId,
                Date = DateTime.UtcNow
            });
        }

        private Edition Resolve() =>
            TrackedMultipartAudioRepairHelper.ResolveExpectedTrackedEdition(_book, DownloadId, _historyService, _editionService);

        [Test]
        public void returns_the_grabbed_edition()
        {
            GivenEdition(1, monitored: true);
            GivenEdition(2, monitored: false);
            GivenGrabbed(2);

            Assert.That(Resolve().Id, Is.EqualTo(2));
        }

        [Test]
        public void falls_back_to_the_monitored_edition_when_a_refresh_deleted_the_grabbed_one()
        {
            GivenEdition(5, monitored: false);
            GivenEdition(6, monitored: true);
            GivenGrabbed(4);

            Assert.That(Resolve().Id, Is.EqualTo(6));
        }

        [Test]
        public void ignores_a_grabbed_edition_of_another_book()
        {
            GivenEdition(1, monitored: true);
            GivenEdition(9, monitored: false, bookId: 99);
            GivenGrabbed(9);

            Assert.That(Resolve().Id, Is.EqualTo(1));
        }
    }
}
