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
//
// One asymmetry below is deliberate, and it is the only place this file departs
// from reading Ranger literally. The two ways of being wrong are not each
// other's mirror image:
//
//   a GRANT matched too widely puts an item in the index carrying an ACL the
//   cluster never gave it, which is the leak;
//   a DENY matched too narrowly lets that same item through, which is the same
//   leak arrived at from the other side.
//
// So grants are matched exactly as Ranger's own matcher would match them, and
// denies are matched conservatively: a deny whose path covers the candidate or
// any ancestor of it stops the candidate being indexed, whatever the policy's
// isRecursive flag says. RangerPolicy.CoversPathForDeny is that reading, kept
// separate from CoversPath so the widening is visible where it is used rather
// than baked into a method whose name promises fidelity. The cost is that a
// file Ranger would have allowed is queried live instead of indexed, which is
// the error worth making.
//
// The widening applies to paths only. A table has no subtree, so there is no
// recursion to guess at: a database or table name either matches the policy's
// glob or it does not, and both sides of the policy can be read literally.
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

        // Rule 2. Read from the same relevant set as the grants: a table name
        // matches this policy's glob or it does not, so there is nothing here
        // to widen the way a path's recursion has to be widened.
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

    /// <summary>
    /// Decides who may see the CATALOGUE ENTRY for a table - its name, its
    /// columns, its owner and its lineage - as opposed to its rows.
    ///
    /// This deliberately answers differently from <see cref="EvaluateTable"/>,
    /// and the difference is the point of having a catalogue at all.
    ///
    /// A row filter governs which ROWS a person sees; a column mask governs
    /// which VALUES they see. Neither hides the table's existence, its column
    /// names or its owner from somebody granted select - they see all of that
    /// the moment they query it. So the metadata of a filtered or masked table
    /// is indexable for exactly the people Ranger grants select, even though its
    /// data is not. Those are frequently the tables a catalogue is most needed
    /// for, because their contents cannot be indexed at all.
    ///
    /// What still refuses: a deny, and a table nobody is granted. A deny is not
    /// mirrored anywhere in this connector, and a catalogue entry granted to
    /// nobody is indexed and returned to nobody.
    ///
    /// A column-scoped grant does NOT refuse here either, but it narrows: only
    /// the columns the grant names are described, because a column name can
    /// disclose as much as a value - a column called "hiv_status" says
    /// something by existing - and somebody granted three columns of a table
    /// has not been shown the other forty.
    /// </summary>
    /// <param name="database">The database name.</param>
    /// <param name="table">The table name.</param>
    /// <returns>The decision. Groups are who may see the entry; empty means nobody, so no entry.</returns>
    public RoutingDecision EvaluateCatalogueEntry(string database, string table)
    {
        string resource = database + "." + table;

        List<RangerPolicy> relevant = this.policies
            .Where(policy => policy.Enabled)
            .Where(policy => policy.Covers("database", database) && policy.Covers("table", table))
            .ToList();

        List<RangerPolicy> denies = relevant.Where(policy => policy.Deny.Count > 0).ToList();

        if (denies.Count > 0)
        {
            return new RoutingDecision(
                resource,
                RoutingVerdict.LiveQuery,
                "Ranger denies access to this table for at least one principal, so its catalogue entry is not " +
                "indexed either. Deny rules are not mirrored, and a description of a table is still a " +
                "disclosure about it.",
                denies.Select(policy => policy.Id).ToList(),
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
            return new RoutingDecision(
                resource,
                RoutingVerdict.LiveQuery,
                "No Ranger policy grants select on this table to any group, so there is nobody to grant its " +
                "catalogue entry to.",
                relevant.Select(policy => policy.Id).ToList(),
                Array.Empty<string>());
        }

        bool transformed = relevant.Any(
            policy => policy.PolicyType is RangerPolicyType.RowFilter or RangerPolicyType.Masking);

        return new RoutingDecision(
            resource,
            RoutingVerdict.Index,
            transformed
                ? "Select granted to " + groups.Count + " group(s). The table's ROWS are row-filtered or masked " +
                  "and are not indexed, but its description is what those people already see when they query it."
                : "Select granted to " + groups.Count + " group(s).",
            relevant.Select(policy => policy.Id).ToList(),
            groups);
    }

    /// <summary>
    /// Decides who may see the catalogue entry for a DATABASE, as opposed to for
    /// one table in it.
    ///
    /// A database entry names the database and nothing inside it, so the people
    /// entitled to it are the people Ranger grants select on anything it holds.
    /// That cannot be asked as EvaluateCatalogueEntry(database, "*"): "*" there
    /// is a candidate table NAME, matched against each policy's table glob, so
    /// it matches a policy written over "*" and no other. A cluster whose
    /// policies name their tables one at a time - the ordinary arrangement where
    /// a database holds tables of different sensitivities - would catalogue no
    /// databases at all, silently.
    ///
    /// The deny rule is the table rule: a deny anywhere in the database refuses
    /// the entry, because a deny is not mirrored anywhere in this connector.
    /// </summary>
    /// <param name="database">The database name.</param>
    /// <returns>The decision. Groups are who may see the entry; empty means nobody, so no entry.</returns>
    public RoutingDecision EvaluateDatabaseEntry(string database)
    {
        List<RangerPolicy> relevant = this.policies
            .Where(policy => policy.Enabled)
            .Where(policy => policy.Covers("database", database))
            .ToList();

        List<RangerPolicy> denies = relevant.Where(policy => policy.Deny.Count > 0).ToList();

        if (denies.Count > 0)
        {
            return new RoutingDecision(
                database,
                RoutingVerdict.LiveQuery,
                "Ranger denies access to something in this database for at least one principal, so the " +
                "database's own catalogue entry is not indexed either. Deny rules are not mirrored.",
                denies.Select(policy => policy.Id).ToList(),
                Array.Empty<string>());
        }

        List<string> groups = relevant
            .Where(policy => policy.PolicyType == RangerPolicyType.Access)
            .SelectMany(policy => policy.Allow.Where(item => item.GrantsRead).SelectMany(item => item.Groups))
            .Where(group => !string.IsNullOrWhiteSpace(group))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        return groups.Count == 0
            ? new RoutingDecision(
                database,
                RoutingVerdict.LiveQuery,
                "No Ranger policy grants select on anything in this database to any group, so there is " +
                "nobody to grant its catalogue entry to.",
                relevant.Select(policy => policy.Id).ToList(),
                Array.Empty<string>())
            : new RoutingDecision(
                database,
                RoutingVerdict.Index,
                "Select granted somewhere in this database to " + groups.Count + " group(s).",
                relevant.Select(policy => policy.Id).ToList(),
                groups);
    }

    /// <summary>
    /// The columns of a table a catalogue entry may describe, given its
    /// policies. Null means every column; an empty list means none.
    ///
    /// The rule is the INTERSECTION of what the granting policies name, not the
    /// union, and the difference is a disclosure. One item carries one set of
    /// column names and the union of every granting policy's groups, so a
    /// column named by any one policy would be shown to every group on the item.
    /// Two ordinary policies on one table - ward-admin granted
    /// [patient_id, admission_date], clinicians granted [hiv_status] - would
    /// then tell ward-admin that a column called hiv_status exists, which is the
    /// exact disclosure the narrowing was written to prevent.
    ///
    /// So a column is described only when EVERY granting policy covers it. A
    /// policy naming no column, or naming "*", grants all of them and therefore
    /// narrows nothing. When the granting policies name disjoint sets the
    /// intersection is empty and no column is described at all, which is the
    /// right way round to be wrong: an entry that under-describes is a search
    /// that misses, and an entry that over-describes is a leak.
    /// </summary>
    /// <param name="database">The database name.</param>
    /// <param name="table">The table name.</param>
    /// <returns>The describable column names, or null when nothing constrains them.</returns>
    public IReadOnlyList<string>? CatalogueColumns(string database, string table)
    {
        List<RangerPolicy> granting = this.policies
            .Where(policy => policy.Enabled && policy.PolicyType == RangerPolicyType.Access)
            .Where(policy => policy.Covers("database", database) && policy.Covers("table", table))
            .Where(policy => policy.Allow.Any(item => item.GrantsRead))
            .ToList();

        List<string>? describable = null;

        foreach (RangerPolicy policy in granting)
        {
            IList<string> named = policy.Resource("column");

            if (named.Count == 0 || named.Contains("*"))
            {
                continue;
            }

            describable = describable is null
                ? named.Distinct(StringComparer.OrdinalIgnoreCase).ToList()
                : describable.Where(column => named.Contains(column, StringComparer.OrdinalIgnoreCase)).ToList();
        }

        return describable;
    }

    /// <summary>Decides what should happen to one HDFS path.</summary>
    /// <param name="path">The absolute path.</param>
    /// <returns>The decision, whose Groups are the Ranger-granted groups to add to the file's own ACL.</returns>
    public RoutingDecision EvaluatePath(string path)
    {
        List<RangerPolicy> enabled = this.policies.Where(policy => policy.Enabled).ToList();

        // The asymmetry described in the header, at the one place it exists.
        // CoversPathForDeny ignores the policy's isRecursive flag and asks
        // whether the deny covers this path or anything above it, so a deny
        // written non-recursively on a directory still stops the files inside
        // it. CoversPath, used for the grants below, does not: a grant that
        // reached under a directory Ranger stopped at would hand out access
        // nobody granted.
        List<RangerPolicy> denies = enabled
            .Where(policy => policy.Deny.Count > 0 && policy.CoversPathForDeny(path))
            .ToList();

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

        List<RangerPolicy> relevant = enabled.Where(policy => policy.CoversPath(path)).ToList();

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
