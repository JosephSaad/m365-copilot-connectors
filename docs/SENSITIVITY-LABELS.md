# Sensitivity labels: mapping a source's tags, and refusing what must not be indexed

A source that classifies its own content — Atlas classifications, a catalogue's
tags, a real MIP label name — can have those tags mapped to a sensitivity label.
The label is published as a searchable property, and, if you ask it to, the
engine will refuse to index anything carrying a label you have declared
unindexable.

This document is what to read before turning that second half on.

---

## The short version

| | |
|---|---|
| Configured in | `Sensitivity` in the connector's appsettings |
| Modes | `Off` (default), `Annotate`, `Enforce` |
| Enforcement | Refusal to index. There is no deny ACE and no narrowed grant |
| Counted as | A skip, **and** separately as `RefusedByLabel` |
| Exit code | Unchanged. A refusal is the control working, not a failure |
| Where it runs | `PushEngine.Prepare`, for every connector, including dry runs |

---

## Why the engine and not the connector

The only source in this repository that has classifications is Atlas, and the
obvious place to put this was `AtlasPushSource.MapAsync`. That is the wrong
place.

A refusal to index is a security control, and a security control that lives in
one source is a control the next source silently does not have. The engine
already owns every other decision that must hold for all sources — truncation,
the ACL, the refusal to write an item granted to nobody — and this is one of
those.

A source's only job is to say what the row is tagged with. It sets
`PushItem.Classifications` to whatever the source calls them, in the source's own
vocabulary, and stops there. A source with no notion of classification leaves it
null and nothing changes for it.

---

## Why refusal, rather than a narrower audience

The instinct is to translate a label into a deny ACE, or into a smaller grant
set. Both were considered and neither is available.

`PushAclEntry` **cannot express a deny**, by design. Graph supports deny ACEs and
they take precedence, which makes them look like the safe way to mirror a
source's rules — but a deny only protects if it is translated correctly *every*
time, and a mapping that drifts fails open.

Narrowing the grant set requires knowing which Entra group corresponds to a
label. That mapping fails open the moment it drifts too, and it drifts silently,
because nothing about the index looks wrong afterwards.

Declining to index is the only closed option the type system leaves open, and it
has the property that its failure mode is a **missing search result** rather than
an exposed one.

---

## Configuration

```jsonc
"Sensitivity": {
  "Mode": "Enforce",
  "Property": "sensitivityLabel",

  // Required under Enforce. See "The two silent failures" below.
  "Unmapped": "Refuse",
  "Unlabelled": "Allow",

  // LEAST RESTRICTIVE FIRST. Order is the policy.
  "Labels": [
    { "Name": "Public",       "Classifications": [ "PUBLIC" ] },
    { "Name": "Internal",     "Classifications": [ "INTERNAL" ] },
    { "Name": "Confidential", "Classifications": [ "PII", "GDPR" ] },
    { "Name": "Restricted",   "Classifications": [ "PCI", "SOX" ], "Index": false }
  ]
}
```

### The three modes

**`Off`** — the default. Classifications on an item are ignored entirely. A
connector whose appsettings predates this feature deserializes no section at all
and behaves exactly as before.

**`Annotate`** — publish the mapped label as a property, and index everything. A
description of the corpus, not a control over it. `Index: false` does nothing
here, and saying it anyway is a **configuration error** rather than a silent
half-measure: it reads, to whoever wrote it, as protection.

**`Enforce`** — publish the label, and refuse to index an item whose label is not
indexable. The only mode that changes what reaches the index.

### Order is data, not code

`Labels` is an **ordered array**, least restrictive first. An item carrying
several classifications takes the most restrictive label any of them maps to,
and "most restrictive" means *later in this list*.

That ordering has to be written down somewhere. Putting it in configuration
means the operator who knows their own taxonomy declares it, rather than this
code guessing that "Confidential" outranks "Restricted" on a naming hunch.

A classification may belong to **one** label. Two labels claiming one tag has no
most-restrictive answer that is not arbitrary, and an arbitrary answer to "may
this be indexed" is the wrong kind of arbitrary. Validation refuses it.

