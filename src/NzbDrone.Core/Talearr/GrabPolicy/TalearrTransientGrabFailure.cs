using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Threading.Tasks;
using NzbDrone.Common.Http;
using NzbDrone.Core.Download.Clients;
using NzbDrone.Core.Exceptions;

namespace NzbDrone.Core.Talearr.GrabPolicy
{
    public enum TalearrTransientGrabKind
    {
        RateLimited = 0,
        ServerError = 1,
        Timeout = 2,
        Network = 3
    }

    // TaleArr: a grab that failed for a reason that is likely to go away on its own (the indexer
    // or Prowlarr said 429, answered 5xx, timed out, or the connection dropped). Anything else
    // (release gone, blocklisted, rejected by the client, bad torrent) is not transient and keeps
    // upstream's handling.
    public sealed class TalearrTransientGrabFailure
    {
        public TalearrTransientGrabKind Kind { get; private set; }

        // From the Retry-After header of a 429, when there was one.
        public TimeSpan? RetryAfter { get; private set; }

        public int? StatusCode { get; private set; }

        public string Reason
        {
            get
            {
                switch (Kind)
                {
                    case TalearrTransientGrabKind.RateLimited:
                        return RetryAfter.HasValue
                            ? string.Format("HTTP 429 Too Many Requests, Retry-After {0}s", (int)Math.Ceiling(RetryAfter.Value.TotalSeconds))
                            : "HTTP 429 Too Many Requests";
                    case TalearrTransientGrabKind.ServerError:
                        return string.Format("HTTP {0} server error", StatusCode);
                    case TalearrTransientGrabKind.Timeout:
                        return "timed out";
                    default:
                        return "network error";
                }
            }
        }

        // Walks the exception chain (ReleaseDownloadException -> TooManyRequestsException, and so on).
        public static TalearrTransientGrabFailure Classify(Exception exception)
        {
            if (exception == null || IsPermanent(exception))
            {
                return null;
            }

            var depth = 0;
            for (var current = exception; current != null && depth < 10; current = current.InnerException, depth++)
            {
                if (current is AggregateException aggregate && aggregate.InnerExceptions.Count == 1)
                {
                    current = aggregate.InnerExceptions[0];
                }

                var classified = ClassifyOne(current);
                if (classified != null)
                {
                    return classified;
                }
            }

            return null;
        }

        private static bool IsPermanent(Exception exception)
        {
            // Upstream already handles these: gone, blocklisted, refused by the client, or the
            // download client itself is down (stored as pending by upstream).
            return exception is ReleaseUnavailableException ||
                   exception is ReleaseBlockedException ||
                   exception is DownloadClientRejectedReleaseException ||
                   exception is DownloadClientUnavailableException ||
                   exception is DownloadClientAuthenticationException;
        }

        private static TalearrTransientGrabFailure ClassifyOne(Exception exception)
        {
            switch (exception)
            {
                case TooManyRequestsException tooMany:
                    return new TalearrTransientGrabFailure
                    {
                        Kind = TalearrTransientGrabKind.RateLimited,
                        StatusCode = 429,
                        RetryAfter = tooMany.RetryAfter > TimeSpan.Zero ? tooMany.RetryAfter : (TimeSpan?)null
                    };

                case HttpException http when http.Response != null:
                    var status = (int)http.Response.StatusCode;
                    if (status == 429)
                    {
                        return new TalearrTransientGrabFailure { Kind = TalearrTransientGrabKind.RateLimited, StatusCode = 429 };
                    }

                    if (status == 408 || status == 504)
                    {
                        return new TalearrTransientGrabFailure { Kind = TalearrTransientGrabKind.Timeout, StatusCode = status };
                    }

                    if (status >= 500 && status <= 599)
                    {
                        return new TalearrTransientGrabFailure { Kind = TalearrTransientGrabKind.ServerError, StatusCode = status };
                    }

                    return null;

                case TimeoutException:
                case TaskCanceledException:
                    return new TalearrTransientGrabFailure { Kind = TalearrTransientGrabKind.Timeout };

                case WebException web:
                    if (web.Status == WebExceptionStatus.Timeout)
                    {
                        return new TalearrTransientGrabFailure { Kind = TalearrTransientGrabKind.Timeout };
                    }

                    if (web.Status == WebExceptionStatus.ConnectFailure ||
                        web.Status == WebExceptionStatus.ConnectionClosed ||
                        web.Status == WebExceptionStatus.ReceiveFailure ||
                        web.Status == WebExceptionStatus.SendFailure ||
                        web.Status == WebExceptionStatus.KeepAliveFailure ||
                        web.Status == WebExceptionStatus.NameResolutionFailure ||
                        web.Status == WebExceptionStatus.PipelineFailure)
                    {
                        return new TalearrTransientGrabFailure { Kind = TalearrTransientGrabKind.Network };
                    }

                    return null;

                case HttpRequestException:
                case SocketException:
                case IOException:
                    return new TalearrTransientGrabFailure { Kind = TalearrTransientGrabKind.Network };

                default:
                    return null;
            }
        }
    }
}
