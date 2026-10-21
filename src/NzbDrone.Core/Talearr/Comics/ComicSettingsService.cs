using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace NzbDrone.Core.Talearr.Comics
{
    public class ComicSettings
    {
        // Quality: which kinds of release are allowed. Digital is always preferred over a scan of
        // a printed book, and a scan over a c2c scan; these only switch a kind off entirely.
        public bool AllowDigital { get; set; } = true;
        public bool AllowScan { get; set; } = true;
        public bool AllowC2C { get; set; } = true;

        // Rank CBZ above CBR, CB7 and PDF when everything else is equal.
        public bool PreferCbz { get; set; } = true;

        // Accept a release that holds several volumes (v01-v10) when it covers a wanted volume.
        public bool AllowPacks { get; set; } = true;

        // A single volume smaller than this is almost always one chapter, not a volume.
        public int MinimumVolumeSizeMb { get; set; } = 20;

        // Newznab / Torznab categories searched for comics. 7030 is Books > Comics.
        public List<int> Categories { get; set; } = new List<int> { 7030 };

        public bool AutomaticSearch { get; set; } = true;

        public string MangaFolderFormat { get; set; } = "{Series Title}";
        public string ComicFolderFormat { get; set; } = "{Series Title} ({Series Year})";
        public string FileFormat { get; set; } = "{Series Title} v{Volume:00} ({Year})";
    }

    public interface IComicSettingsService
    {
        ComicSettings Get();
        ComicSettings Save(ComicSettings settings);
    }

    public class ComicSettingsService : IComicSettingsService
    {
        private readonly ITalearrComicsStore _store;

        public ComicSettingsService(ITalearrComicsStore store)
        {
            _store = store;
        }

        public ComicSettings Get()
        {
            var settings = new ComicSettings();
            settings.AllowDigital = ReadBool("allowDigital", settings.AllowDigital);
            settings.AllowScan = ReadBool("allowScan", settings.AllowScan);
            settings.AllowC2C = ReadBool("allowC2C", settings.AllowC2C);
            settings.PreferCbz = ReadBool("preferCbz", settings.PreferCbz);
            settings.AllowPacks = ReadBool("allowPacks", settings.AllowPacks);
            settings.AutomaticSearch = ReadBool("automaticSearch", settings.AutomaticSearch);
            settings.MinimumVolumeSizeMb = ReadInt("minimumVolumeSizeMb", settings.MinimumVolumeSizeMb);
            settings.MangaFolderFormat = ReadString("mangaFolderFormat", settings.MangaFolderFormat);
            settings.ComicFolderFormat = ReadString("comicFolderFormat", settings.ComicFolderFormat);
            settings.FileFormat = ReadString("fileFormat", settings.FileFormat);

            var categories = _store.GetSetting("categories");
            if (!string.IsNullOrWhiteSpace(categories))
            {
                var parsed = categories.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                    .Select(c => int.TryParse(c, NumberStyles.Integer, CultureInfo.InvariantCulture, out var id) ? id : 0)
                    .Where(c => c > 0)
                    .Distinct()
                    .ToList();

                if (parsed.Any())
                {
                    settings.Categories = parsed;
                }
            }

            return settings;
        }

        public ComicSettings Save(ComicSettings settings)
        {
            if (settings.Categories == null || !settings.Categories.Any(c => c > 0))
            {
                throw new ArgumentException("Choose at least one indexer category for comics (7030 is Books > Comics).");
            }

            if (!settings.AllowDigital && !settings.AllowScan && !settings.AllowC2C)
            {
                throw new ArgumentException("Allow at least one kind of release: digital, scan or c2c.");
            }

            if (string.IsNullOrWhiteSpace(settings.FileFormat) || !settings.FileFormat.Contains("{Volume", StringComparison.Ordinal))
            {
                throw new ArgumentException("The file name format needs a volume token, like {Volume:00}.");
            }

            _store.SetSetting("allowDigital", settings.AllowDigital.ToString());
            _store.SetSetting("allowScan", settings.AllowScan.ToString());
            _store.SetSetting("allowC2C", settings.AllowC2C.ToString());
            _store.SetSetting("preferCbz", settings.PreferCbz.ToString());
            _store.SetSetting("allowPacks", settings.AllowPacks.ToString());
            _store.SetSetting("automaticSearch", settings.AutomaticSearch.ToString());
            _store.SetSetting("minimumVolumeSizeMb", Math.Max(0, settings.MinimumVolumeSizeMb).ToString(CultureInfo.InvariantCulture));
            _store.SetSetting("mangaFolderFormat", settings.MangaFolderFormat?.Trim());
            _store.SetSetting("comicFolderFormat", settings.ComicFolderFormat?.Trim());
            _store.SetSetting("fileFormat", settings.FileFormat.Trim());
            _store.SetSetting("categories", string.Join(",", settings.Categories.Where(c => c > 0).Distinct()));

            return Get();
        }

        private bool ReadBool(string key, bool fallback)
        {
            var value = _store.GetSetting(key);
            return bool.TryParse(value, out var parsed) ? parsed : fallback;
        }

        private int ReadInt(string key, int fallback)
        {
            var value = _store.GetSetting(key);
            return int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) ? parsed : fallback;
        }

        private string ReadString(string key, string fallback)
        {
            var value = _store.GetSetting(key);
            return string.IsNullOrWhiteSpace(value) ? fallback : value;
        }
    }
}
