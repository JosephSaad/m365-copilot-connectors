// ---------------------------------------------------------------------------
// PushTelemetry.cs
// The spans and instruments a crawl emits, and nothing about where they go.
//
// WHY THIS EXISTS, GIVEN THE REPOSITORY ALREADY HAD "OTLP". It had an OTLP
// LOGS sink - Serilog.Sinks.OpenTelemetry, writing log records to /v1/logs -
// and it lived in SqlTicketsConnector, the agent-hosted service. The push
// executables never touch that logger. So the processes that actually have
// runs had no telemetry of any kind, and the roadmap's claim that wiring the
// existing exporter "buys per-run traces with no new code" was wrong twice
// over: wrong about traces versus logs, and wrong about which executable.
//
// NO PACKAGE REFERENCE, AND THAT IS MEASURED RATHER THAN HOPED.
// System.Diagnostics.DiagnosticSource ships inside the shared framework on both
// target frameworks this repository builds:
//
//   net10.0  C:\Program Files\dotnet\shared\Microsoft.NETCore.App\10.0.11\
//   net9.0   C:\Program Files\dotnet\shared\Microsoft.NETCore.App\9.0.19\
//
// Both were verified by compiling and running ActivitySource, Meter, Counter,
// Histogram, ActivityListener and MeterListener in a project with no
// PackageReference at all. That matters more than it looks: it means
// instrumentation changes neither the default dependency graph nor the offline
// restore list that build\Get-OfflinePackages.ps1 pins, so an air-gapped
// rebuild is unaffected. Only the EXPORTER needs packages, and it stays behind
// -p:EnableOtlpExporter=true where the second gRPC stack already lives.
//
// IT COSTS NOTHING WHEN NOBODY IS LISTENING, and that is a property of the
// runtime rather than a claim of ours. StartActivity returns null when no
// ActivityListener has subscribed to this source, so the using statement
// disposes a null and the tag calls are elided by the null-conditional.
// Counter.Add and Histogram.Record short-circuit when no MeterListener has
// enabled the instrument. A deployment that exports nothing pays for a handful
// of null checks per run, not per item.
//
// WHY COUNTERS ARE ADDED ONCE AT THE END OF A RUN rather than incremented per
// item. A crawl is a batch, its totals are already accumulated in PushSummary
// under Volatile reads because several writers touch them, and adding the
// run's total once is the same monotonic series as adding one at a time while
// costing one call instead of a hundred thousand. The per-item view is the
// span tree; the counter is the rate.
//
// NAMES FOLLOW OPENTELEMETRY SEMANTIC CONVENTION SHAPE: lower case, dotted,
// units in the name where the unit is not obvious. They are part of the
// contract with whatever dashboard is built on them, so changing one is a
// breaking change to somebody's alert.
// ---------------------------------------------------------------------------

namespace PushCore;

using System.Diagnostics;
using System.Diagnostics.Metrics;

/// <summary>The activity source and instruments every crawl reports through.</summary>
public static class PushTelemetry
{
    /// <summary>
    /// The name a listener subscribes to, for both the activity source and the meter.
    /// </summary>
    /// <remarks>
    /// One name for both so that an operator configuring an exporter has one
    /// string to get right rather than two. It is public because a test, and an
    /// exporter, both need to name it.
    /// </remarks>
    public const string Name = "M365.Connectors.Push";

    /// <summary>Version reported with the source, for the resource attributes.</summary>
    /// <remarks>
    /// The release this instrumentation ships in, and it reaches a collector as
    /// the service version on every span and every measurement. Bump it with the
    /// release tag: a dashboard that cannot tell which build produced a series
    /// cannot tell an instrumentation change from a behaviour change.
    /// </remarks>
    public const string Version = "1.7.1";

    /// <summary>Spans: one per run, with a child per phase.</summary>
    public static readonly ActivitySource Source = new(Name, Version);

    private static readonly Meter Instruments = new(Name, Version);

