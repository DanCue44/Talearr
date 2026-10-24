using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using NLog;
using NzbDrone.Common.Disk;
using NzbDrone.Common.Extensions;
using NzbDrone.Core.Download;
using NzbDrone.Core.MediaFiles.TalearrImport;
using NzbDrone.Core.Talearr.Comics.Parsing;
using NzbDrone.Core.Talearr.Comics.Search;

namespace NzbDrone.Core.Talearr.Comics.Import
{
    public class ComicImportResult
    {
        public List<string> Imported { get; set; } = new List<string>();
        public List<string> Skipped { get; set; } = new List<string>();
    }

    public interface IComicImportService
    {
        ComicImportResult ImportDownload(string downloadId);

        // Links comic files already in the series folder to their volumes (nothing is moved).
        ComicImportResult RescanSeries(int seriesId);

        // Moves the series' files to the current naming, after a name format change.
        ComicImportResult RenameSeries(int seriesId);
    }

    // Talearr: files a finished comic download into the library: every comic file in the download
    // is matched to a volume of the series by its name, rewritten as a CBZ with Talearr's
    // ComicInfo.xml (CBR and CB7 converted), named and placed in the series folder. The download
    // itself is never modified, so torrents keep seeding. Scene releases that wrap the comic in
    // zips holding a rar set are unpacked next to the download first, as book imports are. A
    // download that ends with nothing imported is a failed release: it is blocklisted and the
    // volumes are searched for again.
    public class ComicImportService : IComicImportService
    {
        // A comic release unpacks to a few GB at most; the book defaults allow audiobook sets.
        private static readonly ArchiveExtractionLimits UnpackLimits = new ArchiveExtractionLimits { MaxTotalBytes = 2 * ComicArchive.MaxBytes };

        private readonly ITalearrComicsStore _store;
        private readonly IComicSettingsService _settingsService;
        private readonly IComicLibraryNotifier _notifier;
        private readonly IProvideDownloadClient _downloadClientProvider;
        private readonly IDownloadArchiveUnpacker _archiveUnpacker;
        private readonly IComicFailedDownloadService _failedDownloads;
        private readonly IDiskProvider _diskProvider;
        private readonly Logger _logger;

        public ComicImportService(ITalearrComicsStore store,
                                  IComicSettingsService settingsService,
                                  IComicLibraryNotifier notifier,
                                  IProvideDownloadClient downloadClientProvider,
                                  IDownloadArchiveUnpacker archiveUnpacker,
                                  IComicFailedDownloadService failedDownloads,
                                  IDiskProvider diskProvider,
                                  Logger logger)
        {
            _store = store;
            _settingsService = settingsService;
            _notifier = notifier;
            _downloadClientProvider = downloadClientProvider;
            _archiveUnpacker = archiveUnpacker;
            _failedDownloads = failedDownloads;
            _diskProvider = diskProvider;
            _logger = logger;
        }

