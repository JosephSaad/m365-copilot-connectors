// ---------------------------------------------------------------------------
// PushTelemetryTests.cs
// The spans and instruments a crawl emits, observed the way an exporter
// observes them.
//
// NO EXPORTER IS INVOLVED, AND THAT IS THE POINT. ActivityListener and
// MeterListener are the same subscription mechanism the OpenTelemetry SDK uses;
// listening directly tests what the engine EMITS without needing the packages
// that ship it, so these run in the default build where those packages are
// absent. What is left untested here is the wire format, which is the SDK's
// problem rather than this repository's.
//
// TWO THINGS THAT MAKE THESE TESTS SUBTLER THAN THEY LOOK.
//
// Sample is mandatory. Without it StartActivity returns null, every span
// assertion passes vacuously, and the suite reports that instrumentation works
// while observing nothing at all. That is the failure mode this file is most
// likely to develop, so it is stated here rather than left in a listener
// initialiser.
//
// ActivitySource and Meter are process-global statics, and xunit runs test
// CLASSES in parallel with no collection behaviour configured in this project.
// Five other classes drive PushEngine.RunAsync concurrently with this one, so a
// listener subscribed to PushTelemetry.Name receives their spans and their
// measurements too. Every assertion here is therefore filtered on a connection
// id no other test file uses. Filtering on connector.key would NOT work:
// FakePushConnector.Key is the constant "fake" and four other classes use it.
// ---------------------------------------------------------------------------

namespace SqlTicketsConnector.Tests
{
    using System;
    using System.Collections.Concurrent;
    using System.Collections.Generic;
    using System.Diagnostics;
    using System.Diagnostics.Metrics;
    using System.Linq;
    using System.Threading.Tasks;
    using Microsoft.Graph.Models.ExternalConnectors;
    using PushCore;
    using Serilog.Core;
    using SqlTicketsConnector.Tests.TestSupport;
    using Xunit;

    public class PushTelemetryTests
    {
        // A value no other test file uses. See the file header: this is what
        // separates this class's telemetry from five other classes running at
        // the same moment against the same process-global source.
        private const string ConnectionId = "telemetry";

        [Fact]
        public async Task A_run_emits_one_span_with_a_child_for_each_phase()
        {
            using var listener = new SpanCollector();

            await RunAsync(new FakePushSource(Items(3)));

            Activity run = listener.Single("crawl.run");

            Assert.Equal(ActivityStatusCode.Unset, run.Status);
            Assert.Equal("fake", listener.Tag(run, "connector.key"));
            Assert.Equal(ConnectionId, listener.Tag(run, "connector.connection_id"));
            Assert.Equal("False", listener.Tag(run, "crawl.dry_run"));

            // The phases, and their parentage. A flat list of five spans would
            // satisfy a name check and be useless in a trace viewer.
            foreach (string phase in new[] { "crawl.connection", "crawl.schema", "crawl.items" })
            {
                Activity child = listener.Single(phase);
                Assert.Equal(run.SpanId, child.ParentSpanId);
            }
        }

        [Fact]
        public async Task The_run_span_carries_what_the_run_actually_did()
        {
            using var listener = new SpanCollector();

            await RunAsync(new FakePushSource(Items(3)));

            Activity run = listener.Single("crawl.run");

            Assert.Equal("3", listener.Tag(run, "crawl.items.written"));
            Assert.Equal("0", listener.Tag(run, "crawl.items.failed"));

            // No state store in this fixture, so the run id is reported ABSENT
            // rather than as zero. A dashboard rendering "run 0" invites somebody
            // to go looking for a run that was never issued.
            Assert.Null(listener.Tag(run, "crawl.run_id"));
            Assert.Equal("False", listener.Tag(run, "crawl.state_store"));
        }

        [Fact]
        public async Task A_failed_run_records_the_exception_type_and_never_its_message()
        {
            // THE REDACTION CONTROL, and the reason it is tested rather than
            // trusted: a span reaches a monitoring platform read far more widely
            // than the source database, so an exception carrying a row's content
            // would undo the whole logging policy in one line.
            const string Secret = "customer name and account number";

            using var listener = new SpanCollector();

            var source = new FakePushSource(
                Items(2),
                throwOn: item => item.Id == "a2" ? new InvalidOperationException(Secret) : null);

            await Assert.ThrowsAnyAsync<Exception>(() => RunAsync(source));

            Activity run = listener.Single("crawl.run");

            Assert.Equal(ActivityStatusCode.Error, run.Status);
            Assert.Contains("InvalidOperationException", listener.Tag(run, "error.type"));

            foreach (KeyValuePair<string, object> tag in run.TagObjects)
            {
                Assert.DoesNotContain(Secret, tag.Value?.ToString() ?? string.Empty);
            }

            Assert.DoesNotContain(Secret, run.StatusDescription ?? string.Empty);
        }

