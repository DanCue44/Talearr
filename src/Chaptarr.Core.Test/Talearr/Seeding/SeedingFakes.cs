using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using NzbDrone.Core.Books;
using NzbDrone.Core.Download;
using NzbDrone.Core.Download.Clients.QBittorrent;
using NzbDrone.Core.History;
using NzbDrone.Core.Indexers;
using NzbDrone.Core.Talearr.Seeding;

namespace Chaptarr.Core.Test.Talearr.Seeding
{
    // Talearr: hand-written fakes for the seeding report. A method without a handler returns the
    // default value for its return type.
    public class SeedingFake<T> : DispatchProxy
        where T : class
    {
        public Dictionary<string, Func<object[], object>> Handlers { get; } = new Dictionary<string, Func<object[], object>>();
        public Dictionary<string, int> Calls { get; } = new Dictionary<string, int>();

        public static SeedingFake<T> Create(out T instance)
        {
            instance = DispatchProxy.Create<T, SeedingFake<T>>();
            return (SeedingFake<T>)(object)instance;
        }

        public SeedingFake<T> On(string method, Func<object[], object> handler)
        {
            Handlers[method] = handler;
            return this;
        }

        public int CallCount(string method)
        {
            return Calls.TryGetValue(method, out var count) ? count : 0;
        }

        protected override object Invoke(MethodInfo targetMethod, object[] args)
        {
            Calls[targetMethod.Name] = CallCount(targetMethod.Name) + 1;

            if (Handlers.TryGetValue(targetMethod.Name, out var handler))
            {
                return handler(args);
            }

            var type = targetMethod.ReturnType;
            return type == typeof(void) || !type.IsValueType ? null : Activator.CreateInstance(type);
        }
    }

    public interface IFakeSeedingClient : IDownloadClient, ITalearrSeedingSource
    {
    }

    public static class SeedingTestData
    {
        // Shaped like a live qBittorrent 5 /torrents/info entry for a MAM torrent (fake passkey).
        public const string MamTorrentJson = @"{
            ""added_on"": 1792263075, ""category"": ""books"", ""completion_on"": 1792263093,
            ""content_path"": ""/downloads/torrents/Jennifer Lynn Barnes - The Naturals"",
            ""hash"": ""7955efd68558d09baa87e573fec0bbfde10c9b34"", ""max_seeding_time"": 43200,
            ""name"": ""Jennifer Lynn Barnes - The Naturals"", ""private"": true, ""progress"": 1,
            ""ratio"": 0.25, ""ratio_limit"": -2, ""save_path"": ""/downloads/torrents"",
            ""seeding_time"": 81, ""seeding_time_limit"": 43200, ""size"": 215237502,
            ""state"": ""stalledUP"", ""tracker"": ""https://t.myanonamouse.net/tracker.php/FAKEPASSKEY/announce"",
            ""uploaded"": 53809375
        }";

        public static TalearrSeedingClientTorrent Torrent(string hash, string state, long? seeding, bool? isPrivate = true, bool mam = true)
        {
            return new TalearrSeedingClientTorrent
            {
                DownloadId = hash,
                Title = "Release " + hash,
                Category = "books",
                State = state,
                Size = 1000,
                Uploaded = 250,
                Ratio = 0.25,
                SeedingSeconds = seeding,
                SeedLimitSeconds = 30 * 24 * 3600,
                AddedAt = new DateTime(2026, 10, 17, 19, 50, 0, DateTimeKind.Utc),
                CompletedAt = new DateTime(2026, 10, 17, 19, 51, 0, DateTimeKind.Utc),
                IsPrivate = isPrivate,
                IsMamTracker = mam
            };
        }

        public static TalearrSeedingIndexerInfo Mam()
        {
            return new TalearrSeedingIndexerInfo { Name = "MyAnonamouse", IsMam = true, SeedTimeSeconds = 720 * 3600 };
        }

        public static EntityHistory Grab(string hash, int bookId, int authorId, string indexer, string client, DateTime date)
        {
            var history = new EntityHistory
            {
                DownloadId = hash,
                BookId = bookId,
                AuthorId = authorId,
                EventType = EntityHistoryEventType.Grabbed,
                Date = date
            };

            history.Data["Indexer"] = indexer;
            history.Data["DownloadClientName"] = client;
            history.Data["Protocol"] = ((int)DownloadProtocol.Torrent).ToString();

            return history;
        }

        public static QBittorrentTorrent ParseQBittorrent(string json)
        {
            return Newtonsoft.Json.JsonConvert.DeserializeObject<QBittorrentTorrent>(json);
        }

        public static List<Book> Books(params (int Id, int AuthorId, string Title, BookMediaType Type)[] books)
        {
            return books.Select(b => new Book { Id = b.Id, AuthorId = b.AuthorId, Title = b.Title, MediaType = b.Type }).ToList();
        }
    }
}