        public ComicImportResult ImportDownload(string downloadId)
        {
            var download = _store.GetDownload(downloadId) ?? throw new ComicsException("Unknown comic download {0}", downloadId);
            var series = _store.GetSeries(download.SeriesId) ?? throw new ComicsException("The series of this download was removed");
            var result = new ComicImportResult();

            download.Status = ComicDownloadStatuses.Importing;
            _store.SaveDownload(download);

            ArchiveUnpackResult unpacked = null;
            try
            {
                // A download that holds its comic only inside archives is unpacked into a work
                // folder beside it; the download itself is only read.
                unpacked = _archiveUnpacker?.Unpack(download.OutputPath, IsComicFile, "comic files (CBZ, CBR, CB7, PDF, EPUB)", UnpackLimits);
                if (unpacked != null && !unpacked.Success)
                {
                    _failedDownloads.Failed(download, ComicHistoryEvents.ImportFailed, $"Could not unpack the archives in {download.OutputPath}: {unpacked.Error}");
                    return result;
                }

                var files = FindComicFiles(unpacked?.WorkFolder ?? download.OutputPath);
                if (files.Count == 0)
                {
                    _failedDownloads.Failed(download, ComicHistoryEvents.ImportFailed, $"No comic files (CBZ, CBR, CB7, PDF, EPUB) in {download.OutputPath}");
                    return result;
                }

                // A reader that has never seen this folder needs a wider scan
                var newFolder = series.Path.IsNullOrWhiteSpace() || !_diskProvider.FolderExists(series.Path);
                var volumes = _store.GetVolumes(series.Id);
                var grabbedFor = download.GetVolumeIds();
                var matches = Match(series, volumes, grabbedFor, files, result);

                foreach (var (file, volume) in matches)
                {
                    bool filed;
                    try
                    {
                        filed = FileInto(series, volume, file, download.ReleaseTitle, download.Score, result);
                    }
                    catch (ComicArchiveException ex)
                    {
                        // A damaged or locked file is the release's problem, like a missing one.
                        result.Skipped.Add(Path.GetFileName(file) + ": " + ex.Message);
                        continue;
                    }

                    if (filed)
                    {
                        _store.AddHistory(new ComicHistoryEntry
                        {
                            SeriesId = series.Id,
                            VolumeId = volume.Id,
                            EventType = ComicHistoryEvents.Imported,
                            SourceTitle = download.ReleaseTitle,
                            Message = Path.GetFileName(file)
                        });
                    }
                }

                if (!result.Imported.Any())
                {
                    _failedDownloads.Failed(download, ComicHistoryEvents.ImportFailed, "Nothing imported: " + string.Join("; ", result.Skipped));
                    return result;
                }

                download.Status = ComicDownloadStatuses.Imported;
                download.Message = result.Skipped.Any() ? "Imported; skipped: " + string.Join("; ", result.Skipped) : null;
                _store.SaveDownload(download);

                _notifier.FolderChanged(series, series.Path, newFolder);
                MarkImported(download);
            }
            catch (Exception ex)
            {
                // Not the release's fault (the download folder is missing, the library cannot be
                // written): no blocklist, so the release can be tried again once that is fixed.
                download.Status = ComicDownloadStatuses.Failed;
                download.Message = ex.Message;
                _store.SaveDownload(download);
                _store.AddHistory(new ComicHistoryEntry { SeriesId = series.Id, EventType = ComicHistoryEvents.ImportFailed, SourceTitle = download.ReleaseTitle, Message = ex.Message });
                _logger.Warn(ex, "Comics: import of {0} failed", download.ReleaseTitle);
                throw;
            }
            finally
            {
                if (unpacked != null)
                {
                    _archiveUnpacker.Cleanup(unpacked);
                }
            }

            return result;
        }

        public ComicImportResult RescanSeries(int seriesId)
        {
            var series = _store.GetSeries(seriesId) ?? throw new ComicsException("No such series");
            var result = new ComicImportResult();
            var known = _store.GetFiles(seriesId);

            // Forget files that are gone.
            foreach (var file in known.Where(f => !_diskProvider.FileExists(f.Path)))
            {
                _store.DeleteFile(file.Id);
                _store.AddHistory(new ComicHistoryEntry { SeriesId = seriesId, VolumeId = file.VolumeId, EventType = ComicHistoryEvents.Deleted, Message = "File is no longer on disk: " + Path.GetFileName(file.Path) });
            }

            if (!_diskProvider.FolderExists(series.Path))
            {
                return result;
            }

            var volumes = _store.GetVolumes(seriesId);
            var have = _store.GetFiles(seriesId);
            var paths = new HashSet<string>(have.Select(f => f.Path), StringComparer.OrdinalIgnoreCase);
            var filled = new HashSet<int>(have.Select(f => f.VolumeId));

            foreach (var path in FindComicFiles(series.Path).Where(p => !paths.Contains(p)))
            {
                var parsed = ComicReleaseParser.Parse(Path.GetFileName(path), series.AllTitles());
                var volume = VolumeFor(parsed, series, volumes);
                if (volume == null || filled.Contains(volume.Id))
                {
                    result.Skipped.Add(Path.GetFileName(path) + (volume == null ? ": no volume number" : ": volume already has a file"));
                    continue;
                }

                _store.AddFile(new ComicFile
                {
                    SeriesId = seriesId,
                    VolumeId = volume.Id,
                    Path = path,
                    Size = _diskProvider.GetFileSize(path),
                    Format = ComicFormats.FromExtension(Path.GetExtension(path)),
                    Source = parsed.Source,
                    ReleaseGroup = parsed.Group,
                    SourceTitle = Path.GetFileName(path)
                });
                filled.Add(volume.Id);
                result.Imported.Add(Path.GetFileName(path));
            }

            return result;
        }

