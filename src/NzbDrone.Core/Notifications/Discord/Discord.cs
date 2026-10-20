using System;
using System.Collections.Generic;
using System.Linq;
using FluentValidation.Results;
using NzbDrone.Common.Extensions;
using NzbDrone.Core.Books;
using NzbDrone.Core.MediaFiles;
using NzbDrone.Core.Notifications.Discord.Payloads;
using NzbDrone.Core.Validation;

namespace NzbDrone.Core.Notifications.Discord
{
    public class Discord : NotificationBase<DiscordSettings>
    {
        private readonly IDiscordProxy _proxy;

        public Discord(IDiscordProxy proxy)
        {
            _proxy = proxy;
        }

        public override string Name => "Discord";
        public override string Link => "https://support.discordapp.com/hc/en-us/articles/228383668-Intro-to-Webhooks";

        public override void OnGrab(GrabMessage message)
        {
            // Talearr: cover art and book details; a last-resort bundle says so in the title.
            var remoteBook = message.RemoteBook;
            var book = remoteBook?.Books?.FirstOrDefault();
            var details = TalearrNotificationDetails.Describe(book, null, message.Quality, message.Author);
            var fields = new List<DiscordField>();
            AddField(fields, "Quality", message.Quality?.Quality?.Name, true);
            AddField(fields, "Size", remoteBook?.Release?.Size > 0 ? remoteBook.Release.Size.SizeSuffix() : null, true);
            AddField(fields, "Indexer", remoteBook?.Release?.Indexer, true);
            AddField(fields, "Download Client", message.DownloadClientName, true);

            if (remoteBook?.IsBundle == true)
            {
                AddField(fields, "Bundle", TalearrNotificationDetails.IsBundleFallback(remoteBook) ? "Grabbed as last resort: no single copy was found" : "Yes", false);
            }

            AddField(fields, "Release", remoteBook?.Release?.Title, false);

            var title = TalearrNotificationDetails.IsBundleFallback(remoteBook) ? BUNDLE_GRABBED_TITLE : BOOK_GRABBED_TITLE;
            var embeds = new List<Embed>
            {
                BookEmbed(details, message.Author?.Name, title, message.Message, DiscordColors.Warning, fields)
            };
            var payload = CreatePayload($"Grabbed: {message.Message}", embeds);

            _proxy.SendPayload(payload, Settings);
        }

        public override void OnReleaseImport(BookDownloadMessage message)
        {
            // Talearr: cover art and book details; upgrades are labelled as such.
            var bookFile = message.BookFiles?.FirstOrDefault();
            var isUpgrade = message.OldFiles?.Any() == true;
            var details = TalearrNotificationDetails.Describe(message.Book, bookFile?.Edition, bookFile?.Quality, message.Author);
            var fields = new List<DiscordField>();
            AddField(fields, "Quality", bookFile?.Quality?.Quality?.Name, true);
            AddField(fields, "Files", message.BookFiles?.Count > 0 ? message.BookFiles.Count.ToString() : null, true);
            AddField(fields, "Size", message.BookFiles?.Sum(f => f.Size) > 0 ? message.BookFiles.Sum(f => f.Size).SizeSuffix() : null, true);
            AddField(fields, "Download Client", message.DownloadClientInfo?.Name, true);

            var attachments = new List<Embed>
            {
                BookEmbed(details,
                    message.Author?.Name,
                    isUpgrade ? BOOK_UPGRADED_TITLE : BOOK_DOWNLOADED_TITLE,
                    message.Message,
                    isUpgrade ? DiscordColors.Upgrade : DiscordColors.Success,
                    fields)
            };
            var payload = CreatePayload($"{(isUpgrade ? "Upgraded" : "Imported")}: {message.Message}", attachments);

            _proxy.SendPayload(payload, Settings);
        }

        public override void OnRename(Author author, List<RenamedBookFile> renamedFiles)
        {
            var attachments = new List<Embed>
            {
                new ()
                {
                    Title = author.Name,
                }
            };

            var payload = CreatePayload("Renamed", attachments);

            _proxy.SendPayload(payload, Settings);
        }

        public override void OnAuthorAdded(Author author)
        {
            var attachments = new List<Embed>
            {
                new ()
                {
                    Title = author.Name,
                    Fields = new List<DiscordField>()
                    {
                        new ()
                        {
                            Name = "Links",
                            Value = string.Join(" / ", author.Links.Select(link => $"[{link.Name}]({link.Url})"))
                        }
                    },
                }
            };
            var payload = CreatePayload("Author Added", attachments);

            _proxy.SendPayload(payload, Settings);
        }

