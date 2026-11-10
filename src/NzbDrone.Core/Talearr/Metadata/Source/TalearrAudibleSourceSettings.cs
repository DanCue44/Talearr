using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using NLog;
using NzbDrone.Core.Books;
using NzbDrone.Core.Books.Events;
using NzbDrone.Core.Configuration;
using NzbDrone.Core.Messaging.Events;

namespace NzbDrone.Core.Talearr.Metadata.Source
{
    // Talearr: where an author's audiobook details come from (docs/TALEARR_METADATA_SOURCE.md,
    // "Audible").
    public enum TalearrAudiobookSource
    {
        // Hardcover, with Audible filling what Hardcover lacks (when the fill is on).
        Hardcover = 0,

        // Audible leads: its editions, series and books Hardcover does not list for the author.
        Audible = 1
    }

    public static class TalearrAudiobookSources
    {
        public const string Hardcover = "hardcover";
        public const string Audible = "audible";

        public static string ToApi(TalearrAudiobookSource source) => source == TalearrAudiobookSource.Audible ? Audible : Hardcover;

        public static bool TryParse(string value, out TalearrAudiobookSource source)
        {
            var normalized = (value ?? string.Empty).Trim();
            if (string.Equals(normalized, Audible, StringComparison.OrdinalIgnoreCase))
            {
                source = TalearrAudiobookSource.Audible;
                return true;
            }

            source = TalearrAudiobookSource.Hardcover;
            return string.Equals(normalized, Hardcover, StringComparison.OrdinalIgnoreCase);
        }
    }

    public interface ITalearrAudibleSourceSettings
    {
        // Audible fills Hardcover's gaps for every author. On unless turned off.
        bool FillEnabled { get; }
        void SetFillEnabled(bool enabled);

        // By the author's Hardcover id ("hc:123"). Hardcover unless the author was set to Audible.
        TalearrAudiobookSource GetAuthorSource(string foreignAuthorId);

        // False when nothing changed.
        bool SetAuthorSource(string foreignAuthorId, TalearrAudiobookSource source);

        IReadOnlyCollection<string> GetAudibleAuthors();

        // Audiobooks added from an Audible search result for an author on Hardcover (ASINs). The
        // Audible step keeps them in the author's payload even when Audible does not lead.
        IReadOnlyCollection<string> GetKeptBooks(string foreignAuthorId);
        void KeepBook(string foreignAuthorId, string asin);
        void ForgetBook(string asin);
    }

    // Talearr: Config rows that stock Chaptarr never reads: "talearr.metadata.audiblefill" =
    // "false" when the fill is off, and one "talearr.audiobooksource.<provider id>" = "audible"
    // row per author set to Audible, and one "talearr.audiblebook.<provider id>.<ASIN>" = "keep"
    // row per audiobook added from an Audible search result. A rollback ignores them.
    public class TalearrAudibleSourceSettings : ITalearrAudibleSourceSettings, IHandle<AuthorDeletedEvent>, IHandle<BookDeletedEvent>
    {
        public const string FillKey = "talearr.metadata.audiblefill";
        public const string AuthorKeyPrefix = "talearr.audiobooksource.";
        public const string KeptBookKeyPrefix = "talearr.audiblebook.";
        public const string Keep = "keep";

        private static readonly Regex AsinIdRegex = new Regex(@"^az:(?<asin>[A-Z0-9]{10})(?:-|$)", RegexOptions.Compiled | RegexOptions.IgnoreCase);

        private readonly IConfigRepository _repository;
        private readonly Logger _logger;
        private readonly object _lock = new object();
        private bool? _fill;
        private HashSet<string> _audibleAuthors;
        private Dictionary<string, HashSet<string>> _keptBooks;

        public TalearrAudibleSourceSettings(IConfigRepository repository, Logger logger)
        {
            _repository = repository;
            _logger = logger;
        }

        public static string AuthorKey(string foreignAuthorId) => AuthorKeyPrefix + Normalize(foreignAuthorId);

