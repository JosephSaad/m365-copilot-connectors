// ---------------------------------------------------------------------------
// PendingDeletes/Index.cshtml.cs
// Items a sweep decided are gone and Graph has not confirmed removed.
//
// On a healthy connection this page is empty for all but a few seconds per run.
// A row that persists across runs means a DELETE was refused, retried, and
// refused again - which is exactly the failure the Graph connector agent used to
// absorb silently: an item the source dropped, still answering searches. It is
// the reason crawl.Item has a state at all rather than a row that vanishes.
//
// THE AGE FILTER IS THE POINT OF THE PAGE. Without it this list is dominated by
// deletes that are pending because a run is in progress right now, which is
// normal and uninteresting. "Older than one crawl interval" is the rule that
// catches a stuck delete without firing on every run in flight, and that is what
// @MinAgeMinutes expresses.
//
// Paged, because on the run after a large source change this list is the size of
// the change, and a page that tries to render all of it stops rendering
// anything. uspListPendingDeletes applies OFFSET/FETCH and returns TotalRows on
// every row through COUNT(*) OVER().
// ---------------------------------------------------------------------------

namespace ConnectorState.Dashboard.Pages.PendingDeletes;

using ConnectorState.Dashboard.Data;
using ConnectorState.Dashboard.Presentation;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.Extensions.Options;

/// <summary>The paged list of deletes Graph has not confirmed.</summary>
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

    /// <summary>Gets or sets the minimum age, in minutes, a delete must have been pending.</summary>
    [BindProperty(SupportsGet = true, Name = "age")]
    public int? MinAgeMinutes { get; set; }

    /// <summary>Gets or sets the 1-based page number.</summary>
    [BindProperty(SupportsGet = true, Name = Pager.PageKey)]
    public int PageNumber { get; set; } = 1;

    /// <summary>Gets or sets the page size.</summary>
    [BindProperty(SupportsGet = true, Name = "ps")]
    public int? PageSize { get; set; }

    /// <summary>Gets the page of pending deletes.</summary>
    public PagedResult<PendingDeleteRow> Result { get; private set; } =
        new(Array.Empty<PendingDeleteRow>(), 0, 1, 50);

    /// <summary>Gets the connections offered in the filter control.</summary>
    public IReadOnlyList<ConnectionRef> Connections { get; private set; } = Array.Empty<ConnectionRef>();

    /// <summary>Gets the pager.</summary>
    public Pager Pager { get; private set; } = default!;

    /// <summary>Reads the page of pending deletes and the connection list.</summary>
    /// <returns>A task that completes when the page is ready to render.</returns>
    public async Task OnGetAsync()
    {
        CancellationToken cancellationToken = this.HttpContext.RequestAborted;

        this.Connections = await this.queries.ListConnectionsAsync(cancellationToken);

        this.Result = await this.queries.ListPendingDeletesAsync(
            this.ConnectionId,
            this.MinAgeMinutes,
            this.PageNumber,
            this.PageSize ?? this.options.DefaultPageSize,
            cancellationToken);

        this.Pager = Pager.For(
            this.Result,
            "/PendingDeletes",
            new List<KeyValuePair<string, string?>>
            {
                new("c", this.ConnectionId),
                new("age", this.MinAgeMinutes?.ToString(System.Globalization.CultureInfo.InvariantCulture)),
                new("ps", this.PageSize?.ToString(System.Globalization.CultureInfo.InvariantCulture)),
            },
            "pending deletes");
    }
}
