// ---------------------------------------------------------------------------
// Connections/Detail.cshtml.cs
// One connection: is it working, what does it hold, and where would the next
// incremental run start.
//
// It calls crawl.uspGetConnectionDetail, which returns four result sets: health,
// the item-type mix, the trend, and the checkpoint.
//
// THE MIX COMES FROM THE LIVE INVENTORY, NOT FROM THE LAST RUN. That is a
// deliberate difference from the run detail page and the two are easy to
// confuse. This one answers "what is in the index"; the run page answers "what
// did that run touch". A connection whose last run wrote twelve items still
// holds four hundred thousand.
//
// THE CHECKPOINT IS ON THE PAGE because "where would the next incremental run
// start" is a question people currently answer by writing a query, and a
// question answered by writing a query is a question most people do not ask. A
// checkpoint that has stopped advancing while runs keep succeeding is a specific
// and quiet failure.
//
// An unknown connection id returns 404 rather than an empty page.
// ---------------------------------------------------------------------------

namespace ConnectorState.Dashboard.Pages.Connections;

using ConnectorState.Dashboard.Data;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

/// <summary>One connection's detail page.</summary>
public sealed class DetailModel : PageModel
{
    /// <summary>The trend window, in days. Matches the front page so the two charts compare.</summary>
    public const int TrendDays = 30;

    private readonly CrawlStateQueries queries;

    /// <summary>Initializes a new instance of the <see cref="DetailModel"/> class.</summary>
    /// <param name="queries">The read surface.</param>
    public DetailModel(CrawlStateQueries queries)
    {
        this.queries = queries;
    }

    /// <summary>Gets the connection's health. Never null once OnGetAsync has returned a page.</summary>
    public ConnectionHealthRow Health { get; private set; } = default!;

    /// <summary>Gets what the index holds for this connection, by kind of item.</summary>
    public IReadOnlyList<ItemTypeMixRow> ItemTypes { get; private set; } = Array.Empty<ItemTypeMixRow>();

    /// <summary>Gets the daily activity over the trend window.</summary>
    public IReadOnlyList<DailyActivityRow> Trend { get; private set; } = Array.Empty<DailyActivityRow>();

    /// <summary>Gets the checkpoint, or null when the connector keeps none.</summary>
    public CheckpointRow? Checkpoint { get; private set; }

    /// <summary>Gets the total live items across every type, for the mix table's footer.</summary>
    public long TotalLiveItems { get; private set; }

    /// <summary>Gets the total content bytes across every type.</summary>
    public long TotalContentBytes { get; private set; }

    /// <summary>Reads the connection.</summary>
    /// <param name="connectionId">The connection to show.</param>
    /// <returns>The page, or 404 when no such connection exists.</returns>
    public async Task<IActionResult> OnGetAsync(string connectionId)
    {
        ConnectionDetail detail = await this.queries.GetConnectionDetailAsync(
            connectionId,
            TrendDays,
            this.HttpContext.RequestAborted);

        if (detail.Health is null)
        {
            return this.NotFound();
        }

        this.Health = detail.Health;
        this.ItemTypes = detail.ItemTypes;
        this.Trend = detail.Trend;
        this.Checkpoint = detail.Checkpoint;

        foreach (ItemTypeMixRow type in detail.ItemTypes)
        {
            this.TotalLiveItems += type.Live;
            this.TotalContentBytes += type.ContentBytes;
        }

        return this.Page();
    }
}
