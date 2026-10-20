using System;
using System.Collections.Generic;
using System.Linq;
using NzbDrone.Core.Books;

namespace NzbDrone.Core.Talearr.Metadata.Source
{
    // Talearr: the pure part of the shadow comparison. Takes the local rows and the two remote
    // authors (already converted with the same V5 conversion), runs both through the same
    // normalization callback (RefreshAuthorService.NormalizeRemoteBooks in production) and
    // reports the differences. No I/O.
    //
    // Matching follows refresh: a remote book matches local rows of the same media type through
    // BookIdentity.FindWorkFirstMatchResult (stable work ids first, edition ids only for rows
    // without work identity, ambiguous edition matches count as no match).
    public static class TalearrMetadataShadowComparer
    {
        public const int ListLimit = 100;

        public delegate List<Book> NormalizeBooks(Author local, List<Book> remoteBooks, List<Series> remoteSeries);

        public class Side
        {
            public Author Remote { get; set; }
            public string Error { get; set; }
            public int? SeriesFromSource { get; set; }
            public int DatesFilled { get; set; }
        }

        public static TalearrShadowAuthorReport Compare(Author local,
                                                        List<Book> localBooks,
                                                        ISet<int> bookIdsWithFiles,
                                                        Side server,
                                                        Side hardcover,
                                                        NormalizeBooks normalize,
                                                        DateTime now)
        {
            localBooks ??= new List<Book>();
            bookIdsWithFiles ??= new HashSet<int>();

            var report = new TalearrShadowAuthorReport
            {
                AuthorId = local?.Id ?? 0,
                AuthorName = local?.Name,
                ComparedAt = now,
                Status = "ok",
                LocalBooks = localBooks.Count,
                LocalProtectedBooks = localBooks.Count(b => IsProtected(b, bookIdsWithFiles))
            };

            var serverResult = Evaluate(local, localBooks, server, normalize, report.Server);
            var hardcoverResult = Evaluate(local, localBooks, hardcover, normalize, report.Hardcover);

            if (serverResult == null || hardcoverResult == null)
            {
                report.Status = "error";
                report.Message = string.Join("; ", new[]
                {
                    serverResult == null ? $"metadata server: {server?.Error ?? "no data"}" : null,
                    hardcoverResult == null ? $"Hardcover: {hardcover?.Error ?? "no data"}" : null
                }.Where(m => m != null));

                return report;
            }

            // Works only on one side, by stable work ids, after filtering.
            var serverWorks = GroupWorks(serverResult.Filtered);
            var hardcoverWorks = GroupWorks(hardcoverResult.Filtered);
            var onlyServer = WorksWithoutCounterpart(serverWorks, hardcoverWorks);
            var onlyHardcover = WorksWithoutCounterpart(hardcoverWorks, serverWorks);

            report.OnlyOnServerCount = onlyServer.Count;
            report.OnlyOnHardcoverCount = onlyHardcover.Count;
            report.OnlyOnServer = onlyServer.OrderBy(w => w.Title, StringComparer.OrdinalIgnoreCase).Take(ListLimit).ToList();
            report.OnlyOnHardcover = onlyHardcover.OrderBy(w => w.Title, StringComparer.OrdinalIgnoreCase).Take(ListLimit).ToList();

            // Local rows that would lose (or gain) their remote match.
            var lost = localBooks
                .Where(b => serverResult.MatchedFiltered.Contains(b.Id) && !hardcoverResult.MatchedFiltered.Contains(b.Id))
                .Select(b => new TalearrShadowLocalBook
                {
                    BookId = b.Id,
                    Title = b.Title,
                    MediaType = b.MediaType.ToString(),
                    Monitored = b.IsMonitored(),
                    HasFiles = bookIdsWithFiles.Contains(b.Id),
                    ManualAdd = b.AddOptions?.AddType == BookAddType.Manual,
                    ProviderIds = BookIdentity.GetProviderIdentityTokens(b).OrderBy(t => t, StringComparer.OrdinalIgnoreCase).ToList(),
                    Reason = hardcoverResult.MatchedUnfiltered.Contains(b.Id) ? "filtered by profile" : "missing from Hardcover"
                })
                .OrderByDescending(b => b.Protected)
                .ThenBy(b => b.Title, StringComparer.OrdinalIgnoreCase)
                .ToList();

            report.LostMatchesCount = lost.Count;
            report.LostProtectedCount = lost.Count(b => b.Protected);
            report.LostMatches = lost.Take(ListLimit).ToList();
            report.GainedMatchesCount = localBooks.Count(b => hardcoverResult.MatchedFiltered.Contains(b.Id) && !serverResult.MatchedFiltered.Contains(b.Id));

            return report;
        }

