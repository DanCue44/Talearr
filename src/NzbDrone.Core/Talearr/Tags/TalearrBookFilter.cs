using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using NzbDrone.Core.Books;

namespace NzbDrone.Core.Talearr.Tags
{
    // One custom filter row as the UI stores it: { key, value, type }. Value may be one value or
    // a list; a list matches when any value matches, or for the negative types when all do.
    public class TalearrBookFilterRule
    {
        public string Key { get; set; }
        public JsonElement Value { get; set; }
        public string Type { get; set; }
    }

    // What a filter can look at for one book row.
    public class TalearrBookFilterFacts
    {
        public int BookId { get; set; }
        public ICollection<int> Tags { get; set; } = new List<int>();
        public ICollection<int> AuthorTags { get; set; } = new List<int>();
        public ICollection<string> Genres { get; set; } = new List<string>();
        public ICollection<string> Narrators { get; set; } = new List<string>();
    }

    public interface ITalearrBookFilterService
    {
        // Ids of the books that pass every supported rule, or null when no rule needs TaleArr.
        HashSet<int> MatchingBookIds(string filtersJson);
    }

    // TaleArr: server side evaluation of the custom filters on the Wanted pages, which are paged
    // in SQL. Book tags live in talearr.db, so the matching ids are worked out here and the page
    // query only gets an id list. Mirrors frontend filterCollection and filterTypePredicates.
    public class TalearrBookFilterService : ITalearrBookFilterService
    {
        public static readonly string[] SupportedKeys = { "tags", "authorTags", "genres", "narrator" };

        private static readonly JsonSerializerOptions JsonOptions = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true
        };

        private readonly IBookService _bookService;
        private readonly IAuthorService _authorService;
        private readonly ITalearrBookTagService _bookTagService;

        public TalearrBookFilterService(IBookService bookService, IAuthorService authorService, ITalearrBookTagService bookTagService)
        {
            _bookService = bookService;
            _authorService = authorService;
            _bookTagService = bookTagService;
        }

        public HashSet<int> MatchingBookIds(string filtersJson)
        {
            var rules = ParseRules(filtersJson);
            if (!rules.Any())
            {
                return null;
            }

            var bookTags = _bookTagService.GetAll();
            var authors = _authorService.GetAllAuthors()
                .GroupBy(a => a.Id)
                .ToDictionary(g => g.Key, g => g.First());

            var result = new HashSet<int>();
            foreach (var book in _bookService.GetAllBooks())
            {
                var facts = new TalearrBookFilterFacts
                {
                    BookId = book.Id,
                    Tags = bookTags.TryGetValue(book.Id, out var tags) ? tags : new List<int>(),
                    AuthorTags = AuthorTagsFor(authors.GetValueOrDefault(book.AuthorId), book.MediaType),
                    Genres = book.Genres ?? new List<string>(),
                    Narrators = string.IsNullOrWhiteSpace(book.Narrator) ? new List<string>() : new List<string> { book.Narrator }
                };

                if (rules.All(r => Matches(facts, r)))
                {
                    result.Add(book.Id);
                }
            }

            return result;
        }

        // The author's tags for the book's format, as the author editor shows them; authors saved
        // before the per-format split only have the shared list.
        public static ICollection<int> AuthorTagsFor(Author author, BookMediaType mediaType)
        {
            if (author == null)
            {
                return new List<int>();
            }

            var perFormat = mediaType == BookMediaType.Ebook ? author.EbookTags : author.AudiobookTags;
            if (author.AudiobookTags == null && author.EbookTags == null)
            {
                perFormat = author.Tags;
            }

            return (perFormat ?? new HashSet<int>()).ToList();
        }

        public static List<TalearrBookFilterRule> ParseRules(string filtersJson)
        {
            if (string.IsNullOrWhiteSpace(filtersJson))
            {
                return new List<TalearrBookFilterRule>();
            }

            try
            {
                return (JsonSerializer.Deserialize<List<TalearrBookFilterRule>>(filtersJson, JsonOptions) ?? new List<TalearrBookFilterRule>())
                    .Where(r => r?.Key != null && SupportedKeys.Contains(r.Key, StringComparer.OrdinalIgnoreCase))
                    .ToList();
            }
            catch (JsonException)
            {
                return new List<TalearrBookFilterRule>();
            }
        }

        public static bool Matches(TalearrBookFilterFacts facts, TalearrBookFilterRule rule)
        {
            var values = Values(rule.Value);
            if (!values.Any())
            {
                return true;
            }

            var type = (rule.Type ?? "equal").Trim();
            var negative = type.Equals("notContains", StringComparison.OrdinalIgnoreCase) ||
                           type.Equals("notEqual", StringComparison.OrdinalIgnoreCase);

            Func<string, bool> test;
            switch (rule.Key.ToLowerInvariant())
            {
                case "tags":
                    test = v => ArrayTest(facts.Tags, v, negative);
                    break;
                case "authortags":
                    test = v => ArrayTest(facts.AuthorTags, v, negative);
                    break;
                case "genres":
                    test = v => facts.Genres.Any(g => string.Equals(g, v, StringComparison.OrdinalIgnoreCase)) != negative;
                    break;
                case "narrator":
                    test = v => StringTest(string.Join(", ", facts.Narrators), v, type);
                    break;
                default:
                    return true;
            }

            return negative ? values.All(test) : values.Any(test);
        }

        private static bool ArrayTest(ICollection<int> items, string value, bool negative)
        {
            return int.TryParse(value, out var id) && items.Contains(id) != negative;
        }

        private static bool StringTest(string item, string value, string type)
        {
            item = (item ?? string.Empty).ToLowerInvariant();
            value = value.ToLowerInvariant();

            switch (type.ToLowerInvariant())
            {
                case "contains": return item.Contains(value);
                case "notcontains": return !item.Contains(value);
                case "notequal": return item != value;
                case "startswith": return item.StartsWith(value);
                case "notstartswith": return !item.StartsWith(value);
                case "endswith": return item.EndsWith(value);
                case "notendswith": return !item.EndsWith(value);
                default: return item == value;
            }
        }

        private static List<string> Values(JsonElement value)
        {
            switch (value.ValueKind)
            {
                case JsonValueKind.Array:
                    return value.EnumerateArray().Select(Scalar).Where(v => v != null).ToList();
                case JsonValueKind.Undefined:
                case JsonValueKind.Null:
                    return new List<string>();
                default:
                    var single = Scalar(value);
                    return single == null ? new List<string>() : new List<string> { single };
            }
        }

        private static string Scalar(JsonElement value)
        {
            switch (value.ValueKind)
            {
                case JsonValueKind.String:
                    return value.GetString();
                case JsonValueKind.Number:
                case JsonValueKind.True:
                case JsonValueKind.False:
                    return value.GetRawText();
                default:
                    return null;
            }
        }
    }
}
