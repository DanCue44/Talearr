using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using FluentValidation.Results;
using NzbDrone.Core.Books;
using NzbDrone.Core.Notifications;
using NzbDrone.Core.ThingiProvider;
using NzbDrone.Core.Validation;

using HealthCheckModel = NzbDrone.Core.HealthCheck.HealthCheck;

namespace Chaptarr.Core.Test.Talearr.Notifications
{
    // Records every call and answers from per-method handlers; unhandled calls return defaults.
    public class RecordingProxy : DispatchProxy
    {
        public Dictionary<string, Func<object[], object>> Handlers { get; } = new Dictionary<string, Func<object[], object>>();
        public List<Tuple<string, object[]>> Calls { get; } = new List<Tuple<string, object[]>>();

        public static T Create<T>(out RecordingProxy proxy)
            where T : class
        {
            var instance = DispatchProxy.Create<T, RecordingProxy>();
            proxy = (RecordingProxy)(object)instance;
            return instance;
        }

        public int Count(string name)
        {
            return Calls.Count(c => c.Item1 == name);
        }

        public List<object> Args(string name, int index = 0)
        {
            return Calls.Where(c => c.Item1 == name).Select(c => c.Item2[index]).ToList();
        }

        protected override object Invoke(MethodInfo targetMethod, object[] args)
        {
            Calls.Add(Tuple.Create(targetMethod.Name, args));

            if (Handlers.TryGetValue(targetMethod.Name, out var handler))
            {
                return handler(args);
            }

            var returnType = targetMethod.ReturnType;

            if (returnType == typeof(void))
            {
                return null;
            }

            if (returnType.IsGenericType && returnType.GetGenericTypeDefinition() == typeof(List<>))
            {
                return Activator.CreateInstance(returnType);
            }

            return returnType.IsValueType ? Activator.CreateInstance(returnType) : null;
        }
    }

    public class FakeNotificationSettings : IProviderConfig
    {
        public NzbDroneValidationResult Validate()
        {
            return new NzbDroneValidationResult();
        }
    }

    // A connection that only records what it was told.
    public class RecordingNotification : NotificationBase<FakeNotificationSettings>
    {
        public List<HealthCheckModel> HealthIssues { get; } = new List<HealthCheckModel>();
        public List<HealthCheckModel> HealthRestored { get; } = new List<HealthCheckModel>();
        public List<BookDownloadMessage> ImportFailures { get; } = new List<BookDownloadMessage>();
        public List<DownloadFailedMessage> DownloadFailures { get; } = new List<DownloadFailedMessage>();
        public List<GrabMessage> Grabs { get; } = new List<GrabMessage>();

        public RecordingNotification(int id, bool includeHealthWarnings = false, params int[] tags)
        {
            Definition = new NotificationDefinition
            {
                Id = id,
                Name = $"recorder {id}",
                IncludeHealthWarnings = includeHealthWarnings,
                Tags = new HashSet<int>(tags),
                Settings = new FakeNotificationSettings()
            };
        }

        public override string Name => "Recorder";
        public override string Link => "https://example.invalid";

        public override ValidationResult Test()
        {
            return new ValidationResult();
        }

        public override void OnGrab(GrabMessage grabMessage)
        {
            Grabs.Add(grabMessage);
        }

        public override void OnHealthIssue(HealthCheckModel healthCheck)
        {
            HealthIssues.Add(healthCheck);
        }

        public override void OnHealthRestored(HealthCheckModel previousCheck)
        {
            HealthRestored.Add(previousCheck);
        }

        public override void OnImportFailure(BookDownloadMessage message)
        {
            ImportFailures.Add(message);
        }

        public override void OnDownloadFailure(DownloadFailedMessage message)
        {
            DownloadFailures.Add(message);
        }
    }

    public static class NotificationTestData
    {
        public static Author Author(int id = 1, params int[] tags)
        {
            return new Author { Id = id, Name = "Frank Herbert", Tags = new HashSet<int>(tags) };
        }

        public static Book Audiobook(Author author = null)
        {
            var book = new Book
            {
                Id = 10,
                Title = "Dune",
                MediaType = BookMediaType.Audiobook,
                SeriesName = "Dune",
                SeriesPosition = "1",
                ReleaseDate = new DateTime(1965, 8, 1),
                Images = new List<NzbDrone.Core.MediaCover.MediaCover>
                {
                    new NzbDrone.Core.MediaCover.MediaCover(NzbDrone.Core.MediaCover.MediaCoverTypes.Cover, "/MediaCover/Books/10/cover.jpg")
                    {
                        RemoteUrl = "https://covers.example/dune.jpg"
                    }
                },
                Editions = new List<Edition>
                {
                    new Edition
                    {
                        Id = 3,
                        Monitored = true,
                        Title = "Dune",
                        NarratorNames = new List<string> { "Scott Brick", "Orlagh Cassidy" }
                    }
                }
            };

            if (author != null)
            {
                book.Author = author;
            }

            return book;
        }
    }
}
