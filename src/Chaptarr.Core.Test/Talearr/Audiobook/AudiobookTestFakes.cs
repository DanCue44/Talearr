using System;
using System.Collections.Generic;
using System.Reflection;

namespace Chaptarr.Core.Test.Talearr.Audiobook
{
    // Minimal interface fake: answer chosen members by name, record every call, throw for the rest
    // so a test notices when the code under test starts depending on something new.
    public class Fake<T> : DispatchProxy
        where T : class
    {
        public Dictionary<string, Func<object[], object>> Handlers { get; } = new Dictionary<string, Func<object[], object>>();
        public List<(string Name, object[] Args)> Calls { get; } = new List<(string, object[])>();

        public static (T Instance, Fake<T> Fake) Create()
        {
            var proxy = Create<T, Fake<T>>();
            return (proxy, (Fake<T>)(object)proxy);
        }

        public Fake<T> On(string member, Func<object[], object> handler)
        {
            Handlers[member] = handler;
            return this;
        }

        protected override object Invoke(MethodInfo targetMethod, object[] args)
        {
            var name = targetMethod?.Name ?? string.Empty;
            Calls.Add((name, args));

            if (Handlers.TryGetValue(name, out var handler))
            {
                return handler(args);
            }

            if (targetMethod?.ReturnType == typeof(void))
            {
                return null;
            }

            throw new NotImplementedException($"Fake<{typeof(T).Name}> does not implement {name}");
        }
    }
}
