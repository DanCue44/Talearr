using System;
using System.Collections.Generic;

namespace NzbDrone.Core.Talearr.Metadata.Source
{
    // Talearr: plain records for what the Hardcover metadata client reads. They mirror the
    // Hardcover GraphQL rows one to one (field names in the comments) so the mapper can be tested
    // without HTTP. Every field and its meaning is listed under "Schema assumptions" in
    // docs/TALEARR_METADATA_SOURCE.md; none of them has been checked with a live token yet.

    // authors row.
    public class HardcoverAuthorRecord
    {
        public int Id { get; set; }                       // id
        public string Name { get; set; }                  // name
        public string Slug { get; set; }                  // slug
        public string Bio { get; set; }                   // bio
        public string BornDate { get; set; }              // born_date (date, "YYYY-MM-DD")
        public string DeathDate { get; set; }             // death_date
        public int? CanonicalId { get; set; }             // canonical_id (set when this row is a duplicate)
        public string ImageUrl { get; set; }              // image { url }
        public int BooksCount { get; set; }               // books_count
        public int UsersCount { get; set; }               // users_count (followers)
        public DateTime? UpdatedAt { get; set; }          // updated_at
        public List<string> AlternateNames { get; set; } = new List<string>(); // alternate_names (jsonb array of strings, assumed)

        // identifiers (jsonb). Assumed shape: { "<key>": "value" } or { "<key>": ["value", ...] },
        // keys such as viaf, isni, goodreads, openlibrary, amazon. Keys are lower-cased.
        public Dictionary<string, List<string>> Identifiers { get; set; } = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
    }

    // books row, read for one author.
    public class HardcoverBookRecord
    {
        public int Id { get; set; }                       // id
        public string Title { get; set; }                 // title
        public string Subtitle { get; set; }              // subtitle
        public string Slug { get; set; }                  // slug
        public string Description { get; set; }           // description
        public string ReleaseDate { get; set; }           // release_date
        public int? ReleaseYear { get; set; }             // release_year
        public int? Pages { get; set; }                   // pages
        public int? AudioSeconds { get; set; }            // audio_seconds
        public decimal? Rating { get; set; }              // rating (0-5)
        public int RatingsCount { get; set; }             // ratings_count
        public int UsersCount { get; set; }               // users_count
        public bool Compilation { get; set; }             // compilation (compilation or anthology)
        public int? BookCategoryId { get; set; }          // book_category_id (8 = Collection; reported, not used for omnibus)
        public int? LiteraryTypeId { get; set; }          // literary_type_id
        public int BookStatusId { get; set; } = 1;        // book_status_id (1 OK, 2 To Review, 3 Deleted, 4 Deduped)
        public int? CanonicalId { get; set; }             // canonical_id (set when this row is a duplicate)
        public bool IsPartialBook { get; set; }           // is_partial_book
        public int? DefaultAudioEditionId { get; set; }   // default_audio_edition_id
        public int? DefaultEbookEditionId { get; set; }   // default_ebook_edition_id
        public int? DefaultPhysicalEditionId { get; set; } // default_physical_edition_id
        public string ImageUrl { get; set; }              // image { url }
        public DateTime? UpdatedAt { get; set; }          // updated_at
        public List<string> Genres { get; set; } = new List<string>(); // cached_tags.Genre[].tag (assumed shape)
        public List<string> Moods { get; set; } = new List<string>();  // cached_tags.Mood[].tag (assumed shape)

        // contributions(where: { author_id: { _eq: <author> } }) { contribution }: this author's roles on the book.
        public List<string> AuthorRoles { get; set; } = new List<string>();

        // book_series { series_id position details featured compilation }
        public List<HardcoverBookSeriesRecord> Series { get; set; } = new List<HardcoverBookSeriesRecord>();

        public List<HardcoverEditionRecord> Editions { get; set; } = new List<HardcoverEditionRecord>();
    }

    public class HardcoverBookSeriesRecord
    {
        public int SeriesId { get; set; }                 // series_id
        public decimal? Position { get; set; }            // position (float8; for collections the first book)
        public string Details { get; set; }               // details (text form of the position, "1-3")
        public bool Featured { get; set; }                // featured
        public bool Compilation { get; set; }             // compilation
    }

    // editions row.
    public class HardcoverEditionRecord
    {
        public int Id { get; set; }                       // id
        public int BookId { get; set; }                   // book_id
        public string Title { get; set; }                 // title
        public string Subtitle { get; set; }              // subtitle
        public string Isbn10 { get; set; }                // isbn_10
        public string Isbn13 { get; set; }                // isbn_13
        public string Asin { get; set; }                  // asin
        public int ReadingFormatId { get; set; }          // reading_format_id (1 Physical, 2 Audio, 3 Both, 4 Ebook)
        public string EditionFormat { get; set; }         // edition_format ("Hardcover", "Kindle Edition", "Audible Audio")
        public string EditionInformation { get; set; }    // edition_information
        public string ReleaseDate { get; set; }           // release_date
        public int? Pages { get; set; }                   // pages
        public int? AudioSeconds { get; set; }            // audio_seconds
        public decimal? Rating { get; set; }              // rating
        public int UsersCount { get; set; }               // users_count
        public int? CanonicalId { get; set; }             // canonical_id (duplicate edition)
        public string LanguageCode2 { get; set; }         // language { code2 }
        public string LanguageCode3 { get; set; }         // language { code3 }
        public string LanguageName { get; set; }          // language { language }
        public string Publisher { get; set; }             // publisher { name }
        public string ImageUrl { get; set; }              // image { url }
        public DateTime? UpdatedAt { get; set; }          // updated_at

        // cached_contributors (json): [{ "author": { "id", "name" }, "contribution": "Narrator" }, ...]
        public List<HardcoverContributorRecord> Contributors { get; set; } = new List<HardcoverContributorRecord>();
    }

    public class HardcoverContributorRecord
    {
        public int? AuthorId { get; set; }
        public string Name { get; set; }
        public string Contribution { get; set; }
    }

    // series row.
    public class HardcoverSeriesRecord
    {
        public int Id { get; set; }                       // id
        public string Name { get; set; }                  // name
        public string Slug { get; set; }                  // slug
        public string Description { get; set; }           // description
        public int BooksCount { get; set; }               // books_count
        public int? PrimaryBooksCount { get; set; }       // primary_books_count
        public bool? IsCompleted { get; set; }            // is_completed
        public int? CanonicalId { get; set; }             // canonical_id
    }

    // Everything read for one author. Books are the author's own works (primary contribution
    // role), with editions attached. Duplicates (status 4 or canonical_id) are kept apart in
    // DuplicateBooks so the mapper can turn them into aliases of their canonical work.
    public class HardcoverAuthorCatalog
    {
        public HardcoverAuthorRecord Author { get; set; }
        public List<HardcoverBookRecord> Books { get; set; } = new List<HardcoverBookRecord>();
        public List<HardcoverBookRecord> DuplicateBooks { get; set; } = new List<HardcoverBookRecord>();
        public Dictionary<int, HardcoverSeriesRecord> Series { get; set; } = new Dictionary<int, HardcoverSeriesRecord>();

        // Diagnostics for the shadow report.
        public int RequestCount { get; set; }
        public int SkippedNotPrimary { get; set; }
        public int SkippedDeleted { get; set; }
        public int SkippedPartial { get; set; }
        public DateTime FetchedAt { get; set; }
    }
}
