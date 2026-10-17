using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using NLog;
using NzbDrone.Common.Http;
using NzbDrone.Common.Messaging;
using NzbDrone.Core.Exceptions;
using NzbDrone.Core.Messaging.Events;
using NzbDrone.Core.MetadataSource;
using NzbDrone.Core.MetadataSource.BookInfo;
using NzbDrone.Core.MetadataSource.BookInfo.V5;

namespace NzbDrone.Core.Talearr.MetadataFallback
{
    // TaleArr: remembers which search results and fetched authors came from the fallback source,
    // without adding a property (and so a column) to Author or Book.
    public static class TalearrMetadataFallbackMarker
    {
        private static readonly ConditionalWeakTable<object, string> Sources = new ConditionalWeakTable<object, string>();

        public static void Mark(object entity, string source)
        {
            if (entity == null || string.IsNullOrWhiteSpace(source))
            {
                return;
            }

            Sources.AddOrUpdate(entity, source);
        }

        public static string GetSource(object entity)
        {
            return entity != null && Sources.TryGetValue(entity, out var source) ? source : null;
        }
    }

    public class TalearrMetadataFallbackUsedEvent : IEvent
    {
        public string Operation { get; }

        public TalearrMetadataFallbackUsedEvent(string operation)
        {
            Operation = operation;
        }
    }

    public class TalearrMetadataFallbackStatus
    {
        public TalearrMetadataFallbackMode Mode { get; set; }
        public string PrimarySource { get; set; }
        public bool PrimaryUnavailable { get; set; }

        // The source search and add use right now: the metadata server, or Open Library.
        public string ActiveSource { get; set; }
        public DateTime? LastUsed { get; set; }
        public string LastOperation { get; set; }
        public int UsesSinceStart { get; set; }
        public string LastError { get; set; }
    }

    public interface ITalearrMetadataFallback
    {
        TalearrMetadataFallbackMode Mode { get; }
        bool IsEnabled { get; }
        bool ShowSource { get; }
        bool IsPrimaryUnavailable();
        bool ShouldUseForAdd(string requestedProviderId, Exception primaryError);
        bool ShouldUseForSearch(Exception searchError);
        V5AuthorResponse GetAuthor(string requestedProviderId, string authorNameHint, IReadOnlyDictionary<string, string> requestedWorkTitles = null);
        List<object> Search(string term);
        TalearrMetadataFallbackStatus GetStatus();
    }

    /// <summary>
    /// TaleArr: a second, keyless metadata source (Open Library) so TaleArr survives an outage of
    /// the Chaptarr metadata server.
    ///
    /// What uses it: search (when the metadata server is down, or the search provider failed
    /// with a server or network error) and add (when fetching the author from the metadata
    /// server failed that way). Refresh never uses it: during an outage a refresh is skipped and
    /// existing data is kept (TalearrMetadataServerProbe / RefreshResult.Unavailable), so sparser
    /// Open Library data can never replace what is stored.
    ///
    /// Identity: Open Library ids go into the existing OpenLibrary* columns as ol:OL...A/W/M.
    /// The requested hc:/gr: author id is kept on an author added during an outage, and an author
    /// found through Open Library carries its Goodreads/Amazon ids from Open Library, so the next
    /// refresh after recovery is a normal metadata server refresh. Its books are attached to the
    /// server's books by TalearrFallbackReconciler (ISBN, then exact title), so nothing is
    /// duplicated. Nothing is stored outside the existing tables; state here is in memory only.
    /// </summary>
    public class TalearrMetadataFallbackService : ITalearrMetadataFallback
    {
        private static readonly TimeSpan EventInterval = TimeSpan.FromMinutes(1);

        private readonly ITalearrMetadataFallbackSettingsService _settings;
        private readonly IOpenLibraryClient _openLibrary;
        private readonly ITalearrMetadataServerProbe _probe;
        private readonly IMetadataServerHealthGate _gate;
        private readonly IEventAggregator _eventAggregator;
        private readonly Logger _logger;
        private readonly object _lock = new object();

