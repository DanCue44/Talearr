using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using NzbDrone.Core.Books;
using NzbDrone.Core.MediaFiles.BookImport;
using NzbDrone.Core.Talearr.GrabPolicy;
using NzbDrone.Core.Talearr.Recordings;

namespace NzbDrone.Core.Talearr.Import
{
    // What a file name or tag says about a file that holds several books.
    public sealed class TalearrPackEvidence
    {
        public string Value { get; set; }

        // "Books 5-6", "Book 5-6", "Books 1 and 2": the series places the file covers.
        public int? RangeStart { get; set; }
        public int? RangeEnd { get; set; }

        // "Publisher's Pack 3", "Omnibus 2": the number of the pack, never a series place.
        public int? PackNumber { get; set; }

        public bool HasRange => RangeStart.HasValue && RangeEnd.HasValue;

        public bool HasNumbers => HasRange || PackNumber.HasValue;

        public override string ToString()
        {
            var parts = new List<string>();
            if (PackNumber.HasValue)
            {
                parts.Add("pack " + PackNumber.Value.ToString(CultureInfo.InvariantCulture));
            }

            if (HasRange)
            {
                parts.Add($"books {RangeStart.Value.ToString(CultureInfo.InvariantCulture)}-{RangeEnd.Value.ToString(CultureInfo.InvariantCulture)}");
            }

            return parts.Count > 0 ? string.Join(", ", parts) : Value;
        }
    }

    public enum TalearrPackVerdict
    {
        Keep,
        Redirect,
        Unmatch
    }

    public sealed class TalearrPackDecision
    {
        public TalearrPackVerdict Verdict { get; set; }
        public Book Target { get; set; }
        public Edition Edition { get; set; }
        public string Reason { get; set; }

        public static readonly TalearrPackDecision Keep = new TalearrPackDecision { Verdict = TalearrPackVerdict.Keep };
    }

    // Talearr (issue #14): files that hold several books, such as Audible's "Publisher's Pack"
    // audiobooks ("Renegade Star Publisher's Pack 3 (Unabridged).m4b", Books 5-6), must never land
    // on a single book. ReleasePackDetector and TalearrBundleDetector judge release names before a
    // grab; this judges files in the library and in downloads, with stricter patterns because a
    // series name alone ("The Broken Earth Trilogy, Book 1") is not a pack.
    //
    // A file is a pack when its file name, title or album tag names a range of books ("Books 1-2",
    // "Book 5-6"), a numbered pack ("Publisher's Pack 3", "Omnibus 2") or a bundle ("Box Set",
    // "Omnibus", "Complete Series", "3 Books"). The parent folder only adds the numbers when the
    // file itself already reads as a pack: a box set download folder holds single books too.
    public static class TalearrPackFiles
    {
        private const string Dash = @"(?:-|\u2013|\u2014)";
        private const string Apostrophe = @"['\u2019`]?";

        // Plural or "#": spaces around the dash are fine ("Books 1 - 3", "#1-2").
        private static readonly Regex PluralRangeRegex = new Regex(
            @"(?:\b(?:books|bks|volumes|vols)\.?|#)\s*#?(\d{1,3})\s*(?:" + Dash + @"|to|through|thru|and|&)\s*#?(\d{1,3})(?![\d.])",
            RegexOptions.Compiled | RegexOptions.IgnoreCase);

        // Singular: "Book 5-6" with a tight dash and no leading zero ("Book 1 - 02" is a chapter).
        private static readonly Regex SingularRangeRegex = new Regex(
            @"\b(?:book|bk|volume|vol)\.?\s*#?(\d{1,3})(?:" + Dash + @"|\s*(?:and|&)\s*)([1-9]\d{0,2})(?![\d.])",
            RegexOptions.Compiled | RegexOptions.IgnoreCase);

        // "Box Set 1-3", "Omnibus (1-3)".
        private static readonly Regex PackRangeRegex = new Regex(
            @"\b(?:omnibus|box(?:ed)?[\s-]?set|boxset|bundle|collection|pack)\s*[:(\[]?\s*#?(\d{1,3})\s*" + Dash + @"\s*#?([1-9]\d{0,2})(?![\d.])",
            RegexOptions.Compiled | RegexOptions.IgnoreCase);

        // "Publisher's Pack 3", "Omnibus 2", "Box Set #1": the pack's own number.
        private static readonly Regex PackNumberRegex = new Regex(
            @"\b(?:publisher" + Apostrophe + @"s?\s+pack|omnibus|box(?:ed)?[\s-]?set|boxset|bundle)\s*(?:#|no\.?|number|vol(?:ume)?\.?)?\s*(\d{1,3})(?![\d.])",
            RegexOptions.Compiled | RegexOptions.IgnoreCase);

