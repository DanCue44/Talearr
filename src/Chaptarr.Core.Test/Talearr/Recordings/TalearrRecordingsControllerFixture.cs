using System.Collections.Generic;
using System.Linq;
using Chaptarr.Api.V1.Talearr;
using Microsoft.AspNetCore.Mvc;
using NUnit.Framework;
using NzbDrone.Core.Books;
using NzbDrone.Core.Talearr.Recordings;

namespace Chaptarr.Core.Test.Talearr.Recordings
{
    [TestFixture]
    public class TalearrRecordingsControllerFixture
    {
        private RecordingsWorld _world;
        private TalearrRecordingsController _controller;

        [SetUp]
        public void SetUp()
        {
            _world = new RecordingsWorld();
            _controller = new TalearrRecordingsController(_world.Recordings, _world.Groups, _world.BookService, _world.CommandQueue);
            _world.AddFirm(FirmEditions.MoffettAbridged(), withFiles: true);
            _world.LinkAudible(FirmEditions.MoffettAbridged(), "B000MOFFET", "abridged", 360, "D.W. Moffett");
            _world.LinkAudible(FirmEditions.ScottBrickUs(), "B002V5BSLC", "unabridged", 1030, "Scott Brick");
        }

        [TearDown]
        public void TearDown()
        {
            _world.Dispose();
        }

        private static int Status(IActionResult result)
        {
            return result switch
            {
                ObjectResult o => o.StatusCode ?? 200,
                StatusCodeResult s => s.StatusCode,
                _ => 0
            };
        }

        [Test]
        public void post_answers_201_then_200()
        {
            var created = _controller.AddRecording(new TalearrAddRecordingResource { BookId = 3401, Kind = "unabridged", RequestedBy = "allseerr", ExternalRef = "req-1" });
            var again = _controller.AddRecording(new TalearrAddRecordingResource { BookId = 3401, Kind = "unabridged" });

            Assert.That(Status(created), Is.EqualTo(201));
            var body = (TalearrAddRecordingResult)((ObjectResult)created).Value;
            Assert.That(body.Action, Is.EqualTo("created"));
            Assert.That(body.SearchQueued, Is.True);
            Assert.That(body.Recording.Abridged, Is.EqualTo("unabridged"));
            Assert.That(Status(again), Is.EqualTo(200));
            Assert.That(((TalearrAddRecordingResult)((ObjectResult)again).Value).Action, Is.EqualTo("existing"));
        }

        [Test]
        public void post_answers_400_404_and_409()
        {
            _world.Editions.Add(new Edition { Id = 28801, BookId = 3401, ForeignEditionId = "gr:28801-audiobook", Title = "The Firm (Abridged)", Language = "eng", ReadingFormatId = 2, NarratorNames = new List<string> { "Reader One" } });
            _world.Editions.Add(new Edition { Id = 28802, BookId = 3401, ForeignEditionId = "gr:28802-audiobook", Title = "The Firm (Abridged)", Language = "eng", ReadingFormatId = 2, NarratorNames = new List<string> { "Reader Two" } });

            Assert.That(Status(_controller.AddRecording(new TalearrAddRecordingResource { BookId = 3401, Kind = "something" })), Is.EqualTo(400));
            Assert.That(Status(_controller.AddRecording(null)), Is.EqualTo(400));
            Assert.That(Status(_controller.AddRecording(new TalearrAddRecordingResource { BookId = 3401, Kind = "narrator", Narrator = "Nobody Known" })), Is.EqualTo(404));

            var conflict = _controller.AddRecording(new TalearrAddRecordingResource { BookId = 3401, Kind = "edition", EditionId = 999 });
            Assert.That(Status(conflict), Is.EqualTo(404));
            Assert.That(((TalearrRecordingProblem)((ObjectResult)conflict).Value).Candidates, Is.Not.Empty, "the body lists what is available");
        }

        [Test]
        public void post_maps_ambiguous_to_409_with_the_tied_candidates()
        {
            var (service, fake) = InterfaceFake<ITalearrRecordingsService>.Create();
            fake.On(nameof(ITalearrRecordingsService.AddRecording), _ => new AddRecordingOutcome
            {
                Status = AddRecordingStatus.Ambiguous,
                Message = "tie",
                Candidates = new List<RecordingCandidateInfo> { new RecordingCandidateInfo { EditionId = 1 }, new RecordingCandidateInfo { EditionId = 2 } }
            });
            var controller = new TalearrRecordingsController(service, _world.Groups, _world.BookService, _world.CommandQueue);

            var result = controller.AddRecording(new TalearrAddRecordingResource { BookId = 3401, Kind = "unabridged" });

            Assert.That(Status(result), Is.EqualTo(409));
            Assert.That(((TalearrRecordingProblem)((ObjectResult)result).Value).Candidates, Has.Count.EqualTo(2));
        }

        [Test]
        public void get_by_book_or_provider_id()
        {
            var byEbook = _controller.GetRecordings(3402, null);
            var byProvider = _controller.GetRecordings(null, "hc:378630");
            var missing = _controller.GetRecordings(12345, null);

            Assert.That(byEbook.Value.Recordings.Single().BookId, Is.EqualTo(3401));
            Assert.That(byProvider.Value.Recordings.Single().BookId, Is.EqualTo(3401));
            Assert.That(missing.Result, Is.InstanceOf<NotFoundObjectResult>());
        }

        [Test]
        public void delete_refuses_the_primary_and_removes_a_copy()
        {
            var copyId = ((TalearrAddRecordingResult)((ObjectResult)_controller.AddRecording(new TalearrAddRecordingResource { BookId = 3401, Kind = "unabridged", Search = false })).Value).Recording.BookId;

            Assert.That(Status(_controller.RemoveRecording(3401)), Is.EqualTo(409));
            Assert.That(Status(_controller.RemoveRecording(copyId, deleteFiles: true)), Is.EqualTo(200));
            Assert.That(Status(_controller.RemoveRecording(777)), Is.EqualTo(404));
            Assert.That(_world.Books.ContainsKey(copyId), Is.False);
        }

        [Test]
        public void refresh_queues_a_forced_refresh_of_the_whole_group()
        {
            var copyId = ((TalearrAddRecordingResult)((ObjectResult)_controller.AddRecording(new TalearrAddRecordingResource { BookId = 3401, Kind = "unabridged", Search = false })).Value).Recording.BookId;

            var result = _controller.Refresh(3402);

            Assert.That(Status(result), Is.EqualTo(202));
            var command = _world.Commands.OfType<RefreshAudibleRecordingFactsCommand>().Single();
            Assert.That(command.Force, Is.True);
            Assert.That(command.BookIds, Is.EquivalentTo(new[] { 3401, copyId }));
            Assert.That(Status(_controller.Refresh(0)), Is.EqualTo(404));
        }
    }
}
