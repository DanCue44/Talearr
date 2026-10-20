using Chaptarr.Http;
using Microsoft.AspNetCore.Mvc;
using NzbDrone.Core.Profiles.Metadata;
using NzbDrone.Core.Talearr.ContentFilter;

namespace Chaptarr.Api.V1.Profiles.Metadata
{
    [V1ApiController("metadataprofile/schema")]
    public class MetadataProfileSchemaController : Controller
    {
        [HttpGet]
        public MetadataProfileResource GetAll()
        {
            var profile = new MetadataProfile
            {
                AllowedLanguages = ""
            };

            var resource = profile.ToResource();

            // Talearr: new profiles start with no genre or content type rules.
            resource.TalearrContentFilter = new TalearrContentFilter();
            return resource;
        }
    }
}
