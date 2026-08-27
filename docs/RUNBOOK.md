# SqlTicketsConnector — operations runbook

For the team that keeps this running at 03:00. Certificate rotation, secret
rotation, where the logs are, and the five failures you are most likely to meet,
each with the line in the log that identifies it.

Sections 0 to 5 are the agent-hosted connector and the SQL push tools. The
Cloudera CDP connectors are **section 6**: a different host, a different
identity, and a failure vocabulary of their own.

---

## 0. Quick reference

| | |
|---|---|
| Service name | `SqlTicketsConnector` |
| Install path | `C:\Connectors\SqlTickets` |
| Configuration | `C:\Connectors\SqlTickets\appsettings.json` |
| Log file | `C:\Connectors\SqlTickets\Logs\ConnectorLog.log` (10 MB × 30) |
| Event log | Application log, source `SqlTicketsConnector`, `Warning` and above |
| Port | 30303 on loopback, must match `CustomConnectorPortMap.json` in the agent directory |
| Agent service | `GcaHostService` — restart it after any port map change |
| Data source | `sql01.contoso.local` / `Ops`, table `dbo.Tickets`, `SELECT` only |
| Exit codes | `0` clean shutdown, `2` configuration invalid, `3` server failed to start |

Health in one command:

```powershell
Get-Service SqlTicketsConnector, GcaHostService
Get-NetTCPConnection -LocalPort 30303 -State Listen
Get-Content C:\Connectors\SqlTickets\Logs\ConnectorLog.log -Tail 40
```

Or the same ground and rather more, read-only:

```powershell
.\deploy\Test-ConnectorHost.ps1
```

---

## 1. Certificate rotation (no outage)

The connector accepts a **list** of thumbprints and tries them in order, so the
new certificate can be proven before the old one is removed.

1. **Import the new certificate** into `LocalMachine\My` on the agent host,
   including its private key. Mark the key exportable only if
   `Connector:UseTls` is `true` (gRPC Core needs PEM key material).

2. **Grant the service account read on the private key.**
   `certlm.msc` → Personal → Certificates → right-click → All Tasks → Manage
   Private Keys → add the service account with **Read**.
   `Install-Connector.ps1` does this for you and reports what it did.

3. **Upload the public certificate to the app registration** (Entra ID → App
   registrations → the connector app → Certificates & secrets → Certificates).
   Keep the outgoing certificate uploaded until step 6.

4. **Put the new thumbprint first** in `appsettings.json`:

   ```json
   "CertificateThumbprints": [ "<new>", "<current>" ]
   ```

5. **Restart and confirm.** The log names the thumbprint that authenticated:

   ```
   [INF] Certificate A1B2… (CN=sqltickets.contoso.local) selected from LocalMachine\My. Matched by thumbprint, expires 2027-08-13T00:00:00.0000000Z, 2 candidate(s) available.
   [INF] Authenticated to Entra ID with certificate A1B2… (CN=sqltickets.contoso.local), expires 2027-08-13T…
   ```

   If the second line names the **old** thumbprint, the new certificate failed
   authentication; the preceding `Warning` says which one and why. Leave both in
   place and fix the cause before continuing.

6. **Remove the old certificate** from the list, restart, then delete it from the
   store and from the app registration.

**Expiry warnings.** From `Auth:ExpiryWarningDays` (default 30) before expiry,
each daily check writes:

```
[WRN] Certificate A1B2… (CN=…) expires in 12 day(s) on 2026-08-25T…. Start the rotation described in docs/RUNBOOK.md.
```

These reach the event log, so alert on event source `SqlTicketsConnector` at
`Warning` rather than on the file.

**Subject fallback.** If `Auth:CertificateSubject` is set, any certificate with
that subject is considered after the listed thumbprints, newest expiry first. A
renewal from the same issuer is therefore picked up on the next restart even if
nobody edited the configuration — useful, but confirm from the log which
certificate is actually in use.

---

## 2. Secret rotation (no restart)

Only applies when `DataSource:SqlAuthMode` is `SqlLogin`. The shipped
configuration uses `WindowsIntegrated`, which has no secret to rotate.

1. Change the password on the SQL login.
2. Add the new value as a **new version** of the vault secret named in
   `KeyVault:Secrets:SqlPassword`.
