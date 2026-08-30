// ---------------------------------------------------------------------------
// StubGraphAdapter.cs
// A Kiota IRequestAdapter that answers Graph external-connection GETs from
// canned objects, so PushEngine's call sites can be driven without a tenant.
//
// It exists for one reason: the schema-ownership control is a single call in
// EnsureSchemaAsync, and a pure-function test cannot notice that call being
// deleted. This stub lets a test walk the real code path.
//
// IT ALSO ANSWERS $batch, AND THAT HALF IS NOT A CONVENIENCE. GraphBatchWriter
// rests entirely on one asymmetry - the envelope comes back HTTP 200 while the
// sub-responses inside it carry 429s and terminal 4xxs of their own - and a
// stub that could only answer "the whole call succeeded" could not express the
// case the writer exists for. So the batch half here builds a real response
// envelope, per sub-request, with a status chosen per item (BatchStatusFor) and
// a Retry-After on the throttled ones. Without it the batch path is reachable
// only against a live tenant, which means in practice it is not tested at all.
// ---------------------------------------------------------------------------

namespace SqlTicketsConnector.Tests.TestSupport
{
    using System;
    using System.Collections.Generic;
    using System.Globalization;
    using System.IO;
    using System.Linq;
    using System.Net;
    using System.Net.Http;
    using System.Text;
    using System.Text.Json;
    using System.Threading;
    using System.Threading.Tasks;
    using Microsoft.Graph.Models.ExternalConnectors;
    using Microsoft.Graph.Models.ODataErrors;
    using Microsoft.Kiota.Abstractions;
    using Microsoft.Kiota.Abstractions.Serialization;
    using Microsoft.Kiota.Abstractions.Store;

    /// <summary>Serves a connection and its registered schema; records writes.</summary>
    public sealed class StubGraphAdapter : IRequestAdapter
    {
        private readonly ExternalConnection connection;
        private readonly Schema registeredSchema;

        public StubGraphAdapter(ExternalConnection connection, Schema registeredSchema)
        {
            this.connection = connection;
            this.registeredSchema = registeredSchema;
        }

        /// <summary>Gets the schemas PATCHed through this adapter.</summary>
        public List<RequestInformation> Writes { get; } = new List<RequestInformation>();

        /// <summary>Gets the item IDs PUT through this adapter, in order.</summary>
        private int writesInFlight;
        private int maxConcurrentWrites;
        private int batchRoundTrips;

        public List<string> WrittenItemIds { get; } = new List<string>();

        /// <summary>
        /// Gets the JSON body of each item PUT through this adapter, in order.
        ///
        /// Read off the wire rather than from the object the engine built: what
        /// matters for an ACL assertion is what Graph would actually have been
        /// sent, and serialization is part of that.
        /// </summary>
        public List<string> WrittenBodies { get; } = new List<string>();

        /// <summary>
        /// The HTTP method of every $batch sub-request, in the order they were
        /// sent.
        ///
        /// Recorded because a round-trip count cannot tell a batched DELETE from
        /// a batched PUT, and the difference between those two is the difference
        /// between removing an item and rewriting the one you were asked to
        /// remove. A test that only counts envelopes would pass through that.
        /// </summary>
        public List<string> BatchMethods { get; } = new List<string>();

        /// <summary>
        /// Gets or sets a hook that fails a write. Returning an exception for a
        /// given item ID makes that PUT throw, which is how a test drives the
        /// engine's behaviour when a write dies partway through a run.
        /// </summary>
        public Func<string, Exception> FailItem { get; set; }

        /// <summary>
        /// Gets or sets how long a write pretends to take. Zero by default. A test
        /// that wants to observe overlap needs the writes to overlap, and a stub
        /// that returns instantly can be driven by eight writers and still never
        /// have two in flight at once.
        /// </summary>
        public TimeSpan WriteDelay { get; set; } = TimeSpan.Zero;