        private static readonly Regex NamedPackRegex = new Regex(
            @"\b(?:omnibus|box(?:ed)?[\s-]?set|boxset|publisher" + Apostrophe + @"s?\s+pack|complete\s+(?:series|saga|collection)|(?:[2-9]|1\d|20)[\s-]*(?:books|novels|volumes)\b(?!\s*#?\d)|\d\s*-?\s*in\s*-?\s*1)\b",
            RegexOptions.Compiled | RegexOptions.IgnoreCase);

        private static readonly Regex PositionRangeRegex = new Regex(@"^\s*(\d{1,3})\s*" + Dash + @"\s*(\d{1,3})\s*$", RegexOptions.Compiled);
        private static readonly Regex SinglePositionRegex = new Regex(@"^\s*#?\s*(\d{1,3})(?:\.0+)?\s*$", RegexOptions.Compiled);

        private static readonly HashSet<string> TitleTagKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "title", "album", "subtitle", "\u00a9nam", "\u00a9alb", "tit2", "talb", "tit3"
        };

        private const int MaxRangeWidth = 50;

        // What one text says, or null when it does not read as a pack.
        public static TalearrPackEvidence Parse(string text)
        {
            if (string.IsNullOrWhiteSpace(text) || text.Length > 1000)
            {
                return null;
            }

            var evidence = new TalearrPackEvidence();

            foreach (var regex in new[] { PackRangeRegex, PluralRangeRegex, SingularRangeRegex })
            {
                foreach (Match match in regex.Matches(text))
                {
                    if (TryRange(match, out var start, out var end))
                    {
                        evidence.RangeStart = start;
                        evidence.RangeEnd = end;
                        evidence.Value = match.Value.Trim();
                        break;
                    }
                }

                if (evidence.HasRange)
                {
                    break;
                }
            }

            foreach (Match match in PackNumberRegex.Matches(text))
            {
                // "Box Set 1-3" is a range, not pack number 1.
                var after = text.Substring(match.Index + match.Length);
                if (Regex.IsMatch(after, @"^\s*" + Dash + @"\s*\d"))
                {
                    continue;
                }

                if (int.TryParse(match.Groups[1].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var number) && number > 0)
                {
                    evidence.PackNumber = number;
                    evidence.Value ??= match.Value.Trim();
                    break;
                }
            }

            if (!evidence.HasNumbers)
            {
                var named = NamedPackRegex.Match(text);
                if (!named.Success)
                {
                    return null;
                }

                evidence.Value = named.Value.Trim();
            }

            return evidence;
        }

        // The places in a text where a pack's own number sits ("3" in "Publisher's Pack 3"), so a
        // series place reader can skip them.
        public static List<(int Index, int Length)> PackNumberSpans(string text)
        {
            var spans = new List<(int Index, int Length)>();
            if (string.IsNullOrWhiteSpace(text))
            {
                return spans;
            }

            foreach (Match match in PackNumberRegex.Matches(text))
            {
                var group = match.Groups[1];
                spans.Add((group.Index, group.Length));
            }

            return spans;
        }

        // What a discovered file says about itself: its file name, title and album tags, with the
        // parent folder adding numbers only once the file reads as a pack.
        public static TalearrPackEvidence FromFile(DiscoveredFileWithMetadata file)
        {
            if (file == null)
            {
                return null;
            }

            var texts = new List<string> { FileNameOf(file.Path), file.ExtractedBook };
            foreach (var tag in file.AllTags ?? new Dictionary<string, List<string>>())
            {
                if (IsTitleTag(tag.Key) && tag.Value != null)
                {
                    texts.AddRange(tag.Value);
                }
            }

            return Combine(texts, FolderNameOf(file.Path));
        }

        // What a tracked file's path says (file name first, parent folder for the numbers).
        public static TalearrPackEvidence FromPath(string path)
        {
            return Combine(new List<string> { FileNameOf(path) }, FolderNameOf(path));
        }

        // The texts a related catalog entry's title may be named in.
        public static List<string> EvidenceTexts(DiscoveredFileWithMetadata file)
        {
            var texts = new List<string> { FileNameOf(file?.Path), FolderNameOf(file?.Path), file?.ExtractedBook };
            foreach (var tag in file?.AllTags ?? new Dictionary<string, List<string>>())
            {
                if (IsTitleTag(tag.Key) && tag.Value != null)
                {
                    texts.AddRange(tag.Value);
                }
            }

            return texts.Where(t => !string.IsNullOrWhiteSpace(t)).ToList();
        }

        public static List<string> EvidenceTexts(string path)
        {
            return new List<string> { FileNameOf(path), FolderNameOf(path) }.Where(t => !string.IsNullOrWhiteSpace(t)).ToList();
        }

