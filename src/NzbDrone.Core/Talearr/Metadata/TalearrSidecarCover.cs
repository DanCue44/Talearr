using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using NzbDrone.Core.Books;
using NzbDrone.Core.MediaCover;
using SixLabors.ImageSharp;
using MediaCoverModel = NzbDrone.Core.MediaCover.MediaCover;

namespace NzbDrone.Core.Talearr.Metadata
{
    public enum TalearrCoverSource
    {
        Edition = 0,
        Book = 1,
        SameRecording = 2
    }

    public sealed class TalearrCoverCandidate
    {
        public TalearrCoverCandidate(string url, TalearrCoverSource source, bool fullSize, string listedUrl)
        {
            Url = url;
            Source = source;
            FullSize = fullSize;
            ListedUrl = listedUrl;
        }

        public string Url { get; }
        public TalearrCoverSource Source { get; }

        // True when Url is the full-size form of a listed thumbnail URL (ListedUrl).
        public bool FullSize { get; }
        public string ListedUrl { get; }
        public bool IsAudible => TalearrSidecarCover.IsAudibleImage(Url);

        public override string ToString() => $"{Source}{(FullSize ? " full size" : string.Empty)} {Url}";
    }

    public sealed class TalearrCoverImage
    {
        public TalearrCoverImage(TalearrCoverCandidate candidate, byte[] data, int width, int height)
        {
            Candidate = candidate;
            Data = data;
            Width = width;
            Height = height;
        }

        public TalearrCoverCandidate Candidate { get; }
        public byte[] Data { get; }
        public int Width { get; }
        public int Height { get; }
        public int ShortSide => Math.Min(Width, Height);
        public long Area => (long)Width * Height;
    }

    public enum TalearrCoverVerdict
    {
        // Write Image to the sidecar path.
        Write = 0,

        // The cover already in the folder is at least as large as the best candidate.
        KeepExisting = 1,

        // Every candidate failed to download or was not an image.
        NoUsableImage = 2,

        // The best candidate is a thumbnail; writing it would hide better artwork from
        // Audiobookshelf (embedded art, its own match).
        TooSmall = 3
    }

    public sealed class TalearrCoverDecision
    {
        public TalearrCoverVerdict Verdict { get; set; }
        public TalearrCoverImage Image { get; set; }
        public int Downloads { get; set; }
        public string Reason { get; set; }
    }

    // Talearr: chooses the image written as cover.jpg next to a book.
    //
    // Upstream wrote the first cover URL of the matched edition, whatever its size. For the 1984
    // import on 2026-10-16 that was a Goodreads thumbnail (`9834606._SX50_.jpg`, 50x67, 1.5 KB) and
    // Audiobookshelf showed it as the book's cover. Now:
    //   - candidates are the edition's covers, the book's covers and the covers of other editions of
    //     the same recording (same narrator for audiobooks, same ISBN for ebooks), each also in its
    //     full-size form when the URL carries a Goodreads or Amazon size token (`._SX50_`, `._SY75_`,
    //     `._SL500_`), which is tried first;
    //   - an Audible image or one of the edition's own of at least 500 px on the short side wins at
    //     once; otherwise the largest image found is used, so nothing under 300 px is written while a
    //     larger one is available;
    //   - a thumbnail (under 150 px) is never written, and an existing cover in the folder is never
    //     replaced by a smaller image.
    public static class TalearrSidecarCover
    {
        public const int PreferredShortSide = 500;
        public const int MinimumShortSide = 300;
        public const int ThumbnailShortSide = 150;
        public const int MaxDownloads = 6;

        // Goodreads and Amazon put the size in the file name: "9834606._SX50_.jpg",
        // "52163480._SX50_SY75_.jpg", "612oBD9OSjL._SL500_.jpg", "51abc._AC_SY400_.jpg". Without it
        // the CDN returns the stored original.
        private static readonly Regex SizeTokenRegex = new Regex(
            @"\._(?:[A-Z]{2}[A-Z0-9,]*_)+(?=\.[A-Za-z]{3,4}$)",
            RegexOptions.Compiled);

        private static readonly string[] SizeTokenHosts =
        {
            "gr-assets.com",
            "goodreads.com",
            "media-amazon.com",
            "ssl-images-amazon.com",
            "images-amazon.com"
        };

        private static readonly string[] AudibleHosts =
        {
            "media-amazon.com",
            "ssl-images-amazon.com",
            "images-amazon.com",
            "audible.com"
        };

