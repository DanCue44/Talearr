using System;
using System.Collections.Concurrent;
using System.IO;
using System.Linq;
using NLog;
using NzbDrone.Common.Disk;
using NzbDrone.Common.Http;
using NzbDrone.Core.MediaFiles;

namespace NzbDrone.Core.Talearr.Metadata
{
    public interface ITalearrSidecarCoverWriter
    {
        // Downloads candidates and decides whether to write; nothing on disk changes.
        TalearrCoverDecision Decide(TalearrSidecarImage image, string fullPath);

        // Writes the chosen image to fullPath (through a temporary file). True when written.
        bool Save(TalearrCoverDecision decision, string fullPath);
    }

    // Talearr: downloads and writes cover sidecars chosen by TalearrSidecarCover. Hooked from
    // MetadataService.ProcessBookImages, after upstream's own checks (an existing cover that this
    // provider did not write, or that was changed since, is never touched).
    public class TalearrSidecarCoverWriter : ITalearrSidecarCoverWriter
    {
        // A book with only thumbnails is not downloaded again on every author scan.
        private static readonly TimeSpan NegativeCacheTime = TimeSpan.FromHours(12);
        private static readonly TimeSpan DownloadTimeout = TimeSpan.FromSeconds(30);

        private readonly IHttpClient _httpClient;
        private readonly IDiskProvider _diskProvider;
        private readonly IMediaFileAttributeService _mediaFileAttributeService;
        private readonly Logger _logger;
        private readonly ConcurrentDictionary<string, (DateTime Until, TalearrCoverDecision Decision)> _negative = new(StringComparer.OrdinalIgnoreCase);

        public TalearrSidecarCoverWriter(IHttpClient httpClient,
                                         IDiskProvider diskProvider,
                                         IMediaFileAttributeService mediaFileAttributeService,
                                         Logger logger)
        {
            _httpClient = httpClient;
            _diskProvider = diskProvider;
            _mediaFileAttributeService = mediaFileAttributeService;
            _logger = logger;
        }

        public TalearrCoverDecision Decide(TalearrSidecarImage image, string fullPath)
        {
            var cacheKey = fullPath + "|" + image.SourceKey;
            if (_negative.TryGetValue(cacheKey, out var cached) && cached.Until > DateTime.UtcNow)
            {
                return cached.Decision;
            }

            var existing = ReadExistingSize(fullPath);
            var decision = TalearrSidecarCover.Choose(image.Candidates, Fetch, existing);

            switch (decision.Verdict)
            {
                case TalearrCoverVerdict.Write:
                    _logger.Debug("Talearr: cover for '{0}': {1}", image.BookTitle, decision.Reason);
                    _negative.TryRemove(cacheKey, out _);
                    break;
                case TalearrCoverVerdict.KeepExisting:
                    _logger.Debug("Talearr: keeping the cover in {0} for '{1}': {2}", fullPath, image.BookTitle, decision.Reason);
                    break;
                default:
                    _logger.Info("Talearr: no cover written for '{0}': {1} ({2} candidates, {3} downloaded)",
                        image.BookTitle, decision.Reason, image.Candidates.Count, decision.Downloads);
                    _negative[cacheKey] = (DateTime.UtcNow.Add(NegativeCacheTime), decision);
                    break;
            }

            return decision;
        }

        public bool Save(TalearrCoverDecision decision, string fullPath)
        {
            if (decision?.Verdict != TalearrCoverVerdict.Write || decision.Image == null)
            {
                return false;
            }

            var temporaryPath = fullPath + ".partial~";

            try
            {
                var bytes = MatchExtension(decision.Image.Data, fullPath);

                using (var stream = _diskProvider.OpenWriteStream(temporaryPath))
                {
                    stream.Write(bytes, 0, bytes.Length);
                }

                _diskProvider.MoveFile(temporaryPath, fullPath, true);
                _mediaFileAttributeService.SetFilePermissions(fullPath);

                return true;
            }
            catch (Exception ex)
            {
                _logger.Warn(ex, "Talearr: couldn't write cover {0}", fullPath);

                try
                {
                    if (_diskProvider.FileExists(temporaryPath))
                    {
                        _diskProvider.DeleteFile(temporaryPath);
                    }
                }
                catch (Exception)
                {
                    // Best effort; disk scans skip *.partial~ files.
                }

                return false;
            }
        }

        private byte[] Fetch(string url)
        {
            var request = new HttpRequest(url)
            {
                RequestTimeout = DownloadTimeout,
                AllowAutoRedirect = true
            };

            return _httpClient.Get(request).ResponseData;
        }

        private (int Width, int Height)? ReadExistingSize(string fullPath)
        {
            try
            {
                if (!_diskProvider.FileExists(fullPath))
                {
                    return null;
                }

                using (var stream = _diskProvider.OpenReadStream(fullPath))
                using (var memory = new MemoryStream())
                {
                    stream.CopyTo(memory);
                    return TalearrSidecarCover.ReadSize(memory.ToArray());
                }
            }
            catch (Exception ex)
            {
                _logger.Debug(ex, "Talearr: couldn't read the existing cover {0}", fullPath);
                return null;
            }
        }

        // cover.jpg must hold a JPEG (Audiobookshelf and players go by the extension); re-encode
        // when the chosen image is in another format.
        internal static byte[] MatchExtension(byte[] data, string fullPath)
        {
            var extension = Path.GetExtension(fullPath)?.TrimStart('.');
            if (string.IsNullOrWhiteSpace(extension))
            {
                return data;
            }

            var format = NzbDrone.Core.MediaCover.CoverImageFormats.DetectFormat(data);
            if (format == null || NzbDrone.Core.MediaCover.CoverImageFormats.FileExtensions(format).Contains(extension, StringComparer.OrdinalIgnoreCase))
            {
                return data;
            }

            var target = NzbDrone.Core.MediaCover.CoverImageFormats.FormatForExtension(extension);
            if (!NzbDrone.Core.MediaCover.CoverImageFormats.CanWrite(target))
            {
                return data;
            }

            using (var image = NzbDrone.Core.MediaCover.CoverImageFormats.Decode(data))
            {
                return NzbDrone.Core.MediaCover.CoverImageFormats.Encode(image, target);
            }
        }
    }
}
