using System;
using System.Collections.Generic;
using System.Linq;
using NzbDrone.Core.Indexers;
using NzbDrone.Core.Parser.Model;
using NzbDrone.Core.Talearr.Comics.Parsing;

namespace NzbDrone.Core.Talearr.Comics.Search
{
    public class ComicReleaseDecision
    {
        public ReleaseInfo Release { get; set; }
        public ParsedComicRelease Parsed { get; set; }
        public List<string> Rejections { get; set; } = new List<string>();
        public List<int> VolumeIds { get; set; } = new List<int>();
        public List<decimal> VolumeNumbers { get; set; } = new List<decimal>();
        public int Score { get; set; }

        public bool Accepted => Rejections.Count == 0;
    }

    public class ComicEvaluationContext
    {
        public ComicSeries Series { get; set; }

        // Every volume Talearr knows for the series.
        public List<ComicVolume> Volumes { get; set; } = new List<ComicVolume>();

        // The volumes this search is for.
        public HashSet<int> WantedVolumeIds { get; set; } = new HashSet<int>();
        public ComicSettings Settings { get; set; } = new ComicSettings();
        public HashSet<string> BlocklistedTitles { get; set; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    }

    // Talearr: decides whether a release is a wanted volume of a series, and how good it is.
    // Rejections are short and plain, because the interactive search shows them.
    public static class ComicReleaseEvaluator
    {
        public const long MegaByte = 1024 * 1024;

        public static ComicReleaseDecision Evaluate(ReleaseInfo release, ComicEvaluationContext context)
        {
            var series = context.Series;
            var settings = context.Settings;
            var hint = ComicReleaseHints.Get(release);
            var parsed = ComicReleaseParser.Parse(hint?.ParseTitle ?? release.Title, series.AllTitles());
            hint?.ApplyTo(parsed);
            var decision = new ComicReleaseDecision { Release = release, Parsed = parsed };
            var reject = decision.Rejections;

            if (!string.IsNullOrWhiteSpace(hint?.Rejection))
            {
                reject.Add(hint.Rejection);
                return decision;
            }

            if (!TitleMatches(parsed, series))
            {
                reject.Add($"Different series ({parsed.SeriesTitle ?? "unreadable title"})");
                return decision;
            }

            if (!string.IsNullOrWhiteSpace(parsed.ExtraTitleWords) && parsed.Edition == null)
            {
                reject.Add($"Different series ({parsed.SeriesTitle} {parsed.ExtraTitleWords})");
            }

            if (parsed.Language != null && !string.Equals(parsed.Language, series.LanguageIso ?? "en", StringComparison.OrdinalIgnoreCase))
            {
                reject.Add($"Not in English ({parsed.Language})");
            }

            if (parsed.IsScanlation)
            {
                reject.Add("Fan translation (scanlation), not an official release");
            }

            if (parsed.IsAdultHint && !series.IsAdult)
            {
                reject.Add("Adult fan work, not the series");
            }

            if (parsed.IsAnnual)
            {
                reject.Add("Annual, not a volume");
            }

            CheckEdition(parsed, series, reject);

            var range = ReadVolumeRange(parsed, release, context, reject);
            if (range.HasValue)
            {
                var (start, end) = range.Value;
                var covered = context.Volumes.Where(v => v.Number >= start && v.Number <= end).ToList();
                var wanted = covered.Where(v => context.WantedVolumeIds.Contains(v.Id)).ToList();

                if (wanted.Count == 0)
                {
                    reject.Add(end > start ? $"Volumes {start}-{end} are not wanted" : $"Volume {start} is not wanted");
                }
                else if (end > start && !settings.AllowPacks)
                {
                    reject.Add($"Holds volumes {start}-{end}; packs are switched off");
                }

                decision.VolumeIds = wanted.Select(v => v.Id).ToList();
                decision.VolumeNumbers = wanted.Select(v => v.Number).ToList();

                var minimum = settings.MinimumVolumeSizeMb * MegaByte;
                var volumesInRelease = Math.Max(1, covered.Count);
                if (release.Size > 0 && minimum > 0 && release.Size / volumesInRelease < minimum)
                {
                    reject.Add($"Too small for a volume ({release.Size / MegaByte} MB); probably a chapter");
                }
            }

            var source = parsed.Source;
            if (source == ComicSources.Digital && !settings.AllowDigital)
            {
                reject.Add("Digital releases are switched off");
            }
            else if (source == ComicSources.C2C && !settings.AllowC2C)
            {
                reject.Add("c2c scans are switched off");
            }
            else if ((source == ComicSources.Scan || source == ComicSources.Unknown) && !settings.AllowScan)
            {
                reject.Add(source == ComicSources.Scan ? "Scans are switched off" : "Not marked as digital, and scans are switched off");
            }

            if (context.BlocklistedTitles.Contains(release.Title))
            {
                reject.Add("Blocklisted");
            }

            if (release is TorrentInfo torrent && torrent.Seeders.HasValue && torrent.Seeders.Value == 0)
            {
                reject.Add("No seeders");
            }

            decision.Score = Score(parsed, decision, settings);
            return decision;
        }

