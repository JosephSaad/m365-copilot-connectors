# Assumptions and decisions

Everything here was either answered by the customer or assumed by me. Assumptions
are stated so a reviewer can correct one without reading the code to find it.

Date: 2026-08-13.

---

## 1. Answered by the customer

| Question | Answer | Consequence |
|---|---|---|
| Where does this run? | **On-premises Windows Server**, alongside the Graph connector agent | Managed identity is not available. `Auth:Mode` is `Certificate`, and startup rejects `ManagedIdentity` in Production with an explanatory message rather than silently failing to acquire a token. |
| Which SQL authentication mode? | **Windows integrated**, using the service account identity | `DataSource:SqlAuthMode` ships as `WindowsIntegrated`, and no credential appears in the connection string. The Entra ID and SQL login paths are implemented and tested, but are not the shipped configuration. This is why the sample in the brief (`"SqlAuthMode": "EntraId"`) differs from what is shipped. |
| Does a soft-delete column exist? | **Not yet — add one** | `sql/02-soft-delete.sql` adds `IsDeleted BIT NOT NULL DEFAULT 0` and the composite index, and documents the required `UPDATE` pattern. `DataSource:SoftDeleteEnabled` defaults to `true`; until the migration runs, either apply it or set the flag to `false` and accept that deletes are only caught by the next full crawl. |
| How do Entra groups map to ticket visibility? | **One group for all tickets** | `Acl:GrantGroupObjectIds` is a list, so more groups can be added without a code change, and every item is granted to all of them. Per-status or per-assignee mapping would need a new configuration shape and a change to `AclBuilder`; nothing in the current design blocks it. |

## 2. Assumptions I made

1. **The connector's own identity is required even though Windows integrated
   SQL auth needs no vault secret.** `Auth:Mode` is `Certificate`, the
   certificate is resolved at startup, and startup fails if it is missing or
   unusable. Rationale: `KeyVault:Uri` is configured, the certificate is the
   connector's identity for it and for TLS, and a deployment missing that
   certificate is misconfigured. If you would rather the service start without a
   certificate when nothing needs one, that is a small change in
   `ConnectorServer.BuildCredential`.
2. **The connector ID stays `9e5e2b95-e7ab-4266-98c7-4f7868d377bf`.** Changing it
   breaks every existing connection, so `Connector:Id` is validated and a
   mismatch against the build's default is logged at `Warning`.
3. **`dbo.Tickets` keeps its current shape** — `TicketId INT` primary key,
   `LastModified DATETIME2` maintained by the application. The composite
   watermark assumes `TicketId` is a stable, ascending `INT`.
4. **`LastModified` is UTC.** The reader stamps `DateTimeKind.Utc` without
   converting. If the column is local time, watermarks drift by the offset at
   each DST change.
5. **The item URL pattern is `https://tickets.contoso.com/ticket/{0}`**, now
   configurable as `DataSource:ItemUrlTemplate` rather than compiled in.
6. **The service account is a domain identity** that the SQL grant names.
   `Install-Connector.ps1` still defaults to `NT AUTHORITY\NETWORK SERVICE`
   (which authenticates to SQL as the machine account); a group managed service
   account is the better choice and is what the examples use, because it has no
   password for anyone to store.
7. **`Environment` is one of `Production`, `Staging`, `Development`.** Validation
   rejects anything else, since several controls key off `Production` and a typo
   there would quietly relax them.
8. **The event log source is created by the installer**, so the service account
   needs no administrative rights. If your image pre-creates event sources, the
   installer detects that and skips it.

## 3. Deliberate deviations from the brief

Each of these is also recorded in `docs/SECURITY.md` §4.

1. **`Authentication=ActiveDirectoryDefault` is not set alongside
   `SqlConnection.AccessToken`.** SqlClient throws when both are supplied. The
   access token path is implemented; `ActiveDirectoryDefault` would reintroduce
   the non-deterministic credential chain the brief rules out elsewhere.
2. **The `Security` project is broader than "secrets, certificates, credential
   factory".** It also holds SQL connection construction, log scrubbing, shared
   option binding and content truncation, because both consuming projects need
   them and the brief forbids duplicating shared code. It references neither the
   Graph SDK nor the gRPC contracts.
3. **Serilog raised from 3.1.1 to 4.3.1**, with the console and file sinks moved
   to 6.0.0. `Serilog.Sinks.EventLog` 4.0.0 requires Serilog 4.x, and the event
   log sink is a required control. Those were the versions this decision was
   taken at; Dependabot has moved them within their major versions since, and
   the project files are the current answer.
