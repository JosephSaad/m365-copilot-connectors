---
title: Go-live readiness
description: Every feature in the direct-push path, what is built and what is not, and the six verification tasks that stand between the current release and a supported service.
---

# Go-live readiness

Current release: **v1.3.1**. This document is the state of the direct-push path
as a whole — what exists, what does not, and what has to happen before it is a
supported service rather than a release that builds.

It is deliberately blunt about the gap between *built* and *verified*, because
that gap is the whole risk right now: 17 features are implemented, 4 more are
part-built, 312 tests pass, and **five of the six blockers are closed, with the
sixth part-done** — against none at all a few days ago. Every blocker that ran
found something. Blocker 1 found two defects in
`sql/26`, one of them silent. Blockers 2 and 5 found the shared ACL that wrote
441 of 1,118 items and refused the other 677. None was findable by reading the
code; all are fixed. The full lifecycle now has evidence behind it — write,
skip-unchanged, delete confirmed gone from the index, and every dashboard page
rendering it under Windows authentication. What remains is the three caveats on
row 1, and the parts no quiet tenant can prove: the engine's backoff has still
never answered a real 429, and no page has yet rendered a failed run. Every
go-live blocker below is a verification task. None of them is construction.

**What this document does not cover.** Every task here is engineering: run it,
watch it, prove it. None of it establishes who owns the connection, who is woken
when a run fails, or who accepts the ACL staleness bound in writing — that is
[`PRODUCTION-ONBOARDING.md`](PRODUCTION-ONBOARDING.md), and clearing all six
blockers below does not answer a single row of it.

**How to read the tables.** Status is ✅ implemented, ⚠️ partial, ❌ not built.
In the feature tables ⚠️ means the plumbing exists but nothing calls it; in the
blocker table it means the task has been run, but not in a form that closes it,
and the row says why. The priority band is the section heading: blockers first,
then what is cheap now and expensive later, then what can follow go-live.

---

## 1. The six blockers

They are blockers because each one is a claim the release makes and cannot yet
support. Five are now closed and row 1 is part-done; every one of the six has
been attempted. What follows is how to build the machine they were run on, kept
in full, because the rig has to be rebuilt for the customer environment anyway.

**Crawl state is now enabled, and rows 2 and 3 closed with it.** Every run before
that logged "No crawl state store configured", wrote all 1,118 items each time,
and recorded nothing — which is also why the dashboard read empty. It was
reporting an empty database accurately. With the store enabled, three runs read
1,117 / 1,118 / 1,119 and wrote 1,117 / 1 / 3, in 77s / 10s / 3s.

Note *where* the connection string is: the `bin` copy only, never a tracked
`appsettings.json`. The secret-hygiene scan rejects that key by name and excludes
`bin/**`, so this touches no gate — but a rebuild wipes it, which makes it a
demonstration rather than a deployment. Section 3 carries the permanent decision.

**Two things the delete rehearsal settled that are worth carrying into
monitoring.** `crawl.vwPendingDeletes` read 0 before the sweep and 0 after:
detect, delete and confirm all happen inside one sweep, so a healthy run never
leaves anything there. It is a failure indicator, not a work queue — which is
what [`CRAWL-STATE-DEPLOYMENT.md`](CRAWL-STATE-DEPLOYMENT.md) already says, now
observed. An alert built expecting that view to be busy will only ever fire when
something is wrong, which is correct, and will look broken until it does.

**The dashboard also gave the ACL fix its first behavioural evidence.** The
inventory page shows one ACL hash, `94B60AC534B3`, identical on every row. The
regression test for that defect is a source scan and says in its own header that
it cannot prove the behaviour — no harness here reproduces the field being
dropped. A column of identical hashes across 1,119 items does prove it: the
shared-object regression is exactly what would make them diverge. Worth knowing
that the check exists and where it lives, because it is not in the test suite.

And the sweep runs on a **full** crawl only. All four runs were full because
`Settings:Incremental` was never set. Turn it on and the sweep does not stop
firing, but it stops firing *every run*: `uspBeginRun` escalates to full once the
last full success ages past `Settings:FullEveryHours`, 168 by default, so
deletions arrive weekly rather than per run. That is the same number
[`PRODUCTION-ONBOARDING.md`](PRODUCTION-ONBOARDING.md) row 1.1 asks somebody to
accept in writing, and this is what it buys.

