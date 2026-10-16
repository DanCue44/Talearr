namespace Chaptarr.Core.Test.Talearr.Recordings
{
    // Recorded 2026-10-16 from the live Audible catalog and Audnexus (response groups trimmed to
    // the fields TaleArr reads). "The Firm" by John Grisham: B002PE36VO is the stale US ASIN the
    // metadata server stores for the Scott Brick edition (Audible answers a bare stub), B002V5BSLC
    // is the current US product, B00NIXU8GW is the UK product, and the US search also returns an
    // abridged German product (B00TWO6SJM, Charles Brauer).
    internal static class AudibleRecordings
    {
        public const string UsBatch = """
{"products":[{"asin":"0739366203"},{"asin":"B002PE36VO"},{"asin":"B002V5BSLC","title":"The Firm","format_type":"unabridged","runtime_length_min":1030,"narrators":[{"name":"Scott Brick"}],"authors":[{"name":"John Grisham"}],"publisher_name":"Random House Audio","release_date":"2009-09-16","issue_date":"2009-09-16","language":"english","content_type":"Product","content_delivery_type":"MultiPartBook"},{"asin":"B004FU0XQ2"},{"asin":"B004UZWZ18"},{"asin":"B008QT29CE"},{"asin":"B00FG9FPRI"},{"asin":"B072BTP7BD"},{"asin":"B079VB1PFQ"},{"asin":"B079VH2X33"},{"asin":"B07FB4HFD8"},{"asin":"B0BTYTFG28"}],"total_results":12}
""";

        public const string UkBatch = """
{"products":[{"asin":"B00NIXGHQ2"},{"asin":"B00NIXKS5S"},{"asin":"B00NIXU8GW","title":"The Firm","format_type":"unabridged","runtime_length_min":1029,"narrators":[{"name":"Scott Brick"}],"authors":[{"name":"John Grisham"}],"publisher_name":"Hodder & Stoughton","release_date":"2014-09-25","issue_date":"2014-09-25","language":"english","content_type":"Product","content_delivery_type":"MultiPartBook"},{"asin":"B07BC4WMF8"}],"total_results":4}
""";

        public const string UsSearchTheFirm = """
{"products":[{"asin":"B002V5BSLC","title":"The Firm","format_type":"unabridged","runtime_length_min":1030,"narrators":[{"name":"Scott Brick"}],"authors":[{"name":"John Grisham"}],"publisher_name":"Random House Audio","release_date":"2009-09-16","issue_date":"2009-09-16","language":"english","content_type":"Product","content_delivery_type":"MultiPartBook"},{"asin":"B0BYKFH2RN","title":"The Exchange","subtitle":"After The Firm","format_type":"unabridged","runtime_length_min":541,"narrators":[{"name":"Edoardo Ballerini"}],"authors":[{"name":"John Grisham"}],"publisher_name":"Random House Audio","release_date":"2023-10-17","issue_date":"2023-10-17","language":"english","content_type":"Product","content_delivery_type":"SinglePartBook"},{"asin":"B0D4WZ3D4S","title":"El intercambio (La tapadera 2)","subtitle":"La tapadera 2 [The Firm, Book 2]","format_type":"unabridged","runtime_length_min":694,"narrators":[{"name":"Carlos Canales"}],"authors":[{"name":"John Grisham"},{"name":"Ana Isabel Sánchez Díez - translator"}],"publisher_name":"Penguin Random House Audio","release_date":"2024-05-23","issue_date":"2024-05-23","language":"spanish","content_type":"Product","content_delivery_type":"SinglePartBook"},{"asin":"B0GYFFPZDS","title":"Die Firma","subtitle":"Roman","format_type":"unabridged","runtime_length_min":1031,"narrators":[{"name":"Johannes Klaußner"}],"authors":[{"name":"John Grisham"},{"name":"Andreas Brandhorst - translator"}],"publisher_name":"Random House Audio","release_date":"2026-08-10","issue_date":"2026-08-10","language":"german","content_type":"Product","content_delivery_type":"MultiPartBook"},{"asin":"B00TWO6SJM","title":"Die Firma","format_type":"abridged","runtime_length_min":237,"narrators":[{"name":"Charles Brauer"}],"authors":[{"name":"John Grisham"},{"name":"Christel Wiemken"}],"publisher_name":"Random House Audio, Deutschland","release_date":"2005-03-29","issue_date":"2005-03-29","language":"german","content_type":"Product","content_delivery_type":"SinglePartBook"},{"asin":"B0CVHG8DBZ","title":"Die Entführung","subtitle":"Die große Fortsetzung des Weltbestsellers \"Die Firma\"","format_type":"unabridged","runtime_length_min":777,"narrators":[{"name":"Charles Brauer"}],"authors":[{"name":"John Grisham"},{"name":"Imke Walsh-Araya - Übersetzer"},{"name":"Bea Reiter - Übersetzer"}],"publisher_name":"Random House Audio, Deutschland","release_date":"2024-02-26","issue_date":"2024-02-26","language":"german","content_type":"Product","content_delivery_type":"MultiPartBook"},{"asin":"B0CVHGT86L","title":"Die Entführung","subtitle":"Die große Fortsetzung des Weltbestsellers \"Die Firma\"","format_type":"abridged","runtime_length_min":700,"narrators":[{"name":"Charles Brauer"}],"authors":[{"name":"John Grisham"},{"name":"Imke Walsh-Araya - Übersetzer"},{"name":"Bea Reiter - Übersetzer"}],"publisher_name":"Random House Audio, Deutschland","release_date":"2024-08-26","issue_date":"2024-08-26","language":"german","content_type":"Product","content_delivery_type":"SinglePartBook"}],"total_results":7}
""";

        public const string AudnexusB002V5BSLC = """
{"asin":"B002V5BSLC","title":"The Firm","formatType":"unabridged","runtimeLengthMin":1030,"narrators":[{"name":"Scott Brick"}],"authors":[{"name":"John Grisham"}],"publisherName":"Random House Audio","releaseDate":"2009-09-16T00:00:00.000Z","language":"english","region":"us"}
""";

        public const string AudnexusRegionUnavailable = """
{"error":{"code":"REGION_UNAVAILABLE","message":"Item not available in region 'us' for ASIN: B002PE36VO","details":{"asin":"B002PE36VO","code":"REGION_UNAVAILABLE"}}}
""";
    }
}
