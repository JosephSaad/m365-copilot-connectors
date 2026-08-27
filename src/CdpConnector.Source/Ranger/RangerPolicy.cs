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
//
// Two per-resource flags are read alongside the values because each of them
// inverts or narrows the resource rather than decorating it. isExcludes turns
// "these tables" into "every table but these", so dropping it reads a policy as
// the exact inverse of itself; isRecursive is what separates a grant on one
// directory from a grant on everything beneath it. Ranger's own default for
// both, when the policy document omits them, is false, and that is the default
// used here.
//
// Matching is Ranger's, not an approximation of it: RangerDefaultResourceMatcher
// with wildCard enabled is a glob, so '*' and '?' mean what they mean anywhere
// in the value, not only in a trailing position. A row-filter policy written
// against '*_pii' has to match customer_pii here or the refusal that keeps a
// filtered table out of the index never fires.
// ---------------------------------------------------------------------------

namespace CdpConnector.Source.Ranger;

using System.Collections.Concurrent;
using System.Text;
using System.Text.RegularExpressions;

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

/// <summary>Ranger's two per-resource modifiers, each of which changes what the values mean.</summary>
public sealed class RangerResourceFlags
{
    /// <summary>
    /// Gets or sets a value indicating whether the values name what the policy
    /// does NOT cover. Ranger supports this on Hive database, table and column.
    /// </summary>
    public bool IsExcludes { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether a path value covers everything
    /// beneath it as well as itself. Meaningful only for a path resource.
    /// </summary>
    public bool IsRecursive { get; set; }
}

/// <summary>One Ranger policy, reduced to what an indexer needs.</summary>
public sealed class RangerPolicy
{
    /// <summary>
    /// Translated resource values, keyed by the value and the matching mode it
    /// was translated for.
    ///
    /// A policy set is read once per crawl but asked about every table and
    /// every file, so the translation is done once. The keys come from Ranger's
    /// own policy document, so the cache is bounded by the size of that.
    /// </summary>
    private static readonly ConcurrentDictionary<string, Regex> Patterns =
        new(StringComparer.Ordinal);

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

    /// <summary>
    /// Gets the per-resource flags, under the same resource names as
    /// <see cref="Resources"/>.
    ///
    /// They are held beside the values rather than inside them so that a
    /// resource set through <see cref="Resources"/> alone reads exactly as
    /// Ranger reads a policy document that omits both flags: no entry here
    /// means false for each, which is Ranger's own default.
    /// </summary>
    public IDictionary<string, RangerResourceFlags> ResourceFlags { get; } =
        new Dictionary<string, RangerResourceFlags>(StringComparer.OrdinalIgnoreCase);

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

    /// <summary>Sets one resource's values together with the flags that qualify them.</summary>
    /// <param name="name">The resource name, for example "table".</param>
    /// <param name="values">Its values.</param>
    /// <param name="isExcludes">True when the values name what the policy does not cover.</param>
    /// <param name="isRecursive">True when a path value covers everything beneath it.</param>
    public void SetResource(string name, IList<string> values, bool isExcludes = false, bool isRecursive = false)
    {
        this.Resources[name] = values;
        this.ResourceFlags[name] = new RangerResourceFlags
        {
            IsExcludes = isExcludes,
            IsRecursive = isRecursive,
        };
    }

    /// <summary>Gets a value indicating whether one resource's values are exclusions.</summary>
    /// <param name="name">The resource name.</param>
    /// <returns>True when the values name what the policy does not cover.</returns>
    public bool IsExcludes(string name)
    {
        return this.ResourceFlags.TryGetValue(name, out RangerResourceFlags? flags) && flags.IsExcludes;
    }

    /// <summary>Gets a value indicating whether one resource's values cover their subtree.</summary>
    /// <param name="name">The resource name.</param>
    /// <returns>True when a path value covers everything beneath it.</returns>
    public bool IsRecursive(string name)
    {
        return this.ResourceFlags.TryGetValue(name, out RangerResourceFlags? flags) && flags.IsRecursive;
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

            if (columns.Count == 0)
            {
                return false;
            }

            // "every column except these" is a subset of the row too, and the
            // subset it describes cannot be read off the values, so it counts
            // as scoped whatever those values are. Refusing to index is the
            // choice being made where the reading is uncertain.
            if (this.IsExcludes("column"))
            {
                return true;
            }

            return !columns.All(value => value == "*");
        }
    }

