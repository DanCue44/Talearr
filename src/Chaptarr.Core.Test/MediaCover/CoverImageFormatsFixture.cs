using System;
using System.IO;
using NUnit.Framework;
using NzbDrone.Core.MediaCover;
using SkiaSharp;

namespace Chaptarr.Core.Test.MediaCover
{
    [TestFixture]
    public class CoverImageFormatsFixture
    {
        [TestCase(CoverImageFormats.Jpeg, 8)]
        [TestCase(CoverImageFormats.Png, 8)]
        [TestCase(CoverImageFormats.Webp, 8)]
        [TestCase(CoverImageFormats.Gif, 1)]
        [TestCase(CoverImageFormats.Bmp, 8)]
        public void should_load_cover_formats(string format, int expectedWidth)
        {
            var data = TestImages.Encode(format, 8, 8);

            Assert.That(CoverImageFormats.DetectFormat(data), Is.EqualTo(format));
            Assert.That(CoverImageFormats.Identify(data).Width, Is.EqualTo(expectedWidth));

            using var image = CoverImageFormats.Decode(data);
            Assert.That(image.Width, Is.EqualTo(expectedWidth));
        }

        [Test]
        public void should_refuse_tiff()
        {
            var tiff = TestImages.Tiff1x1;

            Assert.That(CoverImageFormats.DetectFormat(tiff), Is.Null);
            Assert.Throws<CoverImageFormatException>(() => CoverImageFormats.Decode(tiff));
            Assert.Throws<CoverImageFormatException>(() => CoverImageFormats.Identify(tiff));
        }

        [Test]
        public void should_refuse_images_claiming_more_pixels_than_a_cover_can_have()
        {
            var huge = TestImages.HugePngHeader(20000, 20000);

            var ex = Assert.Throws<CoverImageFormatException>(() => CoverImageFormats.Identify(huge));
            Assert.That(ex.Message, Does.Contain("20000x20000"));
            Assert.Throws<CoverImageFormatException>(() => CoverImageFormats.Decode(huge));
        }

        [Test]
        public void should_refuse_garbage_and_empty_data()
        {
            Assert.That(CoverImageFormats.DetectFormat(new byte[] { 1, 2, 3, 4 }), Is.Null);
            Assert.That(CoverImageFormats.DetectFormat(Array.Empty<byte>()), Is.Null);
            Assert.Throws<CoverImageFormatException>(() => CoverImageFormats.Decode(new byte[] { 1, 2, 3, 4 }));
            Assert.Throws<CoverImageFormatException>(() => CoverImageFormats.Decode(Array.Empty<byte>()));
        }

        [TestCase(".jpg", CoverImageFormats.Jpeg)]
        [TestCase("JPEG", CoverImageFormats.Jpeg)]
        [TestCase(".jfif", CoverImageFormats.Jpeg)]
        [TestCase(".png", CoverImageFormats.Png)]
        [TestCase(".webp", CoverImageFormats.Webp)]
        [TestCase(".gif", CoverImageFormats.Gif)]
        [TestCase(".bmp", CoverImageFormats.Bmp)]
        [TestCase(".tiff", null)]
        [TestCase("", null)]
        public void should_map_extensions_to_formats(string extension, string expected)
        {
            Assert.That(CoverImageFormats.FormatForExtension(extension), Is.EqualTo(expected));
        }

        [Test]
        public void should_write_only_jpeg_png_and_webp()
        {
            Assert.That(CoverImageFormats.CanWrite(CoverImageFormats.Jpeg), Is.True);
            Assert.That(CoverImageFormats.CanWrite(CoverImageFormats.Png), Is.True);
            Assert.That(CoverImageFormats.CanWrite(CoverImageFormats.Webp), Is.True);
            Assert.That(CoverImageFormats.CanWrite(CoverImageFormats.Gif), Is.False);
            Assert.That(CoverImageFormats.CanWrite(CoverImageFormats.Bmp), Is.False);
        }

        [Test]
        public void jpeg_output_puts_transparency_on_white()
        {
            using var transparent = new SKBitmap(new SKImageInfo(4, 4, SKColorType.Rgba8888, SKAlphaType.Premul));
            transparent.Erase(SKColors.Transparent);

            using var decoded = CoverImageFormats.Decode(CoverImageFormats.Encode(transparent, CoverImageFormats.Jpeg));
            var pixel = decoded.GetPixel(2, 2);

            Assert.That(pixel.Red, Is.GreaterThan(240));
            Assert.That(pixel.Green, Is.GreaterThan(240));
            Assert.That(pixel.Blue, Is.GreaterThan(240));
        }

        [Test]
        public void resize_keeps_the_aspect_ratio()
        {
            using var image = CoverImageFormats.Decode(TestImages.Png(300, 450));
            using var resized = CoverImageFormats.ResizeToHeight(image, 100);

            Assert.That(resized.Height, Is.EqualTo(100));
            Assert.That(resized.Width, Is.EqualTo(67));
        }

        [TestCase(".gif")]
        [TestCase(".bmp")]
        public void resizer_refuses_formats_it_cannot_write_and_leaves_no_file(string extension)
        {
            var folder = Path.Combine(Path.GetTempPath(), "talearr-resize-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(folder);
            try
            {
                var source = Path.Combine(folder, "cover" + extension);
                var destination = Path.Combine(folder, "cover-250" + extension);
                File.WriteAllBytes(source, TestImages.Encode(CoverImageFormats.FormatForExtension(extension), 8, 8));

                Assert.Throws<NotSupportedException>(() => new ImageResizer(null, null).Resize(source, destination, 4));
                Assert.That(File.Exists(destination), Is.False);
            }
            finally
            {
                Directory.Delete(folder, recursive: true);
            }
        }
    }
}