3. Do nothing else.

The next connection attempt fails authentication, the connector drops the cached
value, re-reads it from the vault and retries **once**:

```
[WRN] Authentication failed using secret sql-tickets-reader-password. Invalidating the cached value and retrying once. This is the expected path immediately after a credential rotation.
[WRN] Dropped cached secret sql-tickets-reader-password. The next use resolves it again from the secret source.
[WRN] Secret cache miss for sql-tickets-reader-password. Resolving from the configured secret source.
```

A cached value is also refreshed on its own after `KeyVault:SecretCacheTtlMinutes`
(default 60). If the retry also fails, the crawl ends with
`AuthenticationIssue` and the password in the vault does not match the one on the
login — check which vault version is enabled.

To force an immediate refresh across the board, restart the service; nothing is
cached to disk.

---

## 2a. The Entra client secret (`Auth:Mode: ClientSecret`)

Only applies when `Auth:Mode` is `ClientSecret`. With the default `Certificate`
mode there is no client secret and section 1 covers rotation instead.

The secret lives in **Windows Credential Manager**, under the account the
service runs as, and never in `appsettings.json`. Configuration holds only the
entry's name:

```json
"Auth": {
  "Mode": "ClientSecret",
  "ClientSecretCredentialTarget": "SqlTicketsConnector/EntraClientSecret"
}
```

### Storing it

Credential Manager is **per account**. An entry stored while logged in as an
administrator is invisible to the service account, and that is the single most
common cause of `No Credential Manager entry named … is readable by …` at
startup. Store it as the account the service runs as.

**If the service account can log on interactively**, log on as it and run:

```cmd
cmdkey /generic:SqlTicketsConnector/EntraClientSecret /user:<client-id> /pass:<secret>
```

**If it cannot** — a gMSA, or an account denied interactive logon — use one of
these, both of which run `cmdkey` in that account's profile:

```cmd
psexec -u "CONTOSO\svc_gca_reader$" -p ~ cmd /c cmdkey /generic:SqlTicketsConnector/EntraClientSecret /user:<client-id> /pass:<secret>
```

```powershell
# Scheduled task route, no PsExec required
$action = New-ScheduledTaskAction -Execute 'cmdkey.exe' `
  -Argument '/generic:SqlTicketsConnector/EntraClientSecret /user:<client-id> /pass:<secret>'
Register-ScheduledTask -TaskName 'StoreConnectorSecret' -Action $action `
  -User 'CONTOSO\svc_gca_reader$' -LogonType Password
Start-ScheduledTask -TaskName 'StoreConnectorSecret'
Start-Sleep -Seconds 5
Unregister-ScheduledTask -TaskName 'StoreConnectorSecret' -Confirm:$false
```

The secret appears in the command line of both, so it is visible to anything
reading process arguments while it runs, and it lands in the PowerShell history
file if you type the second one interactively. Clear that history afterwards, or
paste the command from a file you then delete. This is the weakest moment in the
whole scheme; certificate mode has no equivalent.

Verify it stored under the right account:

```cmd
cmdkey /list:SqlTicketsConnector/EntraClientSecret
```

### Rotating it

**This one needs a restart**, unlike SQL password rotation. The credential is
read once at startup so that a missing entry fails deployment rather than a
crawl.

1. Add a new client secret to the app registration in Entra. Keep the old one
   valid until step 4.
2. Overwrite the Credential Manager entry with the same `cmdkey` command,
   as the service account.
3. Restart the service. Startup logs
   `Client secret resolved from Credential Manager target …` — the target name,
   never the value.
4. Confirm a crawl completes, then delete the old secret in Entra.

### Expiry

Nothing here warns you. Certificate mode warns daily for 30 days before expiry;
an Entra client secret's expiry is known only to Entra, so track it wherever you
track certificate expiry, and give it a calendar reminder. A secret that expires
unnoticed presents as `AuthenticationIssue` on every crawl.

---

## 3. Where the logs are

| Sink | Location | Level | Use it for |
|---|---|---|---|
| File | `C:\Connectors\SqlTickets\Logs\ConnectorLog.log` | `Logging:MinimumLevel`, default `Information` | Everything. Rolls at 10 MB, 30 files kept. |
| Windows event log | Application log, source `SqlTicketsConnector` | `Warning` and above | SIEM alerting, no file parsing needed. |
| Console | — | Development only | Not present under the service. |
| OTLP | `Logging:Otlp:Endpoint` | As configured | Off by default, and excluded from the default build. See `docs/SECURITY.md` §3. |