        [Fact]
        public async Task Counters_are_added_once_per_run_and_carry_both_dimensions()
        {
            using var listener = new MeasurementCollector(ConnectionId);

            await RunAsync(new FakePushSource(Items(4)));

            // Once per RUN, not once per item: the run's totals are already
            // accumulated in PushSummary, and adding them once is the same
            // monotonic series for one call instead of a hundred thousand.
            Assert.Equal(4, listener.Sum("crawl.items.written"));
            Assert.Equal(0, listener.Sum("crawl.items.failed"));
            Assert.Equal(1, listener.Count("crawl.items.written"));

            // Bounded cardinality, and deliberately no run id: that is unbounded,
            // and the span tree is what answers per-run questions.
            Assert.Equal("fake", listener.TagValue("crawl.items.written", "connector.key"));
        }

        [Fact]
        public async Task The_run_duration_is_recorded_in_seconds()
        {
            using var listener = new MeasurementCollector(ConnectionId);

            await RunAsync(new FakePushSource(Items(1)));

            double seconds = Assert.Single(listener.Doubles("crawl.run.duration"));

            // Seconds, not milliseconds or microseconds. The instrument declares
            // its unit as "s" and a backend renders it accordingly, so a value
            // off by a thousand is a dashboard that is wrong rather than empty.
            Assert.InRange(seconds, 0, 60);
        }

        [Fact]
        public async Task A_refused_item_reaches_its_own_counter_and_the_skip_counter()
        {
            using var listener = new MeasurementCollector(ConnectionId);

            var item = new PushItem
            {
                Id = "a1",
                ItemType = "file",
                Content = "x",
                Classifications = new[] { "PCI" },
            };

            await RunAsync(new FakePushSource(new[] { item }), Refusing());

            // A SUBSET of skipped, not a number beside it. A dashboard that added
            // the two would double count; one that plots the ratio reads how much
            // of the corpus the policy is holding back.
            Assert.Equal(1, listener.Sum("crawl.items.refused_by_label"));
            Assert.Equal(1, listener.Sum("crawl.items.skipped"));
            Assert.Equal(0, listener.Sum("crawl.items.written"));
        }

        [Fact]
        public async Task Instrumentation_costs_nothing_when_nobody_is_listening()
        {
            // No listener at all. StartActivity returns null, Counter.Add
            // short-circuits, and the run must be indistinguishable from one
            // built before any of this existed.
            Assert.Null(Activity.Current);

            StubGraphAdapter adapter = await RunAsync(new FakePushSource(Items(2)));

            Assert.Equal(2, adapter.WrittenItemIds.Count);

            // And nothing was left dangling on the ambient context for the next
            // test in this process to inherit.
            Assert.Null(Activity.Current);
        }

        // --- fixtures ------------------------------------------------------

        private static SensitivityOptions Refusing()
        {
            return new SensitivityOptions
            {
                Mode = nameof(SensitivityMode.Enforce),
                Unmapped = nameof(SensitivityAction.Allow),
                Unlabelled = nameof(SensitivityAction.Allow),
                Labels = new List<SensitivityLabelOptions>
                {
                    new SensitivityLabelOptions
                    {
                        Name = "Restricted",
                        Classifications = new List<string> { "PCI" },
                        Index = false,
                    },
                },
            };
        }

        private static IReadOnlyList<PushItem> Items(int count)
        {
            return Enumerable.Range(1, count)
                .Select(n => new PushItem { Id = "a" + n, ItemType = "file", Content = "content " + n })
                .ToList();
        }

        private static async Task<StubGraphAdapter> RunAsync(
            FakePushSource source, SensitivityOptions sensitivity = null)
        {
            var adapter = new StubGraphAdapter(
                new ExternalConnection { Id = ConnectionId, State = ConnectionState.Ready },
                new SqlHierarchyPush.HierarchyPushConnector().BuildSchema());

            PushOptions options = TestData.ValidPushOptions(ConnectionId);
            options.Settings["Batch"] = "false";
            options.Settings["Writers"] = "1";

            if (sensitivity != null)
            {
                options.Sensitivity = sensitivity;
            }

            var engine = new PushEngine(
                new FakePushConnector(source),
                options,
                new Microsoft.Graph.GraphServiceClient(adapter),
                Logger.None,
                dryRun: false);

            var context = new PushSourceContext(
                options,
                new Azure.Identity.DefaultAzureCredential(),
                secrets: null,
                Logger.None);

            await engine.RunAsync(context);

            return adapter;
        }

