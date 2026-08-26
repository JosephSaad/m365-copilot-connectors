// ---------------------------------------------------------------------------
// HdfsModels.cs
// What WebHDFS returns, in the shapes this connector needs.
//
// Timestamps arrive as milliseconds since the epoch, which is why they are
// converted here once rather than at each use: an off-by-a-thousand in a
// watermark comparison is a crawl that either re-reads everything for ever or
// skips a day of files, and neither announces itself.
//
// The permission triple is read here too, and it means two different things
// depending on the ACL beside it. On a file with no extended ACL the three
// digits are owner, group and other, as they read. On a file with one, the
// GROUP digit is the ACL MASK - HDFS keeps the mask in the mode rather than as
// an entry - and the owning group's own permission moves into the "group::"
// entry. Apache's own GETACLSTATUS example is that shape:
//
//   {"entries":["user:carla:rw-","group::r-x"],"permission":"775"}
//
// where 7 is the mask and r-x is what the owning group actually has. Reading
// the mask as a grant is an over-grant: after "hdfs dfs -chmod 600" on a file
// carrying "group:analysts:r--" the cluster gives analysts nothing and getfacl
// prints "#effective:---", so an index that still shows the file to them shows
// it to people the cluster refuses.
//
// The digits are also padded before they are indexed. Hadoop renders the mode
// with String.format("%o", ...), which drops leading zeros: mode 070 arrives as
// "70", and indexing that positionally reads the owner digit as the group's -
// silently dropping a group-readable file out of the index.
//
// Which of the two readings applies is decided from the ACL's ACCESS entries,
// not from having an HdfsAclStatus at all: WebHDFS answers GETACLSTATUS for
// every path and returns an empty entry list for one carrying no ACL, and a
// directory holding only "default:" entries has a minimal access ACL whose
// group digit is still its owning group's own permission. Anything else that is
// not a default entry is taken as an extended ACL, which is the closed reading:
// it makes the digit a mask and then requires a "group::" entry to grant the
// owning group anything.
// ---------------------------------------------------------------------------

namespace CdpConnector.Source.Hdfs;

/// <summary>One file or directory, as WebHDFS describes it.</summary>
public sealed class HdfsFileStatus
{
    /// <summary>Gets or sets the absolute path. Composed by the client; WebHDFS returns only the last segment.</summary>
    public string Path { get; set; } = string.Empty;

    /// <summary>Gets or sets FILE or DIRECTORY.</summary>
    public string Type { get; set; } = string.Empty;

    /// <summary>Gets or sets the size in bytes. Zero for a directory.</summary>
    public long Length { get; set; }

    /// <summary>Gets or sets when the content last changed.</summary>
    public DateTimeOffset ModifiedUtc { get; set; }

    /// <summary>Gets or sets the owning user.</summary>
    public string Owner { get; set; } = string.Empty;

    /// <summary>Gets or sets the owning group.</summary>
    public string Group { get; set; } = string.Empty;

    /// <summary>Gets or sets the POSIX permission triple, for example "640".</summary>
    public string Permission { get; set; } = string.Empty;