Raise verbosity temporarily by setting `Logging:MinimumLevel` to `Debug` (adds
one line per item, **item ID only**) or `Verbose` (adds every `HealthCheck`
poll — noisy, and rarely what you want). Restart the service to apply, and put it
back afterwards.

Correlation: every crawl gets a `CrawlId` GUID that appears on each line for that
crawl.

```powershell
Select-String -Path C:\Connectors\SqlTickets\Logs\ConnectorLog.log -Pattern '<crawl-id>'
```

**What you will not find in the log:** ticket titles, bodies, assignees, any
property value, any connection string, any secret. That is a control, not an
oversight — see `docs/SECURITY.md` LOG-3. To investigate a specific item, use its
item ID (`ticket1234`) and query SQL directly.

**The push tools log somewhere else.** They are not this service and they do not
write to this file. `SqlGraphPush` and `SqlHierarchyPush` write beside their own
executables, and the CDP connectors are covered in section 6.2.

---

## 4. The five most likely failures

These are the five you will recognise on sight. When you *cannot* — when the
report is only "the tickets aren't in Copilot" — work through
[`TROUBLESHOOTING.md`](TROUBLESHOOTING.md) instead: it walks the pipeline one
stage at a time, with a read-only script for each, and tells you which machine
to run it on.

### 4.1 The service account cannot read the certificate's private key

Most common after a host rebuild, a certificate re-import, or a service account
change.

```
[ERR] Certificate A1B2… in LocalMachine\My cannot be used: PrivateKeyUnreadable the private key could not be used: Keyset does not exist Process identity: CONTOSO\svc_gca_reader.
[FTL] Server failed to start.
```

Exit code 3, service stops. Fix: `certlm.msc` → the certificate → All Tasks →
Manage Private Keys → add the identity named in the message with **Read**. Or
re-run `Install-Connector.ps1`, which grants it and reports what it changed.

### 4.2 Configuration placeholders were never replaced

```
[FTL] Configuration in C:\Connectors\SqlTickets\appsettings.json is invalid. 4 problem(s):
Auth:TenantId: must be a GUID.
Auth:ClientId: must be a GUID.
Auth:CertificateThumbprints[0]: must be a 40 character SHA-1 thumbprint in hexadecimal.
Acl:GrantGroupObjectIds[0]: must be a GUID.
```

Exit code 2. Every problem is listed at once by design: fix them all, restart
once. The connector will not start with an unconfigured ACL, because that is the
setting that decides who can see ticket content.

### 4.3 The agent cannot reach the connector (port map or TLS)

Symptom: the connection shows as failed in the admin centre, and this log has
nothing recent in it at all — the calls never arrive.

Check, in order:

```powershell
Get-NetTCPConnection -LocalPort 30303 -State Listen         # is it listening?
Get-Content 'C:\Program Files\Graph connector agent\CustomConnectorPortMap.json'
Get-Service GcaHostService                                   # restarted since the map changed?
```

The startup lines to compare against:

```
[INF] Server started. ConnectorId 9e5e2b95-… listening on localhost:30303 with TLS True. Data source sql01.contoso.local/Ops (WindowsIntegrated). Environment Production.
[INF] Confirm CustomConnectorPortMap.json maps 9e5e2b95-… to 30303, then restart GcaHostService.
```

If `UseTls` is `true`, the agent must trust the issuer of the TLS certificate and
the certificate must be valid for `localhost`. A handshake failure shows on the
agent side, not here — that asymmetry is the clue. Confirm by setting
`Connector:UseTls` to `false` briefly; if calls then arrive, the fault is the TLS
certificate, not the port map.

Related startup failure, if the TLS key is not exportable:

```
[FTL] Server failed to start.
The TLS certificate A1B2… has a private key that cannot be exported, so gRPC Core cannot use it. Re-import the certificate with an exportable key, set Connector:TlsCertificateThumbprint to one that is exportable, or set Connector:UseTls to false and rely on the loopback interface.
```

### 4.3a Connection setup reports that the data source did not respond

