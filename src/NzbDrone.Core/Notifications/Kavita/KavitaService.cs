using System;
using System.Linq;
using FluentValidation.Results;
using NLog;

namespace NzbDrone.Core.Notifications.Kavita;

public interface IKavitaService
{
    void Notify(KavitaSettings settings, string message);
    void ScanComicFolder(KavitaSettings settings, string folderPath, bool newFolder);
    ValidationFailure Test(KavitaSettings settings, string message);
}

public class KavitaService : IKavitaService
{
    private readonly IKavitaServiceProxy _proxy;
    private readonly Logger _logger;

    public KavitaService(IKavitaServiceProxy proxy,
        Logger logger)
    {
        _proxy = proxy;
        _logger = logger;
    }

    public void Notify(KavitaSettings settings, string folderPath)
    {
        _proxy.Notify(settings, folderPath);
    }

    // Talearr: Kavita's folder scan does nothing for a folder it has never seen, or in a library
    // that never had a full scan, so a new series folder (or such a library) gets a library scan.
    public void ScanComicFolder(KavitaSettings settings, string folderPath, bool newFolder)
    {
        try
        {
            var token = GetToken(settings);
            var library = _proxy.GetLibraries(settings, token)
                .Select(l => new { Library = l, Folder = l.Folders.Where(f => ComicFolderMatch(folderPath, f)).OrderByDescending(f => f.Length).FirstOrDefault() })
                .Where(x => x.Folder != null)
                .OrderByDescending(x => x.Folder.Length)
                .Select(x => x.Library)
                .FirstOrDefault();

            if (library != null && (newFolder || library.NeverScanned))
            {
                _logger.Debug("Kavita: scanning library {0} for {1} ({2})", library.Name, folderPath, newFolder ? "new series folder" : "library never scanned");
                _proxy.ScanLibrary(settings, token, library.Id);
                return;
            }
        }
        catch (Exception ex)
        {
            _logger.Debug(ex, "Kavita: library lookup failed, scanning the folder only");
        }

        _proxy.Notify(settings, folderPath);
    }

    private static bool ComicFolderMatch(string path, string folder)
    {
        return NzbDrone.Core.Talearr.Comics.ComicReaderPaths.IsUnder(path, folder);
    }

    private string GetToken(KavitaSettings settings)
    {
        return _proxy.GetToken(settings);
    }

    public ValidationFailure Test(KavitaSettings settings, string message)
    {
        try
        {
            _logger.Debug("Determining Authentication of Host: {0}", _proxy.GetBaseUrl(settings));
            var token = GetToken(settings);
            _logger.Debug("Token obtained successfully (length: {0})", token?.Length ?? 0);
        }
        catch (KavitaAuthenticationException ex)
        {
            _logger.Error(ex, "Unable to connect to Kavita Server");
            return new ValidationFailure("ApiKey", "Incorrect ApiKey");
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "Unable to connect to Kavita Server");
            return new ValidationFailure("Host", "Unable to connect to Kavita Server");
        }

        return null;
    }
}
