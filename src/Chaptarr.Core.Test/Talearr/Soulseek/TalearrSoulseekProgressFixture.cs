using System.Collections.Generic;
using NUnit.Framework;
using NzbDrone.Core.Talearr.Soulseek;

namespace Chaptarr.Core.Test.Talearr.Soulseek
{
    [TestFixture]
    public class TalearrSoulseekProgressFixture
    {
        private static SlskdTransfer T(string state, long size = 100, long done = 0, string exception = null, int? place = null)
        {
            return new SlskdTransfer { Id = "x", Filename = @"A\Book\01.mp3", State = state, Size = size, BytesTransferred = done, Exception = exception, PlaceInQueue = place };
        }

        [Test]
        public void no_transfers_is_missing()
        {
            Assert.That(TalearrSoulseekProgress.Evaluate(new List<SlskdTransfer>(), 2).Status, Is.EqualTo(TalearrSoulseekProgressStatus.Missing));
        }

        [Test]
        public void all_succeeded_is_completed()
        {
            var result = TalearrSoulseekProgress.Evaluate(new[] { T("Completed, Succeeded"), T("Completed, Succeeded") }, 2);

            Assert.That(result.Status, Is.EqualTo(TalearrSoulseekProgressStatus.Completed));
        }

        [Test]
        public void not_every_file_listed_is_not_completed()
        {
            var result = TalearrSoulseekProgress.Evaluate(new[] { T("Completed, Succeeded") }, 2);

            Assert.That(result.Status, Is.EqualTo(TalearrSoulseekProgressStatus.Downloading));
        }

        [TestCase("Completed, Errored")]
        [TestCase("Completed, Rejected")]
        [TestCase("Completed, TimedOut")]
        [TestCase("Completed, Cancelled")]
        public void any_failed_file_fails_the_grab(string state)
        {
            var result = TalearrSoulseekProgress.Evaluate(new[] { T("Completed, Succeeded"), T(state, exception: "File not shared") }, 2);

            Assert.That(result.Status, Is.EqualTo(TalearrSoulseekProgressStatus.Failed));
            Assert.That(result.Message, Does.Contain("File not shared"));
        }

        [Test]
        public void in_progress_reports_percent()
        {
            var result = TalearrSoulseekProgress.Evaluate(new[] { T("InProgress", 100, 50), T("Queued, Remotely", 100, 0) }, 2);

            Assert.That(result.Status, Is.EqualTo(TalearrSoulseekProgressStatus.Downloading));
            Assert.That(result.Message, Does.Contain("25%"));
        }

        [Test]
        public void queued_reports_the_place()
        {
            var result = TalearrSoulseekProgress.Evaluate(new[] { T("Queued, Remotely", place: 7), T("Queued, Remotely", place: 9) }, 2);

            Assert.That(result.Status, Is.EqualTo(TalearrSoulseekProgressStatus.Queued));
            Assert.That(result.Message, Does.Contain("place 7"));
        }
    }
}
