// ---------------------------------------------------------------------------
// CrawlState.cs
// The vocabulary the engine and the state store share.
//
// These types exist so PushCore can describe what it knows about a crawl
// without naming SQL Server. The store that implements ICrawlStateStore over
// ConnectorState lives in PushCore.State and references SqlClient; this project
// still references neither, for the same reason it has never referenced one:
// a connector reading something that is not a database references PushCore and
// stops there.
//
// Everything here is a record or an enum. There is no behaviour in this file
// on purpose - a decision that lives in a shared DTO is a decision two callers
// will eventually disagree about, and every decision in this design belongs
// either to the engine or to a stored procedure.
// ---------------------------------------------------------------------------

namespace PushCore.State;

/// <summary>What kind of read a run is making.</summary>
/// <remarks>
/// This is the single most consequential value in the state store, because the
/// delete sweep is valid after a full run and meaningless after an incremental
/// one. crawl.uspGetPendingDeletes refuses to answer for an incremental run
/// rather than trusting the caller to have thought about it.
/// </remarks>
public enum CrawlMode
{
    /// <summary>Every live record is expected. The only mode a delete sweep may follow.</summary>
    Full = 1,

    /// <summary>A slice, taken from the checkpoint forward. Absence proves nothing.</summary>
    Incremental = 2,
}

/// <summary>
/// How a source finds its changes, declared by the connector.
/// </summary>
/// <remarks>
/// Named after the two tiers in docs/SOURCE-CONTRACT.md, because the choice is
/// the source system's rather than the connector author's and the document is
/// what gets sent to the team that owns it.
///
/// The distinction is not cosmetic. A ChangeMarker source can be read
/// incrementally, so most runs touch a fraction of the corpus. A Differencing
/// source must read everything every run - its saving is that it still only
/// WRITES what changed, which is the expensive half. A Differencing source's
/// every run is therefore a full crawl, and gets delete detection every run as
/// a side effect rather than only on the weekly sweep.
/// </remarks>
public enum SourceChangeDetection
{
    /// <summary>
    /// The source has no usable modification timestamp. Read everything, write
    /// only what the hashes say moved. Every run is a full crawl.
    /// </summary>
    Differencing = 0,

    /// <summary>
    /// The source exposes a monotonic last-modified value and can be read from
    /// a composite checkpoint. Incremental runs are possible.
    /// </summary>
    ChangeMarker = 1,
}

/// <summary>Identifies the connection a run belongs to, and what it is for.</summary>
/// <param name="ConnectionId">The Graph external connection ID.</param>
/// <param name="ConnectorKey">The connector's key, for grouping on the dashboard.</param>
/// <param name="DisplayName">The connection's display name.</param>
/// <param name="ExpectedIntervalMinutes">
/// How often this connection is scheduled to run, or null when there is no
/// expectation. Only used to decide what "late" means on the dashboard - the
/// push tool does not schedule itself, and this is the half of feature 1 that a
/// database can actually hold.
/// </param>
public readonly record struct CrawlConnectionInfo(
    string ConnectionId,
    string ConnectorKey,
    string DisplayName,
    int? ExpectedIntervalMinutes);

/// <summary>What the store said when the run opened.</summary>
/// <param name="RunId">The run's ID, or zero when no store is configured.</param>
/// <param name="Mode">The mode the run is actually taking, after the store's advice.</param>
/// <param name="FullCrawlDue">
/// True when the store believes a full crawl is needed: none has ever
/// succeeded, the last one has aged out, or there is no checkpoint for an
/// incremental read to start from. The engine escalates rather than argues -
/// an incremental read with no marker reads from the beginning of time, which
/// is a full crawl that has told the sweep it was not one.
/// </param>
/// <param name="LastFullSuccessUtc">When a full crawl last completed, or null.</param>
/// <param name="AbandonedRunsReaped">
/// How many runs this one closed as abandoned on its way in. Non-zero means
/// previous processes died without reporting, which is worth a log line even
/// though it is not this run's problem.
/// </param>
public readonly record struct CrawlRunStart(
    long RunId,
    CrawlMode Mode,
    bool FullCrawlDue,
    DateTime? LastFullSuccessUtc,
    int AbandonedRunsReaped);

/// <summary>What the store holds about one item, as of the last run that wrote it.</summary>
/// <param name="ItemId">The external item ID.</param>
/// <param name="ItemType">The item's declared type.</param>
/// <param name="ContentHash">SHA-256 over the normalised content and properties.</param>
/// <param name="AclHash">SHA-256 over the resolved grants.</param>
/// <param name="ContentBytes">The content's size after truncation.</param>
/// <param name="UnchangedStreak">
/// How many consecutive runs have found this item unchanged. Nothing decides on
/// it; it is the number that makes the case for incremental reads measurable,
/// because "the median item has been re-read unchanged thirty times" is an
/// argument and "the push is slow" is not.
/// </param>
public readonly record struct CrawlItemState(
    string ItemId,
    string ItemType,
    byte[] ContentHash,
    byte[] AclHash,
    int ContentBytes,
    int UnchangedStreak)
{
    /// <summary>
    /// Whether this item, as previously written, matches what the engine has now.
    /// </summary>
    /// <param name="contentHash">The hash of the item about to be written.</param>
    /// <param name="aclHash">The hash of its resolved grants.</param>
    /// <returns>True when neither the content nor the ACL has moved.</returns>
    /// <remarks>
    /// Both halves are compared. An item whose text is identical but whose
    /// grants changed MUST be rewritten: the ACL is what trims the answer, so
    /// leaving it stale is not a performance decision but an access-control one.
    /// </remarks>
    public bool Matches(ReadOnlySpan<byte> contentHash, ReadOnlySpan<byte> aclHash)
    {
        return contentHash.SequenceEqual(this.ContentHash) && aclHash.SequenceEqual(this.AclHash);
    }
}