Matching ignores case and surrounding whitespace, because a catalogue's tag
casing is not a contract — and in Atlas's case the tags are merged from two
endpoints that do not agree about it.

### The two silent failures

There are exactly two ways this control quietly indexes something it should not
have: an item carrying a classification **nobody mapped**, and an item carrying
**no classification at all**.

`Enforce` mode therefore **refuses to start** until `Unmapped` and `Unlabelled`
are both set. There is no safe default. Fail-closed strands a corpus that is
mostly untagged; fail-open is the exposure this feature exists to prevent; and
only the customer knows which of those their tagging discipline supports.

`Unmapped: "Refuse"` refuses an item **even if its other tags map and are
indexable**. An item tagged both `PUBLIC` and something unrecognised has an
unknown sensitivity, and the recognised half does not make it known.

---

## What this is not

It is **not** Microsoft Purview / MIP label propagation. It carries no protection
into the index, applies no encryption, and does not read a tenant label taxonomy.

It maps SOURCE tags to a NAME, publishes that name, and refuses the ones the
configuration says are not indexable. Where a source has a real MIP label, that
label's *name* is the classification to feed in here.

---

## Four things it cannot see

These bound what any policy built on it can claim. Read them before treating a
green run as evidence of coverage.

**Column-level tags.** Atlas deployments commonly tag PII at column level. This
repository reads a table's columns as display names only — `CdpSettings` refuses
`hive_column` outright — so a table whose only tagged thing is a column arrives
with no classification at all.

**Propagated versus direct.** A tag that reached an entity down a lineage edge is
indistinguishable from one an owner applied deliberately. `propagated` and
`entityGuid` are never parsed. `Enforce` will therefore refuse entities nobody
meant to tag.

**Ranger tag-based policies.** Only the resource service named by
`Settings:RangerSqlService` is read, so a mask or deny written on `cm_tag`
against the very classification you are mapping is invisible. This mapping will
be the **only** enforcement of it. Already recorded as a known gap in
[CDP-PILOT-PARAMETERS.md](CDP-PILOT-PARAMETERS.md).

**Existing connections.** A registered schema is append-only, and
`EnsureSchemaAsync` returns early the moment a connection is `Ready`. A newly
added `sensitivityLabel` property is therefore **never** PATCHed onto an existing
connection; `VerifySchemaOwnership` logs a warning calling it a pending addition
and the run proceeds. Items written with a value for an unregistered property are
refused by Graph, one at a time, and land in `Failed`.

The Atlas connector registers `sensitivityLabel` unconditionally for exactly this
reason — so a *new* connection has it whether or not the mapping is on yet. For
an existing connection, plan a deliberate schema migration or a new
`Graph:ConnectionId`.

---

## How to turn it on without breaking anything

**1. Run `Annotate` first.** It publishes the label and refuses nothing, so the
corpus can be inspected in Copilot before anything is withheld from it.

**2. Then dry-run `Enforce`.** The policy runs on the read path, so a dry run
refuses exactly the items a real run would — without writing. Every refusal is a
`Warning` naming the item ID and the reason. That is the only way to answer "how
much of this corpus would we lose" before committing.

**3. Then enable it.** Watch `crawl.items.refused_by_label` against
`crawl.items.skipped` for the first few runs.

---

## What a refusal does, precisely

The check runs in `PushEngine.Prepare`, **before** the ACL is resolved. Cheaper —
a refused item costs a dictionary probe instead of a group resolution, a
truncation and two hashes — and more correct, because an item that must not be
indexed must not be indexed whether or not anybody could have been granted it.

A refused item is:

- **not written to Graph**, on either the single-item or the `$batch` path
- **not committed to the source**, so the watermark cannot advance past a row the
  index does not have
- **counted as a skip**, which keeps `Total + Unchanged + Skipped` reconciling
  against rows read
- **counted again as `RefusedByLabel`**, which is what you evidence the control
  with
- **logged as a Warning** naming the item ID and the reason