        /// <summary>
        /// Gets or sets the status one item's own sub-response carries inside a
        /// $batch. Returning null, or leaving the hook unset, answers 200.
        ///
        /// This is the hook the whole batch design turns on. Graph returns 200
        /// on the envelope and puts the real answer in each sub-response, so a
        /// test needs to say "the envelope succeeded AND item a5 was refused"
        /// - which is not a thing FailItem can express, because FailItem throws
        /// and a throw is a failure of the CALL.
        /// </summary>
        public Func<string, int?> BatchStatusFor { get; set; }

        /// <summary>
        /// Gets or sets the Retry-After, in seconds, put on a 429 sub-response.
        ///
        /// One rather than zero, and it matters. GraphThrottling.RetryAfter
        /// ignores a non-positive value, so a zero here would have the writer
        /// fall back to GraphThrottling.Backoff - four real seconds on the first
        /// retry - and a unit test would pay them.
        /// </summary>
        public int BatchRetryAfterSeconds { get; set; } = 1;

        /// <summary>
        /// Gets the greatest number of writes that were in flight simultaneously.
        /// This is how the ordering guarantee is tested as a fact rather than
        /// inferred from the order things happened to land in.
        /// </summary>
        /// <remarks>
        /// A $batch counts as ONE write in flight however many sub-requests it
        /// carries, because it is one call on the wire. Counting it as twenty
        /// would have this number measure the stub's arithmetic rather than the
        /// engine's writer gate.
        /// </remarks>
        public int MaxConcurrentWrites => Volatile.Read(ref this.maxConcurrentWrites);

        /// <summary>Gets how many $batch POSTs this adapter answered.</summary>
        /// <remarks>
        /// Zero is the assertion that matters: it is how a test proves that
        /// Settings:Batch = false, or a dry run, really did take the
        /// single-item path rather than merely producing the same counts.
        /// </remarks>
        public int BatchRoundTrips => Volatile.Read(ref this.batchRoundTrips);

        /// <summary>Gets how many sub-requests each $batch carried, in order.</summary>
        /// <remarks>
        /// Recorded per round trip rather than summed, because "twenty items in
        /// one POST" and "twenty items in twenty POSTs" have identical totals
        /// and are the two outcomes batching exists to tell apart.
        /// </remarks>
        public List<int> BatchSizes { get; } = new List<int>();

        /// <summary>
        /// The same writer factory a real GraphServiceClient builds: JSON,
        /// wrapped so the backing store is honoured.
        /// </summary>
        /// <remarks>
        /// THE WRAPPER IS THE POINT, and its absence hid a defect for the life
        /// of this project. Graph SDK models are backed models, and this proxy is
        /// what makes a second serialization of the same instance emit only what
        /// changed since - which, for an object nobody touched, is nothing.
        /// GraphServiceClient installs it; a bare JsonSerializationWriterFactory
        /// does not.
        ///
        /// So every test here serialized in full every time, however many
        /// attempts an item took, and the harness could not express the failure
        /// where a retried item loses its ACL and Graph refuses it 400. That
        /// failure reached a tenant instead, on the first run that was ever
        /// throttled, and cost 191 items under a run that reported success.
        ///
        /// A fake that is easier to satisfy than the real thing is a fake that
        /// certifies code the real thing rejects.
        /// </remarks>
        public ISerializationWriterFactory SerializationWriterFactory
        {
            get
            {
                var registry = new global::Microsoft.Kiota.Abstractions.Serialization.SerializationWriterFactoryRegistry();

                registry.ContentTypeAssociatedFactories["application/json"] =
                    new global::Microsoft.Kiota.Serialization.Json.JsonSerializationWriterFactory();

                return global::Microsoft.Kiota.Abstractions.ApiClientBuilder
                    .EnableBackingStoreForSerializationWriterFactory(registry);
            }
        }

        public string BaseUrl { get; set; } = "https://graph.microsoft.com/v1.0";

        public void EnableBackingStore(IBackingStoreFactory backingStoreFactory)
        {
        }

