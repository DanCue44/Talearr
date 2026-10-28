using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using NUnit.Framework;
using NzbDrone.Core.Books;
using NzbDrone.Core.MediaCover;
using NzbDrone.Core.Talearr.Metadata;
using CoverModel = NzbDrone.Core.MediaCover.MediaCover;

namespace Chaptarr.Core.Test.Talearr.Metadata
{
    // cover.jpg next to a book: the largest candidate, never a thumbnail, never smaller than what is there.
    [TestFixture]
    public class TalearrSidecarCoverFixture
    {
        // The live 1984 import (book 5002, edition 39500, narrator Samuel West), 2026-10-16.
        private const string GoodreadsThumb = "https://i.gr-assets.com/images/S/compressed.photo.goodreads.com/books/1291569931l/9834606._SX50_.jpg";
        private const string GoodreadsFull = "https://i.gr-assets.com/images/S/compressed.photo.goodreads.com/books/1291569931l/9834606.jpg";
        private const string AudibleCover = "https://m.media-amazon.com/images/I/612oBD9OSjL._SL500_.jpg";
        private const string AudibleFull = "https://m.media-amazon.com/images/I/612oBD9OSjL.jpg";

        public static byte[] Jpeg(int width, int height) => Chaptarr.Core.Test.MediaCover.TestImages.Jpeg(width, height);

        public static byte[] Png(int width, int height) => Chaptarr.Core.Test.MediaCover.TestImages.Png(width, height);

        private static Edition AudioEdition(int id, string narrator, params string[] coverUrls)
        {
            return new Edition
            {
                Id = id,
                BookId = 5002,
                Format = "audiobook",
                ReadingFormatId = 2,
                Narrator = narrator,
                NarratorNames = narrator == null ? new List<string>() : new List<string> { narrator },
                Images = coverUrls.Select(u => new CoverModel(MediaCoverTypes.Cover, u)).ToList()
            };
        }

        private static Func<string, byte[]> Serve(Dictionary<string, byte[]> images, List<string> requested = null)
        {
            return url =>
            {
                requested?.Add(url);
                if (images.TryGetValue(url, out var data))
                {
                    return data;
                }

                throw new InvalidOperationException("404 " + url);
            };
        }

        [TestCase("https://i.gr-assets.com/images/S/compressed.photo.goodreads.com/books/1291569931l/9834606._SX50_.jpg", "https://i.gr-assets.com/images/S/compressed.photo.goodreads.com/books/1291569931l/9834606.jpg")]
        [TestCase("https://i.gr-assets.com/images/S/compressed.photo.goodreads.com/books/1/52163480._SX50_SY75_.jpg", "https://i.gr-assets.com/images/S/compressed.photo.goodreads.com/books/1/52163480.jpg")]
        [TestCase("https://images.gr-assets.com/books/1/4468230._SY75_.jpg", "https://images.gr-assets.com/books/1/4468230.jpg")]
        [TestCase("https://m.media-amazon.com/images/I/612oBD9OSjL._SL500_.jpg", "https://m.media-amazon.com/images/I/612oBD9OSjL.jpg")]
        [TestCase("https://m.media-amazon.com/images/I/51abcDEF12L._AC_SY400_.jpg", "https://m.media-amazon.com/images/I/51abcDEF12L.jpg")]
        public void full_size_url_drops_the_size_token(string url, string expected)
        {
            Assert.That(TalearrSidecarCover.FullSizeUrl(url), Is.EqualTo(expected));
        }

        [TestCase("https://m.media-amazon.com/images/I/612oBD9OSjL.jpg")]
        [TestCase("https://assets.hardcover.app/edition/123/f49d1ee91730d75fd3bba5ceb903fe86b333423c.jpeg")]
        [TestCase("https://example.test/covers/book._SX50_.jpg")]
        [TestCase("")]
        [TestCase(null)]
        public void full_size_url_is_null_without_a_known_size_token(string url)
        {
            Assert.That(TalearrSidecarCover.FullSizeUrl(url), Is.Null);
        }

