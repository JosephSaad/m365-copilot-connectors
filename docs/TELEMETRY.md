# Telemetry: traces and metrics from a crawl

Every push run emits an OpenTelemetry trace and a set of metrics. This document
says what they are, how to send them somewhere, and what they cost when you do
not.

---

## The short version

| | |
|---|---|
| Instrumentation | Always compiled in. No package, no flag, no configuration |
| Cost when nobody listens | A handful of null checks per **run**, not per item |
| Export | Behind `-p:EnableOtlpExporter=true` at build time **and** `Otlp:Enabled` at run time |
| Source and meter name | `M365.Connectors.Push` |
| Protocol | OTLP over HTTP/protobuf by default, gRPC available |

---

## What was already here, and what was not

The repository has had an OTLP exporter since v1.3. It is
`Serilog.Sinks.OpenTelemetry`, it carries **log records**, and it lives in
`SqlTicketsConnector` — the agent-hosted service. The push executables never
touch that logger.

So the processes that actually have *runs* — `SqlGraphPush`,
`SqlHierarchyPush`, `CdpGraphPush` — had no telemetry of any kind. The roadmap
entry claiming that wiring the existing exporter "buys per-run traces with no
new code" was wrong twice over: wrong about traces versus logs, and wrong about
which executable.

Both exporters now share the one build flag. They are otherwise unrelated: one
sends logs from the agent-hosted connector, the other sends traces and metrics
from every push tool.

---

## Instrumentation costs nothing, and that is measured

`ActivitySource`, `Meter`, `Counter<T>` and `Histogram<T>` come from
`System.Diagnostics.DiagnosticSource`, which ships **inside the shared
framework** on both target frameworks this repository builds:

```
net10.0   C:\Program Files\dotnet\shared\Microsoft.NETCore.App\10.0.11\
net9.0    C:\Program Files\dotnet\shared\Microsoft.NETCore.App\9.0.19\
```

Verified by compiling and running all six types in a project with no
`PackageReference` at all. That is what makes the instrumentation unconditional:
it changes neither the default dependency graph nor the offline restore list
that `build/Get-OfflinePackages.ps1` pins, so an air-gapped rebuild is
unaffected.

The runtime does the rest. `StartActivity` returns `null` when no
`ActivityListener` has subscribed, so the `using` disposes a null and every tag
call is elided by the null-conditional. `Counter.Add` and `Histogram.Record`
short-circuit when no `MeterListener` has enabled the instrument.

---

## The trace

One span per run, with a child per phase.

```
crawl.run                       connector.key, connector.connection_id, crawl.dry_run
├── crawl.connection            create or verify the external connection
├── crawl.schema                register the schema and wait for Ready
└── crawl.items                 read, prepare, write
```

`crawl.run` gains these once the run has been opened and again once it closes:

| Tag | Note |
|---|---|
| `crawl.run_id` | The store's run identifier. **Absent, not zero**, when there is no state store — a dashboard rendering "run 0" invites somebody to go looking for a run nobody issued |
| `crawl.mode` | `Full` or `Incremental` |
| `crawl.state_store` | Whether durable crawl memory is attached |
| `crawl.items.written` / `.failed` / `.skipped` / `.deleted` | The run's totals |
| `crawl.sensitivity.mode` | Only when a sensitivity policy is configured. See [SENSITIVITY-LABELS.md](SENSITIVITY-LABELS.md) |
| `crawl.items.refused_by_label` | Same condition |
| `error.type` | On failure. **The exception TYPE only** |

### The exception message is deliberately not recorded

A span reaches a monitoring platform that is read far more widely than the
source database. An exception carrying a row's content would undo the whole
redaction policy in one line, and it would do it in the one place nobody thinks
to audit.

The type locates the fault. The log file, which is redacted, carries the detail.
`PushTelemetryTests` asserts this directly: it fails a run with a secret in the
exception message and proves the string appears in no tag and no status
description.

---

## The metrics

Ten instruments, all on meter `M365.Connectors.Push`.

