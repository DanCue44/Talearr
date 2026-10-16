using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using NzbDrone.Core.DecisionEngine.Specifications;

namespace NzbDrone.Core.DecisionEngine
{
    // TaleArr: short, readable labels for release rejections, shown next to the reason in
    // interactive search ("Language", "Wrong book", "Pack", "Size", "Blocklisted", ...).
    //
    // The label is display-only. It never changes Rejection.Category, which the search
    // filters and the grab logic read. A specification that sets its own category through
    // Decision.RejectHardFilter/RejectSoftFilter gets that category as its label; one that
    // uses plain Decision.Reject (category "General") gets a label from its class name, so a
    // new specification is labelled without having to be registered here.
    public static class TalearrRejectionLabels
    {
        public const string DefaultLabel = "Rejected";

        private const string GeneralCategory = "General";

        private static readonly Dictionary<string, string> CategoryLabels = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            { "Matching", "Wrong book" },
            { "Title", "Wrong book" },
            { "Author", "Wrong author" },
            { "Parsing", "Unparsed" },
            { "Pack", "Pack" },
            { "Language", "Language" },
            { "Quality", "Quality" },
            { "Format", "Format" },
            { "Indexer", "Indexer" },
            { "Release Profile", "Release profile" },
            { "Error", "Error" }
        };

        private static readonly Dictionary<string, string> SourceLabels = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            { "AcceptableSizeSpecification", "Size" },
            { "MaximumSizeSpecification", "Size" },
            { "AlreadyImportedSpecification", "Already imported" },
            { "BlockedIndexerSpecification", "Indexer unavailable" },
            { "BlocklistSpecification", "Blocklisted" },
            { "CustomFormatAllowedByProfileSpecification", "Custom format score" },
            { "CutoffSpecification", "Cutoff met" },
            { "DiscographySpecification", "Pack" },
            { "MultiBookReleaseSpecification", "Pack" },
            { "EarlyReleaseSpecification", "Early release" },
            { "EbookFormatSpecification", "Format" },
            { "RawDiskSpecification", "Format" },
            { "MinimumAgeSpecification", "Minimum age" },
            { "MonitoredMediaTypeSpecification", "Not monitored" },
            { "MonitoredBookSpecification", "Not monitored" },
            { "MyAnonaMouseVipAccessSpecification", "MyAnonaMouse" },
            { "MyAnonaMouseUnsatisfiedSlotSpecification", "MyAnonaMouse" },
            { "NotSampleSpecification", "Sample" },
            { "ProtocolSpecification", "Protocol" },
            { "QualityAllowedByProfileSpecification", "Quality" },
            { "QueueSpecification", "Already queued" },
            { "ReleaseLanguageSpecification", "Language" },
            { "ReleaseRestrictionsSpecification", "Release profile" },
            { "ReleaseTitleMatchSpecification", "Wrong book" },
            { "BookRequestedSpecification", "Wrong book" },
            { "SingleBookSearchMatchSpecification", "Wrong book" },
            { "AuthorSpecification", "Wrong author" },
            { "RepackSpecification", "Repack" },
            { "ProperSpecification", "Proper" },
            { "RetentionSpecification", "Retention" },
            { "SupportedReleaseFileTypeSpecification", "File type" },
            { "TorrentSeedingSpecification", "Seeders" },
            { "UpgradableSpecification", "Not an upgrade" },
            { "UpgradeAllowedSpecification", "Not an upgrade" },
            { "UpgradeDiskSpecification", "Not an upgrade" },
            { "DelaySpecification", "Delayed" },
            { "DeletedBookFileSpecification", "Deleted file" },
            { "HistorySpecification", "Already grabbed" },
            { "IndexerTagSpecification", "Indexer tags" }
        };

        // Used only when neither the category nor the source says anything.
        private static readonly (Regex Pattern, string Label)[] ReasonHints =
        {
            (new Regex(@"\babridged\b", RegexOptions.IgnoreCase | RegexOptions.Compiled), "Abridged"),
            (new Regex(@"\bblock ?listed\b|\bblocklist\b", RegexOptions.IgnoreCase | RegexOptions.Compiled), "Blocklisted"),
            (new Regex(@"\blanguage\b", RegexOptions.IgnoreCase | RegexOptions.Compiled), "Language"),
            (new Regex(@"\b(multiple books|box ?set|omnibus|bundle)\b", RegexOptions.IgnoreCase | RegexOptions.Compiled), "Pack"),
            (new Regex(@"\bsize\b", RegexOptions.IgnoreCase | RegexOptions.Compiled), "Size")
        };

        private static readonly Regex CamelCaseBoundary = new Regex(@"(?<=[a-z0-9])(?=[A-Z])|(?<=[A-Z])(?=[A-Z][a-z])", RegexOptions.Compiled);

        public static Rejection FromSpecification(IDecisionEngineSpecification spec, Decision result)
        {
            return new Rejection(result.Reason, spec.Type, result.CanBypass, result.Category, result.Severity)
            {
                Source = spec.GetType().Name
            };
        }

        public static string GetLabel(Rejection rejection)
        {
            if (rejection == null)
            {
                return DefaultLabel;
            }

            var category = rejection.Category?.Trim();
            if (!string.IsNullOrEmpty(category) && !category.Equals(GeneralCategory, StringComparison.OrdinalIgnoreCase))
            {
                return CategoryLabels.TryGetValue(category, out var categoryLabel) ? categoryLabel : category;
            }

            var source = rejection.Source?.Trim();
            if (!string.IsNullOrEmpty(source))
            {
                return SourceLabels.TryGetValue(source, out var sourceLabel) ? sourceLabel : HumanizeSource(source);
            }

            var reason = rejection.Reason ?? string.Empty;
            foreach (var (pattern, label) in ReasonHints)
            {
                if (pattern.IsMatch(reason))
                {
                    return label;
                }
            }

            return DefaultLabel;
        }

        // "UnabridgedPreferenceSpecification" -> "Unabridged preference"
        public static string HumanizeSource(string source)
        {
            if (string.IsNullOrWhiteSpace(source))
            {
                return DefaultLabel;
            }

            var name = source.Trim();
            if (name.EndsWith("Specification", StringComparison.Ordinal) && name.Length > "Specification".Length)
            {
                name = name.Substring(0, name.Length - "Specification".Length);
            }

            var words = CamelCaseBoundary.Split(name);
            for (var i = 1; i < words.Length; i++)
            {
                // Keep acronyms (MAM, ASIN) as they are; lower-case ordinary words.
                if (words[i].Length > 1 && words[i].ToUpperInvariant() == words[i])
                {
                    continue;
                }

                words[i] = words[i].ToLowerInvariant();
            }

            return string.Join(" ", words);
        }
    }
}
