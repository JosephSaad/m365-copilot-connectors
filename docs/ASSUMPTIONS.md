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

   The same switch now also carries the OpenTelemetry SDK and its OTLP exporter,
   which send **traces and metrics** from the push tools — a different signal
   from a different assembly, sharing one flag. The push tools' own
   **instrumentation** is unconditional and costs no package:
   `System.Diagnostics.DiagnosticSource` is in the shared framework on both
   target frameworks, so the default dependency graph and the offline restore
   list are unchanged by it. `Otlp:Enabled` is the runtime flag; see
   `docs/TELEMETRY.md`.
5. **Configuration keys added beyond the brief's schema**, all optional with
   defaults: `Auth:CertificateSubject` (required for locate-by-subject rotation),
   `Connector:TlsCertificateThumbprint`, `DataSource:ItemUrlTemplate`,
   `DataSource:SoftDeleteEnabled`, `DataSource:SqlUserId`,
   `DataSource:ExtraConnectionOptions`, `DataSource:ConnectRetry*`,
   `DataSource:CommandTimeoutSeconds`,
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

- **A third CDP connector, for the Atlas catalogue, on 2026-08-26.** The other
  two index what is *in* the lake. `cdpatlascatalog` indexes what the lake
  *contains*: one external item per Apache Atlas entity — `hive_db` and
  `hive_table` by default, `hdfs_path` optionally — carrying the entity's name,
  qualified name, owner, description, columns, Atlas classifications, glossary
  terms, one dataset hop of lineage each way, and a modified timestamp. Its
  configuration is `src/CdpGraphPush/appsettings.cdpatlascatalog.json` and it
  holds its own Graph connection, so it can be deployed or removed without
  touching the other two.

  **The access decision is the part worth reviewing, because it departs from the
  cluster in both directions.** Atlas authorises through its own Ranger service,
  separate from Hadoop SQL, and CDP ships it with a policy called `public`
  granting every authenticated user read on every entity — which is also why a
  Hadoop SQL deny does not hide a table's metadata in Atlas. **This connector
  refuses to inherit that policy.** An entry is granted to exactly the groups
  Ranger grants select on the table it describes
  (`RoutingEvaluator.EvaluateCatalogueEntry`) and skipped when that is nobody.
  "Everyone with a cluster account" and "everyone in the Microsoft 365 tenant"
  are different populations, and inheriting the first would publish the shape of
  the lake — table names, column names, owners — to people who cannot reach the
  cluster at all. Narrower than the source is the safe direction to be wrong in.
  A deny refuses the entry outright, because a description of a table is still a
  disclosure about it, and a column-scoped grant narrows what is described
  rather than refusing it, because a column name discloses by existing.

  **In the other direction, a row filter or a column mask does not refuse an
  entry, where it does refuse the data.** This is the single most important
  thing to understand about the connector, and the one place in the repository
  where a description of data is indexed although the data is not. A filter
  governs which rows a person sees and a mask which values; neither hides a
  table's existence, its columns or its owner from somebody granted select, who
  sees all of that the moment they query. So the entry is indexed for exactly
  those people and tells them nothing new, while the rows stay out of the index
  under the rule that has always governed them. The tables whose data can never
  be indexed are frequently the ones most worth cataloguing.

  **`Settings:AtlasBaseUrl` is required with no default**, and is rejected
  unless it is absolute, `https`, and free of an `/api/atlas` suffix the client
  appends itself. There is no defensible default port: Atlas answers on 31443 in
  a stock CDP 7.1.9 install (31000 without TLS, which this connector refuses),
  on 21443 upstream, and on the Knox gateway's own port and path when Knox
  fronts it. A guessed default that happens to be wrong produces a connection
  error at the least helpful moment, so the operator states it. `AtlasTypes`
  defaults to `hive_db;hive_table`, `AtlasPageSize` to 100 against an Atlas cap
  of 10,000, and `AtlasIncludeLineage` to true at the cost of one extra request
  per table. `AtlasTypes` is checked at startup against the four types the
  connector can describe — `hive_db`, `hive_table`, `hive_view`, `hdfs_path` —
  because a type it has no shape for is enumerated and detailed in full and then
  described not at all, which costs a whole crawl and reports a clean run with
  nothing written.

  **A hop of lineage is a dataset hop, not a graph hop.** Hive records
  `table → hive_process → table` and a process's own name is the query text that
  produced it, so the walk goes *through* transformation nodes to the datasets
  beyond them; `direction=BOTH&depth=2` is requested for what is described as
  one hop each way. Every neighbour is then checked against Ranger and named
  only when every group granted this entry is also granted the neighbour —
  Atlas's own authorization cannot be leaned on for this, because CDP ships it
  granting every authenticated user read on every entity.

  **The search is a `GET`, not a `POST`.** Atlas installs its own CSRF filter in
  front of non-`GET` REST calls, and whether it demands a header depends on
  `atlas.rest-csrf.enabled` at the cluster — configuration this connector cannot
  see and should not depend on. The `GET` form of the basic search takes the
  same parameters, so nothing is given up. Authentication is SPNEGO as the
  service account and the client never sends an `Authorization: Basic` header,
  because Atlas's filter prefers Basic over Kerberos and would authenticate as
  whatever that header claimed.

  **The catalogue is fully enumerated every run, and that is accepted rather
  than worked around.** Atlas 2.1.0, which CDP 7.1.9 ships, cannot filter a
  basic search by modification time, so there is no incremental read to be had
  and pretending otherwise would only hide the cost. The watermark therefore
  spares the **Graph writes**, not the Atlas reads. A catalogue is thousands of
  entities rather than millions, so the full enumeration is cheap; the same
  property means `Settings:FullRecrawlEveryRuns` governs how often every entry's
  ACL is re-derived rather than how much is read, and it remains the ACL
  staleness bound described further down this section.

