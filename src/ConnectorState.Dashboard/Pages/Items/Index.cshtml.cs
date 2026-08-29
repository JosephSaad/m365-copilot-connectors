// ---------------------------------------------------------------------------
// Items/Index.cshtml.cs
// The inventory: what the connector believes is in the index, per item.
//
// "Believes" is the honest word, and the page says so. This table records what
// the connector wrote; drift between it and the real index is possible - a
// tenant-side purge, a manual deletion, a run that died between the Graph write
// and the state upsert. deploy/Compare-SourceToIndex.ps1 is still how drift is
// FOUND. This page is how you know what to compare against.
//
// @ConnectionId IS REQUIRED BY uspListItems and this page does not work around
// that. crawl.Item is clustered on (ConnectionId, ItemId), so a query without a
// connection is a scan of every corpus in the estate to produce a page of fifty
// rows. With no connection chosen the page shows the picker and calls nothing.
//
// THE SEARCH IS A PREFIX MATCH, NOT A CONTAINS, and the label says so. sql/24
// applies LIKE @Search + '%', anchored: a leading wildcard cannot use the
// clustered index and turns every lookup into a scan of the corpus. Item IDs in
// this repository are composed with a stable prefix per type - see
// docs/SOURCE-CONTRACT.md - so a prefix search is the search people want anyway.
// Presenting it as a contains search that silently fails to find things would be
// worse than presenting it as what it is.
//
// THE UNCHANGED-STREAK FILTER IS THE INTERESTING ONE. Items with a long streak
// have been read every run and written almost never, which is the change
// detection paying for itself; items with a streak of zero on a connection whose
// source has not changed are the signature of hashes that are not matching,
// which is a defect rather than a workload change.
//
// NO COLUMN HERE CAN CONTAIN ITEM CONTENT. crawl.Item holds an identifier, a
// type, two hashes and a byte count. There is no title and no body to show, and
// the page says that where an operator would expect one.
// ---------------------------------------------------------------------------

namespace ConnectorState.Dashboard.Pages.Items;

using ConnectorState.Dashboard.Data;
using ConnectorState.Dashboard.Presentation;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.Extensions.Options;

/// <summary>The paged, filtered item inventory.</summary>
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

    /// <summary>Gets or sets the connection whose inventory to show. Required by uspListItems.</summary>
    [BindProperty(SupportsGet = true, Name = "c")]
    public string? ConnectionId { get; set; }

    /// <summary>Gets or sets the item ID PREFIX to search for.</summary>
    [BindProperty(SupportsGet = true, Name = "q")]
    public string? Search { get; set; }

    /// <summary>Gets or sets the item type filter.</summary>
    [BindProperty(SupportsGet = true, Name = "type")]
    public string? ItemType { get; set; }

    /// <summary>Gets or sets the state filter, as the word the views use.</summary>
    [BindProperty(SupportsGet = true, Name = "state")]
    public string? State { get; set; }

    /// <summary>Gets or sets the minimum unchanged streak.</summary>
    [BindProperty(SupportsGet = true, Name = "streak")]
    public int? MinUnchangedStreak { get; set; }

    /// <summary>Gets or sets the 1-based page number.</summary>
    [BindProperty(SupportsGet = true, Name = Pager.PageKey)]
    public int PageNumber { get; set; } = 1;

    /// <summary>Gets or sets the page size.</summary>
    [BindProperty(SupportsGet = true, Name = "ps")]
    public int? PageSize { get; set; }

    /// <summary>Gets the page of items. Empty until a connection is chosen.</summary>
    public PagedResult<ItemRow> Result { get; private set; } =
        new(Array.Empty<ItemRow>(), 0, 1, 50);

    /// <summary>Gets the connections offered in the filter control.</summary>
    public IReadOnlyList<ConnectionRef> Connections { get; private set; } = Array.Empty<ConnectionRef>();

    /// <summary>Gets a value indicating whether a connection has been chosen.</summary>
    public bool HasConnection => !string.IsNullOrWhiteSpace(this.ConnectionId);

    /// <summary>Gets the pager. Null until a connection is chosen.</summary>
    public Pager? Pager { get; private set; }

    /// <summary>Reads the connection list, and a page of items once a connection is chosen.</summary>
    /// <returns>A task that completes when the page is ready to render.</returns>
    public async Task OnGetAsync()
    {
        CancellationToken cancellationToken = this.HttpContext.RequestAborted;

        this.Connections = await this.queries.ListConnectionsAsync(cancellationToken);

        if (!this.HasConnection)
        {
            // Nothing is queried without a connection. See the file header: the
            // clustered index starts with ConnectionId, so this is not a
            // convenience, it is the difference between a seek and a scan.
            return;
        }

        this.Result = await this.queries.ListItemsAsync(
            this.ConnectionId!,
            this.Search,
            this.ItemType,
            StateCodes.ItemState(this.State),
            this.MinUnchangedStreak,
            this.PageNumber,
            this.PageSize ?? this.options.DefaultPageSize,
            cancellationToken);

        this.Pager = Pager.For(
            this.Result,
            "/Items",
            new List<KeyValuePair<string, string?>>
            {
                new("c", this.ConnectionId),
                new("q", this.Search),
                new("type", this.ItemType),
                new("state", this.State),
                new("streak", this.MinUnchangedStreak?.ToString(System.Globalization.CultureInfo.InvariantCulture)),
                new("ps", this.PageSize?.ToString(System.Globalization.CultureInfo.InvariantCulture)),
            },
            "items");
    }
}
