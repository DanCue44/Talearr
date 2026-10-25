using System;
using System.Linq;
using NUnit.Framework;
using NzbDrone.Core.Talearr.Comics;
using NzbDrone.Core.Talearr.Comics.Import;

namespace Chaptarr.Core.Test.Talearr.Comics
{
    [TestFixture]
    public class ComicImportRetryFixture
    {
        private static readonly DateTime Built = new DateTime(2026, 10, 25, 12, 0, 0, DateTimeKind.Utc);

        private static ComicDownload Download(string id, string status, string output, DateTime updated, DateTime? grabbed = null)
        {
            return new ComicDownload { DownloadId = id, SeriesId = 1, VolumeIds = "5", ReleaseTitle = id, Status = status, OutputPath = output, UpdatedAt = updated, GrabbedAt = grabbed ?? updated };
        }

        [Test]
        public void should_retry_imports_that_failed_before_this_build_once_per_folder()
        {
            var before = Built.AddDays(-1);
            var downloads = new[]
            {
                Download("old_import", ComicDownloadStatuses.Failed, "/downloads/Saga", before, before.AddHours(-2)),
                Download("copy_of_it", ComicDownloadStatuses.Failed, "/downloads/Saga", before, before),
                Download("client_failed", ComicDownloadStatuses.Failed, null, before),
                Download("failed_after_build", ComicDownloadStatuses.Failed, "/downloads/Later", Built.AddMinutes(5)),
                Download("imported", ComicDownloadStatuses.Imported, "/downloads/Done", before),
                Download("folder_gone", ComicDownloadStatuses.Failed, "/downloads/Gone", before),
            };

            var picked = ComicImportRetryService.Candidates(downloads, Built, folder => folder != "/downloads/Gone", _ => true);

            Assert.That(picked.Select(d => d.DownloadId), Is.EqualTo(new[] { "copy_of_it" }));
        }

        [Test]
        public void should_not_retry_when_the_volumes_are_no_longer_missing()
        {
            var downloads = new[] { Download("old_import", ComicDownloadStatuses.Failed, "/downloads/Saga", Built.AddDays(-1)) };

            Assert.That(ComicImportRetryService.Candidates(downloads, Built, _ => true, _ => false), Is.Empty);
        }
    }
}
