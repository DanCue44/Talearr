using NUnit.Framework;
using NzbDrone.Core.Parser;
using NzbDrone.Core.Qualities;

namespace Chaptarr.Core.Test.Parser
{
    /// <summary>
    /// The indexer's file type decides the quality at search time; import decides it by file
    /// extension. Both sides must agree or a release is grabbed as one quality and imported as another.
    /// </summary>
    [TestFixture]
    public class QualityParserFileTypeFixture
    {
        [TestCase("m4a")]
        [TestCase("M4A")]
        [TestCase("m4p")]
        public void should_match_import_for_aac_container_file_types(string fileType)
        {
            var quality = QualityParser.ParseQualityFromFileType(fileType, "The Martian", 0);

            Assert.That(quality.Quality, Is.EqualTo(Quality.MP3));
        }

        [Test]
        public void should_keep_m4b_file_type_as_m4b()
        {
            var quality = QualityParser.ParseQualityFromFileType("m4b", "The Martian", 0);

            Assert.That(quality.Quality, Is.EqualTo(Quality.M4B));
        }

        [Test]
        public void should_keep_codec_only_aac_as_m4b()
        {
            var quality = QualityParser.ParseQualityFromFileType("AAC", "The Martian", 0);

            Assert.That(quality.Quality, Is.EqualTo(Quality.M4B));
        }
    }
}
