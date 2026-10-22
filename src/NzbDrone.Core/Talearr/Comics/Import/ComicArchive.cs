using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text.RegularExpressions;
using SharpCompress.Archives;
using SharpCompress.Common;
using SharpCompress.Readers;

namespace NzbDrone.Core.Talearr.Comics.Import
{
    public class ComicArchiveException : Exception
    {
        public ComicArchiveException(string message, Exception inner = null)
            : base(message, inner)
        {
        }
    }

    // Talearr: writes a library CBZ from a downloaded comic. CBZ, CBR (RAR) and CB7 (7-Zip) are
    // all rewritten as CBZ (a plain zip of pages), the format every reader handles, with Talearr's
    // ComicInfo.xml at the root. Only page images are copied: no stray files, no paths outside
    // the archive, and limits on count and size so a hostile file cannot fill the disk.
    public static class ComicArchive
    {
        public const int MaxPages = 5000;
        public const long MaxBytes = 4L * 1024 * 1024 * 1024;

        private static readonly HashSet<string> ImageExtensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            ".jpg", ".jpeg", ".png", ".webp", ".gif", ".avif", ".bmp", ".jxl", ".heic", ".heif"
        };

        public static bool IsImage(string name)
        {
            return ImageExtensions.Contains(Path.GetExtension(name ?? string.Empty));
        }

        // Writes destination as a CBZ holding the pages of source plus comicInfoXml (when given).
        // Returns the number of pages. destination is written through a temporary file, so a
        // failure never leaves half a file behind.
        public static int WriteCbz(string source, string destination, string comicInfoXml)
        {
            return WriteCbz(source, destination, string.IsNullOrWhiteSpace(comicInfoXml) ? null : _ => comicInfoXml);
        }

