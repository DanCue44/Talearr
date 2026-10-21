using System.IO;
using NUnit.Framework;
using NzbDrone.Core.MediaCover;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats;
using SixLabors.ImageSharp.Formats.Bmp;
using SixLabors.ImageSharp.Formats.Gif;
using SixLabors.ImageSharp.Formats.Jpeg;
using SixLabors.ImageSharp.Formats.Png;
using SixLabors.ImageSharp.Formats.Tiff;
using SixLabors.ImageSharp.Formats.Webp;
using SixLabors.ImageSharp.PixelFormats;

namespace Chaptarr.Core.Test.MediaCover
{
    [TestFixture]
    public class CoverImageFormatsFixture
    {
        private static byte[] Encode(string extension)
        {
            using var image = new Image<Rgba32>(8, 8);
            using var stream = new MemoryStream();

            IImageEncoder encoder = extension switch
            {
                ".jpg" => new JpegEncoder(),
                ".png" => new PngEncoder(),
                ".webp" => new WebpEncoder(),
                ".gif" => new GifEncoder(),
                ".bmp" => new BmpEncoder(),
                _ => new TiffEncoder()
            };

            image.Save(stream, encoder);

            return stream.ToArray();
        }

        [TestCase(".jpg")]
        [TestCase(".png")]
        [TestCase(".webp")]
        [TestCase(".gif")]
        [TestCase(".bmp")]
        public void should_load_cover_formats(string extension)
        {
            using var image = Image.Load(CoverImageFormats.DecoderOptions, Encode(extension));

            Assert.That(image.Width, Is.EqualTo(8));
        }

        [Test]
        public void should_refuse_tiff()
        {
            var tiff = Encode(".tiff");

            Assert.Throws<UnknownImageFormatException>(() => Image.Load(CoverImageFormats.DecoderOptions, tiff));
            Assert.Throws<UnknownImageFormatException>(() => Image.Identify(CoverImageFormats.DecoderOptions, tiff));
        }
    }
}