        public bool FillEnabled
        {
            get
            {
                lock (_lock)
                {
                    if (!_fill.HasValue)
                    {
                        try
                        {
                            _fill = !string.Equals(_repository.Get(FillKey)?.Value?.Trim(), "false", StringComparison.OrdinalIgnoreCase);
                        }
                        catch (Exception ex)
                        {
                            _logger.Debug(ex, "Unable to read Talearr setting {0}, using the default", FillKey);
                            return true;
                        }
                    }

                    return _fill.Value;
                }
            }
        }

        public void SetFillEnabled(bool enabled)
        {
            lock (_lock)
            {
                _repository.Upsert(FillKey, enabled ? "true" : "false");
                _fill = enabled;
            }

            _logger.Info("Talearr: Audible fill for audiobooks turned {0}", enabled ? "on" : "off");
        }

        public TalearrAudiobookSource GetAuthorSource(string foreignAuthorId)
        {
            var key = Normalize(foreignAuthorId);
            return key.Length > 0 && LoadAudibleAuthors().Contains(key) ? TalearrAudiobookSource.Audible : TalearrAudiobookSource.Hardcover;
        }

        public bool SetAuthorSource(string foreignAuthorId, TalearrAudiobookSource source)
        {
            var key = Normalize(foreignAuthorId);
            if (key.Length == 0 || GetAuthorSource(key) == source)
            {
                return false;
            }

            lock (_lock)
            {
                if (source == TalearrAudiobookSource.Audible)
                {
                    _repository.Upsert(AuthorKeyPrefix + key, TalearrAudiobookSources.Audible);
                }
                else
                {
                    var row = _repository.Get(AuthorKeyPrefix + key);
                    if (row != null)
                    {
                        _repository.Delete(row);
                    }
                }

                _audibleAuthors = null;
            }

            _logger.Info("Talearr: audiobook details for author {0} now come from {1}", key, TalearrAudiobookSources.ToApi(source));
            return true;
        }

        public IReadOnlyCollection<string> GetAudibleAuthors()
        {
            return LoadAudibleAuthors().ToList();
        }

        public IReadOnlyCollection<string> GetKeptBooks(string foreignAuthorId)
        {
            var key = Normalize(foreignAuthorId);
            return key.Length > 0 && LoadKeptBooks().TryGetValue(key, out var asins) ? asins.ToList() : new List<string>();
        }

        public void KeepBook(string foreignAuthorId, string asin)
        {
            var key = Normalize(foreignAuthorId);
            var normalizedAsin = (asin ?? string.Empty).Trim().ToUpperInvariant();
            if (key.Length == 0 || normalizedAsin.Length == 0 || GetKeptBooks(key).Contains(normalizedAsin))
            {
                return;
            }

            lock (_lock)
            {
                _repository.Upsert($"{KeptBookKeyPrefix}{key}.{normalizedAsin}", Keep);
                _keptBooks = null;
            }

            _logger.Info("Talearr: keeping Audible audiobook {0} for author {1}", normalizedAsin, key);
        }

        public void ForgetBook(string asin)
        {
            var normalizedAsin = (asin ?? string.Empty).Trim().ToUpperInvariant();
            if (normalizedAsin.Length == 0)
            {
                return;
            }

            lock (_lock)
            {
                foreach (var row in (_repository.All() ?? Enumerable.Empty<Config>())
                             .Where(r => r?.Key != null &&
                                         r.Key.StartsWith(KeptBookKeyPrefix, StringComparison.OrdinalIgnoreCase) &&
                                         r.Key.EndsWith("." + normalizedAsin, StringComparison.OrdinalIgnoreCase))
                             .ToList())
                {
                    _repository.Delete(row);
                    _keptBooks = null;
                }
            }
        }

        public void Handle(BookDeletedEvent message)
        {
            foreach (var book in (message?.DeletedBooks ?? new List<Book>()).Append(message?.Book).Where(b => b != null))
            {
                try
                {
                    foreach (var asin in KeptAsins(book))
                    {
                        ForgetBook(asin);
                    }
                }
                catch (Exception ex)
                {
                    _logger.Debug(ex, "Talearr: unable to forget the Audible audiobook of deleted book {0}", book.Title);
                }
            }
        }