    /// <summary>Items written to Graph, whether created or updated.</summary>
    public static readonly Counter<long> ItemsWritten =
        Instruments.CreateCounter<long>("crawl.items.written", "item", "Items written to the index.");

    /// <summary>Items the store proved unchanged, so no write was attempted.</summary>
    public static readonly Counter<long> ItemsUnchanged =
        Instruments.CreateCounter<long>("crawl.items.unchanged", "item", "Items skipped as unchanged since the last run.");

    /// <summary>Items removed by the delete sweep.</summary>
    public static readonly Counter<long> ItemsDeleted =
        Instruments.CreateCounter<long>("crawl.items.deleted", "item", "Items removed from the index by the sweep.");

    /// <summary>Items the service refused after every retry.</summary>
    public static readonly Counter<long> ItemsFailed =
        Instruments.CreateCounter<long>("crawl.items.failed", "item", "Items the service refused after the retry budget was spent.");

    /// <summary>
    /// Items deliberately not written: no grants, a duplicate identifier, or a
    /// sensitivity policy that declined them.
    /// </summary>
    /// <remarks>
    /// Separate from <see cref="ItemsFailed"/> on purpose. A skip is a decision
    /// and a failure is an accident, and an alert that cannot tell them apart
    /// fires on a correctly configured connector.
    /// </remarks>
    public static readonly Counter<long> ItemsSkipped =
        Instruments.CreateCounter<long>("crawl.items.skipped", "item", "Items deliberately not written.");

    /// <summary>Items a sensitivity policy declined to index.</summary>
    /// <remarks>
    /// A subset of <see cref="ItemsSkipped"/>, counted separately because it is
    /// the one skip reason that is a security control rather than a data quality
    /// observation, and somebody will be asked to evidence it.
    /// </remarks>
    public static readonly Counter<long> ItemsRefusedByLabel =
        Instruments.CreateCounter<long>("crawl.items.refused_by_label", "item", "Items a sensitivity policy declined to index.");

    /// <summary>Rows the source returned twice in one run.</summary>
    public static readonly Counter<long> ItemsDuplicate =
        Instruments.CreateCounter<long>("crawl.items.duplicate", "item", "Rows returned more than once by the source in one run.");

    /// <summary>Content bytes written, after truncation.</summary>
    public static readonly Counter<long> BytesWritten =
        Instruments.CreateCounter<long>("crawl.bytes.written", "By", "Content bytes written, measured after truncation.");

    /// <summary>Times the crawl waited because the service asked it to.</summary>
    public static readonly Counter<long> ThrottleWaits =
        Instruments.CreateCounter<long>("crawl.throttle.waits", "{wait}", "Occasions the crawl backed off at the service's request.");

    /// <summary>Items whose content was cut to fit the platform ceiling.</summary>
    public static readonly Counter<long> ItemsTruncated =
        Instruments.CreateCounter<long>("crawl.items.truncated", "item", "Items whose content was trimmed to the configured ceiling.");

    /// <summary>How long a whole crawl took, end to end.</summary>
    public static readonly Histogram<double> RunDuration =
        Instruments.CreateHistogram<double>("crawl.run.duration", "s", "Wall clock duration of one crawl run.");

    /// <summary>
    /// Starts the span covering a whole run, or returns null when nobody is
    /// listening.
    /// </summary>
    /// <param name="connectorKey">The connector, for example sqltickets.</param>
    /// <param name="connectionId">The external connection being written to.</param>
    /// <param name="dryRun">Whether this run writes anything.</param>
    /// <returns>The activity, or null when no listener has subscribed.</returns>
    /// <remarks>
    /// The run identifier is not a parameter because it does not exist yet: the
    /// run is opened part way into RunAsync, after the ownership check. It is
    /// attached with <see cref="SetRun"/> once known, which is also why a crawl
    /// with no state store still produces a well formed span.
    /// </remarks>
    public static Activity? StartRun(string connectorKey, string connectionId, bool dryRun)
    {
        Activity? activity = Source.StartActivity("crawl.run", ActivityKind.Internal);

        activity?.SetTag("connector.key", connectorKey);
        activity?.SetTag("connector.connection_id", connectionId);
        activity?.SetTag("crawl.dry_run", dryRun);

        return activity;
    }