        public static List<TalearrCoverCandidate> BuildCandidates(Book book, Edition edition, IEnumerable<Edition> otherEditions, bool isEbook)
        {
            var candidates = new List<TalearrCoverCandidate>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            Add(candidates, seen, edition?.Images, TalearrCoverSource.Edition);
            Add(candidates, seen, book?.Images, TalearrCoverSource.Book);

            if (edition != null && otherEditions != null)
            {
                var sameRecording = otherEditions
                    .Where(e => e != null && e.Id != edition.Id && IsSameRecording(edition, e, isEbook))
                    .OrderByDescending(e => !string.IsNullOrWhiteSpace(e.AudibleASIN))
                    .ThenBy(e => e.Id);

                foreach (var other in sameRecording)
                {
                    Add(candidates, seen, other.Images, TalearrCoverSource.SameRecording);
                }
            }

            return candidates;
        }

        public static bool IsSameRecording(Edition edition, Edition other, bool isEbook)
        {
            if (isEbook)
            {
                return other.IsEbook &&
                       !string.IsNullOrWhiteSpace(edition.Isbn13) &&
                       string.Equals(edition.Isbn13.Trim(), other.Isbn13?.Trim(), StringComparison.OrdinalIgnoreCase);
            }

            if (!IsAudio(other))
            {
                return false;
            }

            var narrators = NarratorKeys(edition);
            return narrators.Count > 0 && NarratorKeys(other).Overlaps(narrators);
        }

        // The full-size form of a Goodreads or Amazon thumbnail URL, or null when the URL has no
        // size token.
        public static string FullSizeUrl(string url)
        {
            if (string.IsNullOrWhiteSpace(url) || !Uri.TryCreate(url, UriKind.Absolute, out var uri))
            {
                return null;
            }

            if (!SizeTokenHosts.Any(h => HostIs(uri.Host, h)))
            {
                return null;
            }

            var path = uri.AbsolutePath;
            var fullPath = SizeTokenRegex.Replace(path, string.Empty);
            if (fullPath == path)
            {
                return null;
            }

            var builder = new UriBuilder(uri) { Path = fullPath };
            return builder.Uri.AbsoluteUri;
        }

        public static bool IsAudibleImage(string url)
        {
            return Uri.TryCreate(url ?? string.Empty, UriKind.Absolute, out var uri) &&
                   AudibleHosts.Any(h => HostIs(uri.Host, h));
        }

        public static bool IsPreferred(TalearrCoverImage image)
        {
            return image != null &&
                   image.ShortSide >= PreferredShortSide &&
                   (image.Candidate.IsAudible || image.Candidate.Source == TalearrCoverSource.Edition);
        }

        // Downloads candidates in order (at most MaxDownloads), stopping at the first preferred image,
        // and decides what to write. `existing` is the size of the cover already in the folder, if any.
        public static TalearrCoverDecision Choose(IReadOnlyList<TalearrCoverCandidate> candidates,
                                                  Func<string, byte[]> fetch,
                                                  (int Width, int Height)? existing)
        {
            var images = new List<TalearrCoverImage>();
            var fullSizeFound = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var downloads = 0;

            foreach (var candidate in candidates ?? Array.Empty<TalearrCoverCandidate>())
            {
                if (downloads >= MaxDownloads)
                {
                    break;
                }

                // The listed thumbnail is the same picture, smaller; skip it once the full size worked.
                if (!candidate.FullSize && fullSizeFound.Contains(candidate.Url))
                {
                    continue;
                }

                if (MediaCoverRendition.IsKnownPlaceholderImageUrl(candidate.Url))
                {
                    continue;
                }

                byte[] data;
                try
                {
                    downloads++;
                    data = fetch(candidate.Url);
                }
                catch (Exception)
                {
                    continue;
                }

                if (data == null || data.Length == 0 || MediaCoverRendition.InspectDownloadedImage(candidate.Url, data, out _))
                {
                    continue;
                }

                var size = ReadSize(data);
                if (size == null)
                {
                    continue;
                }

                var image = new TalearrCoverImage(candidate, data, size.Value.Width, size.Value.Height);
                images.Add(image);

                if (candidate.FullSize)
                {
                    fullSizeFound.Add(candidate.ListedUrl);
                }

                if (IsPreferred(image))
                {
                    break;
                }
            }

            var decision = new TalearrCoverDecision { Downloads = downloads };
            var best = PickBest(images);

            if (best == null)
            {
                decision.Verdict = TalearrCoverVerdict.NoUsableImage;
                decision.Reason = "no candidate could be downloaded as an image";
                return decision;
            }

            decision.Image = best;

            if (best.ShortSide < ThumbnailShortSide)
            {
                decision.Verdict = TalearrCoverVerdict.TooSmall;
                decision.Reason = $"largest image is a {best.Width}x{best.Height} thumbnail";
                return decision;
            }

            if (existing.HasValue && IsSmaller(best, existing.Value))
            {
                decision.Verdict = TalearrCoverVerdict.KeepExisting;
                decision.Reason = $"existing cover {existing.Value.Width}x{existing.Value.Height} is larger than {best.Width}x{best.Height}";
                return decision;
            }

            decision.Verdict = TalearrCoverVerdict.Write;
            decision.Reason = $"{best.Width}x{best.Height} from {best.Candidate}";
            return decision;
        }