        // The book is itself a pack, box set or collection, and what it says about its range.
        public static TalearrPackEvidence BookPack(Book book)
        {
            if (book == null)
            {
                return null;
            }

            var evidence = Parse(book.Title) ?? new TalearrPackEvidence();
            if (!evidence.HasRange)
            {
                foreach (var position in Positions(book))
                {
                    var range = PositionRangeRegex.Match(position);
                    if (range.Success && TryRange(range, out var start, out var end))
                    {
                        evidence.RangeStart = start;
                        evidence.RangeEnd = end;
                        break;
                    }
                }
            }

            if (evidence.HasNumbers || evidence.Value != null || book.IsOmnibus || TalearrBundleDetector.LooksLikeBundleTitle(book.Title))
            {
                evidence.Value ??= book.Title;
                return evidence;
            }

            return null;
        }

        // The file may belong to this book: the book is a pack and nothing it says contradicts the file.
        public static bool Fits(TalearrPackEvidence file, Book book)
        {
            var pack = BookPack(book);
            if (file == null)
            {
                return true;
            }

            if (pack == null)
            {
                return false;
            }

            if (file.HasRange && pack.HasRange)
            {
                return file.RangeStart == pack.RangeStart && file.RangeEnd == pack.RangeEnd;
            }

            if (file.PackNumber.HasValue && pack.PackNumber.HasValue)
            {
                return file.PackNumber == pack.PackNumber;
            }

            return true;
        }

        // The file's range certainly leaves this single book out: Books 5-6 on book 1.
        public static bool Excludes(TalearrPackEvidence file, Book book)
        {
            if (file?.HasRange != true || book == null || BookPack(book) != null)
            {
                return false;
            }

            var places = Positions(book)
                .Select(p => SinglePositionRegex.Match(p))
                .Where(m => m.Success)
                .Select(m => int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture))
                .Distinct()
                .ToList();