        private DateTime? _lastUsed;
        private string _lastOperation;
        private string _lastError;
        private int _uses;
        private DateTime _lastEventAt = DateTime.MinValue;

        public TalearrMetadataFallbackService(ITalearrMetadataFallbackSettingsService settings,
                                              IOpenLibraryClient openLibrary,
                                              ITalearrMetadataServerProbe probe,
                                              IMetadataServerHealthGate gate,
                                              IEventAggregator eventAggregator,
                                              Logger logger)
        {
            _settings = settings;
            _openLibrary = openLibrary;
            _probe = probe;
            _gate = gate;
            _eventAggregator = eventAggregator;
            _logger = logger;
        }

        // For tests.
        internal Func<DateTime> UtcNow { get; set; } = () => DateTime.UtcNow;

        public TalearrMetadataFallbackMode Mode
        {
            get
            {
                try
                {
                    return _settings?.GetMode() ?? TalearrMetadataFallbackSettingsService.DefaultMode;
                }
                catch (Exception)
                {
                    return TalearrMetadataFallbackSettingsService.DefaultMode;
                }
            }
        }

        public bool IsEnabled => Mode != TalearrMetadataFallbackMode.Off;

        public bool ShowSource => Mode == TalearrMetadataFallbackMode.WhenPrimaryDownShowSource;

        public bool IsPrimaryUnavailable()
        {
            try
            {
                if (_probe?.LastResult?.State == MetadataServerProbeState.Down)
                {
                    return true;
                }

                return _gate != null && !_gate.CanAttemptWithoutProbe(out _);
            }
            catch (Exception ex)
            {
                _logger.Debug(ex, "Unable to read the metadata server state");
                return false;
            }
        }

        public bool ShouldUseForAdd(string requestedProviderId, Exception primaryError)
        {
            if (!IsEnabled || primaryError == null)
            {
                return false;
            }

            // The metadata server does not index Open Library ids, so it answers "not found"
            // (or 202, mapped to not found) for an author that came from an Open Library search.
            // For those ids Open Library is the source of record.
            if (IsOpenLibraryId(requestedProviderId) && FindInChain<AuthorNotFoundException>(primaryError) != null &&
                FindInChain<AuthorTerminalException>(primaryError) == null)
            {
                return true;
            }

            if (IsLegitimateAnswer(primaryError))
            {
                return false;
            }

            return IsPrimaryUnavailable() || IsServerOrNetworkError(primaryError);
        }

        public bool ShouldUseForSearch(Exception searchError)
        {
            if (!IsEnabled)
            {
                return false;
            }

            return IsPrimaryUnavailable() || (searchError != null && IsServerOrNetworkError(searchError));
        }

        public V5AuthorResponse GetAuthor(string requestedProviderId, string authorNameHint, IReadOnlyDictionary<string, string> requestedWorkTitles = null)
        {
            try
            {
                OpenLibraryAuthorRecord record;
                if (IsOpenLibraryId(requestedProviderId))
                {
                    record = _openLibrary.GetAuthor(ProviderIdHelper.StripPrefix(requestedProviderId));
                }
                else
                {
                    record = FindAuthorByName(authorNameHint);
                }

                if (record == null)
                {
                    _logger.Warn("TaleArr: the metadata server is unavailable and Open Library has no author matching {0} ('{1}'); nothing was added",
                        requestedProviderId,
                        authorNameHint ?? "no name");
                    RecordFailure("add", "no matching author on Open Library");
                    return null;
                }

                var works = _openLibrary.GetAuthorWorks(record.Key, 100);
                var response = TalearrOpenLibraryMapper.BuildAuthorResponse(record, works, requestedProviderId, requestedWorkTitles);

                _logger.Info("TaleArr: the metadata server is unavailable; added author '{0}' ({1}) from Open Library with {2} book(s). A refresh after the server recovers updates it from the metadata server.",
                    record.Name,
                    requestedProviderId,
                    response?.Books?.Count ?? 0);

                RecordUse("add", $"author {requestedProviderId}");
                return response;
            }
            catch (OpenLibraryException ex)
            {
                _logger.Warn("TaleArr: Open Library fallback failed for {0}: {1}", requestedProviderId, ex.Message);
                RecordFailure("add", ex.Message);
                return null;
            }
        }

