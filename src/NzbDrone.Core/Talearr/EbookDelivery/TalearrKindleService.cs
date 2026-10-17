using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using MailKit.Net.Smtp;
using MailKit.Security;
using MimeKit;
using NLog;
using NzbDrone.Common.Http.Dispatchers;
using NzbDrone.Core.Books;
using NzbDrone.Core.MediaFiles;
using NzbDrone.Core.MediaFiles.Events;
using NzbDrone.Core.Messaging.Events;
using NzbDrone.Core.Notifications;
using NzbDrone.Core.Notifications.Email;
using NzbDrone.Core.Organizer;

namespace NzbDrone.Core.Talearr.EbookDelivery
{
    // The SMTP server a Kindle email goes through. ToString never includes the password.
    public sealed class TalearrSmtpServer
    {
        public string Server { get; set; }
        public int Port { get; set; }
        public bool RequireEncryption { get; set; }
        public string Username { get; set; }
        public string Password { get; set; }
        public string From { get; set; }

        public override string ToString()
        {
            return $"{Server}:{Port}";
        }
    }

    public interface ITalearrSmtpTransport
    {
        // Connects (and signs in when a username is set), then disconnects. Throws on failure.
        void Verify(TalearrSmtpServer server);

        void Send(TalearrSmtpServer server, MimeMessage message);
    }

    // Same connection rules as the upstream Email connection (Notifications/Email/Email.cs):
    // SSL on connect for port 465, StartTLS otherwise when encryption is required, the shared
    // certificate validation setting, and a sign-in only when a username is set.
    public class TalearrMailKitTransport : ITalearrSmtpTransport
    {
        // Per read/write operation. A 50 MB attachment over a slow uplink needs more than the
        // 10 seconds the Email connection uses for short notifications.
        private const int TimeoutMilliseconds = 120000;

        private readonly ICertificateValidationService _certificateValidationService;

        public TalearrMailKitTransport(ICertificateValidationService certificateValidationService)
        {
            _certificateValidationService = certificateValidationService;
        }

        public void Verify(TalearrSmtpServer server)
        {
            using var client = Connect(server);
            client.Disconnect(true);
        }

        public void Send(TalearrSmtpServer server, MimeMessage message)
        {
            using var client = Connect(server);
            client.Send(message);
            client.Disconnect(true);
        }

        private SmtpClient Connect(TalearrSmtpServer server)
        {
            var client = new SmtpClient { Timeout = TimeoutMilliseconds };

            try
            {
                var option = SecureSocketOptions.Auto;
                if (server.RequireEncryption)
                {
                    option = server.Port == 465 ? SecureSocketOptions.SslOnConnect : SecureSocketOptions.StartTls;
                }

                client.ServerCertificateValidationCallback = _certificateValidationService.ShouldByPassValidationError;
                client.Connect(server.Server, server.Port, option);

                if (!string.IsNullOrWhiteSpace(server.Username))
                {
                    client.Authenticate(server.Username, server.Password ?? string.Empty);
                }

                return client;
            }
            catch
            {
                client.Dispose();
                throw;
            }
        }
    }

    public sealed class TalearrKindleResult
    {
        public bool Success { get; set; }
        public string Message { get; set; }
        public string FileName { get; set; }
        public int Recipients { get; set; }
    }

    // What the book page needs to show the Send to Kindle action.
    public sealed class TalearrKindleBookStatus
    {
        public bool Enabled { get; set; }
        public bool CanSend { get; set; }
        public string Message { get; set; }
        public string FileName { get; set; }
    }

    public sealed class TalearrKindleEmailConnection
    {
        public int Id { get; set; }
        public string Name { get; set; }
    }

    public interface ITalearrKindleService
    {
        TalearrKindleBookStatus GetBookStatus(int bookId);
        TalearrKindleResult SendBook(int bookId);
        TalearrKindleResult Test(TalearrKindleSettings settings);
        List<TalearrKindleEmailConnection> EmailConnections();

        // Why these settings cannot reach an SMTP server, or null when they can.
        string ServerError(TalearrKindleSettings settings);
    }

    // TaleArr: Send to Kindle. Picks the best ebook file Amazon accepts by email (EPUB first), checks
    // the 50 MB limit, and mails it to the Kindle addresses. Never logs passwords or addresses.
    public class TalearrKindleService : ITalearrKindleService, IHandleAsync<BookImportedEvent>
    {
        public const long MaxBytes = 50L * 1024 * 1024;

