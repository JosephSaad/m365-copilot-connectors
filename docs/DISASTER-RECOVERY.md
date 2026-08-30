---
title: Disaster recovery
description: What is actually lost when the connector host or the state database dies, per artefact and with the recovery point objective each conclusion supports; how to rebuild on a replacement host; how to re-provision the Entra credential, which does not travel with a database backup; and the record of the restore rehearsal that proves the plan rather than asserting it.
---

# Disaster recovery

## 1. Start here: the recovery objective is not availability

Every disaster recovery plan begins by naming what the outage costs, because
that number decides what is worth building. For most systems the answer is
"users cannot work". For this one it is not, and getting this wrong leads an
operator to deprioritise exactly the wrong things during an incident.

**When this connector dies, search does not go down — it goes stale.**

Microsoft Graph holds the corpus. It keeps serving every item this connector has
ever pushed, indefinitely, whether or not the connector is running. There is no
outage, no error page, no failed query, and no user complaint. A connector that
has been dead for three weeks looks, from Copilot, exactly like a connector that
ran twenty minutes ago.

So the exposure is not that people cannot find things. It is that
**deletions and permission revocations stop propagating.**

| What happened in the source | What Copilot does while the connector is down |
|---|---|
| A record was deleted | Keeps returning it, with its content, to everyone who could see it before |
| A group's membership changed | Keeps applying the **old** ACL — the one captured at the last successful push |
| An employee left and was removed from a group | Keeps showing them results they are no longer entitled to |
| A customer exercised a deletion right | The record remains searchable and quotable |

⚠️ **RTO here is a security number, not an availability number.** The question
"how long can we be down" is really "for how long are we willing to serve
content under access rules we know are out of date, and to keep serving records
the source has already deleted". That is a question for whoever accepts risk in
this estate, not for whoever runs the backup schedule, and it belongs in the
same conversation as the ACL staleness bound in
[`PRODUCTION-ONBOARDING.md`](PRODUCTION-ONBOARDING.md).

**What this changes for an operator mid-incident.** The instinct during an
outage is to restore state first, because state feels like the precious thing.
Here that instinct is backwards. The state database is a cache with an audit
trail attached; it rebuilds itself. The thing that actually shortens the
security exposure is **getting a host authenticated to Entra and crawling
again**, because until a crawl completes and a delete sweep runs, every stale
ACL in the index stays live. Section 5 is therefore the section that matters
most under time pressure, and it is the one with the least automation behind it.

The total exposure window is:

```
outage duration  +  time to first successful FULL crawl after recovery
```

The second term is not optional and it is not small. Deletions are detected by
comparing a full read of the source against the stored inventory — an
incremental crawl cannot detect them at all, and after a rebuild the connector
forces a full crawl anyway because it has no checkpoint. Budget one full crawl
on top of however long the host was down, and quote that combined number when
somebody asks for the RTO.

---

## 2. What is actually lost, per table

`ConnectorState` holds eight tables. They are not equally valuable, and treating
them as one artefact produces either a backup policy that is too expensive or a
recovery plan that quietly loses evidence. This is the per-table analysis, taken
from [`sql/21-crawl-state-tables.sql`](../sql/21-crawl-state-tables.sql).

The column that matters is the last one: **the recovery point objective each
table can justify on its own.**

