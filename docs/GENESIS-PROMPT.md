# The genesis prompt

This is the prompt that produces this repository.

Not a description of it — an instruction set. Given an empty directory, a
capable coding agent, and the five `.proto` files from
`GraphConnectorsTemplate.vsix`, everything below the rule is what has to be said
to arrive at what is checked in here.

## Why it is written down

The code says *what* was built. Git history says *when*. Neither says *why this
and not the obvious alternative*, and that is the part that gets lost. Roughly
half of what follows exists because something went wrong once — a silent empty
result, a crawl that failed on every checkpoint, a log line that would have
written ticket content to disk. Those are invisible in the finished code,
because the finished code is the version where they do not happen.

Three uses:

1. **Rebuild against a different source.** Sections 6 to 10 are this customer's
   tickets and timesheets. Sections 2 to 5 and 14 to 15 are not — they are true
   of any Copilot connector into a regulated environment. Replace the first set,
   keep the second.
2. **Onboarding.** Hand this to whoever inherits the repository before they read
   a line of C#. It is the only document that states the constraints as
   constraints rather than as things you can infer from the shape of the code.
3. **A drift check on the documentation.** Every question this prompt asks
   should be answerable from `docs/`. Where it is not, the documentation has
   fallen behind the code.

## What it is not

It is not a specification, and it is not maintained as one. Package versions
move under Dependabot; the project files are the current answer, not section 11.
Where this document and the repository disagree, **the repository is right.**

It will also not reproduce the tree byte for byte, and does not claim to. What
it does is start a rebuild past the fifteen or so discoveries that cost real
time the first time round — which is the whole point of writing it down.

---

# ▸ THE PROMPT

## 0. Role

You are building a Microsoft 365 Copilot connector solution for a
**financial-services customer under regulatory review**. The security reviewer
is a named person who will read the repository, not run it. Assume every
decision will be questioned and that "it works" is not an answer to "why is it
allowed to".

Write plain, direct prose in the documentation. No marketing language, no
enthusiasm, no reassurance. State what a thing does, what it costs, and what it
does not do. If something is a compromise, say so and say what it was traded
against.

## 1. What you are building

Four projects in one Visual Studio solution:

| Project | Model | Runs where |
|---|---|---|
| `SqlTicketsConnector` | gRPC server behind the Microsoft Graph connector agent | On-premises Windows Server |
| `SqlConnector.Security` | Shared secrets, certificates, credentials, SQL, redaction | Class library, referenced by the other three |
| `SqlGraphPush` | Direct `PUT /external/connections/{id}/items/{itemId}` — one flat table | Operator workstation |
| `SqlHierarchyPush` | The same, for a three level hierarchy | Operator workstation |

Two connector models are being demonstrated deliberately: the **SDK** model,
where Microsoft's agent holds the tenant relationship and calls your gRPC
server, and the **API** model, where your own code calls Graph. The customer
needs to see both and understand what the second one gives up.

## 2. Architecture constraints — not negotiable

> **The agent-hosted connector never calls Microsoft Graph.** The GCA holds the
> tenant relationship and performs all Graph ingestion. Therefore:
>
> In `SqlTicketsConnector`, certificate-based authentication applies to **Azure
> Key Vault access only**. Do not add a Graph SDK dependency, a
> `GraphServiceClient`, or Graph API permissions to this project. If you find
> yourself writing `ExternalConnection.ReadWrite.OwnedBy` into this project's
> documentation, you have misunderstood the architecture.
>
> In `SqlGraphPush`, certificate-based authentication applies to **Microsoft
> Graph**, replacing the current client secret.
>
> Both projects need vault access. **Share that code; do not duplicate it.**

## 3. Secret handling — not negotiable

