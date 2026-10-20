using System;
using System.Collections.Generic;
using System.Linq;
using NLog;
using NUnit.Framework;
using NzbDrone.Common.Cache;
using NzbDrone.Core.Books;
using NzbDrone.Core.Exceptions;
using NzbDrone.Core.MetadataSource;
using NzbDrone.Core.MetadataSource.BookInfo;
using NzbDrone.Core.MetadataSource.BookInfo.V5;
using NzbDrone.Core.Talearr.Metadata.Source;

namespace Chaptarr.Core.Test.Talearr.Metadata.Source
{
    // BookInfoProxy with Talearr's source selected: Hardcover ids never reach the metadata server
    // (the scripted HTTP client throws), anything else still would.
    [TestFixture]
    public class TalearrSourceRoutingFixture
    {
        private sealed class FakeSource : ITalearrSourcePayloads
        {
            public bool IsActive { get; set; } = true;
            public Dictionary<int, string> Signatures { get; } = new Dictionary<int, string> { [42] = "hct-7-1-w1" };
            public int AuthorReads { get; private set; }

            public V5AuthorResponse GetAuthor(int hardcoverAuthorId, bool useCache = true)
            {
                AuthorReads++;
                return hardcoverAuthorId == 42 ? TalearrHardcoverMapper.BuildAuthorResponse(HardcoverFixtures.Catalog()) : null;
            }

            public Dictionary<int, string> GetSignatures(IReadOnlyCollection<int> hardcoverAuthorIds)
            {
                return hardcoverAuthorIds.Where(Signatures.ContainsKey).ToDictionary(id => id, id => Signatures[id]);
            }

            public int? GetBookAuthorId(int hardcoverBookId, out int canonicalBookId)
            {
                // 104 is a deduped copy of 100.
                canonicalBookId = hardcoverBookId == 104 ? 100 : hardcoverBookId;
                return hardcoverBookId is 100 or 101 or 104 ? 42 : null;
            }

            public int? GetEditionBookId(int hardcoverEditionId) => hardcoverEditionId == 502 ? 100 : null;
        }

        private static (BookInfoProxy Proxy, FakeSource Source) Create()
        {
            var logger = LogManager.GetLogger("talearr-source-routing-test");
            var health = new MetadataServerHealthService(logger);
            var source = new FakeSource();
            var proxy = new BookInfoProxy(new ScriptedHttpClient(_ => throw new InvalidOperationException("the metadata server must not be called")),
                cachedHttpClient: null,
                goodreadsSearchProxy: null,
                hardcoverSearchClient: null,
                audibleCatalogProxy: null,
                authorService: null,
                bookService: null,
                editionService: null,
                configService: ConfigServiceTestProxy.Create(),
                requestBuilder: null,
                logger: logger,
                cacheManager: new CacheManager(),
                metadataServerHealthGate: new MetadataServerHealthGate(ConfigServiceTestProxy.Create(), health, logger),
                metadataFallback: null,
                talearrSource: source);
            return (proxy, source);
        }

        [Test]
        public void author_info_for_a_hardcover_id_comes_from_hardcover()
        {
            var (proxy, _) = Create();

            var author = proxy.GetAuthorInfo("hc:42");

            Assert.That(author.Name, Is.EqualTo("Ursula Example"));
            Assert.That(author.Books.Any(b => b.HardcoverBookId == "hc:100"), Is.True);
            Assert.Throws<AuthorNotFoundException>(() => proxy.GetAuthorInfo("hc:43"));
        }

        [Test]
        public void refresh_uses_the_signature_as_etag()
        {
            var (proxy, source) = Create();

            var unchanged = proxy.RefreshAuthorInfo("hc:42", "hct-7-1-w1");
            Assert.That(unchanged.Reason, Is.EqualTo(RefreshReason.NotModified));
            Assert.That(source.AuthorReads, Is.EqualTo(0), "an unchanged signature costs no catalog read");

            var changed = proxy.RefreshAuthorInfo("hc:42", "old");
            Assert.That(changed.HasChanges, Is.True);
            Assert.That(changed.ETag, Is.EqualTo("hct-7-1-w1"));
            Assert.That(changed.Author.Name, Is.EqualTo("Ursula Example"));

            var forced = proxy.RefreshAuthorInfo("hc:42", "hct-7-1-w1", forceRefresh: true);
            Assert.That(forced.HasChanges, Is.True);
        }