            return places.Count > 0 && places.All(p => p < file.RangeStart || p > file.RangeEnd);
        }

        // The author's catalog entry that is this pack: a pack or collection of the same media
        // type whose range or pack number agrees with the file's, for the same series (its title
        // starts like the matched book's or is named in the file). Null when none or several.
        public static Book FindPackEntry(TalearrPackEvidence file, Book matched, IEnumerable<Book> catalog, IEnumerable<string> evidenceTexts)
        {
            if (file?.HasNumbers != true || matched == null || catalog == null)
            {
                return null;
            }

            var texts = (evidenceTexts ?? Enumerable.Empty<string>()).Where(t => !string.IsNullOrWhiteSpace(t)).ToList();
            var matchedPrimary = PrimaryTitle(matched.Title);

            var found = catalog
                .Where(b => b != null && b.Id > 0 && b.Id != matched.Id && b.MediaType == matched.MediaType)
                .Where(b => !TalearrRecordingGroupService.IsCopySlug(b.TitleSlug))
                .Where(b => AgreesExactly(file, BookPack(b)))
                .Where(b =>
                {
                    var primary = PrimaryTitle(b.Title);
                    return (primary.Length > 0 && string.Equals(primary, matchedPrimary, StringComparison.OrdinalIgnoreCase)) ||
                           texts.Any(t => TalearrBundleDetector.ReleaseMentionsTitle(t, b.Title));
                })
                .GroupBy(b => b.Id)
                .Select(g => g.First())
                .ToList();

            return found.Count == 1 ? found[0] : null;
        }

        // What to do with a match the matcher made: keep it, move it to the pack entry, or leave
        // the file unmatched (for manual import). A download grabbed for the book keeps its match:
        // the grab policy's bundle fallback takes a pack for a single book on purpose.
        public static TalearrPackDecision Decide(DiscoveredFileWithMetadata file,
                                                 Book matched,
                                                 Func<int, List<Book>> booksByAuthor,
                                                 Func<int, List<Edition>> editionsByBook,
                                                 ICollection<int> targetBookIds = null,
                                                 ICollection<int> hardAllowedBookIds = null)
        {
            if (file == null || matched == null || targetBookIds?.Contains(matched.Id) == true)
            {
                return TalearrPackDecision.Keep;
            }

            var evidence = FromFile(file);
            if (evidence == null || Fits(evidence, matched))
            {
                return TalearrPackDecision.Keep;
            }

            var catalog = matched.AuthorId > 0 ? booksByAuthor?.Invoke(matched.AuthorId) : null;
            var target = FindPackEntry(evidence, matched, catalog, EvidenceTexts(file));
            if (target != null && (hardAllowedBookIds == null || hardAllowedBookIds.Count == 0 || hardAllowedBookIds.Contains(target.Id)))
            {
                var edition = ChooseEdition(editionsByBook?.Invoke(target.Id));
                if (edition != null)
                {
                    return new TalearrPackDecision
                    {
                        Verdict = TalearrPackVerdict.Redirect,
                        Target = target,
                        Edition = edition,
                        Reason = $"File holds several books ({evidence}); matched to '{target.Title}' instead of the single book '{matched.Title}'"
                    };
                }
            }

            return new TalearrPackDecision
            {
                Verdict = TalearrPackVerdict.Unmatch,
                Reason = $"File holds several books ({evidence}); not matched to the single book '{matched.Title}'. Use Manual Import to choose the book"
            };
        }

        // The edition files go on: the chosen (pinned) one, else the monitored one, else the first.
        public static Edition ChooseEdition(IEnumerable<Edition> editions)
        {
            var list = (editions ?? Enumerable.Empty<Edition>()).Where(e => e != null && e.Id > 0).OrderBy(e => e.Id).ToList();
            return list.FirstOrDefault(e => e.Monitored && e.ManualAdd) ?? list.FirstOrDefault(e => e.Monitored) ?? list.FirstOrDefault();
        }

        private static TalearrPackEvidence Combine(List<string> fileTexts, string folder)
        {
            TalearrPackEvidence combined = null;
            foreach (var text in fileTexts.Where(t => !string.IsNullOrWhiteSpace(t)))
            {
                combined = Merge(combined, Parse(text));
            }

            if (combined == null)
            {
                return null;
            }

            return Merge(combined, Parse(folder));
        }

        private static TalearrPackEvidence Merge(TalearrPackEvidence into, TalearrPackEvidence from)
        {
            if (from == null)
            {
                return into;
            }

            if (into == null)
            {
                return from;
            }

            if (!into.HasRange && from.HasRange)
            {
                into.RangeStart = from.RangeStart;
                into.RangeEnd = from.RangeEnd;
            }

            into.PackNumber ??= from.PackNumber;
            return into;
        }

        private static bool AgreesExactly(TalearrPackEvidence file, TalearrPackEvidence pack)
        {
            if (file == null || pack == null)
            {
                return false;
            }

            if (file.HasRange && pack.HasRange)
            {
                return file.RangeStart == pack.RangeStart && file.RangeEnd == pack.RangeEnd;
            }

            if (file.PackNumber.HasValue && pack.PackNumber.HasValue)
            {
                return file.PackNumber == pack.PackNumber;
            }

            return false;
        }

        private static bool TryRange(Match match, out int start, out int end)
        {
            start = 0;
            end = 0;
            return int.TryParse(match.Groups[1].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out start) &&
                   int.TryParse(match.Groups[2].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out end) &&
                   start > 0 && end > start && end - start <= MaxRangeWidth;
        }

        private static List<string> Positions(Book book)
        {
            var positions = new List<string>();
            if (!string.IsNullOrWhiteSpace(book.SeriesPosition))
            {
                positions.Add(book.SeriesPosition);
            }

            try
            {
                if (book.LazySeriesLinks?.IsLoaded == true)
                {
                    foreach (var link in book.SeriesLinks ?? new List<SeriesBookLink>())
                    {
                        if (!string.IsNullOrWhiteSpace(link?.Position))
                        {
                            positions.Add(link.Position);
                        }
                    }
                }
            }
            catch (Exception)
            {
                // Series links are a bonus; the book's own position is enough.
            }

            return positions;
        }

        private static bool IsTitleTag(string key)
        {
            if (string.IsNullOrWhiteSpace(key))
            {
                return false;
            }

            var separator = key.LastIndexOf(':');
            var name = separator >= 0 && separator < key.Length - 1 ? key.Substring(separator + 1) : key;
            return TitleTagKeys.Contains(name.Trim());
        }

        private static string PrimaryTitle(string title)
        {
            if (string.IsNullOrWhiteSpace(title))
            {
                return string.Empty;
            }

            var cut = title.IndexOfAny(new[] { ':', '(', '[', ',' });
            return (cut > 0 ? title.Substring(0, cut) : title).Trim();
        }

        private static string FileNameOf(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                return null;
            }

            var name = path.Replace('\\', '/');
            var slash = name.LastIndexOf('/');
            name = slash >= 0 ? name.Substring(slash + 1) : name;
            return Path.GetFileNameWithoutExtension(name);
        }

        private static string FolderNameOf(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                return null;
            }

            var parts = path.Replace('\\', '/').TrimEnd('/').Split('/', StringSplitOptions.RemoveEmptyEntries);
            return parts.Length >= 2 ? parts[parts.Length - 2] : null;
        }
    }
}
