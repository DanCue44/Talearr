using System.Collections.Generic;
using Chaptarr.Http;
using Chaptarr.Http.REST;
using Microsoft.AspNetCore.Mvc;
using NzbDrone.Core.Talearr.EbookDelivery;
using NzbDrone.Http.REST.Attributes;

namespace Chaptarr.Api.V1.Config
{
    // Talearr: Send to Kindle settings. A new route; no existing route or field changes. Stored as
    // talearr.kindle.* Config rows. The password is never returned: GET answers "********" when
    // one is stored, and saving "********" back keeps it.
    public class TalearrKindleConfigResource : RestResource
    {
        public bool Enabled { get; set; }
        public bool AutoSendOnImport { get; set; }
        public List<string> KindleAddresses { get; set; }
        public int EmailConnectionId { get; set; }
        public string Server { get; set; }
        public int Port { get; set; }
        public bool RequireEncryption { get; set; }
        public string Username { get; set; }
        public string Password { get; set; }
        public string From { get; set; }
    }

    [V1ApiController("config/talearrkindle")]
    public class TalearrKindleConfigController : RestController<TalearrKindleConfigResource>
    {
        private readonly ITalearrKindleSettingsService _settingsService;
        private readonly ITalearrKindleService _kindleService;

        public TalearrKindleConfigController(ITalearrKindleSettingsService settingsService, ITalearrKindleService kindleService)
        {
            _settingsService = settingsService;
            _kindleService = kindleService;
        }

        protected override TalearrKindleConfigResource GetResourceById(int id)
        {
            return GetConfig();
        }

        [HttpGet]
        public TalearrKindleConfigResource GetConfig()
        {
            return ToResource(_settingsService.Get());
        }

        [RestPutById]
        public ActionResult<TalearrKindleConfigResource> SaveConfig([FromBody] TalearrKindleConfigResource resource)
        {
            var saved = _settingsService.Save(FromResource(resource));

            return Accepted(ToResource(saved));
        }

        // Checks the (possibly unsaved) settings: addresses, From, and that the SMTP server accepts
        // the login. Sends nothing.
        [HttpPost("test")]
        public TalearrKindleResult Test([FromBody] TalearrKindleConfigResource resource)
        {
            return _kindleService.Test(FromResource(resource));
        }

        // Email connections whose SMTP settings can be reused.
        [HttpGet("emailconnections")]
        public List<TalearrKindleEmailConnection> EmailConnections()
        {
            return _kindleService.EmailConnections();
        }

        private static TalearrKindleSettings FromResource(TalearrKindleConfigResource resource)
        {
            var defaults = TalearrKindleSettings.Default;

            return new TalearrKindleSettings
            {
                Enabled = resource?.Enabled ?? false,
                AutoSendOnImport = resource?.AutoSendOnImport ?? false,
                KindleAddresses = TalearrKindleSettings.CleanAddresses(resource?.KindleAddresses),
                EmailConnectionId = resource?.EmailConnectionId ?? 0,
                Server = resource?.Server ?? string.Empty,
                Port = resource == null || resource.Port == 0 ? defaults.Port : resource.Port,
                RequireEncryption = resource?.RequireEncryption ?? defaults.RequireEncryption,
                Username = resource?.Username ?? string.Empty,
                Password = resource?.Password ?? string.Empty,
                From = resource?.From ?? string.Empty
            };
        }

        private static TalearrKindleConfigResource ToResource(TalearrKindleSettings settings)
        {
            return new TalearrKindleConfigResource
            {
                Id = 1,
                Enabled = settings.Enabled,
                AutoSendOnImport = settings.AutoSendOnImport,
                KindleAddresses = settings.KindleAddresses,
                EmailConnectionId = settings.EmailConnectionId,
                Server = settings.Server,
                Port = settings.Port,
                RequireEncryption = settings.RequireEncryption,
                Username = settings.Username,
                Password = string.IsNullOrEmpty(settings.Password) ? string.Empty : TalearrKindleSettings.MaskedPassword,
                From = settings.From
            };
        }
    }
}
