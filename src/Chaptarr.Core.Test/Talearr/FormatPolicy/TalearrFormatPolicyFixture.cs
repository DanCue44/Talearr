using System;
using System.Collections.Generic;
using System.Linq;
using Chaptarr.Core.Test.Talearr.Audiobook;
using NLog;
using NUnit.Framework;
using NzbDrone.Core.Books;
using NzbDrone.Core.Configuration;
using NzbDrone.Core.MediaFiles;
using NzbDrone.Core.Parser.Model;
using NzbDrone.Core.Qualities;
using NzbDrone.Core.Queue;
using NzbDrone.Core.Talearr.FormatPolicy;

namespace Chaptarr.Core.Test.Talearr.FormatPolicy
{
    [TestFixture]
    public class TalearrFormatPolicyFixture
    {
        private static readonly DateTime Now = new DateTime(2026, 10, 16, 12, 0, 0, DateTimeKind.Utc);

        private Dictionary<string, string> _config;
        private TalearrFormatPolicyService _policy;
        private Author _orwell;
        private Book _ebook;
        private Book _audio;
        private HashSet<int> _withFiles;
        private List<NzbDrone.Core.Queue.Queue> _queue;
        private TalearrFormatPolicySpecification _spec;

        [SetUp]
        public void SetUp()
        {
            _config = new Dictionary<string, string>();
            var (repository, repoFake) = Fake<IConfigRepository>.Create();
            repoFake.On(nameof(IConfigRepository.Get), args => _config.TryGetValue((string)args[0], out var v) ? new Config { Key = (string)args[0], Value = v } : null)
                .On(nameof(IConfigRepository.Upsert), args =>
                {
                    _config[(string)args[0]] = (string)args[1];
                    return null;
                });
            _policy = new TalearrFormatPolicyService(repository, LogManager.GetCurrentClassLogger());

            _orwell = new Author { Id = 11, Name = "George Orwell", AudiobookMonitored = true, EbookMonitored = true };
            _ebook = new Book { Id = 4095, AuthorId = 11, Author = _orwell, BaseBookId = "hc:379760", MediaType = BookMediaType.Ebook, EbookMonitored = true, TitleSlug = "1984" };
            _audio = new Book { Id = 5002, AuthorId = 11, Author = _orwell, BaseBookId = "hc:379760", MediaType = BookMediaType.Audiobook, AudiobookMonitored = true, TitleSlug = "1984_2", Added = Now.AddHours(-48), LastSearchTime = Now.AddHours(-1) };
            _withFiles = new HashSet<int>();
            _queue = new List<NzbDrone.Core.Queue.Queue>();

            var (books, booksFake) = Fake<IBookService>.Create();
            booksFake.On(nameof(IBookService.GetBooksByAuthor), _ => new List<Book> { _ebook, _audio });
            var (authors, authorsFake) = Fake<IAuthorService>.Create();
            authorsFake.On(nameof(IAuthorService.GetAuthor), _ => _orwell);
            var (media, mediaFake) = Fake<IMediaFileService>.Create();
            mediaFake.On(nameof(IMediaFileService.GetFilesByBook), args => _withFiles.Contains((int)args[0]) ? new List<BookFile> { new BookFile() } : new List<BookFile>());
            var (queue, queueFake) = Fake<IQueueService>.Create();
            queueFake.On(nameof(IQueueService.GetQueue), _ => _queue);

            _spec = new TalearrFormatPolicySpecification(_policy, books, authors, media, queue, LogManager.GetCurrentClassLogger()) { Now = () => Now };
        }

        private static RemoteBook Release(Book book, Quality quality)
        {
            return new RemoteBook
            {
                Books = new List<Book> { book },
                ParsedBookInfo = new ParsedBookInfo { Quality = new QualityModel(quality) },
                Release = new ReleaseInfo { Title = "Orwell - 1984" }
            };
        }

        private void Set(FormatPolicyMode mode, int fallbackHours = 24, Dictionary<int, FormatPolicyMode> authors = null)
        {
            _policy.Save(new TalearrFormatPolicySettings { Default = mode, FallbackHours = fallbackHours, Authors = authors ?? new Dictionary<int, FormatPolicyMode>() });
        }

        [Test]
        public void both_is_upstream_behaviour()
        {
            Assert.That(_spec.IsSatisfiedBy(Release(_ebook, Quality.EPUB), null).Accepted, Is.True);
            Assert.That(_spec.IsSatisfiedBy(Release(_audio, Quality.M4B), null).Accepted, Is.True);
        }

        [Test]
        public void audio_only_skips_ebooks_as_a_soft_filter()
        {
            Set(FormatPolicyMode.AudioOnly);

            var decision = _spec.IsSatisfiedBy(Release(_ebook, Quality.EPUB), null);
            Assert.That(decision.Accepted, Is.False);
            Assert.That(decision.Reason, Is.EqualTo("Format policy: audiobooks only for this author"));
            Assert.That(decision.CanBypass, Is.True, "a soft filter: interactive search can still grab it");
            Assert.That(decision.Category, Is.EqualTo("Format policy"));
            Assert.That(_spec.IsSatisfiedBy(Release(_audio, Quality.M4B), null).Accepted, Is.True);
        }

