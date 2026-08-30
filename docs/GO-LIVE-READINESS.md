---
title: Go-live readiness
description: Every feature in the direct-push path, what is built and what is not, and the six verification tasks that stand between the current release and a supported service.
---

# Go-live readiness

Current release: **v1.4.0**. This document is the state of the direct-push path
as a whole — what exists, what does not, and what has to happen before it is a
supported service rather than a release that builds.

It is deliberately blunt about the gap between *built* and *verified*, because
that gap is the whole risk right now: 17 features are implemented, 3 more are
part-built, 407 tests pass, **five of the six blockers are closed with the sixth
part-done**, and **ten of the eleven pre-go-live recommendations are done** — the
eleventh being the one nobody but the customer can close. **Nine of the eleven live tests now pass**; the two that do not need a machine this one is not — domain principals, and a D: volume. Of the seventeen shipped features, twelve are verified against a live tenant, two partly, and three have never been exercised at all. Every blocker that ran
found something. Blocker 1 found two defects in
`sql/26`, one of them silent. Blockers 2 and 5 found the shared ACL that wrote
441 of 1,118 items and refused the other 677. None was findable by reading the
code; all are fixed. The full lifecycle now has evidence behind it — write,
skip-unchanged, delete confirmed gone from the index, and every dashboard page
rendering it under Windows authentication. What remains is the three caveats on
row 1. The backoff has now answered a real 429, which no fixture of 1,118 items
could ever have made it do: a 100-fold corpus made the tenant push back, and the
first throttled run in this project's history lost 191 items on the spot, to a defect nothing smaller could reach. Those 191 are back: run 21 read all 111,900 rows, rewrote exactly those items in 41 seconds, refused none, and Graph now returns all 191 with their ACLs intact. A run that completes with refusals is no longer filed as a success. The failure views have now been seen — a deliberate
refusal put the connection into `failing`, raised the attention banner and left
three failed runs in the history, and a clean run put it back. Every go-live
blocker below is a verification task. None of them is construction.

**A deployment defect worth naming separately**, because nothing in the code
review or the test suite could have reached it. SQL Server stores
`QUOTED_IDENTIFIER` *with each module*, as it stood in the session that created
it, and replays that stored value on every execution whatever the caller sets.
sqlcmd connects with it off; SSMS connects with it on. `crawl.Item` carries a
filtered index, so every procedure deployed from a command line was refused at
execution — `uspBeginRun` threw error 1934 and no crawl could open a run. The
scripts had only ever been deployed from a query window, so the documented
command-line path had never once produced a working database. All six
module-creating scripts now set the option, and `sql/30` fails a deployment that
produces a module without it.

The same shape turned up once more in `deploy/GraphPushAuth.ps1`. Its Credential
Manager reader passed `-UsingNamespace` to `Add-Type` for a namespace `Add-Type`
already supplies — a duplicate using directive, which Windows PowerShell 5.1
treats as warning-as-error and Roslyn rejects outright. It failed on every
supported shell, so `Get-StoredClientSecret` had always fallen through to
prompting, and every pre-flight that claimed to test the stored secret was
testing whichever one the operator typed. That is the failure its own
documentation says it exists to catch.

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

**Status and Live Test Status are different claims**, and section 3 carries both
because the gap between them is where this project keeps finding defects. Status
says the thing is built and the suite covers it. Live Test Status says it has
been run against a real server. Two rows in that table read ✅ and *"passed,
after a fix"* — both were built, both had green suites, and both failed the first
time a server saw them.

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

And the sweep runs on a **full** crawl only — but not for the reason this
paragraph gave before L10 was run. It said that turning `Settings:Incremental`
on would make deletions arrive weekly, at `Settings:FullEveryHours`. It does not,
today: setting it changes nothing at all. No shipped connector implements the
`ChangeMarker` tier, so `PushItem.LastModifiedUtc` is never set and no checkpoint
is ever saved, and `uspBeginRun` escalates *every* incremental request with no
checkpoint straight back to full. Two runs with the flag on both recorded
`Mode = 1`, and `crawl.Checkpoint` is still empty.

So the sweep currently fires on every run, and the ACL staleness bound
[`PRODUCTION-ONBOARDING.md`](PRODUCTION-ONBOARDING.md) row 1.1 asks somebody to
accept is better than the 168 hours that row quotes. It becomes exactly 168 the
day a connector reads the marker, and that is the day to revisit the number
rather than now.

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
(`Grpc.Tools` ships an x64 `protoc`, so all 407 tests run rather than 239), and
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

