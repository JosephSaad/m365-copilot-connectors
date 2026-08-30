// ---------------------------------------------------------------------------
// GraphBatchWriter.cs
// Twenty writes in one round trip, and the twenty separate answers that come
// back with them.
//
// A push writes one item per HTTP PUT and measures 3.49 seconds a row. Very
// little of that is Graph thinking; most of it is the round trip itself, paid
// once per row. The $batch endpoint carries up to twenty sub-requests in one
// POST and answers them in one response, so twenty rows cost one round trip
// instead of twenty. That is the whole of the gain, and the next paragraph is
// the whole of what the gain is not.
//
// BATCHING IS A LATENCY OPTIMISATION AND NEVER A QUOTA ONE. Every sub-request
// inside a batch is evaluated INDIVIDUALLY against the service's throttling
// limits. Twenty items in one POST spend exactly the quota that twenty PUTs
// would; what is saved is nineteen round trips, not one unit of budget. A run
// that is throttle-bound rather than latency-bound gets nothing here, and
// reading this file as a way past a service limit is reading it wrong. The
// in-flight/backoff split in PushTiming is what says which run you have.
//
// WHAT BREAKS IF THIS FILE IS WRONG. A batch returns HTTP 200 on the envelope
// while individual sub-responses carry 429, 503 or a terminal 4xx. The outer
// 200 says only that Graph accepted the envelope; it says nothing at all about
// whether any item landed. A writer that reads the outer status and moves on
// reports twenty writes when it made none - and the caller then records a
// content hash for twenty rows that are not in the index. That is worse than
// the failure it hides, because the next incremental run sees a matching hash,
// skips the row, and the gap never closes on its own. So: every sub-response is
// inspected here, one at a time, an item is reported written only when its own
// status says so, and an item for which no sub-response came back at all is
// treated as unanswered rather than as success.
//
// PARTIAL SUCCESS IS THE NORMAL CASE, NOT THE ERROR PATH. Seventeen of twenty
// landing, two being throttled and one being refused outright is an ordinary
// outcome, and BatchWriteResult reports all three per item. Nothing here throws
// because an item was refused: throwing would discard the knowledge of which
// seventeen landed, which is the one thing the caller needs in order to record
// hashes for exactly those. Only a failure of the batch CALL itself - a 401, a
// 403, a 404 on the connection - climbs out as an exception, because that is a
// property of the run rather than of an item.
//
// SDK BATCH TYPES RATHER THAN HAND-BUILT JSON. Microsoft.Graph 5.105.0 ships
// Microsoft.Graph.BatchRequestContent and BatchResponseContent over
// Microsoft.Graph.Core 3.2.5, and GraphServiceClient.Batch.PostAsync sends
// them. That is used here, for three reasons that are all about not owning a
// second copy of something: ToPutRequestInformation produces the same
// serialized ExternalItem body that PutAsync sends, so a batched write and a
// single write cannot drift; the sub-request Content-Type and per-request id
// come out of the SDK rather than out of a string literal here; and
// BatchResponseContent hands back each sub-response as a real
// HttpResponseMessage, headers included, which is what makes Retry-After
// readable per item. BatchRequestContentCollection is the type the SDK now
// points at - the bare BatchRequestContent is obsolete as of 5.105.0 - but it is
// constructed with a limit of exactly twenty and handed at most twenty steps, so
// it can never split a call into batches of its own. The batch boundary is where
// timing and throttle accounting are taken, and a boundary the SDK moved without
// telling us would be a round trip nothing in this file counted.
//
// TIMING, AND WHY IT IS DIVIDED. WriteWithRetryAsync adds one sample per ROW to
// PushTiming.WriteInFlight and one to WriteBackoff. This writer keeps that
// invariant - one sample per row, never one per batch - by charging each item
// its SHARE of the batches it took part in. A 900 ms POST carrying twenty items
// is 45 ms of write time for each of them, and that amortised number is the one
// that is comparable with the 3.49 s/row the unbatched engine measured. Charging
// every item the full batch duration would multiply the sum by twenty and make
// the share-of-row-total arithmetic in PushTiming.Report lie.
// ---------------------------------------------------------------------------

namespace PushCore;