    /// <summary>Starts a child span for one phase of a run.</summary>
    /// <param name="phase">The phase name, appended to "crawl.".</param>
    /// <returns>The activity, or null when no listener has subscribed.</returns>
    public static Activity? StartPhase(string phase) =>
        Source.StartActivity("crawl." + phase, ActivityKind.Internal);

    /// <summary>Attaches the run identifier and mode once the run has been opened.</summary>
    /// <param name="activity">The run span, which may be null.</param>
    /// <param name="runId">The store's run identifier, or 0 when there is no store.</param>
    /// <param name="mode">Full or incremental.</param>
    /// <param name="stateStore">Whether a crawl state store is attached.</param>
    public static void SetRun(Activity? activity, long runId, string mode, bool stateStore)
    {
        if (activity is null)
        {
            return;
        }

        // Reported as absent rather than as zero. A run identifier of 0 means
        // there is no store to have issued one, and a dashboard that renders it
        // as run zero invites somebody to go looking for that run.
        activity.SetTag("crawl.run_id", stateStore ? runId : null);
        activity.SetTag("crawl.mode", mode);
        activity.SetTag("crawl.state_store", stateStore);
    }

    /// <summary>Records a completed run's totals against the instruments.</summary>
    /// <param name="summary">The run's counters, final by the time this is called.</param>
    /// <param name="connectorKey">The connector, as a dimension on every measurement.</param>
    /// <param name="connectionId">The connection, as a dimension on every measurement.</param>
    /// <param name="seconds">Wall clock duration of the run.</param>
    /// <remarks>
    /// Every measurement carries the same two dimensions so that a host running
    /// several connectors produces series that can be told apart. Cardinality is
    /// bounded by the number of connections on the host, which is small, and
    /// deliberately does NOT include the run identifier: that is unbounded and
    /// is what the span tree is for.
    /// </remarks>
    public static void RecordRun(PushSummary summary, string connectorKey, string connectionId, double seconds)
    {
        ArgumentNullException.ThrowIfNull(summary);

        var tags = new TagList
        {
            { "connector.key", connectorKey },
            { "connector.connection_id", connectionId },
        };

        ItemsWritten.Add(summary.Total, tags);
        ItemsUnchanged.Add(summary.Unchanged, tags);
        ItemsDeleted.Add(summary.Deleted, tags);
        ItemsFailed.Add(summary.Failed, tags);
        ItemsSkipped.Add(summary.Skipped, tags);

        // A subset of Skipped rather than a number beside it, exactly as
        // PushSummary counts it. A dashboard that adds the two would double
        // count; one that plots refusals against skips reads the ratio, which is
        // the question worth asking of a policy nobody has tuned yet.
        ItemsRefusedByLabel.Add(summary.RefusedByLabel, tags);

        ItemsDuplicate.Add(summary.Duplicates, tags);
        ItemsTruncated.Add(summary.Truncated, tags);
        BytesWritten.Add(summary.BytesWritten, tags);
        ThrottleWaits.Add(summary.ThrottleWaits, tags);
        RunDuration.Record(seconds, tags);
    }

    /// <summary>Marks a run span as failed, with the exception type but not its message.</summary>
    /// <param name="activity">The run span, which may be null.</param>
    /// <param name="error">What went wrong.</param>
    /// <remarks>
    /// THE MESSAGE IS DELIBERATELY NOT RECORDED. A span reaches a monitoring
    /// platform that is read far more widely than the source database, and an
    /// exception carrying a row's content would undo the whole redaction policy
    /// in one line. The type locates the fault; the log file, which is redacted,
    /// carries the detail.
    /// </remarks>
    public static void SetFailed(Activity? activity, Exception error)
    {
        ArgumentNullException.ThrowIfNull(error);

        activity?.SetStatus(ActivityStatusCode.Error, error.GetType().Name);
        activity?.SetTag("error.type", error.GetType().FullName);
    }
}
