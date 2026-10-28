using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;
using NLog;
using NzbDrone.Core.Books.Calibre;
using NzbDrone.Core.MediaFiles.Azw;
using UglyToad.PdfPig;
using UglyToad.PdfPig.Tokens;

namespace NzbDrone.Core.MediaFiles.TalearrImport
{
    public class FileLanguageEvidence
    {
        public FileLanguageEvidence(IEnumerable<string> languages, string source)
        {
            Languages = (languages ?? Enumerable.Empty<string>())
                .Where(language => !string.IsNullOrWhiteSpace(language))
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            Source = source;
        }

        public static FileLanguageEvidence None => new FileLanguageEvidence(null, null);

        // ISO 639-2/T codes, as Edition.Language stores them ("eng", "swe", "nld").
        public HashSet<string> Languages { get; }

        // Where the language came from, for the rejection message.
        public string Source { get; }

        public bool HasLanguage => Languages.Count > 0;
    }

    public interface IFileLanguageReader
    {
        /// <summary>
        /// Reads the language a book file declares about itself: EPUB OPF dc:language, MOBI/AZW3
        /// EXTH 524, PDF /Lang or XMP dc:language, or audio language tags from
        /// <paramref name="audioTags"/>. Returns <see cref="FileLanguageEvidence.None"/> when the
        /// file says nothing usable (missing, "und", "mul", or unreadable).
        /// </summary>
        FileLanguageEvidence Read(string path, IDictionary<string, List<string>> audioTags = null);
    }

    public class FileLanguageReader : IFileLanguageReader
    {
        // Codes that say "no particular language" and are never evidence either way.
        private static readonly HashSet<string> NonLanguageCodes = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "und", "mul", "zxx", "mis", "unk", "xx", "xxx", "none", "null", "unknown"
        };

        private static readonly Regex XmpLanguageRegex = new Regex(
            @"<dc:language>\s*<rdf:(?:Bag|Seq|Alt)>(?<items>.*?)</rdf:(?:Bag|Seq|Alt)>\s*</dc:language>",
            RegexOptions.Singleline | RegexOptions.IgnoreCase | RegexOptions.Compiled);

        private static readonly Regex XmpItemRegex = new Regex(@"<rdf:li[^>]*>(?<value>[^<]+)</rdf:li>", RegexOptions.IgnoreCase | RegexOptions.Compiled);

        private const int MaxPdfScanBytes = 4 * 1024 * 1024;

        private readonly Logger _logger;

        public FileLanguageReader(Logger logger)
        {
            _logger = logger;
        }

