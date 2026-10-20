using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using NLog;
using NzbDrone.Core.Books;
using NzbDrone.Core.Books.Services;
using NzbDrone.Core.Configuration;
using NzbDrone.Core.DecisionEngine;
using NzbDrone.Core.Download;
using NzbDrone.Core.MediaFiles.BookImport;
using NzbDrone.Core.Parser.Model;
using NzbDrone.Core.Profiles.Metadata;

namespace NzbDrone.Core.MediaFiles.TalearrImport
{
    public interface IDownloadedFileLanguageVerifier
    {
        /// <summary>
        /// Rejects files of a completed download whose own language contradicts the wanted
        /// language. Every decision is checked, including ones already rejected for another
        /// reason, so a foreign release that also failed to match is still recognised as foreign.
        /// </summary>
        void Verify(List<ImportDecision<LocalBook>> decisions, RemoteBook remoteBook, DownloadClientItem downloadClientItem);

        /// <summary>
        /// Talearr: the language mismatch sentence for one matched file, or null. Used by manual
        /// import as a warning, never as a rejection.
        /// </summary>
        string DescribeMismatch(LocalBook localBook);
    }

    public class DownloadedFileLanguageVerifier : IDownloadedFileLanguageVerifier
    {
        public const string RejectionCategory = "Language";

        private static readonly HashSet<string> NonLanguageCodes = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "und", "mul", "zxx", "mis"
        };