        public override void OnBookAdded(Book book)
        {
            // Talearr: cover art and book details.
            var details = TalearrNotificationDetails.Describe(book);
            var attachments = new List<Embed>
            {
                BookEmbed(details, book.Author?.Name, $"{book.Author?.Name} - {book.Title}", details?.Overview, DiscordColors.Success, new List<DiscordField>())
            };
            var payload = CreatePayload("Book Added", attachments);

            _proxy.SendPayload(payload, Settings);
        }

        public override void OnAuthorDelete(AuthorDeleteMessage deleteMessage)
        {
            var attachments = new List<Embed>
            {
                new ()
                {
                    Title = deleteMessage.Author.Name,
                    Description = deleteMessage.DeletedFilesMessage
                }
            };

            var payload = CreatePayload("Author Deleted", attachments);

            _proxy.SendPayload(payload, Settings);
        }

        public override void OnBookDelete(BookDeleteMessage deleteMessage)
        {
            var attachments = new List<Embed>
            {
                new ()
                {
                    Title = $"{deleteMessage.Book.Author.Name} - {deleteMessage.Book.Title}",
                    Description = deleteMessage.DeletedFilesMessage,

                    // Talearr: cover art on deletes too.
                    Thumbnail = Cover(TalearrNotificationDetails.CoverUrl(deleteMessage.Book))
                }
            };

            var payload = CreatePayload("Book Deleted", attachments);

            _proxy.SendPayload(payload, Settings);
        }

        public override void OnBookFileDelete(BookFileDeleteMessage deleteMessage)
        {
            var attachments = new List<Embed>
            {
                new ()
                {
                    Title = $"{deleteMessage.Book.Author.Name} - {deleteMessage.Book.Title} - file deleted",
                    Description = deleteMessage.BookFile.Path
                }
            };

            var payload = CreatePayload("Book File Deleted", attachments);

            _proxy.SendPayload(payload, Settings);
        }

        public override void OnHealthIssue(HealthCheck.HealthCheck healthCheck)
        {
            var attachments = new List<Embed>
            {
                new ()
                {
                    Title = healthCheck.Source.Name,
                    Text = healthCheck.Message,
                    Color = healthCheck.Type == HealthCheck.HealthCheckResult.Warning ? (int)DiscordColors.Warning : (int)DiscordColors.Danger
                }
            };

            var payload = CreatePayload("Health Issue", attachments);

            _proxy.SendPayload(payload, Settings);
        }

        // Talearr: Sonarr/Radarr "Health Restored".
        public override void OnHealthRestored(HealthCheck.HealthCheck previousCheck)
        {
            var attachments = new List<Embed>
            {
                new ()
                {
                    Title = previousCheck.Source.Name,
                    Description = $"The following issue is now resolved: {previousCheck.Message}",
                    Color = (int)DiscordColors.Success
                }
            };

            var payload = CreatePayload(HEALTH_RESTORED_TITLE, attachments);

            _proxy.SendPayload(payload, Settings);
        }

        public override void OnBookRetag(BookRetagMessage message)
        {
            var attachments = new List<Embed>
            {
                new ()
                {
                    Title = BOOK_RETAGGED_TITLE,
                    Text = message.Message
                }
            };

            var payload = CreatePayload($"Track file tags updated: {message.Message}", attachments);

            _proxy.SendPayload(payload, Settings);
        }

        public override void OnDownloadFailure(DownloadFailedMessage message)
        {
            // Talearr: book details when the failed download is still tracked.
            var book = message.Books?.FirstOrDefault();
            var details = TalearrNotificationDetails.Describe(book, null, message.Quality, message.Author);
            var fields = new List<DiscordField>();
            AddField(fields, "Release", message.SourceTitle, false);
            AddField(fields, "Download Client", message.DownloadClientInfo?.Name ?? message.DownloadClient, true);

            var attachments = new List<Embed>
            {
                details == null
                    ? new Embed
                    {
                        Description = message.Message,
                        Title = message.SourceTitle,
                        Text = message.Message,
                        Color = (int)DiscordColors.Danger
                    }
                    : BookEmbed(details, message.Author?.Name, DOWNLOAD_FAILURE_TITLE, message.Message, DiscordColors.Danger, fields)
            };
            var payload = CreatePayload($"Download Failed: {message.Message}", attachments);

            _proxy.SendPayload(payload, Settings);
        }