| # | Feature | What it means | Status |
|---|---|---|---|
| 1 | **SQL scripts executed** | Run `sql/20`–`25` against a real instance and read the verification block each one prints. A syntax error in a `CREATE OR ALTER` batch would leave a procedure absent and only fail later, at the `GRANT`. **Partially done.** `sql/02`, `10`–`13`, `20`–`26` have now been run once against a scratch SQL Server 2025 instance, and `ConnectorState` built out — 8 tables, 6 views, 19 write and 7 reporting procedures. It paid for itself immediately: `sql/26` carried two defects, one of them silent, both fixed in `eb94ab1`. It stays open on three counts. `sql/20` ran from an edited copy, not the repo file, its `D:` paths being placeholders. `sql/01`, `13` and `25` created no principals at all — their `CONTOSO\` logins cannot exist on that machine, and local accounts stood in, so the least-privilege model is deployed but has never been exercised by the accounts it is written for. And the run is reported rather than witnessed. Re-run it where the accounts are real | ⚠️ |
| 2 | **Live tenant pilot run** | One full crawl of the timesheet fixture against a real connection. Validates the retry removal, `$batch`, hashing and the state store in a single pass, and produces the first attribution table anyone has seen. **Done**, and it validated three of the four: `$batch` (row 5), hashing and the state store (row 3). Attribution tables exist — the first measurements of this pipeline that are not a guess. One item failed in run 1 and *run 2 rewrote it without any source change*, because the store had never recorded a confirmation for it. That is the "record the hash only after Graph confirms" rule working, observed rather than argued. **The retry removal remains unproven**: `throttleWaits=0` across every run, so the engine-owned backoff has still never answered a real 429 | ✅ |
| 3 | **Second-run validation** | Re-run immediately and check `UnchangedPercent` in `crawl.vwRunHistory` climbs. Stuck near zero means item IDs are not deterministic and the corpus is being rewritten every run — see [`SOURCE-CONTRACT.md`](SOURCE-CONTRACT.md). **Done, and it climbed to 99.9%.** Run 2 read 1,118 and wrote 1; run 3, after one time entry was inserted, read 1,119 and wrote 3. The item IDs are deterministic and the corpus is not being rewritten. The 3 is the part worth reading twice: the inserted entry, its parent engagement and its grandparent customer, because the `sql/12` views roll `TotalHours` and `ChildCount` upward, so one insert genuinely changes three items. The engine knows nothing of the hierarchy — it hashed all 1,119 and found the three that differed. Wall clock fell 77s → 10s → 3s as the work shrank to what had actually changed | ✅ |
| 4 | **Delete detection rehearsal** | Remove a fixture row, run a full crawl, watch the sweep remove it from the index. **Done, and verified on three surfaces with a control.** One time entry soft-deleted at the source, so it fell out of the views; run 4 read 1,118, logged "Delete sweep: 1 item(s) the source no longer returns", and reported `deleted=1`. After it: `crawl.Item` state 3 with `PendingSinceUtc` NULL, and the item **404 in the Graph index** — the proof that matters, since the other two are the connector agreeing with itself. The control item beside it was untouched, and the 2 rewrites were the parent engagement and grandparent customer, the rollups changing back exactly as they changed in run 3. The guard was never near firing: 1 of 1,119 is 0.09% against `MaxDeletePercent` 10 | ✅ |
| 5 | **`$batch` live validation** | **Done.** The default write path has now spoken to Graph: 1,118 items in 56 batches, zero failures, at 8 writers and again at 16. It did not pass first time — the run it was meant to validate is the one that exposed the shared-ACL defect fixed in `44e464f`, writing 441 and refusing 677, which is the whole argument for this row existing. One clause of it is still untested: `Settings:Batch = false` is named here as the rehearsed fallback and has not actually been rehearsed. Also note `throttleWaits=0` throughout, so the backoff path is unexercised — 16 writers × 20 sub-requests is nominally far above the 25 concurrent operations per connection the clamp's own warning cites, and a quiet tenant is not evidence that a busy one will agree | ✅ |
| 6 | **Dashboard smoke test** | Deploy to IIS, confirm Windows authentication and that all seven pages render against real rows. **Done.** Anonymous requests get `401` with IIS advertising `Negotiate` and `NTLM`, so Anonymous is off and the fallback policy in `Program.cs` is holding — there is no anonymous page. All seven routes render under an authenticated identity against four runs and 1,119 items. **Five show rows; two are empty and correctly so**: Throttling, because nothing was throttled across 1,123 writes, and Pending deletes, because a healthy sweep leaves nothing there. Both say so on the page rather than looking broken. What has *not* been seen is either page carrying a row, and no page has yet rendered a failed run, an unhealthy connection, or the "needs attention" banner — the states an operator actually reads during an incident are the ones still unexercised | ✅ |

### Doing all six on one Windows machine

A single Windows 11 box hosting both the code and SQL Server clears every one of
them, and is a better rig than a Mac: it builds the gRPC connector project
(`Grpc.Tools` ships an x64 `protoc`, so all 312 tests run rather than 239), and
it is closer to the production Windows Server than anything that has touched
this code.

**Install:** SQL Server 2022 Developer Edition (free; not Express — you want SQL
Agent to schedule `crawl.uspPurgeHistory`, which `sql/23` defines and `sql/25`
withholds from both roles; nothing in the repository ships the job itself, which
is the ⚠️ row in section 3), the .NET SDK, and IIS with the Windows
Authentication feature plus the ASP.NET Core Hosting Bundle. The dashboard
*requires* IIS's authentication handler; under bare Kestrel it returns 500 on
every page by design. The Entra app certificate goes in the certificate store
per [`APP-REGISTRATION.md`](APP-REGISTRATION.md), and the machine needs outbound
443 to `login.microsoftonline.com` and `graph.microsoft.com`.

**Three edits before running anything.** These are placeholders that fail loudly
rather than silently, which is the intended behaviour:

- `sql/20` — the `D:\SQLData` and `D:\SQLLogs` file paths. Edit them, or delete
  the `ON PRIMARY` and `LOG ON` clauses to accept the instance defaults.
- `sql/25` — the two domain accounts are placeholders. On a workgroup machine
  use local principals: your own account for `crawl_writer`, and the IIS
  application pool identity for `crawl_reader`.
- Run `sql/10`–`13` first so the fixture is on the same instance.

**Order**, mapped to the blockers above: fixture and state database (1) → dry
run, then a real run with `Settings:StateConnectionString` set (2, 5) → immediate
re-run and read `UnchangedPercent` (3) → delete a row and run again (4) →
publish the dashboard to IIS (6). Optionally then `sql/26`, rename a fixture
customer, and confirm every descendant's `EffectiveLastModified` moves.

This order has now been walked end to end on one machine. What it did not
produce is a *failure*: every run succeeded, so no page has rendered a failed
run, an unhealthy connection or the "needs attention" banner, and those are the
views an operator reads when something is wrong rather than when it is right.
Setting `Settings:MaxDeletePercent` to 0 and sweeping with one row missing trips
`THROW 50007` and exercises all three cheaply. It leaves a real failed run in the
history, which `crawl.uspPurgeHistory` ages out at 90 days — except that nothing
schedules that job yet, so on this rig it stays until someone removes it.

**What this rig cannot prove**, so the pilot's scope stays honest: production
scale, the locked-down network path (`Settings:GraphProxy` against a real
proxy), domain accounts rather than local ones, IIS on Windows Server rather
than on 11, TLS trust (the rig runs `Environment: Development` because
`SqlConnectionStringFactory` rejects `TrustServerCertificate=true` in
Production, so no certificate has ever been validated), and throttling headroom
— the pilot saw `throttleWaits=0` at 16 writers × 20 sub-requests, nominally far
above the 25 concurrent operations per connection the clamp warns about, which
says something about that tenant on that day and nothing about a busy one. Those
six carry forward to the customer environment.

---

## 2. Implemented and shipped

The foundation. All of it rides on the verification above.

| Feature | Description | Status |
|---|---|---|
| Push engine | Connection and schema registration, foreign-connection guard, truncation, ACL resolution, upsert writes, documented exit codes | ✅ |
| Retry and throttling | Engine-owned backoff honouring `Retry-After`; the SDK's hidden retry handler is removed and its absence pinned by a test | ✅ |
| Concurrent writers | `Settings:Writers`, default 4, clamped to 16 because Graph allows 25 concurrent operations per connection. Forced to 1 for any source that keeps a position | ✅ |
| `$batch` writing | Twenty per request, capped by accumulated content bytes, with per-item outcomes so one refusal does not abandon the other nineteen | ✅ |
| Change detection | Content and ACL hashed separately and both compared; an unchanged item is skipped but still marked seen | ✅ |
| Delete detection | Inventory diff after a full crawl. The source is never asked whether a record was deleted and needs no soft-delete column | ✅ |
| Delete guard | Refuses a sweep after an incremental run outright, and one that would remove more than `Settings:MaxDeletePercent` of the live corpus | ✅ |
| Checkpointing | Composite `(marker, id)` marker, forward-only, frozen for the rest of a run once anything is refused so it cannot pass a gap | ✅ |
| Duplicate detection | Per-run identifier set held on the reading thread, counted and logged | ✅ |
| Run history | Per connection, per run, per item type, with the timing attribution and raw throttling events persisted | ✅ |
| Dashboard | Seven read-only Razor Pages, every list paged in the database, two roles that share no permission | ✅ |
| Throttle telemetry | Every 429 and 5xx buffered with its real timestamp and flushed once at run close, never on the hot path | ✅ |
| Timing attribution | p50/p95/p99/max per phase with a verdict that states the precondition it rests on | ✅ |
| Safe degradation | No `Settings:StateConnectionString` gives exactly the pre-v1.3 behaviour. A connection string carrying a password is refused | ✅ |
| Single controlled egress | `Settings:GraphProxy` forces all Graph traffic through one proxy | ✅ |
| Logging redaction | No item content or property values in logs; enforced by a tripwire test over every source file | ✅ |
| Dry run | Schema and mapping proven with no writes and no state recorded | ✅ |

---

## 3. Recommended before go-live

Cheap to do now, materially more expensive afterwards. None blocks a pilot.

| Feature | Description | Status |
|---|---|---|
| **Hash version stamp** | A version recorded beside each stored hash, so a future change to the hash framing is a detected migration rather than a silent overnight rewrite of the whole corpus | ❌ |
| **`StateConnectionString` given a home** | The setting that enables everything rows 2 and 3 proved currently lives in the `bin` copy of `appsettings.json`, which a rebuild deletes. It cannot live in a tracked one: `SecretHygiene.targets` rejects any key matching `connectionstring`, and rightly. Three ways out, in the order I would take them — deploy from a published folder outside the repository, where no build-time scan reaches and the shipped placeholder stays clean; or build the rig with the documented `-p:SkipAppSettingsSecretScan=true`; or add the key to `AppSettingsSecretScanAllowedPaths`, which permanently widens a shipped control and should be paired with a startup check, as the `Auth:ClientSecretCredentialTarget` precedent in that file says. `CrawlStateWiring` already refuses a value containing a password, though by substring rather than by parsing — see the last row of section 4 | ❌ |
| **CI integration job** | LocalDB on the Windows runner executing `sql/20`–`25` and driving the state store end to end, making blocker 1 permanent instead of a one-off | ❌ |
| **Dashboard authorisation** | Group membership, not merely authentication. Any authenticated user can currently read crawl metadata | ❌ |
| **Run identifier in logs** | Stamp the run identifier on the logging context so a log file correlates to a dashboard row without a timestamp hunt | ❌ |
| **Retention scheduled** | A deployable SQL Agent job for `crawl.uspPurgeHistory`. The procedure and its arguments are documented; nothing ships the job | ⚠️ |
| **Control evidence registered** | Pin the hash determinism, ordered-commit and retry-handler tests in the protected list, so they cannot be renamed or deleted without failing the control check | ❌ |
| **Drift detection updated** | Rewrite `deploy/Compare-SourceToIndex.ps1` against `crawl.vwItemInventory`. It predates the inventory and still reconstructs by re-reading the source | ❌ |
| **Concurrency × batching test** | Several writers each flushing batches, with a mid-batch refusal. The one interaction no test currently pins | ❌ |
| **Delete threshold agreed** | `Settings:MaxDeletePercent` defaults to 10. Set it against the source's real daily deletion volume — a configuration decision, not code | ❌ |
| **Security evidence script** | One script running every control verification query, so the review is reproducible rather than a list of queries to paste | ❌ |

---

## 4. After go-live

| Feature | Description | Status |
|---|---|---|
| Incremental reads, connector side | The engine plumbs the resume marker and the change-detection tier, but no connector reads the marker yet. `sql/26` is not merely unreached by the shipped binaries — it is **incompatible with them**: `HierarchyPushConnector` emits an explicit 30-column `SELECT` and `vwExternalItemsIncremental` projects 12, so pointing `Source:ItemView` at it fails on 19 invalid column names, `LastModified` among them — the view names that column `EffectiveLastModified`. Column parity alone would not finish it: the query orders by item type then id, not the ascending `(marker, id)` a `ChangeMarker` source owes [`SOURCE-CONTRACT.md`](SOURCE-CONTRACT.md). Unnecessary at pilot scale; the lever that matters at production scale | ⚠️ |
| Hierarchy-aware timestamp deployed | `sql/26` alters the source tables and installs cascading triggers. Needs the source team's agreement; differencing works without it | ⚠️ |
| Identity cache wired | The principal cache table and store methods exist, but nothing calls them — the CDP resolver still resolves in memory each run | ⚠️ |
| Single flush procedure | Fold the per-chunk round trips into one server-side compare. The next throughput lever after batching | ❌ |
| Larger lookup chunks | Decouple the state-lookup size from the batch size, cutting store round trips roughly tenfold | ❌ |
| Overlapped store reads | Prefetch the next chunk's hashes while the current chunk writes | ❌ |
| Batched deletions | The sweep still deletes one item at a time; the batch writer already exists | ❌ |
| Dry-run preview | Consult the state store on a dry run to report what *would* be written, skipped and deleted — the delete sweep previewed before it happens | ❌ |
| Health endpoint | A machine-readable projection of connection health for monitoring, rather than a page for people | ❌ |
| Trigger health check | Scheduled run of `sql/26`'s verification query; nothing detects a disabled trigger, and the source keeps accepting writes while the column stops moving | ❌ |
| Negative TTL enforced | The shorter time-to-live for an unresolvable principal is a caller convention the database cannot see | ❌ |
| Batch envelope tuning | The client-side batch byte cap is deliberately conservative and should be raised against measured behaviour | ❌ |
| Sargable incremental view | Project the numeric keys from the item views so the incremental view joins on them rather than on a constructed string | ❌ |
| Central package management | One file pinning package versions for every project, removing the class of split-version failure that has already broken CI twice | ❌ |
| Identity library unification | Collapse the pre-existing split that carries eight packages at two versions in the offline restore set | ❌ |
| Harness fixture generation | Generate the local test harness's fixture copies rather than hand-syncing them | ❌ |
| Release automation | Encode the publish-then-delete-previous release sequence as a workflow rather than manual steps | ❌ |
| Strict hasher fallback | Throw on a property type the hasher does not recognise instead of falling back to a string conversion | ❌ |
| Parsed connection-string check | Parse rather than substring-match when refusing a password in the state connection string | ❌ |
| Per-type duplicate counts | The run table counts duplicates; the per-item-type breakdown does not | ❌ |
| Engine file split | Separate the run lifecycle, the chunk flush and the delete sweep into their own files | ❌ |

---

## 5. What is not in this repository

Sample data throughout this repository is fictional — Contoso, Northwind and
Consultco names, `corp.example` hosts. Nothing here describes a real customer's
cluster, tenant, or data.

Customer-specific material is held outside the repository by design and is not
published here: environment-specific findings and their remediation status,
measurements taken in a customer environment, cluster and tenant configuration,
and anything naming a real organisation, host or directory object. Where such
analysis has driven a change in this repository, the *change* appears here —
with its control identifier and its proving test — while the analysis does not.

The security controls in [`SECURITY.md`](SECURITY.md) are the public,
reviewable form of that work.
