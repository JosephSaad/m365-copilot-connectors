---
title: Scheduling
description: Several connectors on one host, in one queue, behind one scheduled task — plus how to schedule incremental crawls, what full crawls are still for, and how to schedule the weekly reconciliation.
---

# Scheduling

Three things live here, because they are the same decision seen from three
angles:

1. **[Incremental crawls](#3-scheduling-incremental-crawls)** — the switch, the
   view it needs, the bootstrap, and the deletion latency it buys speed with.
   Start here if that is what you came for; it is section 3 and it stands alone.
2. **[The queue](#4-the-queue)** — several connectors on one host, serialised
   behind one scheduled task, so full crawls cannot stack in one window and
   collectively exceed the tenant's Graph budget.
3. **[The weekly reconciliation](#7-scheduling-the-reconciliation)** — the one
   check that catches the store and Graph agreeing with each other and both
   being wrong about the source, scheduled and alerted on rather than run by
   hand.

Numbers marked **MEASURED** were observed on this rig; **PUBLISHED** means
stated by Microsoft and not observed here. The same convention as
[`CAPACITY-PLANNING.md`](CAPACITY-PLANNING.md), which carries the arithmetic
this document leans on.

---

## 1. What the scheduler decides, and what it does not

Read this first, because most scheduling mistakes come from getting it backwards.

**The scheduler decides WHEN a run starts. The connector decides WHAT KIND of
run it is.**

A scheduled task fires the executable. The executable asks the state store to
open a run, and `uspBeginRun` decides — from `Settings:Incremental`, from when
the last successful full crawl was, from `Settings:FullEveryHours`, and from
whether the hash version has changed — whether this run reads the whole source
or only what moved. **There is no `--full` argument and there should not be.**
The mode is a property of the connection's history, and history lives in the
store.

So: you cannot schedule "a full crawl on Sunday and incrementals on weekdays" by
giving them different triggers. You schedule *runs*, at a cadence, and you set
`Settings:FullEveryHours` to say how stale a full crawl may get. The connector
escalates when it must. Section 3.5 works through what that looks like on a
calendar.

---

## 2. Exit codes, and the one that must not page

Every push tool — `SqlGraphPush`, `SqlHierarchyPush`, `CdpGraphPush` — runs on
`PushCore` and returns the same codes. A scheduled task's **Last Run Result** is
this number, and for a connector with no queue in front of it, it is the only
signal anybody gets.

| Code | Means | Page it as |
|---|---|---|
| `0` | The crawl completed | Nothing. Check `skipped=` is what you expect |
| `2` | Configuration invalid. Nothing opened a socket | A deployment fault, not an incident |
| `3` | A credential was rejected — by **Entra** or by **the source** | Credential rotation. Never fold this in with `4` |
| `4` | Ingestion failed part-way | The data path |
| `5` | **Skipped: another instance holds the run lease** | **Nothing. This is success** |

### Why `5` is a success

`sql/43` puts a heartbeat lease on the connection. A second run against a
connection that is already being crawled does not queue, does not interleave,
and does not race the first one's delete sweep — it stands down and returns
**5**.

That is the correct outcome of a correct design, and it will happen routinely:
a crawl that overran its window and is still going when the next fires, a second
node in an active/passive pair whose scheduled task fires while the first node
holds the lease. **A monitoring rule that treats non-zero as failure will page
somebody nightly for a connector working exactly as designed**, and the third
time that happens the rule gets muted, taking `3` and `4` with it.

`deploy/Schedule-Connectors.ps1` reports `5` as `skipped (lease held
elsewhere)` and does not count it as a failure. If you are running connectors
without the queue, encode the same rule in whatever polls Last Run Result:

> Alert on `2`, `3` and `4`. Do not alert on `0` or `5`.

### The codes the queue itself returns

`Schedule-Connectors.ps1 -Run` aggregates a whole cycle, so it has its own small
table:

| Code | Means | Page it as |
|---|---|---|
| `0` | Every due entry ran; none failed | Nothing |
| `1` | The window closed before part of the queue could start | **Not a connector failure — a capacity finding.** Something is taking longer than its slot. Read it in the morning, act on it before it becomes routine |
| `2` | At least one entry failed | The per-entry outcome names which and why |
| `3` | The queue refused to run, or could not | A deployment fault: a missing manifest, a configuration the preview already refused |

---

## 3. Scheduling incremental crawls

### 3.1 What changed, and why a reader who remembers otherwise needs telling

**Until recently, `Settings:Incremental` did nothing.** Setting it to `true`
produced a log line and a full crawl. The reason was in the connectors, not the
engine: no shipped source implemented the `ChangeMarker` tier, so
`PushItem.LastModifiedUtc` was never set, `crawl.Checkpoint` stayed empty across
every run in the project's history, and `uspBeginRun` escalated every
incremental request straight back to full.

**That is no longer true for the hierarchy connector.** `SqlHierarchyPush` reads
the marker, saves a checkpoint, and an incremental run genuinely reads only what
moved. The evidence is in section 4 and live test L10 of
[`GO-LIVE-READINESS.md`](GO-LIVE-READINESS.md), and it is worth quoting because
it is the thing that makes this section worth writing at all: a full crawl
followed by an incremental one read **111,900 rows then 0**; three narrative
edits read exactly **3**; one customer rename read exactly **69** — the
customer, its 4 engagements and their 64 time entries — matching the SQL delta
exactly.

**It is still not true for the others.** `SqlGraphPush` and `CdpGraphPush` do
not implement `ChangeMarker`. Turning `Settings:Incremental` on for them
produces the old behaviour: a request that `uspBeginRun` escalates, a full read,
and a line in the log saying it did. Nothing breaks; nothing is gained.

### 3.2 Turning it on

Two settings, and both of them:

```jsonc
{
  "Source":   { "ItemView": "dbo.vwExternalItemsIncremental" },
  "Settings": { "Incremental": true, "FullEveryHours": 168 }
}
```

**`Source:ItemView` has to move too, and forgetting it is the mistake everybody
makes once.** `dbo.vwExternalItems` carries no `EffectiveLastModified` column.
`dbo.vwExternalItemsIncremental` — created by `sql/26` — is that view plus the
column, and it is what the marker is read from.

The connector refuses the combination by name:

```
Source:ItemView is dbo.vwExternalItems, which carries no EffectiveLastModified
column, but Settings:Incremental is on. Point it at
dbo.vwExternalItemsIncremental (sql/26), or turn Settings:Incremental off to
read the whole source every run.
```

**Exit code 2**, before anything is opened. That refusal exists because of what
happens without it: SQL Server answers the first read with *"Invalid column
name 'EffectiveLastModified'"* and the tool exits **4** — after the connection
and the schema have been registered, naming a column rather than a setting, at
02:00, in a log nobody is reading.

`deploy/Schedule-Connectors.ps1` checks for the same combination in its preview
and refuses to install a queue containing it, so the mistake is caught by the
person scheduling rather than by the schedule.

**Leaving `Source:ItemView` blank is also correct** — the connector fills it in
from `Settings:Incremental`, choosing the incremental view when the flag is on
and the plain one when it is off. Naming it explicitly is clearer in a
configuration file somebody will read at 03:00, which is why the example above
does.

### 3.3 The first run must be a full crawl, and the bootstrap is slower

**There is nothing to resume from before the first checkpoint exists.** The
first run reads everything, writes everything and saves the marker the second
run resumes from. That is unavoidable and it is not the interesting part.

The interesting part is that **the incremental path bootstraps more slowly than
the plain one**, and the reason is structural rather than incidental.
`HierarchyIncrementalSource` declares `RequiresOrderedCommit = true`, and
`PushEngine.ResolveWriterCount` returns **1** for any source that does — before
it even looks at `Settings:Writers`. A source that keeps a position needs serial
writes, because out-of-order completion is precisely what would let its
checkpoint pass an item that never landed.

So the initial load runs on **one writer instead of sixteen**. On an unchanged
111,900-item corpus that was reported as **56.6 seconds against 38.3** — figures
carried over from the connector-side incremental work rather than re-measured
for this document, and marked accordingly. On a corpus that is actually being
*written*, where [`CAPACITY-PLANNING.md`](CAPACITY-PLANNING.md) measures the
writer pool at 98.4% utilisation across sixteen writers, the ratio is far worse
than that: writes are the bottleneck, and a bootstrap on one writer gives up
fifteen sixteenths of the throughput.

**So the documented escape is to do the initial load with `Settings:Incremental`
OFF, then turn it on:**

1. Deploy with `Settings:Incremental` absent or `false`, and `Source:ItemView`
   at the plain view (or blank).
2. Run one full crawl. Sixteen writers, the throughput
   [`CAPACITY-PLANNING.md`](CAPACITY-PLANNING.md) section 3 models.
3. Set `Settings:Incremental` to `true` and `Source:ItemView` to
   `dbo.vwExternalItemsIncremental`.
4. Run again. This one is still a full crawl — no checkpoint exists yet — but it
   is an *all-unchanged* full crawl, which is the cheap kind: **MEASURED**, 12
   to 15 seconds for 111,900 items. It writes nothing and saves the marker.
5. Every run after that is incremental.

Step 4 is the trick, and it is why this sequence works: the expensive part of a
bootstrap is the writes, and step 2 has already done them at full concurrency.
Step 4 pays the serial penalty on a run that writes nothing.

### 3.4 What incremental costs: deletions and ACL revocations get slower

⚠️ **This is the trade and it must be made deliberately.**

**The delete sweep runs on a full crawl only.** It has to. The sweep works by
diffing the source's inventory against `crawl.Item` — and an incremental read
returns a *slice*, so absence from it means nothing at all. An incremental run
that swept would delete the entire unchanged corpus. The engine logs
`"Incremental run; no delete sweep. Absence from a partial read means nothing."`
and moves on.

Therefore, with `Settings:Incremental` on:

> **Deletions and permission revocations propagate at the FULL-CRAWL cadence,
> not the run cadence. On the default `FullEveryHours` of 168, that is up to a
> week.**

The ACL half is the sharper one, and it is not obvious. An incremental read
picks up an item when the item's own timestamp moves. **A group membership
change in the directory does not move any item's timestamp.** So a revocation
reaches the index when the next full crawl re-resolves and re-hashes the ACLs —
`FullEveryHours` — and not before.

Section 1 of [`GO-LIVE-READINESS.md`](GO-LIVE-READINESS.md) carries a ⚠️ about
exactly this: the ACL staleness bound somebody accepted in
[`PRODUCTION-ONBOARDING.md`](PRODUCTION-ONBOARDING.md) row 1.1 was a bound on
the *run* cadence, and turning incremental on makes the real bound the
*full-crawl* cadence. **That number needs re-accepting by the person
accountable for it, not silently inherited.** Do not turn incremental on for a
connection whose accepted staleness bound is shorter than
`Settings:FullEveryHours`.

The framing that makes the decision easy is the one section 7 of
[`GO-LIVE-READINESS.md`](GO-LIVE-READINESS.md) opens with: when this connector
falls behind, search does not go down, it goes stale — and the exposure is not
that users cannot search, it is that **deletions and permission revocations stop
propagating**. Incremental crawling makes the connector faster by deliberately
accepting a slower version of exactly that exposure. It is a good trade for a
large, slow-changing corpus with a generous staleness bound. It is a bad trade
for a small corpus that a full crawl sweeps in thirteen seconds anyway.

**Two other things force a full crawl, and both are safety rather than
scheduling.** `Settings:FullEveryHours` elapsing is one. A **hash-version
change** is the other: when `ItemHasher.HashVersion` moves, the store reports it
once and the run escalates, because a change to the hash framing would otherwise
be a silent overnight rewrite of the whole corpus. Neither is configurable away,
and neither should be.

### 3.5 What a sensible schedule looks like

Because the connector picks the mode, a schedule is two numbers: **how often the
task fires**, and **`Settings:FullEveryHours`**.

| Corpus | Task fires | `FullEveryHours` | What happens |
|---|---|---|---|
| Small (thousands), full crawl is seconds | Nightly | 168, and irrelevant | **Leave `Incremental` off.** A 13-second full crawl every night gives you deletions within 24 hours for free. Incremental buys nothing and costs a week of deletion latency |
| Large (hundreds of thousands), moderate churn | Hourly, or every 4 hours | 168 | Incremental most of the time; one full crawl a week, whenever the 168 hours happen to elapse, which sweeps deletions |
| Large, and the staleness bound is tight | Every 4 hours | **24** | A daily full crawl bounds deletion latency at 24 hours. Costs one full read per day — cheap when unchanged (**MEASURED**, 12–15s per 111,900 items), expensive only if lots changed |
| Very large, bootstrapping | See section 3.3, then above | 168 | |

**The full crawl is not a fallback and it is not a repair.** It is what
re-establishes the baseline every incremental delta is measured against, and it
is the only thing that can conclude anything about absence. Schedule it as a
feature.

**One thing the schedule cannot control:** which run happens to be the one that
escalates. `uspBeginRun` escalates the first run that finds
`FullEveryHours` elapsed, so on an hourly schedule with 168 hours, the weekly
full crawl drifts an hour later each week. If it matters that the full crawl
lands in a particular window, set `FullEveryHours` a little *below* the interval
you want — 160 rather than 168 — so the escalation always happens on the first
run of the intended night rather than sliding out of it.

---

## 4. The queue

### 4.1 The problem

[`CDP-DEPLOYMENT.md`](CDP-DEPLOYMENT.md) step 9 says to register one scheduled
task per connector, staggered by hand — *"three tasks, three hours: they share a
host, a service account and a tenant, and a full HDFS crawl overlapping a
catalogue run buys nothing but throttling."*

That advice is correct and it does not survive a growing estate. A
hand-staggered schedule is a set of independent assumptions about how long each
crawl takes, written down once, in separate places, and never revisited. The
first crawl that outgrows its gap overlaps the next one and nothing says so.
Adding a fourth connector means re-deriving four offsets in somebody's head. And
the arithmetic that actually matters — peak concurrent operations against Graph
— is written down nowhere.

### 4.2 The unit of contention is the (tenant, application) pair

This decides what the queue can and cannot promise, so it is worth being exact.

**Not the connection.** Two runs against *one* connection are refused by the
`sql/43` lease, which returns exit 5. Solved elsewhere; the queue must not
duplicate it.

**Not the host**, convenient though that would be. **MEASURED:** the 100-fold
run was Graph-round-trip-bound with the writer pool 98.4% busy while the machine
was largely idle. The host is not the scarce resource. Two hosts each running
four connectors against one tenant collide exactly as badly as one host running
eight.

**The (tenant, application) pair.** Graph counts throttling against the
application making the calls, and everything sharing the app registration shares
the budget. The concurrency ceiling this repository designs against — *"an
application is limited to 25 concurrent operations on a connection"* — is
per-application. (Read section 2.3 of
[`CAPACITY-PLANNING.md`](CAPACITY-PLANNING.md) before quoting that number: it is
cited by this repository from Microsoft guidance and is **not** on the current
published limits page.)

**So the honest statement of what `Schedule-Connectors.ps1` promises: it
serialises one host, which is a complete answer only when one host runs every
connector for one application.** That is the deployment this repository
describes. It is not one you may assume. Two hosts sharing an app registration
need a shared lease, and the only shared thing this deployment already has is
`ConnectorState` — the same place `sql/43` puts the per-connection lease. That
is the shape of the fix and it is deliberately not attempted here.

### 4.3 One task, one process, a queue inside it

The obvious design — keep N tasks and compute N start times — cannot handle the
case this is actually about, which is a crawl that overruns. Task Scheduler has
no native "on completion of task X" trigger; the nearest thing keys off events
in the Task Scheduler operational log, which has to be enabled and is fragile.
A computed offset is a promise about durations that nothing enforces.

So: **one scheduled task**, which runs `Schedule-Connectors.ps1 -Run`, which
executes the queue sequentially in its own process.

At most one crawl is ever in flight from this host, by construction rather than
by arithmetic. Peak concurrency against Graph becomes `max(Writers)` instead of
`sum(Writers)`, and the preview prints both next to the budget so an operator
can see what serialising bought:

```
== Graph concurrency ==

  serialised (this queue)   peak 16 concurrent operation(s)
  if these ran in parallel  peak 32 concurrent operation(s)
  declared budget           25

  This is what serialising bought: 32 would have exceeded 25; 16 does not.
```

**Adding a connector is one object in the manifest and a re-run of the script.**
Nothing is re-derived by hand, because nothing was derived by hand.

---

## 5. The manifest

One JSON file. `connector-schedule.json` beside `Schedule-Connectors.ps1` unless
`-ManifestPath` says otherwise.

```jsonc
{
  "window": { "start": "01:00", "end": "06:00" },
  "concurrentOperationBudget": 25,
  "safetyFactor": 2.0,
  "slotGranularityMinutes": 15,
  "taskPrincipal": {
    "userId":    "CORP\\svc-push$",
    "logonType": "Password",
    "runLevel":  "Limited"
  },
  "queue": [
    {
      "name":             "consultingwork",
      "kind":             "crawl",
      "executable":       "C:\\Connectors\\Hierarchy\\SqlHierarchyPush.exe",
      "arguments":        [],
      "workingDirectory": "C:\\Connectors\\Hierarchy",
      "connectorKey":     "",
      "expectedMinutes":  20,
      "runOn":            "always",
      "overrunPolicy":    "finish",
      "enabled":          true
    },
    {
      "name":            "weekly-reconciliation",
      "kind":            "reconciliation",
      "arguments":       [ "-ConfigPath", "C:\\Connectors\\SqlTickets\\appsettings.json",
                           "-EventLogSource", "ConnectorReconciliation" ],
      "expectedMinutes": 45,
      "runOn":           "Sunday"
    }
  ]
}
```

### Top level

| Field | Default | What it does |
|---|---|---|
| `window.start` / `window.end` | `01:00` / `06:00` | Local time, `HH:mm`. A window crossing midnight is normal and handled. The task's trigger is `window.start`; the queue stops starting new entries at `window.end` |
| `concurrentOperationBudget` | `25` | The ceiling the preview checks peak concurrency against. Graph's cited per-application, per-connection figure |
| `safetyFactor` | `2.0` | Multiplier on `expectedMinutes` to get a slot. Two, because a crawl that has to write is many times a crawl that does not, and the point of a slot is to survive a bad night |
| `slotGranularityMinutes` | `15` | Slots round up to this, so the timetable reads like a timetable |
| `taskPrincipal` | — | `userId`, `logonType`, `runLevel` for the single registered task |
| `queue` | — | The entries, **in the order they run** |

### Per entry

| Field | Default | What it does |
|---|---|---|
| `name` | `entry<n>` | What the summary and the findings call it |
| `kind` | `crawl` | `crawl` or `reconciliation` |
| `executable` | — | Required for `crawl`. For `reconciliation` the script supplies its own interpreter and `Invoke-Reconciliation.ps1`, so a manifest cannot point that kind at something else |
| `arguments` | `[]` | Passed through. For `reconciliation` these are appended to `Invoke-Reconciliation.ps1`'s own |
| `workingDirectory` | the executable's folder | Also where `appsettings[.key].json` is looked for |
| `connectorKey` | `""` | Selects `appsettings.<key>.json` when it exists, mirroring `PushOptions.ResolveFile` |
| `expectedMinutes` | `15` | How long this normally takes. **Measure it; do not guess it.** `crawl.Run` knows |
| `runOn` | `always` | `always`, or a day name, or an array of day names |
| `overrunPolicy` | `finish` | `finish` or `kill` — section 6 |
| `enabled` | `true` | `false` keeps the entry in the file and out of the cycle |

### What the preview checks before it will install

`Schedule-Connectors.ps1` with no mode previews and registers nothing. It reads
each entry's `appsettings` — because the manifest says what an operator
*intends* and `appsettings` says what the engine will *do* — and refuses to
install on any of:

- an `executable` that does not exist on this host
- no configuration where `PushOptions.ResolveFile` would look
- `Settings:Incremental` on with `Source:ItemView` at `dbo.vwExternalItems` —
  section 3.2
- **two entries crawling one `Graph:ConnectionId`**, which schedules a nightly
  exit 5: the `sql/43` lease refuses the second and returns "skipped", correctly,
  for ever
- a queue whose slots do not fit its window
- one entry asking for more concurrent writers than the budget

and warns, without refusing, on:

- `Settings:Writers` above 16, naming the number `PushEngine` will clamp it to,
  so the timetable shows what will actually happen rather than what was typed
- `Settings:Incremental` on, restating the deletion-latency trade from section
  3.4 at the moment somebody is scheduling it

**It never writes to an `appsettings.json`.** `PushOptions.Load` reads its file
directly with `System.Text.Json` — there is no `IConfiguration`, no
environment-variable provider, no command-line provider, and `PushHost` accepts
only `--connector`, `--dry-run` and `--help`. So `Settings:Writers` cannot be
injected at launch, and a scheduler claiming to set a per-connection budget at
run time would be claiming something the engine cannot receive. The budget is
enforced by **refusing to schedule** a configuration that breaches it, at the
moment a person is present to fix it, and by serialising so that only one
connector's writers are ever in flight.

---

## 6. Overruns

An entry that takes longer than its slot is the most useful capacity signal this
estate produces. Three things happen, in order.

**1. It is allowed to finish.** Killing a crawl mid-write is worse than
overrunning: the run row is left open for the store to reap as abandoned, the
checkpoint stops where it stopped, and the next run redoes the work. Nothing is
corrupted — the store records a hash only after Graph confirms the write — but
nothing is gained either. `"overrunPolicy": "kill"` is available per entry for
operators who prefer a hard stop, and that paragraph is what it costs.

**2. The overrun is recorded and named.**

```
WARNING: consultingwork took 47.3 minute(s) against a 45-minute slot. Raise
expectedMinutes in the manifest, or the tail of this queue will start being
skipped.
```

**Record it before raising `expectedMinutes` to make it go away.** A slot that
has been raised three times is a corpus that has grown 3x, and that is a fact
worth having when somebody asks whether 10x is coming.

**3. The rest of the queue keeps its window, not its clock.** Entries that have
not started when the window closes are **skipped and reported**, never started
late into the morning. A crawl that runs into business hours is how a connector
becomes the thing that made the tenant slow. The cycle exits **1**, which is a
capacity finding and not a connector failure.

A cycle that starts *outside* its window — a delayed task, a manual run at
noon — does not conclude it has no window and skip everything. It takes the
window's full length from now and says so, because a silently zero-length window
looks exactly like a scheduler that ran and did nothing.

---

## 7. Scheduling the reconciliation

### 7.1 Why it needs a wrapper at all

`deploy/Compare-SourceToIndex.ps1` is the only check in this repository that
asks the source and Graph directly, with the state store as a third opinion
rather than the arbiter. Everything else compares the connector against its own
memory. That makes it the only thing that can catch a *consistent* lie — section
7 of [`GO-LIVE-READINESS.md`](GO-LIVE-READINESS.md), Tier 1 item 7.

**But it exits 0 whether or not it finds drift.** Its only `exit 1` at the end
is guarded by `if ($errors.Count -gt 0)`. A run that finds four hundred orphans,
prints them in red and prints forty `DELETE` commands for review exits 0 —
green, in Last Run Result, indistinguishable from a clean week. That is not a
defect: it is an interactive tool whose output is read by the person who ran it.
It does mean the finding lives in the text, and something has to read the text.

`deploy/Invoke-Reconciliation.ps1` is that something. It runs the comparison as
a child process, parses the `== Result ==` block, and exits with a code a
monitoring rule can act on.

### 7.2 Its exit codes

| Code | Name | Means | Alert |
|---|---|---|---|
| `0` | `clean` | Ran, coverage complete, no drift | No |
| `1` | `transient` | Could not run — SQL down, no token, timed out — and the consecutive-failure count is below the threshold | No |
| `2` | `blind` | Ran, verdict withheld: rows in ERROR, or `-MaxItems` cut it short, or `-RequireInventory` and the inventory was not authoritative | Yes |
| `3` | `drift` | Ran, coverage complete, orphans or missing or stale above `-DriftTolerance` | Yes |
| `4` | `stuck` | Could not run, and has now failed `-FailuresBeforeAlert` times in a row | Yes |
| `5` | `misconfigured` | The wrapper cannot proceed, or the comparison produced output it could not parse | Yes |

> **Exit 0 and 1 do not page. Exit 2 and above page.**

The codes ascend by severity, so precedence is the ordering: a run that is both
blind and drifted exits **3**, because a confirmed finding outranks an unknown
one, and the summary says both.

**Why `1` does not page and `4` exists.** A weekly reconciliation that wakes
somebody because the SQL instance was patching is a weekly reconciliation that
gets muted, and a muted check is a deleted one. But "does not page" must not
become "is never mentioned": a check that has failed to run for three weeks is a
check that has silently stopped, which is the failure mode Tier 0 item 1 is
about. A consecutive-failure counter lives in the state directory, is reset by
any run that reaches a verdict, and turns the second consecutive could-not-run
into exit 4. Two weeks blind is the longest it stays quiet.

**Why a missing `== Result ==` block is `5` and never `0`.** If the comparison
exits 0 and the wrapper cannot find a result block, the two files no longer
agree about the contract between them. Reporting "clean" from an absence of
parsed data would be the same defect the comparison itself was fixed for: an
empty inventory that read successfully was reporting the hard-delete gap CLOSED,
which is a reassurance manufactured out of missing data. **No parsed counts, no
verdict.**

### 7.3 What it can and cannot detect

**It reconciles `dbo.Tickets` and only `dbo.Tickets`.** The comparison's SELECT
is literally `SELECT ... FROM dbo.Tickets ORDER BY TicketId`, and it builds the
external item identifier as `"ticket"` plus the integer `TicketId`. It does
**not** generalise to the hierarchy connector — whose items are `Customer`,
`Engagement` and `TimeEntry` rows from three views, with identifiers like
`cust1` and `time5003` — and it does not generalise to the CDP connectors at
all. Section 3 of [`GO-LIVE-READINESS.md`](GO-LIVE-READINESS.md) records the
same limit from the other side: the inventory read is proven live, but
"end-to-end reconciliation is still unproven here — the only connection with an
inventory is the hierarchy one, and this script reads `dbo.Tickets`".

Scheduling it against the hierarchy connection would either fail on a missing
table or, worse, succeed: it would read whatever `dbo.Tickets` holds, ask Graph
about `ticket<n>` identifiers that connection has never contained, and report
every one of them MISSING. Every week. **So the wrapper does a preflight**:
given a state connection string it reads `crawl.Connection.ConnectorKey` and
exits 5 when it is not `-AssertConnectorKey` (default `sqltickets`). Without a
state connection string it cannot check, says so, and proceeds — refusing
outright would make the state store a hard dependency of a script documented to
work without one.

**It costs one Graph GET per source row.** There is no list-items API. On this
rig's corpus that is 111,900 GETs for a complete pass, drawn from the same
per-application budget the crawls spend. That is why it belongs in the queue as
an entry with a slot, rather than on a trigger of its own, and why
[`CAPACITY-PLANNING.md`](CAPACITY-PLANNING.md) section 7.4 says a full
reconciliation does not scale to 10x.

**It fixes nothing.** The comparison prints `DELETE` commands and does not run
them, deliberately, and the wrapper does not change that. Prefer a full crawl to
a hand-run list of DELETEs anyway: the connector performs the same
reconciliation fenced by `Settings:MaxDeletePercent`, which the hand-run
commands do not have.

**It cannot carry a secret.** `-SqlCredential` and `-ClientSecret` cannot cross
a process boundary, so the wrapper refuses them rather than pretending. Run the
task as an identity that reaches SQL with Windows authentication and Graph with
a certificate. A scheduled task with a password in its arguments is not an
improvement on a check nobody runs.

### 7.4 Coverage, and the switch that must be typed

`-MaxItems` defaults to **0, meaning no cap** — a complete pass. The
comparison's own default is 500, which is right for a tool somebody is watching
and wrong for a weekly reconciliation: a check that looked at 500 of 111,900
rows and reported nothing has not found nothing. A truncated pass is reported
**blind** unless `-AllowTruncated` is typed, and `-DriftTolerance` defaults to
**0** because a tolerance above zero is how a reconciliation check becomes
decorative.

Both defaults are deliberately the strict ones. Weakening either is a decision
somebody makes on the command line, in a manifest, where it can be read back.

### 7.5 Where it writes, and the event log

The state directory — `reconciliation` beside the script unless
`-StateDirectory` says otherwise — holds three things per connection:

| File | What it is |
|---|---|
| `counter-<connection>.json` | The consecutive-failure counter behind exit 4 |
| `latest-<connection>.json` | The machine-readable summary: outcome, exit code, counts, coverage. What a monitoring agent reads if it reads files |
| `transcript-<connection>-<stamp>.log` | The comparison's full output plus the wrapper's reading and verdict. `-KeepTranscripts` keeps 26 — six months of a weekly job |

`-EventLogSource` writes the outcome to the Windows event log, and **failing to
write is never allowed to change the exit code**: the exit code is the signal,
the event is a convenience. Register the source once, as an administrator:

```powershell
New-EventLog -LogName Application -Source ConnectorReconciliation
```

Event IDs are `1000 + exit code`. Information for clean and transient, Warning
for blind, Error for drift, stuck and misconfigured.

---

## 8. Installing it

```powershell
# 1. Preview. Registers nothing, runs nothing. Do this after every manifest edit.
.\Schedule-Connectors.ps1

# 2. Register the single task, once the preview is clean.
.\Schedule-Connectors.ps1 -Install -Apply

# 3. Run a cycle by hand, to see it work.
.\Schedule-Connectors.ps1 -Run

# 4. Remove it.
.\Schedule-Connectors.ps1 -Uninstall -Apply
```

`-Apply` is a switch rather than PowerShell's `-WhatIf`, because `-WhatIf`'s
sibling `-Confirm` can prompt and nothing in a deployment script for scheduled
tasks should be able to prompt. **Preview is the default and `-Apply` is
typed.**

The registered task uses `-MultipleInstances IgnoreNew`, so a cycle still
running when the next fires is not doubled, and an `ExecutionTimeLimit` of twice
the window as a backstop — long enough that a legitimate overrun is handled by
the queue's own rules rather than by Task Scheduler killing the process
mid-crawl.

**`LogonType Password` with a gMSA supplies no password**: Windows retrieves the
current one from Active Directory at logon. Nothing is typed into the manifest
and nothing is stored in the task. Same stance as
[`CDP-DEPLOYMENT.md`](CDP-DEPLOYMENT.md) step 9 for the per-connector tasks this
replaces.

### Migrating from per-connector tasks

1. Preview the manifest until it is clean.
2. **Disable** the existing per-connector tasks — disable, not delete, so the
   rollback is one click.
3. `-Install -Apply`.
4. Watch one cycle. `cycle-latest.json` in the state directory has the outcome
   and the duration of every entry, which is also where the real
   `expectedMinutes` values come from.
5. Delete the old tasks once a full week has run, including the day the weekly
   full crawl escalates.

---

## 9. Both PowerShell hosts

`Invoke-Reconciliation.ps1` and `Schedule-Connectors.ps1` both carry
`#Requires -Version 5.1` and were exercised on **Windows PowerShell 5.1** and
**PowerShell 7**. Three traps this repository has hit before, and one it had not:

- **`-SkipCertificateCheck` is PowerShell 7 only.** Neither script uses it.
- **`Add-Type -UsingNamespace` fails on both.** Neither script uses it.
- **`$PSScriptRoot` is EMPTY inside a `param()` default under 5.1** — not null,
  empty — so `Join-Path $PSScriptRoot 'x'` there yields `\x` and resolves
  against the current drive root. Every path default in both scripts is resolved
  *after* the param block for exactly this reason.
- **New, and it was found by running the tests rather than by reading:** under
  Windows PowerShell 5.1 a process object returned by `Start-Process -PassThru`
  comes back without its native handle cached, so once the child exits
  `.ExitCode` reads as `$null` — **empty, not zero** — while `.HasExited` is
  `$true`. Reading `.Handle` once while the child is alive keeps a
  `SafeProcessHandle` open and the exit code survives. PowerShell 7 returns the
  code either way, which is precisely what makes it dangerous: **the bug is
  invisible to whoever develops on 7.** Both scripts touch `.Handle`, with the
  measurement in a comment beside it. Without it, `Schedule-Connectors.ps1`
  would have read `sql/43`'s exit 5 as a failure and paged for correct
  behaviour — the exact outcome section 2 exists to prevent.

One more, worth knowing before adding to either script: **the `*-EventLog`
cmdlets are Windows PowerShell cmdlets.** Under PowerShell 7 they resolve, when
they resolve at all, through the Windows PowerShell compatibility session — a
remoting round trip inside a scheduled task. `Invoke-Reconciliation.ps1` calls
`[System.Diagnostics.EventLog]::WriteEntry` directly, which is present on both
hosts and is what both cmdlets call anyway.