| Feature | Description | Status | Live Test Status |
|---|---|---|---|
| Push engine | Connection and schema registration, foreign-connection guard, truncation, ACL resolution, upsert writes, documented exit codes | ✅ | **PASSED, in full.** Registration, schema, ACL resolution and upsert writes exercised across 24 runs and 110,590 items, with exit 0 and exit 4 both observed. The two clauses that had never fired now have: **truncation** — customer 1's notes were enlarged to 4,000,561 bytes, and the run logged "content truncated from 4000561 to 3670016 bytes", wrote exactly 3,670,016, kept the head marker, dropped the tail marker and ended with the visible notice; the source was then restored byte-for-byte and re-crawled to `truncated=0`. **Foreign-connection guard** — a dry run pointed at `sqltickets`, a connection this connector does not own, refused with "properties ticketId, assignedTo do not exist in this connector's schema", exited 4 and wrote nothing |
| Retry and throttling | Engine-owned backoff honouring `Retry-After`; the SDK's hidden retry handler is removed and its absence pinned by a test | ✅ | **PASSED, and it found a defect.** The 100-fold run was the first ever throttled: 191 sub-requests took a 429 with `Retry-After: 10`, and the engine backed off and retried each. That retry then refused all 191 with `400 NullOrEmptyValue` — a retried item was being re-serialized from the same instance and losing its ACL. Fixed in `af3dc6e`; the backoff itself behaved exactly as written. **The 191 have since been recovered** — run 21 read all 111,900 rows, rewrote exactly those 191 in 41 seconds and refused none, and Graph now returns all 191 with their ACLs intact. Note run 21 was not itself throttled, so it re-proved the recovery path, not the retry fix; that remains covered by `BatchRetrySerializationTests` |
| Concurrent writers | `Settings:Writers`, default 4, clamped to 16 because Graph allows 25 concurrent operations per connection. Forced to 1 for any source that keeps a position | ✅ | **PASSED** — `Writers=99` logged "was 99; using 16", and the run used 16. "Forced to 1 for any source that keeps a position" is untested, no shipped source keeping one |
| `$batch` writing | Twenty per request, capped by accumulated content bytes, with per-item outcomes so one refusal does not abandon the other nineteen | ✅ | **PASSED** — 5,608 batches of 20 across 111,709 rows, with per-item outcomes proven the hard way: 191 refused individually while the rest of their batches landed. The 4 MiB byte cap has never been approached; the largest item is 808 bytes |
| Change detection | Content and ACL hashed separately and both compared; an unchanged item is skipped but still marked seen | ✅ | **PASSED at both scales** — 1,117 of 1,118 skipped on a second run, and at 100-fold the 1,119 pre-existing items were re-read, re-hashed and skipped with **zero** rewrites while 110,590 new ones were written |
| Delete detection | Inventory diff after a full crawl. The source is never asked whether a record was deleted and needs no soft-delete column | ✅ | **PASSED** — see blocker 4. The item left the index and Graph returned 404 for it |
| Delete guard | Refuses a sweep after an incremental run outright, and one that would remove more than `Settings:MaxDeletePercent` of the live corpus | ✅ | **PASSED** — tripped deliberately at `MaxDeletePercent = 0`: exit 4, nothing marked pending, the store unchanged. "Refuses a sweep after an incremental run outright" is untested because no incremental run can occur — see L10 |
| Checkpointing | Composite `(marker, id)` marker, forward-only, frozen for the rest of a run once anything is refused so it cannot pass a gap | ✅ | **NOT EXERCISED** — `crawl.Checkpoint` is empty after 20 runs and has never held a row. No shipped connector sets `PushItem.LastModifiedUtc`, so no marker is ever saved. Same root cause as L10 and the `sql/26` incompatibility |
| Duplicate detection | Per-run identifier set held on the reading thread, counted and logged | ✅ | **NOT EXERCISED** — `duplicates=0` on every run; no source has offered the same identifier twice |
| Run history | Per connection, per run, per item type, with the timing attribution and raw throttling events persisted | ✅ | **PASSED** — 21 runs with per-item-type breakdowns, phase-timing rows and the throttle events behind them. A run that completes with refusals is now recorded `partial` (status 5) rather than `succeeded`, and the connection reads `items refused` until a later run clears it — see `sql/29`. Run 20 is the one row in the history carrying it |
| Dashboard | Seven read-only Razor Pages, every list paged in the database, two roles that share no permission | ✅ | **PASSED** — all seven pages against real data (blocker 6), and the authorisation rule proven live (L4). Since extended with a **machine-readable `/health` endpoint** and **viewer-timezone rendering**: timestamps are converted individually with `ConvertTimeFromUtc` (never one cached offset, which would render a DST-spanning run inconsistently) and the zone is **named on the page**, because "14:32" is ambiguous and dangerous on a page about run timing. The zone is genuinely detected from the browser, which cost one `'sha256-'` CSP source rather than `script-src 'self'` — one script body, not every file in `wwwroot`. The UTC switch is a plain link and works with scripting disabled |
| Throttle telemetry | Every 429 and 5xx buffered with its real timestamp and flushed once at run close, never on the hot path | ✅ | **PASSED at scale** — 191 events buffered with their real timestamps, endpoint and attempt number, flushed once at run close. It also exposed a mislabelled tile: the figure counts 429s only while the label claimed 429 and 5xx, and this rig had a 504 on record under a tile reading zero |
| Timing attribution | p50/p95/p99/max per phase with a verdict that states the precondition it rests on | ✅ | **PASSED** — p50/p95/p99/max per phase over 111,709 rows, with the verdict stating its precondition: "191 of 111709 row(s) (0.2%) slept at least once; backoff is 0.0% of per-row time" |
| Safe degradation | No `Settings:StateConnectionString` gives exactly the pre-v1.3 behaviour. A connection string carrying a password is refused | ✅ | **PARTIAL** — runs before the store was configured wrote every item every time and deleted nothing, exactly as described. The other half, that a connection string carrying a password is refused, has not been tested |
| Single controlled egress | `Settings:GraphProxy` forces all Graph traffic through one proxy | ✅ | **NOT EXERCISED** — `Settings:GraphProxy` has never been set. Carried forward as one of the six things this rig cannot prove |
| Logging redaction | No item content or property values in logs; enforced by a tripwire test over every source file | ✅ | **PASSED at scale** — a 327 KB live log over ~16,000 written items searched for a real narrative fragment, a real customer note and any consultant email address: zero hits for all three |
| Dry run | Schema and mapping proven with no writes and no state recorded | ✅ | **PASSED** — runs 8 and 18 recorded no item state and no checkpoint. Note the run row still reports `ItemsWritten`, being what it *would* have written; the dashboard excludes dry runs from every average for that reason |