        // The ASIN of a book added from Audible (az:{ASIN}), from its own ids.
        public static IEnumerable<string> KeptAsins(Book book)
        {
            return new[] { book?.BaseBookId, book?.ForeignEditionId }
                .Concat(book?.RemoteProviderIds ?? Enumerable.Empty<string>())
                .Select(id => AsinIdRegex.Match(id ?? string.Empty))
                .Where(m => m.Success)
                .Select(m => m.Groups["asin"].Value.ToUpperInvariant())
                .Distinct();
        }

        public void Handle(AuthorDeletedEvent message)
        {
            var foreignAuthorId = message?.Author?.HardcoverAuthorId;
            if (!string.IsNullOrWhiteSpace(foreignAuthorId))
            {
                try
                {
                    SetAuthorSource(foreignAuthorId, TalearrAudiobookSource.Hardcover);
                    foreach (var asin in GetKeptBooks(foreignAuthorId))
                    {
                        ForgetBook(asin);
                    }
                }
                catch (Exception ex)
                {
                    _logger.Debug(ex, "Talearr: unable to clear the audiobook source of deleted author {0}", foreignAuthorId);
                }
            }
        }

        private static string Normalize(string foreignAuthorId)
        {
            return (foreignAuthorId ?? string.Empty).Trim().ToLowerInvariant();
        }

        private Dictionary<string, HashSet<string>> LoadKeptBooks()
        {
            lock (_lock)
            {
                if (_keptBooks != null)
                {
                    return _keptBooks;
                }

                var kept = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
                try
                {
                    foreach (var row in _repository.All() ?? Enumerable.Empty<Config>())
                    {
                        if (row?.Key == null ||
                            !row.Key.StartsWith(KeptBookKeyPrefix, StringComparison.OrdinalIgnoreCase) ||
                            !string.Equals(row.Value?.Trim(), Keep, StringComparison.OrdinalIgnoreCase))
                        {
                            continue;
                        }

                        // "<provider id>.<ASIN>"; the provider id ("hc:123") has no dot.
                        var rest = row.Key.Substring(KeptBookKeyPrefix.Length);
                        var dot = rest.LastIndexOf('.');
                        if (dot <= 0 || dot == rest.Length - 1)
                        {
                            continue;
                        }

                        var author = rest.Substring(0, dot).Trim().ToLowerInvariant();
                        if (!kept.TryGetValue(author, out var asins))
                        {
                            asins = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                            kept[author] = asins;
                        }

                        asins.Add(rest.Substring(dot + 1).Trim().ToUpperInvariant());
                    }
                }
                catch (Exception ex)
                {
                    _logger.Debug(ex, "Talearr: unable to read the kept Audible audiobooks");
                    return kept;
                }

                _keptBooks = kept;
                return kept;
            }
        }

        private HashSet<string> LoadAudibleAuthors()
        {
            lock (_lock)
            {
                if (_audibleAuthors != null)
                {
                    return _audibleAuthors;
                }

                var keys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                try
                {
                    foreach (var row in _repository.All() ?? Enumerable.Empty<Config>())
                    {
                        if (row?.Key != null &&
                            row.Key.StartsWith(AuthorKeyPrefix, StringComparison.OrdinalIgnoreCase) &&
                            string.Equals(row.Value?.Trim(), TalearrAudiobookSources.Audible, StringComparison.OrdinalIgnoreCase))
                        {
                            keys.Add(row.Key.Substring(AuthorKeyPrefix.Length).Trim().ToLowerInvariant());
                        }
                    }
                }
                catch (Exception ex)
                {
                    _logger.Debug(ex, "Talearr: unable to read the authors set to Audible");
                    return keys;
                }

                _audibleAuthors = keys;
                return keys;
            }
        }
    }
}
