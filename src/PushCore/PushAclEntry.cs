// ---------------------------------------------------------------------------
// PushAclEntry.cs
// One grant on one item, for a source whose items are not all readable by the
// same people.
//
// Two deliberate absences.
//
// There is no deny. Graph supports deny ACEs and they take precedence, which
// makes them look like the safe way to mirror a source's deny rules - but a
// deny only protects if it is translated correctly EVERY time, and a mapping
// that drifts fails open. A source with denies in scope is a source to route to
// a live query instead of indexing, so the type simply cannot express one.
//
// There is no user. Group principals only, as in the shared BuildAcl: expanding
// a group's membership into item ACLs turns one HR change into a rewrite of
// every item that person could see, and Microsoft's own guidance says not to.
// Where a source's groups are not Entra groups, mirror them as external groups
// and reference them here by ID.
// ---------------------------------------------------------------------------

namespace PushCore;

/// <summary>What kind of principal a grant names.</summary>
public enum PushAclType
{
    /// <summary>A Microsoft Entra group, named by object ID.</summary>
    Group = 0,

    /// <summary>
    /// An external group registered on this connection, named by its ID. This is
    /// how a group that does not exist in Entra - a cluster-local Hadoop group,
    /// a source system's own role - reaches an item ACL.
    /// </summary>
    ExternalGroup = 1,
}

/// <summary>One grant. There is no deny, by design; see the file header.</summary>
public sealed class PushAclEntry
{
    /// <summary>Initializes a new instance of the <see cref="PushAclEntry"/> class.</summary>
    /// <param name="type">Entra group, or an external group on this connection.</param>
    /// <param name="value">The group object ID, or the external group ID.</param>
    public PushAclEntry(PushAclType type, string value)
    {
        this.Type = type;
        this.Value = value;
    }

    /// <summary>Gets the kind of principal.</summary>
    public PushAclType Type { get; }

    /// <summary>Gets the group object ID, or the external group ID.</summary>
    public string Value { get; }

    /// <summary>Gets a value used to deduplicate grants that name the same principal.</summary>
    /// <returns>A stable key for this grant.</returns>
    public string Key()
    {
        return this.Type + ":" + this.Value;
    }
}
