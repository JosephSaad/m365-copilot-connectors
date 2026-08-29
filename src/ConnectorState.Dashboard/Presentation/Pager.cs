// ---------------------------------------------------------------------------
// Pager.cs
// The prev/next control, and the URLs it points at.
//
// It exists so that paging a filtered list does not silently drop the filters.
// The obvious implementation - a link to "?p=2" - resets the connection, the
// status and the date range to their defaults, and the operator who was looking
// at failed runs for one connection is now looking at page two of everything.
// This carries the current query forward and replaces one key.
//
// The numbers it renders come from TotalRows, which every list procedure in
// sql/24 returns on every row through COUNT(*) OVER(). Nothing here counts
// anything: see the header of PagedResult.cs.
// ---------------------------------------------------------------------------

namespace ConnectorState.Dashboard.Presentation;

using System.Globalization;
using System.Text;

/// <summary>The state a pager control needs, and the links it renders.</summary>
public sealed class Pager
{
    /// <summary>The query-string key carrying the page number.</summary>
    public const string PageKey = "p";

    private readonly string path;
    private readonly IReadOnlyList<KeyValuePair<string, string?>> query;

    /// <summary>Initializes a new instance of the <see cref="Pager"/> class.</summary>
    /// <param name="path">The path to link back to, without a query string.</param>
    /// <param name="query">The filters to carry forward. Null and empty values are omitted.</param>
    /// <param name="page">The current 1-based page.</param>
    /// <param name="pageCount">The number of pages.</param>
    /// <param name="totalRows">The size of the whole filtered set.</param>
    /// <param name="firstRowNumber">The ordinal of the first row on this page.</param>
    /// <param name="lastRowNumber">The ordinal of the last row on this page.</param>
    /// <param name="noun">What the rows are, plural, for the summary line.</param>
    public Pager(
        string path,
        IReadOnlyList<KeyValuePair<string, string?>> query,
        int page,
        int pageCount,
        int totalRows,
        int firstRowNumber,
        int lastRowNumber,
        string noun)
    {
        this.path = path;
        this.query = query;
        this.Page = page;
        this.PageCount = pageCount;
        this.TotalRows = totalRows;
        this.FirstRowNumber = firstRowNumber;
        this.LastRowNumber = lastRowNumber;
        this.Noun = noun;
    }

    /// <summary>Gets the current 1-based page.</summary>
    public int Page { get; }

    /// <summary>Gets the number of pages.</summary>
    public int PageCount { get; }

    /// <summary>Gets the size of the whole filtered set.</summary>
    public int TotalRows { get; }

    /// <summary>Gets the ordinal of the first row on this page.</summary>
    public int FirstRowNumber { get; }

    /// <summary>Gets the ordinal of the last row on this page.</summary>
    public int LastRowNumber { get; }

    /// <summary>Gets the plural noun for the rows being paged.</summary>
    public string Noun { get; }

    /// <summary>Gets a value indicating whether there is a page before this one.</summary>
    public bool HasPrevious => this.Page > 1;

    /// <summary>Gets a value indicating whether there is a page after this one.</summary>
    public bool HasNext => this.Page < this.PageCount;

    /// <summary>Builds a pager from a page of results.</summary>
    /// <typeparam name="T">The row type.</typeparam>
    /// <param name="result">The page.</param>
    /// <param name="path">The path to link back to.</param>
    /// <param name="query">The filters to carry forward.</param>
    /// <param name="noun">What the rows are, plural.</param>
    /// <returns>The pager.</returns>
    public static Pager For<T>(
        Data.PagedResult<T> result,
        string path,
        IReadOnlyList<KeyValuePair<string, string?>> query,
        string noun)
    {
        return new Pager(
            path,
            query,
            result.Page,
            result.PageCount,
            result.TotalRows,
            result.FirstRowNumber,
            result.LastRowNumber,
            noun);
    }

    /// <summary>Builds the URL for a page, carrying the current filters.</summary>
    /// <param name="page">The 1-based page to link to.</param>
    /// <returns>A relative URL.</returns>
    public string Url(int page)
    {
        var url = new StringBuilder(this.path);
        char separator = '?';

        foreach (KeyValuePair<string, string?> pair in this.query)
        {
            if (string.IsNullOrEmpty(pair.Value) || string.Equals(pair.Key, PageKey, StringComparison.Ordinal))
            {
                continue;
            }

            url.Append(separator)
               .Append(Uri.EscapeDataString(pair.Key))
               .Append('=')
               .Append(Uri.EscapeDataString(pair.Value));

            separator = '&';
        }

        url.Append(separator)
           .Append(PageKey)
           .Append('=')
           .Append(page.ToString(CultureInfo.InvariantCulture));

        return url.ToString();
    }
}