Symptom, in the connection wizard rather than in a crawl:

```
The data source did not respond within 20 seconds. Check that sql01.contoso.local/Ops
is reachable from this server and that the firewall allows SQL traffic.
```

This is the connector giving up on purpose. The platform allows a connection
management call 30 seconds and then shows its own timeout instead, so validation
stops at `Connector:ConnectionCallTimeoutSeconds` (20 by default) to return a
message that says what to check.

What it means: TCP to SQL Server is being accepted and then nothing comes back,
or the connection is being dropped silently. Credentials are not the issue —
those produce an authentication error naming the login.

1. From the connector host, prove the port is open:

   ```powershell
   Test-NetConnection sql01.contoso.local -Port 1433
   ```

2. If that succeeds but validation still times out, the listener is answering
   and the instance is not. Check whether SQL Server is in a state that accepts
   connections but stalls on login — an exhausted worker thread pool, or a
   database in recovery.

3. Raise `Connector:ConnectionCallTimeoutSeconds` only if the source is
   legitimately slow and you have measured it. The ceiling is 29: past 30 the
   platform times out first and you are back to a message nobody can act on.
   Startup validation rejects anything higher.

### 4.4 SQL login or permission failure

```
[ERR] ValidateAuthentication failed against sql01.contoso.local/Ops (WindowsIntegrated). Category: Authentication.
[ERR] Full crawl 8f0c… failed after 0 item(s). Category: Authentication.
```

`Category: Authentication` means SQL rejected the identity (error 18456, 4060,
18452 and friends). Check:

- the service really is running as the account the grant was made to
  (`Get-WmiObject Win32_Service -Filter "Name='SqlTicketsConnector'" | Select StartName`);
- `sql/01-least-privilege.sql` was run against the right database;
- the verification query at the end of that script returns exactly one `SELECT`
  grant on `dbo.Tickets`.

`Category: Transient` instead means a timeout, deadlock or reset; the connector
returns `RetryDetails` with exponential backoff and the platform re-drives the
crawl. Repeated transient failures are a SQL health problem, not a connector one.

### 4.5 Items missing from Copilot after an incremental crawl

Watermark drift. Both ends of every incremental crawl are logged:

```
[INF] Incremental crawl 1c2d… started against sql01.contoso.local/Ops (WindowsIntegrated). Watermark in: v2|2026-08-13T09:00:00.0000000Z|1002.
[INF] Incremental crawl 1c2d… finished. Watermark out: v2|2026-08-13T09:35:12.4410000Z|1187.
[INF] Incremental crawl summary: items=42 deleted=3 skipped=0 truncated=1 contentBytes=918233 sqlRoundTrips=1 durationMs=1180 errors={} watermarkIn=… watermarkOut=…
```

Diagnose in this order:

1. **Is the row's `LastModified` actually moving?** The crawl only sees rows
   after the watermark. An application that updates a ticket without touching
   `LastModified` is invisible to it.
2. **Was the item skipped for size?** Look for
   `Item ticket1234 … exceeds the 4 MB platform item limit and was skipped.`
3. **Is the checkpoint stale or in the old format?**
   `Checkpoint marker … is not a watermark this build understands. Falling back to
   the platform supplied crawl start time.` appears once after upgrading from a
   build that checkpointed item IDs. It is safe — the crawl re-reads from the
   platform's crawl start time — but if you see it every crawl, the agent is not
   storing the checkpoint.
4. **Deletes not disappearing?** They are only reported when
   `DataSource:SoftDeleteEnabled` is `true` **and** the application soft-deletes
   (`IsDeleted = 1` plus a `LastModified` touch — see `sql/02-soft-delete.sql`).
   With the flag off you get this every crawl:
   `DataSource:SoftDeleteEnabled is false. Deletions cannot be detected
   incrementally and are only removed from the index by the next periodic full
   crawl.`

To force a full recrawl, use the admin centre's recrawl action for the
connection; do not hand-edit checkpoints.

---

## 5. Routine tasks

**Change the log level**: edit `Logging:MinimumLevel`, restart the service.

**Move the logs**: edit `Logging:Directory`, grant the service account Modify on
the new folder, restart.

**Change the ACL groups**: edit `Acl:GrantGroupObjectIds`, restart, then trigger a
full crawl — existing items keep their old ACL until they are re-indexed.

