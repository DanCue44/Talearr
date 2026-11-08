using System.IO;
using System.Reflection;
using System.Runtime.CompilerServices;
using NUnit.Framework;
using NzbDrone.Core.Books;

namespace Chaptarr.Core.Test.Books
{
    // An author with no bio (none on Hardcover) used to fail every refresh with a
    // NullReferenceException in UpdateEntity, so the author's other books were never added.
    [TestFixture]
    public class RefreshAuthorServiceNoOverviewFixture
    {
        private static object Update(Author local, Author remote)
        {
            // UpdateEntity touches no injected service on this path.
            var service = (RefreshAuthorService)RuntimeHelpers.GetUninitializedObject(typeof(RefreshAuthorService));
            var method = typeof(RefreshAuthorService).GetMethod("UpdateEntity", BindingFlags.Instance | BindingFlags.NonPublic);
            return method.Invoke(service, new object[] { local, remote });
        }

        [TestCase(null, null)]
        [TestCase(null, "A bio")]
        [TestCase("A bio", null)]
        public void an_author_without_an_overview_refreshes(string localOverview, string remoteOverview)
        {
            var local = new Author { Name = "Dietmar Arthur Wehr", Overview = localOverview, Path = Path.GetTempPath() };
            var remote = new Author { Name = "Dietmar Arthur Wehr", Overview = remoteOverview };

            Assert.DoesNotThrow(() => Update(local, remote));
        }

        [Test]
        public void a_changed_name_still_retags_files()
        {
            var local = new Author { Name = "Dietmar Wehr", Path = Path.GetTempPath() };
            var remote = new Author { Name = "Dietmar Arthur Wehr" };

            Assert.That(Update(local, remote).ToString(), Is.EqualTo("UpdateTags"));
            Assert.That(local.Name, Is.EqualTo("Dietmar Arthur Wehr"));
        }
    }
}