        public FileLanguageEvidence Read(string path, IDictionary<string, List<string>> audioTags = null)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                return FileLanguageEvidence.None;
            }

            var extension = Path.GetExtension(path).ToLowerInvariant();

            try
            {
                switch (extension)
                {
                    case ".epub":
                    case ".kepub":
                        return new FileLanguageEvidence(ReadEpubLanguages(path), "EPUB dc:language");
                    case ".mobi":
                    case ".azw":
                    case ".azw3":
                        return new FileLanguageEvidence(ReadMobiLanguages(path), "MOBI/AZW3 EXTH language");
                    case ".pdf":
                        return new FileLanguageEvidence(ReadPdfLanguages(path), "PDF metadata");
                }

                if (MediaFileExtensions.AudioExtensions.Contains(extension))
                {
                    return new FileLanguageEvidence(ReadAudioTagLanguages(audioTags), "audio language tag");
                }
            }
            catch (Exception ex)
            {
                _logger.Debug(ex, "Could not read the language of {0}", path);
            }

            return FileLanguageEvidence.None;
        }

        /// <summary>
        /// Turns a raw language value ("en-US", "sv", "Swedish", "dut", "eng; fre") into ISO 639-2/T
        /// codes, dropping anything that is not a real language.
        /// </summary>
        public static IEnumerable<string> Canonicalize(string raw)
        {
            if (string.IsNullOrWhiteSpace(raw))
            {
                yield break;
            }

            foreach (var part in raw.Split(new[] { ';', ',', '/', '|' }, StringSplitOptions.RemoveEmptyEntries))
            {
                var token = part.Trim().Trim('"', '\'');
                if (token.Length == 0 || NonLanguageCodes.Contains(token))
                {
                    continue;
                }

                var canonical = token.CanonicalizeLanguage();
                if (!string.IsNullOrWhiteSpace(canonical) && !NonLanguageCodes.Contains(canonical))
                {
                    yield return canonical;
                }
            }
        }

        public static IEnumerable<string> ReadEpubLanguages(string path)
        {
            using var zip = ZipFile.OpenRead(path);
            return ReadEpubLanguages(zip);
        }

        public static List<string> ReadEpubLanguages(ZipArchive zip)
        {
            var opfEntry = FindOpfEntry(zip);
            if (opfEntry == null)
            {
                return new List<string>();
            }

            XDocument opf;
            using (var stream = opfEntry.Open())
            {
                opf = LoadXmlSafely(stream);
            }

            // Only the package metadata counts: dc:language elsewhere (a guide, a nav) is not the
            // book's declared language.
            var metadata = opf.Descendants().FirstOrDefault(e => e.Name.LocalName == "metadata");
            var languageElements = (metadata ?? opf.Root)?
                .Descendants()
                .Where(e => e.Name.LocalName == "language")
                .Select(e => e.Value)
                ?? Enumerable.Empty<string>();

            return languageElements.SelectMany(Canonicalize).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        }

        private static ZipArchiveEntry FindOpfEntry(ZipArchive zip)
        {
            var container = zip.GetEntry("META-INF/container.xml") ??
                            zip.Entries.FirstOrDefault(e => e.FullName.Equals("META-INF/container.xml", StringComparison.OrdinalIgnoreCase));

            if (container != null)
            {
                try
                {
                    XDocument containerXml;
                    using (var stream = container.Open())
                    {
                        containerXml = LoadXmlSafely(stream);
                    }

                    var rootFile = containerXml.Descendants()
                        .Where(e => e.Name.LocalName == "rootfile")
                        .Select(e => (string)e.Attribute("full-path"))
                        .FirstOrDefault(p => !string.IsNullOrWhiteSpace(p));

                    if (rootFile != null)
                    {
                        var entry = zip.GetEntry(rootFile) ??
                                    zip.Entries.FirstOrDefault(e => e.FullName.Equals(rootFile, StringComparison.OrdinalIgnoreCase));
                        if (entry != null)
                        {
                            return entry;
                        }
                    }
                }
                catch (XmlException)
                {
                    // Fall through to the first .opf in the archive.
                }
            }

            return zip.Entries.FirstOrDefault(e => e.FullName.EndsWith(".opf", StringComparison.OrdinalIgnoreCase));
        }

        private static XDocument LoadXmlSafely(Stream stream)
        {
            var settings = new XmlReaderSettings
            {
                DtdProcessing = DtdProcessing.Ignore,
                XmlResolver = null,
                MaxCharactersInDocument = 16 * 1024 * 1024
            };

            using var reader = XmlReader.Create(stream, settings);
            return XDocument.Load(reader);
        }

        public static IEnumerable<string> ReadMobiLanguages(string path)
        {
            var book = new Azw3File(path);
            return Canonicalize(book.Language).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        }

        public static List<string> ReadPdfLanguages(string path)
        {
            var languages = new List<string>();

            try
            {
                using var document = PdfDocument.Open(path, new ParsingOptions { UseLenientParsing = true });
                var catalog = document.Structure.Catalog.CatalogDictionary;
                if (catalog.TryGet(NameToken.Create("Lang"), out var token))
                {
                    var language = token switch
                    {
                        StringToken text => text.Data,
                        HexToken hex => hex.Data,
                        _ => null
                    };

                    languages.AddRange(Canonicalize(language));
                }
            }
            catch (Exception)
            {
                // Encrypted or damaged PDFs can still carry readable XMP below.
            }

            if (languages.Count == 0)
            {
                languages.AddRange(ReadXmpLanguages(path));
            }

            return languages.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        }

        private static IEnumerable<string> ReadXmpLanguages(string path)
        {
            // XMP packets are plain XML inside the PDF and usually near the start or the end.
            var info = new FileInfo(path);
            var chunks = new List<byte[]>();

            using (var stream = File.OpenRead(path))
            {
                var headLength = (int)Math.Min(MaxPdfScanBytes, info.Length);
                var head = new byte[headLength];
                stream.ReadExactly(head, 0, headLength);
                chunks.Add(head);

                if (info.Length > MaxPdfScanBytes)
                {
                    var tailLength = (int)Math.Min(MaxPdfScanBytes, info.Length - headLength);
                    var tail = new byte[tailLength];
                    stream.Seek(-tailLength, SeekOrigin.End);
                    stream.ReadExactly(tail, 0, tailLength);
                    chunks.Add(tail);
                }
            }

            foreach (var chunk in chunks)
            {
                var text = Encoding.Latin1.GetString(chunk);
                foreach (Match match in XmpLanguageRegex.Matches(text))
                {
                    foreach (Match item in XmpItemRegex.Matches(match.Groups["items"].Value))
                    {
                        foreach (var code in Canonicalize(item.Groups["value"].Value))
                        {
                            yield return code;
                        }
                    }
                }
            }
        }

        /// <summary>
        /// Language values from tags already read by the import (ID3 TLAN, MP4/Vorbis/APE/ASF
        /// LANGUAGE, iTunes freeform LANGUAGE).
        /// </summary>
        public static List<string> ReadAudioTagLanguages(IDictionary<string, List<string>> tags)
        {
            if (tags == null || tags.Count == 0)
            {
                return new List<string>();
            }

            return tags
                .Where(tag => IsLanguageTagKey(tag.Key))
                .SelectMany(tag => tag.Value ?? new List<string>())
                .SelectMany(Canonicalize)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        private static bool IsLanguageTagKey(string key)
        {
            if (string.IsNullOrWhiteSpace(key))
            {
                return false;
            }

            return key.Equals("LANGUAGE", StringComparison.OrdinalIgnoreCase) ||
                   key.Equals("ID3v2:TLAN", StringComparison.OrdinalIgnoreCase) ||
                   key.EndsWith(":LANGUAGE", StringComparison.OrdinalIgnoreCase) ||
                   key.EndsWith("/Language", StringComparison.OrdinalIgnoreCase);
        }
    }
}
