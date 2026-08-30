---
title: Alerting and the dead-scheduler watch
description: Why a dead connector is a security incident rather than an outage, the watchdog that detects it, and the paging matrix — which conditions wake somebody at 03:00, which wait for morning, which are dashboard-only facts, and which nothing on this host can detect at all.
---

# Alerting and the dead-scheduler watch

This page closes items 1 and 8 of
[section 7 of the go-live readiness review](GO-LIVE-READINESS.md): dead-scheduler
detection wired to alerting, and a single decision about which of this system's
alert-worthy events reach a person.

It covers one script,
[`deploy/Watch-ConnectorHealth.ps1`](../deploy/Watch-ConnectorHealth.ps1), and
one table. The table is the part worth arguing about; the script is an
implementation of it.

| | |
|---|---|
| **What it is** | A scheduled PowerShell watch that polls `GET /health` or `crawl.vwConnectionHealth`, decides what is alert-worthy, writes the verdict to the Windows Event Log, and exits with a code a scheduler can page on. |
| **Where it runs** | Any Windows host that can reach either the dashboard over HTTPS or the state database over TDS. Usually the connector host itself; there is an argument for elsewhere, in [section 6](#6-what-watches-the-watchdog). |
| **What it needs** | Windows PowerShell 5.1 or PowerShell 7, and read access to one of the two sources. No secret, no password, no configuration file. |
| **What it writes** | Event log entries `9000`–`9004` under source `ConnectorState`, and a small state file under `%ProgramData%`. It writes nothing to `ConnectorState` or to any source database. |
| **Prerequisite** | Either the dashboard deployed per [`CRAWL-STATE-DEPLOYMENT.md`](CRAWL-STATE-DEPLOYMENT.md) with the watch identity in `CrawlState:ReaderGroups`, or `SELECT` on `crawl.vwConnectionHealth`. |

---

## 1. Why this is a security control and not an availability one

**When this connector dies, search does not go down — it goes stale.**

That single sentence decides everything else on this page, so it is worth
stating precisely. Microsoft Graph holds the pushed corpus. It keeps serving
that corpus, at full speed and with no error of any kind, for as long as nobody
pushes anything new. A connector whose scheduled task has been disabled,
whose service account password has expired, or whose host has been decommissioned
therefore produces:

- no outage,
- no error page,
- no slow query,
- and **no user complaint** — because from a user's seat, search still works.

What has actually stopped is the only two things a crawl can do that nothing
else does: **deletions stop propagating, and permission revocations stop
propagating.** A terminated employee's access removal and a deleted customer
record both remain searchable for the whole duration of the outage plus one
crawl. Nobody notices, because the symptom of this failure is that everything
looks fine.

Two consequences follow, and both are unusual enough to be worth writing down:

**The recovery-time objective here is a security number, not an availability
one.** "How long can search be down" has a comfortable answer. "How long may a
revoked permission remain effective in the index" has a different and much
shorter one, and it is the question this system is actually asking.

**A watchdog that only notices when something errors is useless.** The dangerous
state is *silence*. Every design decision below follows from that: the quiet
outcomes — nothing ran, nothing answered, nothing is registered, the watch
itself did not execute — are made loud, and only a measured, in-date, positive
answer is allowed to be quiet.

---

## 2. What already existed, and why none of it reached anybody

The estate was not short of signals before this. It was short of subscribers.

| Signal | Where it lives | Who saw it before |
|---|---|---|
| `GET /health` | The dashboard, JSON, gated by the reader policy | Nothing polled it |
| `crawl.vwConnectionHealth` | The state database, one row per connection | The dashboard pages, when somebody opened them |
| The delete guard, `THROW 50007` | Raised by `crawl.uspGetPendingDeletes`, fails the run | The run log, and the connection page afterwards |
| A `partial` run | Run status 5, health word `items refused` | The dashboard |
| A failed trigger-health job | SQL Agent, `sql/32`, with `@notify_level_eventlog = 2` | The Application event log — unsubscribed |
| `Test-TriggerHealth.ps1` | Exit code 1, for estates with no SQL Agent | Task Scheduler's Last Run Result column |

Every one of those is a good signal that terminates in a place nobody is
watching. The header of `sql/22` says of the health view, in as many words, that
it "is the view a monitoring system polls" — and no monitoring system polled it.
`HealthEndpoint.cs` was written specifically to answer "is anything wrong" in a
form a scheduled check can parse, and no scheduled check parsed it.

So the gap was never detection. It was that **nothing converted a detection into
a person**, and item 8 of section 7 asks for that decision to be taken once
rather than five times.

---

## 3. The paging matrix

This is the table. Every row states who observes the condition, what signal it
produces, what happens, and — the column that matters — **why that severity and
not another**.

Three severities are used throughout:

- **Page** — wake somebody, at 03:00 if that is when it happens.
- **Ticket** — a person in working hours. Never wakes anybody.
- **Dashboard only** — visible on the pages and in the watch's own report, and
  deliberately not routed anywhere. Silence here is correct.

### 3.1 Conditions the watch itself detects

| Condition | Signal | Severity | Why this severity |
|---|---|---|---|
| **No successful run inside the freshness threshold** (`Stale`) | Event `9002`, exit `2` | **Page** | The condition this whole control exists for, and the only one with no second detector anywhere in the estate. Deletions and permission revocations have stopped propagating, the exposure grows for every hour it continues, and **nothing else will ever raise it** — the index keeps answering, so there is no user complaint coming and no error to notice. It is also, uniquely, invisible in the database's own health word: see [section 4.1](#41-why-the-health-word-is-not-the-freshness-signal). |
| **Never succeeded, and live items exist** (`NeverSucceeded`) | Event `9002`, exit `2` | **Page** | Content is live in the index while crawl state has no record of anything having put it there. Retention cannot cause this — `uspPurgeHistory` refuses to remove a run that live inventory still points at — so it means the state store was rewound, restored, or edited, or the connection was registered over an existing Graph connection. Something is being served that nothing now knows how to keep current, and unlike `Stale` there is not even a timer to measure the exposure against. |
| **Never succeeded, and no live items** (`NeverSucceeded`) | Event `9001`, exit `1` | **Ticket** | The same word, a completely different exposure, and the distinction is deliberate. Nothing was ever pushed, so there is nothing in the index to go stale and the security exposure is **exactly zero**. It is a broken deployment: real, worth fixing, and not worth waking anybody for. It escalates to a page by itself the moment a partial push puts items in the index. |
| **`failing`, at or above `-FailuresToPage`** (default 2 consecutive) | Event `9002`, exit `2` | **Page** | Two consecutive failures means the connector's own retry did not work. Runs are happening and not succeeding, so the corpus is frozen while the estate looks active — which is worse than a stopped task, because a stopped task at least has an obviously missing process. |
| **`failing`, one failure only** | Event `9001`, exit `1` | **Ticket** | One failed run is frequently transient: a Graph `429` or `503`, a source restart, a network blip. The connector retries on its next firing. Paging on the first is how a watchdog gets muted, and a muted watchdog is worse than none because it still looks installed. |
| **`items refused` / a `partial` run** | Event `9001`, exit `1`, after 2 polls | **Ticket** | Self-healing by construction: a refused write records no hash, so the item is retried on the next run without anyone doing anything. It is also **self-escalating**, which is what makes the ticket safe — a `partial` run is status 5, and the view's `LastSuccess` CTE counts only status 2, so a connection stuck partial accumulates staleness and reaches the `Stale` page on its own schedule. |
| **`deletes pending`, persisting** | Event `9001`, exit `1`, after 2 polls | **Ticket** | The textbook flapper: non-zero for a few seconds of *every* sweep, which is why it needs two consecutive polls before it counts at all. Persisting across polls means a `DELETE` is being refused and retried — an item the source dropped, still answering searches. That is a real exposure, but a **bounded** one: a known, listable set of items, in `crawl.vwPendingDeletes` with their ages. Treat it as a page by hand if the count is large or the oldest age exceeds one full-crawl interval. |
| **No `ExpectedIntervalMinutes` configured** (`NoExpectedInterval`) | Event `9001`, exit `1` | **Ticket** | A configuration gap that silently disables the database's own lateness detection, so it must be visible or it is never fixed. It nags every single run, on purpose. It is not a page because the watch's `-MaxMinutesSinceSuccess` fallback is covering the hole in the meantime. |
| **Zero connections registered** (`NothingWatched`) | Event `9002`, exit `2` | **Page** | An empty result means "measured nothing" at least as often as it means "nothing wrong", and the two must never render identically. If this fires on an estate that had connections, it is a state-store loss. `HealthProjection.cs` makes the identical call for the identical reason: zero rows is a warning there, never an `ok`. |
| **The state file is unreadable** (`StateUnavailable`) | Event `9001`, exit `1` | **Ticket** | Losing the ability to debounce costs noise, never coverage. The watch disables demotion so everything counts at full severity, and says so. |
| **`disabled`** | Report line only | **Dashboard only** | Somebody decided this. Holding an alert amber through planned maintenance is precisely how a check gets suppressed and then left suppressed. It is still *printed* every run, so a connection disabled in March and forgotten is visible in every report. |
| **`running`** | Nothing | **Dashboard only** | A monitor that pages while the connector is working is a monitor somebody turns off. |

### 3.2 Conditions the watch reports on but does not observe directly

These are the rows where honesty matters more than completeness. The watch sees
the *consequence*, not the event.

| Condition | Who observes it | Severity | Why, and what is genuinely missing |
|---|---|---|---|
| **The delete guard firing** (`THROW 50007`) | The connector's run fails; the **next poll** of the watch sees `failing` | **Page**, via the `failing` row | The guard is working exactly as designed when it fires — it refused a sweep that would have removed more than `MaxDeletePercent` of the corpus, which is far more often a source that returned too few rows than a real mass deletion. **The gap: its moment is not routed anywhere, and the watch cannot identify it.** `SqlCrawlStateStore` rethrows 50007 as an `InvalidOperationException` to keep the server's message intact, so the `errorKind` recorded is the literal string `InvalidOperationException` — verified against the live database, where that is the only `errorKind` present at all. It is indistinguishable from any other `InvalidOperationException`. The counts that make the refusal actionable are in the run's `ErrorMessage`, on the connection page. **Recommendation, not implemented here:** give the guard a stable `errorKind` token such as `delete-guard`, and this becomes a first-class row. |
| **A failed trigger-health job** (`sql/32`) | SQL Agent, already writing to the Application log via `@notify_level_eventlog = 2` | **Ticket** | It is already in the right log — under the SQL Agent provider, not under `ConnectorState`, so it needs a **second subscription rule** ([section 7.2](#72-the-two-rules-that-are-not-about-this-script)). Ticket rather than page because what it detects is correctness drift on a daily clock: a disabled cascading trigger stops `EffectiveLastModified` moving, so incrementals silently miss rows. Serious, bounded at roughly a day, and not improved by being fixed at 03:00. Escalate on the second consecutive failure. |
| **`Test-TriggerHealth.ps1` exit 1** (estates with no SQL Agent) | Task Scheduler's Last Run Result | **Ticket** | Same finding, different scheduler, and **currently routed nowhere at all**. SQL Server Express has no Agent, so on Express this script is the only trigger-health check that exists. Its Last Run Result needs the same treatment as the watch's own — see [section 6](#6-what-watches-the-watchdog). |

### 3.3 Conditions about the watch itself

| Condition | Signal | Severity | Why |
|---|---|---|---|
| **The health source could not be read** — host unreachable, connection refused, DNS failure, `401`, `403`, `404`, `500`, `503`, an HTML body, unparseable JSON, a payload missing its contract fields, a stale or future-dated payload | Event `9003`, exit `3` | **Page** | From the watch's seat, "the connector is dead" and "the box that would have told me is dead" carry **the same exposure**, and the second one hides the first. This is the rule that makes silence unsuccessful, and it is why it is a page rather than the ticket that an unreachable *monitoring* system would normally rate. |
| **The watch itself failed** — bad parameters, an unhandled error | Event `9004`, exit `4` | **Page** | A broken watchdog is the silent failure one level up. Note the exit code is deliberately not `1`: an unhandled PowerShell error exits `1` by default, which is this script's *ticket* code, so a crashing watchdog would otherwise rate its own death below a pending delete. |
| **The watch did not run at all** — task disabled, deleted, host powered off | **Nothing on this host** | **Page** | Nothing that lives here can detect that it did not execute. This is the one row that must be implemented in the customer's monitoring system, and [section 6](#6-what-watches-the-watchdog) gives the rule. |

---

## 4. How the watch decides

### 4.1 Why the health word is not the freshness signal

The obvious implementation of "alert when a connector stops" is to alert when
`crawl.vwConnectionHealth` returns `late`. **It does not work**, and it fails
silently in exactly the scenario the control exists for. Three reasons, all read
out of the `CASE` expression in `sql/22`, and the first of them measured against
a live database:

**1. `late` is unreachable without configuration.** The arm is gated on
`c.ExpectedIntervalMinutes IS NOT NULL`. Nothing requires that column to be
populated. On the estate this was developed against, `crawl.Connection` holds one
row — `consultingwork` — with `ExpectedIntervalMinutes` **NULL**, and the view
returns `healthy` for it. On that connection the word `late` cannot be produced
by any input at all. A watch keyed on it would have sat green through an
indefinitely dead scheduler.

**2. `failing` outranks `late`.** The `CASE` arms are a priority order. A
connection failing for a month reports `failing` and never reports `late`, so a
rule that pages only on lateness never sees the longest outages.

**3. `items refused` outranks `late` too.** A connection stuck `partial`
accumulates staleness while permanently displaying `items refused`.

So the watch computes freshness itself, from `minutesSinceLastSuccess` against
`expectedIntervalMinutes`, **independently of the health word**. It still reads
the word, still reports it, and still drives every other condition from it — the
watch never re-derives health, because `sql/22` owns that rule and a second copy
would be free to disagree with the dashboard on the one afternoon somebody is
comparing an alert to a page and cannot work out which is lying. But the word is
not the freshness signal, because by construction it cannot be.

The threshold is:

```
max(expectedIntervalMinutes * MissedIntervalFactor, MinimumStaleMinutes)
```

or `-MaxMinutesSinceSuccess` when no interval is configured. `sql/22` turns the
pill amber at **two** intervals; the watch pages at **three**, plus a
sixty-minute floor. Those are different jobs: two is right for "show me amber on
a screen", and a page at two fires on a single missed run — a patch reboot, one
throttled request — which is the fastest route to a muted watchdog.

### 4.2 HTTP 200 is not healthy

`HealthEndpoint.cs` answers **200 with an unhealthy body**, and reserves **503**
for "this process could not read crawl state". Its header explains why at
length: a failing connection and a dead dashboard must not arrive as the same
red, and a 503 for an unhealthy connection would also fire on planned
maintenance.

The consequence for anything consuming it is that **the status line carries
almost no information and the body carries all of it**. A check written as
"`Invoke-WebRequest`, no exception, pass" is blind to every condition the
endpoint exists to report. The watch therefore treats a 200 as permission to
parse and nothing more.

Three further traps are handled explicitly, because each one is green to a naive
check:

- **A 200 carrying HTML.** That is an ASP.NET error page or an authentication
  challenge, not health. Rejected before parsing.
- **A 200 carrying valid JSON that is not this contract.** The three fields the
  verdict depends on — `status`, `generatedUtc`, `connections` — must all be
  present. `HealthReport.cs` warns that a check which can no longer find its
  field usually evaluates to "not alerting" rather than to an error; this is the
  guard against that.
- **A 200 carrying a payload generated an hour ago.** The endpoint sends
  `Cache-Control: no-store` *specifically* so no intermediary can answer a
  monitor with an old verdict, and publishes `generatedUtc` *specifically* so a
  monitor can tell. Nothing was checking it. A cached green body is the single
  most dangerous response the watch can receive — it is green, well-formed, and
  a lie — so a payload older than `-MaxPayloadAgeMinutes` is refused rather than
  believed. The same test catches clock skew in either direction, which
  invalidates every freshness measurement and is worth knowing about for its own
  sake.

### 4.3 How it avoids flapping

Four separate mechanisms, and the important part is that **each applies only
where the underlying signal is genuinely instantaneous**:

| Mechanism | Applies to | Why |
|---|---|---|
| **Persistence, `-ConsecutivePolls`** (default 2) | `deletes pending`, `items refused` / `partial` | Both are normal for part of a normal run. A condition must be observed on two consecutive polls before it counts; the first sighting is reported as `HELD` and contributes nothing to the exit code. |
| **A trend threshold, `-FailuresToPage`** (default 2) | `failing` | One failure is an event, two is a trend. |
| **A floor, `-MinimumStaleMinutes`** (default 60) | Staleness | Without it a five-minute connection pages after fifteen minutes and one host reboot wakes somebody. |
| **A loose multiplier, `-MissedIntervalFactor`** (default 3) | Staleness | Leaves room for one missed run without a page. |

**The debounce is deliberately not applied to staleness, to never-succeeded, to
an unusable source, or to an empty estate.** A threshold measured in minutes is
already debounced by construction — it cannot fire on a blip — and adding a
second delay would only make the control slower at the one job it has.

That asymmetry is also what makes the error-side debounce *safe*. A connection
crawling weekly would take a fortnight to reach two consecutive failures; the
staleness check is not gated on the failure count and pages it on time
regardless. Every debounced condition has an undebounced backstop underneath it.

**When the state file cannot be read, debouncing switches off rather than
staying on.** Coverage is never reduced by a failure in the machinery that
exists to reduce noise. Losing the state file makes the watch noisier and raises
a ticket saying so.

### 4.4 Two sources, and no failover between them

The watch reads either `GET /health` or `crawl.vwConnectionHealth`.

Both exist because **the dashboard is optional**. `Install-Dashboard-IIS.ps1` is
a separate installation and an estate can run connectors without it; a watch that
only spoke HTTP could not be deployed there, and "the watchdog needs a web site
first" is exactly the prerequisite that ends with no watchdog installed at all.

They are not equivalent, and the difference decides which to choose:

| | Proves | Needs |
|---|---|---|
| **HTTP** (preferred) | IIS, the app pool, the dashboard process, the reader policy **and** crawl state, in one request | The watch identity in `CrawlState:ReaderGroups`, and HTTPS reachability |
| **SQL** | Crawl state is readable. Nothing else | `SELECT` on `crawl.vwConnectionHealth`, and TDS reachability |

HTTP is the stronger check and is used whenever `-HealthUrl` is supplied.

**There is deliberately no runtime failover from HTTP to SQL.** It is the
obvious convenience and it would destroy the control: a dashboard down for a
month would be papered over every fifteen minutes by the SQL path, and the
estate would believe it had a working `/health`. The source is selected once,
from the parameters. If the selected source cannot answer, that is the alert.

---

## 5. Exit codes and event IDs

### 5.1 Exit codes

A scheduled task's Last Run Result is the cheapest alerting integration Windows
offers, so the codes are chosen to be **monotonic in severity**. That is the
whole design: it means an operator writes two rules and neither has to enumerate
anything.

| Code | Means | Page it as |
|---|---|---|
| `0` | Asked, answered, every enabled connection inside its freshness threshold | Nothing. This is also the heartbeat — see [section 6](#6-what-watches-the-watchdog) |
| `1` | Ticket-level findings only | Working hours |
| `2` | Page-level findings | Now |
| `3` | The source could not be read. **The question was not answered** | Now. Not "the monitor is broken" — see [section 3.3](#33-conditions-about-the-watch-itself) |
| `4` | The watch itself could not run | Now, and to whoever owns the watch rather than the connector |

**The two rules are `!= 0` means look, and `>= 2` means page.** Nothing else
needs to be encoded anywhere.

These sit deliberately alongside the connector's own codes documented in
[`RUNBOOK.md`](RUNBOOK.md) (`0` clean, `2` configuration, `3` credential, `4`
ingestion) — they are a different program's vocabulary and are not
interchangeable. Read them against the task name that produced them.

### 5.2 Event IDs

Written to the **Application** log under source **`ConnectorState`**.

**Why the Event Log rather than a new channel.** Item 8 asks for the
alert-worthy events to reach people; adding a fourth destination would have made
that worse. The Event Log is already this system's convergence point — `sql/27`
and `sql/32` register their Agent jobs with `@notify_level_eventlog = 2`, and the
Serilog Event Log sink is already a packaged dependency of the connector. It is
also the one sink every Windows monitoring agent subscribes to with no bespoke
integration: SCOM, Zabbix, the Azure Monitor agent, Datadog and NSClient++ all
read event logs out of the box. So the matrix in section 3 is implemented as
*subscription rules in one place* rather than as five integrations.

**An event ID is a contract.** A monitoring rule is written against the number,
and a number reused for a different meaning breaks that rule silently. Add new
IDs; never redefine these.

| ID | Level | Meaning | Rule to write |
|---|---|---|---|
| `9000` | Information | Clean run. **The heartbeat** | Alert on its **absence** — see below |
| `9001` | Warning | Ticket-level findings | Raise a ticket |
| `9002` | Error | Page-level findings | Page |
| `9003` | Error | Source unreachable or unusable | Page |
| `9004` | Error | The watch itself failed | Page |

**`9000` is emitted on every clean run, and it is not noise.** It is the only
thing that makes a dead watchdog detectable. A watchdog that speaks only when
something is wrong is indistinguishable, from outside, from one that has been
disabled, deleted, or is on a host that is switched off.

If the event source is not registered, the watch **does not fail and does not go
quiet**: it prints a warning naming the missing registration and the exit code
continues to carry the full signal. Registering the source needs elevation and is
done once by `-Register`.

---

## 6. What watches the watchdog

**Nothing in this repository, and nothing that lives on the watched host.** That
is the honest answer, and stating it plainly is more useful than a mechanism that
appears to close the loop and does not.

The failure mode is "this code did not run". If the task is disabled, deleted, or
the host is powered off, there is no instruction left executing to report it. No
amount of on-box cleverness changes that; a second watchdog watching the first
just moves the question up one level.

Three things close as much of it as can be closed from here:

**1. The heartbeat.** Event `9000` on every clean run, plus `lastRunUtc` in the
state file, makes the *absence* of a recent heartbeat detectable. This is the
rule to write in the monitoring system, and it is the single most important
configuration step on this page:

> Alert if no event with ID `9000`, `9001`, `9002`, `9003` or `9004` from source
> `ConnectorState` has appeared in the Application log on the connector host
> within **three times the watch interval**.

Note that it keys on *any* of the five, not on `9000` alone. A watch that is
running and correctly reporting a page every five minutes is alive, and a
dead-man rule that only accepts `9000` would fire a second, confusing alert
alongside the real one.

**2. The scheduled task's own record.** Task Scheduler keeps `LastTaskResult` and
`LastRunTime`, and `Get-ScheduledTaskInfo` reads them without elevation, so "the
task exists, is enabled, and ran recently" is a second cheap check an agent can
run. The same check covers `Test-TriggerHealth.ps1` on estates with no SQL Agent,
whose Last Run Result is otherwise routed nowhere.

**3. The gap, stated rather than papered over.** If the host is powered off,
neither of the above is readable *from that host*. Only something off-box closes
it: the dead-man rule above evaluated in a monitoring system that is not on this
machine, or Windows Event Forwarding to a collector, or a plain host-level ping.
**Running the watch on a different host from the connector converts a total
failure into a detected one** — it is the strongest single argument for not
co-locating them, and it costs only the reader-group membership.

There is a fourth, deliberately rejected option: having the watch phone
somewhere on every run. It replaces one silent dependency with two, and the thing
it phones needs its own watchdog.

---

## 7. Installing it

### 7.1 The watch

Run once from an **elevated** PowerShell on the host that will carry the watch.
This registers the event source, creates the state directory, and registers the
scheduled task:

```powershell
.\Watch-ConnectorHealth.ps1 -Register `
    -HealthUrl https://sqlprod01:8443/health `
    -EveryMinutes 15
```

Or, for an estate with no dashboard:

```powershell
.\Watch-ConnectorHealth.ps1 -Register -Source Sql -SqlInstance SQLPROD01
```

`-Register` **does not also perform a watch**. A privileged state-changing
operation and an unattended read-only check have no business sharing an exit
code: a registration typo returning `0` would read as a clean estate, and a
genuine page would read as a failed install.

Registration is folded into the watch script rather than living in a separate
`Register-HealthWatch.ps1` for one reason — the task's argument list *is* the
watch's parameter list. Split across two files they drift in the direction
hardest to notice: somebody tunes a threshold, believes the task changed, and the
task goes on passing the number the other file hard-coded. Generated from the
same `param()` block the watch reads, they cannot. The registration prints the
command line it produced, so it can be read back and diffed against the task.

**Every threshold is written into the task's arguments explicitly, including the
ones left at their defaults.** A task naming only the overrides changes behaviour
silently the day somebody edits a default in the script, and the change is
invisible in the task definition an operator reads.

**The identity.** The script asks for no password anywhere — it runs unattended,
so `Read-Host` and `Get-Credential` are not available to it — and therefore
registers only identities that need none: `SYSTEM` (the default), `NETWORK
SERVICE`, `LOCAL SERVICE`, and group managed service accounts. `SYSTEM` is a
better answer than it first looks: it authenticates on the network as the
machine account `DOMAIN\HOSTNAME$`, which can be added to
`CrawlState:ReaderGroups` and granted `SELECT` like any other principal, and it
has no password to rotate or to leak into a task definition. For an ordinary
domain account the script prints the `schtasks /RP` command line to run by hand
rather than half-registering a task that cannot start.

Two steps remain that the script cannot do:

1. **Grant the watch identity read access** — membership of
   `CrawlState:ReaderGroups` for the HTTP path, or `SELECT` on
   `crawl.vwConnectionHealth` for the SQL path.
2. **Write the dead-man rule** from [section 6](#6-what-watches-the-watchdog).

Then run the watch once by hand and confirm the event appears. A newly created
event source can take a few seconds to become writable and the first write after
creation is occasionally lost, so confirm rather than assume.

### 7.2 The two rules that are not about this script

These route the signals from [section 3.2](#32-conditions-the-watch-reports-on-but-does-not-observe-directly) that
already exist and are simply unsubscribed:

- **SQL Agent job failures.** `sql/27` and `sql/32` already write to the
  Application log with `@notify_level_eventlog = 2`. Subscribe to Error-level
  events from the SQL Agent provider whose message names
  `ConnectorState - purge crawl history` or `Ops - timesheet trigger health`.
  Ticket severity.
- **`Test-TriggerHealth.ps1`'s Last Run Result**, on estates with no SQL Agent.
  Ticket severity, and see [section 6](#6-what-watches-the-watchdog) for the
  shape of a task-result rule.

---

## 8. Tuning, and what to do when it is too noisy

A watchdog that is muted is worse than one that was never installed, because it
still looks installed. If this one is producing noise, change a threshold —
**do not disable the task and do not suppress the rule.**

| Symptom | Change | Do not |
|---|---|---|
| Pages on a single missed run | Raise `-MissedIntervalFactor` or `-MinimumStaleMinutes` | Stop paging on `Stale`. It is the only detector for the failure that matters most |
| Constant `NoExpectedInterval` tickets | Set `ExpectedIntervalMinutes` in `crawl.Connection` — the correct fix, and it also restores the database's own `late` detection | Raise `-MaxMinutesSinceSuccess` to hide it |
| `deletes pending` tickets on every sweep | Raise `-ConsecutivePolls`, or lengthen `-EveryMinutes` so polls are less likely to land mid-sweep | Stop reading them. A persistent pending delete is an item the source dropped, still answering searches |
| Pages on one transient Graph failure | Raise `-FailuresToPage` | Raise it above 3 without also tightening the staleness threshold — the staleness check is what makes the failure debounce safe |
| Tickets for a connection nobody owns yet | Disable the connection in `crawl.Connection` | Leave it enabled and ignore the tickets |

A note on that last row: **enabling a connection is the act that puts it under
this watch.** A connection that is registered but not yet ready should be
disabled, which is reported every run and alerts on nothing.

---

## 9. What this does not cover

Stated plainly, because a monitoring page that implies more coverage than it has
is worse than one that admits its edges:

- **It does not detect a connector that is running and pushing wrong data.**
  Every check here is about freshness and failure. A crawl that succeeds against
  a source view that has silently started returning half the rows looks perfect
  from here. That is what `deploy/Compare-SourceToIndex.ps1` is for, and
  scheduling it is item 7 of section 7 — a separate piece of work.
- **It does not detect the delete guard at the moment it fires**, only the failed
  run afterwards, and it cannot distinguish the guard from any other
  `InvalidOperationException`. See [section 3.2](#32-conditions-the-watch-reports-on-but-does-not-observe-directly).
- **It does not watch certificate or client-secret expiry.** That is item 4 of
  section 7, and nothing currently watches the client secret's expiry at all.
- **It does not detect two instances crawling one connection concurrently.**
  That is item 2, the single-instance run lock, and it is a defect this watch
  would report only as whatever damage resulted.
- **It cannot detect its own non-execution.** [Section 6](#6-what-watches-the-watchdog),
  and it is the reason that section exists.
