using System;
using Chaptarr.Http;
using Chaptarr.Http.REST;
using FluentValidation;
using Microsoft.AspNetCore.Mvc;
using NzbDrone.Core.Jobs;
using NzbDrone.Core.Talearr.MissingSearch;
using NzbDrone.Http.REST.Attributes;

namespace Chaptarr.Api.V1.Config
{
    // Talearr: scheduled missing-books search settings. A new route; no existing route or field
    // changes. Values are stored as talearr.missingsearch.* rows in the Config table.
    public class TalearrMissingSearchConfigResource : RestResource
    {
        // off | weekly | daily
        public string Schedule { get; set; }
        public int BooksPerRun { get; set; }
        public int StaggerSeconds { get; set; }

        // Read only: when the scheduled task runs next, null while the schedule is off.
        public DateTime? NextRun { get; set; }
    }

    [V1ApiController("config/talearrmissingsearch")]
    public class TalearrMissingSearchConfigController : RestController<TalearrMissingSearchConfigResource>
    {
        private readonly ITalearrMissingSearchSettingsService _settingsService;
        private readonly ITaskManager _taskManager;

        public TalearrMissingSearchConfigController(ITalearrMissingSearchSettingsService settingsService, ITaskManager taskManager)
        {
            _settingsService = settingsService;
            _taskManager = taskManager;

            SharedValidator.RuleFor(c => c.Schedule)
                .Must(value => TryParseSchedule(value, out _))
                .WithMessage("schedule must be off, weekly or daily");
            SharedValidator.RuleFor(c => c.BooksPerRun)
                .InclusiveBetween(1, TalearrMissingSearchSettings.MaxBooksPerRun);
            SharedValidator.RuleFor(c => c.StaggerSeconds)
                .InclusiveBetween(0, TalearrMissingSearchSettings.MaxStaggerSeconds);
        }

        protected override TalearrMissingSearchConfigResource GetResourceById(int id)
        {
            return GetConfig();
        }

        [HttpGet]
        public TalearrMissingSearchConfigResource GetConfig()
        {
            return ToResource(_settingsService.Get());
        }

        [RestPutById]
        public ActionResult<TalearrMissingSearchConfigResource> SaveConfig([FromBody] TalearrMissingSearchConfigResource resource)
        {
            TryParseSchedule(resource.Schedule, out var schedule);

            var saved = _settingsService.Save(new TalearrMissingSearchSettings
            {
                Schedule = schedule,
                BooksPerRun = resource.BooksPerRun,
                StaggerSeconds = resource.StaggerSeconds
            });

            return Accepted(ToResource(saved));
        }

        private static bool TryParseSchedule(string value, out MissingSearchSchedule schedule)
        {
            return Enum.TryParse(value?.Trim(), true, out schedule) && Enum.IsDefined(typeof(MissingSearchSchedule), schedule);
        }

        private TalearrMissingSearchConfigResource ToResource(TalearrMissingSearchSettings settings)
        {
            DateTime? nextRun = null;
            if (settings.IntervalMinutes > 0)
            {
                try
                {
                    nextRun = _taskManager.GetNextExecution(typeof(ScheduledMissingBookSearchCommand));
                }
                catch (Exception)
                {
                    nextRun = null;
                }
            }

            return new TalearrMissingSearchConfigResource
            {
                Id = 1,
                Schedule = settings.Schedule.ToString().ToLowerInvariant(),
                BooksPerRun = settings.EffectiveBooksPerRun,
                StaggerSeconds = settings.EffectiveStaggerSeconds,
                NextRun = nextRun
            };
        }
    }
}