        // As above, with ComicInfo.xml built from the number of pages actually written, so its
        // PageCount always matches the archive (metadata page counts are for the printed book).
        public static int WriteCbz(string source, string destination, Func<int, string> comicInfoForPages)
        {
            var temporary = destination + ".talearr-tmp";
            try
            {
                int pages;
                using (var output = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None))
                using (var zip = new ZipArchive(output, ZipArchiveMode.Create))
                {
                    // ComicInfo.xml goes first (some readers look there), so count the pages the copy
                    // below will write before writing it.
                    var counted = comicInfoForPages == null ? 0 : CountPages(source);
                    var comicInfoXml = counted > 0 ? comicInfoForPages(counted) : null;
                    if (!string.IsNullOrWhiteSpace(comicInfoXml))
                    {
                        var info = zip.CreateEntry("ComicInfo.xml", CompressionLevel.Optimal);
                        using var writer = new StreamWriter(info.Open(), new System.Text.UTF8Encoding(false));
                        writer.Write(comicInfoXml);
                    }

                    pages = CopyPages(source, zip);
                }

                if (pages == 0)
                {
                    throw new ComicArchiveException($"{Path.GetFileName(source)} has no page images");
                }

                if (File.Exists(destination))
                {
                    File.Delete(destination);
                }

                File.Move(temporary, destination);
                return pages;
            }
            catch
            {
                TryDelete(temporary);
                throw;
            }
        }

        // The number of pages CopyPages writes for source: the same entries, image filter and
        // de-duplicated safe names, without reading any page data.
        public static int CountPages(string source)
        {
            List<string> keys;
            if (IsZip(source))
            {
                using var input = ZipFile.OpenRead(source);
                keys = input.Entries.Where(e => e.Length > 0).Select(e => e.FullName).ToList();
            }
            else
            {
                using var archive = ArchiveFactory.OpenArchive(new[] { new FileInfo(source) }, new ReaderOptions());
                keys = archive.Entries.Where(e => !e.IsDirectory).Select(e => e.Key).ToList();
            }

            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            return keys.Where(k => !string.IsNullOrWhiteSpace(k) && IsImage(k))
                .Select(SafeEntryName)
                .Count(n => n != null && names.Add(n));
        }

        private static int CopyPages(string source, ZipArchive zip)
        {
            var pages = 0;
            long bytes = 0;
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            void AddPage(string key, Func<Stream> open)
            {
                if (string.IsNullOrWhiteSpace(key) || !IsImage(key))
                {
                    return;
                }

                var name = SafeEntryName(key);
                if (name == null || !names.Add(name))
                {
                    return;
                }

                if (++pages > MaxPages)
                {
                    throw new ComicArchiveException($"{Path.GetFileName(source)} has more than {MaxPages} pages");
                }

                // Pages are already compressed images; storing them avoids wasted work.
                var entry = zip.CreateEntry(name, CompressionLevel.NoCompression);
                using var input = open();
                using var output = entry.Open();
                var buffer = new byte[81920];
                int read;
                while ((read = input.Read(buffer, 0, buffer.Length)) > 0)
                {
                    bytes += read;
                    if (bytes > MaxBytes)
                    {
                        throw new ComicArchiveException($"{Path.GetFileName(source)} unpacks to more than 4 GB");
                    }

                    output.Write(buffer, 0, read);
                }
            }

            if (IsZip(source))
            {
                using var input = ZipFile.OpenRead(source);
                foreach (var entry in input.Entries.Where(e => e.Length > 0).OrderBy(e => e.FullName, NaturalComparer.Instance))
                {
                    AddPage(entry.FullName, entry.Open);
                }

                return pages;
            }

            try
            {
                using var archive = ArchiveFactory.OpenArchive(new[] { new FileInfo(source) }, new ReaderOptions());
                if (archive.IsEncrypted)
                {
                    throw new ComicArchiveException($"{Path.GetFileName(source)} is password protected");
                }

                if (archive.IsSolid || archive.Type == ArchiveType.SevenZip)
                {
                    // Solid archives read front to back; collect then order.
                    var collected = new List<(string Key, byte[] Data)>();
                    using var reader = archive.ExtractAllEntries();
                    while (reader.MoveToNextEntry())
                    {
                        if (reader.Entry.IsDirectory || !IsImage(reader.Entry.Key))
                        {
                            continue;
                        }

                        using var stream = reader.OpenEntryStream();
                        using var memory = new MemoryStream();
                        stream.CopyTo(memory);
                        collected.Add((reader.Entry.Key, memory.ToArray()));

                        if (collected.Count > MaxPages)
                        {
                            throw new ComicArchiveException($"{Path.GetFileName(source)} has more than {MaxPages} pages");
                        }
                    }

                    foreach (var (key, data) in collected.OrderBy(c => c.Key, NaturalComparer.Instance))
                    {
                        AddPage(key, () => new MemoryStream(data));
                    }
                }
                else
                {
                    foreach (var entry in archive.Entries.Where(e => !e.IsDirectory).OrderBy(e => e.Key, NaturalComparer.Instance))
                    {
                        if (entry.IsEncrypted)
                        {
                            throw new ComicArchiveException($"{Path.GetFileName(source)} is password protected");
                        }

                        AddPage(entry.Key, entry.OpenEntryStream);
                    }
                }
            }
            catch (ComicArchiveException)
            {
                throw;
            }
            catch (Exception ex)
            {
                throw new ComicArchiveException($"Cannot read {Path.GetFileName(source)}: {ex.Message}", ex);
            }

            return pages;
        }

        // Many .cbr files are really zips, so the content decides, not the extension.
        public static bool IsZip(string path)
        {
            using var stream = File.OpenRead(path);
            var header = new byte[4];
            return stream.Read(header, 0, 4) == 4 && header[0] == 0x50 && header[1] == 0x4B && (header[2] == 3 || header[2] == 5 || header[2] == 7);
        }

        private static string SafeEntryName(string key)
        {
            var parts = key.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries)
                .Where(p => p != "." && p != "..")
                .Select(p => Regex.Replace(p, @"[\x00-\x1f:*?""<>|]", "_"))
                .ToList();

            return parts.Count == 0 ? null : string.Join("/", parts);
        }

        private static void TryDelete(string path)
        {
            try
            {
                if (File.Exists(path))
                {
                    File.Delete(path);
                }
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }

        // "page2" before "page10".
        private class NaturalComparer : IComparer<string>
        {
            public static readonly NaturalComparer Instance = new NaturalComparer();
            private static readonly Regex Chunks = new Regex(@"\d+|\D+", RegexOptions.Compiled);

            public int Compare(string x, string y)
            {
                var a = Chunks.Matches(x ?? string.Empty);
                var b = Chunks.Matches(y ?? string.Empty);
                for (var i = 0; i < Math.Min(a.Count, b.Count); i++)
                {
                    var left = a[i].Value;
                    var right = b[i].Value;
                    int result;
                    if (char.IsDigit(left[0]) && char.IsDigit(right[0]))
                    {
                        var l = left.TrimStart('0');
                        var r = right.TrimStart('0');
                        result = l.Length != r.Length ? l.Length.CompareTo(r.Length) : string.CompareOrdinal(l, r);
                    }
                    else
                    {
                        result = string.Compare(left, right, StringComparison.OrdinalIgnoreCase);
                    }

                    if (result != 0)
                    {
                        return result;
                    }
                }

                return a.Count.CompareTo(b.Count);
            }
        }
    }
}