- **Thirteen defects found by an adversarial review of the Atlas catalogue,
  fixed before it was ever published (2026-08-27).** The catalogue was new
  surface and got its own review, on the same pattern as the one below: five
  independent readers, each attacking one failure mode, and every candidate then
  given to a skeptic prompted to refute it. Twenty-six candidates, ten distinct
  defects confirmed and three smaller corrections found alongside them. `v1.2.19`
  and `v1.2.18` were both deliberately left published in the meantime.

  **One of them meant the shipped configuration could not finish a crawl.**
  Atlas serves lineage only for entities deriving from `DataSet` or `Process`. A
  `hive_db` derives from neither, so a healthy Atlas answers HTTP 400 — and the
  client treated anything that was not a 404 as fatal. The shipped
  `AtlasTypes` of `hive_db;hive_table` with lineage on therefore died on the
  first database, part-way through, leaving a permanently partial index and an
  error message pointing the operator at Atlas's health. It survived the test
  suite because the fake answered every lineage path with a canned 200.

  Three more **over-granted**, which is the direction that matters:

  - **Lineage neighbour names had no access check at all.** The names came from
    Atlas — which on a stock cluster shows every authenticated user every entity
    — and were written onto an entry granted to one table's readers. Now a
    neighbour is named only when every group on the entry is also granted it.
  - **A one-hop lineage walk lands on the `hive_process`, not on a table**, and
    a Hive process's name is the query text. `upstream` would have carried raw
    SQL naming tables the reader had no grant on. The walk now goes through
    transformation nodes to the datasets beyond them.
  - **Column narrowing unioned the column grants across policies** while the ACL
    unioned their groups, so a group granted two columns was shown a third
    group's `hiv_status`. It is an intersection now, and disjoint grants
    describe no columns rather than all of them.

  The rest: a pager that read "this page added nothing" as "the catalogue ends
  here", so one restricted database's worth of scrubbed entities truncated the
  catalogue while reporting a clean crawl; a database entry routed by asking
  Ranger about a table literally named `*`, so a cluster with per-table policies
  catalogued no databases; the watermark filter applied with no slack window,
  unlike its sibling, so an entity altered during the enrich loop sat stale
  until the next full recrawl; `classifications` and `glossaryTerms` registered
  as refinable single-value strings but written comma-joined, which makes
  "PII, GDPR" a refiner bucket that filtering on "PII" does not match;
  `Settings:ItemBudget` shipped and validated but enforced only by the HDFS
  source; a `modifiedUtc` of `0001-01-01` when an entity's detail read 404s;
  and two documents that promised a one-run ACL staleness bound the code does
  not provide.

  **The docs were wrong in the same direction twice.** `CDP-DEPLOYMENT.md` and
  `TROUBLESHOOTING-CDP.md` both said the catalogue re-derives every entry's ACL
  every run. It does read every entity every run — but the watermark filter runs
  *before* the routing check, so an entry whose Ranger grant changed while its
  Atlas entity did not is dropped before any ACL is derived. Reading everything
  is not re-deciding everything. Both now state the real bound, which is
  `Settings:FullRecrawlEveryRuns` runs, the same as the other two connectors.

