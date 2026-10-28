using System;
using System.IO;
using NzbDrone.Common.Disk;
using NzbDrone.Common.EnvironmentInfo;

namespace NzbDrone.Core.MediaCover
{
    public interface IImageResizer
    {
        void Resize(string source, string destination, int height);
    }

    public class ImageResizer : IImageResizer
    {
        private readonly IDiskProvider _diskProvider;
        private readonly bool _enabled;

        public ImageResizer(IDiskProvider diskProvider, IPlatformInfo platformInfo)
        {
            _diskProvider = diskProvider;

            _enabled = true;
        }

        public void Resize(string source, string destination, int height)
        {
            if (!_enabled)
            {
                return;
            }

            try
            {
                // The format follows the destination extension. The resizer is shared by author,
                // narrator, and book covers; writing JPEG bytes into a .png/.webp filename
                // produces invalid HTTP content. GIF and BMP can't be written, so those throw and
                // the caller keeps serving the full-size original.
                var format = CoverImageFormats.FormatForExtension(Path.GetExtension(destination));
                if (!CoverImageFormats.CanWrite(format))
                {
                    throw new NotSupportedException($"Resized covers can't be written as '{Path.GetExtension(destination)}'");
                }

                using var image = CoverImageFormats.Decode(File.ReadAllBytes(source));
                using var resized = CoverImageFormats.ResizeToHeight(image, height);
                File.WriteAllBytes(destination, CoverImageFormats.Encode(resized, format));
            }
            catch
            {
                if (_diskProvider != null && _diskProvider.FileExists(destination))
                {
                    _diskProvider.DeleteFile(destination);
                }
                else if (_diskProvider == null && File.Exists(destination))
                {
                    File.Delete(destination);
                }

                throw;
            }
        }
    }
}
