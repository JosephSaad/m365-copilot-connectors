// ---------------------------------------------------------------------------
// StubGraphAdapter.cs
// A Kiota IRequestAdapter that answers Graph external-connection GETs from
// canned objects, so PushEngine's call sites can be driven without a tenant.
//
// It exists for one reason: the schema-ownership control is a single call in
// EnsureSchemaAsync, and a pure-function test cannot notice that call being
// deleted. This stub lets a test walk the real code path.
// ---------------------------------------------------------------------------

namespace SqlTicketsConnector.Tests.TestSupport
{
    using System;
    using System.Collections.Generic;
    using System.IO;
    using System.Threading;
    using System.Threading.Tasks;
    using Microsoft.Graph.Models.ExternalConnectors;
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
        /// Gets the greatest number of writes that were in flight simultaneously.
        /// This is how the ordering guarantee is tested as a fact rather than
        /// inferred from the order things happened to land in.
        /// </summary>
        public int MaxConcurrentWrites => Volatile.Read(ref this.maxConcurrentWrites);

        public ISerializationWriterFactory SerializationWriterFactory =>
            new global::Microsoft.Kiota.Serialization.Json.JsonSerializationWriterFactory();

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

        public Task SendNoContentAsync(
            RequestInformation requestInfo,
            Dictionary<string, ParsableFactory<IParsable>> errorMapping = null,
            CancellationToken cancellationToken = default)
        {
            this.Writes.Add(requestInfo);
            return Task.CompletedTask;
        }

        public Task<T> ConvertToNativeRequestAsync<T>(
            RequestInformation requestInfo, CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
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