        // Amazon's Send to Kindle by email (2024+): MOBI and AZW3 are no longer accepted.
        // Best first, so a book with both EPUB and PDF sends the EPUB.
        public static readonly string[] AcceptedExtensions =
        {
            ".epub", ".pdf", ".docx", ".doc", ".rtf", ".txt", ".htm", ".html"
        };

        private readonly ITalearrKindleSettingsService _settingsService;
        private readonly ITalearrSmtpTransport _transport;
        private readonly IBookService _bookService;
        private readonly IMediaFileService _mediaFileService;
        private readonly INotificationFactory _notificationFactory;
        private readonly Logger _logger;

        public TalearrKindleService(ITalearrKindleSettingsService settingsService,
                                    ITalearrSmtpTransport transport,
                                    IBookService bookService,
                                    IMediaFileService mediaFileService,
                                    INotificationFactory notificationFactory,
                                    Logger logger)
        {
            _settingsService = settingsService;
            _transport = transport;
            _bookService = bookService;
            _mediaFileService = mediaFileService;
            _notificationFactory = notificationFactory;
            _logger = logger;
        }

        public static string MimeType(string path)
        {
            var extension = Path.GetExtension(path ?? string.Empty).ToLowerInvariant();

            return extension switch
            {
                ".epub" => "application/epub+zip",
                ".pdf" => "application/pdf",
                ".docx" => "application/vnd.openxmlformats-officedocument.wordprocessingml.document",
                ".doc" => "application/msword",
                ".rtf" => "application/rtf",
                ".txt" => "text/plain",
                ".htm" or ".html" => "text/html",
                _ => "application/octet-stream"
            };
        }

        public static string FormatSize(long bytes)
        {
            return $"{bytes / 1024.0 / 1024.0:0.#} MB";
        }

        // Picks the file to send, or explains why none can be sent. fileSize returns -1 when the
        // file is missing on disk.
        public static (BookFile File, string Reason) PickFile(IEnumerable<BookFile> files, Func<string, long> fileSize)
        {
            var ebooks = (files ?? Enumerable.Empty<BookFile>())
                .Where(f => f != null && !string.IsNullOrWhiteSpace(f.Path) &&
                            MediaFileExtensions.TextExtensions.Contains(Path.GetExtension(f.Path)))
                .ToList();

            if (ebooks.Count == 0)
            {
                return (null, "This book has no ebook file to send.");
            }

            var accepted = ebooks
                .Where(f => !f.Path.EndsWith(".kepub.epub", StringComparison.OrdinalIgnoreCase))
                .Select(f => (File: f, Rank: Array.IndexOf(AcceptedExtensions, Path.GetExtension(f.Path).ToLowerInvariant())))
                .Where(x => x.Rank >= 0)
                .OrderBy(x => x.Rank)
                .Select(x => x.File)
                .ToList();

            if (accepted.Count == 0)
            {
                var formats = string.Join(", ", ebooks.Select(f => Path.GetExtension(f.Path).TrimStart('.').ToUpperInvariant()).Distinct());
                return (null, $"Kindle email does not accept {formats}. Amazon takes EPUB, PDF, DOC, DOCX, RTF, TXT and HTML (MOBI and AZW3 are no longer accepted). Get an EPUB copy of this book to send it.");
            }

            string tooLarge = null;
            var missing = 0;

            foreach (var file in accepted)
            {
                var size = fileSize(file.Path);

                if (size < 0)
                {
                    missing++;
                    continue;
                }

                if (size > MaxBytes)
                {
                    tooLarge ??= $"'{Path.GetFileName(file.Path)}' is {FormatSize(size)}. Send to Kindle email accepts files up to 50 MB.";
                    continue;
                }

                return (file, null);
            }

            return (null, tooLarge ?? (missing > 0 ? "The ebook file is missing on disk. Rescan the author." : "No file can be sent."));
        }

        public List<TalearrKindleEmailConnection> EmailConnections()
        {
            return _notificationFactory.All()
                .Where(d => d.Settings is EmailSettings)
                .OrderBy(d => d.Name, StringComparer.OrdinalIgnoreCase)
                .Select(d => new TalearrKindleEmailConnection { Id = d.Id, Name = d.Name })
                .ToList();
        }