        public override void OnImportFailure(BookDownloadMessage message)
        {
            // Talearr: the reasons, and whether the import waits for the user (Manual Interaction Required).
            var details = TalearrNotificationDetails.Describe(message.Book, null, message.RemoteBook?.ParsedBookInfo?.Quality, message.Author);
            var title = message.ManualInteractionRequired ? MANUAL_INTERACTION_REQUIRED_TITLE : IMPORT_FAILURE_TITLE;
            var fields = new List<DiscordField>();
            AddField(fields, "Release", message.SourceTitle, false);
            AddField(fields, "Reason", message.StatusMessages?.Any() == true ? string.Join("\n", message.StatusMessages.Take(5)) : null, false);

            var embed = details == null
                ? new Embed
                {
                    Description = message.Message,
                    Title = message.Book?.Title ?? title,
                    Text = message.Message,
                    Color = (int)DiscordColors.Warning,
                    Fields = fields
                }
                : BookEmbed(details, message.Author?.Name, title, message.Message, DiscordColors.Warning, fields);

            var attachments = new List<Embed> { embed };
            var payload = CreatePayload($"{title}: {message.Message}", attachments);

            _proxy.SendPayload(payload, Settings);
        }

        public override void OnApplicationUpdate(ApplicationUpdateMessage updateMessage)
        {
            var attachments = new List<Embed>
            {
                new ()
                {
                    Author = new DiscordAuthor
                    {
                        Name = Settings.Author.IsNullOrWhiteSpace() ? Environment.MachineName : Settings.Author,
                        IconUrl = "https://raw.githubusercontent.com/Chaptarr/Chaptarr/develop/Logo/256.png"
                    },
                    Title = APPLICATION_UPDATE_TITLE,
                    Timestamp = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.fffZ"),
                    Color = (int)DiscordColors.Standard,
                    Fields = new List<DiscordField>()
                    {
                        new ()
                        {
                            Name = "Previous Version",
                            Value = updateMessage.PreviousVersion.ToString()
                        },
                        new ()
                        {
                            Name = "New Version",
                            Value = updateMessage.NewVersion.ToString()
                        }
                    },
                }
            };

            var payload = CreatePayload(null, attachments);

            _proxy.SendPayload(payload, Settings);
        }

        public override ValidationResult Test()
        {
            var failures = new List<ValidationFailure>();

            failures.AddIfNotNull(TestMessage());

            return new ValidationResult(failures);
        }

        public ValidationFailure TestMessage()
        {
            try
            {
                var message = $"Test message from Talearr posted at {DateTime.Now}";
                var payload = CreatePayload(message);

                _proxy.SendPayload(payload, Settings);
            }
            catch (DiscordException ex)
            {
                return new NzbDroneValidationFailure("Unable to post", ex.Message);
            }

            return null;
        }

        // Talearr: one embed layout for book events: cover thumbnail, author line, then format,
        // narrator and series before the event's own fields.
        private static Embed BookEmbed(TalearrBookDetails details, string authorName, string title, string description, DiscordColors color, List<DiscordField> eventFields)
        {
            var fields = new List<DiscordField>();

            if (details != null)
            {
                AddField(fields, "Book", details.Year.HasValue ? $"{details.Title} ({details.Year})" : details.Title, false);
                AddField(fields, "Author", authorName ?? details.AuthorName, true);
                AddField(fields, "Format", details.FormatLabel, true);
                AddField(fields, "Narrator", details.Narrator, true);
                AddField(fields, "Series", details.Series, true);
            }
            else
            {
                AddField(fields, "Author", authorName, true);
            }

            fields.AddRange(eventFields ?? new List<DiscordField>());

            return new Embed
            {
                Title = title,
                Description = description,
                Text = description,
                Color = (int)color,
                Thumbnail = Cover(details?.CoverUrl),
                Timestamp = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.fffZ"),
                Fields = fields
            };
        }

        private static DiscordImage Cover(string url)
        {
            return url.IsNullOrWhiteSpace() ? null : new DiscordImage { Url = url };
        }

        // Discord rejects empty field values and values over 1024 characters.
        private static void AddField(List<DiscordField> fields, string name, string value, bool inline)
        {
            if (value.IsNullOrWhiteSpace())
            {
                return;
            }

            fields.Add(new DiscordField
            {
                Name = name,
                Value = value.Length > 1024 ? value.Substring(0, 1021) + "..." : value,
                Inline = inline
            });
        }

        private DiscordPayload CreatePayload(string message, List<Embed> embeds = null)
        {
            var avatar = Settings.Avatar;

            var payload = new DiscordPayload
            {
                Username = Settings.Username,
                Content = message,
                Embeds = embeds
            };

            if (avatar.IsNotNullOrWhiteSpace())
            {
                payload.AvatarUrl = avatar;
            }

            if (Settings.Username.IsNotNullOrWhiteSpace())
            {
                payload.Username = Settings.Username;
            }

            return payload;
        }
    }
}
