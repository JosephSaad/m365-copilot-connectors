// ---------------------------------------------------------------------------
// RangerPolicy.cs
// The parts of a Ranger policy this connector reads, and nothing else.
//
// Ranger's policy document is large and most of it describes things an indexer
// has no opinion about - audit settings, validity schedules, delegated admin.
// What matters here is four questions:
//
//   which resource does this policy cover
//   does it grant read or select, and to which groups
//   does it deny anything
//   does it filter rows or mask columns
//
// The last one is the important one. A row filter or a column mask is a
// per-user transform applied when a query runs, and an index has no way to
// reproduce it: one copy of a row cannot be simultaneously filtered for one
// person and not for another. So a table carrying either is not something to
// index badly - it is something to leave in the cluster and query live.
// ---------------------------------------------------------------------------

namespace CdpConnector.Source.Ranger;

/// <summary>What a policy does, by Ranger's own numbering.</summary>
public enum RangerPolicyType
{
    /// <summary>Grants or denies access.</summary>
    Access = 0,

    /// <summary>Masks a column's values per user.</summary>
    Masking = 1,

    /// <summary>Filters a table's rows per user.</summary>
    RowFilter = 2,
}

/// <summary>One grant or denial inside a policy.</summary>
public sealed class RangerPolicyItem
{
    /// <summary>Gets the groups this item names.</summary>
    public IList<string> Groups { get; } = new List<string>();

    /// <summary>Gets the users this item names.</summary>
    public IList<string> Users { get; } = new List<string>();

    /// <summary>Gets the access types this item allows, lower case: read, select, write.</summary>
    public IList<string> Accesses { get; } = new List<string>();

    /// <summary>Gets a value indicating whether this item grants read or select.</summary>
    public bool GrantsRead =>
        this.Accesses.Any(access =>
            access.Equals("read", StringComparison.OrdinalIgnoreCase) ||
            access.Equals("select", StringComparison.OrdinalIgnoreCase));
}

/// <summary>One Ranger policy, reduced to what an indexer needs.</summary>
public sealed class RangerPolicy
{
    /// <summary>Gets or sets the policy ID, quoted in the routing report so a decision can be traced.</summary>
    public long Id { get; set; }

    /// <summary>Gets or sets the policy name.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>Gets or sets a value indicating whether the policy is enabled. A disabled policy decides nothing.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>Gets or sets what the policy does.</summary>
    public RangerPolicyType PolicyType { get; set; }

    /// <summary>Gets the resource values by resource name: path, database, table, column.</summary>
    public IDictionary<string, IList<string>> Resources { get; } =
        new Dictionary<string, IList<string>>(StringComparer.OrdinalIgnoreCase);

    /// <summary>Gets the items that grant.</summary>
    public IList<RangerPolicyItem> Allow { get; } = new List<RangerPolicyItem>();

    /// <summary>Gets the items that deny.</summary>
    public IList<RangerPolicyItem> Deny { get; } = new List<RangerPolicyItem>();

    /// <summary>Gets the values configured for one resource, or an empty list.</summary>
    /// <param name="name">The resource name, for example "table".</param>
    /// <returns>Its values.</returns>
    public IList<string> Resource(string name)
    {
        return this.Resources.TryGetValue(name, out IList<string>? values) ? values : Array.Empty<string>();
    }

    /// <summary>
    /// Gets a value indicating whether this policy covers only some columns.
    ///
    /// A column-scoped grant means different people see different columns of the
    /// same row, which one indexed copy cannot represent - the same reason a
    /// mask cannot be indexed. Only a policy covering every column, or naming no
    /// column resource at all, describes something safe to copy.
    /// </summary>
    public bool IsColumnScoped
    {
        get
        {
            IList<string> columns = this.Resource("column");

            return columns.Count > 0 && !columns.All(value => value == "*");
        }
    }

    /// <summary>Gets a value indicating whether a resource value matches, treating a trailing * as a prefix.</summary>
    /// <param name="resourceName">The resource to test, for example "table".</param>
    /// <param name="candidate">The value to test against it.</param>
    /// <returns>True when the policy covers the candidate.</returns>
    public bool Covers(string resourceName, string candidate)
    {
        foreach (string value in this.Resource(resourceName))
        {
            if (value == "*")
            {
                return true;
            }

            if (value.EndsWith('*'))
            {
                if (candidate.StartsWith(value[..^1], StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }

                continue;
            }

            if (string.Equals(value, candidate, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Gets a value indicating whether this policy's path resource covers a file.
    ///
    /// Ranger path policies are prefixes with an explicit recursive flag; this
    /// treats every path value as covering its subtree, which is the
    /// conservative reading. Over-matching here can only ADD a grant that
    /// Ranger would also have granted at a deeper level, or refuse indexing of
    /// a subtree that a deny covers - and refusing too much is the safe error.
    /// </summary>
    /// <param name="path">The absolute HDFS path.</param>
    /// <returns>True when the policy covers it.</returns>
    public bool CoversPath(string path)
    {
        foreach (string value in this.Resource("path"))
        {
            string prefix = value.TrimEnd('*').TrimEnd('/');

            if (prefix.Length == 0 ||
                path.Equals(prefix, StringComparison.OrdinalIgnoreCase) ||
                path.StartsWith(prefix + "/", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }
}