4. **The OpenTelemetry exporter is behind a build switch as well as the runtime
   flag.** Referencing it unconditionally would add `Grpc.Net.Client` and a newer
   `Google.Protobuf` to every dependency scan for a feature that ships disabled.
   `dotnet build -p:EnableOtlpExporter=true` or `Build.ps1 -EnableOtlpExporter`.
   Enabling the switch also raises `Google.Protobuf` from the pinned 3.18.0 to
   3.35.1, because the sink requires 3.26.1 or later and the two constraints
   cannot both hold. The generator is unchanged, so the contract types are
   identical; CI builds both configurations.
5. **Configuration keys added beyond the brief's schema**, all optional with
   defaults: `Auth:CertificateSubject` (required for locate-by-subject rotation),
   `Connector:TlsCertificateThumbprint`, `DataSource:ItemUrlTemplate`,
   `DataSource:SoftDeleteEnabled`, `DataSource:SqlUserId`,
   `DataSource:ExtraConnectionOptions`, `DataSource:ConnectRetry*`,
   `Logging:EventLogSource`, `Logging:FileSizeLimitBytes`,
   `Logging:RetainedFileCountLimit`, `Logging:Otlp`.
6. **`appsettings.json` ships with `REPLACE-WITH-…` placeholders** rather than
   plausible-looking GUIDs. Startup validation rejects them and names each one,
   so a half-finished deployment cannot start and quietly index against the wrong
   tenant or grant to the wrong group.
