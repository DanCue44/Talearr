using System;
using System.IO;
using NzbDrone.Core.MediaCover;
using SkiaSharp;

namespace Chaptarr.Core.Test.MediaCover
{
    // Builds small images for tests. SkiaSharp writes JPEG, PNG and WebP; GIF, BMP and TIFF are
    // put together by hand because covers in those formats are only ever read.
    public static class TestImages
    {
        public static byte[] Encode(string format, int width, int height, SKColor? color = null)
        {
            switch (format)
            {
                case CoverImageFormats.Gif:
                    return Gif1x1;
                case CoverImageFormats.Bmp:
                    return Bmp(width, height, color ?? SKColors.Black);
                case "TIFF":
                    return Tiff1x1;
            }

            using var bitmap = new SKBitmap(new SKImageInfo(width, height, SKColorType.Rgba8888, SKAlphaType.Premul));
            bitmap.Erase(color ?? SKColors.Black);
            return CoverImageFormats.Encode(bitmap, format);
        }

        public static byte[] Png(int width, int height, SKColor? color = null) => Encode(CoverImageFormats.Png, width, height, color);

        public static byte[] Jpeg(int width, int height, SKColor? color = null) => Encode(CoverImageFormats.Jpeg, width, height, color);

        // The classic 1x1 transparent GIF89a
        public static readonly byte[] Gif1x1 =
        {
            0x47, 0x49, 0x46, 0x38, 0x39, 0x61, 0x01, 0x00, 0x01, 0x00, 0x80, 0x00, 0x00, 0xFF, 0xFF, 0xFF,
            0x00, 0x00, 0x00, 0x21, 0xF9, 0x04, 0x01, 0x00, 0x00, 0x00, 0x00, 0x2C, 0x00, 0x00, 0x00, 0x00,
            0x01, 0x00, 0x01, 0x00, 0x00, 0x02, 0x02, 0x44, 0x01, 0x00, 0x3B
        };

        // A valid little-endian 1x1 8-bit grayscale TIFF
        public static readonly byte[] Tiff1x1 =
        {
            0x49, 0x49, 0x2A, 0x00, 0x08, 0x00, 0x00, 0x00, 0x08, 0x00,
            0x00, 0x01, 0x03, 0x00, 0x01, 0x00, 0x00, 0x00, 0x01, 0x00, 0x00, 0x00,
            0x01, 0x01, 0x03, 0x00, 0x01, 0x00, 0x00, 0x00, 0x01, 0x00, 0x00, 0x00,
            0x02, 0x01, 0x03, 0x00, 0x01, 0x00, 0x00, 0x00, 0x08, 0x00, 0x00, 0x00,
            0x03, 0x01, 0x03, 0x00, 0x01, 0x00, 0x00, 0x00, 0x01, 0x00, 0x00, 0x00,
            0x06, 0x01, 0x03, 0x00, 0x01, 0x00, 0x00, 0x00, 0x01, 0x00, 0x00, 0x00,
            0x11, 0x01, 0x04, 0x00, 0x01, 0x00, 0x00, 0x00, 0x6E, 0x00, 0x00, 0x00,
            0x16, 0x01, 0x03, 0x00, 0x01, 0x00, 0x00, 0x00, 0x01, 0x00, 0x00, 0x00,
            0x17, 0x01, 0x04, 0x00, 0x01, 0x00, 0x00, 0x00, 0x01, 0x00, 0x00, 0x00,
            0x00, 0x00, 0x00, 0x00,
            0x80
        };

        // 24-bit uncompressed BMP, bottom-up rows padded to 4 bytes
        public static byte[] Bmp(int width, int height, SKColor color)
        {
            var rowSize = ((width * 3) + 3) & ~3;
            var pixelBytes = rowSize * height;
            using var stream = new MemoryStream();
            using var writer = new BinaryWriter(stream);
            writer.Write((byte)'B');
            writer.Write((byte)'M');
            writer.Write(54 + pixelBytes);
            writer.Write(0);
            writer.Write(54);
            writer.Write(40);
            writer.Write(width);
            writer.Write(height);
            writer.Write((short)1);
            writer.Write((short)24);
            writer.Write(0);
            writer.Write(pixelBytes);
            writer.Write(2835);
            writer.Write(2835);
            writer.Write(0);
            writer.Write(0);
            for (var y = 0; y < height; y++)
            {
                for (var x = 0; x < width; x++)
                {
                    writer.Write(color.Blue);
                    writer.Write(color.Green);
                    writer.Write(color.Red);
                }

                for (var p = width * 3; p < rowSize; p++)
                {
                    writer.Write((byte)0);
                }
            }

            writer.Flush();
            return stream.ToArray();
        }

        // A PNG whose header claims a huge size, to check the pixel limit without allocating it
        public static byte[] HugePngHeader(int width, int height)
        {
            var png = Png(1, 1);
            var copy = (byte[])png.Clone();
            WriteBigEndian(copy, 16, width);
            WriteBigEndian(copy, 20, height);

            // IHDR is "IHDR" + 13 data bytes at offset 12, followed by its CRC
            WriteBigEndian(copy, 29, (int)Crc32(copy, 12, 17));
            return copy;
        }

        private static uint Crc32(byte[] data, int offset, int length)
        {
            var crc = 0xFFFFFFFFu;
            for (var i = offset; i < offset + length; i++)
            {
                crc ^= data[i];
                for (var bit = 0; bit < 8; bit++)
                {
                    crc = (crc & 1) != 0 ? (crc >> 1) ^ 0xEDB88320u : crc >> 1;
                }
            }

            return ~crc;
        }

        private static void WriteBigEndian(byte[] buffer, int offset, int value)
        {
            buffer[offset] = (byte)(value >> 24);
            buffer[offset + 1] = (byte)(value >> 16);
            buffer[offset + 2] = (byte)(value >> 8);
            buffer[offset + 3] = (byte)value;
        }
    }
}
