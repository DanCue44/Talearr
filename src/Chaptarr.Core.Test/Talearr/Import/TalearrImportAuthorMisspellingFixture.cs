using NUnit.Framework;
using NzbDrone.Core.Talearr.Import;

namespace Chaptarr.Core.Test.Talearr.Import
{
    [TestFixture]
    public class TalearrImportAuthorMisspellingFixture
    {
        // 2026-11-22: an ebook of One Second After credited "William R. Fortschen" and was rejected.
        [TestCase("William R. Fortschen", "William R. Forstchen")]
        [TestCase("William Forstchen", "William Forstchn")]
        [TestCase("Brandon Sandersen", "Brandon Sanderson")]
        public void a_one_letter_slip_is_the_same_author(string found, string expected)
        {
            Assert.That(TalearrImportAuthorCheck.IsMisspelling(found, expected), Is.True);
        }

        [TestCase("Craig Rice", "Ryan Becker")]
        [TestCase("William R. Forstchen", "William R. Forstchen")]
        [TestCase("Jon Fosse", "Jon Fossa")]
        [TestCase("Anne Rices", "Anne Rice")]
        [TestCase("Brandon Anderson", "Brandon Sanderson")]
        [TestCase("Stephen King", "Stephen Kong")]
        [TestCase("Mary Higgins Clark", "Carol Higgins Clark")]
        [TestCase("William Forstchen", "William R. Forstchen")]
        public void a_different_author_is_still_different(string found, string expected)
        {
            Assert.That(TalearrImportAuthorCheck.IsMisspelling(found, expected), Is.False);
        }
    }
}
