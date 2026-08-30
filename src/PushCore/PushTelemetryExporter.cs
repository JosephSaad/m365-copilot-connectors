// ---------------------------------------------------------------------------
// PushTelemetryExporter.cs
// Ships the spans and instruments PushTelemetry emits to an OTLP collector, and
// is the only part of telemetry that costs a package.
//
// THE SPLIT THIS FILE EXISTS TO KEEP. PushTelemetry has no PackageReference:
// ActivitySource, Meter, Counter and Histogram are in the shared framework on
// both target frameworks, so instrumenting the engine changed neither the
// dependency graph nor the offline restore list. Everything that does cost a
// package is here, behind -p:EnableOtlpExporter=true, exactly where the log
// exporter already lives. A build without the flag compiles the #else below and
// has no OpenTelemetry assemblies at all.
//
// THE DEFINE HAS TO BE DECLARED IN THIS PROJECT. OTLP_EXPORTER is set by
// SqlTicketsConnector.csproj for SqlTicketsConnector, and compile constants do
// not cross project boundaries - #if OTLP_EXPORTER written here without the
// matching DefineConstants in PushCore.csproj would silently compile the
// disabled branch and nobody would see a warning. See PushCore.csproj.
//
// WHAT THE FLAG ACTUALLY COSTS, MEASURED RATHER THAN ASSUMED. OpenTelemetry
// 1.18.0's OTLP exporter carries NO gRPC stack: it speaks both OTLP protocols
// over HttpClient, so Grpc.Net.Client, Grpc.Core.Api and Google.Protobuf are
// absent from the restored graph. That matters here specifically, because
// Directory.Packages.props pins Google.Protobuf at two versions either side of
// this same flag to satisfy the log sink, and a second package wanting a third
// version would have been an NU1605 with no obvious remedy. It resolves eleven
// packages this repository did not already have, all of them
// Microsoft.Extensions.* or OpenTelemetry.*.
//
// DELTA TEMPORALITY, AND IT IS NOT A PREFERENCE. A crawl is a process that
// starts, counts, and exits. Under the OTLP default of cumulative temporality
// every run reports a series that begins at zero and dies with the process, so
// a backend sees a permanent sawtooth and any rate() over it is wrong at every
// process boundary. Delta says "this run wrote 11,900 items", which is both
// what happened and what sums correctly across runs.
//
// FLUSH BEFORE EXIT, EXPLICITLY. Counters are recorded ONCE, at the end of the
// run, and the periodic metric reader exports on a timer measured in tens of
// seconds. A push tool that returned its exit code without flushing would
// therefore lose the only measurement it ever took, on every run short enough
// to matter. ForceFlush is called on both providers and its result is LOGGED:
// telemetry that silently fails to leave the host is worse than no telemetry,
// because somebody will build an alert on the absence.
// ---------------------------------------------------------------------------

namespace PushCore;

using Connector.Security.Configuration;
using Connector.Security.Secrets;
using Serilog;

#if OTLP_EXPORTER
using OpenTelemetry;
using OpenTelemetry.Exporter;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;
#endif

/// <summary>The "Otlp" section: where traces and metrics are sent, if anywhere.</summary>
public sealed class OtlpOptions
{
    /// <summary>The OTLP/HTTP default port, for the message that explains the two.</summary>
    public const int HttpProtobufPort = 4318;

    /// <summary>The OTLP/gRPC default port, for the same message.</summary>
    public const int GrpcPort = 4317;

    /// <summary>Gets or sets whether to export at all. False, and nothing is built.</summary>
    public bool Enabled { get; set; }

    /// <summary>
    /// Gets or sets the collector's base address, for example
    /// http://otel-collector.internal:4318. Not a signal path: the exporter
    /// appends /v1/traces and /v1/metrics itself.
    /// </summary>
    public string Endpoint { get; set; } = string.Empty;

    /// <summary>Gets or sets HttpProtobuf or Grpc.</summary>
    /// <remarks>
    /// HttpProtobuf by default, which is NOT the OTLP specification's own
    /// default. The reason is local: this estate routes egress through a proxy
    /// (Settings:GraphProxy exists for exactly that), and OTLP/gRPC needs HTTP/2
    /// end to end, which an HTTP/1.1 forward proxy will not carry. A deployment
    /// with a direct route to its collector should say Grpc and use 4317.
    /// </remarks>
    public string Protocol { get; set; } = "HttpProtobuf";

    /// <summary>Gets or sets how long one export attempt may take.</summary>
    public int TimeoutSeconds { get; set; } = 10;

