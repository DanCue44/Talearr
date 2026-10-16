using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading.Tasks;
using NLog;
using NUnit.Framework;
using NzbDrone.Common.Http;
using NzbDrone.Common.Messaging;
using NzbDrone.Core.HealthCheck;
using NzbDrone.Core.HealthCheck.Checks;
using NzbDrone.Core.Messaging.Events;
using NzbDrone.Core.MetadataSource;

namespace Chaptarr.Core.Test.Talearr
{
    [TestFixture]
    public class TalearrMetadataServerProbeFixture
    {
        private sealed class ScriptedHttpClient : IHttpClient
        {
            private readonly Queue<Func<HttpRequest, HttpResponse>> _script = new();

            public List<HttpRequest> Requests { get; } = new();

            public ScriptedHttpClient Respond(HttpStatusCode statusCode)
            {
                _script.Enqueue(request => new HttpResponse(request, new HttpHeader(), "ok", statusCode));
                return this;
            }

            public ScriptedHttpClient Throw(Exception exception)
            {
                _script.Enqueue(_ => throw exception);
                return this;
            }

            public HttpResponse Execute(HttpRequest request)
            {
                Requests.Add(request);
                return _script.Dequeue()(request);
            }

            public void DownloadFile(string url, string fileName, string userAgent = null) => throw new NotImplementedException();
            public HttpResponse Get(HttpRequest request) => throw new NotImplementedException();
            public HttpResponse<T> Get<T>(HttpRequest request) where T : new() => throw new NotImplementedException();
            public HttpResponse Head(HttpRequest request) => throw new NotImplementedException();
            public HttpResponse Post(HttpRequest request) => throw new NotImplementedException();
            public HttpResponse<T> Post<T>(HttpRequest request) where T : new() => throw new NotImplementedException();
            public Task<HttpResponse> ExecuteAsync(HttpRequest request) => throw new NotImplementedException();
            public Task DownloadFileAsync(string url, string fileName, string userAgent = null) => throw new NotImplementedException();
            public Task<HttpResponse> GetAsync(HttpRequest request) => throw new NotImplementedException();
            public Task<HttpResponse<T>> GetAsync<T>(HttpRequest request) where T : new() => throw new NotImplementedException();
            public Task<HttpResponse> HeadAsync(HttpRequest request) => throw new NotImplementedException();
            public Task<HttpResponse> PostAsync(HttpRequest request) => throw new NotImplementedException();
            public Task<HttpResponse<T>> PostAsync<T>(HttpRequest request) where T : new() => throw new NotImplementedException();
        }

        private sealed class RecordingEventAggregator : IEventAggregator
        {
            public List<IEvent> Events { get; } = new();

            public void PublishEvent<TEvent>(TEvent @event)
                where TEvent : class, IEvent
            {
                Events.Add(@event);
            }
        }

        private static TalearrMetadataServerProbe CreateProbe(ScriptedHttpClient http, RecordingEventAggregator events, string url = "https://api2.chaptarr.com")
        {
            var config = ConfigServiceTestProxy.Create();
            ((ConfigServiceTestProxy)(object)config).MetadataServerUrl = url;

            return new TalearrMetadataServerProbe(http, config, events, LogManager.GetCurrentClassLogger());
        }

        [Test]
        public void healthy_probe_sends_one_short_get_to_the_configured_server_root()
        {
            var http = new ScriptedHttpClient().Respond(HttpStatusCode.OK);
            var probe = CreateProbe(http, new RecordingEventAggregator());

            var result = probe.ProbeNow();

            Assert.That(result.State, Is.EqualTo(MetadataServerProbeState.Healthy));
            Assert.That(result.SourceName, Is.EqualTo("https://api2.chaptarr.com"));
            Assert.That(http.Requests, Has.Count.EqualTo(1));

            var request = http.Requests[0];
            Assert.That(request.Method, Is.EqualTo(HttpMethod.Get));
            Assert.That(request.Url.ToString(), Is.EqualTo("https://api2.chaptarr.com/"));
            Assert.That(request.RequestTimeout, Is.EqualTo(TalearrMetadataServerProbe.ProbeTimeout));
            Assert.That(request.SuppressHttpError, Is.True);
            Assert.That(result.NextProbeAt, Is.Not.Null);
        }

