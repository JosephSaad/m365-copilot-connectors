// ---------------------------------------------------------------------------
// HdfsAclBuilder.cs
// Who may read one file, expressed as grants an external item can carry.
//
// Two sources of truth, combined the way the cluster combines them: the file's
// own POSIX permissions and extended ACL, and any Ranger path policy covering
// it. The Ranger HDFS plugin falls back to the file's permissions when no
// policy matches, so reading both and taking the union is the same answer the
// cluster would give - not an approximation of it.
//
// What is deliberately not represented:
//
//   The owning USER. HDFS grants the owner read through the first permission
//   digit, and that is a person, not a group. Item ACLs here are group-only -
//   expanding memberships into item ACLs turns one HR change into a rewrite of
//   every item, which Microsoft's own guidance warns against - so an owner who
//   is not also in a granted group does not get a grant. The effect is that the
//   index can show a file to fewer people than the cluster would, never more.
//
//   The other-read bit, unless an operator names a group for it. "Everyone with
//   an account on the cluster" and "everyone in the Microsoft 365 tenant" are
//   different sets of people, and quietly treating them as one is how a lake
//   becomes searchable by the whole company.
//
// An empty result is a real answer and means the file is not indexed. See
// PushEngine: an item granted to nobody is accepted by Graph and then returned
// to no one, which looks like success.
// ---------------------------------------------------------------------------

namespace CdpConnector.Source.Acl;

using CdpConnector.Source.Hdfs;
using CdpConnector.Source.Ranger;
using PushCore;

/// <summary>Builds the grants for one HDFS file.</summary>
public sealed class HdfsAclBuilder
{
    private readonly PrincipalResolver principals;
    private readonly string otherReadableGroupId;

    /// <summary>Initializes a new instance of the <see cref="HdfsAclBuilder"/> class.</summary>
    /// <param name="principals">Turns cluster group names into Entra grants.</param>
    /// <param name="otherReadableGroupId">The Entra group world-readable files map to, or empty.</param>
    public HdfsAclBuilder(PrincipalResolver principals, string otherReadableGroupId)
    {
        this.principals = principals;
        this.otherReadableGroupId = otherReadableGroupId;
    }

    /// <summary>Names every cluster group the cluster would let read this file.</summary>
    /// <param name="status">The file's status.</param>
    /// <param name="acl">Its extended ACL, or null when it has none.</param>
    /// <param name="rangerGroups">Groups a Ranger path policy grants read.</param>
    /// <returns>The cluster group names, deduplicated.</returns>
    public static IReadOnlyList<string> ClusterGroups(
        HdfsFileStatus status, HdfsAclStatus? acl, IReadOnlyList<string> rangerGroups)
    {
        var groups = new List<string>();

        // The owning group, but only when the group permission digit actually
        // grants read. A file owned by group "finance" with mode 600 grants
        // finance nothing, and treating ownership as access would be inventing a
        // grant the cluster does not give.
        if (status.GroupCanRead && !string.IsNullOrWhiteSpace(status.Group))
        {
            groups.Add(status.Group);
        }

        if (acl is not null)
        {
            groups.AddRange(acl.GroupsGrantedRead());
        }

        groups.AddRange(rangerGroups);

        return groups
            .Where(group => !string.IsNullOrWhiteSpace(group))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>Builds the grants for one file.</summary>
    /// <param name="status">The file's status.</param>
    /// <param name="acl">Its extended ACL, or null when it has none.</param>
    /// <param name="rangerGroups">Groups a Ranger path policy grants read.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>The grants. Empty means the file is not indexed.</returns>
    public async Task<IReadOnlyList<PushAclEntry>> BuildAsync(
        HdfsFileStatus status,
        HdfsAclStatus? acl,
        IReadOnlyList<string> rangerGroups,
        CancellationToken cancellationToken)
    {
        IReadOnlyList<string> clusterGroups = ClusterGroups(status, acl, rangerGroups);

        List<PushAclEntry> grants = await this.principals.ResolveAsync(clusterGroups, cancellationToken);

        if (status.OtherCanRead && !string.IsNullOrWhiteSpace(this.otherReadableGroupId))
        {
            // Only because an operator wrote down which group "everyone on the
            // cluster" means in this tenant.
            grants.Add(new PushAclEntry(PushAclType.Group, this.otherReadableGroupId));
        }

        return grants
            .GroupBy(grant => grant.Key(), StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .ToList();
    }
}