        public List<object> Search(string term)
        {
            var results = new List<object>();
            if (string.IsNullOrWhiteSpace(term))
            {
                return results;
            }

            try
            {
                var authors = _openLibrary.SearchAuthors(term, 5)
                    .Where(a => a.WorkCount > 0)
                    .Select(TalearrOpenLibraryMapper.MapSearchAuthor)
                    .Where(a => a != null)
                    .ToList();

                var books = _openLibrary.SearchWorks(term, 15)
                    .Select(TalearrOpenLibraryMapper.MapSearchBook)
                    .Where(b => b != null)
                    .ToList();

                results.AddRange(authors);
                results.AddRange(books);

                _logger.Info("TaleArr: searched Open Library for '{0}' because the metadata server or search provider is unavailable ({1} author(s), {2} book(s))",
                    term,
                    authors.Count,
                    books.Count);

                RecordUse("search", term);
                return results;
            }
            catch (OpenLibraryException ex)
            {
                _logger.Warn("TaleArr: Open Library fallback search failed for '{0}': {1}", term, ex.Message);
                RecordFailure("search", ex.Message);
                throw;
            }
        }

        public TalearrMetadataFallbackStatus GetStatus()
        {
            var primaryUnavailable = IsPrimaryUnavailable();
            var mode = Mode;

            lock (_lock)
            {
                return new TalearrMetadataFallbackStatus
                {
                    Mode = mode,
                    PrimarySource = SafeSourceName(),
                    PrimaryUnavailable = primaryUnavailable,
                    ActiveSource = primaryUnavailable && mode != TalearrMetadataFallbackMode.Off
                        ? TalearrOpenLibraryMapper.SourceName
                        : "Metadata server",
                    LastUsed = _lastUsed,
                    LastOperation = _lastOperation,
                    UsesSinceStart = _uses,
                    LastError = _lastError
                };
            }
        }

        public static bool IsOpenLibraryId(string providerId)
        {
            return !string.IsNullOrWhiteSpace(providerId) &&
                   providerId.Trim().StartsWith("ol:", StringComparison.OrdinalIgnoreCase);
        }

        // A real answer from the metadata server (not found, terminal, invalid id) is never a
        // reason to use the fallback.
        public static bool IsLegitimateAnswer(Exception error)
        {
            return FindInChain<AuthorNotFoundException>(error) != null ||
                   FindInChain<AuthorTerminalException>(error) != null ||
                   FindInChain<BookNotFoundException>(error) != null ||
                   FindInChain<InvalidProviderIdException>(error) != null ||
                   FindInChain<FluentValidation.ValidationException>(error) != null;
        }

        public static bool IsServerOrNetworkError(Exception error)
        {
            if (error == null || IsLegitimateAnswer(error))
            {
                return false;
            }

            foreach (var ex in Chain(error))
            {
                switch (ex)
                {
                    case TooManyRequestsException:
                        return true;
                    case HttpException http when http.Response != null:
                        if (IsTransientStatus(http.Response.StatusCode))
                        {
                            return true;
                        }

                        break;
                    case WebException web:
                        if (web.Status is WebExceptionStatus.ConnectFailure or
                            WebExceptionStatus.ConnectionClosed or
                            WebExceptionStatus.KeepAliveFailure or
                            WebExceptionStatus.NameResolutionFailure or
                            WebExceptionStatus.ReceiveFailure or
                            WebExceptionStatus.SecureChannelFailure or
                            WebExceptionStatus.Timeout or
                            WebExceptionStatus.TrustFailure)
                        {
                            return true;
                        }

                        break;
                    case HttpRequestException:
                    case TimeoutException:
                    case TaskCanceledException:
                        return true;
                    // BookInfoException is always a 503 client exception, so only its message
                    // (or its inner exception, checked next) tells an outage apart.
                    case BookInfoException bookInfo:
                        if (IsTransientBookInfoMessage(bookInfo.Message))
                        {
                            return true;
                        }

                        break;
                    case NzbDroneClientException client when IsTransientStatus(client.StatusCode):
                        return true;
                }
            }

            return false;
        }