- **Twelve defects found by an adversarial review of the CDP connector, fixed
  before it was ever published (2026-08-26).** The connector was reviewed by
  six independent readers, each attacking one failure mode, and every candidate
  finding was then given to a skeptic prompted to refute it. Twenty-eight
  candidates, fourteen survived, twelve distinct. The release was held as an
  unpublished draft until all twelve were fixed, and `v1.2.18` was deliberately
  left as the published version in the meantime so there was always a
  last-known-good package to fall back to.

  Two root causes accounted for half of them, and both **over-granted** — which
  is the direction that matters here, because the failure is a person seeing a
  document the cluster would have refused them.

  **The POSIX ACL mask.** HDFS does not store the extended-ACL mask as an ACL
  entry. It stores it in the *group digit of the file mode*, and moves the
  owning group's own permission into a `group::` entry. So `chmod 600` on a file
  carrying `group:analysts:r--` revokes analysts at the cluster — `getfacl`
  prints `#effective:---` — while the entry text is unchanged. Reading the entry
  alone kept granting it; reading the digit as the owning group's permission
  granted the file's owner whatever the mask allowed. Both now intersect with
  the mask. Related: a permission string is read by place value rather than by
  position, because Hadoop renders the mode with `%o` and drops leading zeros —
  `"70"` means `070`, and indexing position 1 of it read the *other* digit and
  silently dropped a group-readable file from the index.

  **Reading a Ranger policy the way Ranger reads it.** Three fields were parsed
  and discarded. `isExcludes` turned "every finance table EXCEPT salaries" into
  "salaries" — the exact inverse, so the excluded table was the one indexed.
  `isRecursive` turned a grant on one directory into a grant on its whole
  subtree. And resource matching handled only a trailing wildcard where Ranger
  honours `*` and `?` anywhere, so a row-filter policy named `*_pii` was
  invisible and the filtered table was read and indexed — the exact failure the
  routing doctrine exists to prevent.

  **One asymmetry there is deliberate**, and is commented at both ends: grants
  are matched faithfully, denies conservatively. A grant that matches too much
  over-grants; a deny that matches too little fails open. A deny covering any
  ancestor therefore disqualifies indexing whatever its recursive flag says,
  through its own `CoversPathForDeny` rather than by making `CoversPath` wrong
  for grants.

  The rest, each with a regression test that fails without its fix:

  - The **Hive watermark** was stored in .NET round-trip form
    (`2026-08-20T10:00:00.0000000Z`) and injected as a HiveQL timestamp literal,
    whose grammar accepts neither the `T` separator nor the trailing `Z`. Run 1
    read the whole table; every incremental run after it returned zero rows and
    reported success. The test that existed pinned the query *shape* using a
    hand-written Hive-format marker, so it agreed with itself and never
    exercised the format the code produced. The round trip is now the test.
  - **Rows with a NULL watermark** are excluded and the exclusion is logged.
    Hive sorts NULLs first, so a capped window could fill with rows that commit
    no marker, leaving the checkpoint untouched and the crawl re-reading the
    same first N rows for ever, successfully.
  - A **full recrawl truncated by `MaxItemsPerRun`** wrote its truncated
    position over the high-water mark and still counted as a completed crawl,
    bounding the reachable corpus at cap × cadence and leaving the *newest*
    files unreachable by any run. The marker is now monotonic, and a truncated
    run does not advance the cadence — re-deriving ACLs is what the cadence is
    for, and a truncated recrawl did not do it.
  - A **file deleted between the listing and the read** killed the whole crawl:
    the extractor called `open()` outside its `try`, and the catch meant to
    handle it was dead because the 404 had already been swallowed upstream.
  - The **visited set compared paths case-insensitively** on a case-sensitive
    filesystem, silently skipping an entire subtree.
  - `DataSource:MaxContentBytes` is the one field of that section the **engine**
    reads, so `PushOptions` now checks it; it had been validated only by the SQL
    family, which a connector reading no database never runs.

  **Two existing tests encoded the old behaviour and were corrected rather than
  worked around.** One asserted that a named ACL entry grants at mode `600`,
  which is the over-grant itself; one asserted a first-run Hive query carries no
  `WHERE`, no longer true now that NULL watermarks are excluded. Both now say
  why in place. A test that has to change when a bug is fixed is evidence the
  test was asserting the bug, and that is worth recording rather than quietly
  editing.

