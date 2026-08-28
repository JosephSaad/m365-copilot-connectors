# Deploying the Customer → Engagement → TimeEntry connector

Step-by-step instructions for `SqlHierarchyPush`, the three level connector.
**This is a different deployment from the ticket connector** and shares nothing
with it at run time — different tables, a different Graph connection, a different
schema and a different executable. Both can run against the same tenant and the
same database.

Instructions are given for **.NET 10 and .NET 9** throughout. Take whichever
column matches your toolchain; nothing else differs.

If what you actually want is to index **a different SQL source**, this is not
the document: a new source is one class and one configuration file, and the
recipe is [`ADDING-A-PUSH-CONNECTOR.md`](ADDING-A-PUSH-CONNECTOR.md). Deploying
it then looks exactly like the steps below, with your own names.

| | |
|---|---|
| **What it is** | A console tool an operator runs. **Not** a Windows service, and **not** installed by `Install-Connector.ps1`. |
| **Where it runs** | A workstation or jump box that can reach **both** SQL Server and `graph.microsoft.com`. |
| **What it needs on the host** | Nothing but the package. The runtime is bundled. |
| **Graph connection** | `consultingwork` (configurable; any connection another connector registered is refused by the schema-ownership check). |
| **Exit codes** | `0` success · `2` configuration invalid · `3` credential · `4` ingestion |

If you are looking for the agent-hosted ticket connector instead, that is
[`../README.md`](../README.md) and `Install-Connector.ps1`. If you want to
understand *why* this design flattens the hierarchy before reading how to deploy
it, read [`HIERARCHY-TEST-CASE.md`](HIERARCHY-TEST-CASE.md) first — twenty
minutes there saves an afternoon here.

---

## Step 0 — Prerequisites

**On the machine that will run the push:**

- A route to SQL Server on 1433 **and** outbound HTTPS to `graph.microsoft.com`.
  This is the requirement people miss: a jump box usually has one or the other.
