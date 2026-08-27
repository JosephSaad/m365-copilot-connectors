// ---------------------------------------------------------------------------
// AtlasModels.cs
// The parts of an Atlas entity a catalogue entry is made of.
//
// Atlas describes everything with one very general entity model - a GUID, a
// type name, a bag of attributes, some classifications and a set of
// relationships - and most of it is not what somebody searching for a dataset
// wants. What they want is: what is this called, who owns it, what does it
// hold, what is it tagged with, and where did it come from.
//
// So this is deliberately a narrow reading of a wide model. Anything not
// needed to answer those five questions is not parsed, because a field parsed
// is a field that has to stay correct.
//
// One thing worth knowing when reading the code: an Atlas qualifiedName for a
// Hive object carries the cluster name after an @ - "finance.customer@prod" -
// and that suffix is not part of the table's name. Splitting it off is what
// lets the Ranger check be asked about the right table.
// ---------------------------------------------------------------------------

namespace CdpConnector.Source.Atlas;

/// <summary>What kind of thing a catalogue entry describes.</summary>
public enum AtlasEntityKind
{
    /// <summary>Something this connector does not describe.</summary>
    Other = 0,

    /// <summary>A Hive database.</summary>
    Database = 1,

    /// <summary>A Hive table or view.</summary>
    Table = 2,

    /// <summary>A path in HDFS.</summary>
    Path = 3,
}

/// <summary>
/// One dataset on the other end of a lineage hop, before anything decides
/// whether the reader of this entry may be told it exists.
///
/// It carries the qualified name rather than only the display name because that
/// is what the Ranger check needs: a name alone cannot be split into a database
/// and a table, and a neighbour that cannot be checked is a neighbour that must
/// be dropped.
/// </summary>
public sealed class AtlasNeighbour
{
    /// <summary>Gets or sets the neighbour's Atlas GUID.</summary>
    public string Guid { get; set; } = string.Empty;

    /// <summary>Gets or sets its Atlas type name.</summary>
    public string TypeName { get; set; } = string.Empty;

    /// <summary>Gets or sets its display name, which is what a reader is shown.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>Gets or sets its qualified name, which is what Ranger is asked about.</summary>
    public string QualifiedName { get; set; } = string.Empty;

    /// <summary>
    /// Gets a value indicating whether this node is an ETL step rather than a
    /// dataset, and so something to walk THROUGH rather than name.
    ///
    /// Hive lineage does not join two tables directly: it records
    /// table -> hive_process -> table, and the process's name is the query text
    /// that produced it. Naming the process instead of the table it read would
    /// put raw SQL in the index, and that SQL names tables of its own - which is
    /// the disclosure the neighbour check exists to prevent, arriving inside a
    /// string nobody thought to check. Impala and Spark interpose their own
    /// equivalents, and column-level lineage interposes another again.
    /// </summary>
    public bool IsTransformation =>
        this.TypeName.Contains("process", StringComparison.OrdinalIgnoreCase) ||
        this.TypeName.Contains("lineage", StringComparison.OrdinalIgnoreCase);
}

/// <summary>One Atlas entity, reduced to what a catalogue entry needs.</summary>
public sealed class AtlasEntity
{
    /// <summary>Gets or sets the Atlas GUID. Stable for the life of the entity.</summary>
    public string Guid { get; set; } = string.Empty;

    /// <summary>Gets or sets the Atlas type name, for example hive_table.</summary>
    public string TypeName { get; set; } = string.Empty;

    /// <summary>Gets or sets the short name, for example customer.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the fully qualified name as Atlas states it, cluster suffix
    /// and all - "finance.customer@prod". Kept verbatim because it is the
    /// identifier an operator will paste into Atlas to find the entity again.
    /// </summary>
    public string QualifiedName { get; set; } = string.Empty;

    /// <summary>Gets or sets who owns it, as Atlas records it.</summary>
    public string Owner { get; set; } = string.Empty;

    /// <summary>Gets or sets the description, which is usually the most useful field and usually empty.</summary>
    public string Description { get; set; } = string.Empty;

    /// <summary>Gets or sets the comment, which Hive carries separately from the description.</summary>
    public string Comment { get; set; } = string.Empty;

    /// <summary>Gets or sets when Atlas last changed the entity. The watermark.</summary>
    public DateTimeOffset UpdatedUtc { get; set; }

    /// <summary>Gets or sets the entity's lifecycle state. Deleted entities are not indexed.</summary>
    public string Status { get; set; } = string.Empty;

    /// <summary>Gets the classifications applied to it - Atlas's tags, for example PII.</summary>
    public IList<string> Classifications { get; } = new List<string>();

    /// <summary>Gets the glossary terms it means.</summary>
    public IList<string> Terms { get; } = new List<string>();

    /// <summary>Gets the column names, for a table.</summary>
    public IList<string> Columns { get; } = new List<string>();

    /// <summary>Gets the names of entities feeding this one.</summary>
    public IList<string> Upstream { get; } = new List<string>();

    /// <summary>Gets the names of entities this one feeds.</summary>
    public IList<string> Downstream { get; } = new List<string>();

    /// <summary>Gets a value indicating whether Atlas still considers this entity live.</summary>
    public bool IsActive =>
        this.Status.Length == 0 || this.Status.Equals("ACTIVE", StringComparison.OrdinalIgnoreCase);

    /// <summary>Gets what kind of thing this is, from its Atlas type.</summary>
    public AtlasEntityKind Kind => this.TypeName.ToLowerInvariant() switch
    {
        "hive_db" => AtlasEntityKind.Database,
        "hive_table" => AtlasEntityKind.Table,
        "hive_view" => AtlasEntityKind.Table,
        "hdfs_path" => AtlasEntityKind.Path,
        _ => AtlasEntityKind.Other,
    };

    /// <summary>
    /// Splits an Atlas qualified name into the parts Ranger is asked about.
    ///
    /// "finance.customer@prod" is database finance, table customer, on cluster
    /// prod. The cluster suffix is Atlas's, not Hive's, and asking Ranger about
    /// a table called "customer@prod" would match no policy at all - which
    /// would read as "nobody is granted" and silently drop every entry.
    /// </summary>
    /// <param name="qualifiedName">The Atlas qualified name.</param>
    /// <returns>The database, the object, and the cluster; any may be empty.</returns>
    public static (string Database, string Object, string Cluster) SplitQualifiedName(string qualifiedName)
    {
        if (string.IsNullOrWhiteSpace(qualifiedName))
        {
            return (string.Empty, string.Empty, string.Empty);
        }

        string cluster = string.Empty;
        string name = qualifiedName;

        int at = name.LastIndexOf('@');

        if (at >= 0)
        {
            cluster = name[(at + 1)..];
            name = name[..at];
        }

        int dot = name.IndexOf('.', StringComparison.Ordinal);

        return dot < 0
            ? (name, string.Empty, cluster)
            : (name[..dot], name[(dot + 1)..], cluster);
    }
}
