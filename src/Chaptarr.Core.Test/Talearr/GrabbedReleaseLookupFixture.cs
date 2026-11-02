using System;
using System.Collections.Generic;
using NUnit.Framework;
using NzbDrone.Core.Download.TrackedDownloads;
using NzbDrone.Core.History;
using NzbDrone.Core.Talearr.GrabbedReleases;

namespace Chaptarr.Core.Test.Talearr
{
    [TestFixture]
    public class GrabbedReleaseLookupFixture
    {
        private const string Hash = "DFBAEACB3923F6BCCD5F658E68473843DC2B46ED";

        private static EntityHistory Grab(string guid, string downloadId, DateTime date, string infoHash = null, string client = "qBittorrent (VPN)")
        {
            // Stored camel cased, as the database hands it back
            var data = new Dictionary<string, string> { ["guid"] = guid, ["downloadClientName"] = client };

            if (infoHash != null)
            {
                data["torrentInfoHash"] = infoHash;
            }

            return new EntityHistory
            {
                EventType = EntityHistoryEventType.Grabbed,
                DownloadId = downloadId,
                Date = date,
                Data = data
            };
        }

        private static EntityHistory Event(EntityHistoryEventType type, string downloadId, DateTime date)
        {
            return new EntityHistory { EventType = type, DownloadId = downloadId, Date = date, Data = new Dictionary<string, string>() };
        }

        [Test]
        public void marks_a_completed_download_that_is_waiting_on_a_blocked_import()
        {
            var grabbedAt = new DateTime(2026, 10, 31, 15, 45, 2, DateTimeKind.Utc);
            var lookup = GrabbedReleaseLookup.Build(
                new[] { Grab("MAM-1165528", Hash, grabbedAt) },
                new[] { new GrabbedReleaseLookup.QueueEntry { DownloadId = Hash, State = TrackedDownloadState.ImportBlocked, DownloadClient = "qBittorrent (VPN)" } });

            var found = lookup.Find("MAM-1165528", null);

            Assert.That(found, Is.Not.Null);
            Assert.That(found.State, Is.EqualTo(GrabbedReleaseStates.ImportBlocked));
            Assert.That(found.GrabbedAt, Is.EqualTo(grabbedAt));
            Assert.That(found.DownloadClient, Is.EqualTo("qBittorrent (VPN)"));
            Assert.That(GrabbedReleaseStates.IsInDownloadClient(found.State), Is.True);
        }

        [Test]
        public void matches_by_info_hash_when_the_guid_differs()
        {
            var lookup = GrabbedReleaseLookup.Build(
                new[] { Grab("other-indexer-guid", Hash, DateTime.UtcNow) },
                Array.Empty<GrabbedReleaseLookup.QueueEntry>());

            Assert.That(lookup.Find("MAM-1165528", Hash.ToLowerInvariant()), Is.Not.Null);
        }

        [Test]
        public void reports_imported_and_failed_downloads_from_history_once_they_leave_the_queue()
        {
            var now = DateTime.UtcNow;
            var lookup = GrabbedReleaseLookup.Build(
                new[]
                {
                    Grab("imported-guid", "id-1", now.AddDays(-3)),
                    Event(EntityHistoryEventType.DownloadImported, "id-1", now.AddDays(-3).AddMinutes(5)),
                    Grab("failed-guid", "id-2", now.AddDays(-2)),
                    Event(EntityHistoryEventType.DownloadFailed, "id-2", now.AddDays(-2).AddMinutes(5)),
                    Grab("gone-guid", "id-3", now.AddDays(-1))
                },
                Array.Empty<GrabbedReleaseLookup.QueueEntry>());

            Assert.That(lookup.Find("imported-guid", null).State, Is.EqualTo(GrabbedReleaseStates.Imported));
            Assert.That(lookup.Find("failed-guid", null).State, Is.EqualTo(GrabbedReleaseStates.Failed));
            Assert.That(lookup.Find("gone-guid", null).State, Is.EqualTo(GrabbedReleaseStates.Grabbed));
        }

        [Test]
        public void reports_the_latest_grab_of_a_release_grabbed_twice()
        {
            var now = DateTime.UtcNow;
            var lookup = GrabbedReleaseLookup.Build(
                new[]
                {
                    Grab("same-guid", "old-id", now.AddDays(-5)),
                    Event(EntityHistoryEventType.DownloadFailed, "old-id", now.AddDays(-5).AddMinutes(1)),
                    Grab("same-guid", "new-id", now.AddHours(-1))
                },
                new[] { new GrabbedReleaseLookup.QueueEntry { DownloadId = "new-id", State = TrackedDownloadState.Downloading } });

            var found = lookup.Find("same-guid", null);

            Assert.That(found.DownloadId, Is.EqualTo("new-id"));
            Assert.That(found.State, Is.EqualTo(GrabbedReleaseStates.Downloading));
        }

        [Test]
        public void marks_a_download_added_in_the_client_by_hand_by_its_hash()
        {
            var lookup = GrabbedReleaseLookup.Build(
                Array.Empty<EntityHistory>(),
                new[] { new GrabbedReleaseLookup.QueueEntry { DownloadId = Hash, State = TrackedDownloadState.ImportPending } });

            Assert.That(lookup.Find("anything", Hash).State, Is.EqualTo(GrabbedReleaseStates.ImportPending));
        }

        [Test]
        public void finds_nothing_for_a_release_never_grabbed()
        {
            var lookup = GrabbedReleaseLookup.Build(new[] { Grab("MAM-1165528", Hash, DateTime.UtcNow) }, null);

            Assert.That(lookup.Find("MAM-999", "ABCDEF"), Is.Null);
            Assert.That(lookup.Find(null, null), Is.Null);
            Assert.That(GrabbedReleaseLookup.Empty.IsEmpty, Is.True);
        }
    }
}