        public string ServerError(TalearrKindleSettings settings)
        {
            return ResolveServer(settings ?? TalearrKindleSettings.Default).Error;
        }

        // Builds the SMTP server from an Email connection or the own fields, or explains what is missing.
        public (TalearrSmtpServer Server, string Error) ResolveServer(TalearrKindleSettings settings)
        {
            if (settings.EmailConnectionId > 0)
            {
                NotificationDefinition definition = null;

                try
                {
                    definition = _notificationFactory.Get(settings.EmailConnectionId);
                }
                catch (Exception ex)
                {
                    _logger.Debug(ex, "TaleArr: Email connection {0} not found", settings.EmailConnectionId);
                }

                if (definition?.Settings is not EmailSettings email)
                {
                    return (null, "The Email connection chosen for Send to Kindle no longer exists. Pick another one in Settings > Conversion.");
                }

                return Check(new TalearrSmtpServer
                {
                    Server = email.Server,
                    Port = email.Port,
                    RequireEncryption = email.RequireEncryption,
                    Username = email.Username,
                    Password = email.Password,
                    From = email.From
                });
            }

            return Check(new TalearrSmtpServer
            {
                Server = settings.Server?.Trim(),
                Port = settings.Port,
                RequireEncryption = settings.RequireEncryption,
                Username = settings.Username?.Trim(),
                Password = settings.Password,
                From = settings.From?.Trim()
            });
        }

        private static (TalearrSmtpServer Server, string Error) Check(TalearrSmtpServer server)
        {
            if (string.IsNullOrWhiteSpace(server.Server))
            {
                return (null, "Set the SMTP server for Send to Kindle.");
            }

            if (server.Port < 1 || server.Port > 65535)
            {
                return (null, "The SMTP port must be between 1 and 65535.");
            }

            if (string.IsNullOrWhiteSpace(server.From) || !MailboxAddress.TryParse(server.From, out _))
            {
                return (null, "Set a valid From address. Amazon only delivers mail from an address on your Approved Personal Document E-mail List.");
            }

            return (server, null);
        }

        private static string CheckAddresses(TalearrKindleSettings settings)
        {
            var addresses = TalearrKindleSettings.CleanAddresses(settings.KindleAddresses);

            if (addresses.Count == 0)
            {
                return "Add at least one Kindle address (for example name_123@kindle.com).";
            }

            var invalid = addresses.Where(a => !MailboxAddress.TryParse(a, out _) || !a.Contains('@')).ToList();
            return invalid.Count > 0 ? $"'{invalid[0]}' is not a valid email address." : null;
        }

        public TalearrKindleBookStatus GetBookStatus(int bookId)
        {
            var settings = _settingsService.Get();
            var status = new TalearrKindleBookStatus { Enabled = settings.Enabled };

            if (!settings.Enabled)
            {
                status.Message = "Send to Kindle is off. Turn it on in Settings > Conversion.";
                return status;
            }

            var (file, reason) = PickFile(_mediaFileService.GetFilesByBook(bookId), SizeOnDisk);
            status.CanSend = file != null;
            status.FileName = file == null ? null : Path.GetFileName(file.Path);
            status.Message = reason ?? $"Email '{status.FileName}' to your Kindle";

            return status;
        }

        public TalearrKindleResult SendBook(int bookId)
        {
            var settings = _settingsService.Get();

            if (!settings.Enabled)
            {
                return Fail("Send to Kindle is off. Turn it on in Settings > Conversion.");
            }

            var book = _bookService.GetBook(bookId);
            return Send(settings, book, _mediaFileService.GetFilesByBook(bookId));
        }

        public void HandleAsync(BookImportedEvent message)
        {
            try
            {
                var settings = _settingsService.Get();

                // Only imports from a download, like the reader hand-off: a disk scan must not
                // mail the whole library.
                if (message?.Book == null || !settings.IsActive || !settings.AutoSendOnImport || !message.NewDownload)
                {
                    return;
                }

                var hasEbook = (message.ImportedBooks ?? new List<BookFile>())
                    .Any(f => f?.Path != null && MediaFileExtensions.TextExtensions.Contains(Path.GetExtension(f.Path)));

                if (!hasEbook)
                {
                    return;
                }

                var result = Send(settings, message.Book, message.ImportedBooks);

                if (!result.Success)
                {
                    _logger.Warn("TaleArr: Send to Kindle skipped '{0}': {1}", message.Book.Title, result.Message);
                }
            }
            catch (Exception ex)
            {
                _logger.Warn(ex, "TaleArr: Send to Kindle failed for '{0}'", message?.Book?.Title);
            }
        }

