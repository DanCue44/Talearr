using System;
using System.Collections.Generic;
using System.Linq;
using Chaptarr.Http;
using Microsoft.AspNetCore.Mvc;
using NzbDrone.Core.Talearr.SeriesMonitoring;

namespace Chaptarr.Api.V1.Talearr
{
    public class TalearrSeriesRuleResource
    {
        public bool Audiobook { get; set; }
        public bool Ebook { get; set; }
        public bool SearchOnAdd { get; set; }
        public bool IncludeExtras { get; set; }
        public DateTime AddedAt { get; set; }
        public DateTime? LastAppliedAt { get; set; }
        public string LastResult { get; set; }
    }

    public class TalearrSeriesSummaryResource
    {
        public int SeriesId { get; set; }
        public List<int> SeriesIds { get; set; }
        public string SeriesKey { get; set; }
        public string Title { get; set; }
        public List<string> Authors { get; set; }
        public int BookCount { get; set; }
        public int AudiobookFiles { get; set; }
        public int EbookFiles { get; set; }

        // Null when the series is not monitored.
        public TalearrSeriesRuleResource Rule { get; set; }
    }

    public class TalearrSeriesDetailResource
    {
        public TalearrSeriesSummaryResource Series { get; set; }
        public string Description { get; set; }
        public bool AllowBundles { get; set; }
        public List<TalearrSeriesEntry> Entries { get; set; }
    }

    // TaleArr: series monitoring under /api/v1/talearr/series (docs/TALEARR_SERIES.md). New routes
    // only; upstream's /api/v1/series is unchanged.
    [V1ApiController("talearr/series")]
    public class TalearrSeriesController : Controller
    {
        private readonly ITalearrSeriesMonitorService _seriesMonitorService;

        public TalearrSeriesController(ITalearrSeriesMonitorService seriesMonitorService)
        {
            _seriesMonitorService = seriesMonitorService;
        }

        // Every series in the library (one entry per series, whatever its media type rows), or
        // only the monitored ones.
        [HttpGet]
        public List<TalearrSeriesSummaryResource> GetSeries(bool monitoredOnly = false)
        {
            return _seriesMonitorService.GetSeries(monitoredOnly).Select(ToResource).ToList();
        }

        // seriesId may be either media type row of the series.
        [HttpGet("{seriesId:int}")]
        public ActionResult<TalearrSeriesDetailResource> GetSeriesDetail(int seriesId)
        {
            var detail = _seriesMonitorService.GetDetail(seriesId);
            return detail == null ? NotFound() : ToResource(detail);
        }

        // {"audiobook": true, "ebook": false, "searchOnAdd": true, "includeExtras": false}.
        // Both formats false stops monitoring the series; books keep their monitoring.
        [HttpPut("{seriesId:int}")]
        public ActionResult<TalearrSeriesDetailResource> SetRule(int seriesId, [FromBody] TalearrSeriesRuleRequest request)
        {
            var detail = _seriesMonitorService.SetRule(seriesId, request ?? new TalearrSeriesRuleRequest());
            return detail == null ? NotFound() : ToResource(detail);
        }

        // Applies the rule now (new books since the last run), as the scheduled task does.
        [HttpPost("{seriesId:int}/apply")]
        public ActionResult<TalearrSeriesDetailResource> ApplyRule(int seriesId)
        {
            var detail = _seriesMonitorService.GetDetail(seriesId);
            if (detail == null)
            {
                return NotFound();
            }

            if (detail.Series.Rule == null)
            {
                return Conflict(new { message = "This series is not monitored." });
            }

            _seriesMonitorService.Apply(seriesId);
            return ToResource(_seriesMonitorService.GetDetail(seriesId));
        }

        // {"format": "audiobook"}: turns the format on for the series' authors whose format side is
        // off, so monitored series books are searched. No other book of the author is monitored.
        [HttpPost("{seriesId:int}/authors")]
        public ActionResult<TalearrSeriesDetailResource> EnableAuthors(int seriesId, [FromBody] TalearrSeriesAuthorsRequest request)
        {
            var format = request?.Format;
            if (!string.Equals(format, TalearrSeriesFormats.Audiobook, StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(format, TalearrSeriesFormats.Ebook, StringComparison.OrdinalIgnoreCase))
            {
                return BadRequest(new { message = "format must be audiobook or ebook" });
            }

            var detail = _seriesMonitorService.EnableAuthors(seriesId, format);
            return detail == null ? NotFound() : ToResource(detail);
        }

        private static TalearrSeriesDetailResource ToResource(TalearrSeriesDetail detail)
        {
            return new TalearrSeriesDetailResource
            {
                Series = ToResource(detail.Series),
                Description = detail.Description,
                AllowBundles = detail.AllowBundles,
                Entries = detail.Entries
            };
        }

        private static TalearrSeriesSummaryResource ToResource(TalearrSeriesSummary summary)
        {
            var rule = summary.Rule;
            return new TalearrSeriesSummaryResource
            {
                SeriesId = summary.SeriesId,
                SeriesIds = summary.SeriesIds,
                SeriesKey = summary.SeriesKey,
                Title = summary.Title,
                Authors = summary.Authors,
                BookCount = summary.BookCount,
                AudiobookFiles = summary.AudiobookFiles,
                EbookFiles = summary.EbookFiles,
                Rule = rule == null
                    ? null
                    : new TalearrSeriesRuleResource
                    {
                        Audiobook = rule.Audiobook,
                        Ebook = rule.Ebook,
                        SearchOnAdd = rule.SearchOnAdd,
                        IncludeExtras = rule.IncludeExtras,
                        AddedAt = rule.AddedAt,
                        LastAppliedAt = rule.LastAppliedAt,
                        LastResult = rule.LastResult
                    }
            };
        }
    }
}