---

## 3. Recommended before go-live

Cheap to do now, materially more expensive afterwards. None blocks a pilot.

**Ten of the eleven are done.** The one left is `MaxDeletePercent`, which is a
number about the customer's source rather than anything in this repository — the
measurement that informs it now exists, the decision does not.

Three of the ten need something a build cannot give them, and are listed here so
they are not read as finished when they are only shipped: `sql/27` has to be
deployed to an instance with SQL Agent, `CrawlState:ReaderGroups` has to be given
real group names, and the published-folder route for `StateConnectionString` has
to be the one actually used. Each is a deployment step; §5 of this document
carries the live tests that confirm them.

| Feature | Description | Status | Live Test Status |
|---|---|---|---|
| **Hash version stamp** | A version recorded beside each stored hash, so a future change to the hash framing is a detected migration rather than a silent overnight rewrite of the whole corpus. **Done**, in `sql/28` and `ItemHasher.HashVersion` - recorded per connection rather than per item, because the hashes reach `crawl.Item` through a table type and SQL Server cannot ALTER one, and because within a connection the version is a property of the writer and not of the row. A change escalates the run to full and says so. What that gives up is gradual rehashing: a version change is one deliberate full rewrite | ✅ | **PASSED, end to end** — `sql/28` deployed to the live `ConnectorState`, all three checks OK, and the report-once contract confirmed against the procedure (0, 1, 0, 1). Then proven through a running connector: a scratch build at version 2 reported the migration and escalated to full, its second run said nothing, and rolling back to version 1 reported the downgrade once and then fell silent. See L11 for the one limit — only the constant moved, so this proves the detection and not the rewrite |
| **`StateConnectionString` given a home** | The setting that enables everything rows 2 and 3 proved currently lives in the `bin` copy of `appsettings.json`, which a rebuild deletes. It cannot live in a tracked one: `SecretHygiene.targets` rejects any key matching `connectionstring`, and rightly. Three ways out, in the order I would take them — deploy from a published folder outside the repository, where no build-time scan reaches and the shipped placeholder stays clean; or build the rig with the documented `-p:SkipAppSettingsSecretScan=true`; or add the key to `AppSettingsSecretScanAllowedPaths`, which permanently widens a shipped control and should be paired with a startup check, as the `Auth:ClientSecretCredentialTarget` precedent in that file says. `CrawlStateWiring` already refuses a value containing a password, though by substring rather than by parsing — see the last row of section 4 **Route chosen and documented** in CRAWL-STATE-DEPLOYMENT.md section 11: deploy from a published folder outside the repository, where no build-time scan reaches and the shipped placeholder stays clean. The gate is not widened. The allowlist remains an option and is the wrong one to take before the parsed connection-string check in section 4 lands | ✅ | **PASSED** — published to a folder outside the repository, with the setting in that copy only. The run logged "Crawl state is enabled", a repo rebuild left it intact, the tracked file stayed clean and the hygiene gate stayed green. This is live test L5 |
| **CI integration job** | LocalDB on the Windows runner executing `sql/20`–`25` and driving the state store end to end, making blocker 1 permanent instead of a one-off. **Done** - the `state-database` job builds `ConnectorState` and the fixture on LocalDB, asserts the counts the scripts themselves state, proves the set is idempotent by running it twice, and checks that a hash version change is reported exactly once. `sql/25` is parsed and not applied, because its principals are domain accounts; `sql/27` is not run at all, because LocalDB has no SQL Agent | ✅ | **NOT RUN** — the job runs on a GitHub Actions runner and has not been triggered. Nothing here can stand in for it: the point is LocalDB on a clean runner |
| **Dashboard authorisation** | Group membership, not merely authentication. Any authenticated user can currently read crawl metadata. **Done**: `CrawlState:ReaderGroups`, empty by default so the site behaves as it did until somebody sets it. Membership is read from the Windows token, so a user added to a group has to sign in again | ✅ | **PASSED** — deployed to IIS and both halves proven live: a member got 200, a non-member got 403. See L4. The eight unit tests cover the rule; this covers the thing they said they could not see, that IIS supplies a `WindowsPrincipal` and a group name resolves against a token |
| **Run identifier in logs** | Stamp the run identifier on the logging context so a log file correlates to a dashboard row without a timestamp hunt. **Done** - pushed for the life of the run, so the batch writer's own events carry it too. The file template shows it and the console one does not: the file is what gets read beside a dashboard row hours later, the console is watched by somebody who already knows. Empty without a state store, because a log full of "run 0" reads as a real run | ✅ | **PASSED** — a dry run tagged all 1,119 in-run lines `run 8`, and the startup lines before the run opened carried no tag, which is the half that would have been wrong if the property leaked outside the run |
| **Retention scheduled** | A deployable SQL Agent job for `crawl.uspPurgeHistory`. The procedure and its arguments are documented; nothing ships the job. **Done**: `sql/27` ships it, idempotently, refusing early and distinctly when `ConnectorState` is absent or SQL Agent is not running. Deploying it is still a deployment step - CI cannot run it, because LocalDB has no Agent | ✅ | **PASSED, after a fix** — the script would not run: `sp_add_job` was given a concatenated `@description`, and T-SQL takes a constant or a variable as a procedure parameter, never an expression. Corrected to one literal. The job then deployed, all three checks OK, and `sp_start_job` returned `run_status = 1` with the step history naming the connection it purged |
| **Control evidence registered** | Pin the hash determinism, ordered-commit and retry-handler tests in the protected list, so they cannot be renamed or deleted without failing the control check. **Done** - six tests pinned across the three | ✅ | **PASSED** — carried by the suite, 316 green |
| **Drift detection updated** | Rewrite `deploy/Compare-SourceToIndex.ps1` against `crawl.vwItemInventory`. It predates the inventory and still reconstructs by re-reading the source. **Done**, and it closes a gap the script previously reported and lived with: with no enumeration API, an item whose source row was HARD-deleted had an ID nothing could guess, so it stayed indexed and citeable. The inventory is that enumeration. Where it is unavailable the old source-derived pass and its gap are unchanged, and the run says which of the two it did | ✅ | **PARTIAL, after a fix** — the inventory read and its per-connection scoping are proven against live data: 1,119 rows for `consultingwork`, 0 for `sqltickets`. That zero exposed a defect in this change, now fixed: an inventory that reads successfully but holds nothing was reporting the hard-delete gap CLOSED, which is a reassurance manufactured out of missing data. End-to-end reconciliation is still unproven here — the only connection with an inventory is the hierarchy one, and this script reads `dbo.Tickets` |
| **Concurrency × batching test** | Several writers each flushing batches, with a mid-batch refusal. The one interaction no test currently pins. **Done** - eighty items, eight writers, two refusals in different chunks, reconciling to exactly 78 written and 2 failed | ✅ | **PASSED** — carried by the suite, 316 green |
| **Delete threshold agreed** | `Settings:MaxDeletePercent` defaults to 10. Set it against the source's real daily deletion volume — a configuration decision, not code, and the only row here nobody but the customer can close. What has been done is the measurement: CRAWL-STATE-DEPLOYMENT.md section 12 carries the query that reports deletions per day as a percentage of the live corpus, and the two facts that make a low threshold misbehave - at 1,118 items one deletion is already 0.09%, and on a weekly full crawl the figure to compare is a week's churn rather than a day's | ❌ | **N/A** — a customer decision, not a testable one |
| **Security evidence script** | One script running every control verification query, so the review is reproducible rather than a list of queries to paste. **Done**: `deploy/Invoke-SecurityEvidence.ps1`, one verdict per control and a non-zero exit if any failed. SKIPPED is reported apart from PASS, because a machine without gitleaks has no history evidence and should say so | ✅ | **PASSED, and it earned its keep** — every control now has evidence: 7 passed, 0 skipped. Its first runs reported five SKIPPED rather than counting them as passes, which is what sent us to install the tooling; the tooling then exposed that the gitleaks control had never executed. Two defects in the script itself came out of the same run: it ran the tools in the caller's directory rather than the repository, and it reported a crashed scanner as a failed control |

