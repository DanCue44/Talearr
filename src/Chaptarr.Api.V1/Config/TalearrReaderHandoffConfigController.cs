using System.Collections.Generic;
using Chaptarr.Http;
using Chaptarr.Http.REST;
using FluentValidation;
using Microsoft.AspNetCore.Mvc;
using NzbDrone.Core.Talearr.ReaderHandoff;
using NzbDrone.Core.Validation.Paths;
using NzbDrone.Http.REST.Attributes;

namespace Chaptarr.Api.V1.Config
{
    // Talearr: reader hand-off settings (copy imported ebooks to a watch folder). A new route;
    // no existing route or field changes. Stored as talearr.readerhandoff.* Config rows.
    public class TalearrReaderHandoffConfigResource : RestResource
    {
        public bool Enabled { get; set; }
        public string Path { get; set; }
        public bool AuthorFolder { get; set; }
        public bool ConvertToKepub { get; set; }
        public bool KepubifyInstalled { get; set; }
    }

    public class TalearrReaderHandoffTestResource
    {
        public bool Success { get; set; }
        public string Message { get; set; }
        public List<string> Errors { get; set; }
        public List<string> Warnings { get; set; }
    }

    [V1ApiController("config/talearrreaderhandoff")]
    public class TalearrReaderHandoffConfigController : RestController<TalearrReaderHandoffConfigResource>
    {
        private readonly ITalearrReaderHandoffSettingsService _settingsService;
        private readonly ITalearrReaderHandoffService _handoffService;
        private readonly ITalearrKepubify _kepubify;

        public TalearrReaderHandoffConfigController(ITalearrReaderHandoffSettingsService settingsService,
                                                    ITalearrReaderHandoffService handoffService,
                                                    ITalearrKepubify kepubify)
        {
            _settingsService = settingsService;
            _handoffService = handoffService;
            _kepubify = kepubify;

            SharedValidator.RuleFor(c => c.Path).IsValidPath().When(c => c.Enabled);
        }

        protected override TalearrReaderHandoffConfigResource GetResourceById(int id)
        {
            return GetConfig();
        }

        [HttpGet]
        public TalearrReaderHandoffConfigResource GetConfig()
        {
            return ToResource(_settingsService.Get());
        }

        [RestPutById]
        public ActionResult<TalearrReaderHandoffConfigResource> SaveConfig([FromBody] TalearrReaderHandoffConfigResource resource)
        {
            var saved = _settingsService.Save(FromResource(resource));

            return Accepted(ToResource(saved));
        }

        // Checks the (possibly unsaved) folder: exists, absolute, writable; kepubify if needed.
        [HttpPost("test")]
        public TalearrReaderHandoffTestResource Test([FromBody] TalearrReaderHandoffConfigResource resource)
        {
            var result = _handoffService.Test(FromResource(resource));

            return new TalearrReaderHandoffTestResource
            {
                Success = result.Success,
                Message = result.Message,
                Errors = result.Errors,
                Warnings = result.Warnings
            };
        }

        private static TalearrReaderHandoffSettings FromResource(TalearrReaderHandoffConfigResource resource)
        {
            return new TalearrReaderHandoffSettings
            {
                Enabled = resource?.Enabled ?? false,
                Path = resource?.Path ?? string.Empty,
                AuthorFolder = resource?.AuthorFolder ?? false,
                ConvertToKepub = resource?.ConvertToKepub ?? false
            };
        }

        private TalearrReaderHandoffConfigResource ToResource(TalearrReaderHandoffSettings settings)
        {
            return new TalearrReaderHandoffConfigResource
            {
                Id = 1,
                Enabled = settings.Enabled,
                Path = settings.Path,
                AuthorFolder = settings.AuthorFolder,
                ConvertToKepub = settings.ConvertToKepub,
                KepubifyInstalled = _kepubify.FindExecutable() != null
            };
        }
    }
}
