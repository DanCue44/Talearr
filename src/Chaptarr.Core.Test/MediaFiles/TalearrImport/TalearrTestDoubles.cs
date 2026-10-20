using System;
using System.Collections.Generic;
using System.Reflection;
using NzbDrone.Common.Disk;
using NzbDrone.Core.Configuration;

namespace Chaptarr.Core.Test.MediaFiles.TalearrImport
{
    /// <summary>
    /// IConfigService stand-in for the Talearr import settings. Anything else throws so a test
    /// notices when code under test starts depending on another setting.
    /// </summary>
    public class TalearrConfigProxy : DispatchProxy
    {
        public bool UnpackArchives { get; set; } = true;
        public bool VerifyFileLanguage { get; set; } = true;

        // Other getters the code under test may read, by property name.
        public Dictionary<string, object> Values { get; } = new Dictionary<string, object>
        {
            [nameof(IConfigService.AutoAddMissingAuthorsFromCompletedDownloads)] = false,
            [nameof(IConfigService.ImportExtraFiles)] = false
        };

        public static IConfigService Create(bool unpackArchives = true, bool verifyFileLanguage = true)
        {
            var proxy = Create<IConfigService, TalearrConfigProxy>();
            var state = (TalearrConfigProxy)(object)proxy;
            state.UnpackArchives = unpackArchives;
            state.VerifyFileLanguage = verifyFileLanguage;
            return proxy;
        }

        protected override object Invoke(MethodInfo targetMethod, object[] args)
        {
            switch (targetMethod?.Name)
            {
                case "get_" + nameof(IConfigService.TalearrUnpackArchives):
                    return UnpackArchives;
                case "get_" + nameof(IConfigService.TalearrVerifyFileLanguage):
                    return VerifyFileLanguage;
                default:
                    if (targetMethod?.Name?.StartsWith("get_") == true && Values.TryGetValue(targetMethod.Name.Substring(4), out var value))
                    {
                        return value;
                    }

                    throw new NotImplementedException($"Test proxy does not implement IConfigService.{targetMethod?.Name}");
            }
        }
    }

    /// <summary>
    /// IDiskProvider stand-in that only answers the free-space question.
    /// </summary>
    public class FreeSpaceDiskProviderProxy : DispatchProxy
    {
        public long? AvailableSpace { get; set; }
        public List<string> FreeSpaceQueries { get; } = new List<string>();

        public static IDiskProvider Create(long? availableSpace = null)
        {
            var proxy = Create<IDiskProvider, FreeSpaceDiskProviderProxy>();
            ((FreeSpaceDiskProviderProxy)(object)proxy).AvailableSpace = availableSpace;
            return proxy;
        }

        protected override object Invoke(MethodInfo targetMethod, object[] args)
        {
            if (targetMethod?.Name == nameof(IDiskProvider.GetAvailableSpace))
            {
                FreeSpaceQueries.Add((string)args[0]);
                return AvailableSpace;
            }

            throw new NotImplementedException($"Test proxy does not implement IDiskProvider.{targetMethod?.Name}");
        }
    }
}