> 1. No secret, password, connection string containing a password, certificate
>    thumbprint alone as an auth factor, PFX file, or client secret may appear
>    in source, `appsettings.json`, environment variables baked into deployment
>    scripts, or logs.
> 2. `appsettings.json` may contain only non-sensitive references: vault URI,
>    secret names, certificate subject or thumbprint, tenant ID, client ID, SQL
>    server hostname, database name. Treat client ID and tenant ID as
>    non-sensitive; treat everything else as sensitive.
> 3. Secrets are resolved at runtime and held in memory only. Do not write a
>    resolved secret to disk, to a temp file, or to a
>    `SqlConnectionStringBuilder` that is later logged.
> 4. Add a `.gitleaks.toml` and a pre-commit hook configuration. Add a build
>    target that fails the build if `appsettings.json` contains a key matching
>    `password|secret|pwd|apikey|connectionstring` with a non-empty value.

## 4. Anti-patterns

Writing any of these means you have misread something above.

- A Graph SDK reference, `GraphServiceClient`, or Graph permission in
  `SqlTicketsConnector`
- `DefaultAzureCredential` anywhere outside a test
- `Information`-level logging inside a `HealthCheck` — it runs constantly and
  will bury everything else
- A swallowed exception in a crawl method. A crawl that fails must say so and
  must not advance the watermark
- `TrustServerCertificate=true`
- A PFX loader. Certificates come from the Windows certificate store
- Migrating off `Grpc.Core` 2.40.0 — see section 14
- Inventing Graph or contract API surface. If you cannot cite it, it does not
  exist

The `.proto` files under `Contracts/` are Microsoft's, copied byte for byte.
**Do not modify them.** The only build warnings permitted anywhere in the
solution are `NETSDK1206` and `CS8981`, both suppressed in the project file with
a comment saying why.

## 5. Platform facts to design around

These are properties of Microsoft Graph and the Copilot index, not of your code.
Design against them; do not try to work around them.

**A Graph external item is flat.** No parent property, no child collection, no
join at retrieval, no traversal at query time. Any hierarchy has to be solved
before the item is written.

**There is no list-items API.** `externalItem` documents Create, Get, Update,
Delete and `addActivities`. Nothing else. `Get-MgExternalConnectionItem`
advertises a `List` parameter set generated from OData metadata — that is
metadata, not an implemented operation. You cannot enumerate what you pushed;
if you want to reconcile, keep the source-side list.

**`ExternalConnection.ReadWrite.OwnedBy` means "connections owned by the calling
app".** In interactive PowerShell the calling app is *Microsoft Graph Command
Line Tools*, so listing connections returns **empty with no error** — the single
most expensive false alarm in this system. The inverse holds too: when the
caller is the owner, an empty list is real.

**Schema rules.** Flat property list. `isSearchable` and `isRefinable` are
**mutually exclusive**. Property names are ≤32 alphanumeric characters. Item IDs
are ≤128 alphanumeric characters. Semantic labels available: `title`, `url`,
`lastModifiedDateTime`, `containerName`, `containerUrl`.

**Schema is effectively append-only once `ready`.** You can add a property. You
cannot change a type, an annotation or a label. Correcting a mistake means
deleting the connection and every item in it. Get it right in the draft.

**Microsoft Search and the Copilot semantic index are siblings, not stages.**
Both are built from the same ingested content. An item that is searchable is not
therefore "on its way to" Copilot. Draw it this way in every diagram.

**Every `ConnectionManagementService` method has 30 seconds** before the
platform substitutes its own timeout message, which is generic and unhelpful.
Bound your own validation below that so your message wins.

**A direct push never deletes.** Excluding a soft-deleted row from the push
leaves the item in the index. Say this out loud in the documentation rather than
letting an operator discover it.

## 6. Deliverable A — the agent-hosted connector

A gRPC server on port `30303`, connector ID
`9e5e2b95-e7ab-4266-98c7-4f7868d377bf`, implementing the four services in the
contracts: connection management, crawl, info, OAuth.

- **Source**: `dbo.Tickets`, `TicketId INT` primary key, `LastModified
  DATETIME2` maintained by the application and **assumed UTC**.
- **Watermark**: composite `(LastModified, TicketId)`, carried in the crawl
  stream. Ties on the timestamp are broken by the ID, so no row is skipped and
  none is re-sent forever.
- **Full and incremental crawl**, both. Incremental resumes from the checkpoint;
  full ignores it. Deletes are detected by the soft-delete flag on incremental
  and by absence on full.