**Re-seed a connection from scratch** (rare, and it bypasses the agent):
`SqlGraphPush` with its own `appsettings.json`. It uses the same certificate
credential and the same ACL groups, and needs the Graph application permissions
listed in `docs/SECURITY.md` §3.

**The three level connector is a separate deployment.** `SqlHierarchyPush`
(Customer → Engagement → TimeEntry) is not a Windows service, is not installed by
`Install-Connector.ps1`, and shares nothing with this connector at run time — its
own tables, its own Graph connection, its own schema. Nothing in sections 1 to 4
above applies to it. It has its own end-to-end instructions in
[`HIERARCHY-DEPLOYMENT.md`](HIERARCHY-DEPLOYMENT.md), and when it misbehaves the
guide is [`TROUBLESHOOTING-DIRECT-PUSH.md`](TROUBLESHOOTING-DIRECT-PUSH.md).

**Every push tool runs on one engine**, `PushCore` — `SqlGraphPush`,
`SqlHierarchyPush` and `CdpGraphPush` — so they fail the same way and the same
exit codes mean the same things. Another SQL source is a class and a
configuration file rather than another program to learn:
[`ADDING-A-PUSH-CONNECTOR.md`](ADDING-A-PUSH-CONNECTOR.md). The cluster ones
have their own section below, because their identity and their failures are
different: section 6.

Three things about it are worth knowing at 03:00, because they look like faults
and are not:

- **It never deletes.** Rows soft-deleted since the last run are excluded from
  the push, not removed from the index, so a deleted time entry stays findable.
  `deploy/Compare-SourceToIndex.ps1` lists those orphans and prints the `DELETE`
  for each without running it.
- **Schema registration is silent for 5 to 15 minutes.** Watch it with
  `deploy/Watch-SchemaRegistration.ps1`. Do not delete and recreate the
  connection — that restarts the wait and discards every item already written.
- **Its certificate lives in `CurrentUser\My`**, not `LocalMachine\My`, because
  it runs as a person. A certificate in the machine store is invisible to it and
  produces exit code 3.
- **It also supports `Auth:Mode: ClientSecret`**, the same way this connector
  does and through the same code — section 2a applies, with one simplification:
  the tool runs as a person, so the Credential Manager entry is stored as
  *yourself* with a plain `cmdkey` and needs none of the PsExec or scheduled-task
  workarounds a service account forces. There is no service to restart after a
  rotation either; just run it again.

**After any change to `appsettings.json`**, check the first lines after restart:

```
[INF] SqlTicketsConnector starting. Configuration C:\Connectors\SqlTickets\appsettings.json. Environment Production.
```

followed either by `Server started.` or by a `Fatal` listing every problem.

---

## 6. The CDP connectors (`CdpGraphPush`)

A different deployment from everything above. `CdpGraphPush` is a console tool
started by a scheduled task, **not** a Windows service: nothing restarts it,
nothing polls it, and the only thing watching it is the task's **Last Run
Result**, which is the process exit code. It runs on a host that can reach the
Cloudera cluster and `graph.microsoft.com`, under a service account the cluster
already trusts.

Its deployment is [`CDP-DEPLOYMENT.md`](CDP-DEPLOYMENT.md) and its stage-by-stage
diagnosis is [`TROUBLESHOOTING-CDP.md`](TROUBLESHOOTING-CDP.md). What follows is
what an operator needs without reading either.

| Code | Means | Page it as |
|---|---|---|
| `0` | The crawl completed and the watermark advanced over what was written | Nothing. Check `skipped=` is what you expect |
| `2` | Configuration invalid. Nothing opened a socket | A deployment fault, not an incident |
| `3` | A credential was rejected — by **Entra** or by **the cluster** | Credential rotation. Never fold this in with `4` |
| `4` | Ingestion failed part-way | The data path |

### 6.1 Three connectors, one executable

One `CdpGraphPush.exe`, three connectors, chosen by argument. Each has its own
Graph connection and its own configuration file, and the connector key is what
picks the file, so none of them can read another's settings. A file naming
another connector's `Graph:ConnectionId` is rejected at startup rather than
allowed to overwrite its items.