| Table | What it holds | Lost if gone? | Rebuilt by | RPO this table alone justifies |
|---|---|---|---|---|
| `crawl.Item` | Item IDs, types, `ContentHash`, `AclHash`, state, tombstones — 111,900 rows on the reference rig | **No** | One full crawl. Every write is an upsert, so an empty inventory rebuilds itself against the corpus already in Graph | **None.** Any RPO is defensible; the cost of loss is Graph write quota, not data |
| `crawl.Checkpoint` | One row per connection: the incremental marker | **No** | The next run. `uspBeginRun` returns `FullCrawlDue = 1` when `HasCheckpoint = 0`, so an absent checkpoint forces a full crawl rather than a silent partial read | **None** |
| `crawl.Connection` | Registration, `DisplayName`, `ExpectedIntervalMinutes`, `IsEnabled` | **No** | The next run. `uspRegisterConnection` passes `ExpectedIntervalMinutes` on **every** call, not only on insert, so the lateness threshold is restored from the connector's own configuration rather than by hand | **None** — but see the warning below |
| `crawl.PrincipalMap` | Cached source-group → Entra object ID mappings, with TTLs | **No** | Re-resolution against Entra on the next crawl | **None.** Cost is a burst of directory reads |
| `crawl.Run` | One row per crawl: mode, status, counts, host, `ToolVersion`, errors | **YES — permanently** | **Nothing.** No crawl recreates the history of previous crawls | **24 hours** |
| `crawl.RunItemType` | Per-item-type breakdown of each run | **YES — permanently** | Nothing | **24 hours** |
| `crawl.RunPhaseTiming` | Per-phase percentiles per run | **YES — permanently** | Nothing | **24 hours** |
| `crawl.ThrottleEvent` | Every 429 with its endpoint, attempt and `Retry-After` | **YES — permanently** | Nothing | **24 hours** |

### The conclusion this supports

**Four of the eight tables are a cache. Four are evidence. The recovery point
objective is set entirely by the evidence, and by nothing else.**

