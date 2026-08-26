// ---------------------------------------------------------------------------
// ConnectionValidationTimeoutTests.cs
// Evidence that connection validation returns inside the platform's budget.
//
// The Copilot connectors SDK gives every ConnectionManagementService method 30
// seconds, then shows the admin its own timeout and discards whatever this
// connector was about to say. An unreachable data source is exactly when the
// StatusMessage matters, so validation has to lose the race deliberately.
// ---------------------------------------------------------------------------

namespace SqlTicketsConnector.Tests
{
    using System;
    using System.Collections.Generic;
    using System.Diagnostics;
    using System.Threading;
    using System.Threading.Tasks;
    using Microsoft.Graph.Connectors.Contracts.Grpc;
    using Serilog.Core;
    using SqlTicketsConnector.Connector;
    using Connector.Security.Configuration;
    using SqlTicketsConnector.Server;
    using SqlTicketsConnector.Tests.TestSupport;
    using Xunit;

    public class ConnectionValidationTimeoutTests
    {
        [Fact]
        public async Task A_data_source_that_never_answers_fails_inside_the_platform_budget()
        {
            ConnectorOptions options = TestData.ValidOptions();
            options.Connector.ConnectionCallTimeoutSeconds = 5;

            var source = new FakeTicketSource(new List<TicketRow>(), failure: null, hangOnValidate: true);
            var service = new ConnectionManagementServiceImpl(source, options, Logger.None);
            var context = new FakeServerCallContext("ValidateAuthentication", CancellationToken.None);

            var stopwatch = Stopwatch.StartNew();
            ValidateAuthenticationResponse response = await service.ValidateAuthentication(
                new ValidateAuthenticationRequest(), context);
            stopwatch.Stop();

            // The platform's limit is 30s. Returning at all is the point; the
            // margin is generous so a loaded CI agent cannot make this flaky.
            Assert.True(
                stopwatch.Elapsed < TimeSpan.FromSeconds(25),
                "Validation took " + stopwatch.Elapsed + ", which the platform would have cut short.");

            Assert.Equal(OperationResult.DatasourceError, response.Status.Result);

            // AuthenticationIssue would move the connection to a failed state and
            // tell the admin their credentials are wrong, which they are not.
            Assert.NotEqual(OperationResult.AuthenticationIssue, response.Status.Result);

            Assert.Contains("did not respond within", response.Status.StatusMessage);
            Assert.Contains("reachable from this server", response.Status.StatusMessage);
        }

        [Fact]
        public async Task A_reachable_data_source_still_validates_normally()
        {
            ConnectorOptions options = TestData.ValidOptions();
            var source = new FakeTicketSource(new List<TicketRow>());
            var service = new ConnectionManagementServiceImpl(source, options, Logger.None);
            var context = new FakeServerCallContext("ValidateAuthentication", CancellationToken.None);

            ValidateAuthenticationResponse response = await service.ValidateAuthentication(
                new ValidateAuthenticationRequest(), context);

            Assert.Equal(OperationResult.Success, response.Status.Result);
        }

        /// <summary>
        /// The platform cancelling and this connector's own deadline are
        /// different events and must not report the same way: one is the agent
        /// giving up, the other is the data source failing to answer.
        /// </summary>
        [Fact]
        public async Task Platform_cancellation_is_reported_as_cancelled_not_as_a_data_source_error()
        {
            ConnectorOptions options = TestData.ValidOptions();
            options.Connector.ConnectionCallTimeoutSeconds = 25;

            var source = new FakeTicketSource(new List<TicketRow>(), failure: null, hangOnValidate: true);
            var service = new ConnectionManagementServiceImpl(source, options, Logger.None);

            using (var platform = new CancellationTokenSource())
            {
                var context = new FakeServerCallContext("ValidateAuthentication", platform.Token);
                platform.CancelAfter(TimeSpan.FromMilliseconds(200));

                ValidateAuthenticationResponse response = await service.ValidateAuthentication(
                    new ValidateAuthenticationRequest(), context);

                Assert.Equal(OperationResult.Cancelled, response.Status.Result);
            }
        }

        [Fact]
        public void A_budget_the_platform_would_outlive_is_rejected_at_startup()
        {
            ConnectorOptions options = TestData.ValidOptions();
            options.Connector.ConnectionCallTimeoutSeconds = 45;

            ValidationErrors errors = options.Validate();

            Assert.True(errors.HasErrors);
            Assert.Contains("ConnectionCallTimeoutSeconds", errors.ToMessage());
        }
    }
}
