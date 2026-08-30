// ---------------------------------------------------------------------------
// HierarchyIncrementalSource.cs
// The first source in this repository that actually reads the resume marker.
//
// Everything needed for an incremental crawl was already plumbed: the engine
// fetches crawl.Checkpoint into PushSourceContext.ResumeFrom before the source
// is opened, and saves a new one from PushItem.LastModifiedUtc after each
// chunk's writes are confirmed. What was missing was a source that consumed the
// first and set the second, so crawl.Checkpoint had never held a row - across
// thirty-six runs - and Settings:Incremental changed nothing but the log.
//
// This class is that source, and it exists SEPARATELY FROM SqlPushSource rather
// than as a flag on it. SqlPushSource declares RequiresOrderedCommit = false,
// which is what buys the full crawl its sixteen concurrent writers, and that
// declaration is only safe because it keeps no position at all. A marker source
// cannot share it - see RequiresOrderedCommit below - and folding both
// behaviours into one class would put the two contradictory contracts one
// boolean apart.
//
// WHAT THIS SOURCE OWES, from docs/SOURCE-CONTRACT.md Tier 1 and IPushSource:
//
//   1. Read from PushSourceContext.ResumeFrom, strictly after the marker.
//   2. Yield in ascending (marker, id) order, with no exceptions.
//   3. Set PushItem.LastModifiedUtc on every item, so the engine can save a
//      checkpoint at all.
//   4. Declare SourceChangeDetection.ChangeMarker, so the engine may open the
//      run as incremental.
//
// Doing three of the four is how a connector silently stops indexing the
// changes its timestamp missed, so each one is asserted or enforced below
// rather than left to the reader.
//
// THE MARKER IS EffectiveLastModified, NOT LastModified, and the difference is
// the entire reason sql/26 exists. LastModified means "when did THIS row
// change". A time entry carries its engagement's and its customer's names for
// searchability, so renaming a customer makes a thousand descendants' indexed
// text wrong while moving only the customer's own LastModified. A checkpoint
// taken from LastModified would advance past every one of those descendants,
// permanently, and nothing would report it. EffectiveLastModified is the
// hierarchy-aware answer - "when did anything affecting this row's indexed
// content last change" - maintained by sql/26's cascading triggers.
//
// Both columns are read. LastModified still becomes the lastModified schema
// property, exactly as it does on the full path, because that is what a person
// sees on a search result and because the item has to hash identically
// whichever view it came from. EffectiveLastModified never becomes a property;
// it only ever becomes the checkpoint.
// ---------------------------------------------------------------------------

namespace SqlHierarchyPush;

using System.Runtime.CompilerServices;
using Connector.Security.Configuration;
using Microsoft.Data.SqlClient;
using PushCore;
using PushCore.State;

/// <summary>Reads the timesheet source from a composite checkpoint forward.</summary>
public sealed class HierarchyIncrementalSource : IPushSource
{
    private readonly HierarchyPushConnector connector;
    private readonly PushSourceContext context;

    private int skipped;

    /// <summary>Initializes a new instance of the <see cref="HierarchyIncrementalSource"/> class.</summary>
    /// <param name="connector">The connector supplying the query and the row mapping.</param>
    /// <param name="context">Configuration, credential, logger and the resume marker.</param>
    public HierarchyIncrementalSource(HierarchyPushConnector connector, PushSourceContext context)
    {
        ArgumentNullException.ThrowIfNull(connector);
        ArgumentNullException.ThrowIfNull(context);

        this.connector = connector;
        this.context = context;
    }

    /// <inheritdoc/>
    public int Skipped => this.skipped;

    /// <inheritdoc/>
    /// <remarks>
    /// True, and it must be, even though <see cref="OnItemCommittedAsync"/>
    /// below does nothing.
    ///
    /// The IPushSource documentation says a source whose commit callback does
    /// nothing keeps no position and may therefore return false. That rule was
    /// written before any source kept its position somewhere OTHER than in
    /// itself, and this one does: the position is crawl.Checkpoint, and the
    /// engine writes it - once per chunk, from the last confirmed item of the
    /// unbroken prefix. The callback being empty says only that there is no
    /// second copy to keep, not that there is no position.
    ///
    /// Returning false here would give the run several writers, and several
    /// writers finish out of order. Chunk n+1 would then save its marker before
    /// chunk n had landed; uspSaveCheckpoint refuses to move backwards, so
    /// chunk n's later save would be REFUSED rather than correcting it, and the
    /// stored marker would sit past a range of items that never reached the
    /// index. Every one of them would be skipped by every subsequent
    /// incremental run, and the only visible symptom would be a fast crawl.
    ///
    /// The cost is real and worth naming: this source writes one item at a time.
    /// On a steady-state incremental run that is nothing, because almost nothing
    /// is written. On the FIRST run - which is escalated to a full crawl,
    /// because there is no checkpoint yet - it is the whole corpus written
    /// serially. The way to avoid paying it is to do the initial load with
    /// Settings:Incremental off, which uses SqlPushSource and its sixteen
    /// writers, and to turn the setting on afterwards.
    /// </remarks>
    public bool RequiresOrderedCommit => true;

