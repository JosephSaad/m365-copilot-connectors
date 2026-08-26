// ---------------------------------------------------------------------------
// CrawlCheckpoint.cs
// Where a crawl got to, and how to write that down without losing it.
//
// The marker is composite - (modification time, path) for files, (watermark
// column, key column) for rows - for the reason the SQL family's watermark is:
// two files can share a timestamp to the millisecond, and a marker of only the
// timestamp either re-reads that whole group for ever or loses whichever of
// them had not been written when the run stopped. Comparing the pair makes the
// ordering total, and the resume rule "strictly after the marker" exact.
//
// Writing is temp-then-rename, which on every filesystem this runs on is
// atomic: a process killed mid-write leaves either the old checkpoint or the
// new one, never half of either. A half-written checkpoint would be worse than
// none, because none means "recrawl everything" and half means "resume from a
// position nobody chose".
//
// An unreadable or unparseable file is therefore treated as absent, and absent
// means a full recrawl. That is safe because every write is an upsert: reading
// a file twice costs time and changes nothing.
// ---------------------------------------------------------------------------

namespace CdpConnector.Source.Watermark;

using System.Text.Json;
using System.Text.Json.Serialization;
using Serilog;

/// <summary>How far a crawl got, and how many runs have happened.</summary>
public sealed class CrawlCheckpoint
{
    /// <summary>Gets or sets the first component of the marker: a timestamp, in round-trip UTC.</summary>
    [JsonPropertyName("markerTime")]
    public string MarkerTime { get; set; } = string.Empty;

    /// <summary>Gets or sets the second component: the path or key that breaks a timestamp tie.</summary>
    [JsonPropertyName("markerKey")]
    public string MarkerKey { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets how many runs have completed against this checkpoint.
    ///
    /// It exists for the periodic full recrawl, which is the only thing that
    /// re-derives item ACLs after a permission change at the source - a
    /// permission change does not alter a file's modification time, so an
    /// incremental pass never revisits the file.
    /// </summary>
    [JsonPropertyName("runCount")]
    public int RunCount { get; set; }

    /// <summary>Gets or sets when the last run completed, for the operator rather than for the logic.</summary>
    [JsonPropertyName("lastCompletedUtc")]
    public string LastCompletedUtc { get; set; } = string.Empty;

    /// <summary>Gets a value indicating whether there is a marker to resume from.</summary>
    [JsonIgnore]
    public bool HasMarker => this.MarkerTime.Length > 0;

    /// <summary>Gets the marker's timestamp, or the epoch when there is none.</summary>
    /// <returns>The timestamp.</returns>
    public DateTimeOffset MarkerTimestamp()
    {
        return DateTimeOffset.TryParse(
            this.MarkerTime,
            System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.RoundtripKind,
            out DateTimeOffset parsed)
            ? parsed
            : DateTimeOffset.UnixEpoch;
    }

    /// <summary>
    /// Decides whether an item is strictly after the marker, with the tie broken
    /// by the key.
    ///
    /// Ties repeat rather than disappear: an item whose timestamp equals the
    /// marker's and whose key is not greater has already been written, and one
    /// whose key IS greater has not. Getting this backwards loses rows silently,
    /// which is why it is one method with one test rather than an inline
    /// comparison at each call site.
    /// </summary>
    /// <param name="timestamp">The candidate's timestamp.</param>
    /// <param name="key">The candidate's tie-breaking key.</param>
    /// <param name="slack">Seconds subtracted from the marker, for clock skew.</param>
    /// <returns>True when the candidate has not been written yet.</returns>
    public bool IsAfter(DateTimeOffset timestamp, string key, int slack = 0)
    {
        if (!this.HasMarker)
        {
            return true;
        }

        DateTimeOffset marker = this.MarkerTimestamp().AddSeconds(-slack);

        if (timestamp > marker)
        {
            return true;
        }

        if (timestamp < marker)
        {
            return false;
        }

        return string.Compare(key, this.MarkerKey, StringComparison.Ordinal) > 0;
    }
}

/// <summary>Reads and writes one connector's checkpoint file.</summary>
public sealed class CheckpointStore
{
    private static readonly JsonSerializerOptions SerializerOptions = new() { WriteIndented = true };

    private readonly string filePath;
    private readonly ILogger log;

    /// <summary>Initializes a new instance of the <see cref="CheckpointStore"/> class.</summary>
    /// <param name="directory">Where checkpoints live. Created if absent.</param>
    /// <param name="connectorKey">Names the file, so two connectors never share one.</param>
    /// <param name="log">Where to report a checkpoint that could not be read.</param>
    public CheckpointStore(string directory, string connectorKey, ILogger log)
    {
        string resolved = Path.IsPathRooted(directory)
            ? directory
            : Path.Combine(AppContext.BaseDirectory, directory);

        this.filePath = Path.Combine(resolved, connectorKey + ".watermark.json");
        this.log = log;
    }

    /// <summary>Gets the file this store reads and writes.</summary>
    public string FilePath => this.filePath;

    /// <summary>Reads the checkpoint, or an empty one when there is none to read.</summary>
    /// <returns>The checkpoint.</returns>
    public CrawlCheckpoint Read()
    {
        if (!File.Exists(this.filePath))
        {
            return new CrawlCheckpoint();
        }

        try
        {
            CrawlCheckpoint? checkpoint =
                JsonSerializer.Deserialize<CrawlCheckpoint>(File.ReadAllText(this.filePath));

            return checkpoint ?? new CrawlCheckpoint();
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            // Treated as absent, which means a full recrawl. Safe, because every
            // write is an upsert; loud, because silently recrawling a million
            // files every night is a cost somebody should know about.
            this.log.Warning(
                "The checkpoint at {Path} could not be read ({Message}). This run re-reads everything, " +
                "which is safe because writes are upserts, and writes a fresh checkpoint at the end.",
                this.filePath,
                ex.Message);

            return new CrawlCheckpoint();
        }
    }

    /// <summary>Writes the checkpoint atomically.</summary>
    /// <param name="checkpoint">What to write.</param>
    public void Write(CrawlCheckpoint checkpoint)
    {
        string? directory = Path.GetDirectoryName(this.filePath);

        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        // Temp-then-rename. A kill between the two leaves the previous
        // checkpoint intact rather than a truncated one.
        string temporary = this.filePath + ".tmp";

        File.WriteAllText(temporary, JsonSerializer.Serialize(checkpoint, SerializerOptions));
        File.Move(temporary, this.filePath, overwrite: true);
    }
}
