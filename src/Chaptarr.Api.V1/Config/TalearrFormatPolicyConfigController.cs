using System.Collections.Generic;
using System.Linq;
using Chaptarr.Http;
using Chaptarr.Http.REST;
using FluentValidation;
using Microsoft.AspNetCore.Mvc;
using NzbDrone.Core.Books;
using NzbDrone.Core.Talearr.FormatPolicy;
using NzbDrone.Http.REST.Attributes;

namespace Chaptarr.Api.V1.Config
{
    public class TalearrAuthorFormatPolicyResource
    {
        public int AuthorId { get; set; }
        public string AuthorName { get; set; }
        public FormatPolicyMode Policy { get; set; }
    }

    // Talearr: format policy (docs/TALEARR_FORMAT_POLICY.md). A new route; stored as
    // talearr.formatpolicy.* rows in the Config table, which Chaptarr ignores.
    public class TalearrFormatPolicyConfigResource : RestResource
    {
        public FormatPolicyMode Default { get; set; }
        public int FallbackHours { get; set; }
        public List<TalearrAuthorFormatPolicyResource> Authors { get; set; } = new List<TalearrAuthorFormatPolicyResource>();
    }

    [V1ApiController("config/talearrformatpolicy")]
    public class TalearrFormatPolicyConfigController : RestController<TalearrFormatPolicyConfigResource>
    {
        private readonly ITalearrFormatPolicyService _policyService;
        private readonly IAuthorService _authorService;

        public TalearrFormatPolicyConfigController(ITalearrFormatPolicyService policyService, IAuthorService authorService)
        {
            _policyService = policyService;
            _authorService = authorService;

            SharedValidator.RuleFor(c => c.FallbackHours).InclusiveBetween(0, TalearrFormatPolicySettings.MaxFallbackHours);
            SharedValidator.RuleFor(c => c.Default).IsInEnum();
        }

        protected override TalearrFormatPolicyConfigResource GetResourceById(int id)
        {
            return GetConfig();
        }

        [HttpGet]
        public TalearrFormatPolicyConfigResource GetConfig()
        {
            return ToResource(_policyService.Get());
        }

        // The policy that applies to one author (its override, else the default).
        [HttpGet("author/{authorId:int}")]
        public ActionResult<TalearrAuthorFormatPolicyResource> GetAuthorPolicy(int authorId)
        {
            var author = _authorService.GetAuthor(authorId);
            if (author == null)
            {
                return NotFound();
            }

            return new TalearrAuthorFormatPolicyResource
            {
                AuthorId = authorId,
                AuthorName = author.Name,
                Policy = _policyService.Get().For(authorId)
            };
        }

        [RestPutById]
        public ActionResult<TalearrFormatPolicyConfigResource> SaveConfig([FromBody] TalearrFormatPolicyConfigResource resource)
        {
            var saved = _policyService.Save(new TalearrFormatPolicySettings
            {
                Default = resource.Default,
                FallbackHours = resource.FallbackHours,
                Authors = (resource.Authors ?? new List<TalearrAuthorFormatPolicyResource>())
                    .Where(a => a.AuthorId > 0)
                    .GroupBy(a => a.AuthorId)
                    .ToDictionary(g => g.Key, g => g.Last().Policy)
            });

            return Accepted(ToResource(saved));
        }

        private TalearrFormatPolicyConfigResource ToResource(TalearrFormatPolicySettings settings)
        {
            var names = settings.Authors.Any()
                ? _authorService.GetAllAuthors().ToDictionary(a => a.Id, a => a.Name)
                : new Dictionary<int, string>();

            return new TalearrFormatPolicyConfigResource
            {
                Id = 1,
                Default = settings.Default,
                FallbackHours = settings.FallbackHours,
                Authors = settings.Authors
                    .Select(kv => new TalearrAuthorFormatPolicyResource
                    {
                        AuthorId = kv.Key,
                        AuthorName = names.TryGetValue(kv.Key, out var name) ? name : null,
                        Policy = kv.Value
                    })
                    .OrderBy(a => a.AuthorName)
                    .ToList()
            };
        }
    }
}