        // The image to write: the first preferred image in candidate order, else the largest.
        public static TalearrCoverImage PickBest(IEnumerable<TalearrCoverImage> images)
        {
            var list = (images ?? Enumerable.Empty<TalearrCoverImage>()).Where(i => i != null).ToList();

            var preferred = list.FirstOrDefault(IsPreferred);
            if (preferred != null)
            {
                return preferred;
            }

            return list
                .Select((image, index) => (image, index))
                .OrderByDescending(x => x.image.ShortSide)
                .ThenByDescending(x => x.image.Area)
                .ThenBy(x => x.index)
                .Select(x => x.image)
                .FirstOrDefault();
        }

        public static bool IsSmaller(TalearrCoverImage image, (int Width, int Height) existing)
        {
            var existingShort = Math.Min(existing.Width, existing.Height);
            if (image.ShortSide != existingShort)
            {
                return image.ShortSide < existingShort;
            }

            return image.Area < (long)existing.Width * existing.Height;
        }

        public static (int Width, int Height)? ReadSize(byte[] data)
        {
            if (data == null || data.Length == 0)
            {
                return null;
            }

            try
            {
                var info = Image.Identify(data);
                return info == null || info.Width <= 0 || info.Height <= 0 ? null : (info.Width, info.Height);
            }
            catch (Exception)
            {
                return null;
            }
        }

        private static void Add(List<TalearrCoverCandidate> candidates, HashSet<string> seen, IEnumerable<MediaCoverModel> images, TalearrCoverSource source)
        {
            if (images == null)
            {
                return;
            }

            foreach (var cover in images.Where(c => c != null && c.CoverType == MediaCoverTypes.Cover))
            {
                var url = RemoteUrl(cover);
                if (url == null)
                {
                    continue;
                }

                var fullSize = FullSizeUrl(url);
                if (fullSize != null && seen.Add(fullSize))
                {
                    candidates.Add(new TalearrCoverCandidate(fullSize, source, true, url));
                }

                if (seen.Add(url))
                {
                    candidates.Add(new TalearrCoverCandidate(url, source, false, url));
                }
            }
        }

        private static string RemoteUrl(MediaCoverModel cover)
        {
            foreach (var url in new[] { cover.Url, cover.RemoteUrl })
            {
                if (!string.IsNullOrWhiteSpace(url) &&
                    (url.StartsWith("http://", StringComparison.OrdinalIgnoreCase) || url.StartsWith("https://", StringComparison.OrdinalIgnoreCase)))
                {
                    return url.Trim();
                }
            }

            return null;
        }

        private static bool IsAudio(Edition edition)
        {
            return !edition.IsEbook &&
                   (edition.ReadingFormatId == 2 ||
                    (edition.Format ?? string.Empty).Contains("audio", StringComparison.OrdinalIgnoreCase));
        }

        private static HashSet<string> NarratorKeys(Edition edition)
        {
            var names = (edition.Narrator ?? string.Empty)
                .Split(new[] { ',', ';', '&' }, StringSplitOptions.RemoveEmptyEntries)
                .Concat(edition.NarratorNames ?? new List<string>());

            return new HashSet<string>(TalearrSidecarText.Names(names), StringComparer.OrdinalIgnoreCase);
        }

        private static bool HostIs(string host, string domain)
        {
            return host.Equals(domain, StringComparison.OrdinalIgnoreCase) ||
                   host.EndsWith("." + domain, StringComparison.OrdinalIgnoreCase);
        }
    }
}