using Connector.Security.Logging;
using Microsoft.Graph;
using Microsoft.Graph.Models.ExternalConnectors;
using Microsoft.Graph.Models.ODataErrors;
using Microsoft.Kiota.Abstractions;
using PushCore.State;
using Serilog;
using System.Globalization;
using System.Net;

/// <summary>What became of one item that was offered to a batch.</summary>
public enum BatchItemOutcome
{
    /// <summary>This item's own sub-response carried a success status.</summary>
    /// <remarks>The only value on which a caller may record a content hash.</remarks>
    Written = 0,

    /// <summary>This item was refused, and this writer will not try it again.</summary>
    /// <remarks>
    /// Either a terminal status the service will keep returning, or a retryable
    /// one that survived every attempt. <see cref="BatchItemResult.StatusCode"/>
    /// separates the two.
    /// </remarks>
    Failed = 1,
}

/// <summary>What happened to one item, named so the caller can act on it.</summary>
/// <param name="ItemId">The external item ID, as it was supplied.</param>
/// <param name="Outcome">Written, or failed.</param>
/// <param name="StatusCode">The last status this item's own sub-response carried, or 0 when it never got one.</param>
/// <param name="Attempts">How many batches this item was placed in, from 1.</param>
/// <param name="Reason">The reason phrase for a failure, when the service gave one. Never a property or content value.</param>
/// <remarks>
/// A status of 0 means the batch envelope came back without a sub-response for
/// this item. That is reported as a failure rather than quietly dropped: an item
/// nobody answered for is an item that may or may not be in the index, and the
/// only safe reading of "may or may not" is "not".
/// </remarks>
public readonly record struct BatchItemResult(
    string ItemId,
    BatchItemOutcome Outcome,
    int StatusCode,
    int Attempts,
    string? Reason);

/// <summary>The per-item verdict for one call to <see cref="GraphBatchWriter.WriteAsync"/>.</summary>
/// <remarks>
/// Partial success is expected, so there is no single boolean that means "it
/// worked". <see cref="Items"/> is in the order the caller supplied, and
/// <see cref="Written"/> is the set - possibly empty, possibly all - for which a
/// content hash may now be recorded.
/// </remarks>
public sealed class BatchWriteResult
{
    /// <summary>Initializes a new instance of the <see cref="BatchWriteResult"/> class.</summary>
    /// <param name="items">One entry per item offered, in the order offered.</param>
    /// <param name="roundTrips">How many $batch POSTs were spent, retries included.</param>
    public BatchWriteResult(IReadOnlyList<BatchItemResult> items, int roundTrips)
    {
        ArgumentNullException.ThrowIfNull(items);

        this.Items = items;
        this.RoundTrips = roundTrips;

        int written = 0;

        foreach (BatchItemResult item in items)
        {
            if (item.Outcome == BatchItemOutcome.Written)
            {
                written++;
            }
        }

        this.WrittenCount = written;
        this.FailedCount = items.Count - written;
    }

    /// <summary>Gets one entry per item offered, in the order it was offered.</summary>
    public IReadOnlyList<BatchItemResult> Items { get; }

    /// <summary>Gets how many $batch POSTs this call spent, retries included.</summary>
    /// <remarks>
    /// The number that says whether batching paid. Twenty items in one round trip
    /// is the win; twenty items in nine round trips is a throttled run wearing a
    /// batch writer, and PushTiming.WriteBackoff will agree.
    /// </remarks>
    public int RoundTrips { get; }

    /// <summary>Gets how many items landed in the index.</summary>
    public int WrittenCount { get; }

    /// <summary>Gets how many items did not land.</summary>
    public int FailedCount { get; }

    /// <summary>Gets a value indicating whether every item offered landed.</summary>
    public bool AllWritten => this.FailedCount == 0;

    /// <summary>Gets the items a hash may be recorded for.</summary>
    public IEnumerable<BatchItemResult> Written =>
        this.Items.Where(item => item.Outcome == BatchItemOutcome.Written);

    /// <summary>Gets the items that were refused, with the status that refused them.</summary>
    public IEnumerable<BatchItemResult> Failed =>
        this.Items.Where(item => item.Outcome == BatchItemOutcome.Failed);

