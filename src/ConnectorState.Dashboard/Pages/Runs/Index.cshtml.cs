// ---------------------------------------------------------------------------
// Runs/Index.cshtml.cs
// The run history, filtered and paged.
//
// It calls crawl.uspListRuns, which is one procedure serving the
// all-connections list, one connection's history and the failures-only view -
// every filter is optional and null means "no filter". Three near-identical
// procedures would have drifted apart; three near-identical pages would have
// drifted further.
//
// PAGING IS THE DATABASE'S. uspListRuns applies OFFSET/FETCH and returns
// TotalRows on every row through COUNT(*) OVER(). Nothing here fetches a set and
// slices it: the page size is passed down and the count comes back with the
// rows, which is one round trip and a count that cannot disagree with the page
// beside it.
//
// The status and mode filters go down as the tinyint codes sql/21's CHECK
// constraints define, and StateCodes returns null for a word it does not
// recognise - so a hand-edited query string widens the result rather than
// erroring, which is the right failure for a filter.
//
// The date filter deserves a note: @ToUtc is EXCLUSIVE in sql/24, and an
// operator picking "to 3 March" means the end of the 3rd. This page adds a day
// to what was typed. Getting that wrong silently drops the most recent day,
// which is the day somebody filtering by date is usually looking for.
// ---------------------------------------------------------------------------

namespace ConnectorState.Dashboard.Pages.Runs;

using ConnectorState.Dashboard.Data;
using ConnectorState.Dashboard.Presentation;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.Extensions.Options;

/// <summary>The paged, filtered run list.</summary>
public sealed class IndexModel : PageModel
{
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

    /// <summary>Gets or sets the connection filter.</summary>
    [BindProperty(SupportsGet = true, Name = "c")]
    public string? ConnectionId { get; set; }

    /// <summary>Gets or sets the status filter, as the word the views use.</summary>
    [BindProperty(SupportsGet = true, Name = "status")]
    public string? Status { get; set; }

    /// <summary>Gets or sets the mode filter, as the word the views use.</summary>
    [BindProperty(SupportsGet = true, Name = "mode")]
    public string? Mode { get; set; }

    /// <summary>Gets or sets the inclusive lower bound on the start date.</summary>
    [BindProperty(SupportsGet = true, Name = "from")]
    public DateTime? From { get; set; }

    /// <summary>Gets or sets the inclusive upper bound on the start date, as typed.</summary>
    [BindProperty(SupportsGet = true, Name = "to")]
    public DateTime? To { get; set; }

    /// <summary>Gets or sets a value indicating whether runs that wrote nothing by design are shown.</summary>
    [BindProperty(SupportsGet = true, Name = "dry")]
    public bool IncludeDryRuns { get; set; }

    /// <summary>
    /// Gets or sets the 1-based page number. Named PageNumber rather than Page
    /// because PageModel.Page() is the method that returns a page result.
    /// </summary>
    [BindProperty(SupportsGet = true, Name = Pager.PageKey)]
    public int PageNumber { get; set; } = 1;

    /// <summary>Gets or sets the page size.</summary>
    [BindProperty(SupportsGet = true, Name = "ps")]
    public int? PageSize { get; set; }

    /// <summary>Gets the page of runs.</summary>
    public PagedResult<RunListRow> Result { get; private set; } =
        new(Array.Empty<RunListRow>(), 0, 1, 50);

    /// <summary>Gets the connections offered in the filter control.</summary>
    public IReadOnlyList<ConnectionRef> Connections { get; private set; } = Array.Empty<ConnectionRef>();

    /// <summary>Gets the pager, which carries the filters forward.</summary>
    public Pager Pager { get; private set; } = default!;

    /// <summary>Reads the page of runs and the connection list.</summary>
    /// <returns>A task that completes when the page is ready to render.</returns>
    public async Task OnGetAsync()
    {
        CancellationToken cancellationToken = this.HttpContext.RequestAborted;

        this.Connections = await this.queries.ListConnectionsAsync(cancellationToken);

        // Exclusive upper bound: "to the 3rd" means up to the start of the 4th.
        DateTime? toExclusive = this.To?.Date.AddDays(1);

        this.Result = await this.queries.ListRunsAsync(
            this.ConnectionId,
            StateCodes.RunStatus(this.Status),
            StateCodes.RunMode(this.Mode),
            this.From?.Date,
            toExclusive,
            this.IncludeDryRuns,
            this.PageNumber,
            this.PageSize ?? this.options.DefaultPageSize,
            cancellationToken);

        this.Pager = Pager.For(this.Result, "/Runs", this.QueryForPager(), "runs");
    }

    private List<KeyValuePair<string, string?>> QueryForPager()
    {
        return new List<KeyValuePair<string, string?>>
        {
            new("c", this.ConnectionId),
            new("status", this.Status),
            new("mode", this.Mode),
            new("from", this.From?.ToString("yyyy-MM-dd")),
            new("to", this.To?.ToString("yyyy-MM-dd")),
            new("dry", this.IncludeDryRuns ? "true" : null),
            new("ps", this.PageSize?.ToString(System.Globalization.CultureInfo.InvariantCulture)),
        };
    }
}
