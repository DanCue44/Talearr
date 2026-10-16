using System;
using System.Linq;
using System.Reflection;
using DryIoc;
using NLog;
using NUnit.Framework;
using NzbDrone.Common.Composition.Extensions;
using NzbDrone.Core.MediaFiles;
using NzbDrone.Core.MediaFiles.TalearrImport;

namespace Chaptarr.Core.Test.MediaFiles.TalearrImport
{
    /// <summary>
    /// DownloadedBooksImportService keeps the upstream constructor next to the TaleArr one. The app
    /// container must pick the TaleArr constructor, or archives and the language check are skipped.
    /// </summary>
    [TestFixture]
    public class DownloadedBooksImportServiceContainerFixture
    {
        [Test]
        public void should_build_the_service_with_the_archive_unpacker_and_language_check()
        {
            var container = new Container(rules => rules.WithNzbDroneRules());
            var talearrConstructor = typeof(DownloadedBooksImportService)
                .GetConstructors()
                .OrderByDescending(c => c.GetParameters().Length)
                .First();

            foreach (var parameter in talearrConstructor.GetParameters())
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

            container.Register<DownloadedBooksImportService>();

            var service = container.Resolve<DownloadedBooksImportService>();

            Assert.That(GetField<IDownloadArchiveUnpacker>(service, "_archiveUnpacker"), Is.Not.Null);
            Assert.That(GetField<IDownloadedFileLanguageVerifier>(service, "_fileLanguageVerifier"), Is.Not.Null);
        }

        private static T GetField<T>(object target, string name)
        {
            return (T)target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic).GetValue(target);
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
