using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using NzbDrone.Core.Talearr.ReaderHandoff;

namespace NzbDrone.Core.Talearr.EbookDelivery
{
    // Builds the feed's links. Every link stays under the feed root, and when the reader signed in
    // with ?apikey= (instead of HTTP Basic) the key is carried on, because readers do not add it back.
    public sealed class TalearrOpdsLinks
    {
        public TalearrOpdsLinks(string root, string apiKey)
        {
            Root = (root ?? string.Empty).TrimEnd('/');
            ApiKey = string.IsNullOrWhiteSpace(apiKey) ? null : apiKey;
        }

        public string Root { get; }
        public string ApiKey { get; }

        public string To(string path, params (string Name, string Value)[] query)
        {
            var parameters = query
                .Where(q => !string.IsNullOrEmpty(q.Value))
                .Select(q => Uri.EscapeDataString(q.Name) + "=" + Uri.EscapeDataString(q.Value))
                .ToList();

            if (ApiKey != null)
            {
                parameters.Add("apikey=" + Uri.EscapeDataString(ApiKey));
            }

            var relative = string.IsNullOrEmpty(path) ? string.Empty : "/" + path.TrimStart('/');
            return Root + relative + (parameters.Count == 0 ? string.Empty : "?" + string.Join("&", parameters));
        }

        // OpenSearch template: {searchTerms} must stay unescaped.
        public string SearchTemplate()
        {
            return Root + "/search?q={searchTerms}" + (ApiKey == null ? string.Empty : "&apikey=" + Uri.EscapeDataString(ApiKey));
        }
    }

    public sealed class TalearrOpdsPage<T>
    {
        public List<T> Items { get; set; }
        public int Page { get; set; }
        public int PageSize { get; set; }
        public int Total { get; set; }
        public int PageCount => Total == 0 ? 1 : (Total + PageSize - 1) / PageSize;

        public static TalearrOpdsPage<T> Of(IReadOnlyList<T> all, int page, int pageSize)
        {
            pageSize = TalearrOpdsSettings.ClampPageSize(pageSize);
            var total = all?.Count ?? 0;
            var pageCount = total == 0 ? 1 : (total + pageSize - 1) / pageSize;
            page = Math.Clamp(page, 1, pageCount);

            return new TalearrOpdsPage<T>
            {
                Items = (all ?? new List<T>()).Skip((page - 1) * pageSize).Take(pageSize).ToList(),
                Page = page,
                PageSize = pageSize,
                Total = total
            };
        }
    }

    // Talearr: OPDS 1.2 (https://specs.opds.io/opds-1.2) Atom documents for the catalog.
    public static class TalearrOpdsFeedWriter
    {
        public const string NavigationType = "application/atom+xml;profile=opds-catalog;kind=navigation";
        public const string AcquisitionType = "application/atom+xml;profile=opds-catalog;kind=acquisition";
        public const string OpenSearchType = "application/opensearchdescription+xml";
        public const string KepubType = "application/kepub+zip";

        public static readonly XNamespace Atom = "http://www.w3.org/2005/Atom";
        public static readonly XNamespace Dc = "http://purl.org/dc/terms/";
        public static readonly XNamespace Opds = "http://opds-spec.org/2010/catalog";
        public static readonly XNamespace OpenSearch = "http://a9.com/-/spec/opensearch/1.1/";

        private static readonly Regex HtmlTag = new Regex("<[^>]+>", RegexOptions.Compiled);

        public static string MimeType(string path)
        {
            var name = (path ?? string.Empty).ToLowerInvariant();

            if (name.EndsWith(".kepub.epub", StringComparison.Ordinal))
            {
                return KepubType;
            }

            return Path.GetExtension(name) switch
            {
                ".epub" => "application/epub+zip",
                ".pdf" => "application/pdf",
                ".azw3" => "application/x-mobi8-ebook",
                ".mobi" => "application/x-mobipocket-ebook",
                ".azw" => "application/vnd.amazon.ebook",
                ".fb2" => "application/x-fictionbook+xml",
                ".cbz" => "application/vnd.comicbook+zip",
                ".cbr" => "application/vnd.comicbook-rar",
                ".djvu" => "image/vnd.djvu",
                ".txt" => "text/plain",
                ".rtf" => "application/rtf",
                ".doc" => "application/msword",
                ".docx" => "application/vnd.openxmlformats-officedocument.wordprocessingml.document",
                ".lit" => "application/x-ms-reader",
                _ => "application/octet-stream"
            };
        }

        public static string Serialize(XDocument document)
        {
            var builder = new StringBuilder();
            using (var writer = new Utf8StringWriter(builder))
            {
                document.Save(writer);
            }

            return builder.ToString();
        }

