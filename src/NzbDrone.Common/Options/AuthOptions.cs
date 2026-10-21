using System.Collections.Generic;

namespace NzbDrone.Common.Options;

public class AuthOptions
{
    public string ApiKey { get; set; }
    public bool? Enabled { get; set; }
    public string Method { get; set; }
    public string Required { get; set; }
    public bool? TrustCgnatIpAddresses { get; set; }

    // Talearr: OpenID Connect from the environment (Chaptarr #318), e.g. Talearr__Auth__OidcClientId.
    public string OidcAuthority { get; set; }
    public string OidcClientId { get; set; }
    public string OidcClientSecret { get; set; }
    public string OidcClientSecretFile { get; set; }
    public string OidcScopes { get; set; }
    public string OidcAllowedEmails { get; set; }
    public string OidcAllowedEmailDomains { get; set; }
    public bool? OidcRequireEmailVerified { get; set; }
    public bool? OidcAllowAnyVerifiedUser { get; set; }

    // Names of the OIDC settings that the environment sets, for the UI to show as locked.
    public List<string> GetOidcEnvironmentFields()
    {
        var fields = new List<string>();

        void Add(bool isSet, string name)
        {
            if (isSet)
            {
                fields.Add(name);
            }
        }

        Add(!string.IsNullOrWhiteSpace(OidcAuthority), "oidcAuthority");
        Add(!string.IsNullOrWhiteSpace(OidcClientId), "oidcClientId");
        Add(!string.IsNullOrWhiteSpace(OidcClientSecret) || !string.IsNullOrWhiteSpace(OidcClientSecretFile), "oidcClientSecret");
        Add(!string.IsNullOrWhiteSpace(OidcScopes), "oidcScopes");
        Add(!string.IsNullOrWhiteSpace(OidcAllowedEmails), "oidcAllowedEmails");
        Add(!string.IsNullOrWhiteSpace(OidcAllowedEmailDomains), "oidcAllowedEmailDomains");
        Add(OidcRequireEmailVerified.HasValue, "oidcRequireEmailVerified");
        Add(OidcAllowAnyVerifiedUser.HasValue, "oidcAllowAnyVerifiedUser");

        return fields;
    }
}
