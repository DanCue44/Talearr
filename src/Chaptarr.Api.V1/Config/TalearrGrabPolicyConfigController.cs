using Chaptarr.Http;
using Chaptarr.Http.REST;
using FluentValidation;
using Microsoft.AspNetCore.Mvc;
using NzbDrone.Core.Talearr.GrabPolicy;
using NzbDrone.Http.REST.Attributes;

namespace Chaptarr.Api.V1.Config
{
    // Talearr: grab policy settings (abridged audiobooks, bundles, automatic retry, re-search after failed grabs). A new
    // route; no existing route or field changes. Values are stored by TalearrGrabPolicyService
    // as talearr.grabpolicy.* rows in the Config table, which Chaptarr ignores.
    public class TalearrGrabPolicyConfigResource : RestResource
    {
        public AbridgedAudiobookPolicy AbridgedPolicy { get; set; }
        public bool AllowBundles { get; set; }
        public bool FailRejectedImports { get; set; }
        public int MaxAutomaticRetries { get; set; }

        // Optional on PUT: a client that does not send it keeps the stored value.
        public bool? RetryFailedGrabs { get; set; }
    }

    [V1ApiController("config/talearrgrabpolicy")]
    public class TalearrGrabPolicyConfigController : RestController<TalearrGrabPolicyConfigResource>
    {
        private readonly ITalearrGrabPolicyService _grabPolicyService;

        public TalearrGrabPolicyConfigController(ITalearrGrabPolicyService grabPolicyService)
        {
            _grabPolicyService = grabPolicyService;

            SharedValidator.RuleFor(c => c.MaxAutomaticRetries)
                .InclusiveBetween(1, TalearrGrabPolicy.MaxAutomaticRetriesLimit);
        }

        protected override TalearrGrabPolicyConfigResource GetResourceById(int id)
        {
            return GetConfig();
        }

        [HttpGet]
        public TalearrGrabPolicyConfigResource GetConfig()
        {
            return ToResource(_grabPolicyService.Get());
        }

        [RestPutById]
        public ActionResult<TalearrGrabPolicyConfigResource> SaveConfig([FromBody] TalearrGrabPolicyConfigResource resource)
        {
            var saved = _grabPolicyService.Save(new TalearrGrabPolicy
            {
                AbridgedPolicy = resource.AbridgedPolicy,
                AllowBundles = resource.AllowBundles,
                FailRejectedImports = resource.FailRejectedImports,
                MaxAutomaticRetries = resource.MaxAutomaticRetries,
                RetryFailedGrabs = resource.RetryFailedGrabs ?? _grabPolicyService.Get().RetryFailedGrabs
            });

            return Accepted(ToResource(saved));
        }

        private static TalearrGrabPolicyConfigResource ToResource(TalearrGrabPolicy policy)
        {
            return new TalearrGrabPolicyConfigResource
            {
                Id = 1,
                AbridgedPolicy = policy.AbridgedPolicy,
                AllowBundles = policy.AllowBundles,
                FailRejectedImports = policy.FailRejectedImports,
                MaxAutomaticRetries = policy.EffectiveMaxAutomaticRetries,
                RetryFailedGrabs = policy.RetryFailedGrabs
            };
        }
    }
}
