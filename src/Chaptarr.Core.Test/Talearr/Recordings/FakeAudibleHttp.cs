using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Reflection;
using NzbDrone.Common.Http;

namespace Chaptarr.Core.Test.Talearr.Recordings
{
    // IHttpClient stand-in that answers from recorded fixtures by URL and keeps every request.
    internal class FakeAudibleHttp : DispatchProxy
    {
        public List<HttpRequest> Requests { get; } = new List<HttpRequest>();

        // (url fragment, status, body, headers). First match wins.
        public List<(string Fragment, HttpStatusCode Status, string Body, Dictionary<string, string> Headers)> Routes { get; } = new();

        public static (IHttpClient Client, FakeAudibleHttp Fake) Create()
        {
            var client = Create<IHttpClient, FakeAudibleHttp>();
            return (client, (FakeAudibleHttp)(object)client);
        }

        public FakeAudibleHttp On(string fragment, string body, HttpStatusCode status = HttpStatusCode.OK, Dictionary<string, string> headers = null)
        {
            Routes.Add((fragment, status, body, headers));
            return this;
        }

        public int CountTo(string fragment)
        {
            return Requests.Count(r => r.Url.FullUri.Contains(fragment, StringComparison.OrdinalIgnoreCase));
        }

        protected override object Invoke(MethodInfo targetMethod, object[] args)
        {
            if (targetMethod?.Name != nameof(IHttpClient.Execute))
            {
                throw new NotImplementedException($"Fake IHttpClient does not implement {targetMethod?.Name}");
            }

            var request = (HttpRequest)args[0];
            Requests.Add(request);

            var url = request.Url.FullUri;
            foreach (var route in Routes)
            {
                if (url.Contains(route.Fragment, StringComparison.OrdinalIgnoreCase))
                {
                    var headers = new HttpHeader();
                    foreach (var header in route.Headers ?? new Dictionary<string, string>())
                    {
                        headers.Add(header.Key, header.Value);
                    }

                    return new HttpResponse(request, headers, route.Body ?? string.Empty, route.Status);
                }
            }

            throw new InvalidOperationException("Unexpected request in test: " + url);
        }
    }
}