---

## 4. After go-live

| Feature | Description | Status | Live Test Status |
|---|---|---|---|
| Incremental reads, connector side | The engine plumbs the resume marker and the change-detection tier, but no connector reads the marker yet. `sql/26` is not merely unreached by the shipped binaries — it is **incompatible with them**: `HierarchyPushConnector` emits an explicit 30-column `SELECT` and `vwExternalItemsIncremental` projects 12, so pointing `Source:ItemView` at it fails on 19 invalid column names, `LastModified` among them — the view names that column `EffectiveLastModified`. Column parity alone would not finish it: the query orders by item type then id, not the ascending `(marker, id)` a `ChangeMarker` source owes [`SOURCE-CONTRACT.md`](SOURCE-CONTRACT.md). Unnecessary at pilot scale; the lever that matters at production scale | ⚠️ | — |
| Hierarchy-aware timestamp deployed | `sql/26` alters the source tables and installs cascading triggers. Needs the source team's agreement; differencing works without it | ✅ | **Deployed and verified live on this rig.** The three cascading triggers are present and enabled on `Ops`, and `sql/31`'s 18-check probe passes `PASS 18/0/0` against them — including a live write-and-roll-back at each level, which is the only check that catches a trigger that is enabled but no longer firing. **The ⚠️ was about consent, not code, and that part does not transfer**: a customer's source team still has to agree to triggers on their tables. What is now settled is that the script works and that a disabled trigger is detectable. **Do not re-run `sql/26` wholesale on a populated database** — it disables all three triggers and rewrites `EffectiveLastModified` on every row whose timestamp the triggers have legitimately moved since the backfill (74,034 rows here), opening a trigger-disabled window and inventing an enormous delta |
| Identity cache wired | The principal cache table and store methods exist, but nothing calls them — the CDP resolver still resolves in memory each run | ⚠️ | — |
| Single flush procedure | Fold the per-chunk round trips into one server-side compare. The next throughput lever after batching | ❌ | — |
| Larger lookup chunks | Decouple the state-lookup size from the batch size, cutting store round trips roughly tenfold | ✅ | **PASSED, and the tenfold is measured rather than estimated.** `uspGetItemState` was called **560** times for a 111,900-row crawl where it previously took 5,595 — the lookup window is 200 and the write chunk stayed at Graph's 20. `uspRecordUnchanged` stayed at 5,595, correctly: recording belongs to the write chunk, not the window. Run 24 took 12s against 18s for the comparable prior run, but the round-trip count is the evidence here; one wall-clock sample is not. The first attempt used one number for both units and starved the writer pool — 200 rows went to one writer and fifteen sat idle — which the concurrency tests caught |
| Overlapped store reads | Prefetch the next chunk's hashes while the current chunk writes | ❌ | **Built, measured, and deliberately not shipped.** A one-window pipeline was implemented — the closed window's lookup starts without being awaited, the previous window publishes while it runs — and it made no difference: three runs each over the same 111,900-row corpus gave **13/13/13s without it and 12/14/14s with it**. The reason is that the lookup-window change already removed the cost it was meant to hide: 560 round trips instead of 5,595, on a run where source read is 0.1% of per-row time. Against that it doubles the live-window memory bound and puts a second in-flight window into the one place source order must hold for the checkpoint. Measured on an all-unchanged crawl; a write-heavy crawl is dominated by Graph, where the reader matters less still, so there is no regime on this rig where it pays. Revisit only if a corpus appears where the store is the bottleneck |
| Batched deletions | The sweep still deletes one item at a time; the batch writer already exists | ✅ | **PASSED live.** A crawl pointed at a view hiding 25 rows swept them and logged *"25 deletion(s) sent in 2  round trip(s)"* — 20 + 5, where it would previously have been 25 calls. All 25 then returned **404 from Graph**, the store recorded 25 deleted and 0 pending, and re-crawling against the full view restored them. The unit tests assert the sub-request **method is DELETE**, not just the round-trip count: a batch that PUT twenty items would report twenty successes and one round trip while rewriting everything the sweep was told to remove. Disabling the branch fails all five |
| Dry-run preview | Consult the state store on a dry run to report what *would* be written, skipped and deleted — the delete sweep previewed before it happens | ✅ | **PASSED live, both halves.** Writes/skips: a dry run over the corpus reported **111,900 "Would SKIP" and 0 "Would write"**, where it previously called all 111,900 writes — an overstatement of four orders of magnitude on the one question a preview is asked. Deletes: a source capped to 500 rows produced *"a real run would remove 111400 of 111900 live item(s) (99.55% of the corpus). The guard is 10%, so the sweep WOULD BE REFUSED and the run would exit 4"*, with twenty named and the remainder counted. It cannot use `uspGetPendingDeletes`, which mutates and returns nothing on a dry run, so `sql/34` adds a read-only `uspListLiveItemIds` and the diff runs the other way. Verified to touch nothing: 0 pending deletes, 0 checkpoints, 111,900 still live afterwards |
| Health endpoint | A machine-readable projection of connection health for monitoring, rather than a page for people | ✅ | **Done.** `GET /health` returns stable JSON read straight from `crawl.vwConnectionHealth` — every word verbatim from the view, `byHealth` a verbatim histogram, so a word a future `sql/` script adds appears on the next poll with no rebuild. **Requires the reader policy by name**, not `.RequireAuthorization()`: that overload means the *default* policy, which would have handed `/health` to every authenticated user in the domain while `ReaderGroups` still said otherwise, silently. **200 with an unhealthy body; 503 only when it cannot answer** — a 503 for an unhealthy connection would make "a connection is failing" and "the dashboard is down" arrive as the same red. An estate with zero connections reports `warning`, not `ok`. 26 tests |
| Trigger health check | Scheduled run of `sql/26`'s verification query; nothing detects a disabled trigger, and the source keeps accepting writes while the column stops moving | ✅ | **PASSED live.** `sql/31` adds an 18-check procedure, `sql/32` schedules it daily in SQL Agent, `deploy/Test-TriggerHealth.ps1` is the Express path. Healthy: `PASS 18/0/0`. With a trigger disabled, **two independent legs fire** — the catalogue check and a live write-and-roll-back probe — and the uncaught `THROW` gives `sqlcmd -b` exit 1 and a failed Agent job. The catalogue legs start from an expected-trigger list `LEFT JOIN`ed to `sys.triggers`, so a **deleted** trigger reports `ABSENT` rather than one fewer healthy row. `is_disabled` alone was not enough: the probe is what catches *enabled but altered*. Every trigger re-enabled and verified afterwards |
| Negative TTL enforced | The shorter time-to-live for an unresolvable principal is a caller convention the database cannot see | ✅ | **PASSED live.** `sql/33` puts the two TTLs on `crawl.Connection` with a trusted `CHECK`, and `uspCachePrincipal` now applies `min(requested, cap)` to a negative answer — the database sets a floor on freshness and never overrules a caller that wants to be stricter. Five scenarios written through the real procedure and rolled back: a negative answer asking 720 stores **60** (the case the old procedure got wrong), one asking 15 still stores 15. The constraint refuses a violation with error 547. **Honest note:** the retroactive clamp reported `0 clamped`, and that 0 is an *empty table*, not "nothing was over the cap" — which is why the rule was proven by the written rows instead |
| Batch envelope tuning | The client-side batch byte cap is deliberately conservative and should be raised against measured behaviour | ✅ | **Exposed as `Settings:MaxBatchContentBytes`, and deliberately left at its default.** The cap was raisable only by rebuilding, though its own header said to raise it "once a tenant's real behaviour is known" — which is learned in production, not at compile time. **The measurement says do not move it**: this corpus is p50 491 content bytes and max 904, so twenty requests is 18 KB and the request count closed all 5,608 batches while the byte ceiling has never once fired. Moving it would be tuning against a number nobody has observed |
| Sargable incremental view | Project the numeric keys from the item views so the incremental view joins on them rather than on a constructed string | ✅ | **PASSED, with a measured A/B and an honest limit.** The three item views project `SourceId` (appended last, so nothing moves) and `sql/26` joins on it. Base-table logical reads fell **13,494 → 7,772 (-42%)** over 293 changed items. Proven additive on the live 111,900-row corpus: identical `COUNT(*)`, identical `CHECKSUM_AGG`, and an identical SHA2_256 over the connector's 30-column projection. **What it did not fix, stated rather than glossed:** the time-entry branch still hash-joins to a clustered index scan — now the optimiser's costing choice, not the predicate — and the dominant worktable cost is the `STRING_AGG` rollups, untouched. It also replaced `sql/26`'s verdict, which was hard-coded to 1,118 rows and would have reported FAIL on this database every time |
| Central package management | One file pinning package versions for every project, removing the class of split-version failure that has already broken CI twice | ✅ | **Done.** `Directory.Packages.props` with transitive pinning and `CentralPackageVersionOverrideEnabled=false`, so neither a `Version` attribute nor a `VersionOverride` can reintroduce a per-project decision. 21 `PackageReference` entries across 9 projects stripped of versions. Two had to move rather than lose an attribute: `Google.Protobuf`, whose version was a csproj property switching on `EnableOtlpExporter`, and the dashboard's two hand-rolled pins to packages it never calls. Verified clean at net10.0 **and** net9.0, and with a `global.json` pinned to 9.0.100 as CI does |
| Identity library unification | Collapse the pre-existing split that carries eight packages at two versions in the offline restore set | ✅ | **Done, and the backlog wording was off by one.** Measured from `project.assets.json`: **7** ids at two versions, not eight — the family has 8 distinct ids, and the eighth (`Microsoft.IdentityModel.Validators`) already rode at a single version. Pinned at 8.15.0, which is a raise but not a gamble: four projects referencing SqlClient 5.2.2 directly *already* resolved this family at 8.15.0, so the combination already ships. Resolved graph went 69 → 62 packages with **no id left at two versions** |
| Harness fixture generation | Generate the local test harness's fixture copies rather than hand-syncing them | ⚠️ | **Half done, and the boundary is the reason.** `build/Sync-HarnessFixtures.ps1` derives the 8 duplicated values from `appsettings.json`, compares by default, rewrites with `-Update`, and is wired into CI as a drift guard. The copies it maintains live under `deploy/`, which the agent that wrote it did not own, so they are checked rather than generated in place — they currently agree, so CI is green. It also found a **fourth** copy of the connector GUID, in `deploy/Install-Connector.ps1`, that the README's own warning about that GUID does not list |
| Release automation | Encode the publish-then-delete-previous release sequence as a workflow rather than manual steps | ✅ | **Done.** The policy is real and documented in `GENESIS-PROMPT.md` §11 — *delete the releases, keep the tags*. `release-retire.yml` never passes `--cleanup-tag`, never touches a draft, retires both lines of a version together, and re-reads the remote tags afterwards to prove they survived. **It reports on publish and only deletes on an explicit dispatch**: `build.yml` creates a draft precisely so a person decides what ships, and an unattended irreversible delete would point that same decision the other way. Exercised against stubbed `gh`/`jq`/`git` — 51 releases deleted, drafts and the current version skipped, all 51 tags confirmed present |
| Strict hasher fallback | Throw on a property type the hasher does not recognise instead of falling back to a string conversion | ✅ | **Done, with the defect measured rather than assumed.** The old fallback never *failed* — it succeeded quietly, and for collections `ToString` returns the type name, so `int[] {1,2,3}` and `int[] {4,5,6}` both hashed as `"System.Int32[]"` and compared **equal**. Two different values, one hash: the change-detection failure this hasher's own header calls the expensive one. Now `NotSupportedException` naming the property and its type, never its value. **`HashVersion` deliberately NOT bumped**, justified from evidence: every write into `PushItem.Properties` across all shipped connectors is a closed set of five types, all of which hit a branch, so nothing could reach the old fallback and no rendering moved. Bumping would have cost every deployed corpus an announced full rewrite to reach the state it was already in. 9 tests; 7 fail against the old code |
| Parsed connection-string check | Parse rather than substring-match when refusing a password in the state connection string | ✅ | **Done.** `SqlConnectionStringBuilder` replaces the substring match, tested via `ShouldSerialize` (`ContainsKey` is useless — the builder pre-populates every keyword, so it is always true). Six inputs the old check got wrong as **false positives**: `Server=pwd-sql01`, `Database=PasswordVault`, `Application Name=PwdReset`, a quoted `Initial Catalog` containing the word, and an empty password. Two it **silently accepted**: `Integrated Securty=true` (a typo) and a syntax error — both now refused here rather than at the first connect. The parser's own message is deliberately not repeated: an unquoted password containing a semicolon makes it answer `Keyword not supported: 'ter'`, a fragment of the secret, and this message is logged. **Honest negative result:** no legal encoding of the keyword slips past a substring match — the malformed string was the only real gap. 13 tests; 7 fail against the old code |
| Per-type duplicate counts | The run table counts duplicates; the per-item-type breakdown does not | ✅ | **PASSED live.** A projection repeating 3 customers and 2 time entries produced exactly `Customer 3 / Engagement 0 / TimeEntry 2` in `crawl.RunItemType`, reconciling with the run-level 5. Deliberately not five of one kind: a single-kind fixture cannot tell a correct implementation from one that lumps every duplicate under whichever type it saw first. `sql/40` is longer than the column it adds because `ItemTypeCountList` is a **table type** — it cannot be altered, only dropped, which needs the procedure dropped first, and `DROP`…`CREATE` destroys the grants `CREATE OR ALTER` would have kept. The script puts back `crawl_writer`'s EXECUTE on **both** the procedure and the type, and verifies it; without the second the push identity fails at the end of every run, after the crawl has done all its work. **It also caught a regression I had introduced**: the dry-run preview made `Total` written-only, so `Total - Duplicates` reported *"-5 distinct item(s)"*. Now computed from rows read, and reporting 111,900 |
| Engine file split | Separate the run lifecycle, the chunk flush and the delete sweep into their own files | ❌ | — |