| Instrument | Unit | What it counts |
|---|---|---|
| `crawl.items.written` | item | Items written to the index |
| `crawl.items.unchanged` | item | Items the state store proved unchanged |
| `crawl.items.deleted` | item | Items the sweep removed |
| `crawl.items.failed` | item | Items the service refused after the retry budget |
| `crawl.items.skipped` | item | Items deliberately not written, for any reason |
| `crawl.items.refused_by_label` | item | **A subset of skipped**, not a number beside it |
| `crawl.items.duplicate` | item | Rows the source returned twice in one run |
| `crawl.items.truncated` | item | Items whose content was cut to the ceiling |
| `crawl.bytes.written` | By | Content bytes, measured after truncation |
| `crawl.throttle.waits` | {wait} | Occasions the crawl backed off at the service's request |
| `crawl.run.duration` | s | Wall clock for the whole run |

Every measurement carries `connector.key` and `connector.connection_id`, so a
host running several connectors produces series that can be told apart.
Cardinality is bounded by the number of connections on the host, which is small.
The run identifier is deliberately **not** a dimension: it is unbounded, and the
span tree is what answers per-run questions.

### Counters are added once, at the end of a run

Not once per item. A crawl is a batch, its totals are already accumulated in
`PushSummary`, and adding the run's total once is the same monotonic series as
adding one at a time while costing one call instead of a hundred thousand. The
per-item view is the span tree; the counter is the rate.

### Refusals are counted twice on purpose

`crawl.items.refused_by_label` is a **subset** of `crawl.items.skipped`. A
dashboard that adds the two double counts. One that plots the ratio reads how
much of the corpus the policy is holding back, which is the question worth
asking of a policy nobody has tuned yet.

The reason is arithmetic: rows read is reconciled as
`Total + Unchanged + Skipped`, in the host's summary line and again in the run
row. A refusal counted only in its own bucket would make that identity stop
holding, and rows-read would silently under-report.

---

## Sending it somewhere

Two halves, and both are required.

**Build with the packages:**

```powershell
.\Build.ps1 -EnableOtlpExporter
```

or `dotnet build -p:EnableOtlpExporter=true`.

**Then configure the collector**, in the connector's appsettings:

```jsonc
"Otlp": {
  "Enabled": true,
  "Endpoint": "http://otel-collector.corp.example:4318",
  "Protocol": "HttpProtobuf",
  "TimeoutSeconds": 10,

  // For a hosted collector wanting an API key. The VALUE is in Windows
  // Credential Manager under this target, never in this file.
  "HeaderName": "x-api-key",
  "HeaderCredentialTarget": "OtelCollectorKey"
}
```

A build **without** the packages and `Enabled: true` says so on startup and
crawls normally. That is the same contract the log sink already keeps: a
configuration that asks for what the binary cannot do reports it rather than
running silently unobserved.

### Endpoint is the collector's base address

`/v1/traces` and `/v1/metrics` are appended for you. A value ending in either
produces a doubled path, so validation refuses it at startup rather than letting
it 404 quietly on every export.

This is worth stating because it caught us. The OpenTelemetry SDK appends the
signal path itself **only** when the endpoint came from the
`OTEL_EXPORTER_OTLP_ENDPOINT` environment variable. Set `Endpoint`
programmatically — as this repository does, because its configuration lives in
appsettings — and the SDK treats the value as the final per-signal address and
posts everything to it verbatim. The first version of the exporter did exactly
that. `OtlpExporterTests` caught three POSTs to `/` and none to either signal
path; against a real collector that is a 404 per export, retried and then
dropped, with a working crawl and no telemetry at all.

### Protocol: HTTP by default, and not by accident

`HttpProtobuf` is the default here, which is **not** the OTLP specification's
own default. The reason is local: this estate routes egress through a proxy —
`Settings:GraphProxy` exists for exactly that — and OTLP/gRPC needs HTTP/2 end
to end, which an HTTP/1.1 forward proxy will not carry.

A deployment with a direct route to its collector should say `Grpc` and use port
4317.

### The API key never goes in the file

`HeaderCredentialTarget` names a Windows Credential Manager entry; the value
lives there. Same rule as `Auth:ClientSecretCredentialTarget`, and for the same
reason: an API key in an appsettings file is an API key in source control, in
the release package, and in every support bundle anybody ever sends. Validation
rejects a value that *looks* like a key rather than a name.