    /// <inheritdoc/>
    /// <remarks>
    /// ChangeMarker, which is what permits the engine to open the run as
    /// incremental at all. The claim is only honest because sql/26 maintains
    /// EffectiveLastModified on every write path through triggers rather than
    /// through the application, so it moves on bulk updates and direct DBA
    /// edits as well - which is requirement 6 of docs/SOURCE-CONTRACT.md and the
    /// one a column maintained by an application layer always fails.
    ///
    /// sql/31 is what keeps the claim honest over time: a disabled trigger
    /// leaves the source accepting writes while the column stops moving, and
    /// nothing in this process could detect that.
    /// </remarks>
    public SourceChangeDetection ChangeDetection => SourceChangeDetection.ChangeMarker;

    /// <inheritdoc/>
    public async IAsyncEnumerable<PushItem> ReadAsync(
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        PushOptions options = this.context.Options;
        CrawlMarker? resume = this.context.ResumeFrom;

        // Null is not an error and is not rare: it is what the engine hands over
        // whenever the store escalated this run to a full crawl - no checkpoint
        // yet, the hash framing changed, or Settings:FullEveryHours elapsed. The
        // query then has no lower bound and reads the whole source, still in
        // (marker, id) order, still setting the marker on every item. That is
        // how the FIRST checkpoint is ever created, so this branch is the
        // bootstrap rather than a fallback.
        if (resume is null)
        {
            this.context.Log.Information(
                "No resume marker: reading the whole source in (EffectiveLastModified, ItemId) order. " +
                "This run establishes the checkpoint the next one resumes from.");
        }
        else
        {
            this.context.Log.Information(
                "Resuming strictly after ({MarkerTime:o}, {MarkerKey}).",
                resume.Value.MarkerTime,
                resume.Value.MarkerKey);
        }

        string query = HierarchyPushConnector.BuildIncrementalQuery(options, resume);

        var connections = new Connector.Security.Sql.SqlConnectionFactory(
            options.DataSource,
            options.Environment,
            this.context.Secrets,
            options.KeyVault.SecretName(KeyVaultOptions.SqlPasswordKey),
            this.context.Credential,
            this.context.Log);

        await using SqlConnection connection = await connections.OpenAsync(cancellationToken);
        await using var command = new SqlCommand(query, connection);

        command.CommandTimeout = options.DataSource.CommandTimeoutSeconds;

        if (resume is not null)
        {
            // Parameters, not interpolation, and TYPED. DATETIME2 scale 3 matches
            // both crawl.Checkpoint.MarkerTime and the source column exactly, so
            // no implicit conversion sits between the predicate and the index it
            // has to seek - an untyped datetime parameter is enough on its own to
            // turn the seek into a scan of the whole hierarchy.
            command.Parameters.Add(new SqlParameter("@ResumeTime", System.Data.SqlDbType.DateTime2)
            {
                Scale = 3,
                Value = resume.Value.MarkerTime,
            });

            // 128 is Graph's item-ID ceiling and the width of the view's ItemId
            // column. Declaring it stops SqlClient inferring a width from the
            // value, which would produce a different plan for every marker length.
            command.Parameters.Add(new SqlParameter("@ResumeKey", System.Data.SqlDbType.NVarChar, 128)
            {
                Value = resume.Value.MarkerKey ?? string.Empty,
            });
        }

        await using SqlDataReader reader = await command.ExecuteReaderAsync(cancellationToken);

        int rowOrdinal = 0;
        DateTime? previousMarker = null;
        string previousId = string.Empty;

        while (await reader.ReadAsync(cancellationToken))
        {
            rowOrdinal++;

            PushItem? mapped;
            DateTime? marker;

            try
            {
                mapped = this.connector.MapRow(reader, options);

                int ordinal = reader.GetOrdinal(HierarchyPushConnector.MarkerColumn);

                // Null here is not a missing value. It is the read ceiling doing
                // its job: the row changed at or after the moment this query
                // started, so it is deliberately given no marker and the
                // checkpoint will not advance past it. See BuildIncrementalQuery.
                marker = reader.IsDBNull(ordinal)
                    ? null
                    : DateTime.SpecifyKind(reader.GetDateTime(ordinal), DateTimeKind.Utc);
            }
            catch (Exception ex)
            {
                // Same policy as SqlPushSource: locate the failure by ordinal and
                // never log the row, because this log is more widely readable
                // than the source it came from.
                throw new InvalidOperationException(
                    $"Row {rowOrdinal} could not be mapped. " +
                    "The row's content is deliberately not logged; find it in the source by ordinal.",
                    ex);
            }

            if (mapped is null)
            {
                this.skipped++;
                continue;
            }

            // THE ORDERING GUARANTEE, CHECKED RATHER THAN ASSUMED.
            //
            // The engine cannot verify this and the store cannot either: both see
            // a marker and take it at face value. If the ORDER BY is ever
            // weakened - by a well-meaning edit, or by a view that a future
            // Source:ItemView points somewhere else - a run interrupted partway
            // leaves the checkpoint at the largest pair it happened to reach, and
            // the next run starts strictly after it. Every row that sorted below
            // that pair but had not been written is then skipped for ever. The
            // corpus quietly stops matching the source and no run reports an
            // error.
            //
            // The check is on the TIMESTAMP only, deliberately. The tie-break
            // half of the order is a string comparison under the source
            // database's collation, and reproducing that in .NET is exactly the
            // kind of near-agreement that fails on one row in a hundred thousand;
            // an assertion that is subtly wrong is worse than none. What is
            // checked here is collation-free and catches the failure that
            // matters - an order that is not ascending in the marker at all,
            // which is what BuildQuery's "parents first" ordering would produce.
            if (previousMarker is not null && marker is not null && marker < previousMarker)
            {
                throw new InvalidOperationException(
                    $"Row {rowOrdinal} ({mapped.Id}) has an EffectiveLastModified of {marker:o}, " +
                    $"which is before row {rowOrdinal - 1}'s {previousMarker:o}. A ChangeMarker source " +
                    "must yield in ascending (marker, id) order; a checkpoint saved from an out-of-order " +
                    "read can pass a row that was never written. The run is stopped rather than " +
                    "advancing the checkpoint.");
            }

            if (previousMarker is not null && marker == previousMarker &&
                string.Equals(mapped.Id, previousId, StringComparison.OrdinalIgnoreCase))
            {
                // The pair is the checkpoint, so a repeated pair makes "strictly
                // after the marker" ambiguous for precisely the rows that repeat
                // it. sql/35 check 3 proves this cannot happen for the shipped
                // views; this catches a repointed one.
                throw new InvalidOperationException(
                    $"Row {rowOrdinal} repeats the marker pair ({marker:o}, {mapped.Id}). " +
                    "The composite checkpoint requires (EffectiveLastModified, ItemId) to be unique.");
            }

            previousMarker = marker ?? previousMarker;
            previousId = mapped.Id;

            // The one line the whole feature turns on. Everything else here
            // arranges for this value to be correct; the engine does the rest.
            mapped.LastModifiedUtc = marker;

            yield return mapped;
        }
    }