        public ComicImportResult RenameSeries(int seriesId)
        {
            var series = _store.GetSeries(seriesId) ?? throw new ComicsException("No such series");
            var settings = _settingsService.Get();
            var root = _store.GetRootFolder(series.RootFolderId);
            var result = new ComicImportResult();
            var movedFolder = false;

            if (root != null)
            {
                var folder = Path.Combine(root.Path, ComicNaming.SeriesFolder(series, settings));
                if (!string.Equals(folder, series.Path, StringComparison.Ordinal))
                {
                    movedFolder = true;
                    series.Path = folder;
                    _store.UpdateSeries(series);
                }
            }

            var volumes = _store.GetVolumes(seriesId).ToDictionary(v => v.Id);
            foreach (var file in _store.GetFiles(seriesId))
            {
                if (!volumes.TryGetValue(file.VolumeId, out var volume) || !_diskProvider.FileExists(file.Path))
                {
                    continue;
                }

                var target = Path.Combine(series.Path, ComicNaming.FileName(series, volume, settings) + Path.GetExtension(file.Path));
                if (string.Equals(target, file.Path, StringComparison.Ordinal))
                {
                    continue;
                }

                _diskProvider.CreateFolder(series.Path);
                _diskProvider.MoveFile(file.Path, target);
                var old = file.Path;
                file.Path = target;
                _store.UpdateFile(file);
                _store.AddHistory(new ComicHistoryEntry { SeriesId = seriesId, VolumeId = volume.Id, EventType = ComicHistoryEvents.Renamed, Message = $"{Path.GetFileName(old)} -> {Path.GetFileName(target)}" });
                result.Imported.Add(Path.GetFileName(target));
            }

            if (result.Imported.Any())
            {
                _notifier.FolderChanged(series, series.Path, movedFolder);
            }

            return result;
        }

        private List<(string File, ComicVolume Volume)> Match(ComicSeries series, List<ComicVolume> volumes, List<int> grabbedFor, List<string> files, ComicImportResult result)
        {
            var matches = new List<(string, ComicVolume)>();
            var targets = volumes.Where(v => grabbedFor.Contains(v.Id)).ToList();

            // One file for one volume: whatever it is called, it is that volume.
            if (files.Count == 1 && targets.Count == 1)
            {
                matches.Add((files[0], targets[0]));
                return matches;
            }

            foreach (var file in files)
            {
                var parsed = ComicReleaseParser.Parse(Path.GetFileName(file), series.AllTitles());
                var volume = VolumeFor(parsed, series, targets);
                if (volume == null)
                {
                    result.Skipped.Add(Path.GetFileName(file) + ": not one of the volumes grabbed");
                    continue;
                }

                if (matches.Any(m => m.Item2.Id == volume.Id))
                {
                    result.Skipped.Add(Path.GetFileName(file) + ": a second file for the same volume");
                    continue;
                }

                matches.Add((file, volume));
            }

            return matches;
        }

        private static ComicVolume VolumeFor(ParsedComicRelease parsed, ComicSeries series, List<ComicVolume> volumes)
        {
            if (!parsed.Number.HasValue || parsed.NumberKind == ComicNumberKind.Chapter || parsed.NumberKind == ComicNumberKind.Issue)
            {
                return volumes.Count == 1 && parsed.NumberKind == ComicNumberKind.None ? volumes[0] : null;
            }

            if (parsed.NumberKind == ComicNumberKind.Bare && series.Kind == ComicKinds.Comic)
            {
                return null;
            }

            return volumes.FirstOrDefault(v => v.Number == parsed.Number.Value);
        }

