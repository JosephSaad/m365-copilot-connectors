# SqlTicketsConnector — operations runbook

For the team that keeps this running at 03:00. Certificate rotation, secret
rotation, where the logs are, and the five failures you are most likely to meet,
each with the line in the log that identifies it.

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

---

## 4. The five most likely failures

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

**After any change to `appsettings.json`**, check the first lines after restart:

```
[INF] SqlTicketsConnector starting. Configuration C:\Connectors\SqlTickets\appsettings.json. Environment Production.
```

followed either by `Server started.` or by a `Fatal` listing every problem.
