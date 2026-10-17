using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;
using NzbDrone.Core.Indexers.MyAnonaMouse;

namespace NzbDrone.Core.Talearr.MamFiller
{
    // TaleArr: pure rules behind the freeleech seeding filler, kept apart from MAM, the client and
    // talearr.db so they can be tested with plain data.
    public static class TalearrMamFillerRules
    {
        public const int AudiobookCategory = 13;
        public const int EbookCategory = 14;
        public const int EnglishLanguageId = 1;

        // How long MAM may take to count a new snatch (see MamUnsatisfiedSlotGuard).
        public static readonly TimeSpan AccountingLag = TimeSpan.FromMinutes(20);
        public static readonly TimeSpan Backoff = TimeSpan.FromHours(12);
        public static readonly TimeSpan MinimumSeedTime = TimeSpan.FromHours(72);

        public static TalearrMamFillerCandidate Parse(JToken torrent)
        {
            if (torrent == null || torrent.Type != JTokenType.Object)
            {
                return null;
            }

            var id = ReadString(torrent, "id");
            if (string.IsNullOrWhiteSpace(id) || !long.TryParse(id, out var numeric) || numeric <= 0)
            {
                return null;
            }

            var sizeToken = torrent["size"];
            long size = 0;
            if (sizeToken != null && sizeToken.Type is JTokenType.Integer or JTokenType.Float)
            {
                size = (long)Math.Max(0, sizeToken.ToObject<double>());
            }
            else if (sizeToken != null)
            {
                size = MyAnonaMouseAccountStanding.ParseSize(sizeToken.ToObject<string>()) ?? 0;
            }

            return new TalearrMamFillerCandidate
            {
                TorrentId = id,
                Title = ReadString(torrent, "title") ?? ReadString(torrent, "name") ?? id,
                Size = size,
                Seeders = ReadInt(torrent, "seeders") ?? 0,
                Leechers = ReadInt(torrent, "leechers") ?? 0,
                MainCategory = ReadInt(torrent, "main_cat"),
                LanguageCode = ReadString(torrent, "lang_code"),
                LanguageId = ReadInt(torrent, "language"),
                Free = ReadFlag(torrent, "free"),
                PersonalFreeleech = ReadFlag(torrent, "personal_freeleech"),
                VipFreeleech = ReadFlag(torrent, "fl_vip"),
                Vip = ReadFlag(torrent, "vip"),
                CategoryId = ReadInt(torrent, "category"),
                CategoryName = ReadString(torrent, "catname"),
                Authors = ReadAuthors(torrent["author_info"])
            };
        }

        // TaleArr: author_info is a JSON string like {"7640": "J K Rowling"} (sometimes an object).
        internal static List<string> ReadAuthors(JToken token)
        {
            var authors = new List<string>();
            if (token == null || token.Type is JTokenType.Null or JTokenType.Undefined)
            {
                return authors;
            }

            try
            {
                var map = token.Type == JTokenType.Object ? (JObject)token : JObject.Parse(token.ToObject<string>() ?? string.Empty);
                foreach (var property in map.Properties())
                {
                    var name = property.Value?.Type == JTokenType.String ? property.Value.ToObject<string>()?.Trim() : null;
                    if (!string.IsNullOrEmpty(name) && !authors.Contains(name, StringComparer.OrdinalIgnoreCase))
                    {
                        authors.Add(name);
                    }
                }
            }
            catch (Exception)
            {
                // Not JSON: no authors, the torrent can still match by category.
            }

            return authors;
        }

        // Strictly from MAM's own flags (no title text guessing): global freeleech, or personal
        // freeleech already applied. VIP freeleech only counts for a VIP account.
        public static bool IsFreeleechFor(TalearrMamFillerCandidate candidate, bool accountIsVip)
        {
            return candidate.Free ||
                   candidate.PersonalFreeleech ||
                   (accountIsVip && (candidate.VipFreeleech || candidate.Vip));
        }

        public static bool IsEnglish(TalearrMamFillerCandidate candidate)
        {
            if (!string.IsNullOrWhiteSpace(candidate.LanguageCode))
            {
                return string.Equals(candidate.LanguageCode.Trim(), "ENG", StringComparison.OrdinalIgnoreCase) ||
                       string.Equals(candidate.LanguageCode.Trim(), "EN", StringComparison.OrdinalIgnoreCase);
            }

            return candidate.LanguageId == EnglishLanguageId;
        }