- **A credential.** Either a certificate in **`CurrentUser\My`** with its private
  key — *not* `LocalMachine\My`, this tool runs as a person — or a client secret
  in Windows Credential Manager. Both are supported; see
  [Step 3b](#step-3b--using-a-client-secret-instead-of-a-certificate).
- PowerShell 5.1 or later for the verification scripts, and the
  `Microsoft.Graph.Authentication` module for `Test-HierarchySearch.ps1`.

**On the build machine, only if you are building rather than using a release:**

| | .NET 10 | .NET 9 |
|---|---|---|
| SDK | .NET 10 SDK | .NET 9 SDK |
| IDE | Visual Studio 2026 | Visual Studio 2022 17.12+ |
| Branch | `main` | `release/net9`, or `main` with a build flag |

**In the tenant:** an app registration with `ExternalConnection.ReadWrite.OwnedBy`
and `ExternalItem.ReadWrite.OwnedBy`, admin-consented — see
[`APP-REGISTRATION.md`](APP-REGISTRATION.md) §4, which covers whether to reuse
the `SqlGraphPush` registration or create a second one.

---

## Step 1 — Get the binaries

### Option A: take a release (recommended)

| | |
|---|---|
| **.NET 10** | [Latest release](https://github.com/JosephSaad/m365-copilot-connectors/releases/latest) → `SqlTicketsConnector-<tag>.zip` |
| **.NET 9** | [Latest `-net9` release](https://github.com/JosephSaad/m365-copilot-connectors/releases?q=net9&expanded=true) → `SqlTicketsConnector-<tag>-net9.zip` |

Both are self-contained: the runtime is inside, and the host needs neither .NET
installed. Verify the checksum before unzipping — a document library round trip
is exactly the sort of hop that truncates a file quietly:

```powershell
$zip = Get-Item .\SqlTicketsConnector-<tag>.zip
$expected = (Get-Content "$($zip.Name).sha256").Split(' ')[0]
$actual = (Get-FileHash $zip -Algorithm SHA256).Hash.ToLower()
if ($actual -ne $expected) { throw "Checksum mismatch. Do not deploy this package." }
Unblock-File $zip
Expand-Archive $zip -DestinationPath C:\Staging\Hierarchy
```

The tool is at `SqlHierarchyPush\SqlHierarchyPush.exe`; the SQL scripts are in
`sql\`, and the verification scripts in `deploy\`.

### Option B: build it

**.NET 10**, from `main`:

```powershell
dotnet build .\SqlTicketsConnector.sln -c Release
dotnet publish .\src\SqlHierarchyPush\SqlHierarchyPush.csproj -c Release -r win-x64 --self-contained true -o .\out\SqlHierarchyPush
```

**.NET 9**, either from the `release/net9` branch with no arguments:

```powershell
git clone --branch release/net9 https://github.com/JosephSaad/m365-copilot-connectors.git
dotnet build .\SqlTicketsConnector.sln -c Release
dotnet publish .\src\SqlHierarchyPush\SqlHierarchyPush.csproj -c Release -r win-x64 --self-contained true -o .\out\SqlHierarchyPush
```

…or from `main` without switching branches, since the target framework lives in
one file:

```powershell
dotnet build .\SqlTicketsConnector.sln -c Release -p:ConnectorTargetFramework=net9.0
dotnet publish .\src\SqlHierarchyPush\SqlHierarchyPush.csproj -c Release -r win-x64 --self-contained true -o .\out\SqlHierarchyPush -p:ConnectorTargetFramework=net9.0
```

`Build.ps1 -SelfContained -TargetFramework net9.0` builds the whole package the
same way and puts the framework in the zip name, because two packages differing
only in their bundled runtime are otherwise indistinguishable on a server.

---

## Step 2 — The database

Four scripts, in order, against the database named in `DataSource:Database`.
They create their own tables and touch nothing belonging to the ticket test
case.

```powershell
sqlcmd -S sql01.contoso.local -d Ops -i sql\10-timesheet-source.sql
sqlcmd -S sql01.contoso.local -d Ops -i sql\11-timesheet-sample-data.sql
sqlcmd -S sql01.contoso.local -d Ops -i sql\12-timesheet-views.sql
sqlcmd -S sql01.contoso.local -d Ops -i sql\13-timesheet-least-privilege.sql
```

| Script | What it does | Edit before running? |
|---|---|---|
| `10-timesheet-source.sql` | `Customers`, `Engagements`, `TimeEntries` with foreign keys, `IsDeleted`, `LastModified` | Change `USE [Ops]` if your database differs |
| `11-timesheet-sample-data.sql` | 12 customers, 62 engagements, 1052 time entries | **Skip this against real data.** It deletes and reinserts IDs 1–99, 101–999 and 5001–9999 |
| `12-timesheet-views.sql` | The flattening layer — three views plus `dbo.vwExternalItems` | Requires **SQL Server 2017+** for `STRING_AGG` |
| `13-timesheet-least-privilege.sql` | `SELECT` on the views only, base tables denied | **Yes** — set the principal name, and pick the variant matching your `SqlAuthMode` |

**Before going further, settle the question this whole design exists to answer**,
while it is still cheap. Script 12 ends with it:

```sql
SELECT ItemType, COUNT(*) FROM dbo.vwExternalItems
WHERE Content LIKE N'%Contoso Financial Services%' GROUP BY ItemType;
```

Three rows back — `Customer`, `Engagement`, `TimeEntry` — and the flattening
works. One row back, and nothing you do in Graph will fix it.

---

## Step 3 — Configure

Edit `appsettings.json` beside the executable. Same shape as the connector's,
plus `Graph` and `Source`.

```json
{
  "Environment": "Production",
  "Auth": {
    "Mode": "Certificate",
    "TenantId": "<your tenant GUID>",
    "ClientId": "<the app registration's client ID>",
    "CertificateStoreLocation": "CurrentUser",
    "CertificateThumbprints": [ "<40 hex characters>" ]
  },
  "DataSource": {
    "Server": "sql01.contoso.local",
    "Database": "Ops",
    "SqlAuthMode": "WindowsIntegrated"
  },
  "Acl": { "GrantGroupObjectIds": [ "<Entra group object ID>" ] },
  "Graph": { "ConnectionId": "consultingwork" },
  "Source": { "ItemView": "dbo.vwExternalItems", "MaxItems": 0 }
}
```

Four settings decide whether this works:

**`Auth:CertificateStoreLocation` is `CurrentUser`.** Not `LocalMachine`. A
certificate imported to the machine store is invisible here, and produces exit
code 3 for a certificate you can plainly see in `certlm.msc`. If you are using a
client secret instead, this setting is ignored — see
[Step 3b](#step-3b--using-a-client-secret-instead-of-a-certificate).

**`Graph:ConnectionId` must be this connector's own connection.** Before
pushing anything, the engine fetches the schema already registered on the
connection and refuses to continue if it carries any property this connector
does not build — a connection another connector registered fails with the
foreign properties named, before a single item is overwritten. A registered
schema cannot be changed, and `OwnedBy` means whichever app created a
connection is the only one that can manage it, so the only recovery from a
collision is a different connection ID.

**`Acl:GrantGroupObjectIds` is written into every item at push time.** An empty
or wrong value means items nobody can find, and correcting it later requires
pushing every item again — there is no ACL-only update.

**No secret ever goes in this file.** Vault URI, secret *names*, the Credential
Manager target *name*, tenant ID, client ID, thumbprints, server and database
only. The build fails on anything else — see [`SECURITY.md`](SECURITY.md) SEC-1.

---

## Step 3b — Using a client secret instead of a certificate

**Yes, this connector supports client secret authentication.** It is the same
mechanism the agent-hosted connector uses, through the same shared code in
`Connector.Security` — nothing about it is specific to one tool.

Certificate remains the default and the better option: a secret is a bearer
credential that anyone who reads it can replay from anywhere, and unlike a
certificate **nothing warns you before it expires**. Use this when the tenant
will not issue a certificate to this application.

### What goes where

```json
"Auth": {
  "Mode": "ClientSecret",
  "TenantId": "<your tenant GUID>",
  "ClientId": "<the app registration's client ID>",
  "ClientSecretCredentialTarget": "SqlHierarchyPush/EntraClientSecret"
}
```

`CertificateThumbprints` and `CertificateStoreLocation` are ignored in this mode.
**The secret itself never appears in this file** — only the *name* of the
Credential Manager entry holding it. Paste the secret into that field by mistake
and startup rejects it: the value is shape-checked, and anything that looks like
a secret rather than a name fails validation with exit code 2.

### Storing it

Credential Manager is **per Windows account**. This is where deploying the push
tool is genuinely easier than deploying the connector service: the connector runs
as a service account that often cannot log on interactively, which is why
[`RUNBOOK.md`](RUNBOOK.md) §2a needs PsExec and scheduled-task workarounds. This
tool runs **as you**. So store it as yourself, in one line:

```cmd
cmdkey /generic:SqlHierarchyPush/EntraClientSecret /user:<client-id> /pass:<secret>
```

Confirm it landed:

```cmd
cmdkey /list:SqlHierarchyPush/EntraClientSecret
```

Two cautions that apply to that command and not to certificates. The secret
appears in the process command line while it runs, so anything reading process
arguments can see it; and typed interactively it lands in your PowerShell history
file. Clear the history afterwards, or paste the command from a file you then
delete. This is the weakest moment in the whole scheme, and certificate mode has
no equivalent to it.

If a different account will run the push — a scheduled task, say — the entry has
to be stored under **that** account. `RUNBOOK.md` §2a has both routes for that
case, and they apply here unchanged.

### What the tool does with it

The entry is read **once, at startup**, before any Graph call. A missing or
unreadable entry is therefore a deployment failure you see immediately, not a
token failure partway through a push:

```
[FTL] Could not build the Entra credential.
```

…and exit code **3**. The startup log names the *target*, never the value.

**Windows only.** Credential Manager is a Windows facility, so this mode fails
with a clear message on any other platform rather than a `PlatformNotSupported`
exception from deep inside a P/Invoke. Certificate mode has no such restriction.

### Rotating it

Simpler than the service case, because there is no service to restart:

1. Add a new client secret to the app registration. Keep the old one valid.
2. Overwrite the Credential Manager entry with the same `cmdkey` command.
3. Run the tool again and confirm it completes.
4. Delete the old secret in Entra.

**Nothing warns you before expiry.** A certificate warns daily for 30 days
(`Auth:ExpiryWarningDays`); an Entra client secret's expiry is known only to
Entra. Put it in the same calendar you use for certificate expiry, or the first
symptom will be `AADSTS7000222` on a run that worked last month.

### Verifying it

`Test-GraphPushPrereqs.ps1` reads the **actual Credential Manager entry** the
tool will read, rather than prompting — testing a secret you typed would prove
nothing about the deployment. It tells you which happened:

```
  PASS  client secret read from Credential Manager target 'SqlHierarchyPush/EntraClientSecret'
```

A `WARN` saying it fell back to prompting means the entry is not readable by the
account running the script. If that account is the one that runs the push, the
push will fail at startup until you fix it.

---

## Step 4 — Pre-flight

Settle identity and ownership before spending fifteen minutes on schema
registration:

```powershell
.\deploy\Test-GraphPushPrereqs.ps1 -ConfigPath .\SqlHierarchyPush\appsettings.json
```

It decodes the token it acquires and lists the application permissions actually
consented — which is the only client-side way to tell a missing consent from a
wrong connection owner, since both arrive as a bare 403.

Then check the source, ideally as the identity that will read it:

```powershell
.\deploy\Test-SqlSource.ps1 -ConfigPath .\SqlHierarchyPush\appsettings.json
```

---

## Step 5 — Dry run

Read the source and report exactly what would be written, writing nothing.
Three guards run at the desk: the schema builds (so the irrecoverable
searchable/refinable and name-length rules fire here, not against the tenant),
every row maps, and — when the tenant is reachable — a read-only GET checks the
connection is not another connector's. If Graph is unreachable the ownership
check is skipped with a note and the dry run continues; its main job needs only
SQL.

```powershell
.\SqlHierarchyPush\SqlHierarchyPush.exe --dry-run
```

Expect a line per item and a summary counting all three levels:

```
Dry run complete. 1126 row(s) processed (Customer=12, Engagement=62, TimeEntry=1052) for connection consultingwork; 1126 distinct item(s). truncated=0 skipped=0 duplicates=0 throttleWaits=0
```

`duplicates=` above zero means the view returned more than one row per item ID
— the later row would silently overwrite the earlier item in the real push.

If the level counts are wrong, the problem is in the views, not in Graph. Go
back to step 2.

---

## Step 6 — First push

```powershell
.\SqlHierarchyPush\SqlHierarchyPush.exe
```

The first run creates the connection, registers the schema, waits for it, then
writes every item. **Schema registration takes 5 to 15 minutes and reports no
progress while it runs.** That silence is why people conclude it has hung and
delete the connection — the one action that makes things worse, because it
restarts the same wait and discards everything already written.

Watch it properly from a second window:

```powershell
.\deploy\Watch-SchemaRegistration.ps1 -ConfigPath .\SqlHierarchyPush\appsettings.json
```

**Read the schema it prints when the state reaches `ready`.** After that the
schema is append-only: properties can be added, but no property's type,
annotations or labels can be changed and none can be removed. Correcting a
mistake means deleting the connection — and every item in it — and starting
again, including the wait.

A timeout has cancelled nothing. Registration continues server-side whether or
not the tool that started it is still running; re-run the watcher, not the push.

---

## Step 7 — Verify

```powershell
.\deploy\Test-HierarchySearch.ps1
```

Run it signed in as **a person in the ACL group**. `POST /search/query` has no
app-only form, and results are security-trimmed, so what a user can find is the
only meaningful question. It checks the searchable annotations, then searches one
customer and groups the hits by level. All three levels must appear.

Then reconcile the index against the source row by row:

```powershell
.\deploy\Compare-SourceToIndex.ps1 -ConfigPath .\SqlHierarchyPush\appsettings.json
```

---

## Step 8 — Ask Copilot

Allow longer than search: the semantic index is built independently from the same
content and lags it. Prompts that exercise the flattening rather than plain
retrieval:

- *What work have we done for Contoso Financial Services?*
- *Who has logged time against the Data Platform Migration?*
- *What is Priya Raman working on?*
- *Summarise the Northwind Health engagements and their status.*

A weak answer when `Test-HierarchySearch.ps1` passed means semantic indexing has
not caught up — not that the flattening failed. That distinction is the reason to
run step 7 first.

---

## Routine operation

**To refresh the index**, run the tool again. It re-reads and re-writes every
item; there is no incremental mode and nothing runs it on a schedule. Put it on
a scheduled task if you want it regular, and read the caveat below first.

**It never deletes.** Rows soft-deleted since the last run are *excluded from the
push*, not removed from the index — so a deleted time entry stays searchable and
citable. Find those orphans, and get the exact `DELETE` for each printed rather
than run:

```powershell
.\deploy\Compare-SourceToIndex.ps1 -ConfigPath .\SqlHierarchyPush\appsettings.json
```

If that list keeps growing, this path is being used as a synchroniser, which it
is not. The agent-hosted connector removes deletions incrementally and needs none
of this.

**After renaming a customer**, push again. Every descendant item carries the old
name until it is rewritten — that is the cost of doing the join at write time.

**Certificate rotation** works exactly as in [`RUNBOOK.md`](RUNBOOK.md) §1, with
`CurrentUser\My` in place of `LocalMachine\My`.

---

## Removing it

Deleting the Graph connection deletes every item in it, and cannot be undone:

```powershell
Connect-MgGraph -Scopes 'ExternalConnection.ReadWrite.OwnedBy'
Invoke-MgGraphRequest -Method DELETE -Uri 'v1.0/external/connections/consultingwork'
```

That must run as **the owning app**, or under a delegated `.All` scope — the
interactive PowerShell app does not own this connection. Dropping the SQL objects
is `DROP VIEW`/`DROP TABLE` in reverse dependency order; the ticket test case is
unaffected either way.

---

## When something goes wrong

| Symptom | Where to look |
|---|---|
| `FATAL:` before any log line, exit code 2 | Configuration. Every problem is listed at once |
| Exit code 3 | The credential. With `Certificate`, almost always `CurrentUser` vs `LocalMachine`; with `ClientSecret`, a Credential Manager entry the running account cannot read |
| `AADSTS7000222` | The client secret has **expired**. Nothing warned you — see [Step 3b](#step-3b--using-a-client-secret-instead-of-a-certificate) |
| `AADSTS7000215` | Invalid client secret. Check the entry holds the value and not the secret *ID* from the Entra blade |
| A bare 403 from any Graph call | `Test-GraphPushPrereqs.ps1`; consent and ownership are indistinguishable without it |
| Stuck in `draft`, then a timeout | Step 6. Re-run the watcher, do not recreate the connection |
| Dry run shows the wrong level counts | The views, step 2 |
| Search returns customers but no time entries | The flattening. Run script 12's verification query 3 |
| Deleted rows still in Copilot | Expected. `Compare-SourceToIndex.ps1` |

Everything else is in
[`TROUBLESHOOTING-DIRECT-PUSH.md`](TROUBLESHOOTING-DIRECT-PUSH.md), which applies
to this tool in full — it is the same pipeline with a different source.