    /// <summary>Gets a value indicating whether this is a directory.</summary>
    public bool IsDirectory => string.Equals(this.Type, "DIRECTORY", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Gets a value indicating whether the owning group may read this, on a file
    /// that carries no extended ACL.
    ///
    /// The middle digit of the permission triple is the group's, and bit 4 of it
    /// is read. That reading holds only while the file has no extended ACL: once
    /// it has one, the same digit is the ACL mask and the owning group's own
    /// permission lives in the "group::" entry beside it. HdfsAclBuilder is what
    /// decides which of the two a file is, so nothing else should reach for this
    /// without having asked that question first;
    /// <see cref="HdfsAclStatus.MaskGrantsRead"/> reads the same digit under its
    /// other name.
    /// </summary>
    public bool GroupCanRead => PermissionDigit(this.Permission, 1) is int digit && (digit & 4) != 0;

    /// <summary>
    /// Gets a value indicating whether everyone on the cluster may read this.
    ///
    /// The other digit means the same thing whether or not there is an extended
    /// ACL: the mask never covers it.
    /// </summary>
    public bool OtherCanRead => PermissionDigit(this.Permission, 2) is int digit && (digit & 4) != 0;

    /// <summary>Reads one digit of a POSIX permission triple, or null when it cannot be read.</summary>
    /// <param name="permission">The triple, which may carry a leading sticky-bit digit.</param>
    /// <param name="index">0 for owner, 1 for group, 2 for other.</param>
    /// <returns>The digit, or null.</returns>
    public static int? PermissionDigit(string permission, int index)
    {
        if (string.IsNullOrWhiteSpace(permission) || index is < 0 or > 2)
        {
            return null;
        }

        // Anything that is not a run of digits is a permission this does not
        // understand, and half-parsing one grants on a guess. Refusing the whole
        // value instead costs a file its grants, which is the direction to fail
        // in: no digit means no read bit means no grant.
        foreach (char character in permission)
        {
            if (!char.IsAsciiDigit(character))
            {
                return null;
            }
        }

        // A value longer than three digits carries the sticky, setuid and setgid
        // bits first; the triple this cares about is always the last three.
        string triple = permission.Length > 3 ? permission[^3..] : permission;

        // Hadoop formats the mode as octal with String.format("%o", ...), which
        // drops leading zeros - mode 070 arrives as "70". Padding it back is what
        // keeps the digits under the names they belong to; without this, "70" is
        // read as owner 7, group 0, and a file its group can read is dropped.
        return triple.PadLeft(3, '0')[index] - '0';
    }
}

/// <summary>
/// The ACL of one path as GETACLSTATUS reports it. An empty entry list is a
/// path with no extended ACL, which is most of them.
/// </summary>
public sealed class HdfsAclStatus
{
    /// <summary>Gets or sets the owning user.</summary>
    public string Owner { get; set; } = string.Empty;