- **ACLs**: grant to a configured list of Entra group object IDs, one ACL per
  item. A list, not a single value, so more groups need no code change.
- **Connection validation** bounded to `Connector:ConnectionCallTimeoutSeconds`
  (20) and reporting `DatasourceError` with what to check. Crawl methods are
  **not** bounded this way — they are streaming calls with no such limit, and
  cutting one short loses the watermark progress the stream carries.
- **TLS on the loopback interface**, on by default, thumbprint configured.
- `appsettings.json` ships `REPLACE-WITH-…` placeholders, not plausible GUIDs.
  Startup validation rejects each one **by name**, so a half-finished deployment
  cannot start and quietly index against the wrong tenant.

## 6a. The push engine

The two direct push tools below are **85% the same program**. Write that part
once, in a library of its own, and make a connector the small remainder: a
schema, a query, a row mapping, and nothing else. Credentials, the vault, the
SQL connection, creating the connection, registering the schema and polling to
`Ready`, truncation, ACLs, the `PUT` with backoff, exit codes, logging,
`--dry-run` and `--help` are the engine's, identical for every source.

Adding a third source must be **one class and one configuration file**, with no
file in the engine changed and no existing connector affected. Two mechanisms
make that true rather than aspirational: a `Settings` bag on the shared options,
so a value only one connector understands never becomes a field every connector
carries; and per-connector defaults for the connection ID and source view, so a
configuration file already deployed keeps working when the core gains a section.

Put the Graph SDK **here**, not in the security library. That is what lets the
credential, vault and SQL code be shared with the agent-hosted connector while
section 2's boundary stays real.

Discover connectors by reflection over **the entry assembly only** — never by
scanning a directory for assemblies to load. A plugin folder means the set of
things the tool can do is decided by whatever is sitting next to it on a server,
which is not a property a reviewer should have to accept.

## 7. Deliverable B — direct push, flat

`SqlGraphPush`. Reads `dbo.Tickets`, creates the connection and schema if
absent, waits for `ready`, pushes one item per row. Connection ID `sqltickets`.
Six schema properties. Exit codes are part of the interface: `0` success, `2`
configuration, `3` authentication or authorisation, `4` Graph rejected an item.

This is the model that gives things up. Document what: no incremental crawl
management, no platform-side scheduling, no crawl history in the admin centre,
no deletion, no retry the platform owns. A deck comparing the two is a
deliverable (`docs/agent-bypass-tradeoffs.pptx`).

## 8. Deliverable C — direct push, three levels

`SqlHierarchyPush`. Connection ID `consultingwork`. **Coexists with B** —
different tables, different connection, different schema, different executable.
Neither replaces the other.

The source is three levels:

| Level | Table | What it is |
|---|---|---|
| 1 | `dbo.Customers` | Who is billed — the account |
| 2 | `dbo.Engagements` | A contracted body of work for that customer |
| 3 | `dbo.TimeEntries` | One consultant's logged hours against an engagement |

**The requirement, stated exactly:** a search for a customer in Copilot must
return that customer's engagements and time entries too. Make as many fields
searchable as the schema rules allow.

**The answer is deliberate denormalisation in both directions**, computed in SQL
views so the join happens at write time:

- **Downward** — every engagement item carries its customer's name, code,
  industry and account manager. Every time entry carries all of that *plus* its
  engagement's name, code, practice and status. The customer search matches all
  three levels because the string is physically present in each item.
- **Upward** — the customer item lists its engagement names; the engagement item
  lists the consultants who logged time to it. So an engagement search returns
  the customer, and a person search returns the engagements they worked on.

The cost is duplication — a customer name appears in roughly a hundred items.
Accept it and say why: these are index items, not a system of record;
`dbo.Customers` is still the only place the name is authored; a rename is
corrected by pushing again. Record the two consequences a reviewer will ask
about: roll-up figures are correct as of the last push, not at query time; and
the sample data alone is 1126 items against tenant item quota.

The alternative — one connection per level — cannot be searched as one thing,
which is the entire requirement. Say that too.

