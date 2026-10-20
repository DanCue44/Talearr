using System;
using System.Diagnostics;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using NLog;
using NzbDrone.Common.Http;
using NzbDrone.Common.Messaging;
using NzbDrone.Core.Configuration;
using NzbDrone.Core.Lifecycle;
using NzbDrone.Core.Messaging.Events;

namespace NzbDrone.Core.MetadataSource
{
    // Talearr: active health probe for the metadata server.
    //
    // Chaptarr's MetadataServerHealthService only learns about an outage when a real
    // request fails, and its health check runs every 6 hours. This probe sends one small
    // GET to the root of the configured metadata server (the same server, it never picks
    // another one) on a timer, with a short timeout, and backs off while the server is
    // down. System > Status shows the result through TalearrMetadataServerProbeCheck.
    //
    // No settings and no database: state is in memory and starts fresh on every launch.
    public enum MetadataServerProbeState
    {
        Unknown,
        Healthy,
        Slow,
        RateLimited,
        Down
    }

    public class MetadataServerProbeResult
    {
        public MetadataServerProbeState State { get; set; } = MetadataServerProbeState.Unknown;
        public string SourceName { get; set; }
        public DateTime? CheckedAt { get; set; }
        public TimeSpan? Latency { get; set; }
        public HttpStatusCode? StatusCode { get; set; }
        public string Error { get; set; }
        public int ConsecutiveFailures { get; set; }
        public DateTime? NextProbeAt { get; set; }

        // Set while a new problem waits for a confirming probe; State still shows the last
        // confirmed state.
        public MetadataServerProbeState? PendingState { get; set; }

        public bool IsProblem => State is MetadataServerProbeState.Slow or MetadataServerProbeState.RateLimited or MetadataServerProbeState.Down;
    }

    public class MetadataServerProbeStatusChangedEvent : IEvent
    {
        public MetadataServerProbeResult Result { get; }

        public MetadataServerProbeStatusChangedEvent(MetadataServerProbeResult result)
        {
            Result = result;
        }
    }

    public interface ITalearrMetadataServerProbe
    {
        MetadataServerProbeResult LastResult { get; }
        MetadataServerProbeResult ProbeNow();
    }