        /// <summary>Collects this connection's completed spans.</summary>
        private sealed class SpanCollector : IDisposable
        {
            private readonly ActivityListener listener;
            private readonly ConcurrentBag<Activity> stopped = new ConcurrentBag<Activity>();

            public SpanCollector()
            {
                this.listener = new ActivityListener
                {
                    ShouldListenTo = source => source.Name == PushTelemetry.Name,

                    // MANDATORY. Without it StartActivity returns null and every
                    // assertion below passes while observing nothing.
                    Sample = (ref ActivityCreationOptions<ActivityContext> _) =>
                        ActivitySamplingResult.AllDataAndRecorded,

                    ActivityStopped = activity => this.stopped.Add(activity),
                };

                ActivitySource.AddActivityListener(this.listener);
            }

            /// <summary>The one span of this name belonging to this connection.</summary>
            /// <remarks>
            /// A phase span carries no connection tag of its own, so it is
            /// matched by parentage instead: its root is the crawl.run span whose
            /// connection id is ours. Matching on name alone would pick up the
            /// concurrently running classes described in the file header.
            /// </remarks>
            public Activity Single(string name)
            {
                Activity[] mine = this.stopped
                    .Where(activity => activity.OperationName == name && this.BelongsToUs(activity))
                    .ToArray();

                return Assert.Single(mine);
            }

            /// <summary>Reads one tag, whatever type it was set as.</summary>
            /// <remarks>
            /// TagObjects, not Tags. Activity.Tags silently returns only the
            /// tags whose values happen to be strings, so a bool or an int set
            /// with SetTag is simply absent from it - which reads exactly like a
            /// tag the code forgot to set. TagObjects is also what an exporter
            /// serializes, so this observes what a collector would receive.
            /// </remarks>
            public string Tag(Activity activity, string name)
            {
                foreach (KeyValuePair<string, object> tag in activity.TagObjects)
                {
                    if (tag.Key == name)
                    {
                        return tag.Value?.ToString();
                    }
                }

                return null;
            }

            public void Dispose()
            {
                this.listener.Dispose();
            }

            private bool BelongsToUs(Activity activity)
            {
                for (Activity walk = activity; walk != null; walk = walk.Parent)
                {
                    if (this.Tag(walk, "connector.connection_id") == ConnectionId)
                    {
                        return true;
                    }
                }

                return false;
            }
        }

        /// <summary>Collects this connection's measurements.</summary>
        private sealed class MeasurementCollector : IDisposable
        {
            private readonly MeterListener listener;
            private readonly ConcurrentBag<(string Instrument, double Value, KeyValuePair<string, object>[] Tags)> taken =
                new ConcurrentBag<(string, double, KeyValuePair<string, object>[])>();

            private readonly string connectionId;

            public MeasurementCollector(string connectionId)
            {
                this.connectionId = connectionId;

                this.listener = new MeterListener
                {
                    InstrumentPublished = (instrument, meterListener) =>
                    {
                        // The Meter field is private on PushTelemetry, which is
                        // right - nothing outside it should create instruments -
                        // and reachable through any instrument it published.
                        if (instrument.Meter.Name == PushTelemetry.Name)
                        {
                            meterListener.EnableMeasurementEvents(instrument);
                        }
                    },
                };

                this.listener.SetMeasurementEventCallback<long>(
                    (instrument, measurement, tags, _) => this.Record(instrument.Name, measurement, tags));

                this.listener.SetMeasurementEventCallback<double>(
                    (instrument, measurement, tags, _) => this.Record(instrument.Name, measurement, tags));

                this.listener.Start();
            }

            public double Sum(string instrument)
            {
                return this.Mine(instrument).Sum(m => m.Value);
            }

            public int Count(string instrument)
            {
                return this.Mine(instrument).Count();
            }

            public IEnumerable<double> Doubles(string instrument)
            {
                return this.Mine(instrument).Select(m => m.Value);
            }

            public string TagValue(string instrument, string tag)
            {
                return this.Mine(instrument)
                    .SelectMany(m => m.Tags)
                    .Where(t => t.Key == tag)
                    .Select(t => t.Value?.ToString())
                    .FirstOrDefault();
            }

            public void Dispose()
            {
                this.listener.Dispose();
            }

            private void Record(string instrument, double value, ReadOnlySpan<KeyValuePair<string, object>> tags)
            {
                this.taken.Add((instrument, value, tags.ToArray()));
            }

            private IEnumerable<(string Instrument, double Value, KeyValuePair<string, object>[] Tags)> Mine(
                string instrument)
            {
                return this.taken.Where(m =>
                    m.Instrument == instrument &&
                    m.Tags.Any(t => t.Key == "connector.connection_id" &&
                        string.Equals(t.Value?.ToString(), this.connectionId, StringComparison.Ordinal)));
            }
        }
    }
}
