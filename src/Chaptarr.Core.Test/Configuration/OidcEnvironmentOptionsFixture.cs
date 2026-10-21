using System.Collections.Generic;
using System.IO;
using Microsoft.Extensions.Configuration;
using NUnit.Framework;
using NzbDrone.Common.Options;
using NzbDrone.Core.Configuration;

namespace Chaptarr.Core.Test.Configuration
{
    // Chaptarr #318: OIDC settings from the environment, the way Bootstrap binds them.
    [TestFixture]
    public class OidcEnvironmentOptionsFixture
    {
        private static AuthOptions Bind(Dictionary<string, string> values)
        {
            var config = new ConfigurationBuilder().AddInMemoryCollection(values).Build();
            var options = new AuthOptions();
            config.GetSection("Chaptarr:Auth").Bind(options);
            config.GetSection("Talearr:Auth").Bind(options);

            return options;
        }

        [Test]
        public void should_bind_oidc_settings_and_let_talearr_variables_win()
        {
            var options = Bind(new Dictionary<string, string>
            {
                ["Chaptarr:Auth:OidcAuthority"] = "https://old.example.com/",
                ["Talearr:Auth:OidcAuthority"] = "https://sso.example.com/application/o/talearr/",
                ["Talearr:Auth:OidcClientId"] = "talearr",
                ["Chaptarr:Auth:OidcRequireEmailVerified"] = "false"
            });

            Assert.That(options.OidcAuthority, Is.EqualTo("https://sso.example.com/application/o/talearr/"));
            Assert.That(options.OidcClientId, Is.EqualTo("talearr"));
            Assert.That(options.OidcRequireEmailVerified, Is.False);
            Assert.That(options.GetOidcEnvironmentFields(), Is.EquivalentTo(new[] { "oidcAuthority", "oidcClientId", "oidcRequireEmailVerified" }));
        }

        [Test]
        public void should_report_nothing_when_the_environment_sets_no_oidc_values()
        {
            Assert.That(Bind(new Dictionary<string, string>()).GetOidcEnvironmentFields(), Is.Empty);
        }

        [Test]
        public void should_read_the_client_secret_from_a_file()
        {
            var file = Path.GetTempFileName();
            File.WriteAllText(file, "s3cret\n");

            try
            {
                var options = new AuthOptions { OidcClientSecretFile = file };

                Assert.That(ConfigFileProvider.ReadOidcClientSecret(options), Is.EqualTo("s3cret"));
                Assert.That(options.GetOidcEnvironmentFields(), Does.Contain("oidcClientSecret"));
            }
            finally
            {
                File.Delete(file);
            }
        }

        [Test]
        public void a_secret_value_wins_over_a_secret_file_and_none_means_config()
        {
            Assert.That(ConfigFileProvider.ReadOidcClientSecret(new AuthOptions { OidcClientSecret = "direct", OidcClientSecretFile = "/missing" }), Is.EqualTo("direct"));
            Assert.That(ConfigFileProvider.ReadOidcClientSecret(new AuthOptions()), Is.Null);
        }
    }
}