        [Test]
        public void candidates_are_edition_then_book_then_same_recording_with_full_size_forms_first()
        {
            var edition = AudioEdition(39500, "Samuel  West", GoodreadsThumb);
            var book = new Book { Id = 5002, Images = new List<CoverModel> { new CoverModel(MediaCoverTypes.Cover, "https://covers.example.test/book.jpg") } };
            var sameNarrator = AudioEdition(39459, "Samuel West", "https://assets.hardcover.app/edition/1/west.jpeg");
            var otherNarrator = AudioEdition(39454, "Simon Prebble", AudibleCover);
            var ebook = new Edition { Id = 1, BookId = 5002, IsEbook = true, Narrator = "Samuel West", Images = new List<CoverModel> { new CoverModel(MediaCoverTypes.Cover, "https://covers.example.test/ebook.jpg") } };

            var candidates = TalearrSidecarCover.BuildCandidates(book, edition, new[] { edition, sameNarrator, otherNarrator, ebook }, isEbook: false);

            Assert.That(candidates.Select(c => (c.Url, c.Source, c.FullSize)), Is.EqualTo(new[]
            {
                (GoodreadsFull, TalearrCoverSource.Edition, true),
                (GoodreadsThumb, TalearrCoverSource.Edition, false),
                ("https://covers.example.test/book.jpg", TalearrCoverSource.Book, false),
                ("https://assets.hardcover.app/edition/1/west.jpeg", TalearrCoverSource.SameRecording, false)
            }));
        }

        [Test]
        public void ebook_candidates_take_other_editions_only_with_the_same_isbn()
        {
            var edition = new Edition { Id = 1, IsEbook = true, Isbn13 = "9780000000001", Images = new List<CoverModel> { new CoverModel(MediaCoverTypes.Cover, "https://covers.example.test/a.jpg") } };
            var sameIsbn = new Edition { Id = 2, IsEbook = true, Isbn13 = "9780000000001", Images = new List<CoverModel> { new CoverModel(MediaCoverTypes.Cover, "https://covers.example.test/b.jpg") } };
            var otherIsbn = new Edition { Id = 3, IsEbook = true, Isbn13 = "9780000000002", Images = new List<CoverModel> { new CoverModel(MediaCoverTypes.Cover, "https://covers.example.test/c.jpg") } };

            var candidates = TalearrSidecarCover.BuildCandidates(new Book(), edition, new[] { sameIsbn, otherIsbn }, isEbook: true);

            Assert.That(candidates.Select(c => c.Url), Is.EqualTo(new[] { "https://covers.example.test/a.jpg", "https://covers.example.test/b.jpg" }));
        }

        [Test]
        public void local_media_cover_urls_fall_back_to_the_remote_url()
        {
            var book = new Book
            {
                Images = new List<CoverModel>
                {
                    new CoverModel(MediaCoverTypes.Cover, "/MediaCover/Books/5002/cover.jpg") { RemoteUrl = GoodreadsThumb },
                    new CoverModel(MediaCoverTypes.Fanart, "https://covers.example.test/fanart.jpg")
                }
            };

            var candidates = TalearrSidecarCover.BuildCandidates(book, new Edition { Id = 1 }, null, isEbook: false);

            Assert.That(candidates.Select(c => c.Url), Is.EqualTo(new[] { GoodreadsFull, GoodreadsThumb }));
        }