    /// <summary>
    /// Gets a value indicating whether a resource value matches, as Ranger's own
    /// wildcard matcher would match it: a glob honouring '*' and '?' anywhere in
    /// the value, and honouring isExcludes.
    ///
    /// Case-insensitively, which is right for the resources this method is
    /// asked about - database, table and column are Hive identifiers. A PATH is
    /// not, and CoversPath matches one case-sensitively.
    /// </summary>
    /// <param name="resourceName">The resource to test, for example "table".</param>
    /// <param name="candidate">The value to test against it.</param>
    /// <returns>True when the policy covers the candidate.</returns>
    public bool Covers(string resourceName, string candidate)
    {
        IList<string> values = this.Resource(resourceName);
        bool matched = values.Any(value => Matches(value, candidate));

        // An exclusion inverts the resource: "every table in finance except
        // salaries". The inversion applies only where the policy actually named
        // something to exclude - negating an empty value list would turn "this
        // policy puts no constraint on tables" into "this policy matches no
        // table at all", which is a different policy.
        if (values.Count > 0 && this.IsExcludes(resourceName))
        {
            return !matched;
        }

        return matched;
    }

    /// <summary>
    /// Gets a value indicating whether this policy's path resource covers a
    /// path, read exactly as Ranger reads it.
    ///
    /// A recursive value covers the path it names and everything beneath it. A
    /// non-recursive one covers only the path it names, and a wildcard inside it
    /// matches within a single path segment and never crosses a '/', so a
    /// non-recursive grant on a directory grants nothing on the files in it.
    /// That is the faithful reading, and it is the one a GRANT needs: a grant
    /// that covered the subtree anyway would put files into the index with an
    /// ACL the cluster never gave them. A deny needs the opposite error and has
    /// its own method, <see cref="CoversPathForDeny"/>.
    /// </summary>
    /// <param name="path">The absolute HDFS path.</param>
    /// <returns>True when the policy covers it.</returns>
    public bool CoversPath(string path)
    {
        IList<string> values = this.Resource("path");
        bool recursive = this.IsRecursive("path");
        bool matched = values.Any(value => PathMatches(value, path, recursive));

        // Same reading as Covers gives an excluded table, and the same refusal
        // to negate an empty list.
        if (values.Count > 0 && this.IsExcludes("path"))
        {
            return !matched;
        }

        return matched;
    }

    /// <summary>
    /// Gets a value indicating whether this policy's path resource covers a path
    /// for the purpose of refusing to index it.
    ///
    /// Deliberately not the same question as <see cref="CoversPath"/>. A grant
    /// read too widely over-grants; a deny read too narrowly fails open, which
    /// is the failure this connector exists to avoid. So a deny is read with
    /// whichever setting of isRecursive disqualifies more: the flag is ignored
    /// and every value is treated as covering its whole subtree, which is the
    /// same as saying the deny catches the candidate or any ancestor of it.
    ///
    /// isExcludes is still honoured, because ignoring it does not widen a deny
    /// consistently - it would union a set with its own complement and stop the
    /// entire crawl. Where the values are excluded, the negation is taken
    /// against the NON-recursive reading, since that is the narrower match and
    /// so the wider refusal.
    /// </summary>
    /// <param name="path">The absolute HDFS path.</param>
    /// <returns>True when a deny in this policy should stop the path being indexed.</returns>
    public bool CoversPathForDeny(string path)
    {
        IList<string> values = this.Resource("path");

        if (values.Count == 0)
        {
            return false;
        }

        if (this.IsExcludes("path"))
        {
            return !values.Any(value => PathMatches(value, path, recursive: false));
        }

        return values.Any(value => PathMatches(value, path, recursive: true));
    }