/// <summary>An item the sweep found the source no longer returns.</summary>
/// <param name="ItemId">The external item ID to remove from the index.</param>
/// <param name="ItemType">Its type, for the run's per-kind counters.</param>
public readonly record struct CrawlDeletion(string ItemId, string ItemType);

/// <summary>Where an incremental read should resume.</summary>
/// <param name="MarkerTime">The last modification time confirmed written.</param>
/// <param name="MarkerKey">The item ID that breaks a tie on that timestamp.</param>
/// <remarks>
/// Composite, and not negotiable. Two records can share a modification
/// timestamp to the millisecond; a marker of only the timestamp either re-reads
/// that whole group for ever or loses whichever of them had not been written
/// when the run stopped. The pair makes the ordering total and the resume rule
/// - strictly after the marker - exact.
/// </remarks>
public readonly record struct CrawlMarker(DateTime MarkerTime, string MarkerKey);

/// <summary>One resolved source principal.</summary>
/// <param name="SourceKey">The identifier as the source knows it.</param>
/// <param name="EntraObjectId">
/// The directory object, or null - which is a real answer and is cached as one.
/// A source principal with no Entra counterpart looked up on every item of every
/// run for ever is the single most expensive thing an unbounded resolver does.
/// </param>
/// <param name="EntraType">"group" or "user", or null alongside a null ID.</param>
public readonly record struct PrincipalGrant(string SourceKey, Guid? EntraObjectId, string? EntraType);

/// <summary>The run's totals, written once when it closes.</summary>
/// <param name="ItemsRead">Candidates the source yielded.</param>
/// <param name="ItemsWritten">Items sent to Graph and confirmed.</param>
/// <param name="ItemsUnchanged">Items the hashes said were already correct.</param>
/// <param name="ItemsDeleted">Items removed from the index by the sweep.</param>
/// <param name="ItemsSkipped">Candidates declined by the source or the engine.</param>
/// <param name="ItemsDuplicate">Item IDs that repeated within the run.</param>
/// <param name="ItemsFailed">Writes that gave up. Zero on a successful run.</param>
/// <param name="ThrottleWaits">Waits taken after a 429.</param>
/// <param name="BatchesSent">$batch requests issued. Zero when batching is off.</param>
/// <param name="BytesWritten">Content bytes actually sent.</param>
public readonly record struct RunTotals(
    int ItemsRead,
    int ItemsWritten,
    int ItemsUnchanged,
    int ItemsDeleted,
    int ItemsSkipped,
    int ItemsDuplicate,
    int ItemsFailed,
    int ThrottleWaits,
    int BatchesSent,
    long BytesWritten);

/// <summary>The same totals, for one kind of item.</summary>
/// <param name="ItemType">The item type as the connector declared it.</param>
/// <param name="ItemsWritten">Items of this type sent and confirmed.</param>
/// <param name="ItemsUnchanged">Items of this type found unchanged.</param>
/// <param name="ItemsDeleted">Items of this type removed.</param>
/// <param name="ItemsSkipped">Candidates of this type declined.</param>
/// <param name="ItemsFailed">Writes of this type that gave up.</param>
/// <param name="BytesWritten">Content bytes sent for this type.</param>
/// <remarks>
/// The grain the dashboard drills down to. A run that wrote 1,118 items and a
/// run that wrote 12 customers, 62 engagements and 1,044 time entries are the
/// same sentence; only the second one says what happened.
/// </remarks>
public readonly record struct ItemTypeTotals(
    string ItemType,
    int ItemsWritten,
    int ItemsUnchanged,
    int ItemsDeleted,
    int ItemsSkipped,
    int ItemsFailed,
    long BytesWritten);

/// <summary>One refusal from Graph, buffered until the run closes.</summary>
/// <param name="OccurredUtc">When it happened.</param>
/// <param name="StatusCode">429, or the 5xx that was retried.</param>
/// <param name="RetryAfterSeconds">What the service asked for, when it said.</param>
/// <param name="Endpoint">"item", "batch" or "schema" - which surface refused.</param>
/// <param name="AttemptNumber">Which attempt this was, from 1.</param>
/// <remarks>
/// Buffered rather than written when it happens, and that is a throughput
/// decision rather than a convenience. A round trip to SQL Server inside the
/// write loop's catch block would put a second network call beside every 429 -
/// on precisely the run that is already struggling, and on the path the retry
/// is about to sleep on anyway.
/// </remarks>
public readonly record struct ThrottleEvent(
    DateTime OccurredUtc,
    int StatusCode,
    int? RetryAfterSeconds,
    string Endpoint,
    int AttemptNumber);
