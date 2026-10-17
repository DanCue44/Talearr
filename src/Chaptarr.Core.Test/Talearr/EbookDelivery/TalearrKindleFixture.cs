using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Chaptarr.Core.Test.Talearr.Audiobook;
using Chaptarr.Core.Test.Talearr.ReaderHandoff;
using MimeKit;
using NLog;
using NUnit.Framework;
using NzbDrone.Core.Books;
using NzbDrone.Core.Configuration;
using NzbDrone.Core.HealthCheck.Checks;
using NzbDrone.Core.MediaFiles;
using NzbDrone.Core.MediaFiles.Events;
using NzbDrone.Core.Notifications;
using NzbDrone.Core.Notifications.Email;
using NzbDrone.Core.Talearr.EbookDelivery;

namespace Chaptarr.Core.Test.Talearr.EbookDelivery
{
    public class FakeSmtpTransport : ITalearrSmtpTransport
    {
        public List<(TalearrSmtpServer Server, MimeMessage Message)> Sent { get; } = new List<(TalearrSmtpServer, MimeMessage)>();
        public List<TalearrSmtpServer> Verified { get; } = new List<TalearrSmtpServer>();
        public Exception FailWith { get; set; }

        public void Verify(TalearrSmtpServer server)
        {
            if (FailWith != null)
            {
                throw FailWith;
            }

            Verified.Add(server);
        }

        public void Send(TalearrSmtpServer server, MimeMessage message)
        {
            if (FailWith != null)
            {
                throw FailWith;
            }

            Sent.Add((server, message));
        }
    }

    [TestFixture]
    public class TalearrKindleFixture
    {
        private string _root;
        private Dictionary<string, string> _rows;
        private FakeEventAggregator _events;
        private TalearrKindleSettingsService _settingsService;
        private FakeSmtpTransport _transport;
        private Dictionary<int, List<BookFile>> _filesByBook;
        private Dictionary<int, Book> _books;
        private List<NotificationDefinition> _notifications;
        private TalearrKindleService _service;