---

## 5. Live tests to perform

Everything in sections 1 and 3 that a build can settle has been settled. What is
left needs a server, a tenant or a directory, and this is that list.

It is ordered so that a failure stops you before the next test wastes its setup,
and each row says what a pass looks like — because the recurring failure in this
project has not been a test that failed, it has been a test that passed while
proving nothing. Two of the defects found so far were silent: a view that
returned zero rows without erroring, and a guard whose entire message was empty.
Both looked like passes.

### Blocking — these close row 1, the last open blocker

| # | Test | Pass looks like | Why it cannot be done here | Live Test Status |
|---|---|---|---|---|
| L1 | Run `sql/25` where the `CONTOSO\` principals are real, then run a crawl as `crawl_writer` and open the dashboard as `crawl_reader` | The crawl writes and the dashboard reads, and *nothing else works*: `crawl_writer` must fail a direct `SELECT` on `crawl.Item`, and `crawl_reader` must fail every write procedure | Domain accounts. CI substitutes nothing and says so; a grant nobody has authenticated against is a claim, not a control | **BLOCKED** — the principals are domain accounts and cannot exist on this machine. Local ones stood in, so the roles exist and `sql/28` granted to `crawl_writer` successfully, but no grant here has been authenticated against by the account it is written for |
| L2 | Run `sql/20` **unedited** on an instance that has the `D:` volumes it names | It creates the database without the placeholder edit every run so far has made | The rig has no `D:`, so every execution to date used a modified copy | **BLOCKED** — no `D:` volume on this box, which is the whole point of the test |
| L3 | Deploy `sql/27`, then `sp_start_job` it once | `run_status = 1`, and the step history names each connection it purged | LocalDB has no SQL Agent | **PASSED**, after a fix. `sp_add_job` was given a concatenated `@description` and T-SQL takes no expression as a procedure parameter, so the script would not run at all. Corrected, the job deployed, `sp_start_job` returned `run_status = 1`, and the step history read "Purging consultingwork" |

### Confirming the three shipped-but-not-deployed items from section 3

| # | Test | Pass looks like | Live Test Status |
|---|---|---|---|
| L4 | Set `CrawlState:ReaderGroups` to a real group, then open the dashboard as a member and as a non-member | The member sees pages; the non-member is refused. **Test the non-member** — an empty list silently permits everyone, and that is the state this shipped in | **PASSED**, both halves. Member `S-1-5-32-545` (`BUILTIN\Users`) got **200**; non-member `S-1-5-32-551` (`BUILTIN\Backup Operators`) got **403** — the half that matters, and the half a rule failing open would have passed. Neither a domain nor a second account was needed: one token is in the first group and not the second. It also settled the open question in `ReaderPolicy` — the name form `BUILTIN\Users` returned 200, so IIS supplies a `WindowsPrincipal` and names resolve, though a SID remains sturdier since it needs no resolution and survives a rename. Run with `deploy/Test-DashboardAuthorization.ps1`, which restores the deployed configuration afterwards |
| L5 | Publish to a folder outside the repository, put the real `StateConnectionString` in *that* `appsettings.json`, and rebuild the repository | The connector still runs, and the rebuild did not wipe the setting. This is the route section 11 of the deployment guide recommends, and it is untested | **PASSED** — the connector loaded its configuration from outside the repository, logged "Crawl state is enabled", opened run 8 and wrote nothing to Graph. A repository rebuild left the setting intact, the tracked file stayed clean and the hygiene gate stayed green |
| L6 | Re-run `Invoke-SecurityEvidence.ps1` on a machine with `gitleaks` and `pre-commit` installed | Zero SKIPPED. Every run so far has skipped most of the controls, which is not the same as passing them | **PASSED** — 7 passed, 0 failed, 0 skipped, exit 0, after installing gitleaks, Python with pre-commit, and the .NET 10 SDK so the suite could run at the solution's own target. Getting there took four fixes, because a control nobody had run is a control nobody had tuned: `.gitleaks.toml` did not compile at all, its allowlist matched the wrong target, the `no-certificate-files` hook needed a `bash` that Windows does not have, and two hooks were rewriting Microsoft's vendored `.proto` files |

### The two things no quiet tenant has been able to prove

| # | Test | Pass looks like | Live Test Status |
|---|---|---|---|
| L7 | Provoke throttling — a corpus large enough, or a tenant busy enough, to return a real `429` | `throttleWaits > 0`, the run still completes, and the backoff honours `Retry-After` | **PASSED, and it earned more than any other test here.** `sql/14` scaled the fixture 100-fold; the run wrote 110,590 items in 60m33s at ~1,826/min over 5,608 batches. 191 sub-requests took a `429` with `Retry-After: 10`, arriving in three short clusters rather than steadily — minutes 0, 12 and 19 of sixty, the other fifty-seven clean — and the backoff honoured every one. **The retry then refused all 191 with `400 NullOrEmptyValue`**: a retried item was re-serialized from the same instance and lost its ACL, so every throttled item was absent from the index under a run reporting success. Fixed in `af3dc6e`, reproduced in a test for the first time, and the harness that could not express it corrected. No faster machine would have found this — only more rows |
| L8 | Fail a run deliberately — `MaxDeletePercent = 0` with one row missing trips `THROW 50007` | The Runs page shows a failed run, the connection health badge flips, and the Overview shows "needs attention" | **PASSED**, all three. One time entry soft-deleted with the guard at 0: run 12 exited 4 and the failure was the most recent run, which is what the earlier attempt lacked. `/Runs` rendered a `failed` pill, the health pill flipped to `failing` with `vwConnectionHealth` reporting `Health=failing, LastRunStatus=failed`, and the Overview carried `banner banner-bad` reading "1 connection is failing or late", with tiles `Failed runs 3 of 11` and `Needs attention 1`. It also gave the `%%` fix its first live proof: the guard message arrived intact — "It would remove 1 of 1119 live items (0.09%), above the 0.00% guard" — where it used to arrive empty. The fixture and the guard were then restored and run 13 returned the connection to healthy, so the failed runs remain in history and the rig does not |

### Worth doing while the fixture is in the right state

| # | Test | Pass looks like | Live Test Status |
|---|---|---|---|
| L9 | Set `IsDeleted = 0` on the tombstoned time entry and run a full crawl | The item returns and `crawl.Item` moves from state 3 back to live. Resurrection is what `@KeepTombstoneDays` exists for and it has never been exercised | **PASSED** — run 9 wrote exactly 3 items and `time6053` went State 3 to State 1 with `DeletedUtc` cleared. The corpus is 1,119 live and zero tombstoned. The 3 are the entry and its two rollup ancestors, the same lineage the insert and the delete produced, so resurrection costs what a change costs and not a rewrite |
| L10 | Set `Settings:Incremental = true` and run twice inside `FullEveryHours` | **The pass condition as first written is unreachable, and that is the finding.** It expected an incremental read with no delete sweep. No shipped connector implements the `ChangeMarker` tier, so `PushItem.LastModifiedUtc` is never set, so no checkpoint is ever saved — and `uspBeginRun` escalates any incremental request with no checkpoint to full. Read this row instead as: the escalation guard fires, says why, and records the mode the run will actually read in | **PASSED, against the corrected condition** — runs 10 and 11 both requested Incremental and both recorded `Mode = 1`. `crawl.Checkpoint` is empty and stayed empty. The warning names the cause exactly: "an incremental read with no baseline to be a delta against reads from the beginning of time anyway, so the run is recorded as what it will actually do". Until a connector reads the marker, `Settings:Incremental` changes nothing but the log |
| L11 | Deploy `sql/28` to the live `ConnectorState`, then run the connector | No migration is reported, because the framing has not changed. Then set `ItemHasher.HashVersion` to 2 in a scratch build and confirm the next run escalates to full, says so, and reports it only once | **PASSED**, both halves and the rollback. `sql/28` deployed to the live database; the v1 connector then ran and reported nothing, with `HashVersion` staying 1. A scratch build at version 2 reported "The hash framing changed from version 1 to 2", escalated to Full, and its second run said nothing — reported once, as the store advances the version while answering. Running v1 again reported the **downgrade**, 2 to 1, and then fell silent, which is the rollback path behaving the same way in the other direction. Stored version is back at 1. One honest limit: only the constant was moved, not the framing, so the hashes still matched and both runs reported `unchanged=1119, written=0`. This proves the detection, the escalation and the report-once contract; it does not prove the rewrite, which needs a real change to the hasher |

Two notes on the order. L1 first, because it is the only one still holding a
blocker open. L7 last, because it is the only one that cannot be scheduled —
throttling happens when the service decides it does, and the honest plan is to
watch for it in the pilot rather than to wait for it.

---

## 6. What is not in this repository

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