        // Orders accepted releases best first, then picks the fewest that cover every wanted
        // volume (a pack that holds several wanted volumes beats one release per volume).
        public static List<ComicReleaseDecision> PickBest(IEnumerable<ComicReleaseDecision> decisions)
        {
            var picked = new List<ComicReleaseDecision>();
            var covered = new HashSet<int>();

            foreach (var decision in Prioritize(decisions.Where(d => d.Accepted)))
            {
                if (decision.VolumeIds.Any(id => !covered.Contains(id)))
                {
                    picked.Add(decision);
                    covered.UnionWith(decision.VolumeIds);
                }
            }

            return picked;
        }

        public static IEnumerable<ComicReleaseDecision> Prioritize(IEnumerable<ComicReleaseDecision> decisions)
        {
            return decisions
                .OrderByDescending(d => d.Score)
                .ThenByDescending(d => (d.Release as TorrentInfo)?.Seeders ?? 0)
                .ThenBy(d => d.Release.IndexerPriority)
                .ThenByDescending(d => d.Release.PublishDate);
        }

        public static int SourceRank(string source)
        {
            switch (source)
            {
                case ComicSources.Digital:
                    return 300;
                case ComicSources.Scan:
                    return 200;
                case ComicSources.C2C:
                    return 100;
                default:
                    return 150;
            }
        }

        private static int Score(ParsedComicRelease parsed, ComicReleaseDecision decision, ComicSettings settings)
        {
            var score = SourceRank(parsed.Source);

            switch (parsed.Format)
            {
                case ComicFormats.Cbz:
                    score += settings.PreferCbz ? 40 : 30;
                    break;
                case ComicFormats.Cbr:
                    score += 30;
                    break;
                case ComicFormats.Cb7:
                    score += 25;
                    break;
                case ComicFormats.Pdf:
                case ComicFormats.Epub:
                    score += 10;
                    break;
                default:
                    score += 20;
                    break;
            }

            if (parsed.IsFixed)
            {
                score += 5;
            }

            // More wanted volumes in one release is better, up to a point.
            score += Math.Min(decision.VolumeIds.Count, 20) * 2;

            return score;
        }

        private static bool TitleMatches(ParsedComicRelease parsed, ComicSeries series)
        {
            if (parsed.MatchedKnownTitle)
            {
                return true;
            }

            var normalized = ComicReleaseParser.NormalizeTitle(parsed.SeriesTitle);
            return normalized.Length > 0 && series.AllTitles().Any(t => ComicReleaseParser.NormalizeTitle(t) == normalized);
        }

        private static void CheckEdition(ParsedComicRelease parsed, ComicSeries series, List<string> reject)
        {
            var seriesEdition = series.AllTitles()
                .Select(t => ComicReleaseParser.Parse(t).Edition)
                .FirstOrDefault(e => e != null);

            if (parsed.Edition != null && seriesEdition == null)
            {
                reject.Add($"Different edition ({parsed.Edition}); its volume numbers do not match");
            }
            else if (seriesEdition != null && parsed.Edition != seriesEdition)
            {
                reject.Add(parsed.Edition == null ? $"Not the {seriesEdition} edition" : $"Different edition ({parsed.Edition})");
            }
        }

        private static (decimal Start, decimal End)? ReadVolumeRange(ParsedComicRelease parsed, ReleaseInfo release, ComicEvaluationContext context, List<string> reject)
        {
            var series = context.Series;
            var maxKnown = context.Volumes.Count > 0 ? context.Volumes.Max(v => v.Number) : 0;
            var totalVolumes = Math.Max(series.TotalVolumes ?? 0, (int)maxKnown);

            switch (parsed.NumberKind)
            {
                case ComicNumberKind.Chapter:
                    reject.Add("Single chapter, not a volume");
                    return null;

                case ComicNumberKind.Issue:
                    reject.Add("Single issue; Talearr gets volumes for now");
                    return null;

                case ComicNumberKind.None:
                    // A one-shot: the whole series is one book.
                    if (context.Volumes.Count == 1)
                    {
                        return (context.Volumes[0].Number, context.Volumes[0].Number);
                    }

                    reject.Add("No volume number");
                    return null;

                case ComicNumberKind.Bare:
                    // Manga chapters are often numbered with no "c": 211 is a chapter when the
                    // series has 26 volumes, and chapters are a few MB where volumes are 50+.
                    var number = parsed.Number.Value;
                    var small = release.Size > 0 && release.Size < Math.Max(1, context.Settings.MinimumVolumeSizeMb) * MegaByte;
                    if (series.Kind == ComicKinds.Comic)
                    {
                        reject.Add("Single issue; Talearr gets volumes for now");
                        return null;
                    }

                    if ((totalVolumes > 0 && number > totalVolumes) || small)
                    {
                        reject.Add($"Looks like chapter {number}, not a volume");
                        return null;
                    }

                    return (number, parsed.NumberEnd ?? number);

                default:
                    var start = parsed.Number.Value;
                    return (start, parsed.NumberEnd.HasValue && parsed.NumberEnd.Value >= start ? parsed.NumberEnd.Value : start);
            }
        }
    }
}