        private static readonly Dictionary<string, string> LanguageNames = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["eng"] = "English",
            ["deu"] = "German",
            ["fra"] = "French",
            ["spa"] = "Spanish",
            ["ita"] = "Italian",
            ["nld"] = "Dutch",
            ["swe"] = "Swedish",
            ["dan"] = "Danish",
            ["nor"] = "Norwegian",
            ["nob"] = "Norwegian",
            ["nno"] = "Norwegian",
            ["fin"] = "Finnish",
            ["isl"] = "Icelandic",
            ["pol"] = "Polish",
            ["rus"] = "Russian",
            ["por"] = "Portuguese",
            ["jpn"] = "Japanese",
            ["zho"] = "Chinese",
            ["kor"] = "Korean",
            ["hun"] = "Hungarian",
            ["ces"] = "Czech",
            ["slk"] = "Slovak",
            ["tur"] = "Turkish",
            ["ell"] = "Greek",
            ["heb"] = "Hebrew",
            ["ara"] = "Arabic",
            ["hin"] = "Hindi",
            ["ron"] = "Romanian",
            ["cat"] = "Catalan",
            ["ukr"] = "Ukrainian",
            ["hrv"] = "Croatian",
            ["srp"] = "Serbian",
            ["slv"] = "Slovenian",
            ["bul"] = "Bulgarian",
            ["vie"] = "Vietnamese",
            ["ind"] = "Indonesian"
        };

        private readonly IFileLanguageReader _fileLanguageReader;
        private readonly IMetadataProfileService _metadataProfileService;
        private readonly IEditionService _editionService;
        private readonly IConfigService _configService;
        private readonly Logger _logger;

        public DownloadedFileLanguageVerifier(IFileLanguageReader fileLanguageReader,
                                              IMetadataProfileService metadataProfileService,
                                              IEditionService editionService,
                                              IConfigService configService,
                                              Logger logger)
        {
            _fileLanguageReader = fileLanguageReader;
            _metadataProfileService = metadataProfileService;
            _editionService = editionService;
            _configService = configService;
            _logger = logger;
        }

        public void Verify(List<ImportDecision<LocalBook>> decisions, RemoteBook remoteBook, DownloadClientItem downloadClientItem)
        {
            // Only completed downloads: a manual import is the user's own call.
            if (downloadClientItem == null || decisions == null || decisions.Count == 0 || !IsEnabled())
            {
                return;
            }

            var wantedCache = new Dictionary<int, WantedLanguages>();
            var releaseNames = GetReleaseNames(remoteBook, downloadClientItem);

            foreach (var decision in decisions)
            {
                var localBook = decision?.Item;
                if (localBook == null || decision.Rejections?.Any(r => r.IsLanguageFilter) == true)
                {
                    continue;
                }

                var mismatch = FindMismatch(localBook, remoteBook, releaseNames, wantedCache);
                if (mismatch == null)
                {
                    continue;
                }

                var reason = mismatch + " Talearr rejected this release so another one can be tried.";

                _logger.Info("Rejecting {0} from {1}: {2}", Path.GetFileName(localBook.Path), downloadClientItem.Title, reason);

                decision.Reject(new Rejection(reason, RejectionType.Permanent, canBypass: false, category: RejectionCategory, severity: 3));
            }
        }

        public string DescribeMismatch(LocalBook localBook)
        {
            // Talearr: manual import only warns; importing a translation on purpose is the user's call.
            if (localBook == null || !IsEnabled())
            {
                return null;
            }

            try
            {
                return FindMismatch(localBook, null, new List<string>(), new Dictionary<int, WantedLanguages>());
            }
            catch (Exception ex)
            {
                _logger.Debug(ex, "Language check failed for {0}", localBook.Path);
                return null;
            }
        }

        // The "File language is X (from Y), but Z is wanted (W)." sentence, or null when the file
        // is in a wanted language, says nothing about its language, or has no book to compare with.
        private string FindMismatch(LocalBook localBook, RemoteBook remoteBook, List<string> releaseNames, Dictionary<int, WantedLanguages> wantedCache)
        {
            if (string.IsNullOrWhiteSpace(localBook.Path) ||
                !MediaFileExtensions.AllExtensions.Contains(Path.GetExtension(localBook.Path)))
            {
                return null;
            }

            var targetBook = ResolveTargetBook(localBook, remoteBook);
            if (targetBook == null)
            {
                return null;
            }

            if (!wantedCache.TryGetValue(targetBook.Id, out var wanted))
            {
                wanted = ResolveWantedLanguages(targetBook, localBook, remoteBook);
                wantedCache[targetBook.Id] = wanted;
            }

            if (wanted.Languages.Count == 0)
            {
                return null;
            }

            var evidence = ReadEvidence(localBook, targetBook, remoteBook, releaseNames);
            if (!evidence.HasLanguage || evidence.Languages.Overlaps(wanted.Languages))
            {
                _logger.Trace("Language check passed for {0}: file [{1}], wanted [{2}]",
                    localBook.Path,
                    string.Join(", ", evidence.Languages),
                    string.Join(", ", wanted.Languages));
                return null;
            }

            return $"File language is {FormatLanguages(evidence.Languages)} (from {evidence.Source}), " +
                   $"but {FormatLanguages(wanted.Languages)} is wanted ({wanted.Source}).";
        }

        public static string FormatLanguages(IEnumerable<string> codes)
        {
            var labels = codes
                .OrderBy(code => code, StringComparer.OrdinalIgnoreCase)
                .Select(code => LanguageNames.TryGetValue(code, out var name) ? $"{name} ({code})" : code)
                .ToList();

            return labels.Count switch
            {
                0 => "unknown",
                1 => labels[0],
                _ => string.Join(" or ", labels)
            };
        }

        private FileLanguageEvidence ReadEvidence(LocalBook localBook, Book targetBook, RemoteBook remoteBook, List<string> releaseNames)
        {
            var fromFile = _fileLanguageReader.Read(localBook.Path, localBook.RawTags?.AllTags);
            if (fromFile.HasLanguage)
            {
                return fromFile;
            }

            // The file says nothing: fall back to explicit language words in the release and file
            // names, ignoring words that are part of the book's own title or author.
            var names = new List<string>(releaseNames)
            {
                localBook.SceneName,
                Path.GetFileNameWithoutExtension(localBook.Path)
            };

            var detected = ReleaseNameLanguageDetector.Detect(names, GetProtectedNames(targetBook, localBook, remoteBook));
            return new FileLanguageEvidence(detected, "release name");
        }

        private static List<string> GetReleaseNames(RemoteBook remoteBook, DownloadClientItem downloadClientItem)
        {
            return new[] { remoteBook?.Release?.Title, downloadClientItem?.Title, remoteBook?.ParsedBookInfo?.ReleaseTitle }
                .Where(name => !string.IsNullOrWhiteSpace(name))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        private IEnumerable<string> GetProtectedNames(Book targetBook, LocalBook localBook, RemoteBook remoteBook)
        {
            var names = new List<string>
            {
                targetBook?.Title,
                targetBook?.OriginalTitle,
                targetBook?.Subtitle,
                targetBook?.Author?.Name,
                localBook?.Book?.Title,
                localBook?.Author?.Name,
                remoteBook?.Author?.Name,
                remoteBook?.ParsedBookInfo?.BookTitle,
                remoteBook?.ParsedBookInfo?.AuthorName,
                localBook?.Narrator
            };

            foreach (var edition in GetEditions(targetBook))
            {
                names.Add(edition?.Title);
                names.Add(edition?.Subtitle);
            }

            return names.Where(name => !string.IsNullOrWhiteSpace(name));
        }

        private static Book ResolveTargetBook(LocalBook localBook, RemoteBook remoteBook)
        {
            // The grabbed book is what the user asked for. A file may have matched a different
            // book (or nothing) precisely because it is a translation.
            var grabbed = remoteBook?.Books?
                .Where(book => book != null && book.Id > 0)
                .ToList() ?? new List<Book>();

            if (localBook.Book != null && grabbed.Any(book => book.Id == localBook.Book.Id))
            {
                return grabbed.First(book => book.Id == localBook.Book.Id);
            }

            if (grabbed.Count == 1)
            {
                return grabbed[0];
            }

            return localBook.Book?.Id > 0 ? localBook.Book : null;
        }

        private WantedLanguages ResolveWantedLanguages(Book book, LocalBook localBook, RemoteBook remoteBook)
        {
            // 1. Languages the metadata profile allows, when the user set any.
            var profile = GetMetadataProfile(book, localBook, remoteBook);
            if (profile != null)
            {
                EditionMetadataProfileFilter.ParseAllowedLanguages(
                    profile.AllowedLanguages,
                    out var allowedLanguages,
                    out _,
                    out var configured,
                    out _);

                if (configured && allowedLanguages.Count > 0)
                {
                    return new WantedLanguages(allowedLanguages, $"metadata profile '{profile.Name}'");
                }
            }

            // 2. The languages of the book's own editions: the monitored one(s), plus every edition
            //    published under the book's own title. Edition metadata is not always right (an
            //    English "Animal Farm" can carry "urd"), so a single monitored edition is not
            //    trusted on its own; translations normally carry a translated title and stay out.
            var editions = GetEditions(book);
            var bookTitle = NormalizeTitle(book.Title);
            var editionLanguages = editions
                .Where(edition => edition != null &&
                                  (edition.Monitored || (bookTitle.Length > 0 && NormalizeTitle(edition.Title) == bookTitle)))
                .Select(edition => NormalizeLanguage(edition.Language))
                .Where(language => language != null)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            if (editionLanguages.Count > 0)
            {
                return new WantedLanguages(editionLanguages, $"editions of '{book.Title}'");
            }

            // 3. The language of the edition the file matched, as a last resort.
            var matched = NormalizeLanguage(localBook.Edition?.Language);
            if (matched != null && localBook.Book?.Id == book.Id)
            {
                return new WantedLanguages(new[] { matched }, "matched edition");
            }

            return new WantedLanguages(null, null);
        }

        private MetadataProfile GetMetadataProfile(Book book, LocalBook localBook, RemoteBook remoteBook)
        {
            try
            {
                var author = book.Author ?? remoteBook?.Author ?? localBook.Author;
                if (author == null)
                {
                    return null;
                }

                var isEbook = book.MediaType == BookMediaType.Ebook;
                var loaded = isEbook ? author.EbookMetadataProfile?.Value : author.AudiobookMetadataProfile?.Value;
                if (loaded != null)
                {
                    return loaded;
                }

                var profileId = isEbook ? author.EbookMetadataProfileId : author.AudiobookMetadataProfileId;
                if (!profileId.HasValue || profileId.Value <= 0 || !_metadataProfileService.Exists(profileId.Value))
                {
                    return null;
                }

                return _metadataProfileService.Get(profileId.Value);
            }
            catch (Exception ex)
            {
                _logger.Debug(ex, "Could not load the metadata profile for book {0}", book.Id);
                return null;
            }
        }

        private List<Edition> GetEditions(Book book)
        {
            if (book == null)
            {
                return new List<Edition>();
            }

            try
            {
                var editions = book.Editions;
                if (editions != null && editions.Count > 0)
                {
                    return editions;
                }

                return book.Id > 0 ? _editionService.GetEditionsByBook(book.Id) ?? new List<Edition>() : new List<Edition>();
            }
            catch (Exception ex)
            {
                _logger.Debug(ex, "Could not load editions for book {0}", book.Id);
                return new List<Edition>();
            }
        }

        private static string NormalizeTitle(string title)
        {
            return string.IsNullOrWhiteSpace(title)
                ? string.Empty
                : new string(title.Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray());
        }

        private static string NormalizeLanguage(string language)
        {
            var canonical = FileLanguageReader.Canonicalize(language).FirstOrDefault();
            return canonical == null || NonLanguageCodes.Contains(canonical) ? null : canonical;
        }

        private bool IsEnabled()
        {
            try
            {
                return _configService?.TalearrVerifyFileLanguage ?? true;
            }
            catch (Exception ex)
            {
                _logger.Debug(ex, "Cannot read TalearrVerifyFileLanguage, using the default");
                return true;
            }
        }

        private sealed class WantedLanguages
        {
            public WantedLanguages(IEnumerable<string> languages, string source)
            {
                Languages = (languages ?? Enumerable.Empty<string>())
                    .Select(NormalizeLanguage)
                    .Where(language => language != null)
                    .ToHashSet(StringComparer.OrdinalIgnoreCase);
                Source = source;
            }

            public HashSet<string> Languages { get; }
            public string Source { get; }
        }
    }
}