| Connector | Indexes | Connection | Configuration |
|---|---|---|---|
| `cdphdfsdocs` | Files under `Settings:HdfsRoots`, over HttpFS or WebHDFS. Each item carries the grants derived from that file's own POSIX ACL and Ranger | `cdphdfsdocs` | `appsettings.cdphdfsdocs.json` |
| `cdphivecontracts` | Rows of `Source:ItemView`, over ODBC | `cdphivecontracts` | `appsettings.cdphivecontracts.json` |
| `cdpatlascatalog` | The Apache Atlas catalogue. One item per entity — `hive_db` and `hive_table` by default, `hdfs_path` if `Settings:AtlasTypes` asks — carrying name, qualified name, owner, description, columns, Atlas classifications, glossary terms, one dataset hop of lineage each way, and a modified timestamp. A lineage neighbour is named only where everybody granted the entry is granted the neighbour too | `cdpatlascatalog` | `appsettings.cdpatlascatalog.json` |

```powershell
.\CdpGraphPush.exe --connector cdpatlascatalog
```

**The catalogue describes data it may not index, and that is deliberate.** A
table Ranger row-filters or masks never reaches the index as rows, because one
indexed copy cannot show two people different rows or different values. Its
name, its columns and its owner are a different matter: everybody granted select
on that table already sees all three the moment they query it, so its catalogue
entry is indexed for exactly those people and nobody else. The tables whose data
can never be indexed are frequently the ones most worth cataloguing. If somebody
asks why a table nobody can search the contents of is nevertheless findable by
name, that is the answer.

What still refuses a catalogue entry is a **deny** — a description of a table is
still a disclosure about it — and a table no group is granted select on, because
an entry granted to nobody is indexed and then returned to nobody. A
**column-scoped** grant narrows rather than refuses: only the columns the grant
names are described, since a column name discloses by existing.

The connector is deliberately **stricter than the cluster**. CDP ships Atlas
with a Ranger policy called `public` that grants every authenticated user read on
every entity, and Atlas's authorisation is a separate Ranger service (`cm_atlas`)
from Hadoop SQL (`cm_hive`), so a deny on a table's data does not hide that
table's metadata in Atlas. None of
that is mirrored here. "Everyone with a cluster account" and "everyone in the
Microsoft 365 tenant" are different populations, and inheriting the first would
publish the shape of the lake — table names, column names, owners — to people who
cannot reach the cluster at all.

### 6.2 Where the logs and the checkpoints are

| | |
|---|---|
| Log file | `Logs\CdpGraphPush.log` beside the executable, for example `C:\Connectors\Cdp\Logs\CdpGraphPush.log`. Rolls at 10 MB, 30 files kept, `Information` and above |
| Console | The same lines. A scheduled task discards them, so the file is what you read afterwards |
| Checkpoints | `Settings:CheckpointDirectory`, default `state`, relative to the executable unless rooted |

The log file is named after the **executable**, not the connector, so all three
connectors write to one file and an existing deployment's log path does not move
when a connector is added to it. Each run names the connector it is running:

```
02:00:04 [INF] CdpGraphPush starting connector cdpatlascatalog (CDP Atlas catalogue) against
               connection cdpatlascatalog, configuration C:\Connectors\Cdp\appsettings.cdpatlascatalog.json.
```

If the `Logs` directory is not writable, the tool says so on stderr at startup
rather than leaving an empty folder to be discovered during an incident.

One checkpoint per connector, named for the connector key:

```
C:\Connectors\Cdp\state\cdphdfsdocs.watermark.json
C:\Connectors\Cdp\state\cdphivecontracts.watermark.json
C:\Connectors\Cdp\state\cdpatlascatalog.watermark.json
```

Each is written temp-then-rename, so a process killed mid-write leaves the old
checkpoint or the new one and never half of either. An absent, unreadable or
unparseable file is treated as **absent**, and absent means the next run re-reads
and re-writes everything. That is safe, because every write is an upsert, but it
is not free — and it resets `runCount`, which restarts the full-recrawl cadence
in 6.5. Back the `state` directory up with the host, and do not put it anywhere a
cleanup job treats as scratch.

The marker only ever moves to an item whose write returned, so a failed run
cannot advance it past something the index does not have. Re-running resumes; it
does not duplicate.

