// ---------------------------------------------------------------------------
// Index.cshtml.cs
// The landing page: everything an operator needs to decide whether to look
// further, in ONE round trip.
//
// It calls crawl.uspDashboardSummary and nothing else. That procedure returns
// four result sets - tiles, per-connection health, a thirty-day trend and the
// ten most recent runs - specifically so the front page cannot drift into six
// queries as it grows. If something new belongs here, it belongs in that
// procedure.
//
// The window matters. Totals since installation - "1.4 million items written" -
// are a number nobody acts on. Twenty-four hours against the day before is a
// number somebody does. The window is a query parameter so an operator can widen
// it after an outage without editing configuration, clamped below because
// @WindowHours goes into DATEADD and a hostile value there is a scan of the
// whole history rather than a seek.
// ---------------------------------------------------------------------------

namespace ConnectorState.Dashboard.Pages;

using ConnectorState.Dashboard.Data;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.Extensions.Options;

/// <summary>The dashboard landing page.</summary>
public sealed class IndexModel : PageModel
{
    /// <summary>The widest window the front page will summarise over: one quarter.</summary>
    public const int MaxWindowHours = 24 * 90;

    private readonly CrawlStateQueries queries;
    private readonly CrawlStateOptions options;

    /// <summary>Initializes a new instance of the <see cref="IndexModel"/> class.</summary>
    /// <param name="queries">The read surface.</param>
    /// <param name="options">The bound "CrawlState" section.</param>
    public IndexModel(CrawlStateQueries queries, IOptions<CrawlStateOptions> options)
    {
        this.queries = queries;
        this.options = options.Value;
    }

    /// <summary>Gets the window, in hours, the headline figures cover.</summary>
    [BindProperty(SupportsGet = true, Name = "hours")]
    public int? WindowHours { get; set; }

    /// <summary>Gets all four result sets of uspDashboardSummary.</summary>
    public DashboardSummary Summary { get; private set; } = new(
        new DashboardTiles(),
        Array.Empty<ConnectionHealthRow>(),
        Array.Empty<DailyActivityRow>(),
        Array.Empty<RunHistoryRow>());

    /// <summary>Gets the window actually used, after clamping.</summary>
    public int EffectiveWindowHours { get; private set; }

    /// <summary>Reads the summary.</summary>
    /// <returns>A task that completes when the page is ready to render.</returns>
    public async Task OnGetAsync()
    {
        int requested = this.WindowHours ?? this.options.SummaryWindowHours;

        this.EffectiveWindowHours = requested switch
        {
            < 1 => 1,
            > MaxWindowHours => MaxWindowHours,
            _ => requested,
        };

        this.Summary = await this.queries.GetDashboardSummaryAsync(
            this.EffectiveWindowHours,
            this.HttpContext.RequestAborted);
    }
}
