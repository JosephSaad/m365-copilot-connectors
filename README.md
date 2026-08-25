# SQL Tickets Copilot Connector

[![build](https://github.com/JosephSaad/m365-copilot-sql-connector/actions/workflows/build.yml/badge.svg)](https://github.com/JosephSaad/m365-copilot-sql-connector/actions/workflows/build.yml)
[![licence: MIT](https://img.shields.io/badge/licence-MIT-blue.svg)](LICENSE)

A Visual Studio solution containing working paths from SQL Server to Microsoft
365 Copilot grounding data, hardened for deployment into a regulated
environment: one behind the Graph connector agent, and two that push straight to
Microsoft Graph — a flat table, and a three level hierarchy.

Generated against the contracts extracted from `GraphConnectorsTemplate.vsix`
(`ms-graph-connectors.graphConnectors`, v3.4). The five `.proto` files under
`src/SqlTicketsConnector/Contracts/` are Microsoft's originals, copied byte for
byte and unmodified.

![Data flow from SQL Server through the connector and agent to Microsoft Graph, the semantic index, and Copilot](docs/architecture.svg)

| Project | Model | Runs where |
|---|---|---|
| `SqlTicketsConnector` | gRPC server behind the Graph connector agent. **Never calls Microsoft Graph.** | On-premises Windows Server with the agent installed |
| `SqlTicketsConnector.Security` | Shared secrets, certificates, credentials, SQL connections, log redaction | Class library, referenced by all three |
| `SqlGraphPush` | Direct `PUT /external/connections/{id}/items/{itemId}` — one flat table | Operator workstation or jump box with outbound HTTPS to Graph |
| `SqlHierarchyPush` | The same, for a three level hierarchy: Customer → Engagement → TimeEntry | Same |

Connector ID: `9e5e2b95-e7ab-4266-98c7-4f7868d377bf`
Default port: `30303`

> **This is the `release/net9` branch — the .NET 9 line, for Visual Studio 2022.**
> `main` targets `net10.0`, which Visual Studio 2022 cannot open. The only
> differences here are the target framework in `Directory.Build.props`, the SDK
> pinned in `global.json`, and the package list that follows from them. Fixes
> come from `main`; releases from this branch are tagged `-net9`.

**Before reviewing or deploying this, read [`docs/SECURITY.md`](docs/SECURITY.md)
(control mapping), [`docs/RUNBOOK.md`](docs/RUNBOOK.md) (rotation and failure
modes) and [`docs/ASSUMPTIONS.md`](docs/ASSUMPTIONS.md).**

**Deploying the three level connector** (Customer → Engagement → TimeEntry) is a
separate procedure with its own document:
[`docs/HIERARCHY-DEPLOYMENT.md`](docs/HIERARCHY-DEPLOYMENT.md), covering .NET 10
and .NET 9.

**When something is wrong and you do not yet know what**, work through
[`docs/TROUBLESHOOTING.md`](docs/TROUBLESHOOTING.md): one stage at a time from
the SQL row to the answer in Copilot, with a read-only script for each. The
direct push path fails differently and has its own —
[`docs/TROUBLESHOOTING-DIRECT-PUSH.md`](docs/TROUBLESHOOTING-DIRECT-PUSH.md).

---

## Download

Two lines, same code, different target framework. Take the one your toolchain
can open — Visual Studio 2022 cannot load a `net10.0` project whatever SDK is
installed.

| Release | Framework | For |
|---|---|---|
| [**Latest `-net9` release**](https://github.com/JosephSaad/m365-copilot-sql-connector/releases?q=net9&expanded=true) | `net9.0` | **This branch.** Visual Studio 2022 17.12 or later, or the .NET 9 SDK |
| [**Latest release**](https://github.com/JosephSaad/m365-copilot-sql-connector/releases/latest) | `net10.0` | `main`. Visual Studio 2026, or the .NET 10 SDK |

Both lines are released together and carry the same version number. These links
follow the current one rather than naming a version, so they cannot rot: the
first lists the `-net9` line newest first, the second resolves to whichever
release is marked latest.

Each carries the same four assets:

| Asset | Size | When you need it |
|---|---|---|
| `SqlTicketsConnector-<tag>.zip` | ~90 MB | Always. Self-contained build plus a buildable source tree — the only file a deployment needs |
| `offline-packages-<tag>.zip` | ~220 MB | Only to rebuild on a machine with no route to `api.nuget.org` |
| `…zip.sha256` | | Verify before deploying; a document library round trip is exactly the sort of hop that truncates a file quietly |

Nothing else is required on the target server: the .NET runtime is inside the
package. See [Transfer via SharePoint](#transfer-via-sharepoint) for the
download, unblock and checksum sequence.

---

## Contents

```
SqlTicketsConnector.sln
Build.ps1                              Scan + build + test + audit + publish + zip
.github/
  workflows/build.yml                  CI: build, test, package, attach to a release
  dependabot.yml                       Weekly NuGet updates; pinned packages ignored
.gitleaks.toml                         Secret scanning rules
.pre-commit-config.yaml                Pre-commit hooks (gitleaks, key material, appsettings scan)
build/
  SecretHygiene.targets                Build fails on a secret-shaped key with a value
  SecretHygiene.proj                   Repository-wide entry point for the same scan
  Get-OfflinePackages.ps1              Downloads every NuGet package, for an air-gapped build
  Test-OfflinePackageList.ps1          CI check: that list against the real restore graph
  NuGet.offline.config                 Copy to the root as NuGet.config to stop restore using the network
deploy/
  Install-Connector.ps1                Server-side install, run elevated
  Test-ConnectorHost.ps1               Diagnose the host: config, service, cert, port, port map, SQL
  Test-SqlSource.ps1                   Diagnose the source: grant, columns, watermark health, item sizes
  Get-CrawlHistory.ps1                 Reconstruct every crawl from the log, and check the watermark chain
  Verify-GraphConnection.ps1           Post-deploy: connection, schema, item, search
  GraphPushAuth.ps1                    Shared app-only auth for the SqlGraphPush scripts below
  Test-GraphPushPrereqs.ps1            SqlGraphPush pre-flight: token, consented roles, connection ownership
  Watch-SchemaRegistration.ps1         The draft to ready wait, every state explained, the schema printed
  Compare-SourceToIndex.ps1            Reconcile SQL against the index; finds the orphans a push leaves behind
  Test-HierarchySearch.ps1             Proves a customer search returns engagement and time entry items too
  CustomConnectorPortMap.json          Reference copy of the agent port map entry
  Manifest.json                        Uploaded in the admin center wizard
  ConnectionInfo.json                  TestApp input, no credentials
docs/
  architecture.svg                     The data flow drawing embedded above
  hierarchy-flow.svg                   How the three level source is flattened into flat index items
  SECURITY.md                          Control mapping for the security reviewer
  APP-REGISTRATION.md                  Entra apps, permission by permission, cert and secret
  RUNBOOK.md                           Rotation, log locations, five failure modes
  TROUBLESHOOTING.md                   Stage by stage, SQL to Copilot, when you do not yet know what broke
  TROUBLESHOOTING-DIRECT-PUSH.md       The same for SqlGraphPush, which fails differently
  HIERARCHY-TEST-CASE.md               The three level test case: why a flat index needs flattening, and how
  HIERARCHY-DEPLOYMENT.md              Step-by-step deployment of the three level connector, net10 and net9
  ASSUMPTIONS.md                       Decisions, deviations, open questions
  GENESIS-PROMPT.md                    The prompt that produces this repository, and why it looks like this
  agent-bypass-tradeoffs.pptx          Deck: the ten features the agent provides and a direct push forgoes
  hierarchy-in-copilot.pptx            Deck: how to handle hierarchical data in a flat index
sql/
  00-sample-source.sql                 Creates dbo.Tickets (with IsDeleted) and seeds 3 rows
  01-least-privilege.sql               Login, user and SELECT grant, with verification
  02-soft-delete.sql                   IsDeleted column and composite watermark index
  10-timesheet-source.sql              Three level test case: Customers, Engagements, TimeEntries
  11-timesheet-sample-data.sql         12 customers, 62 engagements, 1052 time entries
  12-timesheet-views.sql               The flattening layer — the whole test case is here
  13-timesheet-least-privilege.sql     SELECT on the views only, base tables denied
src/
  SqlTicketsConnector/
    Contracts/*.proto                  Microsoft contracts, unmodified
    Connector/                         Service implementations, SQL source, ACLs, watermark
    Logging/                           Redaction policy, interceptor, metrics, sink setup
    Server/                            Options, validation, host
    appsettings.json                   Non-sensitive configuration only
  SqlTicketsConnector.Security/
    Secrets/                           ISecretProvider, Key Vault, environment, cache, retry
    Certificates/                      Store resolver, selection rules, expiry, process identity
    Credentials/                       TokenCredential factory, rotating certificate credential
    Sql/                               Connection string rules, error classification, factory
    Logging/                           Scrubber, enricher, redacted exception
    Configuration/                     Shared options and validation
    Schema/                            The two Graph schema rules that cannot be undone once registered
  SqlGraphPush/
    Program.cs, PushOptions.cs         Six property schema in TicketSchema.cs
    appsettings.json
  SqlHierarchyPush/
    Program.cs, HierarchyOptions.cs    Three level test case; same Security engine, its own schema
    HierarchySchema.cs                 26 properties, each annotation explained
    appsettings.json
tests/
  SqlTicketsConnector.Tests/           82 tests, no live tenant, vault or database
```

---

## Prerequisites

**Build machine**
- Visual Studio 2022 17.12 or later with the .NET desktop development workload,
  or the .NET 9 SDK alone. `global.json` pins the SDK to 9.0.x, so a machine with
  newer SDKs installed still builds this branch with .NET 9
- Nothing here needs Visual Studio 2026 or the .NET 10 SDK — that is `main`
- NuGet access to `api.nuget.org`, or a package folder staged from a connected
  machine — see [Building without NuGet access](#building-without-nuget-access)

**Target server** — if you deploy the release zip, this is the whole list
- Windows Server 2019 or later
- Microsoft Graph connector agent from https://aka.ms/gca, already registered
  against the tenant — see [`docs/APP-REGISTRATION.md`](docs/APP-REGISTRATION.md)
  for the app registrations, permissions and network allow-list it needs
- The connector's client certificate in `LocalMachine\My`, with its private key readable by the service account
- Network path to SQL Server

**No .NET runtime is required.** The release package is self-contained: the
runtime ships inside it. The agent and the certificate are the only two things
that cannot be in the zip — the agent is Microsoft's installer, tied to your
tenant, and the certificate has to come from your own PKI.
You do not need the VSIX installed to build this solution.

---

## Build

```powershell
dotnet build .\SqlTicketsConnector.sln -c Release
dotnet test  .\SqlTicketsConnector.sln
```

`Grpc.Tools` runs `protoc` during the first build and generates the C# types from
the `.proto` files. Those generated files land in `obj\` and are not checked in,
so a clean clone always needs a restore before the code will resolve in the
editor.

Expected warnings, and only these: `NETSDK1206` (Grpc.Core ships RID-specific
native assets) and `CS8981` (protoc emits all-lowercase identifiers). Both are
suppressed in the project file with a comment explaining why.

To produce the transfer package:

```powershell
.\Build.ps1
.\Build.ps1 -SelfContained          # server has no .NET 9 runtime
.\Build.ps1 -EnableOtlpExporter     # include the optional OpenTelemetry sink
```

`Build.ps1` runs four gates before it publishes anything — the secret hygiene
scan, a build with warnings treated as errors, the tests, and the dependency
audit — and refuses to package certificate or key material. They are the same
four the CI workflow runs. Output:
`SqlTicketsConnector-deploy-<timestamp>.zip` in the solution root.

CI produces the same package on every push to `main`, downloadable from the
workflow run, and it builds `-SelfContained` so the zip carries its own runtime.
A step then extracts the package and fails the build if the service executable,
the bundled runtime, the install script, the SQL scripts or the docs are
missing — "everything is in the zip" is checked, not assumed.

Pushing a `v*` tag additionally creates a **draft** release with the zip and its
`.sha256` attached, named after the tag; publishing it stays a human decision.

### What the package contains

```
publish/            the connector service, with the .NET runtime bundled
SqlGraphPush/       the direct push tool, also self-contained
source/             a buildable copy of this repository, no bin/ or obj/
sql/                least-privilege grant, soft-delete migration, sample table
docs/               SECURITY.md, RUNBOOK.md, ASSUMPTIONS.md
Install-Connector.ps1, Manifest.json, ConnectionInfo.json,
CustomConnectorPortMap.json, README.md
```

To rebuild from the package rather than from a clone:

```powershell
cd source
dotnet build .\SqlTicketsConnector.sln -c Release      # needs the .NET 10 SDK
```

That still restores from NuGet. If the machine cannot reach it, see
[Building without NuGet access](#building-without-nuget-access); the two scripts
that make it possible travel inside `source\build\`.

CI fails the build if any of the above is missing from the archive, and if
`bin/`, `obj/` or `.git/` leak into `source/`.

### Relationship to main

`main` targets `net10.0`; this branch targets `net9.0`, because Visual Studio
2022 has no .NET 10 support and will not load a `net10.0` project whatever SDK is
installed. The difference is two files:

| File | Here | On `main` |
|---|---|---|
| `Directory.Build.props` | `ConnectorTargetFramework` is `net9.0` | `net10.0` |
| `global.json` | pins the SDK to 9.0.x | absent |

Everything else — the connector, the security library, the tests, the gates, the
docs — is the same code. Take fixes from `main` and keep it that way; CI on
`main` builds and tests `net9.0` on every push, with the .NET 9 SDK alone, so
that branch cannot quietly break this one between releases.

The package list under `build\` is larger here (80 packages rather than 68):
`net9.0` needs `System.Text.Json`, `System.IO.Pipelines` and their neighbours as
NuGet packages, where `net10.0` has them in the shared framework.

To build this tree the way `main` does, without switching branches:

```powershell
dotnet build .\SqlTicketsConnector.sln -c Release -p:ConnectorTargetFramework=net10.0
```

Nothing changes for the target server. The release packages are self-contained,
so the bundled runtime is whichever one the package was built against — .NET 9
for releases tagged `-net9` — and the server needs neither installed.

### Building without NuGet access

A build machine on a segregated network cannot reach `api.nuget.org`, and a
restore that cannot reach it fails with `NU1301` rather than anything that names
the real problem. Stage the packages from a connected machine instead:

```powershell
.\build\Get-OfflinePackages.ps1                      # writes .\offline-packages
Compress-Archive .\offline-packages\* offline-packages.zip
```

Or skip that step: every release carries **`offline-packages-<tag>.zip`** beside
the deployment package, built by CI from this same script. Two downloads then
cover a machine that has neither NuGet nor a connected neighbour. Check with
your reviewer first if your supply chain policy requires packages to come from
`api.nuget.org` or an internal mirror rather than from a GitHub release — the
bytes are identical and each `.nupkg` carries its author's signature, but the
route is different, and that is sometimes the thing under review.

Then, on the build machine, from a clone or from the `source\` tree inside a
release package:

```powershell
Expand-Archive .\offline-packages.zip -DestinationPath C:\offline-packages
dotnet restore .\SqlTicketsConnector.sln --source C:\offline-packages
dotnet build   .\SqlTicketsConnector.sln -c Release --no-restore
dotnet test    .\SqlTicketsConnector.sln -c Release --no-build
```

89 packages, 227 MB, in three sets:

| Set | Packages | Size | Needed for |
|---|---:|---:|---|
| Base | 80 | 132 MB | any build or test run |
| Runtime packs | 4 | 92 MB | `Build.ps1 -SelfContained`. `-SkipRuntimePacks` |
| OpenTelemetry | 5 | 3 MB | `Build.ps1 -EnableOtlpExporter`. `-SkipOtlp` |

The runtime packs are the bundled .NET runtime. Two of the four are requested
only from some build hosts — a Windows SDK asks for the WindowsDesktop pack and
already has its own apphost, a cross-build from macOS or Linux is the reverse —
so the list is the union and the CI check treats those two as optional. Their
version is chosen by the SDK rather than by this repository, so the script asks
MSBuild for `BundledNETCoreAppPackageVersion` instead of hard-coding one. It must match the
SDK on the machine that will do the offline build — pass `-RuntimeVersion` if
you are staging for a machine whose SDK differs from yours.

A checked-in package list goes stale the moment a dependency moves, so CI
compares it against the resolved graph in all three configurations and fails the
build on any drift. That check found a missing entry the first time it ran.

Every dependency bump is drift, so it fails on those too, by design and after
the build and tests have had their say. Regenerate the list in the same change:

```powershell
dotnet restore .\SqlTicketsConnector.sln
.\build\Test-OfflinePackageList.ps1 -Configuration Base -Update
```

All three restores above were verified against a staged folder with an empty
package cache, so nothing was quietly served from `~/.nuget/packages`:

```powershell
dotnet restore .\SqlTicketsConnector.sln --source C:\offline-packages --packages C:\nuget-scratch
```

#### Stopping it reaching for nuget.org at all

A source given on the command line applies to that one command. Every other
restore — Visual Studio's, an IDE background restore, a colleague running
`dotnet build` out of habit — still inherits the sources in
`%APPDATA%\NuGet\NuGet.Config` and the machine-wide configuration, `nuget.org`
among them, and fails with `NU1301` on a network that blocks it. That is why the
command line can succeed and Visual Studio's Error List still fills with
`api.nuget.org` seconds later.

Fix it for the whole tree by copying the template into the solution root:

```powershell
copy build\NuGet.offline.config NuGet.config
```

Then edit the one path in it to point at your staged folder. The
`<clear />` element is what does the work: without it the file *adds* a source to
the inherited ones instead of replacing them. Afterwards the tree sees exactly
one source, which you can confirm with:

```powershell
dotnet nuget list source
```

`NuGet.config` is not committed, deliberately: a repository that clears its
package sources cannot restore from nuget.org at all, which would break every
online build including CI. It is also in `.gitignore`, so a copy made on a build
machine cannot be committed by accident.

The .NET SDK installer itself is the one thing that cannot be staged this way:
it is not a NuGet package. Download it from Microsoft and transfer it like any
other approved installer.

---

## Transfer via SharePoint

1. Upload the zip to a document library. A release package is roughly 90 MB; a
   local `Build.ps1` without `-SelfContained` is roughly 15 MB.
2. On the agent server, download it.
3. Unblock before extracting. SharePoint downloads carry the mark of the web, and
   .NET refuses to load blocked assemblies.

A release asset is named after its tag, and a local build after the time it ran
— `SqlTicketsConnector-deploy-<timestamp>.zip`. Both work the same way; the
commands below take whichever zip is in the current folder rather than naming a
version that will be wrong by the next release:

```powershell
$zip = Get-Item .\SqlTicketsConnector-*.zip
Unblock-File $zip
```

If the package came from a CI run or a release it has a `.sha256` beside it.
Verify before extracting: a document library round trip is exactly the sort of
hop that truncates a file quietly.

```powershell
$expected = (Get-Content "$($zip.Name).sha256").Split(' ')[0]
$actual = (Get-FileHash $zip -Algorithm SHA256).Hash.ToLower()
if ($actual -ne $expected) { throw "Checksum mismatch. Do not deploy this package." }

Expand-Archive $zip -DestinationPath C:\Staging\SqlTickets
```

If the tenant blocks `.ps1` in libraries, rename to `.ps1.txt` before upload and
back after download. If it blocks `.zip` outright, use a different transfer
channel; do not disable the blocked file types policy.

---

## Deploy

1. **Prepare SQL.** Run `sql/01-least-privilege.sql` (the variant matching your
   `SqlAuthMode`) and `sql/02-soft-delete.sql` against the `Ops` database.

2. **Install the certificate** into `LocalMachine\My` on the agent server, with
   its private key. If `Connector:UseTls` stays `true`, the key must be
   exportable.

3. **Edit `appsettings.json`** in the package and replace every
   `REPLACE-WITH-…` placeholder: tenant ID, client ID, certificate thumbprint and
   the Entra group object ID that may see ticket content. Startup validation
   rejects placeholders and names each one, so a half-configured deployment
   cannot start.

4. **Install**, from an elevated PowerShell session:

```powershell
cd C:\Staging\SqlTickets
.\Install-Connector.ps1 -SourcePath .\publish -ServiceAccount 'CONTOSO\svc_gca_reader$'
```

The script copies binaries, creates the Windows event log source, verifies the
certificate is present and that the service account can read its private key
(granting Read if it cannot), merges the port map while preserving existing
entries, registers the service with restart-on-failure, sets folder permissions,
and restarts `GcaHostService`.

5. **Verify:**

```powershell
Get-Service SqlTicketsConnector, GcaHostService
Get-NetTCPConnection -LocalPort 30303 -State Listen
Get-Content C:\Connectors\SqlTickets\Logs\ConnectorLog.log -Tail 20
Get-EventLog -LogName Application -Source SqlTicketsConnector -Newest 20
```

Startup logs the connector ID, port, TLS state, data source and environment on
one line.

---

## Test before publishing a connection

The agent ships a test harness that exercises validate, schema and crawl without
touching the Microsoft index.

1. Copy `Manifest.json` and `ConnectionInfo.json` from the package into
   `C:\Program Files\Graph connector agent\TestApp\Config\`.
2. Edit `ConnectionInfo.json` if your server or database differ. **Do not put
   credentials in it**: SQL access comes from `appsettings.json` on the host, and
   anything typed into the credential fields is logged as ignored and discarded.
3. Run `C:\Program Files\Graph connector agent\TestApp\GraphConnectorAgentTest.exe`.

Iterate freely here. Nothing reaches the tenant.

---

## Publish the connection

Microsoft 365 admin center → Settings → Search & intelligence → Data sources → **Add**.

1. Choose **Custom connector**, upload `Manifest.json`.
2. Name the connection.
3. Data source URL: `Server=sql01.contoso.local;Database=Ops`. Authentication:
   **Windows**. Select your registered agent. Validate.
4. Skip the optional configuration step.
5. Accept the schema the agent pulled from `GetDataSourceSchema`. Confirm `body`
   is selected as the content property.
6. Set full and incremental crawl schedules. Publish.

---

## What each service call does

| Call | When | Implementation |
|---|---|---|
| `GetBasicConnectorInfo` | Agent startup | Returns the configured connector GUID |
| `HealthCheck` | Polled continuously | Empty response, never touches SQL, logged at `Verbose` only |
| `ValidateAuthentication` | Wizard step 1 | Opens a connection, runs `SELECT TOP 1` including `IsDeleted` |
| `ValidateCustomConfiguration` | Wizard step 2 | Returns success; behaviour comes from the host's configuration |
| `GetDataSourceSchema` | Wizard step 3 | Returns 7 properties with semantic labels and exactly one content property |
| `GetCrawlStream` | Full crawl | Streams live rows from the checkpoint, composite watermark per item |
| `GetIncrementalCrawlStream` | Incremental crawl | Streams changed rows, emits `DeletedItem` for soft-deleted ones |
| `RefreshAccessToken` | Never here | Stub; SQL auth is not OAuth |

Health check failures in sequence cause the agent to fail the connection, which
is why it deliberately does no I/O.

---

## Things that will bite you

- **Port map edits need a service restart.** `GcaHostService` caches the mapping
  at startup. Symptom is "connector unavailable on specified port" even though
  the process is listening.
- **Each connector needs its own port.** Two entries pointing at 30303 will not
  both work.
- **Connector ID is permanent.** Changing it after connections exist breaks every
  one of them. It appears in `appsettings.json`, `Manifest.json` and
  `CustomConnectorPortMap.json`; the connector warns at startup if the configured
  ID differs from the one this build was created for.
- **Placeholders fail startup by design.** Exit code 2 with every invalid field
  listed at once, not one per restart.
- **The ACL is not optional.** No `Acl:GrantGroupObjectIds` means the service
  refuses to start. There is no "everyone" fallback.
- **4 MB item cap.** Content is truncated at `DataSource:MaxContentBytes`
  (default 3.5 MB) with a `Warning` naming the item; anything still oversize is
  skipped with `SkipItem` rather than being sent and rejected.
- **Deletes need `IsDeleted`.** Without `sql/02-soft-delete.sql` and an
  application that soft-deletes, removals only reach the index at the next
  periodic full crawl.
- **Semantic indexing.** Copilot grounds on the semantic index. The `Title` and
  `Url` semantic labels plus a property flagged `IsContent` are what make items
  retrievable by Copilot rather than only by search.
- **Item quota.** Connector items consume tenant item quota, metered separately
  from Copilot seats. Size a production crawl against the search licensing page
  first.
- **Logs contain no ticket data.** By design — see `docs/SECURITY.md` LOG-3. Use
  the item ID and query SQL if you need to see a row.
- **`Grpc.Core` 2.40.0 is past end of support.** Kept deliberately; migrating to
  `Grpc.AspNetCore` means regenerating the server stubs and rewriting the host.
  Recorded as an accepted risk in `docs/SECURITY.md` §3.

---

## Direct push path (`SqlGraphPush`)

A seeding and repair tool: it proves the tenant, app registration and Copilot
grounding before anyone installs an agent, and it re-seeds a connection that has
gone wrong. It is **not** a synchroniser — it crawls nothing incrementally,
runs on no schedule, and never deletes an item. Read
[`docs/TROUBLESHOOTING-DIRECT-PUSH.md`](docs/TROUBLESHOOTING-DIRECT-PUSH.md)
before relying on it for anything ongoing.

It authenticates with the same certificate credential as the connector, and
supports the same client secret alternative (`Auth:Mode`), with the value in
Windows Credential Manager rather than in configuration.

1. Register an Entra app with the `ExternalConnection.ReadWrite.OwnedBy` and
   `ExternalItem.ReadWrite.OwnedBy` application permissions, grant admin consent,
   and upload the public certificate to it. Give it a connection ID the Graph
   connector agent does not also use: `OwnedBy` means whichever app created a
   connection is the only one that can manage it.
2. Fill in `src/SqlGraphPush/appsettings.json` (same shape as the connector, plus
   a `Graph` section). `Auth:CertificateStoreLocation` is `CurrentUser` there,
   since it usually runs interactively.
3. Run it:

```powershell
cd src\SqlGraphPush
dotnet run
```

Schema registration is a server-side long-running operation; the tool polls until
the connection reports `ready`, typically 5 to 15 minutes, and gives up after
`Graph:SchemaReadyTimeoutMinutes`.

### Three level variant (`SqlHierarchyPush`)

The same path, for hierarchical data: **Customer → Engagement → TimeEntry**. It
exists to answer one question — how do you make a search for a customer return
their engagements and their time entries, when a Graph external item is flat and
Copilot traverses nothing?

The answer is to flatten deliberately, in both directions, in SQL views: every
descendant physically carries its ancestors' searchable text, and every ancestor
carries a roll-up of its descendants.

![How the Customer, Engagement and TimeEntry hierarchy is flattened by SQL views into flat external items, each carrying its ancestors' text, so one customer search matches all three levels](docs/hierarchy-flow.svg)

**To deploy it, follow [`docs/HIERARCHY-DEPLOYMENT.md`](docs/HIERARCHY-DEPLOYMENT.md)** —
step by step, .NET 10 and .NET 9, from the SQL scripts to a Copilot answer. It is
a separate deployment from the ticket connector: not a Windows service, not
installed by `Install-Connector.ps1`, and sharing nothing with it at run time.
For *why* it is built this way, read
[`docs/HIERARCHY-TEST-CASE.md`](docs/HIERARCHY-TEST-CASE.md) first.

It coexists with the ticket test case rather than replacing it — different
tables, a different connection ID (`consultingwork`), and its own schema. Both
can run against one tenant.

```powershell
dotnet run --project src\SqlHierarchyPush -- --dry-run   # read the source, call no API
dotnet run --project src\SqlHierarchyPush
.\deploy\Test-HierarchySearch.ps1                        # prove the cross level search
```

---

## Licence

MIT — see [`LICENSE`](LICENSE).

The five contract files under `src/SqlTicketsConnector/Contracts/` are
Microsoft's, reproduced byte for byte from `GraphConnectorsTemplate.vsix`. They
carry their own copyright and MIT licence headers, which the MIT licence on this
repository does not replace. Do not modify them.