If `ConnectorState` held only the first four tables, the honest backup policy
would be "do not bother" — and section 7 of
[`CRAWL-STATE-DEPLOYMENT.md`](CRAWL-STATE-DEPLOYMENT.md#7-backup-posture)
reaches exactly that conclusion about the inventory, correctly. The reason to
back this database up at all is the audit trail.

That is what makes **a daily backup with RPO = 24 hours** the defensible
posture. Not because a day of inventory is expensive — a day of inventory is
worth precisely one full crawl — but because a day of run history is gone for
good, and "prove this connector ran nightly for the last quarter, and show me
every time Graph throttled it" is a question a regulated estate eventually asks.
There is no other source for that answer.

Two consequences follow, and both are worth writing into the backup ticket:

- **The frequency is driven by evidence, so the retention is too.** A backup
  kept for two weeks satisfies no audit question worth asking. Set retention
  from the evidence-retention policy — quarters, not days — and note that
  `sql/27`'s retention job is already ageing this data out of the live database
  on its own schedule, so the backups are the long-term copy.
- **Point-in-time recovery is still the wrong purchase.** `RECOVERY SIMPLE` is
  deliberate. Losing the last few hours of run history costs a gap in a report;
  paying for log shipping to close that gap is paying for the wrong thing. Daily
  full, `SIMPLE`, long retention.

⚠️ **One caveat on `crawl.Connection`, because "rebuilt automatically" hides a
dependency.** The row is restored from the connector's configuration on the next
run — which means it is restored from `appsettings.json` on the replacement
host. If that file is itself lost and reconstructed from memory,
`ExpectedIntervalMinutes` comes back wrong or null, and the health view's
`late` verdict silently stops meaning anything. The database is not the artefact
to protect here; the connector's configuration is. Put `appsettings.json` in
source control or in the configuration management system, and treat the
deployment zip plus that file as the real recovery unit.

⚠️ **What no backup of this database can give you back.** Deletions that
happened in the source while the connector was down are not recovered by
restoring the state store — they are never known. The rebuilt inventory records
the source as it is *now*, so an item deleted during the outage simply never
enters it, and nothing later concludes it should be removed from Graph. The full
reasoning is in
[section 9 of `CRAWL-STATE-DEPLOYMENT.md`](CRAWL-STATE-DEPLOYMENT.md#9-if-the-state-database-is-lost-or-rewound),
which is required reading before any restore. The tool that finds those
survivors is `deploy/Compare-SourceToIndex.ps1`, and running it after a recovery
is not optional — see section 5, step 8.

---

## 3. Taking the backup

`deploy/Backup-ConnectorState.ps1` takes it, verifies it, and writes a manifest
beside it.

```powershell
.\Backup-ConnectorState.ps1 -ServerInstance SQLPROD01 `
                            -BackupDirectory D:\Backup\ConnectorState `
                            -RetentionDays 120
```

Schedule it daily. It is not interactive, it works on Windows PowerShell 5.1 and
PowerShell 7, and it never writes to the database it backs up.

Three of its behaviours are decisions rather than defaults, and each exists
because of something that breaks otherwise.

**`COPY_ONLY`, always.** If the estate ever adds differential backups of this
database, an ordinary full backup taken by this script would silently become
their new differential base — and their differentials would then depend on a
file this script may already have pruned. `COPY_ONLY` makes the script
structurally incapable of damaging a backup chain it does not own. There is no
switch to turn it off. If this database is the estate's to back up, let the
estate back it up and do not schedule this at all.

**`CHECKSUM` on the way out, `RESTORE VERIFYONLY` on the way back.** A file that
exists is not a backup that restores. Verification roughly doubles the runtime,
which against this database means fractions of a second.

**A sidecar `.json` manifest.** The `.bak` does not tell you how many rows
should come back, or which connector binaries wrote the state inside it. The
manifest records per-table row counts, a schema fingerprint (tables, procedures,
views, table types) and the distinct `crawl.Run.ToolVersion` values present.
`Invoke-RestoreDrill.ps1` verifies a restore against it, and
[`UPGRADE-RUNBOOK.md`](UPGRADE-RUNBOOK.md) uses the `ToolVersion` list to answer
"which binaries does this backup pair with".

### ⚠️ The backup is written by the SQL Server service account, not by you

This catches people out on the first run, every time, and the error message
does not say so.

`BACKUP DATABASE` executes **inside the database engine**. The identity that
must be able to write to `-BackupDirectory` is therefore the SQL Server service
account — typically `NT Service\MSSQLSERVER` — and not the account running the
script. Point it at your own profile, a scratch folder, or a mapped drive and
you get:

```
Cannot open backup device 'C:\Users\...\ConnectorState-20260830.bak'.
Operating system error 5(Access is denied.).
BACKUP DATABASE is terminating abnormally.
```

The script traps this specific failure and explains it rather than passing the
raw error through, because the natural reading — "I do not have permission" — is
wrong and sends you to the wrong place.

Find the account and grant it `Modify` on the directory:

```sql
SELECT servicename, service_account FROM sys.dm_server_services;
```

**The reverse bites just as often.** The instance default backup path lives
under `Program Files`. The service account can write there; *you* frequently
cannot read there. That is why the script takes the backup size from
`msdb.dbo.backupset` rather than from `Get-Item` — a size of zero because the
directory was unreadable looks identical to a size of zero because the backup
was empty — and why a manifest it cannot write is a warning rather than a
failure. The backup is the artefact; the manifest is an aid. Retention pruning
is a filesystem operation performed by *your* account, so where you cannot
enumerate the directory the script skips it and says so, and retention becomes
the estate's housekeeping job. Say that in the ticket rather than discovering it
when the volume fills.

---

## 4. Rehearsing the restore

> An untested restore is a hypothesis, not a plan.

`deploy/Invoke-RestoreDrill.ps1` turns it into a plan. It restores a backup to a
**differently named** database, verifies it the way a deployment would, and
drops it again.

```powershell
.\Invoke-RestoreDrill.ps1 -BackupDirectory D:\Backup\ConnectorState
```

It asks four questions that `RESTORE VERIFYONLY` cannot:

1. **Does it restore onto this instance, with the files relocated?** A backup
   taken on a host with `D:\Data` restores onto a host with only `C:\` only if
   somebody worked out the `MOVE` clauses. Doing that for the first time during
   an incident is how a six-second restore becomes a two-hour one.
2. **Is every row there?** Per-table counts against the manifest. This is what
   catches a backup of the wrong database, or of the right database before a
   load finished.
3. **Is the schema intact?** Fingerprint against the manifest, plus
   `DBCC CHECKDB` — run here rather than against production precisely because
   the drill copy is disposable, so the expensive check costs nothing.
4. **Would the deployment's own verification pass?**
   [`sql/30-verify-set-options.sql`](../sql/30-verify-set-options.sql) and
   [`sql/42-verify-least-privilege.sql`](../sql/42-verify-least-privilege.sql)
   are executed against the restored copy. `sql/30` matters most:
   `QUOTED_IDENTIFIER` is stored **per module** as it stood in the session that
   created it, and a restored database carries whatever the original had — so a
   restore is exactly the moment to re-ask. `sql/42` is run against the drill
   copy rather than production because it creates and drops probe users, which
   is a write.

Finally it executes every view. A view that compiled is not a view that runs;
[section 5 of `CRAWL-STATE-DEPLOYMENT.md`](CRAWL-STATE-DEPLOYMENT.md#5-verification-what-each-script-prints)
makes this point about deployment and it applies unchanged to a restore.

### ⚠️ Three guards, because the script's job is `DROP DATABASE`

A drill script that can be pointed at production by a tired operator at 03:00 is
not a safety tool. This one refuses:

| Guard | What it does |
|---|---|
| **Source-name check** | The target is compared against the source database name read from the backup media itself (`RESTORE HEADERONLY`) — not against a parameter default. Restoring a `ConnectorState` backup to a database called `ConnectorState` is refused outright |
| **Ownership stamp** | Every database the script creates is stamped with an extended property, `CS_DrillDatabase`. It will not drop, and will not restore over, a database lacking that stamp. A pre-existing database that happens to share the drill name is an error, not a casualty. The stamp is re-read immediately before the `DROP`, not trusted from earlier in the run |
| **File relocation** | The restore always `MOVE`s the data and log files to filenames derived from the drill name, so it cannot land on the live database's files even by accident |

There is no `-Force`. If a guard fires, the answer is to pick a different name.

### The rehearsal record

Rehearse **at least once at deployment, and again after any change to the
instance, the storage layout or the schema**. Record it here — a drill nobody
wrote down is a drill nobody can prove happened.

| Date | Instance | Result | By |
|---|---|---|---|
| 2026-08-30 | `localhost`, SQL Server 17.0.1125.2 Standard Developer | **PASSED** on both Windows PowerShell 5.1 and PowerShell 7 | Initial rehearsal, recorded below |

What the first rehearsal actually measured, against the live reference rig
(111,900 items):

| | |
|---|---|
| Source database | `ConnectorState`, 640.0 MB allocated, `RECOVERY SIMPLE` |
| Backup | **8.5 MB** compressed, 61.1 MB uncompressed, ratio 7.1×, in **0.7 s** (5.1) and **0.3 s** (7) |
| `RESTORE VERIFYONLY WITH CHECKSUM` | passed in **0.1–0.2 s** |
| Restore to `ConnectorState_DrillRestore` | **6.9 s** (Windows PowerShell 5.1), **4.9 s** (PowerShell 7) |
| Row counts | **112,621 rows across 8 tables — every table matched the manifest exactly** |
| Schema fingerprint | 8 tables, 30 procedures, 7 views, 6 table types — all matched |
| `DBCC CHECKDB` | no allocation or consistency errors, 1.1–1.3 s |
| `sql/30-verify-set-options.sql` | passed — every module carries `QUOTED_IDENTIFIER ON` and `ANSI_NULLS ON` |
| `sql/42-verify-least-privilege.sql` | passed |
| Views executed | 7 of 7 |
| Teardown | drill database dropped; instance returned to its prior state |
| Guard test | pointing `-DrillDatabase` at `ConnectorState` was **refused before any restore**, exit code 4 |

**The number worth carrying out of that table is five seconds.** Restoring this
database is not the hard part of recovering this connector, and it never will
be — the corpus lives in Graph and the state store is 8.5 MB compressed. Which
is precisely why the next section is the one that decides your real RTO.

---

## 5. Rebuilding on a replacement host

The state database restores in seconds. The credential does not travel at all.
That asymmetry is the whole of this section.

### What you need before you start

| | |
|---|---|
| The release zip | `SqlTicketsConnector-vX.Y.Z.zip` from the GitHub release for the tag you were running, or rebuilt locally with `Build.ps1`. **Match the version that was running**, not the newest — see [`UPGRADE-RUNBOOK.md`](UPGRADE-RUNBOOK.md) |
| `appsettings.json` | From source control or configuration management. Not from the backup — it is not in it |
| The Entra app registration | Still exists in the tenant. The tenant is not part of this disaster unless it is |
| A credential the new host can use | **This is the long pole.** Section 6 |
| The backup, if you want the history | Optional. Without it the connector rebuilds everything except the evidence |

### The order

1. **Stand up SQL Server access.** If `ConnectorState` survived, nothing to do.
   If the instance was lost too, run the deployment scripts in the order given
   by the table in
   [section 2 of `CRAWL-STATE-DEPLOYMENT.md`](CRAWL-STATE-DEPLOYMENT.md#2-prerequisites-and-the-order-the-scripts-run-in)
   — thirteen scripts, and the order is not the numeric order. That table is the
   authority; this page deliberately does not copy it, because a duplicated
   deployment order is a deployment order that goes stale.

2. **Restore the backup, or do not.** Both are supported and the choice is
   about evidence, not correctness:

   | | Restore it | Skip it |
   |---|---|---|
   | Run history | Preserved to the backup point | Gone permanently |
   | First crawl | Full — the checkpoint is stale, and a restore of any age should be followed by a full crawl before delete detection is trusted again | Full — there is no checkpoint |
   | Delete guard | ⚠️ **Expect it to fire.** Items deleted since the backup are live rows again, and the sweep will find them missing | Will not fire: an empty inventory concludes no deletions |
   | Graph writes | One full crawl either way | One full crawl |

   If you restore, read
   [section 8 of `CRAWL-STATE-DEPLOYMENT.md`](CRAWL-STATE-DEPLOYMENT.md#8-the-delete-guard)
   **before** the first crawl, not after the guard refuses. A restore is exactly
   the situation the guard was written for, and the correct response is the
   investigation, not an override typed from memory.

3. **Deploy the binaries.** Unzip; install with `deploy/Install-Connector.ps1`.

4. **Re-provision the credential.** Section 6. Budget most of the recovery here.

5. **Prove the credential before crawling.** `deploy/Test-GraphPushPrereqs.ps1`
   reads the *actual* credential the tool will read rather than prompting —
   which is the point, because testing a secret you typed proves nothing about
   the deployment.

6. **Run once by hand, dry-run first.** Confirm the connection registers and the
   run opens.

7. **Run a full crawl.** Not incremental. This is the step that ends the
   security exposure described in section 1, and until it completes and its
   delete sweep runs, every stale ACL in the index is still live.

8. **Reconcile.** Run `deploy/Compare-SourceToIndex.ps1`. This is the only check
   that catches items deleted from the source *during the outage*, which no
   amount of state-store recovery will surface — see the second warning in
   section 2. Treat it as part of the recovery, not as a follow-up.

9. **Re-enable the schedule**, and confirm `/health` returns `ok` with a
   sensible `minutesSinceLastSuccess`.

---

## 6. Credential DR and rotation

This is the sharpest gap in the design, and it is worth being precise about why.

### How each mode actually resolves

Established from
[`TokenCredentialFactory.cs`](../src/Connector.Security/Credentials/TokenCredentialFactory.cs),
[`WindowsCredentialStore.cs`](../src/Connector.Security/Secrets/WindowsCredentialStore.cs)
and [`GraphPushAuth.ps1`](../deploy/GraphPushAuth.ps1):

| `Auth:Mode` | Where the credential lives | Does it survive a host rebuild? |
|---|---|---|
| `Certificate` (default) | Windows certificate store, `LocalMachine\My` or `CurrentUser\My`, selected by `Auth:CertificateThumbprints` in order, then by `Auth:CertificateSubject` newest-first | **Partly.** The certificate must be re-installed on the new host, but it is an exportable artefact that can be held in escrow, and the app registration needs no change if you re-install the same one |
| `ClientSecret` | **Windows Credential Manager**, `CRED_TYPE_GENERIC`, under the target named by `Auth:ClientSecretCredentialTarget` | **No. Not at all.** See below |
| `ManagedIdentity` | The platform | Not available on domain-joined on-premises servers, which is the deployment shape this connector is built for |

### ⚠️ Credential Manager entries do not travel, and this is verified

The claim is that a Credential Manager entry is per-machine **and** per-user, and
does not restore with a database backup or a file copy. That is correct, and the
mechanism is worth stating because it rules out the workarounds people reach
for:

- Generic credentials are encrypted with **DPAPI**, keyed to the user profile
  and, for a domain account, backed by the domain's backup master key. The
  ciphertext in `%LOCALAPPDATA%\Microsoft\Credentials` is not portable: copying
  the file to another machine, or restoring it into another profile, yields
  something that cannot be decrypted.
- It is stored **per user**, so it is not enough to be on the right host — you
  must be the right account. `GraphPushAuth.ps1` says this in its own
  documentation, and it is why `Test-GraphPushPrereqs.ps1` must be run **as the
  service account**: a `PASS` under your own login proves nothing about the
  identity the scheduled task runs as.
- It is not in `ConnectorState`, so no backup in this document contains it.
- **Nothing in this solution can write it.** `WindowsCredentialStore` P/Invokes
  `CredReadW` and `CredFree` and deliberately exposes no `CredWrite`;
  `GraphPushAuth.ps1` mirrors that decision explicitly. Provisioning is
  therefore always an out-of-band act by a human or a deployment system, on the
  new host, as the service account.

**The consequence for DR: on a replacement host under `ClientSecret` mode, the
connector cannot authenticate until somebody re-runs `cmdkey` as the service
account with a secret value that exists nowhere in this repository, nowhere in
the backup, and nowhere in the configuration.** If the only copy of that value
was in the old host's Credential Manager, it is not recoverable — you must issue
a *new* client secret in Entra. That is not a disaster, but it is a step nobody
had written down, it requires app-registration rights that the person holding
the pager may not have, and it is why the RTO for this connector is dominated by
identity rather than by data.

Re-provisioning is:

```
cmdkey /generic:SqlGraphPush/EntraClientSecret /user:entra /pass
```

run **as the service account** — see [`RUNBOOK.md`](RUNBOOK.md) for storing it as
an account that cannot log on interactively — and then verified with
`deploy/Test-GraphPushPrereqs.ps1` **as that same account**.

### ⚠️ Key Vault cannot hold the Entra client secret. The go-live note is wrong about this

It is tempting to conclude that because a `KeyVault:` section already exists,
the fix is to move the client secret into it and shrink the DR surface to "the
new machine can reach the vault". **That is not possible without a code change,
and the reason is structural rather than an oversight.**

In `PushHost.cs` and `ConnectorServer.cs` the secret provider is constructed
*from* the `TokenCredential`:

```csharp
ISecretProvider secrets = new KeyVaultSecretProvider(
    new Uri(options.KeyVault.Uri), credential, Log.Logger);
```

The vault is opened **using** the Entra credential. So the Entra credential can
never come *out* of the vault — you would need it to open the vault that holds
it. `TokenCredentialFactory` reflects this: in `ClientSecret` mode it calls
`WindowsCredentialStore.Read(target)` directly and never goes through
`ISecretProvider` at all. `KeyVaultOptions` confirms the intended scope — its
only well-known key is `SqlPassword`.

The code says so in as many words: *"the credential used to reach the vault has
to come from somewhere that is not the vault"*. Credential Manager exists
precisely to solve that bootstrap problem.

**So the correct recommendation is not Key Vault. It is a certificate.** Under
`Auth:Mode = Certificate` the DR story becomes "install the certificate on the
new host", which is an artefact you can hold in escrow, rotate with overlap, and
audit — and which `Auth:CertificateThumbprints` is already designed to carry two
of at once. Key Vault remains the right answer for the *SQL login password*,
which is what it is wired for, and where `RUNBOOK.md` documents a rotation that
needs no restart at all.

### Rotating a client secret before expiry, without downtime

The good news is that this mode rotates cleanly, for a reason that is easy to
miss. `TokenCredentialFactory` reads the secret **once, at startup**, so that a
missing entry is a deployment failure rather than a token failure mid-crawl.
For the push tools that is not a constraint at all: each scheduled run is a new
process, so a secret overwritten in place is picked up by the very next run with
no restart and no window in which the tool holds a stale value.

⚠️ **The long-running server is the exception.** `ConnectorServer` resolves the
credential at startup and holds it, so an in-place rotation does **not** reach a
running instance. Restart it as part of the rotation, or it keeps presenting the
old secret until the next restart — and if the old secret is deleted in Entra
first, it fails at that point rather than at the rotation.

The sequence, which has no downtime because both secrets are valid in the
middle of it:

1. Add a **new** client secret in Entra. Keep the old one valid.
2. Overwrite the Credential Manager entry with the same `cmdkey` command, as the
   service account. Same target name — nothing in configuration changes.
3. Restart the connector service if one is running. Scheduled push tools need
   nothing.
4. Run `deploy/Test-GraphPushPrereqs.ps1` as the service account, and let one
   real run complete.
5. **Only then** delete the old secret in Entra.

Step 5 last is the whole point. Deleting first turns a rotation into an outage,
and — because of section 1 — an outage nobody notices.

### ⚠️ Nothing watches a client secret's expiry. Verified.

`Auth:ExpiryWarningDays` (default 30) is real, and it is threaded from
`AuthOptions` into `StoreCertificateResolver` and `CertificateSelector`, which
raise a daily warning as a **certificate** approaches expiry. Those warnings
reach the Windows event log and can be alerted on.

It is wired to **certificates only**. Tracing every use of the setting through
the solution shows it reaching `CertificateCriteria` and nothing else: there is
no equivalent check for a client secret anywhere in the code, in
`Test-ConnectorHost.ps1`, or in `Install-Connector.ps1`. An Entra client
secret's expiry date is known only to Entra, and this connector never asks for
it.

The failure mode is therefore silent and total, and it is made worse by
section 1: the secret expires, every subsequent run fails to authenticate,
Copilot keeps serving the corpus exactly as before, and nobody notices until
somebody asks why a deleted record is still searchable. `GraphPushAuth.ps1`
recognises the resulting error and reports it in as many words —

> `AADSTS7000222` — *the client secret has expired*

— but only to whoever is already looking at a failed run.

**Until item 1 in section 7 of [`GO-LIVE-READINESS.md`](GO-LIVE-READINESS.md)
lands, the mitigation is a calendar entry, and it is not optional.** Put the
secret's expiry date in the same calendar as certificate expiry, set it to warn
30 days out, and record the date here:

| Credential | Target / thumbprint | Expires | Owner |
|---|---|---|---|
| *(record the deployed credential and its expiry date here at go-live)* | | | |

A two-year secret creates a two-year gap between the person who created it and
the person who gets the incident. The table costs nothing and is the only thing
standing between those two people.

---

## Where to look next

| | |
|---|---|
| [Crawl state deployment](CRAWL-STATE-DEPLOYMENT.md) | The script order, the delete guard, and what a lost or rewound state database costs |
| [Upgrade and rollback](UPGRADE-RUNBOOK.md) | Which scripts a version needs, how to back out, and the rule that keeps rollback possible |
| [Runbook](RUNBOOK.md) | Certificate rotation, the ACL staleness bound, exit codes |
| [Production onboarding](PRODUCTION-ONBOARDING.md) | Who is woken when a run fails, and which numbers somebody has to accept in writing |
| [Go-live readiness](GO-LIVE-READINESS.md) | Section 7, which orders this work against everything else outstanding |
