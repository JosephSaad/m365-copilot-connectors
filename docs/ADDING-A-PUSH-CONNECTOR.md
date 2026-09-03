# Adding a push connector

A new source is **one class and one configuration file**. Nothing in
`PushCore` changes, and no connector already there is touched, rebuilt
differently, or able to notice.

There are two ways in, and what you are reading decides which one you take:

- **A SQL Server table or view.** Implement
  [`ISqlPushConnector`](../src/PushCore.Sql/ISqlPushConnector.cs): a schema, a query and a row
  mapping. The family supplies the rest of the connector on your behalf.
- **Anything that is not a database.** Implement
  [`IPushConnector`](../src/PushCore/IPushConnector.cs) directly and write an
  [`IPushSource`](../src/PushCore/IPushSource.cs) that opens the thing and enumerates it. That is
  what the Cloudera connectors in `CdpGraphPush` do — a filesystem and a Hive table have no
  `SqlDataReader` to map.

This document is the recipe and the reasoning. If you only want the recipe,
[skip to Step 1](#step-1--write-the-connector).

---

## What is shared and what is yours

| | Where it lives | Who writes it |
|---|---|---|
| Certificate or client secret, Key Vault, token acquisition | `PushCore` → `Connector.Security` | Nobody. It is done |
| SQL connection, encryption, retry, error classification | `PushCore.Sql` → `Connector.Security` | Nobody |
| Creating the external connection, registering the schema, polling to `Ready` | `PushCore/PushEngine.cs` | Nobody |
| Content truncation, ACLs, item ID rules, the `PUT`, throttling backoff | Same | Nobody |
| Configuration shape, validation, exit codes, logging, `--dry-run`, `--help` | `PushCore/PushHost.cs` | Nobody |
| **The schema** | your connector | You |
| **The query, or the enumeration** | your connector, or your source | You |
| **The row → item mapping** | your connector | You |
| **Anything specific to your source** | `Settings` in your appsettings file | You |

**The projects were renamed, and the rename is the point.** `SqlPushCore` is now `PushCore` and
`SqlConnector.Security` is now `Connector.Security`, because neither of them is about SQL: they
hold credentials, schema rules, content handling, the engine and the host, and a connector reading
HDFS needs every one of those and no database at all. The SQL half moved out into `PushCore.Sql`,
which `CdpGraphPush` does not reference — nothing there opens a SQL Server connection, so nothing
there carries `SqlClient`.

**The engine is not extended, it is used.** If you find yourself wanting to
change a file in `PushCore` to make your connector work, stop and read
[When the core does have to change](#when-the-core-does-have-to-change) — the
answer is usually the `Settings` bag, and when it is not, the change is a real
one that affects every connector and should be made deliberately.

---

## Which interface

[`IPushConnector`](../src/PushCore/IPushConnector.cs) names no source technology. Six members have
no default and you must write them; five have defaults and you write them only when you mean
something other than the default.

| Member | Default | What it decides |
|---|---|---|
| `Key` | none | The `--connector` name, and which `appsettings` file is yours |
| `DisplayName` | none | What appears in logs and in `--help` |
| `DefaultConnectionId` | none | The connection you own, and the neighbour guard below |
| `DefaultConnectionName` | none | Connection display name when configuration omits one |
| `DefaultDescription` | `""` | Connection description when configuration omits one |
| `ItemsCarryTheirOwnAcl` | `false` | Connection-wide grants, or per-item grants |
| `BuildSchema()` | none | The schema registered on the connection |
| `CreateSource(context)` | none | Opens the source and returns it |
| `ApplyDefaults(options)` | adds nothing | The connector-specific half of what the file left out |
| `Validate(options, errors)` | calls `ValidateOptions` | The source family's checks, then yours |
| `ValidateOptions(options, errors)` | adds nothing | **Your** configuration rules |

[`ISqlPushConnector`](../src/PushCore.Sql/ISqlPushConnector.cs) narrows that to three members and
supplies `CreateSource`, `ApplyDefaults` and `Validate` for you:

| Member | What it decides |
|---|---|
| `DefaultItemView` | The table or view used when `Source:ItemView` is omitted |
| `BuildQuery(options)` | The T-SQL returning one row per external item |
| `MapRow(reader, options)` | The item for the current row, or `null` to skip it |

`ItemsCarryTheirOwnAcl` is the one default worth a second look. `false` means every item gets the
connection-wide ACL from `Acl:GrantGroupObjectIds`, which is then required in configuration —
right for a table whose rows are all readable by the same people. `true` means the source derives
grants per item, that setting is neither required nor read, and an item whose groups could not be
resolved is **skipped rather than written**. There is no fallback on purpose: a fallback would
widen the audience of exactly the item whose permissions could not be established.

---

## Step 1 — write the connector

One file, whichever path you take. There are now **three**, and the choice is
decided by the source rather than by preference.

| Path | Interface | Use it when |
|---|---|---|
| **A** | [`ISqlPushConnector`](../src/PushCore.Sql/ISqlPushConnector.cs) | The source is a **SQL Server** table or view |
| **A′** | [`IDbPushConnector`](../src/PushCore.Db/IDbPushConnector.cs) | The source is a table or view on **any other ADO.NET provider** — Oracle and Teradata are the two that exist |
| **B** | [`IPushSource`](../src/PushCore/IPushSource.cs) directly | The source is not relational at all. MongoDB is the example |

**Why A and A′ are separate rather than one generalised path.** `PushCore.Sql`
is bound to `SqlConnection` throughout, and it carries the pilot that has been
live-tested twice at 111,800 items. Rewriting it onto the provider-agnostic
abstraction to save the duplication would put regression risk exactly where this
project can least afford it, so the two coexist. If SQL Server ever moves across
it is its own change, made when nothing is waiting on it.

**What A′ asks for that A does not.** The connector supplies its own
`DbProviderFactory` and builds its own connection string, which is what keeps
`PushCore.Db` free of every database driver — the Oracle and Teradata packages
are referenced by the leaf executables that open those connections and by
nothing else. It may also declare a `WatermarkColumn`, and doing so is what puts
it on the marker tier: `DbPushSource` then derives `ChangeDetection` and flips
`RequiresOrderedCommit` to true, rather than letting a connector claim the tier
without the column the tier depends on.

**Both A′ and B carry a guard.** `IDbPushConnector.GuardAsync` runs on the open
connection before the query, and it is where a provider that enforces per user
is refused — Oracle's VPD, Label Security and data redaction; Teradata's
row-level security constraints. Path B does the same in its own `ReadAsync`, as
MongoDB does for views and encrypted fields. That is not optional politeness:
see [SECURITY.md](SECURITY.md) controls CDP-1, CDP-17 and DB-1.

### Path A — a SQL table or view

```csharp
namespace SqlInvoicePush;

using Microsoft.Data.SqlClient;
using Microsoft.Graph.Models.ExternalConnectors;
using PushCore;
using PushCore.Sql;

public sealed class InvoicePushConnector : ISqlPushConnector
{
    public string Key => "invoices";
    public string DisplayName => "Customer invoices";

    // Used when configuration does not name one. This is also how the host
    // stops another connector being pointed at your connection.
    public string DefaultConnectionId => "custinvoices";
    public string DefaultConnectionName => "Customer invoices";
    public string DefaultItemView => "dbo.vwInvoiceItems";

    public Schema BuildSchema() => PushSchema.Of(
        PushSchema.Prop("title", PropertyType.String, searchable: true, queryable: true,
            retrievable: true, label: Label.Title),
        PushSchema.Prop("url", PropertyType.String, retrievable: true, label: Label.Url),
        PushSchema.Prop("issued", PropertyType.DateTime, queryable: true, retrievable: true,
            label: Label.LastModifiedDateTime),
        PushSchema.Prop("customerName", PropertyType.String, searchable: true, queryable: true,
            retrievable: true),
        PushSchema.Prop("currency", PropertyType.String, queryable: true, retrievable: true,
            refinable: true),
        PushSchema.Prop("amount", PropertyType.Double, queryable: true, retrievable: true));

    public string BuildQuery(PushOptions options)
    {
        string top = options.Source.MaxItems > 0 ? $"TOP ({options.Source.MaxItems}) " : string.Empty;

        return $"SELECT {top}InvoiceId, Title, Url, Issued, CustomerName, Currency, Amount, Content " +
               $"FROM {options.Source.ItemView} ORDER BY InvoiceId;";
    }

    public PushItem? MapRow(SqlDataReader reader, PushOptions options)
    {
        var item = new PushItem
        {
            Id = "inv" + SqlRead.Integer(reader, "InvoiceId"),
            ItemType = "Invoice",
            Content = SqlRead.Text(reader, "Content"),
        };

        item.Properties["title"] = SqlRead.Text(reader, "Title");
        item.Properties["url"] = SqlRead.Text(reader, "Url");
        item.Properties["issued"] = SqlRead.Utc(reader, "Issued");
        item.Properties["customerName"] = SqlRead.Text(reader, "CustomerName");

        item.AddIfPresent("currency", SqlRead.Text(reader, "Currency"));
        item.AddIfPresent("amount", SqlRead.Number(reader, "Amount"));

        return item;
    }
}
```

Returning `null` from `MapRow` skips the row and increments the source's `Skipped` count, so a row
with no usable key does not have to be filtered out in the view.
[`HierarchyPushConnector.cs`](../src/SqlHierarchyPush/HierarchyPushConnector.cs) is the shipped
example of this path.

### Path B — anything else

Implement `IPushConnector` directly. The connector is still a key, a schema and one method that
assembles a source; the reading lives in the `IPushSource` you hand back.

```csharp
namespace ArchivePush;

using Microsoft.Graph.Models.ExternalConnectors;
using Connector.Security.Configuration;
using PushCore;

public sealed class ArchiveConnector : IPushConnector
{
    public string Key => "archive";
    public string DisplayName => "Records archive";

    public string DefaultConnectionId => "recordsarchive";
    public string DefaultConnectionName => "Records archive";
    public string DefaultDescription => "Documents held in the records archive";

    // Two documents in one folder can have different readers, so a
    // connection-wide grant would be wrong for almost every item.
    public bool ItemsCarryTheirOwnAcl => true;

    public Schema BuildSchema() => PushSchema.Of(
        PushSchema.Prop("title", PropertyType.String, searchable: true, retrievable: true,
            label: Label.Title),
        PushSchema.Prop("itemPath", PropertyType.String, queryable: true, retrievable: true,
            label: Label.ItemPath),
        PushSchema.Prop("modifiedUtc", PropertyType.DateTime, queryable: true, retrievable: true,
            label: Label.LastModifiedDateTime));

    // Everything a source needs is already resolved on the context: the
    // configuration, the credential that also authenticates to Graph, the
    // caching secret provider (null when no vault is configured) and the
    // logger. Building a second credential or a second cache here would
    // quietly double the token traffic.
    public IPushSource CreateSource(PushSourceContext context) =>
        new ArchivePushSource(context.Options, context.Credential, context.Log);

    // Your rules, on top of the ones every connector shares. Never Validate.
    public void ValidateOptions(PushOptions options, ValidationErrors errors)
    {
        if (string.IsNullOrWhiteSpace(options.Setting("ArchiveBaseUrl")))
        {
            errors.Add("Settings:ArchiveBaseUrl", "is required.");
        }
    }
}
```

[`HdfsDocumentsConnector.cs`](../src/CdpGraphPush/HdfsDocumentsConnector.cs) is the worked example:
a key, a schema, `ValidateOptions`, and a `CreateSource` that assembles a WebHDFS client, a Ranger
policy client, an ACL builder, an extractor set and a checkpoint store into an `HdfsPushSource`.
It is 128 lines including the header, and none of them are about credentials, throttling or
retries.

[`AtlasCatalogueConnector.cs`](../src/CdpGraphPush/AtlasCatalogueConnector.cs) is the second, and
worth reading for a different reason: it shows how far a connector's *own* rules can differ from
its neighbours' without the core noticing. It indexes the Atlas catalogue, and it decides who may
see an entry by a rule none of the others use — the groups Ranger grants `select` on the table an
entry describes, which is deliberately stricter than the cluster's own default. It also indexes
descriptions of tables whose *data* is refused, because a row filter hides rows rather than the
existence of the table. All of that lives in the connector and its source. `PushCore` knows only
that it was handed a schema and an `IPushSource`.

That is the property to test a new connector against: if your source's access rules need a change
to `PushCore`, they are probably in the wrong place.

### Four rules the compiler and the tests will hold you to

1. **`isSearchable` and `isRefinable` are mutually exclusive.** `PushSchema.Prop`
   throws rather than letting you find out fifteen minutes into a server side
   registration, against a draft connection that can then only be deleted.
2. **Property names are 32 alphanumeric characters.** No underscores, hyphens or
   spaces. Same guard, same reason.
3. **Item IDs are 128 alphanumeric characters.** Compose them (`"inv" + id`)
   rather than reusing a natural key that might contain punctuation.
4. **Omit a property rather than sending null.** Graph rejects a null value
   rather than ignoring it. `AddIfPresent` is what that looks like.

**Before you write the schema, read
[`HIERARCHY-TEST-CASE.md`](HIERARCHY-TEST-CASE.md).** A registered schema is
append-only: you can add a property, but no property's type, annotation or label
can ever be changed. Correcting one means deleting the connection and every item
in it. This is the one part of the job worth being slow about.

**If your source classifies its own content**, two lines are all it takes, and
both belong in the schema decision above because neither can be retrofitted to a
live connection:

```csharp
// In MapRow / MapAsync - raw, in the source's vocabulary, uninterpreted.
item.Classifications = row.Tags;

// In BuildSchema. String, not StringCollection: one item has ONE label.
PushSchema.Prop(
    SensitivityOptions.DefaultProperty,
    PropertyType.String,
    queryable: true, retrievable: true, refinable: true)
```

Register the property whether or not anyone has configured a mapping yet.
`EnsureSchemaAsync` will not PATCH a connection that has reached `Ready`, so the
alternative to registering it now is deleting the connection the day somebody
wants it. What the tags *mean* is not your problem — that is the `Sensitivity`
section, and it is configured once for every connector. See
[`SENSITIVITY-LABELS.md`](SENSITIVITY-LABELS.md).

---

## Why `ISqlPushConnector` implements `Validate` explicitly

`CreateSource`, `ApplyDefaults` and `Validate` on `ISqlPushConnector` are **explicit** interface
implementations. That is not a style choice, and `Validate` is the member that makes it matter.

The intended arrangement is that a source family validates its own configuration once — for the
SQL family, `SqlSourceRules` checks the `DataSource` section, the view name and the vault secret —
and each connector adds its own rules in `ValidateOptions` on top. The host calls `Validate`, and
`Validate` calls `ValidateOptions`.

Had the interface implemented `Validate` implicitly, a connector class defining a method with that
name would **replace** its family's checks rather than add to them. The connector would compile,
the tests for the connector would pass, and the `DataSource` section would stop being validated at
all — a loss that looks exactly like a passing build. Explicit implementation makes it impossible:
a connector cannot call the family's `Validate` by accident and cannot hide it either. The family's
checks run, and then yours do.

The same reasoning is why `PushHost` calls `connector.Validate` and never `ValidateOptions`
directly, and why a throw out of connector-authored validation is caught and reported as exit 2
rather than escaping `Main`.

---

## The `IPushSource` contract, and the rule it exists to make structural

If you took Path B you are writing an [`IPushSource`](../src/PushCore/IPushSource.cs). It is four
members, and the shape of them is an argument.

```csharp
int Skipped => 0;
IAsyncEnumerable<PushItem> ReadAsync(CancellationToken cancellationToken);
ValueTask OnItemCommittedAsync(PushItem item, CancellationToken cancellationToken);
ValueTask OnCrawlCompletedAsync(CancellationToken cancellationToken);
```

**The unbreakable rule of this repository is that a failed crawl must never advance the
watermark.** The only component that knows whether an item reached the index is the engine,
because the engine made the `PUT`. So the engine tells the source what actually landed, item by
item:

- **`ReadAsync`** yields candidates, **in ascending checkpoint order**. Yielding is not indexing:
  an item may still be truncated, refused by Graph, or dropped as a duplicate.
- **`OnItemCommittedAsync`** is called by the **engine**, only after the write for that item
  returned successfully, and **never during a dry run**. This is where a source advances its
  in-memory marker.
- **`OnCrawlCompletedAsync`** is called only when the enumeration reached its end with no failed
  write. Only here may a source record something describing the run as a whole.

A source that checkpointed inside `ReadAsync` would be recording rows it had merely read. A source
that checkpointed on dispose would be recording a run that threw. Neither is reachable through this
interface, because **the failure path is simply the absence of a call**. That is what "structural"
means here: it is not a convention each connector has to remember.

Ordering is the source's contract, not the engine's. Resumption compares the stored marker against
the next item, so a source that yields out of order loses rows on the run *after* an interruption
rather than on the run itself — which is the harder failure to notice.

Flush the marker to durable storage as often as the source can afford. Everything since the last
flush is re-read after an interruption, which is safe because the write is an upsert, and cheap
compared with losing it.

**`Skipped`** reports how many candidates the source examined and declined to yield: a row with no
key, a file of a type this connector does not index, a table Ranger says must not be indexed. It
defaults to zero and is read once, after the enumeration ends. It exists so the run summary still
reconciles against the source — without it, "1,000 rows in the table, 940 items indexed" has no
explanation in the log.

`SqlPushSource` implements all of this already, which is why Path A never sees it. There is no
watermark in the SQL family: a SQL push re-reads its whole query every run, so the commit callbacks
have nothing to record. A future SQL connector that wants incremental reads implements them there
and gets the failed-crawl guarantee for free rather than inventing its own.

### Exit codes, and the one your source controls

Exit codes are part of the interface and are the same for every connector:

| Code | Meaning |
|---|---|
| 0 | Success |
| 2 | Configuration invalid, or no connector could be selected |
| 3 | The credential could not be built, or was rejected |
| 4 | Ingestion failed partway |

Three is the one a source has to participate in. Graph's own rejections already land there. A
source rejecting the service identity — an expired Kerberos ticket, a revoked grant, a SQL login
that lost its role — is the same class of fault and must reach the same exit code, or a monitoring
rule keyed to 3 sends somebody into the data path to look for a bug that is really a rotation.
Only the source knows which of its driver's error codes mean "authentication" rather than
"unavailable", so raise
[`PushSourceAuthenticationException`](../src/PushCore/PushSourceAuthenticationException.cs) with the
driver's exception as the inner one, rather than letting the driver's own type escape.

---

## Step 2 — host it

Either add it to an existing push executable, or give it one. A push
executable's whole `Program.cs` is:

```csharp
using PushCore;

return await PushHost.RunAsync(args);
```

`PushHost` finds every `IPushConnector` compiled into the executable — by
reflection over that one assembly, never by scanning a folder for DLLs to load,
so what the tool can do is decided at build time and is visible to the package
assertion in CI. Two connectors sharing a `Key` are refused at startup rather than
resolved by whichever the reflection order happened to return first.

- **One connector in the executable**: it runs, no flag needed.
- **More than one**: `--connector invoices` selects it, and `--help` lists them.

`CdpGraphPush.exe` is the two-connector case in the repository:
`--connector cdphdfsdocs` and `--connector cdphivecontracts`, with
`appsettings.cdphdfsdocs.json` and `appsettings.cdphivecontracts.json` beside the executable.

A new SQL project is a copy of
[`SqlHierarchyPush.csproj`](../src/SqlHierarchyPush/SqlHierarchyPush.csproj)
with the name changed. It references `PushCore` and `PushCore.Sql` and nothing else — package
versions and their advisory pins live in `PushCore`, so the offline restore graph cannot
drift between push tools. A non-SQL project is
[`CdpGraphPush.csproj`](../src/CdpGraphPush/CdpGraphPush.csproj): `PushCore` and its own source
projects, and deliberately **not** `PushCore.Sql`.

## Step 3 — write the configuration file

Copy [`src/SqlHierarchyPush/appsettings.json`](../src/SqlHierarchyPush/appsettings.json)
and change `Graph` and `Source`. Every other section is the same for every
connector, and every one of them holds names and references only —
[`SECURITY.md`](SECURITY.md) §2 is the rule and the build enforces it.

```jsonc
{
  "Graph":  { "ConnectionId": "custinvoices", "ConnectionName": "Customer invoices" },
  "Source": { "ItemView": "dbo.vwInvoiceItems", "MaxItems": 0 }
}
```

**Which file gets read**: `appsettings.{Key}.json` when it exists, and
`appsettings.json` when it does not. So a second connector added to an existing
executable gets `appsettings.invoices.json` and the first one's file is not
touched, not moved, and not re-read.

**Anything specific to your source goes under `Settings`**, not into a new
property on `PushOptions`:

```jsonc
"Settings": { "RegionFilter": "EMEA", "BatchSize": "250" }
```

read as `options.Setting("RegionFilter")`, `options.Setting("BatchSize", 25)` or
`options.Setting("IncludeDrafts", false)`. Lookups are case insensitive. This is the mechanism that
keeps the core still: a value only your connector understands never becomes a field every
other connector has to carry and ignore. A non-SQL connector is mostly `Settings` —
`appsettings.cdphdfsdocs.json` is the example, with the HDFS gateway URL, the roots, the group map
and the checkpoint directory all living there and nothing in `PushOptions` knowing what any of them
mean.

A section a connector does not use is left empty rather than filled with a plausible value.
`CdpGraphPush` ships `"KeyVault": { "Uri": "" }` because it resolves no secret, and
`"Source": { "ItemView": "" }` because a filesystem has roots rather than a view. A configuration
file that invents values nothing reads has stopped describing the deployment.

## Step 4 — the source side

### If it is SQL

The engine runs **one query** and expects **one row per item**. Any join,
filter, roll-up or flattening happens in a view, not in C#.

That is a deliberate constraint rather than a limitation. A DBA can read a view
and see exactly what leaves the database; they cannot read a compiled binary.
It also means the soft-delete filter lives where the tool cannot forget it, and
that the grant can be `SELECT` on the view with `DENY` on the base tables —
[`sql/13-timesheet-least-privilege.sql`](../sql/13-timesheet-least-privilege.sql)
is the worked example.

`Source:ItemView` is concatenated into the query, because a table cannot be a
parameter. That is safe only because it is validated as a `[schema.]name`
identifier first — letters, digits and underscores, at most one dot. Do not
work around that check.

### If it is not

The equivalent decisions are yours to make in the source, and three of them are worth making
explicitly.

**Ordering.** Decide the composite marker before you write the enumeration, and sort by it before
yielding anything. The CDP sources use `(modification time, path)` for HDFS and
`(Settings:HiveWatermarkColumn, Settings:HiveKeyColumn)` for Hive, for the reason the SQL family's
watermark is composite: two files can share a timestamp to the millisecond, and a marker of only
the timestamp either re-reads that whole group for ever or loses whichever of them had not been
written when the run stopped.

**Persistence.** Write the marker temp-then-rename. A half-written checkpoint is worse than none,
because none means "recrawl everything" and half means "resume from a position nobody chose". An
unreadable or unparseable checkpoint is therefore treated as absent. The CDP sources keep theirs in
`Settings:CheckpointDirectory` (default `state`) as `{connectorKey}.watermark.json`.

**Permissions that change without changing a timestamp.** An incremental pass never revisits a file
whose group grant was revoked, because a permission change does not alter a modification time, so
its indexed ACL would stay stale indefinitely. `Settings:FullRecrawlEveryRuns` (default 7) makes
every Nth run ignore the marker, and is therefore the documented upper bound on ACL staleness — a
number that belongs in the deployment's risk record rather than only in a settings file.

If your source derives per-item grants, the rules are in
[`PushAclEntry`](../src/PushCore/PushAclEntry.cs) and they are short: **group principals only,
never users, never everyone**, and **there is no deny**. Graph supports deny ACEs and they take
precedence, which makes mirroring a source's deny rules look like the safe option — but a deny only
protects while it is translated correctly every time, and a mirror that drifts fails open. A source
with denies in scope is a source to route to a live query instead of indexing, which is why the type
cannot express one. A group that cannot be resolved to an Entra object ID is dropped, and an item
left with zero grants is skipped rather than written: an item granted to nobody is indexed and then
returned to no one.

## Step 5 — test it

Add a class to `tests/SqlTicketsConnector.Tests`. At minimum, assert:

- the property list, spelled out, so adding one is a deliberate two-file edit
- no property is both searchable and refinable, and every name is within the limit
- which properties must stay searchable, and why, in the failure message
- each semantic label appears exactly once
- for a SQL connector, that the query reads the configured view and honours `MaxItems`

`PushEngineTests.cs` is the template; it also holds a `SampleConnector` written
exactly the way yours will be. That connector exists to prove this document is
true — if adding a connector ever required editing `PushCore`, that file
would stop compiling.

**`MapRow` cannot be unit tested.** `SqlDataReader` is sealed with no interface
behind it, so a row cannot be faked. Exercise it with `--dry-run`, which reads
and maps the real source and reports what would be written without writing
anything to Graph — and without advancing any watermark.

**An `IPushSource` can be.** You wrote it, so it has whatever seams you gave it, and
`PushSourceTests.cs` is the template: it drives the real engine against a stub Graph call path and
asserts on what the source was actually told. Those tests are what fail if somebody moves the commit
callback above the write, calls it during a dry run, or reports completion after an exception.

For a connector against a real cluster, the test data is in `hadoop/`:
[`00-create-hdfs-test-data.sh`](../hadoop/00-create-hdfs-test-data.sh),
[`01-create-hive-test-data.hql`](../hadoop/01-create-hive-test-data.hql) and
[`02-create-ranger-test-policies.sh`](../hadoop/02-create-ranger-test-policies.sh). They create
`/data/caseworks/{contracts,policies,private}`, the tables `contracts.contract` and
`contracts.contract_ppi`, and the matching Ranger policies. The negative cases are the point:
`/data/caseworks/private` is mode 600, so no group can read it and nothing may be indexed from it,
and `contracts.contract_ppi` carries a Ranger row filter, so it must be routed to a live query and
must **not** appear in the index.

## Step 6 — package it

If you added a new executable, add it to `Build.ps1` beside the others, and
add its `.exe` and `PushCore.dll` to the package completeness assertion in
`.github/workflows/build.yml`. If you added a class to an existing executable,
there is nothing to do.

---

## When the core *does* have to change

Three cases, and only three:

1. **A new configuration section every connector needs.** Not one connector —
   every one. Otherwise it is a `Settings` key.
2. **A change to how items are written**: batching, a different content type,
   activities. That is engine behaviour and belongs in `PushEngine`.
3. **A new platform rule.** If Microsoft adds a constraint on schemas or item
   IDs, it goes in `ExternalSchemaRules` in `Connector.Security`, where every
   connector picks it up at once.

Everything else — a filter, a threshold, a URL template, a lookup — is
`Settings` and your own mapping.

**A new source family is not one of the three.** `PushCore.Sql` is what one looks like: a project
beside the core holding the interface, the source, the read helpers and the validation rules for
one kind of source, referenced only by the executables that need it. Adding a second family is that
again, and it costs `PushCore` nothing, because `IPushConnector` names no source technology.

If you do change the core, `PushEngineTests`, `PushSourceTests` and `PushConfigurationTests` cover
it, and every connector is affected by definition. Say so in the commit message.

## What the engine will not do for you

Stated plainly, because each of these has surprised somebody:

- **It never deletes.** A row excluded from your query — soft deleted, filtered,
  outside `MaxItems` — leaves its item in the index. Use
  [`deploy/Compare-SourceToIndex.ps1`](../deploy/Compare-SourceToIndex.ps1) to
  find the orphans, and expect to delete them yourself.
- **It does not crawl incrementally on your behalf.** The engine reads whatever the source yields
  and reports back what landed; deciding where to resume is the source's job. The SQL family does
  not resume at all — a push re-reads its whole query every run — and a source that wants a
  watermark implements the two callbacks and inherits the guarantee that a failed crawl cannot
  advance it. [`TROUBLESHOOTING-DIRECT-PUSH.md`](TROUBLESHOOTING-DIRECT-PUSH.md) has the full list
  of what this model gives up.
- **It cannot enumerate what it wrote.** Graph has no list-items API. If you
  want to reconcile, keep the source-side list.
- **Two connectors cannot share a connection.** They register different schemas,
  a registered schema cannot be replaced, and whichever app created the
  connection is the only one that can manage it. You get two layers of
  protection without writing any: within one executable, the host refuses a
  neighbour's connection ID at validation; across executables, the engine
  fetches the schema registered on the connection before pushing and refuses if
  it carries any property your connector does not build — naming the foreign
  properties. `--dry-run` performs the same check with read-only GETs. The one
  case no check can catch is a foreign connection that exists with NO schema
  registered yet; pick a distinct ID and mean it.
