# SQL Tickets Copilot Connector

[![build](https://github.com/JosephSaad/m365-copilot-sql-connector/actions/workflows/build.yml/badge.svg)](https://github.com/JosephSaad/m365-copilot-sql-connector/actions/workflows/build.yml)
[![licence: MIT](https://img.shields.io/badge/licence-MIT-blue.svg)](LICENSE)

A Visual Studio solution containing two working paths from a SQL Server table to
Microsoft 365 Copilot grounding data, hardened for deployment into a regulated
environment.

Generated against the contracts extracted from `GraphConnectorsTemplate.vsix`
(`ms-graph-connectors.graphConnectors`, v3.4). The five `.proto` files under
`src/SqlTicketsConnector/Contracts/` are Microsoft's originals, copied byte for
byte and unmodified.

| Project | Model | Runs where |
|---|---|---|
| `SqlTicketsConnector` | gRPC server behind the Graph connector agent. **Never calls Microsoft Graph.** | On-premises Windows Server with the agent installed |
| `SqlTicketsConnector.Security` | Shared secrets, certificates, credentials, SQL connections, log redaction | Class library, referenced by both |
| `SqlGraphPush` | Direct `PUT /external/connections/{id}/items/{itemId}` | Operator workstation or jump box with outbound HTTPS to Graph |

Connector ID: `9e5e2b95-e7ab-4266-98c7-4f7868d377bf`
Default port: `30303`

**Before reviewing or deploying this, read [`docs/SECURITY.md`](docs/SECURITY.md)
(control mapping), [`docs/RUNBOOK.md`](docs/RUNBOOK.md) (rotation and failure
modes) and [`docs/ASSUMPTIONS.md`](docs/ASSUMPTIONS.md).**

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
deploy/
  Install-Connector.ps1                Server-side install, run elevated
  CustomConnectorPortMap.json          Reference copy of the agent port map entry
  Manifest.json                        Uploaded in the admin center wizard
  ConnectionInfo.json                  TestApp input, no credentials
docs/
  SECURITY.md                          Control mapping for the security reviewer
  RUNBOOK.md                           Rotation, log locations, five failure modes
  ASSUMPTIONS.md                       Decisions, deviations, open questions
sql/
  00-sample-source.sql                 Creates dbo.Tickets (with IsDeleted) and seeds 3 rows
  01-least-privilege.sql               Login, user and SELECT grant, with verification
  02-soft-delete.sql                   IsDeleted column and composite watermark index
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
  SqlGraphPush/
    Program.cs, PushOptions.cs, appsettings.json
tests/
  SqlTicketsConnector.Tests/           40 tests, no live tenant, vault or database
```

---

## Prerequisites

**Build machine**
- Visual Studio 2022 17.14 or later with the .NET desktop development workload, or the .NET 10 SDK alone
- NuGet access to `api.nuget.org`

**Target server**
- Windows Server 2019 or later
- .NET 10 Runtime, unless you build with `-SelfContained`
- Microsoft Graph connector agent from https://aka.ms/gca, already registered against the tenant
- Network path to SQL Server
- The connector's client certificate in `LocalMachine\My`, with its private key readable by the service account

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
.\Build.ps1 -SelfContained          # server has no .NET 10 runtime
.\Build.ps1 -EnableOtlpExporter     # include the optional OpenTelemetry sink
```

`Build.ps1` runs four gates before it publishes anything — the secret hygiene
scan, a build with warnings treated as errors, the tests, and the dependency
audit — and refuses to package certificate or key material. They are the same
four the CI workflow runs. Output:
`SqlTicketsConnector-deploy-<timestamp>.zip` in the solution root.

CI produces the same package on every push to `main`, downloadable from the
workflow run. Pushing a `v*` tag additionally creates a **draft** release with
the zip and its `.sha256` attached, named after the tag; publishing it stays a
human decision.

---

## Transfer via SharePoint

1. Upload the zip to a document library. Framework-dependent is roughly 15 MB;
   self-contained roughly 70 MB.
2. On the agent server, download it.
3. Unblock before extracting. SharePoint downloads carry the mark of the web, and
   .NET refuses to load blocked assemblies:

```powershell
Unblock-File .\SqlTicketsConnector-deploy-20260813-1400.zip
Expand-Archive .\SqlTicketsConnector-deploy-20260813-1400.zip -DestinationPath C:\Staging\SqlTickets
```

If the package came from a CI run or a release, verify the checksum on the
target server before extracting. A document library round trip is exactly the
sort of hop that truncates a file quietly:

```powershell
$expected = (Get-Content .\SqlTicketsConnector-v1.0.1.zip.sha256).Split(' ')[0]
$actual = (Get-FileHash .\SqlTicketsConnector-v1.0.1.zip -Algorithm SHA256).Hash.ToLower()
if ($actual -ne $expected) { throw "Checksum mismatch. Do not deploy this package." }
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

Useful to prove the tenant, app registration and Copilot grounding before
investing in the agent model, or to re-seed a connection. It authenticates with
the same certificate credential — there is no client secret anywhere in this
solution.

1. Register an Entra app with the `ExternalConnection.ReadWrite.OwnedBy` and
   `ExternalItem.ReadWrite.OwnedBy` application permissions, grant admin consent,
   and upload the public certificate to it.
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

---

## Licence

MIT — see [`LICENSE`](LICENSE).

The five contract files under `src/SqlTicketsConnector/Contracts/` are
Microsoft's, reproduced byte for byte from `GraphConnectorsTemplate.vsix`. They
carry their own copyright and MIT licence headers, which the MIT licence on this
repository does not replace. Do not modify them.