Schema: 26 properties. Guard the two rules in code, not in review — the property
helper must **throw** on searchable-and-refinable together and on a name over 32
characters. Honour `Retry-After` with bounded retries. Provide `--dry-run`.
Do NOT protect connections by connector-naming each other's IDs - a guard
list goes stale the day a connector is added, and it plants one connector's
name in another's code. Protect them in the engine, agnostically: before
pushing into a Ready connection, fetch its registered schema and refuse if it
carries any property this connector does not build. Append-only evolution stays
legal (a missing expected property is a warning); a foreign property is fatal
with the property named.

## 9. The shared security engine

`SqlConnector.Security`, referenced by all three executables.

- `ISecretProvider` with Key Vault, environment and Windows Credential Manager
  implementations, a caching decorator, and a refresh retry policy.
- Certificate resolution from the Windows store by subject **or** thumbprint,
  with expiry checks, private-key-readable checks, and a resolver that reports
  the process identity when it fails — because "cannot find the certificate" is
  almost always "this account cannot read the private key".
- A rotating certificate credential, so a renewed certificate is picked up
  without a restart.
- SQL connection construction: three auth modes (`WindowsIntegrated` shipped,
  `EntraId` and `SqlLogin` implemented and tested), `Encrypt=true` always, error
  classification that distinguishes transient from permanent.
- Log scrubbing: a destructuring policy, an enricher, and a redacted exception
  type.
- The two external-schema rules from section 5, as a validator taking primitives
  and throwing — no Graph type, so the boundary holds, and both push tools use
  the one copy.

It may hold more than "secrets, certificates, credential factory" — SQL
construction, scrubbing, option binding, content truncation all belong here,
because both consumers need them and duplicating shared code is forbidden. It
references neither the Graph SDK nor the gRPC contracts, and that is what keeps
the section 2 boundary honest.

## 10. The SQL layer

Numbered scripts, each idempotent, each ending in a verification query whose
output tells the operator whether it worked.

- `00`–`02`: `dbo.Tickets`, the least-privilege grant, the soft-delete column
  and composite watermark index.
- `10`–`13`: the three level tables with real foreign keys, `IsDeleted`,
  `LastModified` and indexes; sample data of **12 customers, 62 engagements,
  1052 time entries, 8 soft-deleted** (batched inserts — a 1052-row single
  statement is not reviewable); the four views that do the flattening; and a
  grant of `SELECT` **on the views only**, with explicit `DENY` on the base
  tables.

`sql/12-timesheet-views.sql` is the whole test case. `vwCustomerItems`,
`vwEngagementItems`, `vwTimeEntryItems`, and `vwExternalItems` as their
`UNION ALL`. Item IDs `cust<n>`, `eng<n>`, `time<n>`. The soft-delete filter
lives in the view, so the push tool cannot forget it. End the file with the
requirement expressed as SQL — one customer's name, counted by item type,
returning all three.

## 11. Build, gates, and two release lines

- `Build.ps1`: scan, build, test, dependency audit, publish, zip. Four gates,
  and CI runs the same four.