        public static XDocument Root(TalearrOpdsLinks links, TalearrOpdsSnapshot snapshot)
        {
            var feed = Feed(links, "urn:talearr:opds:root", "Talearr", snapshot.Updated, NavigationType, links.To(string.Empty), null);

            feed.Add(Navigation("urn:talearr:opds:recent", "Recently added", $"The newest ebooks first ({snapshot.Books.Count} in all)", links.To("recent"), AcquisitionType, snapshot.Updated, "http://opds-spec.org/sort/new"));
            feed.Add(Navigation("urn:talearr:opds:authors", "Authors", $"{snapshot.Authors.Count} authors with ebooks", links.To("authors"), NavigationType, snapshot.Updated));
            feed.Add(Navigation("urn:talearr:opds:series", "Series", $"{snapshot.Series.Count} series with ebooks", links.To("series"), NavigationType, snapshot.Updated));
            feed.Add(Navigation("urn:talearr:opds:books", "All books", $"{snapshot.Books.Count} ebooks by author and title", links.To("books"), AcquisitionType, snapshot.Updated));

            return Document(feed);
        }

        public static XDocument Groups(TalearrOpdsLinks links, string id, string title, string path, TalearrOpdsPage<TalearrOpdsGroup> page, string itemPath, DateTime updated)
        {
            var feed = Feed(links, id, title, updated, NavigationType, links.To(path, Page(page.Page)), links.To(string.Empty));
            AddPaging(feed, links, path, page, NavigationType, Array.Empty<(string, string)>());

            foreach (var group in page.Items)
            {
                feed.Add(Navigation($"{id}:{group.Id}", group.Title, group.BookCount == 1 ? "1 book" : $"{group.BookCount} books", links.To($"{itemPath}/{group.Id}"), AcquisitionType, group.Updated));
            }

            return Document(feed);
        }

        public static XDocument Books(TalearrOpdsLinks links, string id, string title, string path, TalearrOpdsPage<TalearrOpdsBook> page, DateTime updated, bool offerKepub, string up, params (string Name, string Value)[] query)
        {
            var self = links.To(path, query.Concat(new[] { Page(page.Page) }).ToArray());
            var feed = Feed(links, id, title, updated, AcquisitionType, self, up ?? links.To(string.Empty));
            AddPaging(feed, links, path, page, AcquisitionType, query);

            foreach (var book in page.Items)
            {
                feed.Add(BookEntry(links, book, offerKepub));
            }

            return Document(feed);
        }

        public static XDocument OpenSearchDescription(TalearrOpdsLinks links)
        {
            var root = new XElement(OpenSearch + "OpenSearchDescription",
                new XElement(OpenSearch + "ShortName", "Talearr"),
                new XElement(OpenSearch + "Description", "Search the Talearr ebook library by title, author or series"),
                new XElement(OpenSearch + "InputEncoding", "UTF-8"),
                new XElement(OpenSearch + "OutputEncoding", "UTF-8"),
                new XElement(OpenSearch + "Url",
                    new XAttribute("type", AcquisitionType),
                    new XAttribute("template", links.SearchTemplate())));

            return Document(root);
        }

        public static XElement BookEntry(TalearrOpdsLinks links, TalearrOpdsBook book, bool offerKepub)
        {
            var entry = new XElement(Atom + "entry",
                new XElement(Atom + "title", book.Title ?? string.Empty),
                new XElement(Atom + "id", $"urn:talearr:book:{book.BookId}"),
                new XElement(Atom + "updated", Timestamp(book.Added)));

            if (!string.IsNullOrWhiteSpace(book.AuthorName))
            {
                entry.Add(new XElement(Atom + "author",
                    new XElement(Atom + "name", book.AuthorName),
                    new XElement(Atom + "uri", links.To($"authors/{book.AuthorId}"))));
            }

            if (!string.IsNullOrWhiteSpace(book.Language))
            {
                entry.Add(new XElement(Dc + "language", book.Language));
            }

            if (!string.IsNullOrWhiteSpace(book.Publisher))
            {
                entry.Add(new XElement(Dc + "publisher", book.Publisher));
            }

            if (book.ReleaseDate.HasValue)
            {
                entry.Add(new XElement(Dc + "issued", book.ReleaseDate.Value.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)));
            }

            foreach (var genre in book.Genres.Take(10))
            {
                entry.Add(new XElement(Atom + "category", new XAttribute("term", genre), new XAttribute("label", genre)));
            }

            var summary = PlainText(book.Overview);
            var series = book.Series.FirstOrDefault();
            if (series != null)
            {
                var label = string.IsNullOrWhiteSpace(series.Position) ? series.Title : $"{series.Title} #{series.Position}";
                summary = string.IsNullOrEmpty(summary) ? label : label + ". " + summary;
            }

            if (!string.IsNullOrWhiteSpace(book.Subtitle))
            {
                summary = string.IsNullOrEmpty(summary) ? book.Subtitle : book.Subtitle + ". " + summary;
            }

            if (!string.IsNullOrEmpty(summary))
            {
                entry.Add(new XElement(Atom + "summary", new XAttribute("type", "text"), summary));
            }

            entry.Add(Link("http://opds-spec.org/image", links.To($"cover/{book.BookId}"), "image/jpeg"));
            entry.Add(Link("http://opds-spec.org/image/thumbnail", links.To($"cover/{book.BookId}"), "image/jpeg"));

            foreach (var s in book.Series)
            {
                entry.Add(Link("related", links.To($"series/{s.SeriesId}"), AcquisitionType, $"More in {s.Title}"));
            }

