using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace NzbDrone.Core.Talearr.Wishlists
{
    // Talearr: reads the plain lists behind the author list and URL list wishlists.
    //
    //   Author list: one author name per line (pasted or a text file). Empty lines and lines
    //   starting with # are skipped.
    //   URL list:    the same plain text, where a line can also be an ISBN or an ASIN (a book),
    //   or JSON: an array of strings, an array of objects with name/author and isbn/asin/title
    //   fields, or an object holding such arrays under authors, books or items.
    public static class TalearrWishlistLines
    {
        public const int MaxRows = 2000;

        private static readonly Regex IsbnLine = new Regex(@"^(?:ISBN(?:-1[03])?:?\s*)?[0-9][0-9\- ]{8,16}[0-9Xx]$", RegexOptions.Compiled | RegexOptions.IgnoreCase);
        private static readonly Regex AsinLine = new Regex(@"^(?:ASIN:?\s*)?(B0[A-Z0-9]{8})$", RegexOptions.Compiled | RegexOptions.IgnoreCase);

        public static List<TalearrWishlistRow> ParseAuthors(string text)
        {
            return Finish(Lines(text).Select(AuthorRow));
        }

        public static List<TalearrWishlistRow> ParseMixed(string content)
        {
            var trimmed = (content ?? string.Empty).Trim().TrimStart('﻿');
            if (trimmed.StartsWith("[", StringComparison.Ordinal) || trimmed.StartsWith("{", StringComparison.Ordinal))
            {
                try
                {
                    using var document = JsonDocument.Parse(trimmed);
                    return Finish(FromJson(document.RootElement));
                }
                catch (JsonException ex)
                {
                    throw new FormatException("The list looks like JSON but could not be read: " + ex.Message);
                }
            }

            return Finish(Lines(trimmed).Select(LineRow));
        }

        // A line is a book when it is an ISBN or an Audible ASIN, else an author name.
        internal static TalearrWishlistRow LineRow(string line)
        {
            var value = line.Trim();
            if (IsbnLine.IsMatch(value))
            {
                var isbn = TalearrWishlistCsv.NormalizeIsbn(value.Substring(value.IndexOfAny("0123456789".ToCharArray())));
                if (isbn != null)
                {
                    return new TalearrWishlistRow { Isbn = isbn };
                }
            }

            var asin = AsinLine.Match(value);
            if (asin.Success)
            {
                return new TalearrWishlistRow { Asin = asin.Groups[1].Value.ToUpperInvariant() };
            }

            return AuthorRow(value);
        }

        private static TalearrWishlistRow AuthorRow(string name)
        {
            var clean = Regex.Replace(name ?? string.Empty, @"\s+", " ").Trim();
            return clean.Length == 0 ? null : new TalearrWishlistRow { Author = clean, IsAuthor = true };
        }

        private static IEnumerable<TalearrWishlistRow> FromJson(JsonElement element)
        {
            switch (element.ValueKind)
            {
                case JsonValueKind.Array:
                    foreach (var item in element.EnumerateArray())
                    {
                        foreach (var row in FromJsonItem(item, null))
                        {
                            yield return row;
                        }
                    }

                    break;

                case JsonValueKind.Object:
                    var found = false;
                    foreach (var property in element.EnumerateObject())
                    {
                        var name = property.Name.ToLowerInvariant();
                        if (property.Value.ValueKind != JsonValueKind.Array || (name != "authors" && name != "books" && name != "items"))
                        {
                            continue;
                        }

                        found = true;
                        foreach (var item in property.Value.EnumerateArray())
                        {
                            foreach (var row in FromJsonItem(item, name == "authors" ? (bool?)true : name == "books" ? false : null))
                            {
                                yield return row;
                            }
                        }
                    }

                    if (!found)
                    {
                        foreach (var row in FromJsonItem(element, null))
                        {
                            yield return row;
                        }
                    }

                    break;
            }
        }

        // isAuthor: true under "authors", false under "books", null when the item decides.
        private static IEnumerable<TalearrWishlistRow> FromJsonItem(JsonElement item, bool? isAuthor)
        {
            if (item.ValueKind == JsonValueKind.String)
            {
                var text = item.GetString();
                if (!string.IsNullOrWhiteSpace(text))
                {
                    yield return isAuthor == true ? AuthorRow(text) : LineRow(text);
                }

                yield break;
            }

            if (item.ValueKind != JsonValueKind.Object)
            {
                yield break;
            }

            string Field(params string[] names)
            {
                foreach (var property in item.EnumerateObject())
                {
                    if (names.Any(n => string.Equals(n, property.Name, StringComparison.OrdinalIgnoreCase)))
                    {
                        return property.Value.ValueKind switch
                        {
                            JsonValueKind.String => property.Value.GetString(),
                            JsonValueKind.Number => property.Value.GetRawText(),
                            _ => null
                        };
                    }
                }

                return null;
            }

            var isbn = TalearrWishlistCsv.NormalizeIsbn(Field("isbn13", "isbn", "isbn10"));
            var asin = TalearrWishlistCsv.NormalizeAsin(Field("asin"));
            var title = Field("title");
            var author = Field("author", "authorName", "name");

            if (isAuthor != true && (isbn != null || asin != null || !string.IsNullOrWhiteSpace(title)))
            {
                yield return new TalearrWishlistRow { Title = title?.Trim(), Author = author?.Trim(), Isbn = isbn, Asin = asin };
            }
            else if (!string.IsNullOrWhiteSpace(author))
            {
                yield return AuthorRow(author);
            }
        }

        private static IEnumerable<string> Lines(string text)
        {
            return (text ?? string.Empty)
                .Replace("\r\n", "\n")
                .Split('\n')
                .Select(l => l.Trim().TrimStart('﻿'))
                .Where(l => l.Length > 0 && !l.StartsWith("#", StringComparison.Ordinal));
        }

        private static List<TalearrWishlistRow> Finish(IEnumerable<TalearrWishlistRow> rows)
        {
            var list = new List<TalearrWishlistRow>();
            foreach (var row in rows.Where(r => r != null))
            {
                row.Key = TalearrWishlistCsv.RowKey(row);
                list.Add(row);
            }

            return list
                .GroupBy(r => r.Key, StringComparer.OrdinalIgnoreCase)
                .Select(g => g.First())
                .Take(MaxRows)
                .ToList();
        }
    }
}
