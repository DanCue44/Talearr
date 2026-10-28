using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using SkiaSharp;

namespace NzbDrone.Core.MediaCover
{
    public class CoverImageFormatException : IOException
    {
        public CoverImageFormatException(string message)
            : base(message)
        {
        }
    }

    public readonly record struct CoverImageInfo(int Width, int Height, string Format);

    // Talearr: all cover image work goes through here. Covers only ever need JPEG, PNG, WebP,
    // GIF and BMP, so anything else (TIFF, HEIF, ICO, ...) is refused before it is decoded, and
    // so is any image whose header claims more pixels than a cover could sensibly have.
    // Backed by SkiaSharp (MIT); ImageSharp's fixed releases need a license key to build.
    public static class CoverImageFormats
    {
        public const string Jpeg = "JPEG";
        public const string Png = "PNG";
        public const string Webp = "Webp";
        public const string Gif = "GIF";
        public const string Bmp = "BMP";

        // 100 megapixels, far beyond any real cover (a 10000 x 10000 scan)
        public const long MaximumPixels = 100_000_000;

        private const int JpegQuality = 95;
        private const int WebpQuality = 90;

        private static readonly Dictionary<SKEncodedImageFormat, string> Readable = new Dictionary<SKEncodedImageFormat, string>
        {
            { SKEncodedImageFormat.Jpeg, Jpeg },
            { SKEncodedImageFormat.Png, Png },
            { SKEncodedImageFormat.Webp, Webp },
            { SKEncodedImageFormat.Gif, Gif },
            { SKEncodedImageFormat.Bmp, Bmp }
        };

        private static readonly Dictionary<string, string[]> Extensions = new Dictionary<string, string[]>
        {
            { Jpeg, new[] { "jpg", "jpeg", "jpe", "jfif" } },
            { Png, new[] { "png" } },
            { Webp, new[] { "webp" } },
            { Gif, new[] { "gif" } },
            { Bmp, new[] { "bmp", "dib" } }
        };

        // SkiaSharp writes these three; GIF and BMP covers are read but not written
        private static readonly Dictionary<string, SKEncodedImageFormat> Writable = new Dictionary<string, SKEncodedImageFormat>
        {
            { Jpeg, SKEncodedImageFormat.Jpeg },
            { Png, SKEncodedImageFormat.Png },
            { Webp, SKEncodedImageFormat.Webp }
        };

        public static IReadOnlyCollection<string> FileExtensions(string format)
        {
            return format != null && Extensions.TryGetValue(format, out var extensions) ? extensions : Array.Empty<string>();
        }

        public static string FormatForExtension(string extension)
        {
            var trimmed = extension?.Trim().TrimStart('.');
            if (string.IsNullOrEmpty(trimmed))
            {
                return null;
            }

            return Extensions.FirstOrDefault(e => e.Value.Contains(trimmed, StringComparer.OrdinalIgnoreCase)).Key;
        }

        public static bool CanWrite(string format)
        {
            return format != null && Writable.ContainsKey(format);
        }

        // The format of the data, or null when it is not one of the cover formats.
        public static string DetectFormat(byte[] data)
        {
            if (data == null || data.Length == 0)
            {
                return null;
            }

            using var codec = SKCodec.Create(new SKMemoryStream(data));
            return codec != null && Readable.TryGetValue(codec.EncodedFormat, out var format) ? format : null;
        }

        public static CoverImageInfo Identify(byte[] data)
        {
            using var codec = OpenCodec(data);
            return new CoverImageInfo(codec.Info.Width, codec.Info.Height, Readable[codec.EncodedFormat]);
        }

        public static CoverImageInfo Identify(string path)
        {
            return Identify(File.ReadAllBytes(path));
        }

        public static SKBitmap Decode(byte[] data)
        {
            using var codec = OpenCodec(data);
            var info = new SKImageInfo(codec.Info.Width, codec.Info.Height, SKColorType.Rgba8888, SKAlphaType.Premul);
            var bitmap = new SKBitmap(info);
            var result = codec.GetPixels(info, bitmap.GetPixels());
            if (result != SKCodecResult.Success && result != SKCodecResult.IncompleteInput)
            {
                bitmap.Dispose();
                throw new CoverImageFormatException($"Could not decode the image ({result})");
            }

            return bitmap;
        }

        // Scales to the given height and keeps the aspect ratio
        public static SKBitmap ResizeToHeight(SKBitmap source, int height)
        {
            if (height <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(height));
            }

            var width = Math.Max(1, (int)Math.Round(source.Width * (double)height / source.Height));
            var resized = source.Resize(new SKImageInfo(width, height, source.ColorType, source.AlphaType), new SKSamplingOptions(SKCubicResampler.Mitchell));
            return resized ?? throw new CoverImageFormatException("Could not resize the image");
        }

        public static byte[] Encode(SKBitmap bitmap, string format)
        {
            if (!CanWrite(format))
            {
                throw new NotSupportedException($"Writing {format ?? "unknown"} images is not supported");
            }

            using var pixmap = bitmap.PeekPixels();
            SKData data;

            switch (Writable[format])
            {
                case SKEncodedImageFormat.Jpeg:
                    // JPEG has no transparency: put transparent covers on white instead of black
                    using (var flattened = FlattenOnWhite(bitmap))
                    using (var flatPixmap = flattened.PeekPixels())
                    {
                        data = flatPixmap.Encode(new SKJpegEncoderOptions(JpegQuality, SKJpegEncoderDownsample.Downsample444, SKJpegEncoderAlphaOption.Ignore));
                    }

                    break;
                case SKEncodedImageFormat.Webp:
                    data = pixmap.Encode(new SKWebpEncoderOptions(SKWebpEncoderCompression.Lossy, WebpQuality));
                    break;
                default:
                    data = pixmap.Encode(SKPngEncoderOptions.Default);
                    break;
            }

            using (data)
            {
                return data?.ToArray() ?? throw new CoverImageFormatException($"Could not encode the image as {format}");
            }
        }

        private static SKBitmap FlattenOnWhite(SKBitmap source)
        {
            var flattened = new SKBitmap(new SKImageInfo(source.Width, source.Height, SKColorType.Rgba8888, SKAlphaType.Opaque));
            using var canvas = new SKCanvas(flattened);
            canvas.Clear(SKColors.White);
            using var image = SKImage.FromBitmap(source);
            canvas.DrawImage(image, 0, 0, SKSamplingOptions.Default);
            return flattened;
        }

        private static SKCodec OpenCodec(byte[] data)
        {
            if (data == null || data.Length == 0)
            {
                throw new CoverImageFormatException("The image is empty");
            }

            var codec = SKCodec.Create(new SKMemoryStream(data));
            if (codec == null)
            {
                throw new CoverImageFormatException("Not a recognised image");
            }

            if (!Readable.ContainsKey(codec.EncodedFormat))
            {
                var format = codec.EncodedFormat;
                codec.Dispose();
                throw new CoverImageFormatException($"{format} images are not accepted as covers");
            }

            if (codec.Info.Width <= 0 || codec.Info.Height <= 0 || (long)codec.Info.Width * codec.Info.Height > MaximumPixels)
            {
                var size = $"{codec.Info.Width}x{codec.Info.Height}";
                codec.Dispose();
                throw new CoverImageFormatException($"Image size {size} is outside the accepted range");
            }

            return codec;
        }
    }
}
