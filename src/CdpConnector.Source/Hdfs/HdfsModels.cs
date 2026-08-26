// ---------------------------------------------------------------------------
// HdfsModels.cs
// What WebHDFS returns, in the shapes this connector needs.
//
// Timestamps arrive as milliseconds since the epoch, which is why they are
// converted here once rather than at each use: an off-by-a-thousand in a
// watermark comparison is a crawl that either re-reads everything for ever or
// skips a day of files, and neither announces itself.
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
    /// Gets a value indicating whether the owning group may read this.
    ///
    /// The middle digit of the permission triple is the group's, and bit 4 of it
    /// is read. A file whose group cannot read it grants nothing through its
    /// group, however the ACL entries below read.
    /// </summary>
    public bool GroupCanRead => PermissionDigit(this.Permission, 1) is int digit && (digit & 4) != 0;

    /// <summary>Gets a value indicating whether everyone on the cluster may read this.</summary>
    public bool OtherCanRead => PermissionDigit(this.Permission, 2) is int digit && (digit & 4) != 0;

    /// <summary>Reads one digit of a POSIX permission triple, or null when it is not there.</summary>
    /// <param name="permission">The triple, which may carry a leading sticky-bit digit.</param>
    /// <param name="index">0 for owner, 1 for group, 2 for other.</param>
    /// <returns>The digit, or null.</returns>
    public static int? PermissionDigit(string permission, int index)
    {
        if (string.IsNullOrWhiteSpace(permission))
        {
            return null;
        }

        // A four-digit value carries the sticky, setuid and setgid bits first;
        // the triple this cares about is always the last three.
        string triple = permission.Length > 3 ? permission[^3..] : permission;

        return index < triple.Length && char.IsAsciiDigit(triple[index])
            ? triple[index] - '0'
            : null;
    }
}

/// <summary>The extended ACL of one path, when it has one.</summary>
public sealed class HdfsAclStatus
{
    /// <summary>Gets or sets the owning user.</summary>
    public string Owner { get; set; } = string.Empty;

    /// <summary>Gets or sets the owning group.</summary>
    public string Group { get; set; } = string.Empty;

    /// <summary>Gets or sets the POSIX permission triple.</summary>
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
    /// The names of groups granted read by a named ACL entry.
    ///
    /// Default entries - those prefixed "default:" - are deliberately ignored.
    /// A default entry describes what a file created here will inherit, not who
    /// may read what is here now, and treating the two the same would grant
    /// access on the strength of a template.
    /// </summary>
    /// <returns>The group names.</returns>
    public IEnumerable<string> GroupsGrantedRead()
    {
        foreach (string entry in this.Entries)
        {
            if (entry.StartsWith("default:", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            string[] parts = entry.Split(':');

            if (parts.Length != 3 ||
                !string.Equals(parts[0], "group", StringComparison.OrdinalIgnoreCase) ||
                parts[1].Length == 0)
            {
                continue;
            }

            if (parts[2].Contains('r', StringComparison.OrdinalIgnoreCase))
            {
                yield return parts[1];
            }
        }
    }
}
