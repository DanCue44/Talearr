using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace NzbDrone.Core.Talearr.Wishlists
{
    public static class TalearrWishlistFormats
    {
        public const string Goodreads = "goodreads";
        public const string StoryGraph = "storygraph";
        public const string Audible = "audible";
        public const string Generic = "csv";
        public const string Hardcover = "hardcover";
    }

    // TaleArr: reads wishlist exports. The format is recognised from the header row:
    //
    //   Goodreads  "My Books" export: Title, Author, ISBN, ISBN13, Exclusive Shelf (to-read)
    //   StoryGraph library export:    Title, Authors, ISBN/UID, Format, Read Status (to-read)
    //   Audible (audible-cli "audible wishlist export --format csv", or any export with an
    //   asin column):                 asin, title, authors
    //   Anything else with a title column, plus optional author(s), isbn, asin, status.
    //
    // Only the to-read rows are taken when the export has a shelf or status column and
    // toReadOnly is set; an Audible wishlist is a wishlist, so every row counts.
    public static class TalearrWishlistCsv
    {
        private static readonly Regex NonDigitRegex = new Regex(@"[^0-9Xx]", RegexOptions.Compiled);
        private static readonly Regex AsinRegex = new Regex(@"^[A-Z0-9]{10}$", RegexOptions.Compiled);
        private static readonly Regex SpaceRegex = new Regex(@"\s+", RegexOptions.Compiled);

        public static (string Format, List<TalearrWishlistRow> Rows) Parse(string csv, bool toReadOnly = true)
        {
            var table = ReadTable(csv);
            if (table.Count == 0)
            {
                return (TalearrWishlistFormats.Generic, new List<TalearrWishlistRow>());
            }

            var header = table[0].Select(NormalizeHeader).ToList();
            int Col(params string[] names) => names.Select(n => header.IndexOf(n)).FirstOrDefault(i => i >= 0, -1);

            var format = DetectFormat(header);
            var title = Col("title", "book title", "name");
            if (title < 0)
            {
                throw new FormatException("The file has no Title column. Export your list as CSV (Goodreads, StoryGraph, audible-cli) or add a title column.");
            }

            var subtitle = Col("subtitle");
            var author = Col("author", "authors", "author l-f", "author name", "creator", "creators");
            var isbn13 = Col("isbn13", "isbn 13", "isbn/uid", "isbn-13");
            var isbn10 = Col("isbn", "isbn10", "isbn 10", "isbn-10");
            var asin = Col("asin", "audible asin", "product id");
            var status = Col("exclusive shelf", "read status", "status", "shelf", "bookshelves");

            var rows = new List<TalearrWishlistRow>();
            for (var i = 1; i < table.Count; i++)
            {
                var cells = table[i];
                string Cell(int index) => index >= 0 && index < cells.Count ? Clean(cells[index]) : null;

                var rowTitle = Cell(title);
                if (string.IsNullOrWhiteSpace(rowTitle))
                {
                    continue;
                }

                var rowStatus = Cell(status);
                if (toReadOnly && format != TalearrWishlistFormats.Audible && status >= 0 && !IsToRead(rowStatus))
                {
                    continue;
                }

                var row = new TalearrWishlistRow
                {
                    Title = rowTitle,
                    Subtitle = Cell(subtitle),
                    Author = FirstAuthor(Cell(author)),
                    Isbn = NormalizeIsbn(Cell(isbn13)) ?? NormalizeIsbn(Cell(isbn10)),
                    Asin = NormalizeAsin(Cell(asin)),
                    Status = rowStatus
                };

                row.Key = RowKey(row);
                rows.Add(row);
            }

            return (format, rows.GroupBy(r => r.Key, StringComparer.OrdinalIgnoreCase).Select(g => g.First()).ToList());
        }

        public static string RowKey(TalearrWishlistRow row)
        {
            if (!string.IsNullOrWhiteSpace(row.BookProviderId))
            {
                return row.BookProviderId.ToLowerInvariant();
            }

            if (!string.IsNullOrWhiteSpace(row.Asin))
            {
                return "asin:" + row.Asin.ToLowerInvariant();
            }

            if (!string.IsNullOrWhiteSpace(row.Isbn))
            {
                return "isbn:" + row.Isbn.ToLowerInvariant();
            }

            return "title:" + Simplify(row.Title) + "|" + Simplify(row.Author);
        }

        public static string Simplify(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return string.Empty;
            }

            var normalized = value.Normalize(NormalizationForm.FormD);
            var builder = new StringBuilder();
            foreach (var c in normalized)
            {
                if (CharUnicodeInfo.GetUnicodeCategory(c) == UnicodeCategory.NonSpacingMark)
                {
                    continue;
                }

                builder.Append(char.IsLetterOrDigit(c) ? char.ToLowerInvariant(c) : ' ');
            }

            return SpaceRegex.Replace(builder.ToString(), " ").Trim();
        }

        public static bool IsToRead(string status)
        {
            if (string.IsNullOrWhiteSpace(status))
            {
                return false;
            }

            var simple = Simplify(status).Replace(" ", string.Empty);
            return simple.Contains("toread") || simple.Contains("wanttoread") || simple.Contains("wishlist");
        }

        public static string NormalizeIsbn(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return null;
            }

            var digits = NonDigitRegex.Replace(value, string.Empty).ToUpperInvariant();
            return digits.Length == 13 || digits.Length == 10 ? digits : null;
        }

        public static string NormalizeAsin(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return null;
            }

            var trimmed = value.Trim().ToUpperInvariant();
            return AsinRegex.IsMatch(trimmed) ? trimmed : null;
        }

        private static string DetectFormat(List<string> header)
        {
            if (header.Contains("exclusive shelf") || header.Contains("author l-f"))
            {
                return TalearrWishlistFormats.Goodreads;
            }

            if (header.Contains("read status") || header.Contains("isbn/uid"))
            {
                return TalearrWishlistFormats.StoryGraph;
            }

            if (header.Contains("asin") && (header.Contains("narrators") || header.Contains("authors")))
            {
                return TalearrWishlistFormats.Audible;
            }

            return TalearrWishlistFormats.Generic;
        }

        private static string FirstAuthor(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return null;
            }

            // StoryGraph and audible-cli list several authors: "Terry Pratchett, Neil Gaiman".
            var first = value.Split(new[] { ',', ';', '&' }, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault()?.Trim();
            return string.IsNullOrWhiteSpace(first) ? null : first;
        }

        private static string Clean(string value)
        {
            if (value == null)
            {
                return null;
            }

            var trimmed = value.Trim();

            // Goodreads writes ISBNs as ="0451524934" so spreadsheets keep the leading zero.
            if (trimmed.StartsWith("=\"", StringComparison.Ordinal) && trimmed.EndsWith("\"", StringComparison.Ordinal))
            {
                trimmed = trimmed.Substring(2, trimmed.Length - 3);
            }

            return trimmed.Length == 0 ? null : trimmed;
        }

        private static string NormalizeHeader(string value)
        {
            return (value ?? string.Empty).Trim().Trim('﻿').Replace('_', ' ').ToLowerInvariant();
        }

        // RFC 4180: quoted fields, doubled quotes, newlines inside quotes; comma, semicolon or tab
        // separated (whichever the header uses most).
        internal static List<List<string>> ReadTable(string csv)
        {
            var rows = new List<List<string>>();
            if (string.IsNullOrWhiteSpace(csv))
            {
                return rows;
            }

            var firstLine = csv.Split('\n')[0];
            var separator = new[] { ',', ';', '\t' }.OrderByDescending(c => firstLine.Count(x => x == c)).First();

            var row = new List<string>();
            var field = new StringBuilder();
            var inQuotes = false;

            for (var i = 0; i < csv.Length; i++)
            {
                var c = csv[i];
                if (inQuotes)
                {
                    if (c == '"')
                    {
                        if (i + 1 < csv.Length && csv[i + 1] == '"')
                        {
                            field.Append('"');
                            i++;
                        }
                        else
                        {
                            inQuotes = false;
                        }
                    }
                    else
                    {
                        field.Append(c);
                    }

                    continue;
                }

                if (c == '"' && field.Length == 0)
                {
                    inQuotes = true;
                }
                else if (c == '"' && field.ToString() == "=")
                {
                    // ="0451524934": keep the = so Clean can unwrap it.
                    field.Append('"');
                    var end = csv.IndexOf('"', i + 1);
                    if (end > i)
                    {
                        field.Append(csv, i + 1, end - i);
                        i = end;
                    }
                }
                else if (c == separator)
                {
                    row.Add(field.ToString());
                    field.Clear();
                }
                else if (c == '\n' || c == '\r')
                {
                    if (c == '\r' && i + 1 < csv.Length && csv[i + 1] == '\n')
                    {
                        i++;
                    }

                    row.Add(field.ToString());
                    field.Clear();
                    if (row.Any(f => f.Length > 0))
                    {
                        rows.Add(row);
                    }

                    row = new List<string>();
                }
                else
                {
                    field.Append(c);
                }
            }

            row.Add(field.ToString());
            if (row.Any(f => f.Length > 0))
            {
                rows.Add(row);
            }

            return rows;
        }
    }
}