    /// <summary>Matches one resource value against a candidate, Ranger's wildcards included.</summary>
    /// <param name="value">The policy's value.</param>
    /// <param name="candidate">The database, table or column name being tested.</param>
    /// <returns>True when the value matches.</returns>
    private static bool Matches(string value, string candidate)
    {
        if (value == "*")
        {
            return true;
        }

        if (value.IndexOf('*') < 0 && value.IndexOf('?') < 0)
        {
            return string.Equals(value, candidate, StringComparison.OrdinalIgnoreCase);
        }

        // Case-insensitive, because a Hive identifier is: CUSTOMER and
        // customer are one table, and Ranger matches them as one.
        return Pattern(
            "v:" + value, value, star: ".*", question: ".", suffix: string.Empty, ignoreCase: true)
            .IsMatch(candidate);
    }

    /// <summary>Matches one path value against a candidate path.</summary>
    /// <param name="value">The policy's path value.</param>
    /// <param name="path">The absolute HDFS path being tested.</param>
    /// <param name="recursive">True to cover everything beneath the value as well.</param>
    /// <returns>True when the value matches.</returns>
    private static bool PathMatches(string value, string path, bool recursive)
    {
        string trimmed = Normalise(value);
        string candidate = Normalise(path);

        // A path wildcard is a segment wildcard. "/data/*" names the entries of
        // /data and not the tree under them; what reaches under them is the
        // recursive flag, and only the recursive flag.
        // Case-SENSITIVE, unlike a Hive name. HDFS is a case-sensitive
        // filesystem: /data/Finance and /data/finance are two directories that
        // can hold different files with different permissions. Folding them
        // together would apply a grant written for one to the other, which is
        // an over-grant - and this repository already says so where it
        // normalises Settings:HdfsRoots and deliberately leaves their case
        // alone.
        return Pattern(
            (recursive ? "r:" : "n:") + trimmed,
            trimmed,
            star: "[^/]*",
            question: "[^/]",
            suffix: recursive ? "(/.*)?" : string.Empty,
            ignoreCase: false)
            .IsMatch(candidate);
    }

    /// <summary>Drops a trailing separator so that /data and /data/ are one path.</summary>
    /// <param name="path">The path to normalise.</param>
    /// <returns>The path without its trailing separator.</returns>
    private static string Normalise(string path)
    {
        return path.Length > 1 && path.EndsWith('/') ? path.TrimEnd('/') : path;
    }

    /// <summary>Translates a value into an anchored glob.</summary>
    /// <param name="key">The cache key, distinguishing the matching mode.</param>
    /// <param name="value">The value to translate.</param>
    /// <param name="star">What '*' becomes.</param>
    /// <param name="question">What '?' becomes.</param>
    /// <param name="suffix">Appended before the anchor, to reach under a recursive path.</param>
    /// <param name="ignoreCase">True for a Hive identifier, false for an HDFS path.</param>
    /// <returns>The compiled expression.</returns>
    private static Regex Pattern(
        string key, string value, string star, string question, string suffix, bool ignoreCase)
    {
        return Patterns.GetOrAdd(key, _ =>
        {
            // Anchored with \A and \z rather than ^ and $: in .NET, $ also
            // matches before a trailing newline, which would let a name ending
            // in one match a policy value that does not.
            var builder = new StringBuilder(@"\A");

            foreach (char character in value)
            {
                builder.Append(character switch
                {
                    '*' => star,
                    '?' => question,
                    _ => Regex.Escape(character.ToString()),
                });
            }

            builder.Append(suffix).Append(@"\z");

            // CultureInvariant wherever case is folded at all: without it the
            // current culture decides what upper case means, and under a
            // Turkish culture 'I' and 'i' stop being the same letter, so a
            // policy would match a different set of tables on a differently
            // configured host.
            RegexOptions options = ignoreCase
                ? RegexOptions.IgnoreCase | RegexOptions.CultureInvariant
                : RegexOptions.CultureInvariant;

            return new Regex(builder.ToString(), options);
        });
    }
}