A missing or unreadable credential is a **warning and an unauthenticated
exporter**, not a refusal to run. Telemetry is an observation of the crawl, not
part of it, and refusing to crawl because the monitoring platform's key was not
rotated would make observability an availability risk.

---

## Two decisions worth knowing about

### Delta temporality

A crawl is a process that starts, counts, and exits. Under the OTLP default of
*cumulative* temporality every run reports a series that begins at zero and dies
with the process, so a backend sees a permanent sawtooth and any `rate()` over
it is wrong at every process boundary.

Delta says "this run wrote 11,900 items", which is both what happened and what
sums correctly across runs. It is set explicitly.

### The flush is explicit, and its failure is logged

Counters are recorded **once**, at the end of the run, and the periodic metric
reader exports on a timer measured in tens of seconds. A push tool that returned
its exit code without flushing would lose the only measurement it ever took, on
every run short enough to matter.

`PushHost` therefore disposes the exporter in the run's `finally`, **before**
`Log.CloseAndFlush`, so a warning about undelivered telemetry still reaches a
live sink. The `using` declaration is still there as the backstop for the paths
that return before a run ever starts, and `Dispose` is idempotent so the second
call is a no-op.

If the flush does not complete inside the configured timeout, that is a
**Warning**, not silence. An alert built on "this run reported no items written"
must be able to tell a run that wrote nothing from a run whose telemetry never
left the host.

---

## What the flag costs

16 packages, 6 MB, listed in `build/Get-OfflinePackages.ps1` and checked against
the real restore graph by `build/Test-OfflinePackageList.ps1 -Configuration Otlp`.

Two things worth knowing before raising a version:

**OpenTelemetry 1.18.0 carries no gRPC stack.** It speaks both OTLP protocols
over `HttpClient`, so `Grpc.Net.Client`, `Grpc.Core.Api` and `Google.Protobuf`
stay out of its graph. That is load-bearing rather than merely pleasant: this
same flag already moves `Google.Protobuf` between two pinned versions to satisfy
the Serilog sink, and a third constraint on it would be an `NU1605` with no
version that satisfies everything. The gRPC packages in the OTLP block belong to
the Serilog sink alone.

**1.13.x restores with three `NU1902` moderate-severity advisories** against the
exporter package itself, and CI builds with `-warnaserror`. 1.18.0 clears all
three. Check that before pinning to anything older.

CI builds the solution in **both** configurations, so the optional path cannot
rot into an unbuildable state.

---

## Testing it

`tests/SqlTicketsConnector.Tests/PushTelemetryTests.cs` subscribes an
`ActivityListener` and a `MeterListener` — the same mechanism the SDK uses — and
asserts the shape of what the engine emits. No exporter is involved, so these
run in the default build where the packages are absent.

`OtlpExporterTests.cs` puts a socket in the way and asserts that bytes arrive at
`/v1/traces` and `/v1/metrics`. It runs in both build configurations and asserts
different things in each: compile constants do not cross project boundaries, so
the test project cannot see `OTLP_EXPORTER` and asks the object instead. That is
also the only way to test the documented behaviour of a *default* build, where
`Otlp:Enabled` must warn rather than pretend.

Two traps if you extend these:

**`Sample` is mandatory on an `ActivityListener`.** Without it `StartActivity`
returns null, every span assertion passes vacuously, and the suite reports that
instrumentation works while observing nothing at all.

**`ActivitySource` and `Meter` are process-global statics**, and xunit runs test
classes in parallel with no collection behaviour configured here. Five other
classes drive `PushEngine.RunAsync` concurrently, so a listener subscribed to
`PushTelemetry.Name` receives their spans and measurements too. Filter on a
connection id no other test file uses. Filtering on `connector.key` does **not**
work: `FakePushConnector.Key` is the constant `"fake"` and four other classes
use it.

---

## Related

- [ALERTING.md](ALERTING.md) — what to page on, and what these metrics can and cannot tell you
- [SENSITIVITY-LABELS.md](SENSITIVITY-LABELS.md) — the refusal counter and the control behind it
- [SECURITY.md](SECURITY.md) — the dependency note for the optional packages
