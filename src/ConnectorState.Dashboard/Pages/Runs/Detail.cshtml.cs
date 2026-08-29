// ---------------------------------------------------------------------------
// Runs/Detail.cshtml.cs
// One run, in full.
//
// It calls crawl.uspGetRun, which returns FOUR result sets in one call: the run
// header, the per-item-type breakdown, the timing attribution and the throttle
// summary. This page shows all four, and four round trips for one page is three
// too many - which is exactly why the procedure was written that way rather than
// as four procedures a page composes.
//
// The three tables answer three different questions, and the reason they are on
// one page is that an operator asking any of them usually needs the next:
//
//   PER ITEM TYPE answers "what did this run actually do". crawl.Run says a run
//   wrote 1,118 items; this says it wrote 12 customers, 62 engagements and 1,044
//   time entries, and that the customers were all unchanged while every time
//   entry was rewritten. The second sentence is the one that tells you whether
//   something changed in the source or in the connector.
//
//   TIMING answers "where did the time go", and persisting it per run is what
//   makes "is this getting worse" a comparison rather than a recollection. The
//   percentiles are there for the reason PushTiming.cs gives: one row that waited
//   sixty seconds behind a Retry-After moves a mean and tells you nothing about
//   the other thousand.
//
//   THROTTLING is the aggregate. The raw events are a separate page because a
//   badly throttled run has thousands of them.
//
// An unknown run id returns 404 rather than an empty page. A dashboard that
// renders a blank run for a mistyped URL is a dashboard somebody concludes the
// run was purged from.
// ---------------------------------------------------------------------------

namespace ConnectorState.Dashboard.Pages.Runs;

using ConnectorState.Dashboard.Data;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

/// <summary>One run's detail page.</summary>
public sealed class DetailModel : PageModel
{
    private readonly CrawlStateQueries queries;

    /// <summary>Initializes a new instance of the <see cref="DetailModel"/> class.</summary>
    /// <param name="queries">The read surface.</param>
    public DetailModel(CrawlStateQueries queries)
    {
        this.queries = queries;
    }

    /// <summary>Gets the run header. Never null once OnGetAsync has returned a page.</summary>
    public RunHistoryRow Run { get; private set; } = default!;

    /// <summary>Gets what the run did, per kind of item.</summary>
    public IReadOnlyList<RunItemTypeRow> ByItemType { get; private set; } = Array.Empty<RunItemTypeRow>();

    /// <summary>Gets where the run's time went.</summary>
    public IReadOnlyList<RunPhaseTimingRow> Timing { get; private set; } = Array.Empty<RunPhaseTimingRow>();

    /// <summary>Gets the throttle aggregate, or null for a run that was never throttled.</summary>
    public ThrottleSummaryRow? Throttling { get; private set; }

    /// <summary>Reads the run.</summary>
    /// <param name="runId">The run to show.</param>
    /// <returns>The page, or 404 when no such run exists.</returns>
    public async Task<IActionResult> OnGetAsync(long runId)
    {
        RunDetail detail = await this.queries.GetRunAsync(runId, this.HttpContext.RequestAborted);

        if (detail.Run is null)
        {
            return this.NotFound();
        }

        this.Run = detail.Run;
        this.ByItemType = detail.ByItemType;
        this.Timing = detail.Timing;
        this.Throttling = detail.Throttling;

        return this.Page();
    }
}