        [Test]
        public void the_1984_import_gets_the_full_size_goodreads_cover_not_the_50px_thumbnail()
        {
            var edition = AudioEdition(39500, "Samuel  West", GoodreadsThumb);
            var book = new Book { Id = 5002, Images = new List<CoverModel> { new CoverModel(MediaCoverTypes.Cover, GoodreadsThumb) } };
            var candidates = TalearrSidecarCover.BuildCandidates(book, edition, null, isEbook: false);
            var requested = new List<string>();

            var decision = TalearrSidecarCover.Choose(candidates, Serve(new Dictionary<string, byte[]>
            {
                [GoodreadsThumb] = Jpeg(50, 67),
                [GoodreadsFull] = Jpeg(318, 424)
            }, requested), existing: null);

            Assert.Multiple(() =>
            {
                Assert.That(decision.Verdict, Is.EqualTo(TalearrCoverVerdict.Write));
                Assert.That(decision.Image.Candidate.Url, Is.EqualTo(GoodreadsFull));
                Assert.That((decision.Image.Width, decision.Image.Height), Is.EqualTo((318, 424)));

                // The thumbnail is the same picture, smaller: not downloaded once the full size worked.
                Assert.That(requested, Is.EqualTo(new[] { GoodreadsFull }));
            });
        }

        [Test]
        public void picks_the_largest_image_when_none_is_preferred()
        {
            var candidates = new List<TalearrCoverCandidate>
            {
                new TalearrCoverCandidate("https://covers.example.test/edition.jpg", TalearrCoverSource.Edition, false, "https://covers.example.test/edition.jpg"),
                new TalearrCoverCandidate("https://covers.example.test/book.jpg", TalearrCoverSource.Book, false, "https://covers.example.test/book.jpg"),
                new TalearrCoverCandidate("https://covers.example.test/same.jpg", TalearrCoverSource.SameRecording, false, "https://covers.example.test/same.jpg")
            };

            var decision = TalearrSidecarCover.Choose(candidates, Serve(new Dictionary<string, byte[]>
            {
                ["https://covers.example.test/edition.jpg"] = Jpeg(120, 180),
                ["https://covers.example.test/book.jpg"] = Jpeg(318, 424),
                ["https://covers.example.test/same.jpg"] = Jpeg(1200, 1200)
            }), existing: null);

            Assert.Multiple(() =>
            {
                Assert.That(decision.Verdict, Is.EqualTo(TalearrCoverVerdict.Write));
                Assert.That(decision.Image.Candidate.Url, Is.EqualTo("https://covers.example.test/same.jpg"));
            });
        }

        [Test]
        public void never_writes_an_image_under_300px_when_a_larger_one_is_available()
        {
            var candidates = new List<TalearrCoverCandidate>
            {
                new TalearrCoverCandidate("https://covers.example.test/edition.jpg", TalearrCoverSource.Edition, false, "https://covers.example.test/edition.jpg"),
                new TalearrCoverCandidate("https://covers.example.test/book.jpg", TalearrCoverSource.Book, false, "https://covers.example.test/book.jpg")
            };

            var decision = TalearrSidecarCover.Choose(candidates, Serve(new Dictionary<string, byte[]>
            {
                ["https://covers.example.test/edition.jpg"] = Jpeg(200, 300),
                ["https://covers.example.test/book.jpg"] = Jpeg(330, 500)
            }), existing: null);

            Assert.That(decision.Image.ShortSide, Is.GreaterThanOrEqualTo(TalearrSidecarCover.MinimumShortSide));
        }

        [Test]
        public void an_audible_or_edition_image_of_500px_wins_at_once()
        {
            var candidates = new List<TalearrCoverCandidate>
            {
                new TalearrCoverCandidate(AudibleFull, TalearrCoverSource.Edition, true, AudibleCover),
                new TalearrCoverCandidate(AudibleCover, TalearrCoverSource.Edition, false, AudibleCover),
                new TalearrCoverCandidate("https://covers.example.test/huge.jpg", TalearrCoverSource.Book, false, "https://covers.example.test/huge.jpg")
            };
            var requested = new List<string>();

            var decision = TalearrSidecarCover.Choose(candidates, Serve(new Dictionary<string, byte[]>
            {
                [AudibleFull] = Jpeg(500, 500),
                [AudibleCover] = Jpeg(500, 500),
                ["https://covers.example.test/huge.jpg"] = Jpeg(2000, 3000)
            }, requested), existing: null);

            Assert.Multiple(() =>
            {
                Assert.That(decision.Image.Candidate.Url, Is.EqualTo(AudibleFull));
                Assert.That(requested, Is.EqualTo(new[] { AudibleFull }));
            });
        }

