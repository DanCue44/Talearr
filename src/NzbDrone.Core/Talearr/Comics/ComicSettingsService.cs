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

        // A native MyAnonamouse indexer is searched in these MAM site categories (61 is Ebooks -
        // Comics/Graphic novels, where MAM files manga too). Its Freeleech Only, VIP, ratio and
        // torrent slot rules apply as for books.
        public bool SearchMyAnonamouse { get; set; } = true;
        public List<int> MyAnonamouseCategories { get; set; } = new List<int> { 61 };

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

            settings.SearchMyAnonamouse = ReadBool("searchMyAnonamouse", settings.SearchMyAnonamouse);

            var categories = ReadIds("categories");
            if (categories.Any())
            {
                settings.Categories = categories;
            }

            var mamCategories = ReadIds("myAnonamouseCategories");
            if (mamCategories.Any())
            {
                settings.MyAnonamouseCategories = mamCategories;
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
            _store.SetSetting("searchMyAnonamouse", settings.SearchMyAnonamouse.ToString());

            var mamCategories = (settings.MyAnonamouseCategories ?? new List<int>()).Where(c => c > 0).Distinct().ToList();
            _store.SetSetting("myAnonamouseCategories", string.Join(",", mamCategories.Any() ? mamCategories : new List<int> { 61 }));

            return Get();
        }

        private List<int> ReadIds(string key)
        {
            var value = _store.GetSetting(key);
            if (string.IsNullOrWhiteSpace(value))
            {
                return new List<int>();
            }

            return value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(c => int.TryParse(c, NumberStyles.Integer, CultureInfo.InvariantCulture, out var id) ? id : 0)
                .Where(c => c > 0)
                .Distinct()
                .ToList();
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