        [Test]
        public void probe_keeps_the_configured_server_and_its_path()
        {
            var http = new ScriptedHttpClient().Respond(HttpStatusCode.NotFound);
            var probe = CreateProbe(http, new RecordingEventAggregator(), "http://mirror.local:8080/meta/?x=1");

            var result = probe.ProbeNow();

            Assert.That(http.Requests.Single().Url.ToString(), Is.EqualTo("http://mirror.local:8080/meta/"));
            Assert.That(result.State, Is.EqualTo(MetadataServerProbeState.Healthy), "a 404 still proves the server is answering");
            Assert.That(result.SourceName, Is.EqualTo("http://mirror.local:8080"));
        }

        [Test]
        public void a_single_failed_probe_is_confirmed_before_it_is_reported()
        {
            var http = new ScriptedHttpClient()
                .Respond(HttpStatusCode.OK)
                .Throw(new WebException("connect failed", WebExceptionStatus.ConnectFailure))
                .Throw(new WebException("connect failed", WebExceptionStatus.ConnectFailure));
            var events = new RecordingEventAggregator();
            var probe = CreateProbe(http, events);

            probe.ProbeNow();
            events.Events.Clear();

            var first = probe.ProbeNow();
            Assert.That(first.State, Is.EqualTo(MetadataServerProbeState.Healthy), "one failure may be a blip");
            Assert.That(first.PendingState, Is.EqualTo(MetadataServerProbeState.Down));
            Assert.That(TalearrMetadataServerProbe.GetNextDelay(first), Is.EqualTo(TalearrMetadataServerProbe.ConfirmDelay));
            Assert.That(events.Events, Is.Empty);

            var second = probe.ProbeNow();
            Assert.That(second.State, Is.EqualTo(MetadataServerProbeState.Down));
            Assert.That(second.PendingState, Is.Null);
            Assert.That(second.Error, Does.Contain("ConnectFailure"));
            Assert.That(events.Events.OfType<MetadataServerProbeStatusChangedEvent>().Single().Result.State,
                Is.EqualTo(MetadataServerProbeState.Down));
        }

        [Test]
        public void backoff_grows_while_down_and_resets_after_recovery()
        {
            var http = new ScriptedHttpClient();
            for (var i = 0; i < 8; i++)
            {
                http.Respond(HttpStatusCode.BadGateway);
            }

            http.Respond(HttpStatusCode.OK);

            var events = new RecordingEventAggregator();
            var probe = CreateProbe(http, events);
            var delays = new List<TimeSpan>();

            for (var i = 0; i < 8; i++)
            {
                delays.Add(TalearrMetadataServerProbe.GetNextDelay(probe.ProbeNow()));
            }

            Assert.That(delays[0], Is.EqualTo(TalearrMetadataServerProbe.ConfirmDelay));
            Assert.That(delays.Skip(1), Is.Ordered.Ascending);
            Assert.That(delays.Last(), Is.EqualTo(TalearrMetadataServerProbe.DownBackoff.Last()), "backoff is capped");

            var recovered = probe.ProbeNow();
            Assert.That(recovered.State, Is.EqualTo(MetadataServerProbeState.Healthy));
            Assert.That(recovered.ConsecutiveFailures, Is.Zero);
            Assert.That(TalearrMetadataServerProbe.GetNextDelay(recovered), Is.EqualTo(TalearrMetadataServerProbe.HealthyInterval));
            Assert.That(events.Events.OfType<MetadataServerProbeStatusChangedEvent>().Select(e => e.Result.State),
                Is.EqualTo(new[] { MetadataServerProbeState.Down, MetadataServerProbeState.Healthy }));
        }

