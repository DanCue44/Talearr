using System;
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

    public class TalearrReaderHandoffFailureResource
    {
        public int BookFileId { get; set; }
        public int BookId { get; set; }
        public string Title { get; set; }
        public string FileName { get; set; }
        public string Error { get; set; }
        public int Attempts { get; set; }
        public DateTime UpdatedAt { get; set; }
    }

    public class TalearrReaderHandoffRetryResource
    {
        public int Sent { get; set; }
        public int Failed { get; set; }
        public int Removed { get; set; }
    }

    [V1ApiController("config/talearrreaderhandoff")]
    public class TalearrReaderHandoffConfigController : RestController<TalearrReaderHandoffConfigResource>
    {
        private readonly ITalearrReaderHandoffSettingsService _settingsService;
        private readonly ITalearrReaderHandoffService _handoffService;
        private readonly ITalearrKepubify _kepubify;
        private readonly ITalearrReaderHandoffRetryService _retryService;

        public TalearrReaderHandoffConfigController(ITalearrReaderHandoffSettingsService settingsService,
                                                    ITalearrReaderHandoffService handoffService,
                                                    ITalearrKepubify kepubify,
                                                    ITalearrReaderHandoffRetryService retryService)
        {
            _settingsService = settingsService;
            _handoffService = handoffService;
            _kepubify = kepubify;
            _retryService = retryService;

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

        // Hand-offs that failed (folder missing, disk full, file gone), newest first.
        [HttpGet("failures")]
        public List<TalearrReaderHandoffFailureResource> GetFailures()
        {
            return _retryService.GetFailures().ConvertAll(f => new TalearrReaderHandoffFailureResource
            {
                BookFileId = f.BookFileId,
                BookId = f.BookId,
                Title = f.Title,
                FileName = f.FileName,
                Error = f.Error,
                Attempts = f.Attempts,
                UpdatedAt = f.UpdatedAt
            });
        }

        [HttpPost("retry")]
        public TalearrReaderHandoffRetryResource RetryFailed()
        {
            return ToResource(_retryService.RetryFailed());
        }

        // Sends a book's ebooks to the reader folder again, for example after the reader lost it.
        [HttpPost("resend/{bookId:int}")]
        public TalearrReaderHandoffRetryResource Resend(int bookId)
        {
            return ToResource(_retryService.Resend(bookId));
        }

        private static TalearrReaderHandoffRetryResource ToResource(TalearrReaderHandoffRetryResult result)
        {
            return new TalearrReaderHandoffRetryResource
            {
                Sent = result.Sent,
                Failed = result.Failed,
                Removed = result.Removed
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