**The catalogue's checkpoint records where a run reached rather than limiting
what it reads.** Atlas 2.1.0, which is what CDP 7.1.9 ships, cannot filter a
basic search by modification time, so the catalogue is enumerated in full every
run. That is affordable because a catalogue is thousands of entities rather than
millions, and it is stated here plainly rather than left to be inferred from a
run time.

### 6.3 Kerberos and the gMSA — there is no secret to rotate

That is the point of the design, and it is why this section is short. Everything
`CdpGraphPush` does against the cluster — HttpFS or WebHDFS, HiveServer2, Ranger
Admin, Atlas — is Kerberos over SSPI as the identity the process already runs as,
a group managed service account for preference. Active Directory owns that
account's password and rotates it on its own schedule; the process never sees it,
no operator ever types it, and there is nothing on disk for a backup or a support
bundle to leak. Sections 2 and 2a have no cluster equivalent because there is no
stored value to change.

Two things can still break, and neither of them is a rotation:

1. **The host stops being able to obtain a ticket for the account.** A rebuilt or
   renamed host that is no longer in the group named by
   `-PrincipalsAllowedToRetrieveManagedPassword` cannot retrieve the gMSA's
   password, and clock skew beyond the domain's tolerance — five minutes by
   default — invalidates every ticket it does obtain. Check both from the
   connector host:

   ```powershell
   Test-ADServiceAccount -Identity svc-cdp
   w32tm /stripchart /computer:dc01.corp.example /samples:3 /dataonly
   ```

2. **The realm trust stops accepting it.** The connector obtains its ticket from
   Windows, so the cluster's Kerberos realm must trust the Active Directory
   domain — a cross-realm trust, or a cluster whose Kerberos is AD-integrated. If
   that trust or a service principal changes, the ticket is still issued and the
   cluster refuses it. That is a cluster-side change, not a connector one.

Both present the same way, and it is the same exit code as an expired
certificate, deliberately — this identity is no longer accepted, whoever is
refusing it:

```
[FTL] The source rejected this identity.
```

Exit `3`. The Entra half says `The credential was rejected by Entra ID.` or
`Graph rejected the caller (401)` instead, which is how you tell the two apart
without leaving the log.

**The Entra credential is still a certificate, and section 1 still applies** —
with two differences. `Auth:CertificateStoreLocation` is `LocalMachine`, because
this runs as a service account rather than as a person, and the gMSA needs
**Read** on the private key. A certificate in `CurrentUser\My` is invisible to it
and produces exit code `3` for a certificate you can plainly see in
`certmgr.msc`.

**The one mode with a secret at rest is `Settings:KerberosMode: MitKeytab`**, for
a cluster whose realm has no trust to Active Directory. A keytab is a credential
on disk, so it is opt-in, and it becomes an operator's rotation job: re-provision
it whenever the principal's password changes, because nothing in this deployment
warns you that it has stopped working. Note also that SSPI cannot consume an MIT
ticket cache — the two modes are alternatives, not layers.

### 6.4 When Ranger or Atlas is down, the run stops

Deliberately, and for the same reason in both cases. Ranger is what decides which
tables and paths may be copied into an index at all; Atlas is the catalogue
itself. A run that carried on without either would publish part of the answer and
report it as all of it.

```
[FTL] Ingestion failed.
Ranger Admin at https://ranger01.corp.example:6182 could not be reached, so which tables and paths may be indexed is unknown. The run stops rather than indexing a source whose access policies it cannot read.
```

```
[FTL] Ingestion failed.
Atlas at https://atlas01.corp.example:31443 returned 503, so the catalogue cannot be read. The run stops rather than indexing part of it. Check that the Atlas service is healthy - /api/atlas/admin/status answers without authentication and returns ACTIVE on a working instance - and that this host may reach it.
```

There is nothing to change on the connector side of either. Confirm the service
is healthy and that this host can reach it, then re-run: the writes are upserts
and the watermark only moved over items whose write returned, so a re-run resumes
rather than starting again.

Atlas answers a status probe **without authentication**, which makes it the
cheapest first check and one you can run as yourself rather than as the service
account:

```powershell
Invoke-RestMethod https://atlas01.corp.example:31443/api/atlas/admin/status   # expect ACTIVE
```

