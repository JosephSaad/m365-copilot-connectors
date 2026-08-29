// ---------------------------------------------------------------------------
// PagedResult.cs
// A page of rows and the size of the set it came from.
//
// This type exists so that "page 3 of 214" is computed once. Every list
// procedure in sql/24 returns TotalRows on EVERY ROW via COUNT(*) OVER(), which
// is a window function over the already-filtered set rather than a second COUNT
// query - one round trip, and the count cannot disagree with the page beside it
// because a run completed between two calls.
//
// The alternative that keeps getting written instead is fetching everything and
// calling Skip().Take() on it. On crawl.Item that is the difference between a
// seek of fifty rows and a scan of the corpus, per page view, per operator. The
// OFFSET/FETCH lives in the procedure where the query plan is, and this type is
// only the arithmetic for the pager control.
//
// TotalRows is zero when the page came back empty, which is the one case the
// COUNT(*) OVER() cannot report: no rows means no row to carry it. Page 1 of 1
// with nothing on it is the honest rendering, and PageCount below returns 1
// rather than 0 so the pager does not read "page 1 of 0".
// ---------------------------------------------------------------------------

namespace ConnectorState.Dashboard.Data;

/// <summary>One page of a list, with the total the pager needs.</summary>
/// <typeparam name="T">The row type.</typeparam>
public sealed class PagedResult<T>
{
    /// <summary>Initializes a new instance of the <see cref="PagedResult{T}"/> class.</summary>
    /// <param name="rows">The rows on this page.</param>
    /// <param name="totalRows">The size of the whole filtered set, from TotalRows.</param>
    /// <param name="page">The 1-based page number that was requested.</param>
    /// <param name="pageSize">The page size the procedure actually applied.</param>
    public PagedResult(IReadOnlyList<T> rows, int totalRows, int page, int pageSize)
    {
        this.Rows = rows;
        this.TotalRows = totalRows;
        this.Page = page < 1 ? 1 : page;
        this.PageSize = pageSize < 1 ? 1 : pageSize;
    }

    /// <summary>Gets the rows on this page.</summary>
    public IReadOnlyList<T> Rows { get; }

    /// <summary>Gets the size of the whole filtered set.</summary>
    public int TotalRows { get; }

    /// <summary>Gets the 1-based page number.</summary>
    public int Page { get; }

    /// <summary>Gets the page size.</summary>
    public int PageSize { get; }

    /// <summary>Gets the number of pages, never less than one.</summary>
    public int PageCount => this.TotalRows <= 0 ? 1 : ((this.TotalRows - 1) / this.PageSize) + 1;

    /// <summary>Gets a value indicating whether there is a page before this one.</summary>
    public bool HasPrevious => this.Page > 1;

    /// <summary>Gets a value indicating whether there is a page after this one.</summary>
    public bool HasNext => this.Page < this.PageCount;

    /// <summary>Gets the 1-based ordinal of the first row on this page, or zero when empty.</summary>
    public int FirstRowNumber => this.Rows.Count == 0 ? 0 : ((this.Page - 1) * this.PageSize) + 1;

    /// <summary>Gets the 1-based ordinal of the last row on this page, or zero when empty.</summary>
    public int LastRowNumber => this.Rows.Count == 0 ? 0 : this.FirstRowNumber + this.Rows.Count - 1;

    /// <summary>Gets a value indicating whether this page has no rows.</summary>
    public bool IsEmpty => this.Rows.Count == 0;
}