        public async Task<ModelType> SendAsync<ModelType>(
            RequestInformation requestInfo,
            ParsableFactory<ModelType> factory,
            Dictionary<string, ParsableFactory<IParsable>> errorMapping = null,
            CancellationToken cancellationToken = default)
            where ModelType : IParsable
        {
            string url = requestInfo.URI.ToString();
            int itemsAt = url.IndexOf("/items/", StringComparison.OrdinalIgnoreCase);
            string itemId = itemsAt < 0 ? null : url.Substring(itemsAt + "/items/".Length).Trim('/');

            if (requestInfo.HttpMethod is Method.PATCH or Method.POST or Method.PUT)
            {
                if (itemId is not null)
                {
                    this.EnterWrite();

                    try
                    {
                        if (this.WriteDelay > TimeSpan.Zero)
                        {
                            await Task.Delay(this.WriteDelay, cancellationToken);
                        }
                    }
                    finally
                    {
                        this.LeaveWrite();
                    }

                    Exception failure = this.FailItem?.Invoke(itemId);

                    if (failure is not null)
                    {
                        // Thrown BEFORE the write is recorded: a failed PUT must
                        // not look like a write to anything downstream.
                        throw failure;
                    }

                    // Locked: an unordered source is written by several writer
                    // threads at once, and a torn list here would fail a test for
                    // a reason that has nothing to do with the code under test.
                    lock (this.WrittenItemIds)
                    {
                        this.WrittenItemIds.Add(itemId);
                        this.WrittenBodies.Add(ReadBody(requestInfo));
                    }
                }

                lock (this.WrittenItemIds)
                {
                    this.Writes.Add(requestInfo);
                }
            }

            object result = url.EndsWith("/schema", StringComparison.OrdinalIgnoreCase)
                ? this.registeredSchema
                : itemId is not null
                    ? new ExternalItem { Id = itemId }
                    : this.connection;

            return (ModelType)result;
        }

        /// <summary>Records that a write started, and the new high-water concurrency.</summary>
        private void EnterWrite()
        {
            int now = Interlocked.Increment(ref this.writesInFlight);
            int seen = Volatile.Read(ref this.maxConcurrentWrites);

            while (now > seen)
            {
                int was = Interlocked.CompareExchange(ref this.maxConcurrentWrites, now, seen);

                if (was == seen)
                {
                    break;
                }

                seen = was;
            }
        }

        /// <summary>Records that a write finished.</summary>
        private void LeaveWrite()
        {
            Interlocked.Decrement(ref this.writesInFlight);
        }

        public Task<IEnumerable<ModelType>> SendCollectionAsync<ModelType>(
            RequestInformation requestInfo,
            ParsableFactory<ModelType> factory,
            Dictionary<string, ParsableFactory<IParsable>> errorMapping = null,
            CancellationToken cancellationToken = default)
            where ModelType : IParsable
        {
            throw new NotSupportedException();
        }

        public Task<ModelType> SendPrimitiveAsync<ModelType>(
            RequestInformation requestInfo,
            Dictionary<string, ParsableFactory<IParsable>> errorMapping = null,
            CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }

        public Task<IEnumerable<ModelType>> SendPrimitiveCollectionAsync<ModelType>(
            RequestInformation requestInfo,
            Dictionary<string, ParsableFactory<IParsable>> errorMapping = null,
            CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }

        /// <summary>
        /// Answers a DELETE, or a $batch. GraphServiceClient.Batch.PostAsync
        /// arrives here rather than at SendAsync, which is why a batch used to
        /// come back as a null response and NRE inside the SDK.
        /// </summary>
        public async Task SendNoContentAsync(
            RequestInformation requestInfo,
            Dictionary<string, ParsableFactory<IParsable>> errorMapping = null,
            CancellationToken cancellationToken = default)
        {
            if (IsBatch(requestInfo))
            {
                // Deliberately NOT added to Writes. That list is read as "the
                // schema and item requests this run made", and an envelope
                // carrying twenty of them would be counted as one of them.
                await this.AnswerBatchAsync(requestInfo, cancellationToken);
                return;
            }

            this.Writes.Add(requestInfo);
        }

