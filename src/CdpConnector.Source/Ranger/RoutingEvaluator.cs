// ---------------------------------------------------------------------------
// RoutingEvaluator.cs
// "Own it, index it. Entitle it at the source, call it." - decided per table
// and per path, from the cluster's own policies rather than from a list someone
// maintains.
//
// The rules below are ordered and they fail closed. Each one describes a way
// that one indexed copy of a row cannot represent what the source would show
// two different people:
//
//   1. A row filter or a column mask is a per-user transform. An index holds
//      one copy. Indexing it either leaks the unfiltered rows to everyone
//      granted the item, or stores the masked version and lies to the people
//      entitled to the real one. Neither is a bug that can be fixed by trying
//      harder; the table belongs in a live query.
//   2. A deny is not mirrored. Graph has deny ACEs and they take precedence,
//      which makes mirroring look safe - but a deny only protects while the
//      translation is right every time, and a translation that drifts fails
//      open. Refusing to index is the version that fails closed.
//   3. A column-scoped grant is the same problem as a mask wearing different
//      clothes: different people are entitled to different parts of the row.
//
// Anything not caught by those is indexable, and the groups granted read come
// back with it. A refusal is not an error - the run continues and the report
// records it - because "this table is queried live instead" is an architecture,
// not a failure.
// ---------------------------------------------------------------------------

namespace CdpConnector.Source.Ranger;

/// <summary>What should happen to one table or path.</summary>
public enum RoutingVerdict
{
    /// <summary>Copy it into the index, with the grants that came back.</summary>
    Index = 0,

    /// <summary>Leave it in the cluster and query it live under the user's own identity.</summary>
    LiveQuery = 1,
}

/// <summary>The decision about one resource, and why.</summary>
public sealed class RoutingDecision
{
    /// <summary>Initializes a new instance of the <see cref="RoutingDecision"/> class.</summary>
    /// <param name="resource">The table or path decided about.</param>
    /// <param name="verdict">What to do with it.</param>
    /// <param name="reason">Why, in a sentence an operator can act on.</param>
    /// <param name="policyIds">The policies that decided it.</param>
    /// <param name="groups">The groups granted read, when the verdict is Index.</param>
    public RoutingDecision(
        string resource,
        RoutingVerdict verdict,
        string reason,
        IReadOnlyList<long> policyIds,
        IReadOnlyList<string> groups)
    {
        this.Resource = resource;
        this.Verdict = verdict;
        this.Reason = reason;
        this.PolicyIds = policyIds;
        this.Groups = groups;
    }

    /// <summary>Gets the table or path this is about.</summary>
    public string Resource { get; }

    /// <summary>Gets what to do with it.</summary>
    public RoutingVerdict Verdict { get; }

    /// <summary>Gets why.</summary>
    public string Reason { get; }

    /// <summary>Gets the Ranger policy IDs behind the decision, so it can be traced.</summary>
    public IReadOnlyList<long> PolicyIds { get; }

    /// <summary>Gets the groups Ranger grants read, when the verdict is Index.</summary>
    public IReadOnlyList<string> Groups { get; }

    /// <summary>Gets a value indicating whether this resource may be indexed.</summary>
    public bool MayIndex => this.Verdict == RoutingVerdict.Index;
}

/// <summary>Applies the routing rules to a service's policies.</summary>
public sealed class RoutingEvaluator
{
    private readonly IReadOnlyList<RangerPolicy> policies;

    /// <summary>Initializes a new instance of the <see cref="RoutingEvaluator"/> class.</summary>
    /// <param name="policies">Every policy on the relevant Ranger service.</param>
    public RoutingEvaluator(IReadOnlyList<RangerPolicy> policies)
    {
        this.policies = policies;
    }

