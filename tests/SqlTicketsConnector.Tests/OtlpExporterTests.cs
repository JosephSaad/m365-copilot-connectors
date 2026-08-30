// ---------------------------------------------------------------------------
// OtlpExporterTests.cs
// Does anything actually leave the process.
//
// WHY THIS EXISTS SEPARATELY FROM PushTelemetryTests. That file listens to the
// same in-process mechanism the SDK listens to, and proves the engine EMITS the
// right spans and measurements. It would pass in full against an exporter that
// was never wired up, never started, or that dropped everything on the floor at
// exit. This file puts a socket in the way and asserts that bytes arrive at it.
//
// The three failures it is here to catch are all silent ones:
//
//   The provider is never built, because the configuration was read wrongly or
//   the flag was not honoured. Nothing logs an error; there is simply no
//   telemetry, and the absence looks exactly like a quiet estate.
//
//   The provider is built but never flushed. Counters are recorded ONCE, at the
//   end of a run, and the periodic reader exports on a timer measured in tens
//   of seconds - so a push tool that exits promptly loses every measurement it
//   ever took unless something forces the flush first.
//
//   The flush happens after the process has torn down the thing that would
//   report its failure.
//
// IT RUNS IN BOTH BUILD CONFIGURATIONS, and asserts different things in each,
// which is the point rather than a compromise. Compile constants do not cross
// project boundaries, so this test project cannot see OTLP_EXPORTER and cannot
// use #if to tell which build it is in. It asks the object instead - which is
// also the only way to test the documented behaviour of a default build, where
// Otlp:Enabled must warn rather than pretend.
// ---------------------------------------------------------------------------

namespace SqlTicketsConnector.Tests
{
    using System;
    using System.Collections.Concurrent;
    using System.Collections.Generic;
    using System.Net;
    using System.Net.Sockets;
    using System.Text;
    using System.Threading;
    using System.Threading.Tasks;
    using Microsoft.Graph.Models.ExternalConnectors;
    using PushCore;
    using Serilog.Core;
    using SqlTicketsConnector.Tests.TestSupport;
    using Xunit;

    public class OtlpExporterTests
    {
        private const string ConnectionId = "otlpexport";

        [Fact]
        public async Task A_run_delivers_its_traces_and_metrics_before_the_process_exits()
        {
            using var collector = new FakeCollector();

            var options = new OtlpOptions
            {
                Enabled = true,
                Endpoint = collector.BaseAddress,
                Protocol = "HttpProtobuf",
                TimeoutSeconds = 10,
                ServiceName = "PushTests",
            };

            PushTelemetryExporter exporter = PushTelemetryExporter.Create(options, "PushTests", Logger.None);

            if (!exporter.IsExporting)
            {
                // The default build, which excludes the packages on purpose. The
                // contract there is that asking for the exporter is a warning and
                // a working crawl, never a failure and never a silent pretence.
                exporter.Dispose();

                Assert.Empty(collector.Paths);
                return;
            }

            try
            {
                await RunAsync();
            }
            finally
            {
                // The flush. Everything this test asserts happens here, which is
                // why PushHost calls it explicitly in its finally rather than
                // trusting the `using` to run early enough.
                exporter.Dispose();
            }

            Assert.True(
                collector.Wait("/v1/traces", TimeSpan.FromSeconds(20)),
                "No trace export reached the collector. Paths seen: " + string.Join(", ", collector.Paths));

            Assert.True(
                collector.Wait("/v1/metrics", TimeSpan.FromSeconds(20)),
                "No metric export reached the collector. Paths seen: " + string.Join(", ", collector.Paths));

            // The endpoint configured is the collector's BASE address and the
            // exporter appends the signal path itself. A configuration that had
            // included /v1/traces would show up here as /v1/traces/v1/traces,
            // which is the mistake Otlp:Endpoint validation exists to prevent.
            Assert.DoesNotContain("/v1/traces/v1/", string.Join(" ", collector.Paths));
        }

        [Fact]
        public void A_disabled_section_builds_nothing_and_contacts_nobody()
        {
            using var collector = new FakeCollector();

            using PushTelemetryExporter exporter = PushTelemetryExporter.Create(
                new OtlpOptions { Enabled = false, Endpoint = collector.BaseAddress },
                "PushTests",
                Logger.None);

            Assert.False(exporter.IsExporting);
            Assert.Empty(collector.Paths);
        }

        [Fact]
        public void An_absent_section_is_the_same_as_a_disabled_one()
        {
            // Every appsettings file written before this feature existed.
            using PushTelemetryExporter exporter = PushTelemetryExporter.Create(null, "PushTests", Logger.None);

            Assert.False(exporter.IsExporting);
        }

        [Fact]
        public void An_unreachable_collector_does_not_stop_a_crawl()
        {
            // The run is the product; the telemetry is the commentary. A
            // collector that is down, moved or firewalled must cost a log line,
            // never a failed crawl - and Dispose has to survive it too, because
            // it runs on the way out of every exit path there is.
            using PushTelemetryExporter exporter = PushTelemetryExporter.Create(
                new OtlpOptions
                {
                    Enabled = true,

                    // Port 1 is reserved and nothing listens on it.
                    Endpoint = "http://127.0.0.1:1",
                    Protocol = "HttpProtobuf",
                    TimeoutSeconds = 1,
                },
                "PushTests",
                Logger.None);

            Exception thrown = Record.Exception(() => exporter.Dispose());

            Assert.Null(thrown);
        }