        [Test]
        public void ebook_only_skips_audiobooks()
        {
            Set(FormatPolicyMode.EbookOnly);

            Assert.That(_spec.IsSatisfiedBy(Release(_audio, Quality.MP3), null).Accepted, Is.False);
            Assert.That(_spec.IsSatisfiedBy(Release(_ebook, Quality.EPUB), null).Accepted, Is.True);
        }

        [Test]
        public void an_author_override_wins_over_the_default()
        {
            Set(FormatPolicyMode.Both, authors: new Dictionary<int, FormatPolicyMode> { [11] = FormatPolicyMode.AudioOnly });
            Assert.That(_spec.IsSatisfiedBy(Release(_ebook, Quality.EPUB), null).Accepted, Is.False);

            Set(FormatPolicyMode.AudioOnly, authors: new Dictionary<int, FormatPolicyMode> { [11] = FormatPolicyMode.Both });
            Assert.That(_spec.IsSatisfiedBy(Release(_ebook, Quality.EPUB), null).Accepted, Is.True);
        }

        [Test]
        public void prefer_audio_accepts_the_ebook_after_the_audiobook_was_missing_for_the_fallback_time()
        {
            Set(FormatPolicyMode.PreferAudio, fallbackHours: 24);

            Assert.That(_spec.IsSatisfiedBy(Release(_ebook, Quality.EPUB), null).Accepted, Is.True);
            Assert.That(_spec.IsSatisfiedBy(Release(_audio, Quality.M4B), null).Accepted, Is.True, "the audiobook is always grabbed, also later as the upgrade to audio");
        }

        [Test]
        public void prefer_audio_waits_while_the_audiobook_is_new()
        {
            Set(FormatPolicyMode.PreferAudio, fallbackHours: 72);

            var decision = _spec.IsSatisfiedBy(Release(_ebook, Quality.EPUB), null);
            Assert.That(decision.Accepted, Is.False);
            Assert.That(decision.Reason, Does.Contain("waiting for the audiobook until 2026-10-17 12:00 UTC"));
        }

        [Test]
        public void prefer_audio_waits_until_the_audiobook_was_searched()
        {
            Set(FormatPolicyMode.PreferAudio);
            _audio.LastSearchTime = null;

            Assert.That(_spec.IsSatisfiedBy(Release(_ebook, Quality.EPUB), null).Reason, Does.Contain("has not been searched yet"));
        }

        [Test]
        public void prefer_audio_skips_the_ebook_when_the_audiobook_is_downloaded_or_downloading()
        {
            Set(FormatPolicyMode.PreferAudio);

            _withFiles.Add(5002);
            Assert.That(_spec.IsSatisfiedBy(Release(_ebook, Quality.EPUB), null).Reason, Does.Contain("audiobook is downloaded"));

            _withFiles.Clear();
            _queue.Add(new NzbDrone.Core.Queue.Queue { Book = _audio });
            Assert.That(_spec.IsSatisfiedBy(Release(_ebook, Quality.EPUB), null).Reason, Does.Contain("audiobook is downloading"));
        }

        [Test]
        public void prefer_audio_takes_the_ebook_when_the_audiobook_is_not_wanted()
        {
            Set(FormatPolicyMode.PreferAudio, fallbackHours: 1000);

            _audio.AudiobookMonitored = false;
            Assert.That(_spec.IsSatisfiedBy(Release(_ebook, Quality.EPUB), null).Accepted, Is.True);

            _audio.AudiobookMonitored = true;
            _orwell.AudiobookMonitored = false;
            Assert.That(_spec.IsSatisfiedBy(Release(_ebook, Quality.EPUB), null).Accepted, Is.True, "author's audiobook side is off");
        }

        [Test]
        public void prefer_audio_ignores_additional_recordings_when_finding_the_audiobook()
        {
            Set(FormatPolicyMode.PreferAudio);
            _audio.TitleSlug = "1984_wanted_3";

            Assert.That(_spec.IsSatisfiedBy(Release(_ebook, Quality.EPUB), null).Accepted, Is.True, "no primary audiobook row, so nothing to wait for");
        }

        [Test]
        public void settings_round_trip_through_config_rows()
        {
            Set(FormatPolicyMode.PreferAudio, 48, new Dictionary<int, FormatPolicyMode> { [11] = FormatPolicyMode.AudioOnly, [0] = FormatPolicyMode.EbookOnly });

            Assert.That(_config["talearr.formatpolicy.default"], Is.EqualTo("PreferAudio"));
            Assert.That(_config["talearr.formatpolicy.fallbackhours"], Is.EqualTo("48"));
            Assert.That(_config["talearr.formatpolicy.authors"], Is.EqualTo("{\"11\":\"AudioOnly\"}"));

            var fresh = new TalearrFormatPolicyService(null, LogManager.GetCurrentClassLogger());
            Assert.That(fresh.Get().Default, Is.EqualTo(FormatPolicyMode.Both), "unreadable config falls back to upstream behaviour");

            Assert.That(_policy.Get().For(11), Is.EqualTo(FormatPolicyMode.AudioOnly));
            Assert.That(_policy.Get().For(12), Is.EqualTo(FormatPolicyMode.PreferAudio));
        }

        [Test]
        public void fallback_hours_are_clamped()
        {
            Set(FormatPolicyMode.PreferAudio, 999999);
            Assert.That(_policy.Get().FallbackHours, Is.EqualTo(TalearrFormatPolicySettings.MaxFallbackHours));
        }
    }
}