    public class TalearrMetadataServerProbe : ITalearrMetadataServerProbe,
                                              IHandle<ApplicationStartedEvent>,
                                              IHandle<ApplicationShutdownRequested>
    {
        public static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(10);
        public static readonly TimeSpan SlowThreshold = TimeSpan.FromSeconds(4);
        public static readonly TimeSpan InitialDelay = TimeSpan.FromSeconds(30);
        public static readonly TimeSpan HealthyInterval = TimeSpan.FromMinutes(15);
        public static readonly TimeSpan SlowInterval = TimeSpan.FromMinutes(5);
        public static readonly TimeSpan ConfirmDelay = TimeSpan.FromSeconds(30);
        public const int ConfirmProbes = 2;

        // Backoff while the server is down, by consecutive failed probes.
        public static readonly TimeSpan[] DownBackoff =
        {
            TimeSpan.FromMinutes(1),
            TimeSpan.FromMinutes(2),
            TimeSpan.FromMinutes(5),
            TimeSpan.FromMinutes(10),
            TimeSpan.FromMinutes(15),
            TimeSpan.FromMinutes(30)
        };

        private readonly IHttpClient _httpClient;
        private readonly IConfigService _configService;
        private readonly IEventAggregator _eventAggregator;
        private readonly Logger _logger;
        private readonly object _lock = new object();

        private MetadataServerProbeResult _lastResult = new MetadataServerProbeResult();
        private Timer _timer;
        private int _probing;
        private MetadataServerProbeState _rawState = MetadataServerProbeState.Unknown;
        private int _rawStreak;
        private bool _stopped;

        public TalearrMetadataServerProbe(IHttpClient httpClient,
                                          IConfigService configService,
                                          IEventAggregator eventAggregator,
                                          Logger logger)
        {
            _httpClient = httpClient;
            _configService = configService;
            _eventAggregator = eventAggregator;
            _logger = logger;
        }

        // For tests: replaces the clock used for timestamps and the next probe time.
        internal Func<DateTime> UtcNow { get; set; } = () => DateTime.UtcNow;

        public MetadataServerProbeResult LastResult
        {
            get
            {
                lock (_lock)
                {
                    return _lastResult;
                }
            }
        }

        public void Handle(ApplicationStartedEvent message)
        {
            lock (_lock)
            {
                if (_stopped || _timer != null)
                {
                    return;
                }

                _timer = new Timer(_ => OnTimer(), null, InitialDelay, Timeout.InfiniteTimeSpan);
            }
        }

        public void Handle(ApplicationShutdownRequested message)
        {
            lock (_lock)
            {
                _stopped = true;
                _timer?.Dispose();
                _timer = null;
            }
        }

        private void OnTimer()
        {
            MetadataServerProbeResult result;

            try
            {
                result = ProbeNow();
            }
            catch (Exception ex)
            {
                _logger.Debug(ex, "Metadata server probe failed unexpectedly");
                result = LastResult;
            }

            lock (_lock)
            {
                if (_stopped || _timer == null)
                {
                    return;
                }

                var delay = GetNextDelay(result);
                _timer.Change(delay, Timeout.InfiniteTimeSpan);
            }
        }

        public MetadataServerProbeResult ProbeNow()
        {
            if (Interlocked.Exchange(ref _probing, 1) == 1)
            {
                return LastResult;
            }

            try
            {
                var baseUrl = GetBaseUrl(_configService.MetadataServerUrl);
                var sourceName = GetSourceName(_configService.MetadataServerUrl);
                MetadataServerProbeResult result;

                if (baseUrl == null)
                {
                    result = new MetadataServerProbeResult
                    {
                        State = MetadataServerProbeState.Unknown,
                        SourceName = sourceName,
                        Error = "Metadata server URL is not set or not valid"
                    };
                }
                else
                {
                    result = SendProbe(baseUrl, sourceName);
                }

                return Record(result);
            }
            finally
            {
                Interlocked.Exchange(ref _probing, 0);
            }
        }

        private MetadataServerProbeResult SendProbe(string baseUrl, string sourceName)
        {
            var request = new HttpRequestBuilder(baseUrl + "/")
                .SetHeader("User-Agent", "Chaptarr/2.0")
                .Build();

            request.RequestTimeout = ProbeTimeout;
            request.SuppressHttpError = true;
            request.LogHttpError = false;
            request.AllowAutoRedirect = false;

            var stopwatch = Stopwatch.StartNew();
            HttpResponse response = null;
            Exception error = null;

            try
            {
                response = _httpClient.Execute(request);
            }
            catch (Exception ex)
            {
                error = ex;
            }

            stopwatch.Stop();

            return Classify(sourceName, response, error, stopwatch.Elapsed);
        }

        public static MetadataServerProbeResult Classify(string sourceName, HttpResponse response, Exception error, TimeSpan elapsed)
        {
            var result = new MetadataServerProbeResult
            {
                SourceName = sourceName,
                Latency = elapsed
            };

            if (error is TooManyRequestsException)
            {
                result.State = MetadataServerProbeState.RateLimited;
                result.StatusCode = HttpStatusCode.TooManyRequests;
                result.Error = "rate limited (HTTP 429)";
                return result;
            }

            if (error is HttpException httpException && httpException.Response != null)
            {
                response = httpException.Response;
                error = null;
            }

            if (error != null)
            {
                result.State = MetadataServerProbeState.Down;
                result.Latency = null;
                result.Error = DescribeException(error, elapsed);
                return result;
            }

            if (response == null)
            {
                result.State = MetadataServerProbeState.Down;
                result.Latency = null;
                result.Error = "no response";
                return result;
            }

            result.StatusCode = response.StatusCode;
            var status = (int)response.StatusCode;

            if (response.StatusCode == HttpStatusCode.TooManyRequests)
            {
                result.State = MetadataServerProbeState.RateLimited;
                result.Error = "rate limited (HTTP 429)";
                return result;
            }

            if (status >= 500)
            {
                result.State = MetadataServerProbeState.Down;
                result.Error = $"HTTP {status}";
                return result;
            }

            // Any other answer (200, a redirect, even a 404 from a mirror without a root page)
            // means the server is up and answering.
            result.State = elapsed > SlowThreshold ? MetadataServerProbeState.Slow : MetadataServerProbeState.Healthy;
            return result;
        }

        public static TimeSpan GetNextDelay(MetadataServerProbeResult result)
        {
            if (result?.PendingState != null)
            {
                return ConfirmDelay;
            }

            switch (result?.State)
            {
                case MetadataServerProbeState.Down:
                    var index = Math.Clamp(result.ConsecutiveFailures, 1, DownBackoff.Length) - 1;
                    return DownBackoff[index];
                case MetadataServerProbeState.Slow:
                case MetadataServerProbeState.RateLimited:
                    return SlowInterval;
                default:
                    return HealthyInterval;
            }
        }

        private MetadataServerProbeResult Record(MetadataServerProbeResult result)
        {
            MetadataServerProbeResult previous;
            var now = UtcNow();
            var pending = false;

            lock (_lock)
            {
                previous = _lastResult;

                if (result.State == _rawState)
                {
                    _rawStreak++;
                }
                else
                {
                    _rawState = result.State;
                    _rawStreak = 1;
                }

                result.CheckedAt = now;
                result.ConsecutiveFailures = result.State == MetadataServerProbeState.Down ? _rawStreak : 0;

                // One bad probe can be a blip. Confirm a new problem with a second probe soon
                // after before warning, so a single slow answer does not raise (and notify) an issue.
                if (result.IsProblem && previous.State != result.State && _rawStreak < ConfirmProbes)
                {
                    pending = true;
                    result = new MetadataServerProbeResult
                    {
                        State = previous.State,
                        SourceName = result.SourceName,
                        CheckedAt = now,
                        Latency = previous.Latency,
                        StatusCode = previous.StatusCode,
                        Error = previous.Error,
                        ConsecutiveFailures = result.ConsecutiveFailures,
                        PendingState = result.State
                    };
                }

                result.NextProbeAt = now + GetNextDelay(result);
                _lastResult = result;
            }

            if (pending)
            {
                _logger.Debug("Metadata server {0} probe returned {1}; confirming in {2}",
                              result.SourceName,
                              result.PendingState,
                              MetadataServerHealthGate.FormatRetryAfter(ConfirmDelay));
                return result;
            }

            LogTransition(previous, result);

            if (previous.State != result.State)
            {
                _eventAggregator?.PublishEvent(new MetadataServerProbeStatusChangedEvent(result));
            }

            return result;
        }

        private void LogTransition(MetadataServerProbeResult previous, MetadataServerProbeResult current)
        {
            if (previous.State == current.State)
            {
                _logger.Debug("Metadata server {0} probe: {1}{2}", current.SourceName, current.State, FormatDetail(current));
                return;
            }

            switch (current.State)
            {
                case MetadataServerProbeState.Down:
                    _logger.Warn("Metadata server {0} is not responding ({1}). Existing authors and books are kept; refreshes and new adds will fail until it recovers. Next check in {2}.",
                                 current.SourceName,
                                 current.Error,
                                 MetadataServerHealthGate.FormatRetryAfter(GetNextDelay(current)));
                    break;
                case MetadataServerProbeState.Slow:
                    _logger.Warn("Metadata server {0} is responding slowly ({1:0.0}s). Searches and refreshes may time out.",
                                 current.SourceName,
                                 current.Latency?.TotalSeconds ?? 0);
                    break;
                case MetadataServerProbeState.RateLimited:
                    _logger.Warn("Metadata server {0} is rate limiting requests", current.SourceName);
                    break;
                case MetadataServerProbeState.Healthy:
                    if (previous.State != MetadataServerProbeState.Unknown)
                    {
                        _logger.Info("Metadata server {0} is responding normally again", current.SourceName);
                    }
                    else
                    {
                        _logger.Debug("Metadata server {0} probe: Healthy{1}", current.SourceName, FormatDetail(current));
                    }

                    break;
                default:
                    _logger.Debug("Metadata server {0} probe: {1}{2}", current.SourceName, current.State, FormatDetail(current));
                    break;
            }
        }

        private static string FormatDetail(MetadataServerProbeResult result)
        {
            if (result.Error != null)
            {
                return $" ({result.Error})";
            }

            return result.Latency.HasValue ? $" ({result.Latency.Value.TotalMilliseconds:0} ms)" : string.Empty;
        }

        private static string DescribeException(Exception error, TimeSpan elapsed)
        {
            if (error is TaskCanceledException or OperationCanceledException or TimeoutException ||
                (error is WebException { Status: WebExceptionStatus.Timeout }))
            {
                return $"no response within {ProbeTimeout.TotalSeconds:0}s";
            }

            if (error is WebException webException && webException.Status != WebExceptionStatus.UnknownError)
            {
                return $"{webException.Status}: {webException.Message}";
            }

            if (error is HttpRequestException && error.InnerException != null)
            {
                return error.InnerException.Message;
            }

            return error.Message;
        }

        public static string GetBaseUrl(string metadataServerUrl)
        {
            var trimmed = metadataServerUrl?.Trim().TrimEnd('/');
            if (string.IsNullOrWhiteSpace(trimmed))
            {
                return null;
            }

            if (!Uri.TryCreate(trimmed, UriKind.Absolute, out var uri) ||
                (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps) ||
                string.IsNullOrWhiteSpace(uri.Host))
            {
                return null;
            }

            // Keep any path prefix (a mirror served under /something), drop query and fragment.
            return uri.GetLeftPart(UriPartial.Path).TrimEnd('/');
        }

        // Same naming as MetadataServerHealthGate.SourceName, so both report the same server.
        public static string GetSourceName(string metadataServerUrl)
        {
            var trimmed = metadataServerUrl?.Trim().TrimEnd('/');
            if (!string.IsNullOrWhiteSpace(trimmed) && Uri.TryCreate(trimmed, UriKind.Absolute, out var uri))
            {
                return uri.GetLeftPart(UriPartial.Authority);
            }

            return "Chaptarr Metadata Server";
        }
    }
}