        private static bool IsTransientBookInfoMessage(string message)
        {
            if (string.IsNullOrWhiteSpace(message))
            {
                return false;
            }

            if (message.IndexOf("temporarily unavailable", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return true;
            }

            const string prefix = "V5 API error: ";
            var index = message.IndexOf(prefix, StringComparison.Ordinal);
            if (index < 0)
            {
                return false;
            }

            var statusText = message.Substring(index + prefix.Length).Trim();
            return Enum.TryParse<HttpStatusCode>(statusText, true, out var status) && IsTransientStatus(status);
        }

        private static bool IsTransientStatus(HttpStatusCode statusCode)
        {
            var status = (int)statusCode;
            return statusCode == HttpStatusCode.RequestTimeout ||
                   statusCode == HttpStatusCode.TooManyRequests ||
                   (status >= 500 && status <= 599);
        }

        private static IEnumerable<Exception> Chain(Exception error)
        {
            var pending = new Stack<Exception>();
            pending.Push(error);
            var depth = 0;

            while (pending.Count > 0 && depth++ < 32)
            {
                var current = pending.Pop();
                if (current == null)
                {
                    continue;
                }

                yield return current;

                if (current is AggregateException aggregate)
                {
                    foreach (var inner in aggregate.InnerExceptions)
                    {
                        pending.Push(inner);
                    }
                }
                else if (current.InnerException != null)
                {
                    pending.Push(current.InnerException);
                }
            }
        }

        private static T FindInChain<T>(Exception error)
            where T : Exception
        {
            return error == null ? null : Chain(error).OfType<T>().FirstOrDefault();
        }

        private OpenLibraryAuthorRecord FindAuthorByName(string name)
        {
            var wanted = TalearrOpenLibraryMapper.NormalizeName(name);
            if (string.IsNullOrWhiteSpace(wanted))
            {
                return null;
            }

            // Exact name only, never a guess: the most prolific author of that exact name.
            var match = _openLibrary.SearchAuthors(name, 10)
                .Where(a => string.Equals(TalearrOpenLibraryMapper.NormalizeName(a.Name), wanted, StringComparison.Ordinal))
                .OrderByDescending(a => a.WorkCount)
                .FirstOrDefault();

            return match == null ? null : _openLibrary.GetAuthor(match.Key);
        }

        private void RecordUse(string operation, string detail)
        {
            bool publish;
            lock (_lock)
            {
                var now = UtcNow();
                _lastUsed = now;
                _lastOperation = $"{operation}: {detail}";
                _lastError = null;
                _uses++;
                publish = now - _lastEventAt >= EventInterval;
                if (publish)
                {
                    _lastEventAt = now;
                }
            }

            if (publish)
            {
                try
                {
                    _eventAggregator?.PublishEvent(new TalearrMetadataFallbackUsedEvent(operation));
                }
                catch (Exception ex)
                {
                    _logger.Debug(ex, "Unable to publish the metadata fallback event");
                }
            }
        }

        private void RecordFailure(string operation, string message)
        {
            lock (_lock)
            {
                _lastError = $"{operation}: {message}";
            }
        }

        private string SafeSourceName()
        {
            try
            {
                return _gate?.SourceName;
            }
            catch (Exception)
            {
                return null;
            }
        }
    }
}
