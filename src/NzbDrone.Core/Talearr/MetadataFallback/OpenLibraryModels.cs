using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;

namespace NzbDrone.Core.Talearr.MetadataFallback
{
    // Talearr: the parts of the Open Library JSON the fallback uses. Parsed from JObject so
    // fields that change shape (bio is a string or {type, value}) never break a request.
    public class OpenLibraryAuthorDoc
    {
        // Bare key, for example OL239800A.
        public string Key { get; set; }
        public string Name { get; set; }
        public string BirthDate { get; set; }
        public string DeathDate { get; set; }
        public string TopWork { get; set; }
        public int WorkCount { get; set; }

        public static OpenLibraryAuthorDoc Parse(JToken doc)
        {
            if (doc == null || doc.Type != JTokenType.Object)
            {
                return null;
            }

            var key = OpenLibraryJson.BareKey(OpenLibraryJson.String(doc["key"]));
            if (string.IsNullOrWhiteSpace(key))
            {
                return null;
            }

            return new OpenLibraryAuthorDoc
            {
                Key = key,
                Name = OpenLibraryJson.String(doc["name"]),
                BirthDate = OpenLibraryJson.String(doc["birth_date"]),
                DeathDate = OpenLibraryJson.String(doc["death_date"]),
                TopWork = OpenLibraryJson.String(doc["top_work"]),
                WorkCount = OpenLibraryJson.Int(doc["work_count"]) ?? 0
            };
        }
    }

    public class OpenLibraryAuthorRecord
    {
        public string Key { get; set; }
        public string Name { get; set; }
        public string PersonalName { get; set; }
        public string Bio { get; set; }
        public string BirthDate { get; set; }
        public string DeathDate { get; set; }
        public List<string> AlternateNames { get; set; } = new List<string>();
        public List<long> PhotoIds { get; set; } = new List<long>();
        public Dictionary<string, string> RemoteIds { get; set; } = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        public string GetRemoteId(string name)
        {
            return RemoteIds != null && RemoteIds.TryGetValue(name, out var value) && !string.IsNullOrWhiteSpace(value)
                ? value.Trim()
                : null;
        }

        public static OpenLibraryAuthorRecord Parse(JToken record)
        {
            if (record == null || record.Type != JTokenType.Object)
            {
                return null;
            }

            var key = OpenLibraryJson.BareKey(OpenLibraryJson.String(record["key"]));
            if (string.IsNullOrWhiteSpace(key))
            {
                return null;
            }

            var result = new OpenLibraryAuthorRecord
            {
                Key = key,
                Name = OpenLibraryJson.String(record["name"]),
                PersonalName = OpenLibraryJson.String(record["personal_name"]),
                Bio = OpenLibraryJson.Text(record["bio"]),
                BirthDate = OpenLibraryJson.String(record["birth_date"]),
                DeathDate = OpenLibraryJson.String(record["death_date"]),
                AlternateNames = OpenLibraryJson.Strings(record["alternate_names"]),
                PhotoIds = (record["photos"] as JArray)?
                    .Select(t => OpenLibraryJson.Long(t))
                    .Where(id => id.HasValue && id.Value > 0)
                    .Select(id => id.Value)
                    .ToList() ?? new List<long>()
            };

            if (record["remote_ids"] is JObject remoteIds)
            {
                foreach (var property in remoteIds.Properties())
                {
                    var value = OpenLibraryJson.String(property.Value);
                    if (!string.IsNullOrWhiteSpace(value))
                    {
                        result.RemoteIds[property.Name] = value;
                    }
                }
            }

            return result;
        }
    }

    public class OpenLibraryWorkDoc
    {
        // Bare key, for example OL1992198W.
        public string Key { get; set; }
        public string Title { get; set; }
        public string Subtitle { get; set; }
        public int? FirstPublishYear { get; set; }
        public long? CoverId { get; set; }
        public string CoverEditionKey { get; set; }
        public List<string> EditionKeys { get; set; } = new List<string>();
        public int EditionCount { get; set; }
        public List<string> Isbns { get; set; } = new List<string>();
        public List<string> Languages { get; set; } = new List<string>();
        public List<string> AuthorKeys { get; set; } = new List<string>();
        public List<string> AuthorNames { get; set; } = new List<string>();
        public List<string> Publishers { get; set; } = new List<string>();
        public int? PageCountMedian { get; set; }
        public decimal RatingsAverage { get; set; }
        public int RatingsCount { get; set; }

        // Talearr: Amazon ids of the work's editions (id_amazon). Audible products are B0... ASINs.
        public List<string> AmazonIds { get; set; } = new List<string>();

        public string EditionKey => !string.IsNullOrWhiteSpace(CoverEditionKey) ? CoverEditionKey : EditionKeys.FirstOrDefault();

        public IEnumerable<string> Isbn13s => Isbns.Select(OpenLibraryJson.NormalizeIsbn).Where(i => i?.Length == 13);

        public IEnumerable<string> Isbn10s => Isbns.Select(OpenLibraryJson.NormalizeIsbn).Where(i => i?.Length == 10);

