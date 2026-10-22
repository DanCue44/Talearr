using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using FluentValidation;
using FluentValidation.Results;
using NLog;
using NzbDrone.Common.Extensions;
using NzbDrone.Common.Http;
using NzbDrone.Common.Serializer;
using NzbDrone.Core.Annotations;
using NzbDrone.Core.Talearr.Comics;
using NzbDrone.Core.ThingiProvider;
using NzbDrone.Core.Validation;

namespace NzbDrone.Core.Notifications.Komga
{
    public class KomgaSettingsValidator : AbstractValidator<KomgaSettings>
    {
        public KomgaSettingsValidator()
        {
            RuleFor(c => c.Url).IsValidUrl();
            RuleFor(c => c)
                .Must(c => !string.IsNullOrWhiteSpace(c.ApiKey) || (!string.IsNullOrWhiteSpace(c.Username) && !string.IsNullOrWhiteSpace(c.Password)))
                .WithMessage("Use an API key, or a username and password");
        }
    }

    // Talearr: Komga (https://komga.org), a reader for comics and manga.
    public class KomgaSettings : IProviderConfig
    {
        private static readonly KomgaSettingsValidator Validator = new KomgaSettingsValidator();

        [FieldDefinition(0, Label = "URL", Type = FieldType.Url, Placeholder = "http://komga:25600", HelpText = "Komga's address, including http:// or https://")]
        public string Url { get; set; }

        [FieldDefinition(1, Label = "API Key", Privacy = PrivacyLevel.ApiKey, HelpText = "Komga > Account settings > API Keys (Komga 1.20 or newer). Or leave empty and use a username and password.")]
        public string ApiKey { get; set; }

        [FieldDefinition(2, Label = "Username", Privacy = PrivacyLevel.UserName, HelpText = "An admin user, if not using an API key")]
        public string Username { get; set; }

        [FieldDefinition(3, Label = "Password", Type = FieldType.Password, Privacy = PrivacyLevel.Password)]
        public string Password { get; set; }

        [FieldDefinition(4, Label = "Books", Type = FieldType.Checkbox, HelpText = "Also rescan after an ebook import (Komga reads EPUB and PDF too)")]
        public bool Books { get; set; }

        [FieldDefinition(5, Label = "Talearr Path", Type = FieldType.Textbox, Advanced = true, HelpText = "Only if Komga sees the library under a different path: the folder as Talearr sees it, for example /comics")]
        public string TalearrPath { get; set; }

        [FieldDefinition(6, Label = "Komga Path", Type = FieldType.Textbox, Advanced = true, HelpText = "The same folder as Komga sees it, for example /data/comics")]
        public string ReaderPath { get; set; }

        public NzbDroneValidationResult Validate()
        {
            return new NzbDroneValidationResult(Validator.Validate(this));
        }
    }

    public class KomgaLibrary
    {
        public string Id { get; set; }
        public string Name { get; set; }
        public string Root { get; set; }
    }

    // Talearr: after Talearr files a comic, Komga is asked to scan the library that holds the
    // series folder (POST api/v1/libraries/{id}/scan). Komga has no single-folder scan.
    public class Komga : NotificationBase<KomgaSettings>, IComicLibraryScanner
    {
        private readonly IHttpClient _httpClient;
        private readonly Logger _logger;

        public Komga(IHttpClient httpClient, Logger logger)
        {
            _httpClient = httpClient;
            _logger = logger;
        }

        public override string Name => "Komga";

        public override string Link => "https://komga.org/";

        public override void OnReleaseImport(BookDownloadMessage message)
        {
            if (!Settings.Books)
            {
                return;
            }

            foreach (var folder in (message.BookFiles ?? new List<MediaFiles.BookFile>()).Select(f => Path.GetDirectoryName(f.Path)).Where(f => f.IsNotNullOrWhiteSpace()).Distinct())
            {
                ScanComicFolder(folder);
            }
        }

        public void ScanComicFolder(string folder, bool newFolder = false)
        {
            var mapped = ComicReaderPaths.Map(Settings.TalearrPath, Settings.ReaderPath, folder);
            var libraries = GetLibraries().Where(l => ComicReaderPaths.IsUnder(mapped, l.Root)).ToList();

            if (libraries.Count == 0)
            {
                _logger.Warn("Komga: no library holds '{0}'. Add a Komga library for that folder, or set the path mapping.", mapped);
                return;
            }

            foreach (var library in libraries)
            {
                _httpClient.Execute(Build(HttpMethod.Post, $"api/v1/libraries/{library.Id}/scan"));
                _logger.Debug("Komga: scan of library {0} requested", library.Name);
            }
        }

        public List<KomgaLibrary> GetLibraries()
        {
            var response = _httpClient.Execute(Build(HttpMethod.Get, "api/v1/libraries"));
            return Json.Deserialize<List<KomgaLibrary>>(response.Content) ?? new List<KomgaLibrary>();
        }

        public override ValidationResult Test()
        {
            var failures = new List<ValidationFailure>();

            try
            {
                var libraries = GetLibraries();
                if (libraries.Count == 0)
                {
                    failures.Add(new ValidationFailure(nameof(Settings.Url), "Komga has no libraries yet. Add one for Talearr's comics folder."));
                }
            }
            catch (HttpException ex) when (ex.Response.StatusCode == HttpStatusCode.Unauthorized || ex.Response.StatusCode == HttpStatusCode.Forbidden)
            {
                failures.Add(new ValidationFailure(nameof(Settings.ApiKey), "Komga did not accept the API key or login"));
            }
            catch (Exception ex)
            {
                _logger.Warn(ex, "Unable to connect to Komga");
                failures.Add(new ValidationFailure(nameof(Settings.Url), $"Unable to connect to Komga: {ex.Message}"));
            }

            return new ValidationResult(failures);
        }

        private HttpRequest Build(HttpMethod method, string resource)
        {
            var request = new HttpRequestBuilder(Settings.Url.Trim().TrimEnd('/'))
                .Resource(resource)
                .Accept(HttpAccept.Json)
                .Build();

            request.Method = method;
            request.RequestTimeout = TimeSpan.FromSeconds(30);
            request.Headers.Add("User-Agent", "Talearr");

            if (!string.IsNullOrWhiteSpace(Settings.ApiKey))
            {
                request.Headers.Add("X-API-Key", Settings.ApiKey.Trim());
            }
            else
            {
                var credentials = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{Settings.Username}:{Settings.Password}"));
                request.Headers.Add("Authorization", "Basic " + credentials);
            }

            return request;
        }
    }
}