7. **The gRPC contracts were not modified**, and no contract or Graph API surface
   was invented. One correction worth noting: the generated C# name for the
   `OAuth2ClientCredential` value of `AuthenticationData.AuthenticationType` is
   `Oauth2ClientCredential` (protoc's casing), which is what the code uses.

## 4. Defects found and fixed in the existing code

Found by the new tests, not by inspection:

1. **`ResolveWatermark` threw on every checkpoint.**
   `DateTimeStyles.RoundtripKind | DateTimeStyles.AdjustToUniversal` is an
   invalid combination and `DateTime.TryParse` throws `ArgumentException` for it.
   Any incremental crawl that received a checkpoint would have failed. Fixed in
   `Watermark.TryParse`, with normalisation to UTC done explicitly.
2. **`SqlConnectionStringBuilder.ContainsKey` answers `true` for every keyword
   SqlClient knows**, not just the ones supplied, so the first version of the
   connection string inspection reported a password in strings that had none.
   Now uses `ShouldSerialize`.
3. **Serilog stringifies an object at capture time for a plain `{Value}` hole**,
   before any destructuring policy runs, so a protobuf message logged that way
   would have written full JSON — including ticket content — to the log. Closed
   by registering the risky types as scalars and re-applying the redaction policy
   in the enricher (`LoggingSetup.ApplyRedaction`). The canary test now covers
   both spellings.

## 5. Later changes

- **A three level test case added on 2026-08-24**, at the customer's request:
  Customer to Engagement to TimeEntry, pushed by a new `SqlHierarchyPush`
  alongside the existing ticket test case rather than replacing it.

  The decision worth recording is the **deliberate denormalisation**. A Graph
  external item is flat and Copilot traverses nothing, so a search for a
  customer can only reach that customer's time entries if each time entry
  physically contains the customer's text. Every descendant item therefore
  carries its ancestors' searchable fields, and every ancestor carries a roll-up
  of its descendants, computed in the views in `sql/12-timesheet-views.sql`.

  This duplicates a customer's name across roughly a hundred items. That is
  accepted: these are index items rather than a system of record, `dbo.Customers`
  remains the only place the name is authored, and a rename is corrected by
  pushing again. The alternative — one connection per level — cannot be searched
  as one thing, which is the entire requirement. Reasoning and the full property
  annotation table are in `docs/HIERARCHY-TEST-CASE.md`.

  Two consequences a reviewer should know: the roll-up figures (`totalHours`,
  `childCount`) are correct as of the last push and not at query time, and the
  sample data alone is 1126 items against tenant item quota.

- **The push tools put under test on 2026-08-25.** Neither `SqlGraphPush` nor
  `SqlHierarchyPush` had a test. Everything they delegate to
  `SqlTicketsConnector.Security` was covered, which is most of the security
  surface, but the parts unique to them were not — and those carry the most
  expensive failure here. A Graph schema is append-only once registered, so a
  wrong annotation is corrected only by deleting the connection and every item
  in it, 1126 of them for the three level test case. The guard against that was
  one unexecuted helper inside a top level statement file, which no test
  assembly can reach.

  The two rules that cannot be recovered from now live in
  `ExternalSchemaRules` in the Security project — primitives in, exception out,
  referencing no Graph type, so the rule is shared without the connector
  acquiring a Graph SDK dependency. The schemas moved into `HierarchySchema.cs`
  and `TicketSchema.cs` so they can be asserted. For `SqlGraphPush` that is a
  change in behaviour rather than a move: its six properties were object
  initialisers with no guard at all.

  50 tests became 82. Four of the new ones joined the `ControlEvidenceTests`
  tripwire. They were checked by mutation rather than by going green — dropping
  `searchable` from `customerName` fails one test, and making `region` both
  searchable and refinable fails six.

- **Retargeted from net8.0 to net10.0** (current LTS) on 2026-08-14, at the
  customer's request. No source change was needed; the CI build on
  windows-latest and all 40 tests pass on net10.0. The target server therefore
  needs the .NET 10 runtime, or a `-SelfContained` package.
- **Connection validation bounded to 20 seconds on 2026-08-18.** The connectors
  SDK documentation gives every `ConnectionManagementService` method 30 seconds
  before the platform substitutes its own timeout message. With
  `DataSource:ConnectTimeoutSeconds` at 30 and one secret-refresh retry, an
  unreachable server could take about a minute, so the admin would have seen a
  generic platform timeout rather than the message this connector writes.
  Validation now gives up first, at `Connector:ConnectionCallTimeoutSeconds`,
  and reports `DatasourceError` with what to check. Crawl methods are not
  bounded this way: they are streaming calls with no such limit, and cutting a
  crawl short would lose the watermark progress the stream carries.
- **`Auth:Mode: ClientSecret` added on 2026-08-17**, at the customer's request,
  for a tenant that will not issue a client certificate to this application. The
  secret is read from Windows Credential Manager under the service account; only
  the entry's name is in configuration. This is a deliberate departure from the
  original control set, which excluded DPAPI-backed secret storage — recorded
  with its trade-offs as deviation 7 in `docs/SECURITY.md`, and with the storing
  and rotation procedure in `docs/RUNBOOK.md` §2a. Certificate remains the
  default: the alternative this competes with is a secret in a configuration
  file, not a certificate.
- **A second release line, `release/net9`, added on 2026-08-15.** Visual Studio
  2022 has no .NET 10 support and will not load a `net10.0` project whatever SDK
  is installed, which made the retarget above a barrier for a VS 2022 shop. The
  target framework moved into `Directory.Build.props` as one property, so the
  branch differs from `main` in that property and a `global.json` that pins the
  SDK to 9.0.x, and nothing else. Releases from it carry a `-net9` suffix. CI on
  `main` builds and tests `net9.0` on every push, with the .NET 9 SDK alone
  rather than the .NET 10 SDK targeting downwards, so the branch cannot rot
  between releases and the toolchain being proved is the one a VS 2022 machine
  actually has.
- **CI added** in `.github/workflows/build.yml`: build, test, package, and
  attach the package to a draft release for a `v*` tag. It runs the same four
  gates as `Build.ps1`, plus a build of the optional OTLP configuration.
- **The release package is self-contained** as of 2026-08-14, at the customer's
  request: whoever downloads the zip must not need to fetch anything else. The
  .NET runtime therefore ships inside it, and a CI step fails the build if the
  service executable, the bundled runtime, the install script, the SQL scripts
  or the docs are missing from the archive. The Graph connector agent and the
  client certificate remain outside it: the first is Microsoft's tenant-tied
  installer, the second must come from the customer's PKI.

## 6. Open questions for the customer

1. **Which Entra group object ID goes into `Acl:GrantGroupObjectIds`?** The
   shipped file has a placeholder and the service will not start until it is
   replaced.
2. **Tenant ID, client ID and the certificate thumbprint** for the app
   registration, same placeholders.
3. **Does the ticketing application soft-delete?** `sql/02-soft-delete.sql` adds
   the column, but something has to set it. Until then, deletes rely on the
   periodic full crawl.
4. **Is `Connector:UseTls` wanted on the loopback interface?** It is on by
   default per the brief, which requires an exportable-key certificate the agent
   trusts. If the agent is not configured to trust it, turn it off explicitly
   rather than leaving the connection failing.
5. **Which service account, and is it a gMSA?** The SQL grant in
   `sql/01-least-privilege.sql` names `CONTOSO\svc_gca_reader`; replace it with
   the real account before running.