        public static OpenLibraryWorkDoc Parse(JToken doc)
        {
            if (doc == null || doc.Type != JTokenType.Object)
            {
                return null;
            }

            var key = OpenLibraryJson.BareKey(OpenLibraryJson.String(doc["key"]));
            var title = OpenLibraryJson.String(doc["title"]);
            if (string.IsNullOrWhiteSpace(key) || string.IsNullOrWhiteSpace(title) ||
                !key.EndsWith("W", StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }

            return new OpenLibraryWorkDoc
            {
                Key = key,
                Title = title.Trim(),
                Subtitle = OpenLibraryJson.String(doc["subtitle"]),
                FirstPublishYear = OpenLibraryJson.Int(doc["first_publish_year"]),
                CoverId = OpenLibraryJson.Long(doc["cover_i"]),
                CoverEditionKey = OpenLibraryJson.BareKey(OpenLibraryJson.String(doc["cover_edition_key"])),
                EditionKeys = OpenLibraryJson.Strings(doc["edition_key"]).Select(OpenLibraryJson.BareKey).Where(k => !string.IsNullOrWhiteSpace(k)).ToList(),
                EditionCount = OpenLibraryJson.Int(doc["edition_count"]) ?? 0,
                Isbns = OpenLibraryJson.Strings(doc["isbn"]),
                Languages = OpenLibraryJson.Strings(doc["language"]),
                AuthorKeys = OpenLibraryJson.Strings(doc["author_key"]).Select(OpenLibraryJson.BareKey).Where(k => !string.IsNullOrWhiteSpace(k)).ToList(),
                AuthorNames = OpenLibraryJson.Strings(doc["author_name"]),
                Publishers = OpenLibraryJson.Strings(doc["publisher"]),
                PageCountMedian = OpenLibraryJson.Int(doc["number_of_pages_median"]),
                RatingsAverage = OpenLibraryJson.Decimal(doc["ratings_average"]) ?? 0m,
                RatingsCount = OpenLibraryJson.Int(doc["ratings_count"]) ?? 0,
                AmazonIds = OpenLibraryJson.Strings(doc["id_amazon"])
            };
        }
    }

    internal static class OpenLibraryJson
    {
        public static string String(JToken token)
        {
            if (token == null || token.Type == JTokenType.Null)
            {
                return null;
            }

            if (token.Type == JTokenType.String || token.Type == JTokenType.Integer || token.Type == JTokenType.Float)
            {
                var value = token.ToString().Trim();
                return value.Length == 0 ? null : value;
            }

            return null;
        }

        // Open Library text fields are either a string or {"type": "/type/text", "value": "..."}.
        public static string Text(JToken token)
        {
            if (token is JObject obj)
            {
                return String(obj["value"]);
            }

            return String(token);
        }

        public static List<string> Strings(JToken token)
        {
            if (token is JArray array)
            {
                return array.Select(String).Where(s => !string.IsNullOrWhiteSpace(s)).ToList();
            }

            var single = String(token);
            return single == null ? new List<string>() : new List<string> { single };
        }

        public static int? Int(JToken token)
        {
            var value = Long(token);
            return value.HasValue && value.Value <= int.MaxValue && value.Value >= int.MinValue ? (int)value.Value : null;
        }

        public static long? Long(JToken token)
        {
            if (token == null)
            {
                return null;
            }

            if (token.Type == JTokenType.Integer)
            {
                return token.Value<long>();
            }

            if (token.Type == JTokenType.Float)
            {
                return (long)token.Value<double>();
            }

            return long.TryParse(String(token), out var parsed) ? parsed : null;
        }

        public static decimal? Decimal(JToken token)
        {
            if (token == null)
            {
                return null;
            }

            if (token.Type == JTokenType.Integer || token.Type == JTokenType.Float)
            {
                return Math.Round(token.Value<decimal>(), 2);
            }

            return decimal.TryParse(String(token), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var parsed)
                ? Math.Round(parsed, 2)
                : null;
        }

        // "/authors/OL239800A" -> "OL239800A"; "OL239800A" stays as is.
        public static string BareKey(string key)
        {
            if (string.IsNullOrWhiteSpace(key))
            {
                return null;
            }

            var trimmed = key.Trim().TrimEnd('/');
            var slash = trimmed.LastIndexOf('/');
            var bare = slash >= 0 ? trimmed.Substring(slash + 1) : trimmed;
            return IsOpenLibraryKey(bare) ? bare.ToUpperInvariant() : null;
        }

        public static bool IsOpenLibraryKey(string key)
        {
            if (string.IsNullOrWhiteSpace(key) || key.Length < 4 ||
                !key.StartsWith("OL", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            var suffix = char.ToUpperInvariant(key[key.Length - 1]);
            if (suffix != 'A' && suffix != 'W' && suffix != 'M')
            {
                return false;
            }

            return key.Substring(2, key.Length - 3).All(char.IsDigit);
        }

        public static string NormalizeIsbn(string isbn)
        {
            if (string.IsNullOrWhiteSpace(isbn))
            {
                return null;
            }

            var cleaned = new string(isbn.Where(c => char.IsDigit(c) || c == 'X' || c == 'x').ToArray()).ToUpperInvariant();
            return cleaned.Length == 10 || cleaned.Length == 13 ? cleaned : null;
        }
    }
}