    /// <summary>Describes the outcome in one line for a log.</summary>
    /// <returns>Counts and statuses only. Never an item ID, a property or content.</returns>
    public string Describe()
    {
        string statuses = string.Join(
            ", ",
            this.Failed
                .GroupBy(item => item.StatusCode)
                .OrderBy(group => group.Key)
                .Select(group => FormattableString.Invariant($"{group.Count()}x{group.Key}")));

        return FormattableString.Invariant(
            $"{this.WrittenCount} written, {this.FailedCount} failed") +
            (statuses.Length == 0 ? string.Empty : " (" + statuses + ")") +
            FormattableString.Invariant($", {this.RoundTrips} round trip(s)");
    }
}

/// <summary>Writes external items to a connection through the Graph $batch endpoint.</summary>
/// <remarks>
/// One instance per run. It is stateless between calls to
/// <see cref="WriteAsync"/> and so may be shared by several writer tasks, in the
/// same way the <see cref="GraphServiceClient"/> it holds is.
/// </remarks>
public sealed class GraphBatchWriter
{
    /// <summary>The most sub-requests Graph will accept in one $batch.</summary>
    /// <remarks>
    /// A hard service limit rather than a tuning knob. The SDK's
    /// BatchRequestContent throws MaximumValueExceeded on the twenty-first step,
    /// so exceeding it is a crash and not a slow batch.
    /// </remarks>
    public const int MaxRequestsPerBatch = 20;

    /// <summary>Default ceiling on the serialized bytes carried in one $batch.</summary>
    /// <remarks>
    /// Counting requests is not enough. A single externalItem may be up to 30 MB,
    /// and twenty of those in one envelope is a request no service accepts, so a
    /// batch is closed on whichever of the two limits it reaches first. This
    /// figure is a deliberately conservative client-side guard rather than a
    /// quoted service number: the verified limit is the 30 MB per item, and a
    /// writer that guesses high on the envelope trades a latency win for a whole
    /// batch that fails at once. Raise it with the constructor once a tenant's
    /// real behaviour is known.
    /// </remarks>
    public const int DefaultMaxBatchContentBytes = 4 * 1024 * 1024;

    /// <summary>Attempts allowed per item before it is reported failed.</summary>
    /// <remarks>
    /// Deliberately the same 5 as PushEngine.MaxWriteAttempts, and duplicated
    /// here only because that constant is private to the engine. An item retried
    /// in a batch and an item retried on its own must give up after the same
    /// number of refusals, or two rows in one run get two different policies.
    /// Change one and change the other.
    /// </remarks>
    public const int MaxWriteAttempts = 5;

    private readonly GraphServiceClient graph;
    private readonly string connectionId;
    private readonly PushSummary summary;
    private readonly ILogger log;
    private readonly Action<ThrottleEvent>? onThrottle;
    private readonly long maxBatchContentBytes;