A healthy instance returns `ACTIVE`. If it does and the run still fails, the
fault is authentication rather than availability, and the exit code says so: a
401 or 403 from Atlas or from Ranger is exit `3`, not `4`, because it is this
identity being refused rather than the service being unwell. Atlas is reached
with SPNEGO as the service account and this connector holds no password to offer
it, so the fix is on the cluster or in the ticket, never in a configuration file.

`Settings:AtlasBaseUrl` is the other thing worth checking before blaming the
service, because it has no default on purpose. Atlas answers on 31443 in a stock
CDP 7.1.9 install, on 21443 upstream, and on Knox's own port and path when Knox
fronts it, and the setting is the base URL **without** `/api/atlas` — the
connector appends the API path itself, which is what lets a Knox path work. A
plain-HTTP URL is refused at startup rather than used.

Two Atlas failures are not fatal, and only on one entity's detail or lineage
read. A single entity deleted between the search and the read answers **404**,
and the connector indexes what it already has and carries on. A lineage request
for an entity Atlas will not serve lineage for answers **400** — it serves only
entities deriving from `DataSet` or `Process`, and a `hive_db` derives from
neither — so that entry loses its lineage rather than the run losing the
catalogue. The connector does not ask for a database's lineage in the first
place; tolerating the 400 is the second line, for a customer type this code
cannot know is not a `DataSet`. Every other status from Atlas stops the run, and
on the search path a 400 stops it too, naming the likely cause: a type name in
`Settings:AtlasTypes` that this cluster's Atlas does not define.

### 6.5 The ACL staleness bound (`Settings:FullRecrawlEveryRuns`)

Default `7`. This is the number to have in the deployment's risk register, and
the reason it exists is worth stating precisely.

**A permission change does not alter a file's modification time.** Revoke a
group's read on a file at the source and the file looks untouched to every
incremental pass, so its item keeps the ACL it was written with, and the people
whose access was revoked keep finding it. The periodic full recrawl is the only
thing in the HDFS crawl that re-derives item ACLs, which makes this setting the
documented **upper bound on ACL staleness**: at a daily schedule and the default
of 7, a revocation at the source can take up to seven days to reach the index.
The log says which runs are full recrawls:

```
[INF] Run 8 is a full recrawl (every 7 runs). Every file is re-read, which is what re-derives
      item ACLs after a permission change at the source and picks up files moved into scope
      with older timestamps.
```

Lower it and pay for it in cluster reads and item writes; if a revocation has to
be immediate, the answer is a live-query surface for that data rather than a
smaller number here.

**`0` does not start.** It would disable the full recrawl, and with it the only
thing that re-derives item ACLs, so startup refuses the configuration with exit
code `2` rather than letting an unbounded ACL staleness arrive by way of an edit
nobody reviewed:

```
[FTL] Configuration in C:\Connectors\Cdp\appsettings.cdphdfsdocs.json is invalid. 1 problem(s):
Settings:FullRecrawlEveryRuns: is 0, which disables the periodic full recrawl. That is also the only thing that re-derives item ACLs after a permission change at the source, because a permission change does not alter a file's modification time. Set it to the number of runs you are willing to have stale ACLs for, or record the decision in the deployment's risk register and set it to 1.
```

The three connectors are not bound by it equally, and an operator asked "how
stale can this be" needs the distinction:

- **`cdphdfsdocs`** — bounded by `Settings:FullRecrawlEveryRuns`, exactly as
  above.
- **`cdpatlascatalog`** — bounded by `Settings:FullRecrawlEveryRuns` too, and
  the shape of the connector invites the opposite conclusion, so it is worth
  being exact. The catalogue *is* enumerated in full every run: Atlas 2.1.0
  cannot filter a basic search by modification time, so there is no incremental
  read to ask for. But reading every entity is not re-deciding every entity. The
  watermark filter runs *before* the routing check, and a Ranger policy edit
  does not change an Atlas entity's modification time — so an entry whose grant
  changed while its entity did not is dropped before any ACL is derived, and
  keeps the ACL it last had until the next full recrawl.
- **`cdphivecontracts`** — a crawl watermarked on `Settings:HiveWatermarkColumn`
  reads only new rows, so rows already indexed keep the ACL they were written
  with. Clearing that setting reads the table whole every run, which is the
  trade to make when a table's grants change more often than its rows do.
