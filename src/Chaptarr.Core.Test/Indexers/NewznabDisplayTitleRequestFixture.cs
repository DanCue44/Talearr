using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using NzbDrone.Core.Books;
using NzbDrone.Core.Indexers.Newznab;
using NzbDrone.Core.IndexerSearch.Definitions;

namespace Chaptarr.Core.Test.Indexers
{
    [TestFixture]
    public class NewznabDisplayTitleRequestFixture
    {
        private sealed class StubCapabilities : INewznabCapabilitiesProvider
        {
            public NewznabCapabilities GetCapabilities(NewznabSettings settings, int? proxyId)
            {
                return new NewznabCapabilities
                {
                    SupportedSearchParameters = new[] { "q" },
                    SupportedBookSearchParameters = new[] { "q", "author", "title" }
                };
            }
        }

        private static List<string> Urls(bool requireAuthor)
        {
            var generator = new NewznabRequestGenerator(new StubCapabilities())
            {
                Settings = new NewznabSettings { BaseUrl = "http://indexer.local", ApiPath = "/api" },
                MaxPages = 1
            };

            var chain = generator.GetSearchRequests(new BookSearchCriteria
            {
                Author = new Author { Name = "Tao Wong" },
                BookTitle = "Cataclysm",
                RequireAuthor = requireAuthor
            });

            return Enumerable.Range(0, chain.Tiers)
                .SelectMany(chain.GetTier)
                .SelectMany(requests => requests)
                .Select(r => r.Url.FullUri)
                .ToList();
        }

        [Test]
        public void should_send_title_only_requests_for_the_metadata_title()
        {
            Assert.That(Urls(false).Any(u => !u.Contains("Tao")), Is.True);
        }

        [Test]
        public void should_always_send_the_author_with_a_display_title()
        {
            var urls = Urls(true);

            Assert.That(urls, Is.Not.Empty);
            Assert.That(urls, Has.All.Contains("Tao"));
            Assert.That(urls, Has.Some.Contains("Cataclysm"));
        }
    }
}