    /// <summary>Initializes a new instance of the <see cref="GraphBatchWriter"/> class.</summary>
    /// <param name="graph">An authenticated Graph client, built over <see cref="GraphPipeline"/>.</param>
    /// <param name="connectionId">The external connection items are written to.</param>
    /// <param name="summary">The run's tally. Throttle waits and timing are added to it.</param>
    /// <param name="log">Where refusals are reported. Item ID and status only.</param>
    /// <param name="onThrottle">Called once per throttled or transiently failed sub-response. May be null.</param>
    /// <param name="maxBatchContentBytes">Ceiling on serialized bytes per batch. Defaults to <see cref="DefaultMaxBatchContentBytes"/>.</param>
    /// <remarks>
    /// <paramref name="onThrottle"/> is called on the write path, so it must be
    /// cheap and must not throw. ThrottleEvent's own remarks say why: a round
    /// trip to the state store beside every 429 puts a second network call on
    /// precisely the run that is already struggling.
    /// </remarks>
    public GraphBatchWriter(
        GraphServiceClient graph,
        string connectionId,
        PushSummary summary,
        ILogger log,
        Action<ThrottleEvent>? onThrottle = null,
        long maxBatchContentBytes = DefaultMaxBatchContentBytes)
    {
        ArgumentNullException.ThrowIfNull(graph);
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionId);
        ArgumentNullException.ThrowIfNull(summary);
        ArgumentNullException.ThrowIfNull(log);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxBatchContentBytes);

        this.graph = graph;
        this.connectionId = connectionId;
        this.summary = summary;
        this.log = log;
        this.onThrottle = onThrottle;
        this.maxBatchContentBytes = maxBatchContentBytes;
    }

    /// <summary>Writes every item, batched, and reports what became of each one.</summary>
    /// <param name="batch">The items to write, each with the ID to write it under.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>One <see cref="BatchItemResult"/> per item, in the order supplied.</returns>
    /// <exception cref="ODataError">
    /// The $batch call itself was refused with a status that is not throttling
    /// or a transient 5xx - an expired token, a connection that does not exist.
    /// That is a fact about the run rather than about an item, and it is raised
    /// the same way PushEngine.WriteWithRetryAsync raises a terminal write.
    /// </exception>
    /// <remarks>
    /// <para>
    /// The list may be any length. It is split into service-legal batches here,
    /// on whichever of <see cref="MaxRequestsPerBatch"/> and the byte ceiling is
    /// reached first, so a caller never has to know either number. An item too
    /// large to share a batch with anything travels alone rather than being
    /// refused, because a batch of one still writes.
    /// </para>
    /// <para>
    /// Every pass is one attempt for every item still outstanding. Items refused
    /// with 429 or a transient 5xx go into the next pass after a single sleep,
    /// which honours the LONGEST Retry-After any sub-response asked for -
    /// sleeping the shortest of several stated waits is how one 429 becomes a
    /// run of them. Items refused with anything else are done, and are reported.
    /// </para>
    /// </remarks>
    public async Task<BatchWriteResult> WriteAsync(
        IReadOnlyList<(string ItemId, ExternalItem Item)> batch,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(batch);

        var tracked = new List<TrackedItem>(batch.Count);

        foreach ((string itemId, ExternalItem item) in batch)
        {
            tracked.Add(new TrackedItem(itemId, item));
        }

        var pending = new List<TrackedItem>(tracked);
        int roundTrips = 0;

        try
        {
            // Attempt numbering matches WriteWithRetryAsync's: one pass over the
            // outstanding items is attempt 1, and every item in that pass has
            // been attempted exactly that many times.
            for (int attempt = 1; pending.Count > 0; attempt++)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var retry = new List<TrackedItem>();
                TimeSpan? longestRetryAfter = null;
                int offset = 0;

                while (offset < pending.Count)
                {
                    List<PreparedRequest> chunk = this.NextChunk(pending, ref offset);
                    roundTrips++;

                    TimeSpan? asked = await this.PostChunkAsync(chunk, attempt, retry, cancellationToken);
                    longestRetryAfter = Longer(longestRetryAfter, asked);
                }

                if (retry.Count == 0)
                {
                    break;
                }

                if (attempt >= MaxWriteAttempts)
                {
                    // Out of attempts. Reported rather than thrown: the caller
                    // still has to be told which items in this call DID land.
                    foreach (TrackedItem item in retry)
                    {
                        item.Refuse(item.StatusCode, item.Reason ?? "still refused after every attempt");

                        this.log.Error(
                            "Batched write of {ItemId} gave up after {Max} attempts, last status {Status}.",
                            item.ItemId,
                            MaxWriteAttempts,
                            item.StatusCode);
                    }

                    break;
                }

                // 429 honours Retry-After; a transient 5xx that stated nothing
                // gets the same bounded backoff a single write would get.
                TimeSpan wait = longestRetryAfter ?? GraphThrottling.Backoff(attempt);

                this.log.Warning(
                    "{Count} of {Offered} batched writes were refused as retryable. Waiting {Seconds}s before " +
                    "attempt {Next} of {Max}.",
                    retry.Count,
                    pending.Count,
                    (int)wait.TotalSeconds,
                    attempt + 1,
                    MaxWriteAttempts);

                long sleeping = PushTiming.Now();
                await Task.Delay(wait, cancellationToken);
                Distribute(PushTiming.MicrosecondsSince(sleeping), retry, asleep: true);

                pending = retry;
            }
        }
        finally
        {
            // In the finally for the same reason the engine's is: an item that
            // gave up, and a call that was cancelled or threw, still spent the
            // time they spent, and a timing table that omits the expensive rows
            // is worse than none.
            foreach (TrackedItem item in tracked)
            {
                this.summary.Timing.WriteInFlight.Add(item.InFlightMicroseconds);
                this.summary.Timing.WriteBackoff.Add(item.BackoffMicroseconds);
            }
        }

        var results = new List<BatchItemResult>(tracked.Count);

        foreach (TrackedItem item in tracked)
        {
            results.Add(item.ToResult());
        }

        return new BatchWriteResult(results, roundTrips);
    }

    /// <summary>Takes the next service-legal batch off the outstanding items.</summary>
    /// <param name="pending">The outstanding items for this pass.</param>
    /// <param name="offset">Where to start, advanced past everything taken.</param>
    /// <returns>Between one and <see cref="MaxRequestsPerBatch"/> prepared sub-requests.</returns>
    /// <remarks>
    /// The body is serialized here rather than estimated, because the count that
    /// decides whether a batch fits is the count of bytes actually on the wire,
    /// and Value.Length is characters. An item whose own body exceeds the ceiling
    /// is taken anyway when the chunk is empty, so one oversized row degrades to
    /// a batch of one instead of wedging the pass.
    /// </remarks>
    private List<PreparedRequest> NextChunk(List<TrackedItem> pending, ref int offset)
    {
        var chunk = new List<PreparedRequest>(MaxRequestsPerBatch);
        long bytes = 0;

        while (offset < pending.Count && chunk.Count < MaxRequestsPerBatch)
        {
            TrackedItem item = pending[offset];

            RequestInformation request = this.graph.External
                .Connections[this.connectionId]
                .Items[item.ItemId]
                .ToPutRequestInformation(item.Item);

            // A body whose length cannot be asked for counts as zero, which
            // disables the byte ceiling for that item but never the request
            // count. Degrading to the weaker of the two limits beats throwing on
            // a stream the SDK chose not to make seekable.
            long size = request.Content is { CanSeek: true } body ? body.Length : 0;

            if (chunk.Count > 0 && bytes + size > this.maxBatchContentBytes)
            {
                // Rebuilt on the next chunk. One wasted serialization per batch,
                // and only on the boundary the byte ceiling closed - with rows of
                // the size this connector reads, the request count closes almost
                // every batch and this path is never taken.
                break;
            }

            // The request ID is the item's position, never its item ID. A caller
            // that offers the same ID twice would otherwise collide two items on
            // one sub-response and read one answer as two.
            chunk.Add(new PreparedRequest(item, request, offset.ToString(CultureInfo.InvariantCulture)));

            bytes += size;
            offset++;
        }

        return chunk;
    }

    /// <summary>Posts one batch and reads every sub-response in it.</summary>
    /// <param name="chunk">The prepared sub-requests, at most <see cref="MaxRequestsPerBatch"/> of them.</param>
    /// <param name="attempt">Which attempt this is for every item in the chunk, from 1.</param>
    /// <param name="retry">Items refused retryably are added here.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>The longest Retry-After any sub-response asked for, or null when none did.</returns>
    private async Task<TimeSpan?> PostChunkAsync(
        List<PreparedRequest> chunk,
        int attempt,
        List<TrackedItem> retry,
        CancellationToken cancellationToken)
    {
        var items = new List<TrackedItem>(chunk.Count);
        var byRequestId = new Dictionary<string, TrackedItem>(chunk.Count, StringComparer.Ordinal);

        // BatchRequestContentCollection, capped at exactly MaxRequestsPerBatch,
        // rather than the bare BatchRequestContent it wraps: the SDK marked the
        // latter obsolete in 5.105.0 and points here. The cap is the point of the
        // second argument. The collection is willing to split its steps across
        // several batches, and a split it performed on its own would be a round
        // trip this file did not count, did not time and could not attribute -
        // so it is handed at most twenty steps and can only ever produce one.
        //
        // Content-Type: application/json on each sub-request comes from
        // ToPutRequestInformation, which is the same serialization PutAsync
        // performs. Nothing about the body is composed by hand here.
        //
        // No using: the collection is not IDisposable, despite wrapping
        // HttpContent. That is the SDK's choice rather than an omission here.
        var content = new BatchRequestContentCollection(this.graph.RequestAdapter, MaxRequestsPerBatch);

        foreach (PreparedRequest prepared in chunk)
        {
            string requestId = await content.AddBatchRequestStepAsync(prepared.Request, prepared.RequestId);

            byRequestId[requestId] = prepared.Item;
            items.Add(prepared.Item);

            prepared.Item.Attempts = attempt;

            // Reset per pass. An item answered on attempt 1 and unanswered on
            // attempt 2 must read as unanswered, or the check below stops
            // catching the case it exists for.
            prepared.Item.Answered = false;
        }

        TimeSpan? longest = null;

        // The clock starts here and not above: serializing bodies is this
        // process's own cost, and charging it to time in flight would make the
        // one number that answers "is Graph slow" answer something else.
        long started = PushTiming.Now();
        BatchResponseContentCollection response;

        try
        {
            response = await this.graph.Batch.PostAsync(content, cancellationToken);
            Distribute(PushTiming.MicrosecondsSince(started), items, asleep: false);
        }
        catch (ODataError ex) when (ex.ResponseStatusCode is 429 or 502 or 503 or 504)
        {
            // The envelope itself was refused, so every item inside it was
            // refused, individually and identically.
            Distribute(PushTiming.MicrosecondsSince(started), items, asleep: false);

            TimeSpan? asked = GraphThrottling.RetryAfter(ex);

            foreach (TrackedItem item in items)
            {
                this.Refuse(item, ex.ResponseStatusCode, "the batch envelope was refused", asked, attempt, retry);
            }

            return asked;
        }
        catch (HttpRequestException ex)
        {
            Distribute(PushTiming.MicrosecondsSince(started), items, asleep: false);

            this.log.Warning(
                "A batch of {Count} failed in transit ({Message}) on attempt {Attempt} of {Max}.",
                items.Count,
                ex.Message,
                attempt,
                MaxWriteAttempts);

            foreach (TrackedItem item in items)
            {
                this.Refuse(item, 0, "the batch failed in transit", retryAfter: null, attempt, retry);
            }

            return null;
        }

        // Everything below this line is the point of the file. HTTP 200 on the
        // envelope has told us nothing yet.
        Dictionary<string, HttpStatusCode> statuses = await response.GetResponsesStatusCodesAsync();

        foreach (KeyValuePair<string, HttpStatusCode> pair in statuses)
        {
            if (!byRequestId.TryGetValue(pair.Key, out TrackedItem? item))
            {
                // A sub-response correlating to nothing we sent. Not fatal, but it
                // means the correlation assumption is wrong somewhere, and silence
                // would hide that.
                this.log.Warning(
                    "A batch sub-response carried request ID {RequestId}, which was not sent in this batch.",
                    pair.Key);

                continue;
            }

            int status = (int)pair.Value;
            item.Answered = true;

            if (status is >= 200 and <= 299)
            {
                item.Succeed(status);
                continue;
            }

            // Only a refusal needs its headers, and only a refusal pays for the
            // sub-response to be materialised. The message is disposable and the
            // SDK's own documentation says to dispose it.
            using HttpResponseMessage? subResponse = await response.GetResponseByIdAsync(pair.Key);

            if (status is 429 or 502 or 503 or 504)
            {
                TimeSpan? asked = RetryAfterOf(subResponse, status);

                this.Refuse(item, status, subResponse?.ReasonPhrase, asked, attempt, retry);
                longest = Longer(longest, asked);
                continue;
            }

            // Terminal for this item and this item only. The other nineteen are
            // unaffected, and the run does not stop.
            item.Refuse(status, subResponse?.ReasonPhrase);

            // The body, not just the status. A terminal 4xx from Graph carries an
            // error object naming the field it objected to, and logging only the
            // status discards precisely the sentence that ends the investigation:
            // one pilot spent a day on a 400 whose body read "DeserializationError
            // | The Value field is required". The sub-response is already
            // materialised above, so this costs a read of a few hundred bytes on
            // the failure path only.
            //
            // It is safe to log because an external-item error body describes the
            // request's shape, not its content - and this is the failure path, so
            // it is bounded by the number of refusals rather than by corpus size.
            string body = await ReadErrorBodyAsync(subResponse, cancellationToken);

            this.log.Error(
                "Batched write of {ItemId} was refused with status {Status}. Not retrying this item. {Body}",
                item.ItemId,
                status,
                body);
        }

        foreach (TrackedItem item in items)
        {
            if (item.Answered)
            {
                continue;
            }

            // No sub-response came back for this item. Retried, and failed if the
            // attempts run out - never read as success, because an unanswered PUT
            // is exactly the case where recording a hash loses the row for good.
            this.log.Warning(
                "The batch returned no sub-response for {ItemId} on attempt {Attempt} of {Max}.",
                item.ItemId,
                attempt,
                MaxWriteAttempts);

            this.Refuse(item, 0, "no sub-response was returned for this item", retryAfter: null, attempt, retry);
        }

        return longest;
    }

    /// <summary>Records a retryable refusal and queues the item for the next pass.</summary>
    /// <param name="item">The item that was refused.</param>
    /// <param name="status">The status that refused it, or 0 when there was none.</param>
    /// <param name="reason">What the service called it, when it said. Never a property or content value.</param>
    /// <param name="retryAfter">What the service asked us to wait, when it said.</param>
    /// <param name="attempt">Which attempt this was, from 1.</param>
    /// <param name="retry">The next pass's queue.</param>
    /// <remarks>
    /// ThrottleWaits is incremented once per refused SUB-REQUEST rather than once
    /// per refused batch, because that is what the counter has always meant: how
    /// many writes the service turned away. Batching changes how they were sent,
    /// not how many were refused.
    /// </remarks>
    private void Refuse(
        TrackedItem item,
        int status,
        string? reason,
        TimeSpan? retryAfter,
        int attempt,
        List<TrackedItem> retry)
    {
        item.Refuse(status, reason);

        if (status == 429)
        {
            this.summary.CountThrottleWait();
        }

        if (status != 0)
        {
            this.onThrottle?.Invoke(new ThrottleEvent(
                DateTime.UtcNow,
                status,
                retryAfter is null ? null : (int)retryAfter.Value.TotalSeconds,
                "batch",
                attempt));
        }

        retry.Add(item);
    }

    /// <summary>Reads Retry-After off one sub-response.</summary>
    /// <param name="response">The sub-response, as the SDK rebuilt it.</param>
    /// <param name="status">The status it carried.</param>
    /// <summary>
    /// Reads a refused sub-response's body for the log, scrubbed and capped.
    /// </summary>
    /// <param name="response">The refused sub-response, already materialised.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>A one-line description, never null and never throwing.</returns>
    /// <remarks>
    /// Three constraints, all of them the reason this is a method rather than a
    /// ReadAsStringAsync at the call site.
    ///
    /// It never throws. This runs on the failure path, and an exception raised
    /// while explaining a failure replaces a useful error with a useless one.
    ///
    /// It is scrubbed. A Graph error body normally names the offending field and
    /// not its value, but "normally" is not a guarantee worth logging against,
    /// and every other log line in this codebase passes through LogScrubber.
    ///
    /// It is capped. The cap is small on purpose: the sentence that identifies a
    /// malformed request is at the front of the body, and a refusal storm should
    /// not be able to fill a disk with the same paragraph.
    /// </remarks>
    private static async Task<string> ReadErrorBodyAsync(
        HttpResponseMessage? response,
        CancellationToken cancellationToken)
    {
        const int MaxBodyChars = 800;

        if (response?.Content is null)
        {
            return "No response body.";
        }

        try
        {
            string body = await response.Content.ReadAsStringAsync(cancellationToken);

            if (string.IsNullOrWhiteSpace(body))
            {
                return "Empty response body.";
            }

            body = LogScrubber.Scrub(body).ReplaceLineEndings(" ").Trim();

            return body.Length <= MaxBodyChars
                ? body
                : body.Substring(0, MaxBodyChars) + " [truncated]";
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return "Response body could not be read: " + ex.GetType().Name;
        }
    }

    /// <returns>The wait the service asked for, or null when it did not say.</returns>
    /// <remarks>
    /// The headers are transplanted onto an <see cref="ODataError"/> so that
    /// GraphThrottling.RetryAfter - which already handles both the seconds form
    /// and the HTTP-date form, and already caps the wait at 300 seconds - is the
    /// one parser in this codebase that reads this header. A second parser here
    /// would be a second thing to test and a second thing to get wrong, and
    /// GraphThrottling exists precisely because an untested header parser
    /// silently falls back to the guess.
    /// </remarks>
    private static TimeSpan? RetryAfterOf(HttpResponseMessage? response, int status)
    {
        var headers = new Dictionary<string, IEnumerable<string>>(StringComparer.OrdinalIgnoreCase);

        foreach (KeyValuePair<string, IEnumerable<string>> header in
            response?.Headers ?? Enumerable.Empty<KeyValuePair<string, IEnumerable<string>>>())
        {
            headers[header.Key] = header.Value;
        }

        return GraphThrottling.RetryAfter(new ODataError
        {
            ResponseStatusCode = status,
            ResponseHeaders = headers,
        });
    }

    /// <summary>Charges one batch's microseconds to the items that were in it.</summary>
    /// <param name="microseconds">What the batch, or the sleep after it, cost.</param>
    /// <param name="items">The items that shared it.</param>
    /// <param name="asleep">True for backoff, false for time in flight.</param>
    /// <remarks>
    /// The remainder is spread over the first few items rather than dropped, so
    /// the series sum stays exactly the wall clock the run spent. See the file
    /// header for why the cost is divided rather than repeated.
    /// </remarks>
    private static void Distribute(long microseconds, IReadOnlyList<TrackedItem> items, bool asleep)
    {
        if (items.Count == 0 || microseconds <= 0)
        {
            return;
        }

        long share = microseconds / items.Count;
        long remainder = microseconds % items.Count;

        for (int i = 0; i < items.Count; i++)
        {
            long amount = share + (i < remainder ? 1 : 0);

            if (asleep)
            {
                items[i].BackoffMicroseconds += amount;
            }
            else
            {
                items[i].InFlightMicroseconds += amount;
            }
        }
    }

    /// <summary>Picks the longer of two stated waits.</summary>
    /// <param name="left">A wait, or null.</param>
    /// <param name="right">A wait, or null.</param>
    /// <returns>The longer, or null when neither was stated.</returns>
    private static TimeSpan? Longer(TimeSpan? left, TimeSpan? right)
    {
        if (left is null)
        {
            return right;
        }

        if (right is null)
        {
            return left;
        }

        return right.Value > left.Value ? right : left;
    }

    /// <summary>One sub-request, serialized and ready to be added to a batch.</summary>
    /// <param name="Item">The item it writes.</param>
    /// <param name="Request">The PUT, built by the same code path PutAsync uses.</param>
    /// <param name="RequestId">The ID the sub-response will be correlated by.</param>
    private readonly record struct PreparedRequest(
        TrackedItem Item,
        RequestInformation Request,
        string RequestId);

    /// <summary>One item's progress across the passes of a single WriteAsync call.</summary>
    /// <remarks>Mutable and not thread-safe. It never leaves the call that made it.</remarks>
    private sealed class TrackedItem
    {
        internal TrackedItem(string itemId, ExternalItem item)
        {
            this.ItemId = itemId;
            this.Item = item;
        }

        internal string ItemId { get; }

        internal ExternalItem Item { get; }

        internal int Attempts { get; set; }

        internal int StatusCode { get; private set; }

        internal string? Reason { get; private set; }

        internal bool Answered { get; set; }

        internal long InFlightMicroseconds { get; set; }

        internal long BackoffMicroseconds { get; set; }

        private BatchItemOutcome Outcome { get; set; } = BatchItemOutcome.Failed;

        internal void Succeed(int status)
        {
            this.StatusCode = status;
            this.Reason = null;
            this.Outcome = BatchItemOutcome.Written;
        }

        internal void Refuse(int status, string? reason)
        {
            this.StatusCode = status;
            this.Reason = reason;
            this.Outcome = BatchItemOutcome.Failed;
        }

        internal BatchItemResult ToResult()
        {
            return new BatchItemResult(
                this.ItemId,
                this.Outcome,
                this.StatusCode,
                this.Attempts,
                this.Outcome == BatchItemOutcome.Written ? null : this.Reason);
        }
    }
}
