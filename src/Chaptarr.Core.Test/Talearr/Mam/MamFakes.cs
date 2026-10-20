using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Reflection;
using System.Threading.Tasks;
using NLog;
using NzbDrone.Common.Http;
using NzbDrone.Common.Messaging;
using NzbDrone.Core.Indexers;
using NzbDrone.Core.Indexers.MyAnonaMouse;
using NzbDrone.Core.Messaging.Events;

namespace Chaptarr.Core.Test.Talearr.Mam
{
    // Talearr: hand-written fakes for the MAM hardening fixtures. Every MAM reply below is a
    // recorded shape; nothing here touches the network.
    public class FakeMamHttpClient : DispatchProxy
    {
        public Queue<Func<HttpRequest, HttpResponse>> Responses { get; } = new Queue<Func<HttpRequest, HttpResponse>>();
        public List<HttpRequest> Requests { get; } = new List<HttpRequest>();

        public static FakeMamHttpClient Create(out IIndexerHttpClient client)
        {
            client = DispatchProxy.Create<IIndexerHttpClient, FakeMamHttpClient>();
            return (FakeMamHttpClient)(object)client;
        }

        public void Reply(HttpStatusCode status, string contentType, string content, params string[] setCookies)
        {
            Responses.Enqueue(request =>
            {
                var headers = new HttpHeader { ContentType = contentType };
                foreach (var cookie in setCookies)
                {
                    headers.Add("Set-Cookie", cookie);
                }

                var response = new HttpResponse(request, headers, content, status);
                if (response.HasHttpError && !request.SuppressHttpError)
                {
                    throw new HttpException(request, response);
                }

                return response;
            });
        }

        protected override object Invoke(MethodInfo targetMethod, object[] args)
        {
            if (targetMethod?.Name == nameof(IIndexerHttpClient.ExecuteAsync) && args?[0] is HttpRequest request)
            {
                Requests.Add(request);
                return Task.FromResult(Responses.Dequeue()(request));
            }

            throw new NotImplementedException(targetMethod?.Name);
        }
    }

    public class FakeMamHttpClientFactory : DispatchProxy
    {
        public IIndexerHttpClient Client { get; set; }
        public List<int?> RequestedProxyIds { get; } = new List<int?>();

        public static FakeMamHttpClientFactory Create(IIndexerHttpClient client, out IIndexerHttpClientFactory factory)
        {
            factory = DispatchProxy.Create<IIndexerHttpClientFactory, FakeMamHttpClientFactory>();
            var state = (FakeMamHttpClientFactory)(object)factory;
            state.Client = client;
            return state;
        }

        protected override object Invoke(MethodInfo targetMethod, object[] args)
        {
            if (targetMethod?.Name == nameof(IIndexerHttpClientFactory.GetClient))
            {
                RequestedProxyIds.Add((int?)args[0]);
                return Client;
            }

            throw new NotImplementedException(targetMethod?.Name);
        }
    }

    public class FakeMamIndexerFactory : DispatchProxy
    {
        public List<IndexerDefinition> Definitions { get; } = new List<IndexerDefinition>();
        public List<IIndexer> Providers { get; } = new List<IIndexer>();
        public List<IndexerDefinition> Updated { get; } = new List<IndexerDefinition>();

        public static FakeMamIndexerFactory Create(out IIndexerFactory factory)
        {
            factory = DispatchProxy.Create<IIndexerFactory, FakeMamIndexerFactory>();
            return (FakeMamIndexerFactory)(object)factory;
        }

        protected override object Invoke(MethodInfo targetMethod, object[] args)
        {
            switch (targetMethod?.Name)
            {
                case nameof(IIndexerFactory.Find):
                    return Definitions.FirstOrDefault(d => d.Id == (int)args[0]);
                case nameof(IIndexerFactory.All):
                    return Definitions.ToList();
                case nameof(IIndexerFactory.GetAvailableProviders):
                    return Providers.ToList();
                case nameof(IIndexerFactory.Update) when args[0] is IndexerDefinition definition:
                    Updated.Add(definition);
                    return null;
                default:
                    throw new NotImplementedException(targetMethod?.Name);
            }
        }
    }

    public class FakeEventAggregator : IEventAggregator
    {
        public List<IEvent> Published { get; } = new List<IEvent>();

        public void PublishEvent<TEvent>(TEvent @event)
            where TEvent : class, IEvent
        {
            Published.Add(@event);
        }
    }

    public class RecordingSessionMonitor : IMyAnonaMouseSessionMonitor
    {
        public List<string> Calls { get; } = new List<string>();
        public Dictionary<int, MyAnonaMouseSessionHealth> State { get; } = new Dictionary<int, MyAnonaMouseSessionHealth>();

        public void RecordSessionAccepted(int indexerId, string mamId)
        {
            Calls.Add($"accepted:{indexerId}:{mamId}");
        }

        public void RecordSessionRejected(int indexerId, string mamId, string source)
        {
            Calls.Add($"rejected:{indexerId}:{mamId}:{source}");
        }

        public void RecordRotatedCookie(int indexerId, MyAnonaMouseCookieField field, string previousValue, string rotatedValue)
        {
            Calls.Add($"rotated:{indexerId}:{field}:{previousValue}->{rotatedValue}");
        }

        public void RecordSeedboxResult(int indexerId, string mamId, MyAnonaMouseSeedboxResult result)
        {
            Calls.Add($"seedbox:{indexerId}:{mamId}:{result.Outcome}");
            State[indexerId] = new MyAnonaMouseSessionHealth { Seedbox = result, SeedboxMamId = mamId };
        }

        public MyAnonaMouseSessionHealth Get(int indexerId)
        {
            return State.TryGetValue(indexerId, out var health) ? health : new MyAnonaMouseSessionHealth();
        }
    }

    public static class MamTestIndexers
    {
        public static NzbDrone.Core.Indexers.MyAnonaMouse.MyAnonaMouse Create(MyAnonaMouseSettings settings,
                                                                              IIndexerHttpClientFactory factory,
                                                                              IMyAnonaMouseSessionMonitor monitor,
                                                                              int id = 9,
                                                                              int? proxyId = null)
        {
            return new NzbDrone.Core.Indexers.MyAnonaMouse.MyAnonaMouse(factory, null, null, null, null, LogManager.GetCurrentClassLogger(), monitor)
            {
                Definition = new IndexerDefinition
                {
                    Id = id,
                    Name = "MyAnonaMouse",
                    Implementation = nameof(NzbDrone.Core.Indexers.MyAnonaMouse.MyAnonaMouse),
                    EnableAutomaticSearch = true,
                    ProxyId = proxyId,
                    Settings = settings
                }
            };
        }
    }
}
