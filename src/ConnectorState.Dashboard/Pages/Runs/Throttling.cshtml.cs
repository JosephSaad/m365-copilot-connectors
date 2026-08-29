// ---------------------------------------------------------------------------
// Runs/Throttling.cshtml.cs
// The raw 429s and 5xx responses for one run, paged.
//
// This page exists because the aggregate on the run detail page raises questions
// it cannot answer. "Forty-one throttle events costing nine minutes" is a
// summary; whether those forty-one arrived in one ninety-second cluster or
// trickled across the whole run decides what to do about it, and only the
// individual timestamps say which.
//
// SECONDS INTO RUN IS THE COLUMN THIS PAGE IS FOR. Absolute timestamps make two
// runs incomparable; the offset from the run's own start makes the shape of the
// throttling visible - front-loaded means the first burst of writers is too
// wide, evenly spread means the sustained rate is above what the tenant allows,
// and a cluster at one point usually means something else was running.
//
// It is a SEPARATE page from the run detail, and sql/24 keeps the events out of
// uspGetRun for the same reason: a badly throttled run has thousands of these
// and the detail page wants the aggregate. Paging is uspListThrottleEvents's,
// with TotalRows carried on every row via COUNT(*) OVER().
//
// The run header comes from uspGetRun. That is a second round trip whose other
// three result sets this page discards, and it is worth it: the aggregate above
// the raw events is what turns a list of timestamps into a comparison. Without
// it an operator is reading forty-one rows with nothing to read them against.
//
// RETRY-AFTER IS NULLABLE AND THE NULL MEANS SOMETHING. It records that the
// service refused without saying for how long, which is when
// PushCore/GraphThrottling.cs falls back to its own exponential backoff. A run
// full of nulls is being backed off by a guess.
// ---------------------------------------------------------------------------

namespace ConnectorState.Dashboard.Pages.Runs;

using ConnectorState.Dashboard.Data;
using ConnectorState.Dashboard.Presentation;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

/// <summary>The raw throttle events for one run.</summary>
public sealed class ThrottlingModel : PageModel
{
    /// <summary>The default page size, matching uspListThrottleEvents.</summary>
    public const int DefaultPageSize = 100;

    private readonly CrawlStateQueries queries;

    /// <summary>Initializes a new instance of the <see cref="ThrottlingModel"/> class.</summary>
    /// <param name="queries">The read surface.</param>
    public ThrottlingModel(CrawlStateQueries queries)
    {
        this.queries = queries;
    }

    /// <summary>Gets or sets the 1-based page number.</summary>
    [BindProperty(SupportsGet = true, Name = Pager.PageKey)]
    public int PageNumber { get; set; } = 1;

    /// <summary>Gets or sets the page size.</summary>
    [BindProperty(SupportsGet = true, Name = "ps")]
    public int? PageSize { get; set; }

    /// <summary>Gets the run the events belong to.</summary>
    public RunHistoryRow Run { get; private set; } = default!;

    /// <summary>Gets the throttle aggregate, or null if the run was never throttled.</summary>
    public ThrottleSummaryRow? Summary { get; private set; }

    /// <summary>Gets the page of events.</summary>
    public PagedResult<ThrottleEventRow> Result { get; private set; } =
        new(Array.Empty<ThrottleEventRow>(), 0, 1, DefaultPageSize);

    /// <summary>Gets the pager.</summary>
    public Pager Pager { get; private set; } = default!;

    /// <summary>Reads the run header and one page of its throttle events.</summary>
    /// <param name="runId">The run whose events to show.</param>
    /// <returns>The page, or 404 when no such run exists.</returns>
    public async Task<IActionResult> OnGetAsync(long runId)
    {
        CancellationToken cancellationToken = this.HttpContext.RequestAborted;

        RunDetail detail = await this.queries.GetRunAsync(runId, cancellationToken);

        if (detail.Run is null)
        {
            return this.NotFound();
        }

        this.Run = detail.Run;
        this.Summary = detail.Throttling;

        this.Result = await this.queries.ListThrottleEventsAsync(
            runId,
            this.PageNumber,
            this.PageSize ?? DefaultPageSize,
            cancellationToken);

        this.Pager = Pager.For(
            this.Result,
            $"/Runs/{runId}/Throttling",
            new List<KeyValuePair<string, string?>>
            {
                new("ps", this.PageSize?.ToString(System.Globalization.CultureInfo.InvariantCulture)),
            },
            "events");

        return this.Page();
    }
}