            foreach (var file in book.Files.OrderBy(f => FormatRank(f.Path)))
            {
                var link = Link("http://opds-spec.org/acquisition", links.To($"file/{file.Id}"), MimeType(file.Path), Path.GetExtension(file.Path).TrimStart('.').ToUpperInvariant());
                if (file.Size > 0)
                {
                    link.Add(new XAttribute("length", file.Size));
                }

                entry.Add(link);

                if (offerKepub && TalearrReaderHandoffService.IsConvertibleEpub(file.Path))
                {
                    entry.Add(Link("http://opds-spec.org/acquisition", links.To($"file/{file.Id}/kepub"), KepubType, "KEPUB"));
                }
            }

            return entry;
        }

        public static string PlainText(string html)
        {
            if (string.IsNullOrWhiteSpace(html))
            {
                return string.Empty;
            }

            var text = html.Replace("<br>", "\n").Replace("<br/>", "\n").Replace("<br />", "\n").Replace("</p>", "\n");
            text = System.Net.WebUtility.HtmlDecode(HtmlTag.Replace(text, string.Empty));
            text = Regex.Replace(text, "[ \t]+", " ");
            text = Regex.Replace(text, "\\s*\n\\s*", "\n").Trim();

            return text.Length > 2000 ? text.Substring(0, 2000).TrimEnd() + "..." : text;
        }

        private static int FormatRank(string path)
        {
            var order = new[] { ".epub", ".azw3", ".mobi", ".pdf" };
            var index = Array.IndexOf(order, Path.GetExtension(path ?? string.Empty).ToLowerInvariant());
            return index < 0 ? order.Length : index;
        }

        private static (string Name, string Value) Page(int page)
        {
            return ("page", page > 1 ? page.ToString(CultureInfo.InvariantCulture) : null);
        }

        private static XDocument Document(XElement root)
        {
            return new XDocument(new XDeclaration("1.0", "utf-8", null), root);
        }

        private static XElement Feed(TalearrOpdsLinks links, string id, string title, DateTime updated, string selfType, string self, string up)
        {
            var feed = new XElement(Atom + "feed",
                new XAttribute(XNamespace.Xmlns + "dc", Dc),
                new XAttribute(XNamespace.Xmlns + "opds", Opds),
                new XAttribute(XNamespace.Xmlns + "opensearch", OpenSearch),
                new XElement(Atom + "id", id),
                new XElement(Atom + "title", title),
                new XElement(Atom + "updated", Timestamp(updated)),
                new XElement(Atom + "author", new XElement(Atom + "name", "Talearr")),
                Link("self", self, selfType),
                Link("start", links.To(string.Empty), NavigationType),
                Link("search", links.To("opensearch.xml"), OpenSearchType, "Search"));

            if (up != null)
            {
                feed.Add(Link("up", up, NavigationType));
            }

            return feed;
        }

        private static void AddPaging<T>(XElement feed, TalearrOpdsLinks links, string path, TalearrOpdsPage<T> page, string type, (string Name, string Value)[] query)
        {
            feed.Add(new XElement(OpenSearch + "totalResults", page.Total));
            feed.Add(new XElement(OpenSearch + "itemsPerPage", page.PageSize));
            feed.Add(new XElement(OpenSearch + "startIndex", ((page.Page - 1) * page.PageSize) + 1));

            if (page.PageCount <= 1)
            {
                return;
            }

            string To(int number) => links.To(path, query.Concat(new[] { Page(number) }).ToArray());

            feed.Add(Link("first", To(1), type));
            feed.Add(Link("last", To(page.PageCount), type));

            if (page.Page > 1)
            {
                feed.Add(Link("previous", To(page.Page - 1), type));
            }

            if (page.Page < page.PageCount)
            {
                feed.Add(Link("next", To(page.Page + 1), type));
            }
        }

        private static XElement Navigation(string id, string title, string content, string href, string type, DateTime updated, string rel = "subsection")
        {
            return new XElement(Atom + "entry",
                new XElement(Atom + "title", title ?? string.Empty),
                new XElement(Atom + "id", id),
                new XElement(Atom + "updated", Timestamp(updated)),
                new XElement(Atom + "content", new XAttribute("type", "text"), content ?? string.Empty),
                Link(rel, href, type));
        }

        private static XElement Link(string rel, string href, string type, string title = null)
        {
            var link = new XElement(Atom + "link",
                new XAttribute("rel", rel),
                new XAttribute("href", href),
                new XAttribute("type", type));

            if (title != null)
            {
                link.Add(new XAttribute("title", title));
            }

            return link;
        }

        private static string Timestamp(DateTime value)
        {
            var utc = value == default ? DateTime.UnixEpoch : (value.Kind == DateTimeKind.Unspecified ? DateTime.SpecifyKind(value, DateTimeKind.Utc) : value.ToUniversalTime());
            return utc.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture);
        }

        private sealed class Utf8StringWriter : StringWriter
        {
            public Utf8StringWriter(StringBuilder builder)
                : base(builder, CultureInfo.InvariantCulture)
            {
            }

            public override Encoding Encoding => Encoding.UTF8;
        }
    }
}