        [Test]
        public void a_large_non_audible_image_of_another_edition_is_not_preferred_over_a_larger_one()
        {
            var candidates = new List<TalearrCoverCandidate>
            {
                new TalearrCoverCandidate("https://covers.example.test/same-a.jpg", TalearrCoverSource.SameRecording, false, "https://covers.example.test/same-a.jpg"),
                new TalearrCoverCandidate("https://covers.example.test/same-b.jpg", TalearrCoverSource.SameRecording, false, "https://covers.example.test/same-b.jpg")
            };

            var decision = TalearrSidecarCover.Choose(candidates, Serve(new Dictionary<string, byte[]>
            {
                ["https://covers.example.test/same-a.jpg"] = Jpeg(600, 900),
                ["https://covers.example.test/same-b.jpg"] = Jpeg(1000, 1500)
            }), existing: null);

            Assert.That(decision.Image.Candidate.Url, Is.EqualTo("https://covers.example.test/same-b.jpg"));
        }

        [Test]
        public void a_thumbnail_is_never_written()
        {
            var candidates = new List<TalearrCoverCandidate>
            {
                new TalearrCoverCandidate(GoodreadsThumb, TalearrCoverSource.Edition, false, GoodreadsThumb)
            };

            var decision = TalearrSidecarCover.Choose(candidates, Serve(new Dictionary<string, byte[]> { [GoodreadsThumb] = Jpeg(50, 67) }), existing: null);

            Assert.That(decision.Verdict, Is.EqualTo(TalearrCoverVerdict.TooSmall));
        }

        [Test]
        public void a_small_image_is_written_when_it_is_all_there_is_and_the_folder_has_no_cover()
        {
            var candidates = new List<TalearrCoverCandidate>
            {
                new TalearrCoverCandidate("https://covers.example.test/small.jpg", TalearrCoverSource.Edition, false, "https://covers.example.test/small.jpg")
            };

            var decision = TalearrSidecarCover.Choose(candidates, Serve(new Dictionary<string, byte[]> { ["https://covers.example.test/small.jpg"] = Jpeg(200, 300) }), existing: null);

            Assert.That(decision.Verdict, Is.EqualTo(TalearrCoverVerdict.Write));
        }

        [TestCase(200, 300, 318, 424)]
        [TestCase(500, 500, 1000, 1000)]
        [TestCase(318, 424, 318, 500)]
        public void never_replaces_an_existing_cover_with_a_smaller_one(int newWidth, int newHeight, int existingWidth, int existingHeight)
        {
            var candidates = new List<TalearrCoverCandidate>
            {
                new TalearrCoverCandidate("https://covers.example.test/a.jpg", TalearrCoverSource.Edition, false, "https://covers.example.test/a.jpg")
            };

            var decision = TalearrSidecarCover.Choose(candidates, Serve(new Dictionary<string, byte[]> { ["https://covers.example.test/a.jpg"] = Jpeg(newWidth, newHeight) }), (existingWidth, existingHeight));

            Assert.That(decision.Verdict, Is.EqualTo(TalearrCoverVerdict.KeepExisting));
        }

        [TestCase(318, 424, 50, 67)]
        [TestCase(500, 500, 500, 500)]
        public void replaces_an_existing_cover_with_a_larger_or_equal_one(int newWidth, int newHeight, int existingWidth, int existingHeight)
        {
            var candidates = new List<TalearrCoverCandidate>
            {
                new TalearrCoverCandidate("https://covers.example.test/a.jpg", TalearrCoverSource.Edition, false, "https://covers.example.test/a.jpg")
            };

            var decision = TalearrSidecarCover.Choose(candidates, Serve(new Dictionary<string, byte[]> { ["https://covers.example.test/a.jpg"] = Jpeg(newWidth, newHeight) }), (existingWidth, existingHeight));

            Assert.That(decision.Verdict, Is.EqualTo(TalearrCoverVerdict.Write));
        }

