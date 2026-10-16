using System;
using System.Collections.Generic;
using System.Linq;
using NzbDrone.Common.Extensions;
using NzbDrone.Core.Books;
using NzbDrone.Core.CustomFormats;
using NzbDrone.Core.MediaFiles;
using NzbDrone.Core.Talearr.Audiobook;
using NzbDrone.Core.Talearr.Recordings;

namespace NzbDrone.Core.Organizer
{
    // TaleArr-only half of FileNameBuilder (kept apart so upstream merges stay small).
    public partial class FileNameBuilder
    {
        // Copy-folder disambiguation (docs/TALEARR_RECORDINGS_DESIGN.md, "Folder collisions between
        // copies"). Only an additional recording's book folder can change; the primary row, ebooks,
        // flat layouts and books with a single recording come back untouched.
        private List<string> TalearrDisambiguateRecordingFolder(Author author, Edition edition, BookFile bookFile, NamingConfig namingConfig, List<CustomFormat> customFormats, List<string> components)
        {
            if (_recordingFolders == null || components == null || components.Count < 2 || edition == null)
            {
                return components;
            }

            RecordingFolderContext context;
            try
            {
                context = _recordingFolders.Value.GetContext(edition);
            }
            catch (Exception ex)
            {
                _logger.Debug(ex, "TaleArr: recordings unavailable, book folder left as the naming format gives it");
                return components;
            }

            if (context == null)
            {
                return components;
            }

            var formats = customFormats ?? new List<CustomFormat>();
            var taken = new List<(string Folder, RecordingFolderKey Key)>();
            foreach (var row in context.Earlier.OrderBy(r => r.Ordinal))
            {
                var rowFile = row.SampleFile ?? bookFile;
                var rowComponents = TalearrBuildComponents(author, row.Edition, rowFile, namingConfig, formats);
                if (rowComponents.Count < 2)
                {
                    continue;
                }

                var rowKey = TalearrRecordingFolderKey(author, row.Edition, rowFile, namingConfig, formats, row.Asin, row.Ordinal);
                var rowFolder = row.IsPrimary
                    ? FolderOf(rowComponents)
                    : TalearrRecordingFolders.Resolve(FolderOf(rowComponents), rowKey, taken, Array.Empty<(string, RecordingFolderKey)>());
                taken.Add((rowFolder, rowKey));
            }

            var occupied = context.Others
                .SelectMany(row => row.FileFolders.Select(folder => (Folder: folder, Key: TalearrRecordingFolderKey(author, row.Edition, row.SampleFile ?? bookFile, namingConfig, formats, row.Asin, row.Ordinal))))
                .ToList();

            var selfKey = TalearrRecordingFolderKey(author, edition, bookFile, namingConfig, formats, context.Asin, context.Ordinal);
            var folder = FolderOf(components);
            var resolved = TalearrRecordingFolders.Resolve(folder, selfKey, taken, occupied);
            if (string.Equals(resolved, folder, StringComparison.Ordinal))
            {
                return components;
            }

            // Only the book folder (the last folder component) changes.
            var suffix = resolved.Substring(folder.Length);
            var result = components.ToList();
            result[^2] = CleanFolderName(result[^2] + suffix);
            _logger.Debug("TaleArr: recording book row {0} shares its folder with another recording; using '{1}'", context.BookId, result[^2]);
            return result;
        }

        private List<string> TalearrBuildComponents(Author author, Edition edition, BookFile bookFile, NamingConfig namingConfig, List<CustomFormat> customFormats)
        {
            var tokenHandlers = TalearrTokenHandlers(author, edition, bookFile, customFormats);
            var components = new List<string>();

            foreach (var part in namingConfig.StandardBookFormat.Split(new[] { '\\', '/' }, StringSplitOptions.RemoveEmptyEntries))
            {
                var component = ReplacePartTokens(part, tokenHandlers, namingConfig).Trim();
                component = ReplaceTokens(component, tokenHandlers, namingConfig).Trim();
                component = FileNameCleanupRegex.Replace(component, match => match.Captures[0].Value[0].ToString());
                component = TrimSeparatorsRegex.Replace(component, string.Empty);

                if (component.IsNotNullOrWhiteSpace())
                {
                    components.Add(component);
                }
            }

            return components;
        }

        // Narrator, Abridged/Unabridged and edition year through the existing naming tokens, so the
        // disambiguator reads the way the rest of the path does.
        private RecordingFolderKey TalearrRecordingFolderKey(Author author, Edition edition, BookFile bookFile, NamingConfig namingConfig, List<CustomFormat> customFormats, string asin, int ordinal)
        {
            var tokenHandlers = TalearrTokenHandlers(author, edition, bookFile, customFormats);
            var year = ReplaceTokens("{Edition Year}", tokenHandlers, namingConfig);

            return new RecordingFolderKey
            {
                Narrator = ReplaceTokens("{Narrator}", tokenHandlers, namingConfig),
                Abridgement = ReplaceTokens("{Abridgement}", tokenHandlers, namingConfig),
                EditionYear = string.Equals(year, "Unknown", StringComparison.OrdinalIgnoreCase) ? null : year,
                Asin = asin.IsNullOrWhiteSpace() ? null : CleanFileName(asin, namingConfig),
                Ordinal = ordinal
            };
        }

        private Dictionary<string, Func<TokenMatch, string>> TalearrTokenHandlers(Author author, Edition edition, BookFile bookFile, List<CustomFormat> customFormats)
        {
            var tokenHandlers = new Dictionary<string, Func<TokenMatch, string>>(FileNameBuilderTokenEqualityComparer.Instance);

            AddAuthorTokens(tokenHandlers, author);
            AddBookTokens(tokenHandlers, edition);
            AddBookFileTokens(tokenHandlers, bookFile);
            AddQualityTokens(tokenHandlers, author, bookFile);
            AddMediaInfoTokens(tokenHandlers, bookFile);
            AddCustomFormats(tokenHandlers, author, bookFile, customFormats);
            AddNarratorTokens(tokenHandlers, edition, bookFile);
            TalearrNamingTokens.Add(tokenHandlers, edition, _abridgedResolver);
            AddGraphicAudioTokens(tokenHandlers, edition, bookFile);

            return tokenHandlers;
        }

        private static string FolderOf(List<string> components)
        {
            return string.Join("/", components.Take(components.Count - 1));
        }
    }
}