The classification name appears in that warning. Tag names are metadata, not
content, so naming one is within the logging policy; the row itself still is not.

### The label is added before the hashes are taken

`ItemHasher.HashContent` covers the item's properties, so an item whose
classification changed hashes differently and is rewritten. A label added *after*
hashing would be published once and then never corrected on any later run.

### The exit code does not change

Only `Failed` drives a non-zero exit. Exit 4 exists for items that are absent
from the index **by accident**; these are absent **on purpose**, and paging
somebody nightly for a policy working correctly is how the policy gets switched
off. The host prints a separate warning line when refusals occurred, naming the
count against the skip count.

### A previously indexed item that becomes refusable

It is never marked seen in the state store, so on a **full** crawl the delete
sweep withdraws it — which is what label enforcement wants.

It does **not** happen on an incremental run, or without a state store. And a
mass relabelling that exceeds `Settings:MaxDeletePercent` (default 10) will have
the sweep refused, leaving the labelled items in the index. Plan a full crawl
after any bulk retagging, and raise the guard deliberately if the change is large.

---

## Not in the run row, and why

`RefusedByLabel` is **not** persisted to the crawl state store.

Adding a column there means a change to the `sql/40` table type, the
`SqlMetaData` ordinals and the dashboard models — a schema migration on a live
database — to carry a number that the log line, the run's own metric
`crawl.items.refused_by_label`, and a per-item warning already carry.

Evidence that a control is working does not have to live in the run row. If a
future release needs it there, `docs/UPGRADE-RUNBOOK.md`'s additive-only rule
covers how to add it.

---

## For connector authors

Set `PushItem.Classifications` from whatever your source calls them:

```csharp
var item = new PushItem
{
    Id = ItemId(entity.Guid),
    ItemType = "catalogue",
    Content = Describe(entity),
    Classifications = entity.Classifications.ToList(),
};
```

Raw, in the source's vocabulary, uninterpreted. Do not map them yourself — what a
tag *means* is policy, and policy is configured once for every connector rather
than compiled into each one.

If you want the label published on your connector's items, register the property
in `BuildSchema`:

```csharp
PushSchema.Prop(
    SensitivityOptions.DefaultProperty,
    PropertyType.String,
    queryable: true,
    retrievable: true,
    refinable: true)
```

`String`, not `StringCollection`: one item has **one** label. Several
classifications collapse to the most restrictive one, which is the whole reason
the mapping is an ordered list. Register it unconditionally, before you need it —
see "Existing connections" above.

---

## Related

- [SOURCE-CONTRACT.md](SOURCE-CONTRACT.md) — what a source owes the engine
- [ADDING-A-PUSH-CONNECTOR.md](ADDING-A-PUSH-CONNECTOR.md) — the whole of writing one
- [TELEMETRY.md](TELEMETRY.md) — the refusal counter and the rest of the metrics
- [CDP-PILOT-PARAMETERS.md](CDP-PILOT-PARAMETERS.md) — the Ranger tag-policy gap
- [SECURITY.md](SECURITY.md) — where this sits among the other controls


## Which connectors publish the label

Every connector on the direct-push path registers the sensitivity property in
its schema, and writes it only when a mapping is configured.

| Connector | Classifications come from |
|---|---|
| `cdpatlascatalog` | Atlas classifications on the entity |
| `cdphivecontracts`, `cdphdfsdocuments` | The property is registered; nothing populates it yet |
| `oracle`, `teradata` | The `CLASSIFICATIONS` column of the configured view, comma-separated |
| `mongodb` | The `classifications` field, an array or a comma-separated string |
| `tickets`, `hierarchy` | Not registered |

**The property is registered even where nothing populates it, and that is
deliberate.** A registered schema is append-only: a property cannot be PATCHed
onto a connection that has reached Ready, so the alternative to registering it
now is deleting the connection and every item in it the day somebody wants the
mapping. The cost of registering an unused property is one string per item;
the cost of not registering it is a rebuild.

That window is open for `oracle`, `teradata` and `mongodb` only because none of
them has been deployed. It closes the first time one reaches Ready.