        [Test]
        public void failed_downloads_and_non_images_are_skipped()
        {
            var candidates = new List<TalearrCoverCandidate>
            {
                new TalearrCoverCandidate(GoodreadsFull, TalearrCoverSource.Edition, true, GoodreadsThumb),
                new TalearrCoverCandidate(GoodreadsThumb, TalearrCoverSource.Edition, false, GoodreadsThumb),
                new TalearrCoverCandidate("https://covers.example.test/html.jpg", TalearrCoverSource.Book, false, "https://covers.example.test/html.jpg"),
                new TalearrCoverCandidate("https://covers.example.test/ok.png", TalearrCoverSource.Book, false, "https://covers.example.test/ok.png")
            };

            var decision = TalearrSidecarCover.Choose(candidates, Serve(new Dictionary<string, byte[]>
            {
                [GoodreadsThumb] = Jpeg(50, 67),
                ["https://covers.example.test/html.jpg"] = System.Text.Encoding.UTF8.GetBytes("<html>not found</html>"),
                ["https://covers.example.test/ok.png"] = Png(400, 600)
            }), existing: null);

            Assert.Multiple(() =>
            {
                Assert.That(decision.Verdict, Is.EqualTo(TalearrCoverVerdict.Write));
                Assert.That(decision.Image.Candidate.Url, Is.EqualTo("https://covers.example.test/ok.png"));
            });
        }

        [Test]
        public void no_usable_image_when_every_download_fails()
        {
            var candidates = new List<TalearrCoverCandidate>
            {
                new TalearrCoverCandidate("https://covers.example.test/a.jpg", TalearrCoverSource.Edition, false, "https://covers.example.test/a.jpg")
            };

            var decision = TalearrSidecarCover.Choose(candidates, Serve(new Dictionary<string, byte[]>()), existing: null);

            Assert.That(decision.Verdict, Is.EqualTo(TalearrCoverVerdict.NoUsableImage));
        }

        [Test]
        public void downloads_are_capped()
        {
            var candidates = Enumerable.Range(1, 20)
                .Select(i => new TalearrCoverCandidate($"https://covers.example.test/{i}.jpg", TalearrCoverSource.SameRecording, false, $"https://covers.example.test/{i}.jpg"))
                .ToList();
            var requested = new List<string>();

            TalearrSidecarCover.Choose(candidates, Serve(candidates.ToDictionary(c => c.Url, c => Jpeg(320, 480)), requested), existing: null);

            Assert.That(requested, Has.Count.EqualTo(TalearrSidecarCover.MaxDownloads));
        }

        [Test]
        public void known_placeholder_urls_are_not_downloaded()
        {
            var candidates = new List<TalearrCoverCandidate>
            {
                new TalearrCoverCandidate("https://m.media-amazon.com/images/I/01Kv-W2ysOL.png", TalearrCoverSource.Edition, false, "https://m.media-amazon.com/images/I/01Kv-W2ysOL.png"),
                new TalearrCoverCandidate("https://covers.example.test/real.jpg", TalearrCoverSource.Book, false, "https://covers.example.test/real.jpg")
            };
            var requested = new List<string>();

            var decision = TalearrSidecarCover.Choose(candidates, Serve(new Dictionary<string, byte[]> { ["https://covers.example.test/real.jpg"] = Jpeg(400, 600) }, requested), existing: null);

            Assert.Multiple(() =>
            {
                Assert.That(requested, Is.EqualTo(new[] { "https://covers.example.test/real.jpg" }));
                Assert.That(decision.Image.Candidate.Url, Is.EqualTo("https://covers.example.test/real.jpg"));
            });
        }
    }
}
