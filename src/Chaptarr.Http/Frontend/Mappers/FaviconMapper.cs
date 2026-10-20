using System.IO;
using NLog;
using NzbDrone.Common.Disk;
using NzbDrone.Common.EnvironmentInfo;
using NzbDrone.Core.Configuration;

namespace Chaptarr.Http.Frontend.Mappers
{
    public class FaviconMapper : StaticResourceMapperBase
    {
        private readonly IAppFolderInfo _appFolderInfo;
        private readonly IConfigFileProvider _configFileProvider;

        public FaviconMapper(IAppFolderInfo appFolderInfo, IDiskProvider diskProvider, IConfigFileProvider configFileProvider, Logger logger)
            : base(diskProvider, logger)
        {
            _appFolderInfo = appFolderInfo;
            _configFileProvider = configFileProvider;
        }

        public override string Map(string resourceUrl)
        {
            // Talearr: serve the real transparent ICO. Safari falls back to /favicon.ico when it
            // cannot use the SVG tab icon, and an SVG here showed up as a blank white tile.
            var path = Path.Combine("Content", "Images", "Icons", "favicon.ico");

            return Path.Combine(_appFolderInfo.StartUpFolder, _configFileProvider.UiFolder, path);
        }

        protected override string GetAllowedRoot(string resourceUrl)
        {
            return Path.Combine(_appFolderInfo.StartUpFolder, _configFileProvider.UiFolder);
        }

        public override bool CanHandle(string resourceUrl)
        {
            return resourceUrl.Equals("/favicon.ico");
        }
    }
}