    /// <summary>Decides what should happen to one Hive or Impala table.</summary>
    /// <param name="database">The database name.</param>
    /// <param name="table">The table name.</param>
    /// <returns>The decision.</returns>
    public RoutingDecision EvaluateTable(string database, string table)
    {
        string resource = database + "." + table;

        List<RangerPolicy> relevant = this.policies
            .Where(policy => policy.Enabled)
            .Where(policy => policy.Covers("database", database) && policy.Covers("table", table))
            .ToList();

        // Rule 1. One definition serves Hive and Impala, so this covers both
        // engines whichever one the connector reads through.
        List<RangerPolicy> transforms = relevant
            .Where(policy => policy.PolicyType is RangerPolicyType.RowFilter or RangerPolicyType.Masking)
            .ToList();

        if (transforms.Count > 0)
        {
            bool filtered = transforms.Any(policy => policy.PolicyType == RangerPolicyType.RowFilter);

            return new RoutingDecision(
                resource,
                RoutingVerdict.LiveQuery,
                filtered
                    ? "Ranger applies a row-level filter to this table. A filter shows different rows to " +
                      "different people at query time, and an index holds one copy, so this table is queried " +
                      "live rather than indexed."
                    : "Ranger masks at least one column of this table. A mask shows different values to " +
                      "different people at query time, and an index holds one copy, so this table is queried " +
                      "live rather than indexed.",
                transforms.Select(policy => policy.Id).ToList(),
                Array.Empty<string>());
        }

        // Rule 2.
        List<RangerPolicy> denies = relevant.Where(policy => policy.Deny.Count > 0).ToList();

        if (denies.Count > 0)
        {
            return new RoutingDecision(
                resource,
                RoutingVerdict.LiveQuery,
                "Ranger denies access to this table for at least one principal. Deny rules are not mirrored " +
                "into the index, because a mirrored deny that drifts fails open; the table is queried live " +
                "so the source keeps enforcing its own denial.",
                denies.Select(policy => policy.Id).ToList(),
                Array.Empty<string>());
        }

        // Rule 3.
        List<RangerPolicy> columnScoped = relevant
            .Where(policy => policy.IsColumnScoped && policy.Allow.Any(item => item.GrantsRead))
            .ToList();

        if (columnScoped.Count > 0)
        {
            return new RoutingDecision(
                resource,
                RoutingVerdict.LiveQuery,
                "Ranger grants access to some columns of this table rather than all of them. Different people " +
                "are entitled to different parts of each row, which one indexed copy cannot represent.",
                columnScoped.Select(policy => policy.Id).ToList(),
                Array.Empty<string>());
        }

        List<string> groups = relevant
            .Where(policy => policy.PolicyType == RangerPolicyType.Access)
            .SelectMany(policy => policy.Allow.Where(item => item.GrantsRead).SelectMany(item => item.Groups))
            .Where(group => !string.IsNullOrWhiteSpace(group))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (groups.Count == 0)
        {
            // Not a refusal to index - a refusal to guess. Without a group grant
            // there is nobody to put on the item's ACL, and an item granted to
            // nobody is indexed and returned to no one.
            return new RoutingDecision(
                resource,
                RoutingVerdict.LiveQuery,
                "No Ranger policy grants select on this table to any group. There is no principal to put on " +
                "the indexed items, and an item granted to nobody is indexed and then returned to nobody.",
                relevant.Select(policy => policy.Id).ToList(),
                Array.Empty<string>());
        }

        return new RoutingDecision(
            resource,
            RoutingVerdict.Index,
            "Table-wide select granted to " + groups.Count + " group(s), with no row filter, mask or deny.",
            relevant.Select(policy => policy.Id).ToList(),
            groups);
    }

    /// <summary>Decides what should happen to one HDFS path.</summary>
    /// <param name="path">The absolute path.</param>
    /// <returns>The decision, whose Groups are the Ranger-granted groups to add to the file's own ACL.</returns>
    public RoutingDecision EvaluatePath(string path)
    {
        List<RangerPolicy> relevant = this.policies
            .Where(policy => policy.Enabled && policy.CoversPath(path))
            .ToList();

        List<RangerPolicy> denies = relevant.Where(policy => policy.Deny.Count > 0).ToList();

        if (denies.Count > 0)
        {
            return new RoutingDecision(
                path,
                RoutingVerdict.LiveQuery,
                "Ranger denies access to this path for at least one principal. Deny rules are not mirrored " +
                "into the index, so nothing under it is indexed.",
                denies.Select(policy => policy.Id).ToList(),
                Array.Empty<string>());
        }

        List<string> groups = relevant
            .Where(policy => policy.PolicyType == RangerPolicyType.Access)
            .SelectMany(policy => policy.Allow.Where(item => item.GrantsRead).SelectMany(item => item.Groups))
            .Where(group => !string.IsNullOrWhiteSpace(group))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        // Unlike a table, a path with no Ranger grant is not refused here: the
        // Ranger HDFS plugin falls back to the file's own POSIX permissions and
        // ACL when no policy matches, and those are read separately. An empty
        // group list means "Ranger adds nothing", not "nobody may read it".
        return new RoutingDecision(
            path,
            RoutingVerdict.Index,
            groups.Count == 0
                ? "No Ranger path policy matches; the file's own POSIX permissions and ACL decide."
                : "Ranger grants read to " + groups.Count + " group(s) on this path.",
            relevant.Select(policy => policy.Id).ToList(),
            groups);
    }
}
