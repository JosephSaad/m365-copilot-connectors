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
//
// The one thing this file decides that HdfsModels cannot is which reading of
// the group permission digit applies. On a file with an extended ACL that digit
// is the ACL MASK, every named entry's effective permission is its own bits AND
// the mask, and the owning group's own permission is the "group::" entry; on a
// file without one the digit is the owning group's, as it reads, and there is
// no mask. Only here are both halves - the mode and the entries - in hand at
// once, so only here can the question be settled.
//
// The mask is read from the FILE STATUS permission and passed in. HdfsAclStatus
// carries a permission of its own that WebHDFS populates, and taking the mask
// from there would pair the entries with the mode from the same response; the
// status is preferred because it is the one every caller has. An HdfsAclStatus
// assembled anywhere but the GETACLSTATUS parse carries an empty permission,
// and an empty permission read the fail-closed way would strip every named
// grant off every file without anything looking wrong. Making the mode an
// argument means the caller states the mask rather than being able to forget
// it.
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
    /// <param name="status">The file's status, whose permission carries the mask.</param>
    /// <param name="acl">Its ACL as GETACLSTATUS returns it, or null. Entries that are empty, or only inheritance, mean a minimal ACL.</param>
    /// <param name="rangerGroups">Groups a Ranger path policy grants read.</param>
    /// <returns>The cluster group names, deduplicated.</returns>
    public static IReadOnlyList<string> ClusterGroups(
        HdfsFileStatus status,
        HdfsAclStatus? acl,
        IReadOnlyList<string> rangerGroups,
        IReadOnlySet<string>? traversable = null)
    {
        var groups = new List<string>();

        bool owningGroupCanRead;

        // An HdfsAclStatus is not evidence of an extended ACL - WebHDFS answers
        // GETACLSTATUS for every path - so the entries are what decide which
        // reading of the group digit is in force.
        if (acl is not null && acl.IsExtended)
        {
            // The owning group's effective permission is its "group::" entry AND
            // the mask, which is what getfacl prints as "#effective:". An
            // extended ACL stating no "group::" entry is a shape the cluster
            // does not produce, and it grants nothing here rather than falling
            // back to the digit: falling back would read the mask as a grant,
            // which is the over-grant being fixed.
            owningGroupCanRead =
                HdfsAclStatus.MaskGrantsRead(status.Permission) && acl.OwningGroupCanRead;
        }
        else
        {
            // No extended ACL, so no mask: the digit is the owning group's, as
            // it reads. A file owned by group "finance" with mode 600 grants
            // finance nothing, and treating ownership as access would be
            // inventing a grant the cluster does not give.
            owningGroupCanRead = status.GroupCanRead;
        }

        if (owningGroupCanRead && !string.IsNullOrWhiteSpace(status.Group))
        {
            groups.Add(status.Group);
        }

        if (acl is not null)
        {
            groups.AddRange(acl.GroupsGrantedRead(status.Permission));
        }

        // The POSIX grants above are subject to the traversal gate; the Ranger
        // grants below are not, and the asymmetry is the point.
        //
        // Reading a file on HDFS needs read on the file AND execute on every
        // directory above it, so a group holding read on a file it cannot reach
        // holds nothing. A null gate means no ancestor restricted anybody -
        // which is not the same as an empty one, and conflating them would
        // strip every grant off every file.
        //
        // A Ranger path policy is a different mechanism: the plugin authorises
        // the path itself rather than deferring to the POSIX walk, so a group
        // Ranger grants is not gated by the mode bits of the directories above.
        List<string> reachable = traversable is null
            ? groups
            : groups.Where(traversable.Contains).ToList();

        reachable.AddRange(rangerGroups);

        return reachable
            .Where(group => !string.IsNullOrWhiteSpace(group))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>
    /// The cluster groups that may traverse one directory, and whether every
    /// account may.
    ///
    /// The read counterpart of <see cref="ClusterGroups"/>, and it settles the
    /// same ambiguity the same way: on a directory carrying an extended ACL the
    /// group digit of the mode is the MASK, so the owning group's own execute
    /// comes from its "group::" entry and every named entry is masked; without
    /// one the digit is the owning group's, as it reads.
    /// </summary>
    /// <param name="status">The directory's status.</param>
    /// <param name="acl">Its ACL as GETACLSTATUS returns it, or null.</param>
    /// <returns>The groups that may traverse it, and whether it is world-traversable.</returns>
    public static (IReadOnlyList<string> Groups, bool Everyone) TraverseGroups(
        HdfsFileStatus status, HdfsAclStatus? acl)
    {
        var groups = new List<string>();

        bool owningGroupCanTraverse = acl is not null && acl.IsExtended
            ? HdfsAclStatus.MaskGrantsExecute(status.Permission) && acl.OwningGroupCanExecute
            : status.GroupCanExecute;

        if (owningGroupCanTraverse && !string.IsNullOrWhiteSpace(status.Group))
        {
            groups.Add(status.Group);
        }

        if (acl is not null)
        {
            groups.AddRange(acl.GroupsGrantedExecute(status.Permission));
        }

        return (
            groups.Where(group => !string.IsNullOrWhiteSpace(group))
                  .Distinct(StringComparer.OrdinalIgnoreCase)
                  .ToList(),
            status.OtherCanExecute);
    }

    /// <summary>Builds the grants for one file.</summary>
    /// <param name="status">The file's status, whose permission carries the mask.</param>
    /// <param name="acl">Its ACL as GETACLSTATUS returns it, or null.</param>
    /// <param name="rangerGroups">Groups a Ranger path policy grants read.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>The grants. Empty means the file is not indexed.</returns>
    public async Task<IReadOnlyList<PushAclEntry>> BuildAsync(
        HdfsFileStatus status,
        HdfsAclStatus? acl,
        IReadOnlyList<string> rangerGroups,
        CancellationToken cancellationToken,
        IReadOnlySet<string>? traversable = null,
        bool everyoneCanTraverse = true)
    {
        IReadOnlyList<string> clusterGroups = ClusterGroups(status, acl, rangerGroups, traversable);

        List<PushAclEntry> grants = await this.principals.ResolveAsync(clusterGroups, cancellationToken);

        // The world-readable grant needs a world-traversable path to sit on. A
        // file at 644 under a directory at 750 is readable by nobody outside
        // that directory's group, and "everyone on the cluster" is emphatically
        // outside it.
        if (status.OtherCanRead && everyoneCanTraverse && !string.IsNullOrWhiteSpace(this.otherReadableGroupId))
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