- The release package is **self-contained** — the .NET runtime ships inside it.
  Whoever downloads the zip needs nothing else except the agent (Microsoft's,
  tenant-tied) and the client certificate (the customer's PKI). A CI step fails
  the build if the executable, the runtime, the install script, the SQL scripts,
  the deploy scripts, the docs **or the drawings** are missing from the archive.
- An offline package path for a build machine with no route to
  `api.nuget.org`, and a CI check that the offline list still matches the real
  restore graph. It will go stale on the first Dependabot bump; make it fail
  loudly when it does.
- **Two release lines.** `main` targets `net10.0`; `release/net9` targets
  `net9.0` and exists because Visual Studio 2022 cannot load a `net10.0` project
  whatever SDK is installed. Put the framework in `Directory.Build.props` as one
  property, so the branches differ in **that property and a `global.json`, and
  nothing else**. CI on `main` builds and tests `net9.0` too — with the .NET 9
  SDK itself, not .NET 10 targeting downwards — so the branch cannot rot.
- Releases: push a `v*` tag, CI builds and creates a **draft**, a person
  publishes it. Both lines are released together with the same version, the
  net9 one suffixed `-net9`. When retiring old releases, **delete the releases
  and keep the tags.**

## 12. Documentation

Written for someone who will be woken at 3am by this system.

| File | For |
|---|---|
| `README.md` | Orientation, download, build, deploy, transfer |
| `docs/SECURITY.md` | Control mapping — every control with an ID, plus deviations and accepted risks |
| `docs/APP-REGISTRATION.md` | Every Entra identity, permission by permission, certificate and secret |
| `docs/RUNBOOK.md` | Rotation, log locations, failure modes |
| `docs/ASSUMPTIONS.md` | Answered questions, assumptions, deviations, defects found, open questions |
| `docs/TROUBLESHOOTING.md` | Agent-hosted path, stage by stage, a script per stage |
| `docs/TROUBLESHOOTING-DIRECT-PUSH.md` | The direct path, which fails differently |
| `docs/ADDING-A-PUSH-CONNECTOR.md` | The recipe for a new source: one class, one configuration file, and what the engine will not do for you |
| `docs/HIERARCHY-TEST-CASE.md` | The design: why a flat index needs flattening, and the full property annotation table |
| `docs/HIERARCHY-DEPLOYMENT.md` | Step-by-step deployment of the three level connector, **.NET 10 and .NET 9 at every step** |
| `docs/architecture.svg` + `.png` | The data flow — Search and Copilot as siblings. Markdown embeds the PNG; the SVG is the editable source |
| `docs/hierarchy-flow.svg` + `.png` | Source hierarchy → views → flat items, ancestor-carried fields highlighted |

Two rules that are easy to get wrong. **Real numbers in drawings** — if the
diagram says 95 items, 95 must be what the sample data actually produces.
And **the drawings must be in the release zip** — markdown embeds the PNGs, the
SVGs travel as editable sources; a README that renders its diagrams on GitHub
but not in the package is broken for the one reader who matters.

## 13. Diagnostics

A read-only script per stage, each printing what it checked and what it
concluded. Between them they must catch the failures that look like something
else:

- **The agent port map edited after the agent started.** Compare the file's
  `LastWriteTime` against the agent process `StartTime` and fail on it. This
  looks exactly like a connector that will not start.
- **A break in the watermark chain.** Reconstruct every crawl from the log and
  check link by link that each crawl's `watermarkIn` is the previous
  `watermarkOut`. A break is silent data loss.
- **Future timestamps, and local time in a UTC column.** Both drift the
  watermark and neither raises an error.
- **Orphans.** Reconcile source against index and print the `DELETE` commands
  without running them. Check tombstones *before* applying any `-MaxItems` cap,
  or the cap hides the very thing you are looking for.
- **Consented roles.** Decode the `roles` claim of the acquired token, so
  "missing consent" and "wrong connection owner" stop looking identical.

A pre-flight script must test **the deployment**, not the operator. If it
prompts for a client secret it validates what was typed; read the real
credential store entry and report which source it came from.

## 14. Decisions already taken — do not re-open

1. **`Grpc.Core` 2.40.0 stays.** It is what the contracts were generated
   against. `Grpc.Net.Client` is not a drop-in and the migration is not in
   scope.
2. **`Google.Protobuf` is pinned at 3.18.0**, raised to 3.35.1 only when the
   optional OTLP exporter is switched on, because that sink requires 3.26.1+ and
   both constraints cannot hold at once. The generator is unchanged either way,
   so the contract types are identical. CI builds both.
3. **The OTLP exporter is behind a build switch as well as a runtime flag**, so
   a feature that ships disabled does not put a second gRPC stack into every
   dependency scan.
4. **`Authentication=ActiveDirectoryDefault` is not set** alongside
   `SqlConnection.AccessToken`. SqlClient throws when both are supplied, and it
   would reintroduce the non-deterministic credential chain section 4 rules out.
5. **`Auth:Mode: ClientSecret` is supported**, for a tenant that will not issue
   a certificate. The secret lives in **Windows Credential Manager** under the
   service account, read once at startup via `CredReadW`/`CredFree`, with only
   the entry name in configuration. This is a deliberate departure from an
   original control that excluded DPAPI-backed storage; record it as such with
   its trade-offs. Certificate stays the default — the alternative this competes
   with is a secret in a config file, not a certificate.
6. **The connector ID does not change.** Changing it breaks every existing
   connection. Validate it and warn on a mismatch.
7. **Windows integrated SQL auth is the shipped configuration**, using the
   service account identity, so no credential appears in a connection string at
   all.

## 15. Landmines — do not rediscover these

Every one of these was found by a test or by reasoning, not by inspection. They
are silent.

1. **`DateTimeStyles.RoundtripKind | DateTimeStyles.AdjustToUniversal` is an
   invalid combination** and `DateTime.TryParse` *throws* on it — `TryParse`
   notwithstanding. Any incremental crawl receiving a checkpoint fails. Parse
   with `RoundtripKind` and normalise to UTC explicitly.
2. **`SqlConnectionStringBuilder.ContainsKey` returns `true` for every keyword
   SqlClient knows**, supplied or not. Inspect with `ShouldSerialize`, or your
   "does this string contain a password" check reports one in every string.
3. **Serilog stringifies an object at capture time for a plain `{Value}` hole**,
   before any destructuring policy runs. A protobuf message logged that way
   writes full JSON — including item content — to disk. Register the risky types
   as scalars *and* re-apply the redaction policy in the enricher, and canary-test
   both spellings.
4. **`SqlCredential` cannot be combined with `User ID` in the connection
   string.** Set `Integrated Security = false`, leave the user out of the
   string, attach the credential.
5. **T-SQL will not mix aggregate and non-aggregate expressions in one `OUTER
   APPLY`.** The roll-up (`COUNT`/`SUM`) and the `STRING_AGG` over `DISTINCT`
   have to be separate applies.
6. **`Grpc.Tools` 2.40.0 ships no arm64 `protoc`** — the build fails on Apple
   silicon with "Bad CPU type in executable". CI on `windows-latest` is the
   authority for tests; do not chase this locally.
7. **PowerPoint rewrites a `.pptx` while it is open.** If you open a committed
   deck to look at it, the file changes underneath you. Do not commit that.
8. **`qlmanage` squares the canvas** and crops wide SVGs. Render viewBox slices
   to check a diagram.

## 16. Definition of done

- `dotnet build` clean but for `NETSDK1206` and `CS8981`; `dotnet test` green on
  `windows-latest`, both frameworks. Tests require no live tenant, vault or
  database.
- **The schema guards are tested, not just written.** They are the one piece of
  code whose failure cannot be undone, and a guard inside top level statements
  is unreachable from a test assembly — so the schema does not live there. Prove
  the guards by mutation: break the rule on purpose and watch a test go red. A
  test that has never failed has not been shown to test anything.
- The four `.proto` files byte-identical to Microsoft's originals.
- `grep -rE 'password|secret|pwd|apikey|connectionstring'` over every
  `appsettings.json` returns names and placeholders, never a value.
- The security reviewer can answer "where is this control implemented" from
  `docs/SECURITY.md` alone, without opening the code.
- An operator with the release zip, the agent, and a certificate can deploy from
  `README.md` and `docs/HIERARCHY-DEPLOYMENT.md` without asking a question.
- Every claim you make is one you have checked. Where you could not check it —
  no SQL Server, no live tenant — **say so explicitly** rather than writing it
  as though you had.

---

# ▸ END OF PROMPT

## What this deliberately leaves open

Stated so nobody reads the omissions as oversights.

- **Per-status or per-assignee ACLs.** The shape is a list of groups and every
  item is granted to all of them. Finer grain needs a new configuration shape
  and a change to `AclBuilder`. Nothing blocks it.
- **Deletion from a direct push.** Still unsolved, by design of the platform
  rather than of this code. `Compare-SourceToIndex.ps1` finds the orphans and
  prints the commands; a person runs them.
- **Roll-ups at query time.** They are as of the last push. Making them live
  would mean a different architecture, not a different query.
- **Hadoop, or any non-SQL source.** Raised, scoped, parked. The flattening idea
  transfers; the connection layer does not.

The repository is the authority. This document is why it looks the way it does.
