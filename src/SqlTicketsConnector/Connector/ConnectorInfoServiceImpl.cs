// ---------------------------------------------------------------------------
// ConnectorInfoServiceImpl.cs
// Identity and health reporting.
//
// HealthCheck is polled continuously by the agent and logs nothing here. Call
// telemetry for it comes from CallLoggingInterceptor at Verbose, which keeps the
// file readable without losing the signal entirely.
// ---------------------------------------------------------------------------

namespace SqlTicketsConnector.Connector
{
    using System;
    using System.Threading.Tasks;
    using Grpc.Core;
    using Microsoft.Graph.Connectors.Contracts.Grpc;
    using static Microsoft.Graph.Connectors.Contracts.Grpc.ConnectorInfoService;

    /// <summary>Identity and health reporting for the connector.</summary>
    public class ConnectorInfoServiceImpl : ConnectorInfoServiceBase
    {
        /// <summary>
        /// The connector ID this build was created for. It must appear in
        /// CustomConnectorPortMap.json and Manifest.json. Changing it breaks every
        /// existing connection, which is why the configured value is compared
        /// against this one at startup.
        /// </summary>
        public const string DefaultConnectorId = "9e5e2b95-e7ab-4266-98c7-4f7868d377bf";

        private readonly string connectorId;

        /// <summary>Initializes the service with the configured connector ID.</summary>
        public ConnectorInfoServiceImpl(string connectorId)
        {
            this.connectorId = string.IsNullOrWhiteSpace(connectorId) ? DefaultConnectorId : connectorId;
        }

        /// <summary>Gets the connector ID reported to the agent.</summary>
        public string ConnectorId
        {
            get { return this.connectorId; }
        }

        /// <inheritdoc />
        public override Task<GetBasicConnectorInfoResponse> GetBasicConnectorInfo(
            GetBasicConnectorInfoRequest request,
            ServerCallContext context)
        {
            return Task.FromResult(new GetBasicConnectorInfoResponse
            {
                ConnectorId = this.connectorId,
            });
        }

        /// <summary>
        /// Polled regularly by the agent. A run of failures marks the connection as
        /// failed, so this stays cheap and never touches the data source.
        /// </summary>
        public override Task<HealthCheckResponse> HealthCheck(
            HealthCheckRequest request,
            ServerCallContext context)
        {
            return Task.FromResult(new HealthCheckResponse());
        }
    }
}
