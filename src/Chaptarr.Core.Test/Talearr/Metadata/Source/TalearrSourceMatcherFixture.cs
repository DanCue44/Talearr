using System.Collections.Generic;
using System.Linq;
using NLog;
using NUnit.Framework;
using NzbDrone.Core.Talearr.Metadata.Source;

namespace Chaptarr.Core.Test.Talearr.Metadata.Source
{
    [TestFixture]
    public class TalearrSourceMatcherFixture
    {
        private sealed class FakeClient : ITalearrHardcoverMetadataClient
        {
            public List<string> SearchedFor { get; } = new List<string>();
            public bool IsConfigured => true;

            public HardcoverAuthorCatalog GetAuthorCatalog(int hardcoverAuthorId, bool useCache = true) => null;

            public Dictionary<int, HardcoverAuthorSignature> GetAuthorSignatures(IReadOnlyCollection<int> hardcoverAuthorIds)
            {
                return hardcoverAuthorIds.ToDictionary(id => id, id => new HardcoverAuthorSignature { AuthorId = id, Name = "Jack Rosewood" });
            }

            public HardcoverBookAuthors GetBookAuthors(int hardcoverBookId) =>
                hardcoverBookId == 1704501 ? new HardcoverBookAuthors { BookId = 1704501, AuthorIds = new List<int> { 300254 } } : null;

            public int? GetEditionBookId(int hardcoverEditionId) => null;

            public List<HardcoverEditionHit> FindEditions(IReadOnlyCollection<string> isbns, IReadOnlyCollection<string> asins) =>
                isbns.Contains("9781523361083")
                    ? new List<HardcoverEditionHit> { new HardcoverEditionHit { EditionId = 31799733, BookId = 1704501, ReadingFormatId = 1 } }
                    : new List<HardcoverEditionHit>();

            public List<HardcoverBookHit> SearchBooks(string query, int limit)
            {
                SearchedFor.Add(query);
                return new List<HardcoverBookHit>
                {
                    new HardcoverBookHit { BookId = 1704501, Title = "The Killing Cousins", AuthorId = 300254, AuthorName = "Jack Rosewood" },
                    new HardcoverBookHit { BookId = 1704506, Title = "Donald Gaskins", AuthorId = 300254, AuthorName = "Jack Rosewood" }
                };
            }

            public TalearrHardcoverClientStatus GetStatus() => new TalearrHardcoverClientStatus();
        }

        [Test]
        public void isbn_check_digits_are_verified()
        {
            Assert.That(TalearrSourceMatcher.IsValidIsbn("9781523361083"), Is.True);
            Assert.That(TalearrSourceMatcher.IsValidIsbn("9781523361084"), Is.False);
            Assert.That(TalearrSourceMatcher.IsValidIsbn("043942089X"), Is.True);
        }

        [Test]
        public void identifiers_are_found_in_any_tag()
        {
            var tags = new Dictionary<string, List<string>>
            {
                ["ISBN"] = new List<string> { "978-1-5233-6108-3" },
                ["TXXX:AUDIBLE_ASIN"] = new List<string> { "b01n0x1234" },
                ["comment"] = new List<string> { "Track 9781523361084 of 12" }
            };

            TalearrSourceMatcher.ExtractIdentifiers(tags, out var isbns, out var asins);

            Assert.That(isbns, Is.EqualTo(new[] { "9781523361083" }));
            Assert.That(asins, Is.EqualTo(new[] { "B01N0X1234" }));
        }

        [Test]
        public void identifier_hit_comes_first_then_search_hits_without_duplicates()
        {
            var client = new FakeClient();
            var matcher = new TalearrSourceMatcher(client, LogManager.GetCurrentClassLogger());

            var matches = matcher.Match("Jack Rosewood The Killing Cousins", new Dictionary<string, List<string>> { ["isbn"] = new List<string> { "9781523361083" } });

            Assert.That(matches.Select(m => (m.id, m.work_id, m.edition_hardcover_id, m.name)), Is.EqualTo(new[]
            {
                ("hc:300254", "hc:1704501", "31799733", "Jack Rosewood"),
                ("hc:300254", "hc:1704506", (string)null, "Jack Rosewood")
            }));
            Assert.That(client.SearchedFor, Is.EqualTo(new[] { "Jack Rosewood The Killing Cousins" }));
        }
    }
}