- **A Cloudera CDP connector, and a source seam to hold it, on 2026-08-26.**
  Adding a source that is not a database made two things obvious at once: the
  shared core was named for one of the two things it served, and its extension
  point named a SQL type.

  `SqlPushCore` became **`PushCore`** and `SqlConnector.Security` became
  **`Connector.Security`**, and the SQL half of the engine moved into a new
  **`PushCore.Sql`** which the core does not reference. `PushCore` no longer
  carries a `Microsoft.Data.SqlClient` dependency at all, so a SqlClient
  advisory is not a re-release of a connector that never opens a SQL
  connection. The rename was done before the first CDP file merged, on the
  reasoning that there will never be fewer consumers of the old name than there
  are today.

  `IPushConnector` lost `BuildQuery` and `MapRow` — the two members naming
  `SqlDataReader` — and gained `CreateSource`, returning an `IPushSource` the
  engine reads. `ISqlPushConnector` supplies `CreateSource`, `ApplyDefaults` and
  `Validate` for a SQL connector out of the query and row mapping it already
  wrote, so a SQL connector is still one class and one configuration file and
  the two shipped connectors changed by one token each.

  **Those three are explicit interface implementations on purpose.** A connector
  writes `ValidateOptions` to add its own rules; had the family implemented
  `Validate` implicitly, a connector defining a method with that name would
  silently replace its family's checks — the `DataSource` section, the view
  name, the vault secret — and the loss would look exactly like a passing build.

  The seam also made the unbreakable rule structural rather than conventional.
  Only the engine knows whether an item reached the index, so only the engine
  says so: `OnItemCommittedAsync` fires after the write returns,
  `OnCrawlCompletedAsync` only when the enumeration ended without throwing, and
  neither fires during a dry run. A source cannot checkpoint something that was
  merely read.

- **What the CDP connector refuses to index, and why (2026-08-26).** These are
  refusals rather than gaps, and each one is a case where a single indexed copy
  cannot represent what the source would show two different people:

  - A table carrying a **Ranger row filter or column mask** is routed to a live
    query. A filter and a mask are per-user transforms applied when a query
    runs; indexing one either leaks the unfiltered rows to everyone granted the
    item, or stores the masked version and lies to the people entitled to the
    real one.
  - A table with any **deny policy** is routed rather than mirrored. Graph has
    deny ACEs and mirroring looks safer, but a mirrored deny only protects while
    the translation is right every time, and a translation that drifts fails
    open. `PushAclEntry` cannot express a deny at all, so the rule is enforced
    by the type system rather than by discipline.
  - A **column-scoped grant** is the same problem wearing different clothes.
  - **An unresolved group is dropped and an item with no grants is skipped.**
    There is deliberately no fallback to the connection-wide ACL: a fallback
    would widen the audience of exactly the item whose permissions could not be
    established.

- **ACL staleness is bounded by `Settings:FullRecrawlEveryRuns`, not by the
  crawl interval (2026-08-26).** A permission change at the source does not
  alter a file's modification time, so an incremental crawl never revisits a
  file whose group grant was revoked, and its indexed ACL would stay stale
  indefinitely. The periodic full recrawl is the only thing that re-derives
  those grants, which makes that setting the documented upper bound — seven runs
  by default, so seven days on a daily schedule — and it belongs in the
  deployment's risk register rather than in a code comment. Setting it to zero
  is accepted but reported at startup. The same mechanism is what catches a file
  renamed into a crawled directory carrying an older timestamp, which no
  bounded watermark can see.

- **`GroupMappingMode: ExternalGroups` is refused rather than half-implemented
  (2026-08-26).** A Graph external group may contain Entra users and Entra
  groups; it may not contain an identity that exists only on the cluster. So
  mirroring a cluster-local Hadoop group produces a group with nobody in it, and
  items granted to it would be indexed and returned to no one — which looks like
  success. Files readable only by cluster-local groups therefore cannot be
  securely indexed at all until those identities exist in Entra, and the
  connector says so at startup instead of appearing to work.