        [SetUp]
        public void SetUp()
        {
            _root = Path.Combine(Path.GetTempPath(), "talearr-kindle-test-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_root);

            _rows = new Dictionary<string, string>();
            var (repository, fake) = Fake<IConfigRepository>.Create();
            fake.On(nameof(IConfigRepository.Get), args => _rows.TryGetValue((string)args[0], out var value) ? new Config { Key = (string)args[0], Value = value } : null)
                .On(nameof(IConfigRepository.Upsert), args =>
                {
                    _rows[(string)args[0]] = (string)args[1];
                    return new Config { Key = (string)args[0], Value = (string)args[1] };
                });

            _events = new FakeEventAggregator();
            _settingsService = new TalearrKindleSettingsService(repository, _events, LogManager.GetCurrentClassLogger());
            _transport = new FakeSmtpTransport();
            _filesByBook = new Dictionary<int, List<BookFile>>();
            _books = new Dictionary<int, Book>();
            _notifications = new List<NotificationDefinition>();

            var (bookService, bookFake) = Fake<IBookService>.Create();
            bookFake.On(nameof(IBookService.GetBook), args => _books[(int)args[0]]);

            var (mediaFileService, mediaFake) = Fake<IMediaFileService>.Create();
            mediaFake.On(nameof(IMediaFileService.GetFilesByBook), args => _filesByBook.TryGetValue((int)args[0], out var files) ? files : new List<BookFile>());

            var (notificationFactory, notificationFake) = Fake<INotificationFactory>.Create();
            notificationFake.On(nameof(INotificationFactory.All), args => _notifications)
                .On(nameof(INotificationFactory.Get), args => _notifications.FirstOrDefault(n => n.Id == (int)args[0]) ?? throw new InvalidOperationException("not found"));

            _service = new TalearrKindleService(_settingsService, _transport, bookService, mediaFileService, notificationFactory, LogManager.GetCurrentClassLogger());
        }

        [TearDown]
        public void TearDown()
        {
            if (Directory.Exists(_root))
            {
                Directory.Delete(_root, true);
            }
        }

        private BookFile File(string name, string content = "ebook")
        {
            var path = Path.Combine(_root, name);
            System.IO.File.WriteAllText(path, content);
            return new BookFile { Path = path, MediaType = "ebook" };
        }

        private static TalearrKindleSettings Settings()
        {
            return new TalearrKindleSettings
            {
                Enabled = true,
                KindleAddresses = new List<string> { "reader_1@kindle.com" },
                Server = "smtp.example.com",
                Port = 587,
                Username = "me",
                Password = "secret",
                From = "me@example.com"
            };
        }

        private Book AddBook(int id, params BookFile[] files)
        {
            var book = new Book { Id = id, Title = "Mistborn", Author = new Author { Name = "Brandon Sanderson" } };
            _books[id] = book;
            _filesByBook[id] = files.ToList();
            return book;
        }

        [Test]
        public void settings_are_off_by_default()
        {
            var settings = _settingsService.Get();

            Assert.That(settings.Enabled, Is.False);
            Assert.That(settings.AutoSendOnImport, Is.False);
            Assert.That(settings.KindleAddresses, Is.Empty);
            Assert.That(settings.IsActive, Is.False);
            Assert.That(settings.RequireEncryption, Is.True);
            Assert.That(settings.Port, Is.EqualTo(587));
        }

        [Test]
        public void settings_are_talearr_config_rows_and_a_masked_password_keeps_the_stored_one()
        {
            var settings = Settings();
            settings.KindleAddresses = new List<string> { "a@kindle.com, b@kindle.com", "A@kindle.com" };
            _settingsService.Save(settings);

            Assert.That(_rows.Keys.All(k => k.StartsWith("talearr.kindle.")), Is.True);
            Assert.That(_rows[TalearrKindleSettingsService.AddressesKey], Is.EqualTo("a@kindle.com,b@kindle.com"));
            Assert.That(_events.Events.OfType<TalearrKindleSettingsChangedEvent>().Count(), Is.EqualTo(1));

            var again = Settings();
            again.Password = TalearrKindleSettings.MaskedPassword;
            var saved = _settingsService.Save(again);

            Assert.That(saved.Password, Is.EqualTo("secret"));
            Assert.That(_rows[TalearrKindleSettingsService.PasswordKey], Is.EqualTo("secret"));
        }

        [Test]
        public void smtp_server_description_never_contains_the_password()
        {
            var server = new TalearrSmtpServer { Server = "smtp.example.com", Port = 465, Username = "me", Password = "secret" };

            Assert.That(server.ToString(), Is.EqualTo("smtp.example.com:465"));
        }

        [Test]
        public void epub_is_preferred_over_pdf()
        {
            var pdf = new BookFile { Path = "/b/book.pdf" };
            var epub = new BookFile { Path = "/b/book.epub" };

            var (file, reason) = TalearrKindleService.PickFile(new[] { pdf, epub }, _ => 1000);

            Assert.That(file, Is.SameAs(epub));
            Assert.That(reason, Is.Null);
        }

        [Test]
        public void azw3_and_mobi_only_books_are_skipped_with_a_clear_message()
        {
            var (file, reason) = TalearrKindleService.PickFile(new[] { new BookFile { Path = "/b/book.azw3" }, new BookFile { Path = "/b/book.mobi" } }, _ => 1000);

            Assert.That(file, Is.Null);
            Assert.That(reason, Does.Contain("AZW3, MOBI"));
            Assert.That(reason, Does.Contain("EPUB"));
        }

        [Test]
        public void files_over_50_mb_are_refused_with_the_size()
        {
            var (file, reason) = TalearrKindleService.PickFile(new[] { new BookFile { Path = "/b/huge.epub" } }, _ => 63L * 1024 * 1024);

            Assert.That(file, Is.Null);
            Assert.That(reason, Does.Contain("63 MB"));
            Assert.That(reason, Does.Contain("50 MB"));
        }

        [Test]
        public void a_too_large_epub_falls_back_to_a_pdf_that_fits()
        {
            var epub = new BookFile { Path = "/b/book.epub" };
            var pdf = new BookFile { Path = "/b/book.pdf" };

            var (file, _) = TalearrKindleService.PickFile(new[] { epub, pdf }, p => p.EndsWith(".epub") ? 60L * 1024 * 1024 : 2000);

            Assert.That(file, Is.SameAs(pdf));
        }

        [Test]
        public void audiobook_only_books_have_nothing_to_send()
        {
            var (file, reason) = TalearrKindleService.PickFile(new[] { new BookFile { Path = "/b/book.m4b" } }, _ => 1000);

            Assert.That(file, Is.Null);
            Assert.That(reason, Does.Contain("no ebook file"));
        }

        [Test]
        public void send_book_mails_the_epub_to_every_address()
        {
            var settings = Settings();
            settings.KindleAddresses = new List<string> { "one@kindle.com", "two@kindle.com" };
            _settingsService.Save(settings);
            AddBook(7, File("mistborn.epub"), File("mistborn.azw3"));

            var result = _service.SendBook(7);

            Assert.That(result.Success, Is.True, result.Message);
            Assert.That(_transport.Sent, Has.Count.EqualTo(1));

            var (server, message) = _transport.Sent[0];
            Assert.That(server.Server, Is.EqualTo("smtp.example.com"));
            Assert.That(server.Password, Is.EqualTo("secret"));
            Assert.That(message.To.Mailboxes.Select(m => m.Address), Is.EquivalentTo(new[] { "one@kindle.com", "two@kindle.com" }));
            Assert.That(message.From.Mailboxes.Single().Address, Is.EqualTo("me@example.com"));
            Assert.That(message.Subject, Is.EqualTo("Mistborn"));

            var attachment = message.Attachments.OfType<MimePart>().Single();
            Assert.That(attachment.FileName, Is.EqualTo("Brandon Sanderson - Mistborn.epub"));
            Assert.That(attachment.ContentType.MimeType, Is.EqualTo("application/epub+zip"));
            Assert.That(result.Message, Does.Not.Contain("secret"));
        }

        [Test]
        public void send_is_refused_while_turned_off()
        {
            AddBook(7, File("mistborn.epub"));

            var result = _service.SendBook(7);

            Assert.That(result.Success, Is.False);
            Assert.That(result.Message, Does.Contain("off"));
            Assert.That(_transport.Sent, Is.Empty);
        }

        [Test]
        public void smtp_errors_come_back_as_a_message()
        {
            _settingsService.Save(Settings());
            AddBook(7, File("mistborn.epub"));
            _transport.FailWith = new InvalidOperationException("535 authentication failed");

            var result = _service.SendBook(7);

            Assert.That(result.Success, Is.False);
            Assert.That(result.Message, Does.Contain("535 authentication failed"));
            Assert.That(result.Message, Does.Contain("smtp.example.com:587"));
        }

        [Test]
        public void an_email_connection_lends_its_smtp_settings()
        {
            _notifications.Add(new NotificationDefinition
            {
                Id = 3,
                Name = "Gmail",
                Settings = new EmailSettings { Server = "smtp.gmail.com", Port = 465, RequireEncryption = true, Username = "g", Password = "gpass", From = "g@gmail.com", To = new[] { "x@y.z" } }
            });

            var settings = Settings();
            settings.EmailConnectionId = 3;
            settings.Server = string.Empty;
            _settingsService.Save(settings);
            AddBook(7, File("mistborn.epub"));

            var result = _service.SendBook(7);

            Assert.That(result.Success, Is.True, result.Message);
            Assert.That(_transport.Sent[0].Server.Server, Is.EqualTo("smtp.gmail.com"));
            Assert.That(_transport.Sent[0].Server.Password, Is.EqualTo("gpass"));
            Assert.That(_transport.Sent[0].Message.From.Mailboxes.Single().Address, Is.EqualTo("g@gmail.com"));
            Assert.That(_service.EmailConnections().Select(c => c.Name), Is.EqualTo(new[] { "Gmail" }));
        }

        [Test]
        public void a_missing_email_connection_is_explained()
        {
            var settings = Settings();
            settings.EmailConnectionId = 99;

            Assert.That(_service.ServerError(settings), Does.Contain("no longer exists"));
        }

        [Test]
        public void test_only_signs_in_and_uses_the_stored_password_for_a_masked_one()
        {
            _settingsService.Save(Settings());
            var unsaved = Settings();
            unsaved.Password = TalearrKindleSettings.MaskedPassword;

            var result = _service.Test(unsaved);

            Assert.That(result.Success, Is.True, result.Message);
            Assert.That(_transport.Sent, Is.Empty);
            Assert.That(_transport.Verified.Single().Password, Is.EqualTo("secret"));
            Assert.That(result.Message, Does.Contain("Approved Personal Document"));
        }

        [Test]
        public void test_rejects_a_bad_kindle_address()
        {
            var settings = Settings();
            settings.KindleAddresses = new List<string> { "not-an-address" };

            var result = _service.Test(settings);

            Assert.That(result.Success, Is.False);
            Assert.That(result.Message, Does.Contain("not-an-address"));
            Assert.That(_transport.Verified, Is.Empty);
        }

        [Test]
        public void auto_send_only_follows_imports_from_a_download()
        {
            var settings = Settings();
            settings.AutoSendOnImport = true;
            _settingsService.Save(settings);
            var book = AddBook(7);
            var files = new List<BookFile> { File("mistborn.epub") };

            _service.HandleAsync(new BookImportedEvent(book.Author, book, files, new List<BookFile>(), false, null));
            Assert.That(_transport.Sent, Is.Empty);

            _service.HandleAsync(new BookImportedEvent(book.Author, book, files, new List<BookFile>(), true, null));
            Assert.That(_transport.Sent, Has.Count.EqualTo(1));
        }

        [Test]
        public void auto_send_stays_off_unless_asked_for()
        {
            _settingsService.Save(Settings());
            var book = AddBook(7);

            _service.HandleAsync(new BookImportedEvent(book.Author, book, new List<BookFile> { File("mistborn.epub") }, new List<BookFile>(), true, null));

            Assert.That(_transport.Sent, Is.Empty);
        }

        [Test]
        public void auto_send_ignores_audiobook_imports()
        {
            var settings = Settings();
            settings.AutoSendOnImport = true;
            _settingsService.Save(settings);
            var book = AddBook(7);

            _service.HandleAsync(new BookImportedEvent(book.Author, book, new List<BookFile> { File("mistborn.m4b") }, new List<BookFile>(), true, null));

            Assert.That(_transport.Sent, Is.Empty);
        }

        [Test]
        public void book_status_explains_why_a_book_cannot_be_sent()
        {
            _settingsService.Save(Settings());
            AddBook(7, File("mistborn.azw3"));
            AddBook(8, File("elantris.epub"));

            var azw3 = _service.GetBookStatus(7);
            var epub = _service.GetBookStatus(8);

            Assert.That(azw3.Enabled, Is.True);
            Assert.That(azw3.CanSend, Is.False);
            Assert.That(azw3.Message, Does.Contain("AZW3"));
            Assert.That(epub.CanSend, Is.True);
            Assert.That(epub.FileName, Is.EqualTo("elantris.epub"));
        }

        [Test]
        public void health_check_warns_about_missing_addresses_and_smtp()
        {
            var noAddress = Settings();
            noAddress.KindleAddresses.Clear();
            Assert.That(TalearrEbookDeliveryCheck.GetMessage(noAddress, _ => null, null, () => true), Does.Contain("no Kindle address"));

            var noServer = Settings();
            noServer.Server = string.Empty;
            Assert.That(TalearrEbookDeliveryCheck.GetMessage(noServer, _service.ServerError, null, () => true), Does.Contain("SMTP server"));

            Assert.That(TalearrEbookDeliveryCheck.GetMessage(Settings(), _service.ServerError, null, () => true), Is.Null);
            Assert.That(TalearrEbookDeliveryCheck.GetMessage(TalearrKindleSettings.Default, _ => "x", null, () => true), Is.Null);
        }
    }
}