        [TestCase(HttpStatusCode.OK, 100, MetadataServerProbeState.Healthy)]
        [TestCase(HttpStatusCode.OK, 6000, MetadataServerProbeState.Slow)]
        [TestCase(HttpStatusCode.NotFound, 100, MetadataServerProbeState.Healthy)]
        [TestCase(HttpStatusCode.TooManyRequests, 100, MetadataServerProbeState.RateLimited)]
        [TestCase(HttpStatusCode.ServiceUnavailable, 100, MetadataServerProbeState.Down)]
        [TestCase((HttpStatusCode)522, 100, MetadataServerProbeState.Down)]
        public void classifies_responses(HttpStatusCode statusCode, int elapsedMs, MetadataServerProbeState expected)
        {
            var request = new HttpRequest("https://api2.chaptarr.com/");
            var response = new HttpResponse(request, new HttpHeader(), "ok", statusCode);

            var result = TalearrMetadataServerProbe.Classify("https://api2.chaptarr.com", response, null, TimeSpan.FromMilliseconds(elapsedMs));

            Assert.That(result.State, Is.EqualTo(expected));
        }

        [Test]
        public void timeout_is_reported_as_down_with_a_readable_reason()
        {
            var result = TalearrMetadataServerProbe.Classify("https://api2.chaptarr.com", null, new TaskCanceledException(), TimeSpan.FromSeconds(10));

            Assert.That(result.State, Is.EqualTo(MetadataServerProbeState.Down));
            Assert.That(result.Error, Is.EqualTo("no response within 10s"));
        }

        [Test]
        public void slow_answers_are_retried_sooner_than_healthy_ones()
        {
            Assert.That(TalearrMetadataServerProbe.GetNextDelay(new MetadataServerProbeResult { State = MetadataServerProbeState.Slow }),
                Is.LessThan(TalearrMetadataServerProbe.HealthyInterval));
        }

        [Test]
        public void health_check_is_ok_until_the_probe_confirms_a_problem()
        {
            var http = new ScriptedHttpClient()
                .Respond(HttpStatusCode.OK)
                .Respond(HttpStatusCode.ServiceUnavailable)
                .Respond(HttpStatusCode.ServiceUnavailable);
            var probe = CreateProbe(http, new RecordingEventAggregator());
            var check = new TalearrMetadataServerProbeCheck(probe, null);

            probe.ProbeNow();
            Assert.That(check.Check().Type, Is.EqualTo(HealthCheckResult.Ok));

            probe.ProbeNow();
            Assert.That(check.Check().Type, Is.EqualTo(HealthCheckResult.Ok));

            probe.ProbeNow();
            var health = check.Check();
            Assert.That(health.Type, Is.EqualTo(HealthCheckResult.Error));
            Assert.That(health.Message, Does.Contain("https://api2.chaptarr.com is not responding (HTTP 503)"));
            Assert.That(health.Message, Does.Contain("Your authors and books are kept as they are"));
            Assert.That(health.Message, Does.Contain("Next check in"));
        }

        [Test]
        public void health_check_warns_when_the_server_is_slow()
        {
            var message = TalearrMetadataServerProbeCheck.GetMessage(new MetadataServerProbeResult
            {
                State = MetadataServerProbeState.Slow,
                SourceName = "https://api2.chaptarr.com",
                Latency = TimeSpan.FromSeconds(7.25)
            });

            Assert.That(message, Does.Contain("responding slowly (7.2s)").Or.Contain("responding slowly (7.3s)"));
            Assert.That(message, Does.Contain("may time out"));
        }

        [Test]
        public void health_check_and_probe_messages_use_the_TaleArr_spelling_and_no_em_dashes()
        {
            var down = TalearrMetadataServerProbeCheck.GetMessage(new MetadataServerProbeResult
            {
                State = MetadataServerProbeState.Down,
                SourceName = "https://api2.chaptarr.com",
                Error = "HTTP 502"
            });

            Assert.That(down, Does.Contain("TaleArr will check again shortly."));
            Assert.That(down, Does.Not.Contain(((char)0x2014).ToString()));
            Assert.That(down, Does.Not.Contain("Talearr "));
        }
    }
}