- **PDF text extraction is an optional build flag (2026-08-26).** Text, CSV,
  JSON, XML, HTML and the Open XML formats are extracted with the base class
  library alone — an `.docx` is a zip of XML, and reading it needs no package.
  PDF has no such answer, and adding a parser to a repository the customer
  redistributes is a licensing decision rather than a coding one. Build with
  `-p:EnablePdfExtraction=true` to compile against PdfPig (Apache-2.0), and
  regenerate the offline package list in the same change. Without it a PDF is
  still indexed by its metadata with `extractStatus` saying why there is no
  body: a document nobody can find is worse than a document found without its
  text. Copyleft and per-seat commercial parsers are excluded by policy.

- **A SQL source rejecting the login is still exit 4, not exit 3 (2026-08-26).**
  The new `PushSourceAuthenticationException` maps a source refusing this
  identity to exit 3, and the CDP connector raises it for Kerberos, HDFS and
  Ranger rejections. The SQL family was deliberately left alone: changing what
  a SQL login failure exits with would alter the behaviour of two shipped
  connectors that operators already have monitoring rules for. The asymmetry is
  known and is a candidate for the next deliberate break.

- **The solution and repository keep their `SqlTicketsConnector` names
  (2026-08-26).** The shared code was renamed because it is shared; the
  solution file, the release asset name and the repository are the product's
  identity, and renaming them would change every released asset's file name and
  every link to it for no behavioural gain. Revisit at a major version.

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

