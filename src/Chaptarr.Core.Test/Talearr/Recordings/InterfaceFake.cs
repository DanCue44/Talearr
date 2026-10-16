using System;
using System.Collections.Generic;
using System.Reflection;

namespace Chaptarr.Core.Test.Talearr.Recordings
{
    // Fake for wide upstream interfaces: answer only the methods a test needs, by name.
    internal class InterfaceFake<T> : DispatchProxy
        where T : class
    {
        public Dictionary<string, Func<object[], object>> Handlers { get; } = new Dictionary<string, Func<object[], object>>();
        public List<string> Calls { get; } = new List<string>();

        public static (T Instance, InterfaceFake<T> Fake) Create()
        {
            var instance = DispatchProxy.Create<T, InterfaceFake<T>>();
            return (instance, (InterfaceFake<T>)(object)instance);
        }

        public InterfaceFake<T> On(string method, Func<object[], object> handler)
        {
            Handlers[method] = handler;
            return this;
        }

        protected override object Invoke(MethodInfo targetMethod, object[] args)
        {
            Calls.Add(targetMethod.Name);
            if (Handlers.TryGetValue(targetMethod.Name, out var handler))
            {
                return handler(args);
            }

            throw new NotImplementedException($"{typeof(T).Name}.{targetMethod.Name} is not faked in this test");
        }
    }
}
