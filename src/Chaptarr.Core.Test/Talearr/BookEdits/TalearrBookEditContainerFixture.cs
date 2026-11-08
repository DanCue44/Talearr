using System;
using System.Linq;
using System.Reflection;
using Chaptarr.Core.Test.Talearr.Import;
using DryIoc;
using NLog;
using NUnit.Framework;
using NzbDrone.Common.Composition.Extensions;
using NzbDrone.Core.Books;

namespace Chaptarr.Core.Test.Talearr.BookEdits
{
    /// <summary>
    /// The refresh services take the book edit service as an optional constructor parameter. The
    /// app container must fill it, or a refresh silently overwrites the user's edits.
    /// </summary>
    [TestFixture]
    public class TalearrBookEditContainerFixture
    {
        [TestCase(typeof(RefreshBookService))]
        [TestCase(typeof(RefreshSeriesService))]
        public void should_fill_the_book_edit_dependency(Type serviceType)
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
                    .MakeGenericMethod(type, typeof(TalearrImportContainerFixture.ThrowingProxy))
                    .Invoke(null, null);

                container.RegisterInstance(type, proxy);
            }

            container.Register(serviceType);

            var service = container.Resolve(serviceType);
            var value = serviceType.GetField("_bookEdits", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(service);

            Assert.That(value, Is.Not.Null);
        }
    }
}