- **A regression audit over the audit fixed 31 more findings on 2026-08-25.**
  The same two-phase adversarial shape, aimed at what the first audit, the
  rename and the guard replacement had just changed. Its best catches were in
  the newest code: the schema-ownership check ran only when the connection was
  `Ready`, so a foreign connection still in draft could be silently claimed by
  PATCHing this connector's schema over it - the one window the deleted named
  guard had covered. The check now runs whenever the connection exists, in any
  state, and `--dry-run` performs it too with read-only GETs.

  One boundary is deliberate and recorded here: the ownership comparison is
  one-directional. A connector whose schema is a strict SUPERSET of the
  registered one passes (that is what append-only evolution looks like), so
  protection between two connectors relies on each building at least one
  property the other does not - true of every connector in this repository,
  and pinned by a test comment so the limit is visible where it is relied on.

  The rest, compressed: the agent connector's URL-template validation gained
  the `{0}` and format checks through a shared `UrlTemplateValidator` both
  consumers now call; the installer's failure trap reports from the actual
  mutation flags (a fresh install that dies no longer claims "nothing was
  changed"); the Credential Manager probes use `CredRead` instead of parsing
  localized `cmdkey` text; a Graph HttpClient timeout no longer reports as
  "cancelled"; the duplicate-items counter is in the completion line;
  `Compare-SourceToIndex` prints its closing caveats before its failure exit;
  and the watermark-on-failure fix, the ownership call site, and the template
  rules are each pinned by tests that fail if the code is reverted.

- **The shared library renamed to `Connector.Security` on 2026-08-25**, at
  the customer's direction: nothing shared by more than one connector may carry
  one connector's name. The library began life serving only the tickets
  connector and kept its name as consumers accumulated - by the time the
  hierarchy tool shipped, it was importing `SqlTicketsConnector.Security.*`
  namespaces, deploying `SqlTicketsConnector.Security.dll` beside its own exe,
  stamping `Application Name=SqlTicketsConnector` on its SQL sessions, and
  writing "truncated by SqlTicketsConnector" into hierarchy item content.

  All four are fixed, and the last two were behaviour rather than naming: the
  SQL Application Name is now the entry executable's own name, so a DBA sees
  which connector a session belongs to, and the truncation marker is
  connector-neutral. The shared `ItemUrlTemplate` also lost its tickets-URL
  default - the agent connector now requires the value in its own validation,
  where the need actually lives. Connector-specific names survive only where
  they are the point: `TicketsPushConnector` is the tickets connector. The
  hierarchy tool's hardcoded `sqltickets` rejection guard is gone entirely,
  replaced by an engine-level check that compares the schema registered on a
  connection against the one the running connector builds - any foreign
  property refuses the push with that property named, protecting every
  connector from every other without any of them naming another.

- **A two-sweep adversarial audit fixed 71 confirmed findings on 2026-08-25**,
  run at the customer's request after the engine refactor. Eleven parallel
  reviewers over disjoint dimensions (exception handling, cross-contamination,
  correctness, the build system, SQL, CI, and the test suite itself), then one
  adversarial verifier per finding whose brief was to refute it; 6 of 77 were
  refuted, and only what survived was fixed.

  The ones that mattered most: the crawl failure handlers checkpointed a
  watermark advanced *before* the failing row was delivered — an advance on
  failure, this connector's one unbreakable rule; `DENY CONTROL` in
  `sql/01-least-privilege.sql` implicitly denied the SELECT granted two lines
  above it, blocking every crawl while the verification query looked correct; a
  rejected credential exited 4 (ingestion) instead of the documented 3, because
  no code path could produce 3 for rejection; the hierarchy tool's shipped
  `appsettings.json` carried the tickets certificate subject; and the secret
  scanner matched only leaf key names, so `"ConnectionStrings": { "Default":
  "...Password=..." }` — the canonical .NET idiom — passed the gate.

  Two behaviours are new configuration surface: `DataSource:CommandTimeoutSeconds`
  (default 600, 0 = unlimited) separates query timeout from connect timeout so a
  long view read is not killed at 30 seconds, and schema/connection-ID/item-ID
  validation is now ASCII-only, matching what Graph accepts and what the
  pre-flight scripts already enforced. A new source-scan tripwire asserts every
  logged exception goes through `RedactedException.Wrap` — it caught two drifted
  call sites on its first run, before it ever reached CI.

- **The push path refactored onto one engine on 2026-08-25**, so that adding a
  SQL source is a class and a configuration file rather than a copy of a 550
  line program.

  `SqlGraphPush` and `SqlHierarchyPush` were 85% the same file. That part is now
  `PushCore`: credentials, the vault, the SQL connection, creating the
  connection, registering the schema and polling to Ready, truncation, ACLs, the
  PUT with backoff, exit codes, logging, `--dry-run` and `--help`. A connector
  was `IPushConnector` — a schema, a query and a row mapping. Each executable's
  `Program.cs` is one line.

  *(The SQL connection and the query moved out of `PushCore` into `PushCore.Sql`
  in August 2026, when a source that is not a database arrived; a SQL connector
  still writes exactly those three things, now as `ISqlPushConnector`. See the
  first entry in this section.)*

  Two decisions worth recording. **The Graph SDK lives in the engine, not in
  `Connector.Security`** — that is what lets the credential, vault and
  SQL code stay shared with the agent-hosted connector while that project keeps
  no Graph dependency of any kind. And **connectors are discovered by reflection
  over the entry assembly, never by scanning a folder for DLLs**: a plugin
  directory would mean what the tool can do is decided by whatever is sitting
  beside it on a server, which is not something a reviewer should have to accept.

  Two behaviours changed rather than moved, both improvements, both documented:
  `SqlGraphPush` now honours `Retry-After` with five attempts where it
  previously had no backoff at all — a known way for a large push to lose items
  quietly — and it gains `--dry-run`. Its `appsettings.json` gained a `Source`
  section; a deployed file that lacks one still works, because the connector
  declares `dbo.Tickets` as its default and the host fills it in.

  The recipe is `docs/ADDING-A-PUSH-CONNECTOR.md`. `PushEngineTests` holds a
  `SampleConnector` written exactly the way a new one would be, so if adding a
  connector ever required editing the engine, that file would stop compiling.

- **The push tools put under test on 2026-08-25.** Neither `SqlGraphPush` nor
  `SqlHierarchyPush` had a test. Everything they delegate to
  `Connector.Security` was covered, which is most of the security
  surface, but the parts unique to them were not — and those carry the most
  expensive failure here. A Graph schema is append-only once registered, so a
  wrong annotation is corrected only by deleting the connection and every item
  in it, 1126 of them for the three level test case. The guard against that was
  one unexecuted helper inside a top level statement file, which no test
  assembly can reach.

  The two rules that cannot be recovered from now live in
  `ExternalSchemaRules` in the Security project — primitives in, exception out,
  referencing no Graph type, so the rule is shared without the connector
  acquiring a Graph SDK dependency. The schemas moved out of top level
  statements so they can be asserted — into the connector classes, once the
  refactor above landed later the same day. For `SqlGraphPush` that is a change
  in behaviour rather than a move: its six properties were object initialisers
  with no guard at all.

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
