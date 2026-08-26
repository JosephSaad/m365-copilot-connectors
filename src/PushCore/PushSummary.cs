// ---------------------------------------------------------------------------
// PushSummary.cs
// What the run wrote, counted by item type so the summary line says something
// a person can check against the source.
// ---------------------------------------------------------------------------

namespace PushCore;

using System.Globalization;

/// <summary>Counts for one run.</summary>
public sealed class PushSummary
{
    private readonly Dictionary<string, int> byType = new Dictionary<string, int>(StringComparer.Ordinal);

    /// <summary>Gets the number of items whose content did not fit the cap.</summary>
    public int Truncated { get; internal set; }

    /// <summary>Gets the number of times the run backed off after a 429.</summary>
    public int ThrottleWaits { get; internal set; }

    /// <summary>Gets the number of rows the connector chose to skip.</summary>
    public int Skipped { get; internal set; }

    /// <summary>
    /// Gets the number of rows whose item ID repeated an earlier row's. The later
    /// row overwrote the earlier item; the source should return one row per item.
    /// </summary>
    public int Duplicates { get; internal set; }

    /// <summary>Gets the total number of items written.</summary>
    public int Total { get; private set; }

    /// <summary>Gets the counts, keyed by item type.</summary>
    public IReadOnlyDictionary<string, int> ByType => this.byType;

    /// <summary>Records one written item.</summary>
    /// <param name="itemType">The item's declared type.</param>
    public void Count(string itemType)
    {
        string key = string.IsNullOrWhiteSpace(itemType) ? "item" : itemType;

        this.byType[key] = this.byType.TryGetValue(key, out int existing) ? existing + 1 : 1;
        this.Total++;
    }

    /// <summary>Renders the per-type counts as "Customer=12, Engagement=62".</summary>
    /// <returns>The counts in insertion order, or "none" when nothing was written.</returns>
    public string Describe()
    {
        if (this.byType.Count == 0)
        {
            return "none";
        }

        return string.Join(
            ", ",
            this.byType.Select(pair => string.Format(
                CultureInfo.InvariantCulture, "{0}={1}", pair.Key, pair.Value)));
    }
}