        public static string Summarize(TalearrShadowAuthorReport report)
        {
            if (report == null)
            {
                return string.Empty;
            }

            if (report.Status != "ok")
            {
                return $"{report.AuthorName} ({report.AuthorId}): {report.Status} - {report.Message}";
            }

            return $"{report.AuthorName} ({report.AuthorId}): " +
                   $"server {report.Server.BooksAfterFilter.Audiobook}a/{report.Server.BooksAfterFilter.Ebook}e of {report.Server.BooksBeforeFilter.Total}, " +
                   $"hardcover {report.Hardcover.BooksAfterFilter.Audiobook}a/{report.Hardcover.BooksAfterFilter.Ebook}e of {report.Hardcover.BooksBeforeFilter.Total}; " +
                   $"works only on server {report.OnlyOnServerCount}, only on hardcover {report.OnlyOnHardcoverCount}; " +
                   $"local {report.LocalBooks} (protected {report.LocalProtectedBooks}), lost matches {report.LostMatchesCount} (protected {report.LostProtectedCount}), gained {report.GainedMatchesCount}; " +
                   $"editions {report.Server.EditionsAfterFilter.Total} -> {report.Hardcover.EditionsAfterFilter.Total}; " +
                   $"series {report.Server.SeriesKept} -> {report.Hardcover.SeriesKept} (hardcover sent {report.Hardcover.SeriesFromSource ?? 0})";
        }

        internal static bool IsProtected(Book book, ISet<int> bookIdsWithFiles)
        {
            return book != null &&
                   (book.IsMonitored() ||
                    bookIdsWithFiles.Contains(book.Id) ||
                    book.AddOptions?.AddType == BookAddType.Manual);
        }

        private class SideResult
        {
            public List<Book> Filtered { get; set; }
            public HashSet<int> MatchedFiltered { get; set; }
            public HashSet<int> MatchedUnfiltered { get; set; }
        }

        private static SideResult Evaluate(Author local, List<Book> localBooks, Side side, NormalizeBooks normalize, TalearrShadowSideReport sideReport)
        {
            if (side?.Remote == null)
            {
                sideReport.Available = false;
                sideReport.Error = side?.Error ?? "no data";
                return null;
            }

            var raw = (side.Remote.Books ?? new List<Book>()).Where(b => b != null).ToList();
            sideReport.Available = true;
            sideReport.BooksBeforeFilter = CountBooks(raw);
            sideReport.EditionsBeforeFilter = CountEditions(raw);
            sideReport.SeriesKept = side.Remote.Series?.Count(s => s != null) ?? 0;
            sideReport.SeriesFromSource = side.SeriesFromSource;
            sideReport.DatesFilled = side.DatesFilled;

            // Match the unfiltered rows before normalization trims their editions.
            var matchedUnfiltered = MatchLocal(localBooks, raw);

            List<Book> filtered;
            using (TalearrFilterTrace.Begin(sideReport.FilterReasons))
            {
                filtered = normalize(local, raw, side.Remote.Series?.ToList() ?? new List<Series>()) ?? new List<Book>();
            }

            sideReport.BooksAfterFilter = CountBooks(filtered);
            sideReport.EditionsAfterFilter = CountEditions(filtered);
            sideReport.WorksAfterFilter = GroupWorks(filtered).Count;

            var matchedFiltered = MatchLocal(localBooks, filtered);
            sideReport.MatchedLocalBooks = matchedFiltered.Count;

            return new SideResult
            {
                Filtered = filtered,
                MatchedFiltered = matchedFiltered,
                MatchedUnfiltered = matchedUnfiltered
            };
        }

