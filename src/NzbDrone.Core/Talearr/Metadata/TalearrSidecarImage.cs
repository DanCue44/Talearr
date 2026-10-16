using System.Collections.Generic;
using System.Linq;
using NzbDrone.Core.Extras.Metadata.Files;

namespace NzbDrone.Core.Talearr.Metadata
{
    // TaleArr: a cover sidecar with every candidate image, not just one URL. MetadataService hands it
    // to ITalearrSidecarCoverWriter, which picks the image by size (TalearrSidecarCover). Url stays
    // the first candidate so upstream's plain download still works without the writer.
    public class TalearrSidecarImage : ImageFileResult
    {
        // Bump when the selection rules change, so covers written under the old rules are looked at
        // once more (never replaced by a smaller image).
        public const string RulesVersion = "talearr-cover-v1";

        public TalearrSidecarImage(string relativePath, List<TalearrCoverCandidate> candidates, string bookTitle, bool overwriteExisting = true)
            : base(relativePath, candidates?.FirstOrDefault()?.Url, overwriteExisting)
        {
            Candidates = candidates ?? new List<TalearrCoverCandidate>();
            BookTitle = bookTitle;
        }

        public List<TalearrCoverCandidate> Candidates { get; }
        public string BookTitle { get; }

        // What MetadataService remembers as the image's source: the rules version and every
        // candidate URL. A new candidate (an edition gains a cover) makes TaleArr look again.
        public string SourceKey => RulesVersion + "|" + string.Join("|", Candidates.Select(c => c.Url));

        public static string SourceKeyOf(ImageFileResult image)
        {
            return image is TalearrSidecarImage sidecar ? sidecar.SourceKey : image.Url;
        }
    }
}