        public Task<T> ConvertToNativeRequestAsync<T>(
            RequestInformation requestInfo, CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }

        /// <summary>Says whether this request is the $batch endpoint.</summary>
        /// <param name="requestInfo">The request the SDK built.</param>
        /// <returns>True for a $batch POST.</returns>
        /// <remarks>
        /// Matched on the URL TEMPLATE and not on the URI. The $batch builder in
        /// Microsoft.Graph.Core is hand-written rather than generated and never
        /// seeds "baseurl" into its path parameters - the real HTTP adapter
        /// supplies it on the way out - so reading requestInfo.URI here throws
        /// before it can answer the question.
        /// </remarks>
        private static bool IsBatch(RequestInformation requestInfo)
        {
            return requestInfo.UrlTemplate is not null &&
                requestInfo.UrlTemplate.Contains("$batch", StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>Reads the item ID out of a sub-request's relative URL.</summary>
        /// <param name="url">The sub-request URL, as the SDK serialized it.</param>
        /// <returns>The item ID, or null when the URL is not an item write.</returns>
        private static string ItemIdOf(string url)
        {
            int itemsAt = url is null
                ? -1
                : url.IndexOf("/items/", StringComparison.OrdinalIgnoreCase);

            return itemsAt < 0 ? null : url.Substring(itemsAt + "/items/".Length).Trim('/');
        }

        /// <summary>Reads one batch envelope and answers every sub-request in it.</summary>
        /// <param name="requestInfo">The $batch POST.</param>
        /// <param name="cancellationToken">Cancellation.</param>
        /// <returns>A task for the operation.</returns>
        /// <remarks>
        /// The response is handed back through the NativeResponseHandler the SDK
        /// attached to the request, because BatchRequestBuilder.PostAsync reads
        /// its Value as the HttpResponseMessage rather than reading anything this
        /// method returns. Leaving it unset is what made a batch NRE.
        /// </remarks>
        private async Task AnswerBatchAsync(
            RequestInformation requestInfo, CancellationToken cancellationToken)
        {
            using (JsonDocument envelope = JsonDocument.Parse(ReadBody(requestInfo)))
            {
                JsonElement requests = envelope.RootElement.GetProperty("requests");

                // One call on the wire is one write in flight, and it waits
                // WriteDelay ONCE. Charging the delay per sub-request would make
                // a batch of twenty cost what twenty PUTs cost, which is the one
                // thing batching exists not to do - a timing or concurrency
                // assertion over it would then be measuring this file.
                this.EnterWrite();

                try
                {
                    if (this.WriteDelay > TimeSpan.Zero)
                    {
                        await Task.Delay(this.WriteDelay, cancellationToken);
                    }
                }
                finally
                {
                    this.LeaveWrite();
                }

                Interlocked.Increment(ref this.batchRoundTrips);

                string body;
                int carried = 0;

                using (var buffer = new MemoryStream())
                {
                    using (var writer = new Utf8JsonWriter(buffer))
                    {
                        writer.WriteStartObject();
                        writer.WriteStartArray("responses");

                        foreach (JsonElement request in requests.EnumerateArray())
                        {
                            this.WriteSubResponse(writer, request);
                            carried++;
                        }

                        writer.WriteEndArray();
                        writer.WriteEndObject();
                    }

                    body = Encoding.UTF8.GetString(buffer.ToArray());
                }

                lock (this.WrittenItemIds)
                {
                    this.BatchSizes.Add(carried);
                }

                // HTTP 200 on the envelope, whatever the sub-responses said.
                // That is not a simplification: it is precisely what Graph does,
                // and a writer that reads this status and moves on is the defect
                // GraphBatchWriter's header describes.
                var message = new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(body, Encoding.UTF8, "application/json"),
                };

                ResponseHandlerOption option = requestInfo.RequestOptions
                    .OfType<ResponseHandlerOption>()
                    .FirstOrDefault();

                if (option?.ResponseHandler is NativeResponseHandler native)
                {
                    native.Value = message;
                }
            }
        }

        /// <summary>Answers one sub-request, and records it when it succeeded.</summary>
        /// <param name="writer">The response envelope being built.</param>
        /// <param name="request">The sub-request, as the SDK serialized it.</param>
        private void WriteSubResponse(Utf8JsonWriter writer, JsonElement request)
        {
            lock (this.WrittenItemIds)
            {
                this.BatchMethods.Add(
                    request.TryGetProperty("method", out JsonElement verb) ? verb.GetString() ?? "?" : "?");
            }

            string requestId = request.GetProperty("id").GetString();
            string itemId = ItemIdOf(
                request.TryGetProperty("url", out JsonElement url) ? url.GetString() : null);

            int status = this.StatusFor(itemId);
            bool succeeded = status >= 200 && status <= 299;

            if (succeeded && itemId is not null)
            {
                // Recorded only on success, exactly as the single-item path
                // records only after the PUT returned. An item refused inside a
                // batch must not look like a write to anything downstream.
                lock (this.WrittenItemIds)
                {
                    this.WrittenItemIds.Add(itemId);
                    this.WrittenBodies.Add(
                        request.TryGetProperty("body", out JsonElement body)
                            ? body.GetRawText()
                            : string.Empty);
                }
            }

            writer.WriteStartObject();
            writer.WriteString("id", requestId);
            writer.WriteNumber("status", status);

            writer.WriteStartObject("headers");
            writer.WriteString("Content-Type", "application/json");

            if (status == 429)
            {
                writer.WriteString(
                    "Retry-After", this.BatchRetryAfterSeconds.ToString(CultureInfo.InvariantCulture));
            }

            writer.WriteEndObject();

            writer.WriteStartObject("body");

            if (succeeded)
            {
                writer.WriteString("id", itemId);
            }
            else
            {
                writer.WriteStartObject("error");
                writer.WriteString("code", status == 429 ? "activityLimitReached" : "invalidRequest");
                writer.WriteString("message", "the stub refused this sub-request");
                writer.WriteEndObject();
            }

            writer.WriteEndObject();
            writer.WriteEndObject();
        }

        /// <summary>Chooses the status one item's sub-response carries.</summary>
        /// <param name="itemId">The item the sub-request writes.</param>
        /// <returns>An HTTP status. 200 unless a hook said otherwise.</returns>
        /// <remarks>
        /// FailItem is honoured here as well as in SendAsync, so a fixture
        /// written against the single-item path still means the same thing when
        /// Settings:Batch is turned on. It cannot mean the same MECHANICALLY - a
        /// sub-request has no way to throw, because the call it travelled in
        /// succeeded - so the exception is translated into the status it would
        /// have arrived as. An exception carrying no status becomes 500, which
        /// GraphBatchWriter treats as terminal, matching a throw's finality.
        /// </remarks>
        private int StatusFor(string itemId)
        {
            if (itemId is null)
            {
                return 200;
            }

            int? chosen = this.BatchStatusFor?.Invoke(itemId);

            if (chosen is not null)
            {
                return chosen.Value;
            }

            Exception failure = this.FailItem?.Invoke(itemId);

            if (failure is null)
            {
                return 200;
            }

            return failure is ODataError odata && odata.ResponseStatusCode != 0
                ? odata.ResponseStatusCode
                : 500;
        }

        private static string ReadBody(RequestInformation requestInfo)
        {
            if (requestInfo.Content is null)
            {
                return string.Empty;
            }

            if (requestInfo.Content.CanSeek)
            {
                requestInfo.Content.Position = 0;
            }

            using (var reader = new StreamReader(requestInfo.Content, leaveOpen: true))
            {
                return reader.ReadToEnd();
            }
        }
    }
}
