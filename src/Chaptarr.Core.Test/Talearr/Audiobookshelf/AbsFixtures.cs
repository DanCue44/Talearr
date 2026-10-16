namespace Chaptarr.Core.Test.Talearr.Audiobookshelf
{
    // Recorded-shape fixtures for the Audiobookshelf 2.x API (GET /api/libraries and
    // GET /api/libraries/{id}/items?minified=1), trimmed to the fields TaleArr reads plus a few it
    // ignores. The layout follows a real Unraid setup: Audiobookshelf mounts the ebooks at /books
    // while TaleArr mounts the same share at /ebooks; audiobooks are /audiobooks on both. Titles
    // are public books; ids, paths and keys are made up.
    public static class AbsFixtures
    {
        public const string AudiobookLibraryId = "68a674a7-0cb0-4f07-a30c-eabb7cfbddf5";
        public const string EbookLibraryId = "c2b97462-2209-4dd0-bd05-5cf2f12a63fc";
        public const string AudiobookFolderId = "6ddb6044-f09d-483e-85fd-161528c942a6";
        public const string EbookFolderId = "cdb8e584-6eb1-449c-a6c9-1b8c08d60342";

        public const string Libraries = @"{
  ""libraries"": [
    {
      ""id"": ""68a674a7-0cb0-4f07-a30c-eabb7cfbddf5"",
      ""name"": ""Audiobooks"",
      ""folders"": [
        { ""id"": ""6ddb6044-f09d-483e-85fd-161528c942a6"", ""fullPath"": ""/audiobooks"", ""libraryId"": ""68a674a7-0cb0-4f07-a30c-eabb7cfbddf5"", ""addedAt"": 1765671442578 }
      ],
      ""displayOrder"": 1,
      ""icon"": ""audiobookshelf"",
      ""mediaType"": ""book"",
      ""provider"": ""audible"",
      ""settings"": { ""coverAspectRatio"": 1, ""disableWatcher"": false, ""skipMatchingMediaWithAsin"": false, ""audiobooksOnly"": false },
      ""lastScan"": 1760600000000,
      ""createdAt"": 1765671442578
    },
    {
      ""id"": ""c2b97462-2209-4dd0-bd05-5cf2f12a63fc"",
      ""name"": ""Ebooks"",
      ""folders"": [
        { ""id"": ""cdb8e584-6eb1-449c-a6c9-1b8c08d60342"", ""fullPath"": ""/books"", ""libraryId"": ""c2b97462-2209-4dd0-bd05-5cf2f12a63fc"", ""addedAt"": 1760131118981 }
      ],
      ""displayOrder"": 2,
      ""icon"": ""book"",
      ""mediaType"": ""book"",
      ""provider"": ""google"",
      ""settings"": { ""coverAspectRatio"": 1, ""disableWatcher"": false, ""audiobooksOnly"": false }
    },
    {
      ""id"": ""lib-podcasts"",
      ""name"": ""Podcasts"",
      ""folders"": [ { ""id"": ""fol-podcasts"", ""fullPath"": ""/podcasts"", ""libraryId"": ""lib-podcasts"" } ],
      ""mediaType"": ""podcast"",
      ""settings"": { ""disableWatcher"": false }
    }
  ]
}";

        // Page 0 of the audiobook library with limit 200: total 4, one page.
        public const string AudiobookItems = @"{
  ""results"": [
    {
      ""id"": ""li-steve-jobs"",
      ""ino"": ""649644248522215260"",
      ""libraryId"": ""68a674a7-0cb0-4f07-a30c-eabb7cfbddf5"",
      ""folderId"": ""6ddb6044-f09d-483e-85fd-161528c942a6"",
      ""path"": ""/audiobooks/Walter Isaacson/Steve Jobs"",
      ""relPath"": ""Walter Isaacson/Steve Jobs"",
      ""isFile"": false,
      ""mtimeMs"": 1765671500000,
      ""addedAt"": 1765671500000,
      ""updatedAt"": 1765671600000,
      ""isMissing"": false,
      ""isInvalid"": false,
      ""mediaType"": ""book"",
      ""media"": {
        ""id"": ""b-1"",
        ""metadata"": {
          ""title"": ""Steve Jobs"",
          ""titleIgnorePrefix"": ""Steve Jobs"",
          ""subtitle"": null,
          ""authorName"": ""Walter Isaacson"",
          ""authorNameLF"": ""Isaacson, Walter"",
          ""narratorName"": """",
          ""seriesName"": """",
          ""genres"": [],
          ""publishedYear"": null,
          ""isbn"": null,
          ""asin"": null,
          ""language"": null,
          ""explicit"": false,
          ""abridged"": false
        },
        ""coverPath"": ""/metadata/items/li-steve-jobs/cover.jpg"",
        ""tags"": [],
        ""numTracks"": 126,
        ""numAudioFiles"": 126,
        ""numChapters"": 126,
        ""duration"": 90679.67,
        ""size"": 1300000000,
        ""ebookFormat"": null
      },
      ""numFiles"": 127,
      ""size"": 1300000000
    },
    {
      ""id"": ""li-animal-farm-cosham"",
      ""libraryId"": ""68a674a7-0cb0-4f07-a30c-eabb7cfbddf5"",
      ""folderId"": ""6ddb6044-f09d-483e-85fd-161528c942a6"",
      ""path"": ""/audiobooks/George Orwell/Animal Farm - Ralph Cosham"",
      ""relPath"": ""George Orwell/Animal Farm - Ralph Cosham"",
      ""isFile"": false,
      ""updatedAt"": 1765671700000,
      ""isMissing"": false,
      ""isInvalid"": false,
      ""mediaType"": ""book"",
      ""media"": {
        ""metadata"": {
          ""title"": ""Animal Farm"",
          ""subtitle"": null,
          ""authorName"": ""George Orwell"",
          ""narratorName"": ""Ralph Cosham"",
          ""isbn"": ""9781481540551"",
          ""asin"": ""B0000545BA"",
          ""abridged"": false
        },
        ""numTracks"": 10,
        ""numAudioFiles"": 10,
        ""duration"": 10040.88,
        ""ebookFormat"": null
      }
    },
    {
      ""id"": ""li-firm-abridged"",
      ""libraryId"": ""68a674a7-0cb0-4f07-a30c-eabb7cfbddf5"",
      ""folderId"": ""6ddb6044-f09d-483e-85fd-161528c942a6"",
      ""path"": ""/audiobooks/John Grisham/The Firm (Abridged)"",
      ""relPath"": ""John Grisham/The Firm (Abridged)"",
      ""isFile"": false,
      ""isMissing"": false,
      ""isInvalid"": false,
      ""mediaType"": ""book"",
      ""media"": {
        ""metadata"": {
          ""title"": ""The Firm"",
          ""authorName"": ""John Grisham"",
          ""narratorName"": ""D.W. Moffett"",
          ""isbn"": null,
          ""asin"": null,
          ""abridged"": false
        },
        ""numTracks"": 4,
        ""numAudioFiles"": 4,
        ""duration"": 21600,
        ""ebookFormat"": null
      }
    },
    {
      ""id"": ""li-gone"",
      ""libraryId"": ""68a674a7-0cb0-4f07-a30c-eabb7cfbddf5"",
      ""folderId"": ""6ddb6044-f09d-483e-85fd-161528c942a6"",
      ""path"": ""/audiobooks/Someone/Deleted Book"",
      ""relPath"": ""Someone/Deleted Book"",
      ""isMissing"": true,
      ""isInvalid"": false,
      ""mediaType"": ""book"",
      ""media"": {
        ""metadata"": { ""title"": ""Deleted Book"", ""authorName"": ""Someone"" },
        ""numAudioFiles"": 3,
        ""duration"": 9000
      }
    }
  ],
  ""total"": 4,
  ""limit"": 200,
  ""page"": 0,
  ""sortDesc"": false,
  ""mediaType"": ""book"",
  ""minified"": true,
  ""collapseseries"": false,
  ""include"": """",
  ""offset"": 0
}";

        // The ebook library, served as two pages by the paging tests (limit 200 each).
        public const string EbookItemsSinglePage = @"{
  ""results"": [
    {
      ""id"": ""li-poppy-war-ebook"",
      ""libraryId"": ""c2b97462-2209-4dd0-bd05-5cf2f12a63fc"",
      ""folderId"": ""cdb8e584-6eb1-449c-a6c9-1b8c08d60342"",
      ""path"": ""/books/R.F. Kuang/The Poppy War"",
      ""relPath"": ""R.F. Kuang/The Poppy War"",
      ""isFile"": false,
      ""isMissing"": false,
      ""isInvalid"": false,
      ""mediaType"": ""book"",
      ""media"": {
        ""metadata"": {
          ""title"": ""The Poppy War"",
          ""authorName"": ""R.F. Kuang"",
          ""narratorName"": """",
          ""isbn"": ""9780063413580"",
          ""asin"": null,
          ""abridged"": false
        },
        ""numTracks"": 0,
        ""numAudioFiles"": 0,
        ""duration"": 0,
        ""ebookFormat"": ""epub""
      }
    },
    {
      ""id"": ""li-firm-ebook"",
      ""libraryId"": ""c2b97462-2209-4dd0-bd05-5cf2f12a63fc"",
      ""folderId"": ""cdb8e584-6eb1-449c-a6c9-1b8c08d60342"",
      ""path"": ""/books/John Grisham/The Firm"",
      ""relPath"": ""John Grisham/The Firm"",
      ""isMissing"": false,
      ""isInvalid"": false,
      ""mediaType"": ""book"",
      ""media"": {
        ""metadata"": {
          ""title"": ""The Firm"",
          ""authorName"": ""John Grisham"",
          ""isbn"": ""978-0-307-57614-9"",
          ""asin"": null
        },
        ""numAudioFiles"": 0,
        ""ebookFormat"": ""epub""
      }
    }
  ],
  ""total"": 2,
  ""limit"": 200,
  ""page"": 0,
  ""minified"": true
}";

        public static string ItemsPage(int page, int count, int total, string libraryId)
        {
            var results = new System.Text.StringBuilder();
            for (var i = 0; i < count; i++)
            {
                var n = (page * 200) + i;
                if (i > 0)
                {
                    results.Append(',');
                }

                results.Append($@"{{""id"":""li-{n}"",""libraryId"":""{libraryId}"",""path"":""/books/Author {n}/Book {n}"",""relPath"":""Author {n}/Book {n}"",""mediaType"":""book"",""media"":{{""metadata"":{{""title"":""Book {n}"",""authorName"":""Author {n}""}},""numAudioFiles"":0,""ebookFormat"":""epub""}}}}");
            }

            return $@"{{""results"":[{results}],""total"":{total},""limit"":200,""page"":{page},""minified"":true}}";
        }
    }
}
