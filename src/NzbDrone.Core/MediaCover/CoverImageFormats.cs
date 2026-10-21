using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats;
using SixLabors.ImageSharp.Formats.Bmp;
using SixLabors.ImageSharp.Formats.Gif;
using SixLabors.ImageSharp.Formats.Jpeg;
using SixLabors.ImageSharp.Formats.Png;
using SixLabors.ImageSharp.Formats.Webp;

namespace NzbDrone.Core.MediaCover
{
    // Talearr: covers only ever need these formats. Leaving TIFF (and the other ImageSharp
    // decoders) out keeps crafted files away from the TIFF advisories fixed only in 4.1.2.
    public static class CoverImageFormats
    {
        public static readonly SixLabors.ImageSharp.Configuration Configuration = CreateConfiguration();

        public static readonly DecoderOptions DecoderOptions = new DecoderOptions { Configuration = Configuration };

        private static SixLabors.ImageSharp.Configuration CreateConfiguration()
        {
            var configuration = new SixLabors.ImageSharp.Configuration(
                new JpegConfigurationModule(),
                new PngConfigurationModule(),
                new WebpConfigurationModule(),
                new GifConfigurationModule(),
                new BmpConfigurationModule());

            // Thumbnails don't need super high quality
            configuration.ImageFormatsManager.SetEncoder(JpegFormat.Instance, new JpegEncoder
            {
                Quality = 95,
                ColorType = JpegEncodingColor.YCbCrRatio444
            });

            return configuration;
        }
    }
}