        // Null when the torrent may be grabbed, otherwise why not.
        public static string Reject(TalearrMamFillerCandidate candidate, MyAnonaMouseSettings settings)
        {
            if (candidate.Vip && !settings.IsVip)
            {
                return "VIP only";
            }

            if (!IsFreeleechFor(candidate, settings.IsVip))
            {
                return "not freeleech";
            }

            if (candidate.MainCategory != AudiobookCategory && candidate.MainCategory != EbookCategory)
            {
                return "not an audiobook or ebook";
            }

            if (!IsEnglish(candidate))
            {
                return "not English";
            }

            if (candidate.Size <= 0)
            {
                return "unknown size";
            }

            if (candidate.Size > Math.Max(1, settings.FillerMaxSizeMb) * 1024L * 1024L)
            {
                return "too large";
            }

            if (candidate.Leechers < Math.Max(0, settings.FillerMinLeechers))
            {
                return "too few leechers";
            }

            if (candidate.Seeders > Math.Max(0, settings.FillerMaxSeeders))
            {
                return "too many seeders";
            }

            return null;
        }

        // TaleArr: preferred picks (taste preferences) first, then audiobooks when preferred, then
        // most leechers, fewest seeders, smallest. Without preferences this is the 0.18.0 order.
        public static List<TalearrMamFillerCandidate> Rank(IEnumerable<TalearrMamFillerCandidate> candidates, bool preferAudiobooks)
        {
            return candidates
                .OrderByDescending(c => c.IsPreferred)
                .ThenByDescending(c => preferAudiobooks && c.IsAudiobook)
                .ThenByDescending(c => c.Leechers)
                .ThenBy(c => c.Seeders)
                .ThenBy(c => c.Size)
                .ThenBy(c => c.TorrentId, StringComparer.Ordinal)
                .ToList();
        }

        public static int Target(int limit, int margin)
        {
            return Math.Max(0, limit - Math.Max(0, margin));
        }

        // Slots the filler may use now: limit minus margin, minus what MAM reports and what is
        // in flight but not counted by MAM yet, capped by the per-run maximum.
        public static int FreeSlots(int limit, int unsatisfied, int pending, int margin, int maxPerRun)
        {
            var free = Target(limit, margin) - Math.Max(0, unsatisfied) - Math.Max(0, pending);
            return Math.Max(0, Math.Min(free, Math.Max(0, maxPerRun)));
        }

        public static TimeSpan SeedTime(MyAnonaMouseSettings settings)
        {
            var days = TimeSpan.FromDays(Math.Max(1, settings.FillerSeedDays));
            return days < MinimumSeedTime ? MinimumSeedTime : days;
        }

        public static bool IsRunDue(DateTime? lastRunAt, int intervalHours, DateTime now)
        {
            return !lastRunAt.HasValue ||
                   lastRunAt.Value > now ||
                   now - lastRunAt.Value >= TimeSpan.FromHours(Math.Max(1, intervalHours)) - TimeSpan.FromMinutes(5);
        }

        private static string ReadString(JToken token, string key)
        {
            var value = token[key];
            if (value == null || value.Type is JTokenType.Null or JTokenType.Undefined or JTokenType.Object or JTokenType.Array)
            {
                return null;
            }

            var text = value.ToObject<string>()?.Trim();
            return string.IsNullOrEmpty(text) ? null : text;
        }

        private static int? ReadInt(JToken token, string key)
        {
            var number = MyAnonaMouseUserDataResponse.ReadNumber(token[key]);
            return number.HasValue && number.Value <= int.MaxValue ? (int)Math.Floor(number.Value) : null;
        }

        private static bool ReadFlag(JToken token, string key)
        {
            var value = token[key];
            if (value == null)
            {
                return false;
            }

            switch (value.Type)
            {
                case JTokenType.Boolean:
                    return value.ToObject<bool>();
                case JTokenType.Integer:
                    return value.ToObject<long>() != 0;
                case JTokenType.String:
                    var text = value.ToObject<string>()?.Trim();
                    return text == "1" ||
                           string.Equals(text, "true", StringComparison.OrdinalIgnoreCase) ||
                           string.Equals(text, "yes", StringComparison.OrdinalIgnoreCase);
                default:
                    return false;
            }
        }
    }
}
