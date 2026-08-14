// ---------------------------------------------------------------------------
// ConnectionManagementServiceImpl.cs
// Called while an admin walks the "Add a Copilot connector" wizard. These calls
// are infrequent and high value for diagnostics, so they log at Information.
// ---------------------------------------------------------------------------

namespace SqlTicketsConnector.Connector
{
    using System;
    using System.Collections.Generic;
    using System.Threading.Tasks;
    using Grpc.Core;
    using Microsoft.Graph.Connectors.Contracts.Grpc;
    using Serilog;
    using SqlTicketsConnector.Security.Logging;
    using SqlTicketsConnector.Security.Sql;
    using SqlTicketsConnector.Server;
    using static Microsoft.Graph.Connectors.Contracts.Grpc.ConnectionManagementService;

    /// <summary>
    /// Implements the three calls the agent makes during connection creation, in
    /// order: ValidateAuthentication, ValidateCustomConfiguration, GetDataSourceSchema.
    /// </summary>
    public class ConnectionManagementServiceImpl : ConnectionManagementServiceBase
    {
        private readonly ITicketSourceFactory sourceFactory;
        private readonly ConnectorOptions options;
        private readonly ILogger logger;

        /// <summary>Initializes the service.</summary>
        public ConnectionManagementServiceImpl(
            ITicketSourceFactory sourceFactory,
            ConnectorOptions options,
            ILogger logger)
        {
            if (sourceFactory == null)
            {
                throw new ArgumentNullException(nameof(sourceFactory));
            }

            if (options == null)
            {
                throw new ArgumentNullException(nameof(options));
            }

            this.sourceFactory = sourceFactory;
            this.options = options;
            this.logger = logger ?? Log.Logger;
        }

        /// <summary>
        /// Step 1. Prove the configured identity actually reaches dbo.Tickets.
        /// </summary>
        public override async Task<ValidateAuthenticationResponse> ValidateAuthentication(
            ValidateAuthenticationRequest request,
            ServerCallContext context)
        {
            this.logger.Information(
                "ValidateAuthentication called for {DataSource}.",
                this.sourceFactory.Description);

            try
            {
                IReadOnlyList<string> problems = AgentRequestInspector.Inspect(
                    request.AuthenticationData,
                    this.options,
                    this.logger);

                if (problems.Count > 0)
                {
                    return new ValidateAuthenticationResponse
                    {
                        Status = new OperationStatus
                        {
                            Result = OperationResult.ValidationFailure,
                            StatusMessage = string.Join(" ", problems),
                        },
                    };
                }

                using (ITicketSource source = this.sourceFactory.Create(null))
                {
                    await source.ValidateAsync(context.CancellationToken).ConfigureAwait(false);
                }

                this.logger.Information(
                    "Data source {DataSource} is reachable and readable.",
                    this.sourceFactory.Description);

                return new ValidateAuthenticationResponse
                {
                    Status = new OperationStatus { Result = OperationResult.Success },
                };
            }
            catch (OperationCanceledException)
            {
                return new ValidateAuthenticationResponse
                {
                    Status = new OperationStatus
                    {
                        Result = OperationResult.Cancelled,
                        StatusMessage = "Validation cancelled.",
                    },
                };
            }
            catch (Exception ex)
            {
                SqlFailureCategory category = SqlErrorClassifier.Classify(ex);

                this.logger.Error(
                    RedactedException.Wrap(ex),
                    "ValidateAuthentication failed against {DataSource}. Category: {Category}.",
                    this.sourceFactory.Description,
                    category);

                return new ValidateAuthenticationResponse
                {
                    Status = new OperationStatus
                    {
                        Result = category == SqlFailureCategory.Authentication
                            ? OperationResult.AuthenticationIssue
                            : OperationResult.DatasourceError,
                        StatusMessage = LogScrubber.Scrub(ex.Message),
                    },
                };
            }
        }

        /// <summary>
        /// Step 2. This connector takes its behaviour from appsettings.json on the
        /// host, so any custom configuration the admin leaves blank is valid.
        /// </summary>
        public override Task<ValidateCustomConfigurationResponse> ValidateCustomConfiguration(
            ValidateCustomConfigurationRequest request,
            ServerCallContext context)
        {
            this.logger.Information("ValidateCustomConfiguration called.");

            return Task.FromResult(new ValidateCustomConfigurationResponse
            {
                Status = new OperationStatus { Result = OperationResult.Success },
            });
        }

        /// <summary>
        /// Step 3. Hand the agent the property list. The agent turns this into the
        /// Microsoft Graph schema, which is why this project never calls
        /// PATCH /external/connections/{id}/schema itself.
        /// </summary>
        public override Task<GetDataSourceSchemaResponse> GetDataSourceSchema(
            GetDataSourceSchemaRequest request,
            ServerCallContext context)
        {
            this.logger.Information("GetDataSourceSchema called.");

            try
            {
                DataSourceSchema schema = SqlDataSource.BuildSchema();

                this.logger.Information(
                    "Returning {PropertyCount} source properties to the agent.",
                    schema.PropertyList.Count);

                return Task.FromResult(new GetDataSourceSchemaResponse
                {
                    DataSourceSchema = schema,
                    Status = new OperationStatus { Result = OperationResult.Success },
                });
            }
            catch (Exception ex)
            {
                this.logger.Error(RedactedException.Wrap(ex), "Failed to build the data source schema.");

                return Task.FromResult(new GetDataSourceSchemaResponse
                {
                    DataSourceSchema = null,
                    Status = new OperationStatus
                    {
                        Result = OperationResult.DatasourceError,
                        StatusMessage = LogScrubber.Scrub(ex.Message),
                    },
                });
            }
        }
    }
}
