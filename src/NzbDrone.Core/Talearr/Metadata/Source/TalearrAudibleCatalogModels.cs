using System;
using System.Collections.Generic;
using Newtonsoft.Json;

namespace NzbDrone.Core.Talearr.Metadata.Source
{
    // Talearr: one Audible product from an author's catalog, as the Audible fill reads it.
    public sealed class AudibleCatalogItem
    {
        public string Asin { get; set; }
        public string Title { get; set; }
        public string Subtitle { get; set; }
        public List<string> Authors { get; set; } = new List<string>();
        public List<string> Narrators { get; set; } = new List<string>();
        public List<AudibleCatalogSeries> Series { get; set; } = new List<AudibleCatalogSeries>();
        public int? RuntimeMinutes { get; set; }

        // yyyy-MM-dd
        public string ReleaseDate { get; set; }

        // "english", "german", ...
        public string Language { get; set; }

        // "unabridged", "abridged", ...
        public string FormatType { get; set; }

        // SinglePartBook, MultiPartBook, Periodical, PodcastParent, ...
        public string DeliveryType { get; set; }

        public string Publisher { get; set; }
        public string Summary { get; set; }
        public string ImageUrl { get; set; }
    }

    public sealed class AudibleCatalogSeries
    {
        public string Asin { get; set; }
        public string Title { get; set; }

        // "1", "1.5", "1-3"
        public string Sequence { get; set; }
    }

    // One page of an author search. Throttled or Failed means no answer: nothing is cached.
    public sealed class AudibleCatalogPage
    {
        public List<AudibleCatalogItem> Items { get; } = new List<AudibleCatalogItem>();
        public int TotalResults { get; set; }
        public bool Throttled { get; set; }
        public bool Failed { get; set; }

        public bool Answered => !Throttled && !Failed;
    }

    // The products response with the series and media response groups, which upstream's
    // AudibleCatalogProductResource does not read.
    internal sealed class AudibleAuthorCatalogResponse
    {
        [JsonProperty("total_results")]
        public int TotalResults { get; set; }

        [JsonProperty("products")]
        public List<AudibleAuthorCatalogProduct> Products { get; set; }
    }

    internal sealed class AudibleAuthorCatalogProduct
    {
        [JsonProperty("asin")]
        public string Asin { get; set; }

        [JsonProperty("title")]
        public string Title { get; set; }

        [JsonProperty("subtitle")]
        public string Subtitle { get; set; }

        [JsonProperty("authors")]
        public List<AudibleAuthorCatalogName> Authors { get; set; }

        [JsonProperty("narrators")]
        public List<AudibleAuthorCatalogName> Narrators { get; set; }

        [JsonProperty("series")]
        public List<AudibleAuthorCatalogSeries> Series { get; set; }

        [JsonProperty("runtime_length_min")]
        public int? RuntimeLengthMin { get; set; }

        [JsonProperty("release_date")]
        public DateTime? ReleaseDate { get; set; }

        [JsonProperty("language")]
        public string Language { get; set; }

        [JsonProperty("format_type")]
        public string FormatType { get; set; }

        [JsonProperty("content_delivery_type")]
        public string ContentDeliveryType { get; set; }

        [JsonProperty("publisher_name")]
        public string PublisherName { get; set; }

        [JsonProperty("merchandising_summary")]
        public string MerchandisingSummary { get; set; }

        [JsonProperty("product_images")]
        public Dictionary<string, string> ProductImages { get; set; }
    }

    internal sealed class AudibleAuthorCatalogName
    {
        [JsonProperty("name")]
        public string Name { get; set; }
    }

    internal sealed class AudibleAuthorCatalogSeries
    {
        [JsonProperty("asin")]
        public string Asin { get; set; }

        [JsonProperty("title")]
        public string Title { get; set; }

        [JsonProperty("sequence")]
        public string Sequence { get; set; }
    }
}