        private bool FileInto(ComicSeries series, ComicVolume volume, string sourceFile, string releaseTitle, int score, ComicImportResult result)
        {
            var settings = _settingsService.Get();
            var parsed = ComicReleaseParser.Parse(releaseTitle ?? Path.GetFileName(sourceFile), series.AllTitles());
            var format = ComicFormats.FromExtension(Path.GetExtension(sourceFile));
            var keepsFormat = format == ComicFormats.Pdf || format == ComicFormats.Epub;
            var extension = keepsFormat ? Path.GetExtension(sourceFile).ToLowerInvariant() : ".cbz";

            var existing = _store.GetFiles(series.Id).FirstOrDefault(f => f.VolumeId == volume.Id);
            if (existing != null && existing.Score > score && _diskProvider.FileExists(existing.Path))
            {
                result.Skipped.Add($"{Path.GetFileName(sourceFile)}: the library already has a better copy of volume {ComicNaming.FormatNumber(volume.Number, "0")}");
                return false;
            }

            _diskProvider.CreateFolder(series.Path);
            var target = Path.Combine(series.Path, ComicNaming.FileName(series, volume, settings) + extension);

            if (keepsFormat)
            {
                _diskProvider.CopyFile(sourceFile, target, overwrite: true);
            }
            else
            {
                // PageCount comes from the pages written, not the printed book's page count
                ComicArchive.WriteCbz(sourceFile, target, pages => ComicInfoBuilder.Build(series, volume, pages));
            }

            if (existing != null)
            {
                if (!string.Equals(existing.Path, target, StringComparison.Ordinal) && _diskProvider.FileExists(existing.Path))
                {
                    _diskProvider.DeleteFile(existing.Path);
                }

                _store.DeleteFile(existing.Id);
            }

            _store.AddFile(new ComicFile
            {
                SeriesId = series.Id,
                VolumeId = volume.Id,
                Path = target,
                Size = _diskProvider.GetFileSize(target),
                Format = keepsFormat ? format : ComicFormats.Cbz,
                Source = parsed.Source,
                ReleaseGroup = parsed.Group,
                SourceTitle = releaseTitle,
                Score = score
            });

            result.Imported.Add(Path.GetFileName(target));
            _logger.Info("Comics: imported {0} as {1}", Path.GetFileName(sourceFile), target);
            return true;
        }

        private List<string> FindComicFiles(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                return new List<string>();
            }

            if (_diskProvider.FileExists(path))
            {
                return IsComicFile(path) ? new List<string> { path } : new List<string>();
            }

            if (!_diskProvider.FolderExists(path))
            {
                throw new ComicsException("The download folder {0} does not exist (check Remote Path Mappings)", path);
            }

            return _diskProvider.GetFiles(path, true)
                .Where(IsComicFile)
                .Where(f => !Path.GetFileName(f).StartsWith("._", StringComparison.Ordinal))
                .Where(f => !f.EndsWith(".talearr-tmp", StringComparison.Ordinal))
                .OrderBy(f => f, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        private static bool IsComicFile(string path)
        {
            var extension = Path.GetExtension(path).ToLowerInvariant();
            return extension == ".cbz" || extension == ".cbr" || extension == ".cb7" || extension == ".pdf" || extension == ".epub";
        }

        private void MarkImported(ComicDownload download)
        {
            try
            {
                var client = _downloadClientProvider.Get(download.DownloadClientId);
                var item = client?.GetItems().FirstOrDefault(i => i.DownloadId == download.DownloadId);
                if (item != null)
                {
                    client.MarkItemAsImported(item);
                }
            }
            catch (Exception ex)
            {
                _logger.Debug(ex, "Comics: could not mark {0} as imported in the download client", download.ReleaseTitle);
            }
        }
    }
}
