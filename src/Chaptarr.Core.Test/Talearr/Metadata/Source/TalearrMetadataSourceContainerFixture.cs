using System;
using System.Reflection;
using DryIoc;
using NLog;
using NUnit.Framework;
using NzbDrone.Common.Composition.Extensions;
using NzbDrone.Common.Http;
using NzbDrone.Core.Configuration;
using NzbDrone.Core.Talearr.Metadata.Source;

namespace Chaptarr.Core.Test.Talearr.Metadata.Source
{
    // The app container picks the public constructor; the client's second (test) constructor is
    // internal and must not confuse it.
    [TestFixture]
    public class TalearrMetadataSourceContainerFixture
    {
        [Test]
        public void container_builds_the_hardcover_client_with_its_public_constructor()
        {
            var container = new Container(rules => rules.WithNzbDroneRules());
            container.RegisterInstance(LogManager.GetCurrentClassLogger());
            container.RegisterInstance<IHttpClient>(new ScriptedHttpClient(_ => throw new InvalidOperationException("no HTTP")));
            container.RegisterInstance<IConfigService>(global::Chaptarr.Core.Test.Talearr.MetadataFallback.HardcoverConfigProxy.Create());
            container.Register<ITalearrHardcoverMetadataClient, TalearrHardcoverMetadataClient>();

            var client = container.Resolve<ITalearrHardcoverMetadataClient>();

            Assert.That(client.IsConfigured, Is.True);
            Assert.That(typeof(TalearrHardcoverMetadataClient).GetConstructors(BindingFlags.Public | BindingFlags.Instance).Length, Is.EqualTo(1));
        }

        [Test]
        public void container_builds_the_source_payloads_with_their_public_constructor()
        {
            var container = new Container(rules => rules.WithNzbDroneRules());
            container.RegisterInstance(LogManager.GetCurrentClassLogger());
            container.RegisterInstance<ITalearrMetadataSourceSettingsService>(new FixedSource());
            container.RegisterInstance<ITalearrHardcoverMetadataClient>(new TalearrHardcoverMetadataClient(
                new ScriptedHttpClient(_ => throw new InvalidOperationException("no HTTP")),
                global::Chaptarr.Core.Test.Talearr.MetadataFallback.HardcoverConfigProxy.Create(),
                LogManager.GetCurrentClassLogger()));
            container.RegisterInstance<ITalearrSourceGapFill>(new TalearrSourceGapFill(null, LogManager.GetCurrentClassLogger()));
            container.RegisterInstance<ITalearrAudibleFill>(new TalearrAudibleFill(null, null, LogManager.GetCurrentClassLogger()));
            container.Register<ITalearrSourcePayloads, TalearrSourcePayloads>();

            Assert.That(container.Resolve<ITalearrSourcePayloads>().IsActive, Is.True);
            Assert.That(typeof(TalearrSourcePayloads).GetConstructors(BindingFlags.Public | BindingFlags.Instance).Length, Is.EqualTo(1));
        }

        private sealed class FixedSource : ITalearrMetadataSourceSettingsService
        {
            public TalearrMetadataSource GetSource() => TalearrMetadataSource.Talearr;
            public TalearrMetadataSource SaveSource(TalearrMetadataSource source) => source;
        }
    }
}
