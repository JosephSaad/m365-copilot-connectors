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

        public ISerializationWriterFactory SerializationWriterFactory =>
            new global::Microsoft.Kiota.Serialization.Json.JsonSerializationWriterFactory();

        public string BaseUrl { get; set; } = "https://graph.microsoft.com/v1.0";

        public void EnableBackingStore(IBackingStoreFactory backingStoreFactory)
        {
        }

        public Task<ModelType> SendAsync<ModelType>(
            RequestInformation requestInfo,
            ParsableFactory<ModelType> factory,
            Dictionary<string, ParsableFactory<IParsable>> errorMapping = null,
            CancellationToken cancellationToken = default)
            where ModelType : IParsable
        {
            if (requestInfo.HttpMethod is Method.PATCH or Method.POST or Method.PUT)
            {
                this.Writes.Add(requestInfo);
            }

            string url = requestInfo.URI.ToString();

            object result = url.EndsWith("/schema", StringComparison.OrdinalIgnoreCase)
                ? this.registeredSchema
                : this.connection;

            return Task.FromResult((ModelType)result);
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
    }
}