        internal static HashSet<int> MatchLocal(List<Book> localBooks, IEnumerable<Book> remoteBooks)
        {
            var matched = new HashSet<int>();
            var byMediaType = localBooks
                .Where(b => b != null)
                .GroupBy(b => b.MediaType)
                .ToDictionary(g => g.Key, g => g.ToList());

            foreach (var remote in remoteBooks ?? Enumerable.Empty<Book>())
            {
                if (remote == null || !byMediaType.TryGetValue(remote.MediaType, out var candidates))
                {
                    continue;
                }

                var result = BookIdentity.FindWorkFirstMatchResult(candidates, remote);
                if (result.Disposition == WorkFirstMatchDisposition.StableWork ||
                    result.Disposition == WorkFirstMatchDisposition.EditionUnique)
                {
                    foreach (var match in result.Matches)
                    {
                        matched.Add(match.Id);
                    }
                }
            }

            return matched;
        }

        private sealed class Work
        {
            public string Key { get; set; }
            public string Title { get; set; }
            public HashSet<string> Tokens { get; set; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            public SortedSet<string> MediaTypes { get; set; } = new SortedSet<string>(StringComparer.Ordinal);
        }

        private static List<Work> GroupWorks(IEnumerable<Book> books)
        {
            var works = new Dictionary<string, Work>(StringComparer.OrdinalIgnoreCase);
            foreach (var book in books ?? Enumerable.Empty<Book>())
            {
                if (book == null)
                {
                    continue;
                }

                var key = book.BaseBookId ?? book.ForeignEditionId ?? book.HardcoverBookId ?? book.GoodreadsWorkId ?? book.Title;
                if (string.IsNullOrWhiteSpace(key))
                {
                    continue;
                }

                if (!works.TryGetValue(key, out var work))
                {
                    work = new Work { Key = key, Title = book.Title };
                    works[key] = work;
                }

                work.Tokens.UnionWith(BookIdentity.GetStableWorkProviderIdentityTokens(book));
                work.MediaTypes.Add(book.MediaType.ToString());
            }

            return works.Values.ToList();
        }

        private static List<TalearrShadowWork> WorksWithoutCounterpart(List<Work> works, List<Work> others)
        {
            var otherTokens = new HashSet<string>(others.SelectMany(w => w.Tokens), StringComparer.OrdinalIgnoreCase);
            return works
                .Where(w => !w.Tokens.Overlaps(otherTokens))
                .Select(w => new TalearrShadowWork
                {
                    Title = w.Title,
                    WorkId = w.Key,
                    ProviderIds = w.Tokens.OrderBy(t => t, StringComparer.OrdinalIgnoreCase).ToList(),
                    MediaTypes = w.MediaTypes.ToList()
                })
                .ToList();
        }

        private static TalearrShadowMediaCounts CountBooks(IEnumerable<Book> books)
        {
            var list = books?.Where(b => b != null).ToList() ?? new List<Book>();
            return new TalearrShadowMediaCounts
            {
                Audiobook = list.Count(b => b.MediaType == BookMediaType.Audiobook),
                Ebook = list.Count(b => b.MediaType == BookMediaType.Ebook)
            };
        }

        private static TalearrShadowMediaCounts CountEditions(IEnumerable<Book> books)
        {
            var list = books?.Where(b => b != null).ToList() ?? new List<Book>();
            return new TalearrShadowMediaCounts
            {
                Audiobook = list.Where(b => b.MediaType == BookMediaType.Audiobook).Sum(b => b.Editions?.Count ?? 0),
                Ebook = list.Where(b => b.MediaType == BookMediaType.Ebook).Sum(b => b.Editions?.Count ?? 0)
            };
        }
    }
}