        public TalearrKindleResult Send(TalearrKindleSettings settings, Book book, IEnumerable<BookFile> files)
        {
            var addressError = CheckAddresses(settings);
            if (addressError != null)
            {
                return Fail(addressError);
            }

            var (server, serverError) = ResolveServer(settings);
            if (serverError != null)
            {
                return Fail(serverError);
            }

            var (file, reason) = PickFile(files, SizeOnDisk);
            if (file == null)
            {
                return Fail(reason);
            }

            var addresses = TalearrKindleSettings.CleanAddresses(settings.KindleAddresses);
            var title = string.IsNullOrWhiteSpace(book?.Title) ? Path.GetFileNameWithoutExtension(file.Path) : book.Title;
            var authorName = book?.Author?.Name;
            var attachmentName = AttachmentName(authorName, title, file.Path);

            var message = new MimeMessage();
            message.From.Add(MailboxAddress.Parse(server.From));
            message.To.AddRange(addresses.Select(MailboxAddress.Parse));
            message.Subject = title;

            var body = new BodyBuilder { TextBody = $"{title}{(string.IsNullOrWhiteSpace(authorName) ? string.Empty : " by " + authorName)}, sent by TaleArr." };
            body.Attachments.Add(attachmentName, File.ReadAllBytes(file.Path), ContentType.Parse(MimeType(file.Path)));
            message.Body = body.ToMessageBody();

            try
            {
                _transport.Send(server, message);
            }
            catch (Exception ex)
            {
                _logger.Debug(ex, "TaleArr: Send to Kindle through {0} failed", server);
                return Fail($"The email could not be sent through {server}: {ex.Message}");
            }

            _logger.Info("TaleArr: sent '{0}' to {1} Kindle address(es)", Path.GetFileName(file.Path), addresses.Count);

            return new TalearrKindleResult
            {
                Success = true,
                FileName = attachmentName,
                Recipients = addresses.Count,
                Message = $"Sent '{attachmentName}' to {addresses.Count} Kindle address{(addresses.Count == 1 ? string.Empty : "es")}. It shows up on the Kindle once Amazon has processed it."
            };
        }

        public static string AttachmentName(string authorName, string title, string path)
        {
            var extension = Path.GetExtension(path ?? string.Empty).ToLowerInvariant();
            var name = string.IsNullOrWhiteSpace(authorName) ? title : $"{authorName} - {title}";
            var clean = FileNameBuilder.CleanFileName(name ?? string.Empty).Trim();

            return (clean.Length == 0 ? Path.GetFileNameWithoutExtension(path) : clean) + extension;
        }

        public TalearrKindleResult Test(TalearrKindleSettings settings)
        {
            settings = _settingsService.WithStoredPassword(settings);

            var addressError = CheckAddresses(settings);
            if (addressError != null)
            {
                return Fail(addressError);
            }

            var (server, serverError) = ResolveServer(settings);
            if (serverError != null)
            {
                return Fail(serverError);
            }

            try
            {
                _transport.Verify(server);
            }
            catch (Exception ex)
            {
                _logger.Debug(ex, "TaleArr: Send to Kindle test against {0} failed", server);
                return Fail($"Could not connect to {server}: {ex.Message}");
            }

            return new TalearrKindleResult
            {
                Success = true,
                Message = $"Connected to {server}. Make sure '{server.From}' is on your Approved Personal Document E-mail List at amazon.com, or Amazon drops the mail."
            };
        }

        private static long SizeOnDisk(string path)
        {
            try
            {
                return File.Exists(path) ? new FileInfo(path).Length : -1;
            }
            catch (Exception)
            {
                return -1;
            }
        }

        private static TalearrKindleResult Fail(string message)
        {
            return new TalearrKindleResult { Success = false, Message = message };
        }
    }
}
