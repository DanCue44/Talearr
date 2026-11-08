using System;
using Chaptarr.Http;
using Chaptarr.Http.REST;
using FluentValidation;
using Microsoft.AspNetCore.Mvc;
using NzbDrone.Core.Talearr.Calibre;
using NzbDrone.Http.REST.Attributes;

namespace Chaptarr.Api.V1.Config
{
    // Talearr: the optional Calibre library used (read-only) by Retry Match and Manual Import to
    // identify ebooks. A new route; no existing route or field changes. The path is stored by
    // TalearrCalibreSettingsService as a talearr.calibre.* row in the Config table.
    public class TalearrCalibreConfigResource : RestResource
    {
        // Empty = off.
        public string LibraryPath { get; set; }

        // Read only: how many books Calibre's metadata.db holds, or why it cannot be read.
        public int? BookCount { get; set; }
        public string Problem { get; set; }
    }

    [V1ApiController("config/talearrcalibre")]
    public class TalearrCalibreConfigController : RestController<TalearrCalibreConfigResource>
    {
        private readonly ITalearrCalibreSettingsService _settingsService;

        public TalearrCalibreConfigController(ITalearrCalibreSettingsService settingsService)
        {
            _settingsService = settingsService;

            SharedValidator.RuleFor(c => c.LibraryPath).Custom((path, context) =>
            {
                var problem = TalearrCalibreSettingsService.Validate(path);
                if (problem != null)
                {
                    context.AddFailure(problem);
                }
            });
        }

        protected override TalearrCalibreConfigResource GetResourceById(int id)
        {
            return GetConfig();
        }

        [HttpGet]
        public TalearrCalibreConfigResource GetConfig()
        {
            return ToResource(_settingsService.Get());
        }

        [RestPutById]
        public ActionResult<TalearrCalibreConfigResource> SaveConfig([FromBody] TalearrCalibreConfigResource resource)
        {
            var saved = _settingsService.Save(new TalearrCalibreSettings
            {
                LibraryPath = resource.LibraryPath
            });

            return Accepted(ToResource(saved));
        }

        internal static TalearrCalibreConfigResource ToResource(TalearrCalibreSettings settings)
        {
            var resource = new TalearrCalibreConfigResource
            {
                Id = 1,
                LibraryPath = settings.LibraryPath ?? string.Empty
            };

            if (settings.Enabled)
            {
                resource.Problem = TalearrCalibreSettingsService.Validate(settings.LibraryPath);
                if (resource.Problem == null)
                {
                    try
                    {
                        resource.BookCount = TalearrCalibreLibraryReader.CountBooks(TalearrCalibreSettingsService.MetadataPath(settings.LibraryPath));
                    }
                    catch (Exception ex)
                    {
                        resource.Problem = ex.Message;
                    }
                }
            }

            return resource;
        }
    }
}
