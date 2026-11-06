using System;
using System.Linq;
using System.Reflection;
using DryIoc;
using NLog;
using NUnit.Framework;
using NzbDrone.Common.Composition.Extensions;
using NzbDrone.Core.Download;
using NzbDrone.Core.MediaFiles;
using NzbDrone.Core.MediaFiles.BookImport.Manual;

namespace Chaptarr.Core.Test.Talearr.Import
{
    /// <summary>
    /// The Talearr dependencies of these services are optional constructor parameters. The app
    /// container must still fill them, or the hardlink monitor, the seeding-safe setting and the
    /// manual import language warning silently do nothing.
    /// </summary>
    [TestFixture]
    public class TalearrImportContainerFixture
    {
        [TestCase(typeof(BookFileMovingService), "_talearrHardlinkMonitor")]
        [TestCase(typeof(BookFileMovingService), "_rootFolderService")]
        [TestCase(typeof(DownloadImportModeResolver), "_configService")]
        [TestCase(typeof(ManualImportService), "_fileLanguageVerifier")]
        public void should_fill_the_optional_talearr_dependency(Type serviceType, string field)
        {
            var container = new Container(rules => rules.WithNzbDroneRules());
            var constructor = serviceType.GetConstructors().OrderByDescending(c => c.GetParameters().Length).First();

            foreach (var parameter in constructor.GetParameters())
            {
                var type = parameter.ParameterType;
                if (type == typeof(Logger))
                {
                    container.RegisterInstance(LogManager.GetCurrentClassLogger());
                    continue;
                }

                var proxy = typeof(DispatchProxy)
                    .GetMethod(nameof(DispatchProxy.Create), 2, Type.EmptyTypes)
                    .MakeGenericMethod(type, typeof(ThrowingProxy))
                    .Invoke(null, null);

                container.RegisterInstance(type, proxy);
            }

            container.Register(serviceType);

            var service = container.Resolve(serviceType);
            var value = serviceType.GetField(field, BindingFlags.Instance | BindingFlags.NonPublic).GetValue(service);

            Assert.That(value, Is.Not.Null);
        }

        public class ThrowingProxy : DispatchProxy
        {
            protected override object Invoke(MethodInfo targetMethod, object[] args)
            {
                throw new NotImplementedException(targetMethod?.Name);
            }
        }
    }
}