        [Test]
        public void refresh_of_an_author_hardcover_does_not_know_is_an_error_not_a_deletion()
        {
            var (proxy, _) = Create();

            var result = proxy.RefreshAuthorInfo("hc:43", null);

            Assert.That(result.Reason, Is.EqualTo(RefreshReason.Error));
        }

        [Test]
        public void bulk_check_reports_changed_and_unknown_hardcover_authors()
        {
            var (proxy, _) = Create();

            var response = proxy.GetBulkAuthorChanges(new List<V5AuthorETag>
            {
                new V5AuthorETag { RequestedId = "hc:42", ETag = "stale" },
                new V5AuthorETag { RequestedId = "hc:43", ETag = "x" }
            });

            Assert.That(response.Changed.Select(c => (c.RequestedId, c.CanonicalId, c.ETag)), Is.EqualTo(new[] { ("hc:42", "hc:42", "hct-7-1-w1") }));
            Assert.That(response.Rejected.Select(r => r.RequestedId), Is.EqualTo(new[] { "hc:43" }));
            Assert.That(response.Deleted, Is.Empty);

            var inSync = proxy.GetBulkAuthorChanges(new List<V5AuthorETag> { new V5AuthorETag { RequestedId = "hc:42", ETag = "hct-7-1-w1" } });
            Assert.That(inSync.Changed, Is.Empty);
        }

        [Test]
        public void work_and_edition_lookups_pick_the_book_from_its_author()
        {
            var (proxy, _) = Create();

            var work = proxy.GetWorkInfo("hc:100", BookMediaType.Ebook);
            Assert.That(work.Item2.HardcoverBookId, Is.EqualTo("hc:100"));
            Assert.That(work.Item2.MediaType, Is.EqualTo(BookMediaType.Ebook));
            Assert.That(work.Item3.Single().Name, Is.EqualTo("Ursula Example"));

            var deduped = proxy.GetBookInfo("hc:104", BookMediaType.Audiobook);
            Assert.That(deduped.Item2.HardcoverBookId, Is.EqualTo("hc:100"));

            var edition = proxy.GetEditionInfo("hc:edition:502-ebook", BookMediaType.Ebook);
            Assert.That(edition.Item2.HardcoverBookId, Is.EqualTo("hc:100"));

            Assert.Throws<BookNotFoundException>(() => proxy.GetWorkInfo("hc:999"));
        }

        [Test]
        public void inactive_source_leaves_the_server_path_alone()
        {
            var (proxy, source) = Create();
            source.IsActive = false;

            Assert.That(proxy.TalearrSourceActive, Is.False);
            Assert.That(() => proxy.GetBulkAuthorChanges(new List<V5AuthorETag> { new V5AuthorETag { RequestedId = "hc:42" } }), Throws.Nothing,
                "the server call fails inside and returns null, as before");
            Assert.That(source.AuthorReads, Is.EqualTo(0));
        }

        [Test]
        public void ids_and_signatures_parse()
        {
            Assert.That(TalearrSourcePayloads.TryParseId("hc:123", out var id) && id == 123, Is.True);
            Assert.That(TalearrSourcePayloads.TryParseId("gr:123", out _), Is.False);
            Assert.That(TalearrSourcePayloads.TryParseId("hc:edition:5", out _), Is.False);
            Assert.That(TalearrSourcePayloads.TryParseEditionId("hc:edition:5-audiobook", out var edition) && edition == 5, Is.True);

            var signature = new HardcoverAuthorSignature { AuthorId = 3, BooksCount = 50, UpdatedAt = new DateTime(2026, 10, 17, 0, 0, 0, DateTimeKind.Utc) };
            var monday = TalearrSourcePayloads.Signature(signature, new DateTime(2026, 10, 19, 12, 0, 0, DateTimeKind.Utc));
            Assert.That(monday, Does.StartWith("hct-50-"));
            Assert.That(TalearrSourcePayloads.Signature(signature, new DateTime(2026, 10, 19, 23, 0, 0, DateTimeKind.Utc)), Is.EqualTo(monday), "stable within a day");
            Assert.That(TalearrSourcePayloads.Signature(signature, new DateTime(2026, 10, 27, 0, 0, 0, DateTimeKind.Utc)), Is.Not.EqualTo(monday), "changes within a week");
        }
    }
}
