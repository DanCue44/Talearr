using System.Collections.Generic;
using System.Linq;
using System.Security.Claims;
using System.Text.Encodings.Web;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NzbDrone.Common.EnvironmentInfo;
using NzbDrone.Core.Authentication;
using NzbDrone.Core.Configuration;
using NzbDrone.Core.Talearr.EbookDelivery;

namespace Chaptarr.Http.Authentication
{
    // Talearr: authentication for the OPDS feed (/opds). Readers such as KOReader only speak HTTP
    // Basic or a URL with ?apikey=, so the feed gets its own scheme; the rules are in
    // TalearrOpdsAccess. A turned-off feed answers 404 instead of asking for a login.
    public class TalearrOpdsAuthenticationHandler : AuthenticationHandler<AuthenticationSchemeOptions>
    {
        public const string SchemeName = "TalearrOpds";
        private const string ResultKey = "TalearrOpdsAccess";

        private readonly IConfigFileProvider _configFileProvider;
        private readonly IAuthenticationService _authService;
        private readonly ITalearrOpdsSettingsService _settingsService;

        public TalearrOpdsAuthenticationHandler(IOptionsMonitor<AuthenticationSchemeOptions> options,
            ILoggerFactory logger,
            UrlEncoder encoder,
            IConfigFileProvider configFileProvider,
            IAuthenticationService authService,
            ITalearrOpdsSettingsService settingsService)
            : base(options, logger, encoder)
        {
            _configFileProvider = configFileProvider;
            _authService = authService;
            _settingsService = settingsService;
        }

        protected override Task<AuthenticateResult> HandleAuthenticateAsync()
        {
            var providedKey = Request.Headers["X-Api-Key"].FirstOrDefault();
            if (string.IsNullOrWhiteSpace(providedKey))
            {
                providedKey = Request.Query["apikey"].FirstOrDefault();
            }

            string user = null;
            string password = null;
            TalearrOpdsAccess.TryParseBasic(Request.Headers["Authorization"].FirstOrDefault(), out user, out password);

            var localWithoutLogin = _configFileProvider.AuthenticationRequired == AuthenticationRequiredType.DisabledForLocalAddresses &&
                                    TrustedNetworkPolicy.IsLocalOrTrusted(Context, _configFileProvider);

            var result = TalearrOpdsAccess.Decide(_settingsService.Get().Enabled,
                _configFileProvider.ApiKey,
                providedKey,
                user,
                password,
                localWithoutLogin,
                (u, p) => _authService.Login(Request, u, p) != null);

            Context.Items[ResultKey] = result;

            switch (result)
            {
                case TalearrOpdsAccessResult.Allowed:
                    var claims = new List<Claim>
                    {
                        new Claim("user", string.IsNullOrEmpty(user) ? "OPDS" : user),
                        new Claim("AuthType", SchemeName)
                    };

                    var identity = new ClaimsIdentity(claims, SchemeName, "user", "identifier");
                    return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(identity), SchemeName)));

                case TalearrOpdsAccessResult.Rejected:
                    return Task.FromResult(AuthenticateResult.Fail("The OPDS credentials are not correct."));

                default:
                    return Task.FromResult(AuthenticateResult.NoResult());
            }
        }

        protected override Task HandleChallengeAsync(AuthenticationProperties properties)
        {
            if (Context.Items.TryGetValue(ResultKey, out var value) && value is TalearrOpdsAccessResult.Disabled)
            {
                Response.StatusCode = 404;
                return Task.CompletedTask;
            }

            Response.Headers["WWW-Authenticate"] = $"Basic realm=\"{BuildInfo.ProductName} OPDS\", charset=\"UTF-8\"";
            Response.StatusCode = 401;
            return Task.CompletedTask;
        }

        protected override Task HandleForbiddenAsync(AuthenticationProperties properties)
        {
            Response.StatusCode = 403;
            return Task.CompletedTask;
        }
    }
}