    /// <summary>Gets or sets the owning group.</summary>
    public string Group { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the POSIX permission triple as GETACLSTATUS reports it.
    ///
    /// The mask HdfsAclBuilder applies is read from the FILE STATUS instead. The
    /// two agree when both came from the cluster, and the file status is the one
    /// every caller holds: an HdfsAclStatus assembled anywhere but the
    /// GETACLSTATUS parse leaves this empty, and an empty mode read the
    /// fail-closed way would strip every named grant off every file.
    /// </summary>
    public string Permission { get; set; } = string.Empty;

    /// <summary>
    /// Gets the raw ACL entries, for example "group:analysts:r-x".
    ///
    /// Kept as strings because that is what the wire carries and because the
    /// only thing this connector does with them is pick out the group ones that
    /// grant read - a richer model would be inventing structure it never uses.
    /// </summary>
    public IList<string> Entries { get; } = new List<string>();

    /// <summary>
    /// Gets a value indicating whether this is an extended ACL, and therefore
    /// whether the group digit of the mode beside it is the ACL mask.
    ///
    /// Judged on the entries rather than on this object existing: WebHDFS
    /// answers GETACLSTATUS for every path, with an empty entry list for one
    /// that has no ACL at all. Default entries do not count, because a path
    /// carrying only inheritance rules has a minimal access ACL and no mask.
    /// Everything else does, including an entry this cannot parse - reading an
    /// unrecognised entry as "no extended ACL" would turn the mask back into a
    /// grant, which is the over-grant this whole distinction exists to stop.
    /// </summary>
    public bool IsExtended => this.Entries.Any(entry => !IsDefaultEntry(entry));

    /// <summary>
    /// Gets the owning group's own permission bits, for example "r-x", or null
    /// when the ACL states none.
    ///
    /// On an extended ACL this is where the owning group's permission lives,
    /// because the mode's group digit has been taken over by the mask. Null on
    /// an extended ACL means the cluster said nothing about the owning group,
    /// and nothing is what it then gets: falling back to the digit would be
    /// reading the mask as a grant again.
    /// </summary>
    public string? OwningGroupPermission
    {
        get
        {
            foreach (string entry in this.Entries)
            {
                string[] parts = AccessParts(entry);

                if (parts.Length == 3 &&
                    string.Equals(parts[0], "group", StringComparison.OrdinalIgnoreCase) &&
                    parts[1].Length == 0)
                {
                    return parts[2];
                }
            }

            return null;
        }
    }

    /// <summary>
    /// Gets a value indicating whether the "group::" entry carries read. This is
    /// the owning group's own permission and not yet its effective one - the
    /// mask still applies over it.
    /// </summary>
    public bool OwningGroupCanRead => GrantsRead(this.OwningGroupPermission);

    /// <summary>
    /// Whether the ACL mask lets read through.
    ///
    /// The mask is the GROUP digit of the mode: HDFS keeps it there rather than
    /// returning a "mask::" element, which is why Apache's own GETACLSTATUS
    /// example pairs {"entries":["user:carla:rw-","group::r-x"]} with
    /// "permission":"775" - 7 is the mask, r-x is the owning group. A mode this
    /// cannot parse yields no digit and so no read, which refuses rather than
    /// grants.
    /// </summary>
    /// <param name="permission">The file's mode, for example "640".</param>
    /// <returns>True when the mask carries the read bit.</returns>
    public static bool MaskGrantsRead(string permission)
    {
        return HdfsFileStatus.PermissionDigit(permission, 1) is int digit && (digit & 4) != 0;
    }

    /// <summary>
    /// The names of groups granted read by a named ACL entry, after the mask.
    ///
    /// A named entry's effective permission is its own bits AND the mask, which
    /// is what getfacl prints as "#effective:". An entry reading "r--" under a
    /// mask of "---" grants nothing on the cluster, so it grants nothing here.
    ///
    /// Default entries - those prefixed "default:" - are deliberately ignored.
    /// A default entry describes what a file created here will inherit, not who
    /// may read what is here now, and treating the two the same would grant
    /// access on the strength of a template.
    /// </summary>
    /// <param name="permission">The file's mode, whose group digit is the mask.</param>
    /// <returns>The group names.</returns>
    public IEnumerable<string> GroupsGrantedRead(string permission)
    {
        // A named entry only exists on an extended ACL, so the group digit of
        // the mode beside it is always the mask by the time anything is yielded
        // from here - no IsExtended check is needed for that reading to hold.
        if (!MaskGrantsRead(permission))
        {
            yield break;
        }

        foreach (string entry in this.Entries)
        {
            string[] parts = AccessParts(entry);

            if (parts.Length != 3 ||
                !string.Equals(parts[0], "group", StringComparison.OrdinalIgnoreCase) ||
                parts[1].Length == 0)
            {
                continue;
            }

            if (GrantsRead(parts[2]))
            {
                yield return parts[1];
            }
        }
    }

    /// <summary>Whether one entry's permission bits carry read.</summary>
    /// <param name="bits">The bits, for example "r-x", or null.</param>
    /// <returns>True when read is set.</returns>
    private static bool GrantsRead(string? bits)
    {
        return bits is not null && bits.Contains('r', StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Whether one entry describes inheritance rather than access.</summary>
    /// <param name="entry">The raw entry.</param>
    /// <returns>True for a "default:" entry.</returns>
    private static bool IsDefaultEntry(string entry)
    {
        return entry.StartsWith("default:", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Splits one access entry into kind, name and bits.</summary>
    /// <param name="entry">The raw entry.</param>
    /// <returns>The three parts, or an empty array for a default entry or one this cannot read.</returns>
    private static string[] AccessParts(string entry)
    {
        if (IsDefaultEntry(entry))
        {
            return [];
        }

        string[] parts = entry.Split(':');

        return parts.Length == 3 ? parts : [];
    }
}
