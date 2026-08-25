// ---------------------------------------------------------------------------
// PushItem.cs
// What a connector returns for one row. Deliberately not an ExternalItem: the
// engine owns truncation, the ACL and the write, so a connector that could
// build the whole item could also bypass those.
// ---------------------------------------------------------------------------

namespace SqlPushCore;

/// <summary>One row, mapped.</summary>
public sealed class PushItem
{
    /// <summary>
    /// Gets or sets the external item ID: alphanumeric, 128 characters at most.
    /// Compose it rather than reusing a natural key that may hold punctuation.
    /// </summary>
    public string Id { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets what kind of thing this is, for the run summary. Any string;
    /// a connector with one kind of row can leave it as "item".
    /// </summary>
    public string ItemType { get; set; } = "item";

    /// <summary>
    /// Gets the property values, keyed by schema property name. Omit a property
    /// rather than sending null: Graph rejects a null value rather than ignoring
    /// it, and a customer has no consultant.
    /// </summary>
    public Dictionary<string, object> Properties { get; } = new Dictionary<string, object>(StringComparer.Ordinal);

    /// <summary>
    /// Gets or sets the full text body. The engine truncates it to
    /// DataSource:MaxContentBytes without splitting a character, and counts it.
    /// </summary>
    public string Content { get; set; } = string.Empty;

    /// <summary>Adds a property when the value is present, and skips it when not.</summary>
    /// <param name="name">Schema property name.</param>
    /// <param name="value">Value, ignored when null or empty.</param>
    public void AddIfPresent(string name, string? value)
    {
        if (!string.IsNullOrEmpty(value))
        {
            this.Properties[name] = value;
        }
    }

    /// <summary>Adds a property when the value is present, and skips it when not.</summary>
    /// <param name="name">Schema property name.</param>
    /// <param name="value">Value, ignored when null.</param>
    public void AddIfPresent(string name, double? value)
    {
        if (value.HasValue)
        {
            this.Properties[name] = value.Value;
        }
    }

    /// <summary>Adds a property when the value is present, and skips it when not.</summary>
    /// <param name="name">Schema property name.</param>
    /// <param name="value">Value, ignored when null.</param>
    public void AddIfPresent(string name, bool? value)
    {
        if (value.HasValue)
        {
            this.Properties[name] = value.Value;
        }
    }

    /// <summary>Adds a property when the value is present, and skips it when not.</summary>
    /// <param name="name">Schema property name.</param>
    /// <param name="value">Value, ignored when null.</param>
    public void AddIfPresent(string name, long? value)
    {
        if (value.HasValue)
        {
            this.Properties[name] = value.Value;
        }
    }
}