    /// <summary>
    /// Gets or sets the header a hosted collector authenticates with, for
    /// example x-api-key. Empty for a collector on the estate's own network.
    /// </summary>
    public string HeaderName { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the Windows Credential Manager target holding that header's
    /// value.
    /// </summary>
    /// <remarks>
    /// A target name, never the value, and the same rule as
    /// Auth:ClientSecretCredentialTarget for the same reason: an API key in an
    /// appsettings file is an API key in source control, in the release package,
    /// and in every support bundle anybody ever sends. Validation rejects a
    /// value that looks like a secret rather than a name.
    /// </remarks>
    public string HeaderCredentialTarget { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the service name reported in the resource. The executable
    /// name when empty.
    /// </summary>
    public string ServiceName { get; set; } = string.Empty;

    /// <summary>Adds a message for every invalid field rather than stopping at the first.</summary>
    /// <param name="errors">Where problems are collected.</param>
    /// <param name="path">The configuration path, for the message.</param>
    public void Validate(ValidationErrors errors, string path)
    {
        ArgumentNullException.ThrowIfNull(errors);

        if (!this.Enabled)
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(this.Endpoint))
        {
            errors.Add(path + ":Endpoint", "must give the collector's base address when Otlp:Enabled is true.");
        }
        else if (!Uri.TryCreate(this.Endpoint, UriKind.Absolute, out Uri? uri) ||
            (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            errors.Add(path + ":Endpoint", "must be an absolute http or https URL, for example http://collector:4318.");
        }
        else if (uri.AbsolutePath.Contains("/v1/", StringComparison.OrdinalIgnoreCase))
        {
            // A specific mistake rather than a general one: the log sink already
            // in this repository IS configured with a /v1/logs path, so an
            // operator copying that pattern here lands on /v1/traces/v1/traces
            // and gets a 404 per export with nothing in the log to explain it.
            errors.Add(
                path + ":Endpoint",
                "must be the collector's BASE address, not a signal path. The exporter appends /v1/traces and " +
                "/v1/metrics itself, so a value ending in /v1/logs or /v1/traces produces a doubled path.");
        }

        errors.RequireOneOf(path + ":Protocol", this.Protocol, "HttpProtobuf", "Grpc");
        errors.RequireRange(path + ":TimeoutSeconds", this.TimeoutSeconds, 1, 300);

        bool named = !string.IsNullOrWhiteSpace(this.HeaderName);
        bool targeted = !string.IsNullOrWhiteSpace(this.HeaderCredentialTarget);

        if (named != targeted)
        {
            errors.Add(
                path + ":HeaderName",
                "and Otlp:HeaderCredentialTarget are set together or not at all. One without the other is " +
                "either a header with no value or a credential nothing reads.");
        }

        if (targeted && LooksLikeASecret(this.HeaderCredentialTarget))
        {
            errors.Add(
                path + ":HeaderCredentialTarget",
                "must name a Windows Credential Manager target, not the value itself. This value looks like a " +
                "key; store it with cmdkey and put the target name here.");
        }
    }

    /// <summary>Reads the header value, or null when no header is configured.</summary>
    /// <param name="log">Where to report a credential that is missing.</param>
    /// <returns>The value, or null.</returns>
    /// <remarks>
    /// A missing credential is a Warning and an unauthenticated exporter rather
    /// than a refusal to run. Telemetry is an observation of the crawl, not part
    /// of it, and refusing to crawl because the monitoring platform's key was
    /// not rotated would make observability an availability risk.
    /// </remarks>
    internal string? ReadHeaderValue(ILogger log)
    {
        if (string.IsNullOrWhiteSpace(this.HeaderCredentialTarget))
        {
            return null;
        }

        // Credential Manager is a Windows facility, and the alternative to
        // testing for it is a PlatformNotSupportedException out of the P/Invoke
        // with nothing an operator can act on. Unlike the Entra credential,
        // which refuses to run without one, this degrades: an unauthenticated
        // exporter still works against a collector on the estate's own network.
        if (!OperatingSystem.IsWindows())
        {
            log.Warning(
                "Otlp:HeaderCredentialTarget is set, but Windows Credential Manager is not available on this " +
                "platform. Traces and metrics will be sent without the {Header} header.",
                this.HeaderName);

            return null;
        }

        try
        {
            return WindowsCredentialStore.Read(this.HeaderCredentialTarget);
        }
        catch (Exception ex)
        {
            log.Warning(
                "Otlp:HeaderCredentialTarget {Target} could not be read ({Reason}). Traces and metrics will be " +
                "sent without the {Header} header and a hosted collector will refuse them.",
                this.HeaderCredentialTarget,
                ex.Message,
                this.HeaderName);

            return null;
        }
    }

    private static bool LooksLikeASecret(string value)
    {
        // Same shape test as AuthOptions applies to a client secret: a credential
        // target is a short name somebody typed, and a key is long and dense.
        return value.Length >= 24 && !value.Contains(' ', StringComparison.Ordinal);
    }
}

/// <summary>
/// Holds the OTLP providers open for the length of a run, and flushes them on
/// the way out.
/// </summary>
public sealed class PushTelemetryExporter : IDisposable
{
    private readonly ILogger log;

    private bool disposed;

#if OTLP_EXPORTER
    // Not readonly: they are built in Start, which is separate from the
    // constructor so that a collector this host cannot reach leaves a usable
    // no-op object rather than throwing out of a constructor.
    private TracerProvider? tracers;
    private MeterProvider? meters;
    private int flushMilliseconds;
#endif

    private PushTelemetryExporter(ILogger log)
    {
        this.log = log;
    }

    /// <summary>Gets a value indicating whether anything is actually being exported.</summary>
    public bool IsExporting { get; private set; }

    /// <summary>Builds the exporter, or a no-op when it is switched off or absent.</summary>
    /// <param name="options">The validated Otlp section, or null.</param>
    /// <param name="serviceInstance">The executable, used as the service name when none is configured.</param>
    /// <param name="log">Where to report what was built.</param>
    /// <returns>A disposable that flushes on the way out. Never null.</returns>
    /// <remarks>
    /// Never throws. A collector that is unreachable, misconfigured or absent
    /// must not stop a crawl: the run is the product and the telemetry is the
    /// commentary. Every failure here is a log line and a disabled exporter.
    /// </remarks>
    public static PushTelemetryExporter Create(OtlpOptions? options, string serviceInstance, ILogger log)
    {
        ArgumentNullException.ThrowIfNull(log);

        var exporter = new PushTelemetryExporter(log);

        if (options is null || !options.Enabled)
        {
            return exporter;
        }

#if !OTLP_EXPORTER
        // The same contract the log sink already keeps: the flag is honoured at
        // runtime only if the build included the packages, and a configuration
        // that asks for what the binary cannot do says so out loud rather than
        // running silently unobserved.
        log.Warning(
            "Otlp:Enabled is true, but this build excludes the OpenTelemetry exporter. Rebuild with " +
            "-p:EnableOtlpExporter=true (or Build.ps1 -EnableOtlpExporter). The crawl runs; nothing is exported.");

        return exporter;
#else
        try
        {
            exporter.Start(options, serviceInstance);
        }
        catch (Exception ex)
        {
            log.Warning(
                "The OTLP exporter could not be started ({Reason}). The crawl continues without telemetry export.",
                ex.Message);
        }

        return exporter;
#endif
    }

    /// <summary>Flushes anything pending and shuts the providers down.</summary>
    /// <remarks>
    /// SAFE TO CALL TWICE, and PushHost does. The `using var` declaration there
    /// guarantees disposal on every exit path including the ones that return
    /// before the run begins; the explicit call in the run's finally is what
    /// puts the flush BEFORE Log.CloseAndFlush, so a warning about telemetry
    /// that did not leave the host still reaches a live sink. Without the guard
    /// the second call would flush a shut-down provider and log a spurious
    /// failure.
    /// </remarks>
    public void Dispose()
    {
        if (this.disposed)
        {
            return;
        }

        this.disposed = true;

#if OTLP_EXPORTER
        if (this.tracers is null && this.meters is null)
        {
            return;
        }

        try
        {
            // Metrics first. The run's counters were recorded seconds ago and
            // the periodic reader has almost certainly not fired, so this is the
            // export that would otherwise be lost; spans are pushed to the batch
            // processor as they end and are the likelier of the two to be
            // partway out already.
            bool metricsOut = this.meters?.ForceFlush(this.flushMilliseconds) ?? true;
            bool tracesOut = this.tracers?.ForceFlush(this.flushMilliseconds) ?? true;

            if (!metricsOut || !tracesOut)
            {
                // Warning, not silence. An alert built on "this run reported no
                // items written" must be able to tell a run that wrote nothing
                // from a run whose telemetry never left the host.
                this.log.Warning(
                    "The OTLP exporter did not finish flushing within {Timeout}ms (metrics flushed: {Metrics}, " +
                    "traces flushed: {Traces}). Some of this run's telemetry was not delivered.",
                    this.flushMilliseconds,
                    metricsOut,
                    tracesOut);
            }
        }
        catch (Exception ex)
        {
            this.log.Warning("The OTLP exporter failed to flush ({Reason}).", ex.Message);
        }
        finally
        {
            this.meters?.Dispose();
            this.tracers?.Dispose();
        }
#endif
    }

#if OTLP_EXPORTER
    /// <summary>Turns the collector's base address into the address for one signal.</summary>
    /// <param name="baseAddress">The collector, as configured.</param>
    /// <param name="protocol">Which OTLP protocol this exporter speaks.</param>
    /// <param name="signal">"traces" or "metrics".</param>
    /// <returns>Where this signal is actually posted.</returns>
    /// <remarks>
    /// FOUND BY A TEST, NOT BY READING THE DOCUMENTATION, and it is the kind of
    /// defect that never announces itself. The SDK appends /v1/traces and
    /// /v1/metrics for you ONLY when the endpoint came from the
    /// OTEL_EXPORTER_OTLP_ENDPOINT environment variable. Set Endpoint
    /// programmatically, as this repository does because its configuration lives
    /// in appsettings, and the SDK treats the value as the final per-signal
    /// address and posts everything to it verbatim. The first version of this
    /// file did exactly that: the integration test caught three POSTs to "/" and
    /// none to either signal path. Against a real collector that is a 404 per
    /// export, retried and then dropped, with a working crawl and no telemetry.
    ///
    /// Only for HTTP. OTLP over gRPC addresses a signal by its service method,
    /// not by a URL path, so appending one there would break the endpoint that
    /// currently works.
    /// </remarks>
    private static Uri SignalEndpoint(Uri baseAddress, OtlpExportProtocol protocol, string signal)
    {
        if (protocol != OtlpExportProtocol.HttpProtobuf)
        {
            return baseAddress;
        }

        string root = baseAddress.AbsoluteUri.TrimEnd('/');

        return new Uri(root + "/v1/" + signal);
    }

    private void Start(OtlpOptions options, string serviceInstance)
    {
        string service = string.IsNullOrWhiteSpace(options.ServiceName) ? serviceInstance : options.ServiceName.Trim();
        var endpoint = new Uri(options.Endpoint);
        OtlpExportProtocol protocol = string.Equals(options.Protocol, "Grpc", StringComparison.OrdinalIgnoreCase)
            ? OtlpExportProtocol.Grpc
            : OtlpExportProtocol.HttpProtobuf;

        string? headerValue = options.ReadHeaderValue(this.log);
        string? headers = headerValue is null ? null : options.HeaderName + "=" + headerValue;
        int timeout = options.TimeoutSeconds * 1000;

        // One resource for both signals, so a trace and the metrics from the
        // same run are attributable to the same process without a join on
        // anything the operator has to know about.
        ResourceBuilder resource = ResourceBuilder.CreateDefault()
            .AddService(
                serviceName: service,
                serviceVersion: PushTelemetry.Version,
                serviceInstanceId: Environment.MachineName + ":" + Environment.ProcessId.ToString(
                    System.Globalization.CultureInfo.InvariantCulture));

        this.tracers = Sdk.CreateTracerProviderBuilder()
            .AddSource(PushTelemetry.Name)
            .SetResourceBuilder(resource)
            .AddOtlpExporter(otlp =>
            {
                otlp.Endpoint = SignalEndpoint(endpoint, protocol, "traces");
                otlp.Protocol = protocol;
                otlp.TimeoutMilliseconds = timeout;

                if (headers is not null)
                {
                    otlp.Headers = headers;
                }
            })
            .Build();

        this.meters = Sdk.CreateMeterProviderBuilder()
            .AddMeter(PushTelemetry.Name)
            .SetResourceBuilder(resource)
            .AddOtlpExporter((otlp, reader) =>
            {
                otlp.Endpoint = SignalEndpoint(endpoint, protocol, "metrics");
                otlp.Protocol = protocol;
                otlp.TimeoutMilliseconds = timeout;

                if (headers is not null)
                {
                    otlp.Headers = headers;
                }

                // See the file header: a batch process under cumulative
                // temporality reports a sawtooth that no rate() reads correctly.
                reader.TemporalityPreference = MetricReaderTemporalityPreference.Delta;
            })
            .Build();

        this.flushMilliseconds = timeout;
        this.IsExporting = true;

        this.log.Information(
            "Exporting traces and metrics for {Source} to {Endpoint} over {Protocol} as service {Service}" +
            "{Authenticated}.",
            PushTelemetry.Name,
            endpoint,
            protocol,
            service,
            headers is null ? string.Empty : ", authenticated with " + options.HeaderName);
    }
#endif
}