        [Fact]
        public void Disposing_twice_is_harmless()
        {
            // PushHost does exactly this: an explicit call in the run's finally,
            // so the flush precedes Log.CloseAndFlush, plus the `using`
            // declaration that guarantees the paths returning before the run
            // ever starts.
            PushTelemetryExporter exporter = PushTelemetryExporter.Create(null, "PushTests", Logger.None);

            exporter.Dispose();

            Assert.Null(Record.Exception(() => exporter.Dispose()));
        }

        private static async Task RunAsync()
        {
            var adapter = new StubGraphAdapter(
                new ExternalConnection { Id = ConnectionId, State = ConnectionState.Ready },
                new SqlHierarchyPush.HierarchyPushConnector().BuildSchema());

            PushOptions options = TestData.ValidPushOptions(ConnectionId);
            options.Settings["Batch"] = "false";
            options.Settings["Writers"] = "1";

            var source = new FakePushSource(new[]
            {
                new PushItem { Id = "a1", ItemType = "file", Content = "one" },
                new PushItem { Id = "a2", ItemType = "file", Content = "two" },
            });

            var engine = new PushEngine(
                new FakePushConnector(source),
                options,
                new Microsoft.Graph.GraphServiceClient(adapter),
                Logger.None,
                dryRun: false);

            await engine.RunAsync(new PushSourceContext(
                options,
                new Azure.Identity.DefaultAzureCredential(),
                secrets: null,
                Logger.None));
        }

        /// <summary>A socket that answers 200 and remembers what was posted to it.</summary>
        /// <remarks>
        /// A raw TcpListener rather than HttpListener, and not for elegance:
        /// HttpListener needs a URL reservation to bind as a non-administrator on
        /// Windows, so a test built on it passes on a developer's elevated shell
        /// and fails on a build agent. Reading the request line off the socket
        /// needs no privilege at all, and the request line is the whole
        /// assertion.
        /// </remarks>
        private sealed class FakeCollector : IDisposable
        {
            private readonly TcpListener listener;
            private readonly CancellationTokenSource stopping = new CancellationTokenSource();
            private readonly ConcurrentQueue<string> paths = new ConcurrentQueue<string>();
            private readonly ManualResetEventSlim arrived = new ManualResetEventSlim(false);

            public FakeCollector()
            {
                // Port 0: the OS picks a free one, so two of these running at
                // once - which the suite's cross-class parallelism makes likely -
                // cannot collide on a hard-coded number.
                this.listener = new TcpListener(IPAddress.Loopback, 0);
                this.listener.Start();

                this.BaseAddress = "http://127.0.0.1:" +
                    ((IPEndPoint)this.listener.LocalEndpoint).Port.ToString(
                        System.Globalization.CultureInfo.InvariantCulture);

                _ = Task.Run(this.AcceptAsync);
            }

            public string BaseAddress { get; }

            public IReadOnlyCollection<string> Paths => this.paths;

            /// <summary>Waits for a request to the given path.</summary>
            public bool Wait(string path, TimeSpan timeout)
            {
                DateTime deadline = DateTime.UtcNow + timeout;

                while (DateTime.UtcNow < deadline)
                {
                    foreach (string seen in this.paths)
                    {
                        if (seen == path)
                        {
                            return true;
                        }
                    }

                    // Signalled by each arrival, so this returns as soon as one
                    // lands rather than on a polling interval.
                    this.arrived.Wait(TimeSpan.FromMilliseconds(250));
                    this.arrived.Reset();
                }

                return false;
            }

            public void Dispose()
            {
                this.stopping.Cancel();
                this.listener.Stop();
                this.stopping.Dispose();
                this.arrived.Dispose();
            }

            private async Task AcceptAsync()
            {
                while (!this.stopping.IsCancellationRequested)
                {
                    TcpClient client;

                    try
                    {
                        client = await this.listener.AcceptTcpClientAsync(this.stopping.Token);
                    }
                    catch (Exception)
                    {
                        // Stopped, or the socket was torn down under us. Either
                        // way there is nothing left to accept.
                        return;
                    }

                    _ = Task.Run(() => this.ServeAsync(client));
                }
            }

            private async Task ServeAsync(TcpClient client)
            {
                try
                {
                    using (client)
                    {
                        NetworkStream stream = client.GetStream();
                        var buffer = new byte[8192];
                        var head = new StringBuilder();
                        int read;

                        // Only the head is needed: the request line names the
                        // signal, and the body is a protobuf this test has no
                        // reason to decode.
                        while ((read = await stream.ReadAsync(buffer, this.stopping.Token)) > 0)
                        {
                            head.Append(Encoding.ASCII.GetString(buffer, 0, read));

                            if (head.ToString().Contains("\r\n\r\n", StringComparison.Ordinal))
                            {
                                break;
                            }
                        }

                        string requestLine = head.ToString().Split('\r')[0];
                        string[] parts = requestLine.Split(' ');

                        if (parts.Length >= 2)
                        {
                            this.paths.Enqueue(parts[1]);
                            this.arrived.Set();
                        }

                        byte[] response = Encoding.ASCII.GetBytes(
                            "HTTP/1.1 200 OK\r\nContent-Type: application/x-protobuf\r\nContent-Length: 0\r\n\r\n");

                        await stream.WriteAsync(response, this.stopping.Token);
                        await stream.FlushAsync(this.stopping.Token);
                    }
                }
                catch (Exception)
                {
                    // A client that hung up early has still told this collector
                    // everything it was asked to record.
                }
            }
        }
    }
}
