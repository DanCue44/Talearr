using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Reflection;
using System.Threading.Tasks;
using NzbDrone.Common.Http;

namespace Chaptarr.Core.Test
{
    // Talearr: an IHttpClient for tests that routes every request to a handler and, like the real
    // client, throws HttpException for error status codes. Records the requests it saw.
    public class FakeHttpClient : DispatchProxy
    {
        public Func<HttpRequest, (HttpStatusCode Status, string Content)> Handler { get; set; }

        public List<HttpRequest> Requests { get; } = new List<HttpRequest>();

        public static (IHttpClient Client, FakeHttpClient Fake) Create(Func<HttpRequest, (HttpStatusCode Status, string Content)> handler)
        {
            var client = Create<IHttpClient, FakeHttpClient>();
            var fake = (FakeHttpClient)(object)client;
            fake.Handler = handler;
            return (client, fake);
        }

        protected override object Invoke(MethodInfo targetMethod, object[] args)
        {
            var request = args?.Length > 0 ? args[0] as HttpRequest : null;

            if (request == null)
            {
                throw new NotSupportedException(targetMethod.Name);
            }

            Requests.Add(request);
            var (status, content) = Handler(request);
            var response = new HttpResponse(request, new HttpHeader(), content ?? string.Empty, status);

            var suppressed = request.SuppressHttpError ||
                             (request.SuppressHttpErrorStatusCodes?.Contains(status) ?? false);

            if ((int)status >= 400 && !suppressed)
            {
                throw new HttpException(request, response);
            }

            if (targetMethod.ReturnType == typeof(Task<HttpResponse>))
            {
                return Task.FromResult(response);
            }

            return response;
        }
    }
}
