# Adding a SQL push connector

A new SQL source is **one class and one configuration file**. Nothing in
`SqlPushCore` changes, and no connector already there is touched, rebuilt
differently, or able to notice.

This document is the recipe and the reasoning. If you only want the recipe,
[skip to Step 1](#step-1--write-the-connector).

---

## What is shared and what is yours

| | Where it lives | Who writes it |
|---|---|---|
| Certificate or client secret, Key Vault, token acquisition | `SqlPushCore` → `SqlTicketsConnector.Security` | Nobody. It is done |
| SQL connection, encryption, retry, error classification | Same | Nobody |
| Creating the external connection, registering the schema, polling to `Ready` | `SqlPushCore/PushEngine.cs` | Nobody |
| Content truncation, ACLs, item ID rules, the `PUT`, throttling backoff | Same | Nobody |
| Configuration shape, validation, exit codes, logging, `--dry-run`, `--help` | `SqlPushCore/PushHost.cs` | Nobody |
| **The schema** | your connector | You |
| **The query** | your connector | You |
| **The row → item mapping** | your connector | You |
| **Anything specific to your source** | `Settings` in your appsettings file | You |

Four things. That is the whole interface, and it is
[`IPushConnector`](../src/SqlPushCore/IPushConnector.cs).

**The engine is not extended, it is used.** If you find yourself wanting to
change a file in `SqlPushCore` to make your connector work, stop and read
[When the core does have to change](#when-the-core-does-have-to-change) — the
answer is usually the `Settings` bag, and when it is not, the change is a real
one that affects every connector and should be made deliberately.

---

## Step 1 — write the connector

One file. This is a complete, working example.

```csharp
namespace SqlInvoicePush;

using Microsoft.Data.SqlClient;
using Microsoft.Graph.Models.ExternalConnectors;
using SqlPushCore;

public sealed class InvoicePushConnector : IPushConnector
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

    public PushItem MapRow(SqlDataReader reader, PushOptions options)
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

Four rules the compiler and the tests will hold you to:

1. **`isSearchable` and `isRefinable` are mutually exclusive.** `PushSchema.Prop`
   throws rather than letting you find out fifteen minutes into a server side
   registration, against a draft connection that can then only be deleted.
2. **Property names are 32 alphanumeric characters.** No underscores, hyphens or
   spaces. Same guard, same reason.
3. **Item IDs are 128 alphanumeric characters.** Compose them (`inv" + id`)
   rather than reusing a natural key that might contain punctuation.
4. **Omit a property rather than sending null.** Graph rejects a null value
   rather than ignoring it. `AddIfPresent` is what that looks like.

**Before you write the schema, read
[`HIERARCHY-TEST-CASE.md`](HIERARCHY-TEST-CASE.md).** A registered schema is
append-only: you can add a property, but no property's type, annotation or label
can ever be changed. Correcting one means deleting the connection and every item
in it. This is the one part of the job worth being slow about.

## Step 2 — host it

Either add it to an existing push executable, or give it one. A push
executable's whole `Program.cs` is:

```csharp
using SqlPushCore;

return await PushHost.RunAsync(args);
```

`PushHost` finds every `IPushConnector` compiled into the executable — by
reflection over that one assembly, never by scanning a folder for DLLs to load,
so what the tool can do is decided at build time and is visible to the package
assertion in CI.

- **One connector in the executable**: it runs, no flag needed.
- **More than one**: `--connector invoices` selects it, and `--help` lists them.

A new project is a copy of
[`SqlHierarchyPush.csproj`](../src/SqlHierarchyPush/SqlHierarchyPush.csproj)
with the name changed. It references `SqlPushCore` and nothing else — package
versions and their advisory pins live there, so the offline restore graph cannot
drift between push tools.

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
`options.Setting("IncludeDrafts", false)`. This is the mechanism that keeps the
core still: a value only your connector understands never becomes a field every
other connector has to carry and ignore.

## Step 4 — the SQL side

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

## Step 5 — test it

Add a class to `tests/SqlTicketsConnector.Tests`. At minimum, assert:

- the property list, spelled out, so adding one is a deliberate two-file edit
- no property is both searchable and refinable, and every name is within the limit
- which properties must stay searchable, and why, in the failure message
- each semantic label appears exactly once
- the query reads the configured view and honours `MaxItems`

`PushEngineTests.cs` is the template; it also holds a `SampleConnector` written
exactly the way yours will be. That connector exists to prove this document is
true — if adding a connector ever required editing `SqlPushCore`, that file
would stop compiling.

**`MapRow` cannot be unit tested.** `SqlDataReader` is sealed with no interface
behind it, so a row cannot be faked. Exercise it with `--dry-run`, which reads
and maps the real source and reports what would be written without writing
anything.

## Step 6 — package it

If you added a new executable, add it to `Build.ps1` beside the other two, and
add its `.exe` and `SqlPushCore.dll` to the package completeness assertion in
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
   IDs, it goes in `ExternalSchemaRules` in the Security project, where every
   connector picks it up at once.

Everything else — a filter, a threshold, a URL template, a lookup — is
`Settings` and your own `MapRow`.

If you do change the core, `PushEngineTests` and `PushConfigurationTests` cover
it, and every connector is affected by definition. Say so in the commit message.

## What the engine will not do for you

Stated plainly, because each of these has surprised somebody:

- **It never deletes.** A row excluded from your query — soft deleted, filtered,
  outside `MaxItems` — leaves its item in the index. Use
  [`deploy/Compare-SourceToIndex.ps1`](../deploy/Compare-SourceToIndex.ps1) to
  find the orphans, and expect to delete them yourself.
- **There is no incremental crawl.** A push writes everything the query returns,
  every run. Watermarking is the agent-hosted connector's model, not this one.
  [`TROUBLESHOOTING-DIRECT-PUSH.md`](TROUBLESHOOTING-DIRECT-PUSH.md) has the
  full list of what this model gives up.
- **It cannot enumerate what it wrote.** Graph has no list-items API. If you
  want to reconcile, keep the source-side list.
- **Two connectors cannot share a connection.** They register different schemas,
  a registered schema cannot be replaced, and whichever app created the
  connection is the only one that can manage it. The host refuses a connection
  ID belonging to a connector hosted alongside yours; across separate
  executables nothing can see the collision, so pick a distinct ID and mean it.
