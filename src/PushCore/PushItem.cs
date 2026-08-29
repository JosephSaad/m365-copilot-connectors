// ---------------------------------------------------------------------------
// PushItem.cs
// What a connector returns for one row. Deliberately not an ExternalItem: the
// engine owns truncation, the ACL and the write, so a connector that could
// build the whole item could also bypass those.
// ---------------------------------------------------------------------------

namespace PushCore;

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

    /// <summary>
    /// Gets or sets the grants for this item, when they differ from item to item.
    ///
    /// Null - the normal case - means the item carries the connection-wide ACL
    /// built from Acl:GrantGroupObjectIds, which is right whenever every row in
    /// a source is at least as sensitive as every other. A source whose items
    /// have their own permissions, such as a filesystem, sets this instead.
    ///
    /// An empty list is not the same as null and is not a licence to write the
    /// item: the engine refuses it, because an item granted to nobody is indexed
    /// and then returned to no one, which looks like success and is not.
    /// </summary>
    public IReadOnlyList<PushAclEntry>? Acl { get; set; }

    /// <summary>
    /// Gets or sets when this record last changed at the source, in UTC, or null
    /// when the source has no such value.
    ///
    /// This is the first half of the composite checkpoint; the item ID is the
    /// second. The engine advances the checkpoint to (LastModifiedUtc, Id) only
    /// after the write for this item is confirmed, so a run that dies cannot
    /// leave a marker past an item the index does not have.
    ///
    /// Leave it null unless the source genuinely exposes a monotonic
    /// modification time that moves on EVERY change, including bulk updates and
    /// direct edits - see docs/SOURCE-CONTRACT.md. A timestamp that is merely
    /// usually right produces a checkpoint that silently skips the edits it
    /// missed, which is worse than having no checkpoint at all.
    /// </summary>
    public DateTime? LastModifiedUtc { get; set; }

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

    /// <summary>
    /// Adds a multi-value string property, for a schema property registered as
    /// <see cref="Microsoft.Graph.Models.ExternalConnectors.PropertyType.StringCollection"/>.
    ///
    /// This is not the same as joining the values and adding the string, and the
    /// difference is visible to the person searching. A refiner buckets on the
    /// whole stored value, so a joined "PII, GDPR" is a bucket of its own that
    /// filtering on "PII" does not match. A collection buckets on each element,
    /// which is what a refiner over tags has to do to be worth registering.
    ///
    /// The engine adds the OData annotation Graph requires beside it; nothing
    /// here has to remember to.
    /// </summary>
    /// <param name="name">Schema property name.</param>
    /// <param name="values">The values. Empty and blank entries are dropped, and an empty result is not added.</param>
    public void AddIfPresent(string name, IReadOnlyList<string>? values)
    {
        if (values is null)
        {
            return;
        }

        List<string> present = values.Where(value => !string.IsNullOrWhiteSpace(value)).ToList();

        if (present.Count > 0)
        {
            this.Properties[name] = present;
        }
    }
}