    /// <inheritdoc/>
    /// <remarks>
    /// Nothing to do, and that is not the same as nothing happening. The engine
    /// saves the checkpoint itself, to crawl.Checkpoint, from the same confirmed
    /// prefix that drives this callback - see PushEngine's flush, step 5. A
    /// second copy kept here would be a second answer to the same question, and
    /// the two would eventually disagree.
    ///
    /// See RequiresOrderedCommit for why an empty body here does NOT mean this
    /// source may be written to concurrently.
    /// </remarks>
    /// <param name="item">The item that was written.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>A completed task.</returns>
    public ValueTask OnItemCommittedAsync(PushItem item, CancellationToken cancellationToken)
    {
        return ValueTask.CompletedTask;
    }

    /// <inheritdoc/>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>A completed task.</returns>
    public ValueTask OnCrawlCompletedAsync(CancellationToken cancellationToken)
    {
        // Nothing describes this run as a whole beyond the checkpoint, which is
        // per item and already saved. A high-water timestamp recorded here would
        // be a marker that no confirmed write stands behind.
        return ValueTask.CompletedTask;
    }

    /// <inheritdoc/>
    /// <returns>A completed task.</returns>
    public ValueTask DisposeAsync()
    {
        // The connection, command and reader are scoped to the iterator, so
        // ending the enumeration - normally or by exception - closes them.
        return ValueTask.CompletedTask;
    }
}
