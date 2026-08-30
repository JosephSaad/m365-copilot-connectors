---
title: How the items actually appear
description: Result types, verticals and activities — what Microsoft Search renders from, what Copilot renders from, why they are not the same thing, which of them has a Graph API and which needs a human with an admin role, and the honest answer to whether a timesheet database can produce activity signals at all.
---

# How the items actually appear

Everything else in `docs/` is about getting items into the index correctly. This
document is about the half after that: whether anybody who finds them concludes
they are worth using.

That half fails silently. A connector can be entirely correct — every ACL
mirrored, every deletion propagated, every property registered — and the result
on the page is a line of blue text and a grey snippet, indistinguishable from a
stub. Nothing errors. Nothing is logged. The user's conclusion is "the connector
does not work", and they are not wrong about the thing they can see.

| | |
|---|---|
| **What this covers** | Display templates, admin-centre result types, search verticals, semantic labels, and external item activities |
| **What it is for** | `consultingwork` on the reference rig, but nothing here is specific to it |
| **Scripts** | [`deploy/Get-SearchSurfacing.ps1`](../deploy/Get-SearchSurfacing.ps1) reports; [`deploy/Set-SearchResultTypes.ps1`](../deploy/Set-SearchResultTypes.ps1) configures |
| **What you cannot do from here** | Verticals and admin-centre result types. There is no API. Section 4 |
| **The finding to read first** | Section 6, then section 7 |

---

## 1. Two renderers, and they do not share a mechanism

The single most expensive misunderstanding in this area is that "making the
results look right" is one job. It is two, they use different inputs, and doing
one of them well buys nothing at all in the other.

**Microsoft Search** — the results page on the SharePoint start page, on
Office.com, on Bing at Work — renders a connector result from an **Adaptive
Card layout**: a display template, bound to the item's retrievable properties.
Get the template right and the result carries an icon, a headline, a couple of
facts and a highlighted snippet. Register no template and the platform falls
back to a default layout, which is a title and a snippet.

**Microsoft 365 Copilot** does not use Adaptive Card layouts at all. Microsoft
states this plainly in the connectors FAQ: Copilot Search does not support
Adaptive Card layouts, and generates its result rendering from **semantic
labels** instead ([connectors
FAQ](https://learn.microsoft.com/en-us/microsoft-365/copilot/connectors/frequently-asked-questions)).
So the entire investment in section 3 is invisible to Copilot, and the entire
investment in section 5 is what Copilot actually reads.

The practical consequence is uncomfortable and worth stating before anyone
starts work:

> A beautifully designed display template does nothing for Copilot. A schema
> with the right labels does nothing extra for the Search results page beyond
> the default layout. **You need both, they are different tasks, and they have
> different owners.**

And a third distinction sits underneath both. Microsoft documents that connector
content is *semantically* indexed on two things — the item's title and its
`content` — and that semantic labels are used for filtering rather than for
semantic indexing ([Copilot connectors
overview](https://learn.microsoft.com/en-us/microsoft-365/copilot/extensibility/overview-copilot-connector)).
So there are three distinct questions, and they are answered in three different
places:

| Question | Answered by | Where it is configured |
|---|---|---|
| Can Copilot *retrieve* this item for a prompt? | `title`, the `content` property, and which properties are `searchable` | the connector's schema and its item payload — `src/**` |
| Once retrieved, does it *render* as something a person recognises? | semantic labels, principally `title`, `url`, `iconUrl` | the connector's schema — `src/**` |
| Does the Microsoft Search results page render it as something a person recognises? | Adaptive Card display templates | the connection, via Graph — `deploy/Set-SearchResultTypes.ps1` |

---

## 2. What the connector already gets right, and what it does not

Observed on the reference rig on **2026-08-30**, against connection
`consultingwork`, by [`Get-SearchSurfacing.ps1`](../deploy/Get-SearchSurfacing.ps1).
This is tenant state, not documentation:

| Fact | Value |
|---|---|
| `state` | `ready` |
| `ingestedItemsCount` | 111,900 |
| `searchSettings` | **null** — no display templates |
| `activitySettings` | **null** — no `urlToItemResolvers` |
| `contentCategory` | `uncategorized` |
| `enabledContentExperiences` (beta) | `search` |
| Schema | 26 properties, 26 retrievable, 10 searchable |
| Labels present | `title`, `url`, `lastModifiedDateTime`, `containerName`, `containerUrl` |
| Labels absent | **`iconUrl`**, `lastModifiedBy`, `createdBy`, `createdDateTime`, `fileName`, `fileExtension` |

Two of those rows need reading carefully rather than skimming.

**`enabledContentExperiences: search` is not a Copilot off switch.** It reads
like one. It is not. `search` is the only value Microsoft publishes for that
property on the beta `externalConnection` resource
([beta externalConnection](https://learn.microsoft.com/en-us/graph/api/resources/externalconnectors-externalconnection?view=graph-rest-beta)),
and the property does not exist at all in v1.0. Nobody should spend an afternoon
trying to add `copilot` to it.

**`unknownFutureValue` on a label is usually a missing request header, not a
broken schema.** A plain `GET /schema` against this connection reports
`containerName` and `containerUrl` as carrying the label `unknownFutureValue`,
which looks exactly like a schema registered with garbage. It is not. The label
enum is evolvable, and the values are only serialised in full when the request
carries `Prefer: include-unknown-enum-members`
([property resource](https://learn.microsoft.com/en-us/graph/api/resources/externalconnectors-property)).
With the header, both read back correctly. `Get-SearchSurfacing.ps1` always
sends it, and distinguishes the two cases in its output; anything else reporting
`unknownFutureValue` should be suspected of omitting the header before the
schema is suspected of anything.

---

## 3. Display templates: the half that has an API

A **display template** is an object on the connection itself, under
`searchSettings.searchResultTemplates`. Four fields
([displayTemplate](https://learn.microsoft.com/en-us/graph/api/resources/externalconnectors-displaytemplate)):

| Field | Constraint that bites |
|---|---|
| `id` | at most **16 characters**, alphanumeric only |
| `layout` | an Adaptive Card, JSON-serialised |
| `priority` | must be positive; **lower is evaluated first**; gaps are allowed |
| `rules` | optional; a collection of `propertyRule`. **Omit it and the template matches everything** |

And one constraint that shapes the whole design: **a maximum of two templates
per connection**
([searchSettings](https://learn.microsoft.com/en-us/graph/api/resources/externalconnectors-searchsettings)).

This source has three item types. Two templates. That is not a limitation to
work around, it is a decision to make, and `Set-SearchResultTypes.ps1` makes it
like this:

```
priority 1   rules: itemType equals TimeEntry    the time-entry card
priority 2   no rules                            everything else
```

Time entries are the odd shape — a person, a date, a number of hours. Customers
and engagements are both container-like things with a manager, a status and a
roll-up, and one card serves them with `$when` guards on the fields that differ.

The second template has **no rules on purpose**. A template without rules is a
catch-all, and without one, any item matching none of the rules falls back to
the platform default silently. That matters less for today's three item types
than for the fourth one somebody adds in eighteen months: with a catch-all it
renders acceptably from day one; without, it renders as a stub and nobody
notices for a quarter.

### Rules

A `propertyRule` is `property` + `operation` + `values`, joined by
`valuesJoinedBy` (`or` / `and`). The operations are `null`, `equals`,
`notEquals`, `contains`, `notContains`, `lessThan`, `greaterThan`, `startsWith`
([propertyRule](https://learn.microsoft.com/en-us/graph/api/resources/externalconnectors-propertyrule)).
The property is one from the item schema — here, `itemType`, which this
connector registers as queryable, retrievable and refinable precisely because it
is the thing you facet and branch on rather than type.

### Binding, and the one rule that silently ruins a card

The layout binds with `${propertyName}`, against the schema's own property
names. **Every bound property must be `isRetrievable`.** Microsoft is explicit
that retrievable properties are the ones available to a display template
([manage
schema](https://learn.microsoft.com/en-us/graph/connecting-external-content-manage-schema)),
and the failure mode when a property is not retrievable is not an error — the
card renders the literal text `${hours}` on the results page. `Set-SearchResultTypes.ps1`
therefore reads the live schema and refuses to send a payload binding anything
that is missing or not retrievable. All 26 of this connector's properties happen
to be retrievable, so the check passes today; it exists for the day one is added
that is not.

`${ResultSnippet}` is the exception: it is not a schema property but a system
placeholder for the dynamic snippet generated from the item's `content`, with
the query terms highlighted
([customise
layout](https://learn.microsoft.com/en-us/microsoftsearch/customize-results-layout)).
The same page warns not to apply markdown to it, because markdown breaks the
highlighting.

The rest of Microsoft's published layout rules, all of which the shipped
templates obey:

- only a subset of Adaptive Card elements renders — `TextBlock`,
  `RichTextBlock`, `Image`, `ColumnSet`, `ImageSet`, `FactSet`, `Container`
- no `px` values in element properties
- guard optional fields with `$when`, and compare like with like — do not test a
  number against a string
- `wrap` and `maxLines` on anything holding source text, or one long title
  pushes the card past the height at which the results page crops it
- give every card an icon. Microsoft's reasoning is scanning behaviour: results
  without one break the eye's pattern down the page. The shipped templates
  default to Microsoft's own generic connector icon from the search CDN, which
  is a **placeholder** — point `-IconUrl` at a customer-hosted square PNG of at
  least 32×32 that is legible on a dark background before anybody but a tester
  sees the results
- `StringCollection` properties need `${join(name, ',')}`. None of this schema's
  properties are collections, so it does not arise here — but it will the first
  time one is added

### Registering them

```powershell
# dry run: validates against the live schema, prints the payload, writes nothing
.\Set-SearchResultTypes.ps1 -ConfigPath C:\Connectors\SqlHierarchyPush\appsettings.json

# apply, after backing up whatever searchSettings holds now
.\Set-SearchResultTypes.ps1 -ConfigPath ... -Apply -IconUrl https://portal.consultco.com/static/icon32.png

# put it back exactly as it was
.\Set-SearchResultTypes.ps1 -ConfigPath ... -Revert
```

**One caveat, stated because it is the weakest link in this document.** The
reference page for *Update externalConnection* lists only `configuration`,
`description` and `name` as updatable
([update externalConnection](https://learn.microsoft.com/en-us/graph/api/externalconnectors-externalconnection-update)).
It does not list `searchSettings`. That table is demonstrably incomplete: it also
omits `activitySettings`, and Microsoft's own connection-settings page
demonstrates `activitySettings` being set by exactly this `PATCH`
([manage
connections](https://learn.microsoft.com/en-us/graph/connecting-external-content-manage-connections)).
Both are connection settings described in the same section of the same page. So
`PATCH` is the documented-by-example route and there is no other one short of
deleting and recreating the connection — but **this was not executed against the
tenant** (section 10), and the first `-Apply` run is where it is proven. If that
run returns `400` naming `searchSettings`, the fallback is that display
templates can only be supplied at `POST` time, and the connection has to be
recreated to get them — which for 111,900 items is a full recrawl, not a
five-minute job. Budget for finding out.

A `503` on this call means something specific and worth recognising: Microsoft
documents `503` as the response to a **broken Adaptive Card** in a result layout
([known
issues](https://learn.microsoft.com/en-us/graph/known-issues)). Validate the
card in the Adaptive Cards designer before retrying, and do not simply re-send.

---

## 4. Verticals, and the part with no API at all

A **search vertical** is a tab on the search results page. `All`, `Files`,
`People` are the defaults; a custom one shows results from one or more connector
connections, optionally narrowed by a limited KQL query
([manage
verticals](https://learn.microsoft.com/en-us/microsoftsearch/manage-verticals)).

Four things about them decide how much work this actually is.

**They have no Graph API.** Not in v1.0, not in beta. Verticals are created in
the Microsoft 365 admin centre by a wizard, at organisation level or at
SharePoint site level. So are **admin-centre result types**, which are a
*different object* from the connection-level display templates in section 3 —
same idea, same Adaptive Card language, different owner, different lifecycle,
and configured at
`admin.microsoft.com/Adminportal/Home#/MicrosoftSearch/resulttypes`. Nothing in
this repository can create either. `Get-SearchSurfacing.ps1` reports that it
cannot see them and prints the URLs, because a report that silently omits the
half it cannot reach is worse than no report.

**A vertical is not required for items to appear.** This is the question worth
being precise about, because the roadmap item implies otherwise. Connector
results appear **inline in the `All` vertical by default**, merged with
SharePoint and OneDrive results and ranked against them, with no custom vertical
involved at all
([connectors in All
vertical](https://learn.microsoft.com/en-us/microsoftsearch/connectors-in-all-vertical)).
A vertical buys a *dedicated tab*, and the older result-cluster experience
required one; inline results do not. What the `All` vertical does require of the
schema is one property mapped to the `title` label, and Microsoft recommends
`lastModifiedDateTime` so the Last Modified filter works — both of which this
connector already has.

**A vertical without a result type shows nothing.** This is the trap. Microsoft
states that when a vertical is used with connector content, a result type with
mappings must also be created, or the vertical displays no results
([manage result
types](https://learn.microsoft.com/en-us/microsoftsearch/manage-result-types)),
and the vertical troubleshooting table lists exactly this as the cause of a
"Something went wrong" message. Creating half of the pair produces a tab that
looks broken rather than a tab that looks plain.

**One connection, one vertical.** A connection cannot be a content source under
more than one vertical. Worth knowing before designing three of them.

Against Copilot specifically: verticals are irrelevant. There is one
admin-centre setting that is not, and it is the first item on Microsoft's own
list of reasons custom connector results fail to appear — the connection must be
ticked under **Verticals → All → Manage connection results**, with *Show results
inline* selected. Microsoft's guidance says this enables the connection for
Microsoft Search **and Copilot**
([connector
experiences](https://learn.microsoft.com/en-us/graph/connecting-external-content-experiences)),
and the FAQ notes it is on by default for prebuilt connectors but must be
enabled for custom ones. Somebody with the Search Administrator role has to look
at it. No API will tell you.

---

## 5. What Copilot needs that Microsoft Search does not

Microsoft's list, in its own order
([connector
experiences](https://learn.microsoft.com/en-us/graph/connecting-external-content-experiences)),
scored against this connector:

| Requirement | State here |
|---|---|
| The `iconUrl`, `title` and `url` labels must be applied for content to surface in Copilot | **`iconUrl` is missing.** Section 6 |
| Only `title` can currently be used in prompts; other labels are for later | `title` present |
| `content` ingested as text — Copilot performs better on content-rich items | present; `Content` is built by the SQL views |
| `searchable` on the properties prompts should match against — Microsoft calls this the most important attribute for Copilot | 10 of 26, and they are the right 10: the names, codes, managers and the flattened `hierarchyPath` |
| a `urlToItemResolver` in `activitySettings` | **absent.** Section 7 |
| user activities on items | **absent, and mostly not obtainable.** Section 7 |
| a meaningful connection `description` | present, adequate; could say more about *when* people reach for this content |
| `contentCategory` set — it signals the nature of the content to the ranker | **`uncategorized`.** One `PATCH`, no reingestion, and the plausible values here are `crm` or `taskManagement` |

`contentCategory` deserves a sentence of its own because it is the cheapest item
on the list. Microsoft describes it as helping Graph optimise relevance, ranking
and semantic understanding, and specifically calls out better query
interpretation and improved Copilot experiences
([externalConnection](https://learn.microsoft.com/en-us/graph/api/resources/externalconnectors-externalconnection)).
It is a single enum on the connection. It requires no schema change, no
recrawl, and the same `ExternalConnection.ReadWrite.OwnedBy` the push app
already holds. The only real question is which value: customers and engagements
argue for `crm`, timesheet rows argue for `taskManagement`, and one connection
gets one value. `crm` is the better answer here — the customer and engagement
records are what a prompt will name, and the time entries are evidence hanging
off them — but it is a judgement, and it should be made by whoever owns the
adoption story rather than defaulted.

---

## 6. The finding: `iconUrl`

Microsoft's wording is that the `iconUrl`, `title` and `url` labels **must** be
applied for content to surface in Copilot
([connector
experiences](https://learn.microsoft.com/en-us/graph/connecting-external-content-experiences)).
This connector applies two of the three. `Get-SearchSurfacing.ps1` treats the
gap as blocking and exits `1` on it, which is why it does.

How literally to take "must" is genuinely unclear — this connection is indexed
and searchable today, and Microsoft's own admin-centre *schema recommendations*
feature checks for four labels and `iconUrl` is not among them
([manage
connector](https://learn.microsoft.com/en-us/microsoft-365/copilot/connectors/manage-connector)).
The honest reading is that it is a documented requirement that is probably
enforced softly, which is the worst kind to be on the wrong side of: it does not
fail, it just quietly ranks and renders worse, and no diagnostic will ever
mention it.

**The fix is not in this document's files.** It is a schema change, in
`src/SqlHierarchyPush/HierarchyPushConnector.cs`, and it belongs to whoever owns
`src/**`:

```
PushSchema.Prop("iconUrl", PropertyType.String, retrievable: true, label: Label.IconUrl)
```

with a corresponding column in `dbo.vwExternalItems` — for a source with three
item types, three static URLs pointing at three small square PNGs is entirely
sufficient, and the same asset the display templates use.

Two things make this cheaper than it looks, and one makes it dearer.

Cheaper: Microsoft documents that adding a property to a registered schema is
supported, and that adding or removing a semantic label is supported
([manage
schema](https://learn.microsoft.com/en-us/graph/connecting-external-content-manage-schema),
"Schema update capabilities"). This is **not** a delete-and-recreate. Note that
`src/PushCore/PushSchema.cs` carries a comment asserting the schema is
append-only and that labels cannot be changed afterwards — that is stricter than
what Microsoft publishes today, and it is the kind of caution that was probably
right when it was written. Worth reconciling; do not treat the comment as the
authority without checking.

Dearer: the same page recommends reingesting after a schema change so items
match the new schema. On 111,900 items that is one full crawl, so the change
wants to be batched with any other schema work rather than done alone.

---

## 7. Activities: the API is real, the data mostly is not

This is the part of the roadmap item that does not survive contact with the
source, and saying so is more useful than designing for data that does not
exist.

### The API

```http
POST /external/connections/{connectionId}/items/{itemId}/addActivities
Content-Type: application/json

{
  "activities": [
    {
      "@odata.type": "#microsoft.graph.externalConnectors.externalActivity",
      "type": "created",
      "startDateTime": "2026-08-14T09:12:00Z",
      "performedBy": { "type": "user", "id": "1f0c997e-99f7-43f1-8cca-086f8d42be8d" }
    }
  ]
}
```

- Activity types: `viewed`, `modified`, `created`, `commented`
  ([externalActivity](https://learn.microsoft.com/en-us/graph/api/resources/externalconnectors-externalactivity)).
- Application permission: **`ExternalItem.ReadWrite.OwnedBy`**
  ([addActivities](https://learn.microsoft.com/en-us/graph/api/externalconnectors-externalitem-addactivities))
  — which this app registration already holds. No new consent.
- `200 OK`, or **`207 Multi-Status`** when only some activities were processed,
  with a per-activity `error` field. A partial success is a normal outcome and
  has to be inspected rather than assumed.
- Effect, per Microsoft: items with more activities are boosted in importance.

### Where it would go in the push path

`src/PushCore` writes items through a writer pool; a `PushItem` becomes a
`PUT .../items/{id}`. Activities would be a second call per item, after the
successful `PUT` — the item must exist first — which means:

- **The call volume of a full crawl roughly doubles.** 111,900 items becomes
  111,900 puts plus up to 111,900 activity posts, against the same tenant
  throttling budget the existing retry and `Retry-After` handling manages. The
  100-fold test measured the current shape; it would need re-measuring.
- It has to be idempotent across crawls, and **it is not**. `addActivities`
  *appends*; there is no documented delete for an `externalActivity`. A full
  crawl that replays the same source rows would add the same activity again,
  every time. Anything built here needs a watermark in the source view so a
  given activity is posted exactly once, which is a second checkpoint with the
  same ordering and tie-breaking problems as the item checkpoint — see
  `HierarchyPushConnector.BuildIncrementalQuery`. That is not a small feature.
- The incremental path would carry it too, or the signal decays to whatever the
  last full crawl posted.

### Whether the source can populate it — the honest answer

`performedBy` is an **identity**, meaning an Entra object id, not a name and not
an email. Working through what the timesheet source actually holds:

| Activity | Could it be populated? |
|---|---|
| `viewed` | **No.** The source is a timesheet database. It has no access log, no read audit, nothing that records who looked at a customer record. This is the activity type that most directly reflects human interest, and there is nothing to build it from |
| `commented` | **No.** There are no comments in the schema |
| `created` | **Partly, and weakly.** A `TimeEntry` has `ConsultantEmail` and a `WorkDate`, so "created by this consultant on that date" is derivable — after resolving the email to an Entra object id, which is new plumbing and a cache, though `Directory.Read.All` is already granted. `Customer` and `Engagement` carry only *names* (`AccountManager`, `ProjectManager`), which are not resolvable to identities without guessing |
| `modified` | **Same as `created`, and no better.** The source's `EffectiveLastModified` is a trigger-maintained timestamp with no actor attached |

So roughly a third of the corpus could carry one derivable activity each,
back-dated to when the work was logged.

**And that is worth close to nothing.** The value of an activity signal to a
ranker is that it reflects *recent human attention*. Backfilled `created`
activities dated to the work date carry no attention at all — they are a
monotone restatement of `lastModified`, which is already registered with the
`lastModifiedDateTime` label and already used for ranking and for the Last
Modified filter. The connector would take on a second checkpoint, an identity
resolution cache, a doubled call volume and a non-idempotent write, to tell the
ranker something it already knows.

**Conclusion: the roadmap item overstates what is achievable here.** "External
item activities feed relevance ranking" is true in general and not actionable
against this source. The recommendation is to decline `addActivities` with this
reason recorded, and to revisit only if the `portal.consultco.com` application
that owns those URLs ever exposes an access log — at which point the design
above is the design, and the watermarking problem is the hard part of it.

### What *is* achievable, and is being missed

There is a second activity mechanism that needs no source data at all, and this
connection has not configured it.

`activitySettings.urlToItemResolvers` maps a **URL back to an `externalItem`**.
When somebody pastes or shares a source URL inside Microsoft 365, the platform
uses those resolvers to recognise which indexed item it names, and can then
associate the interaction with that item; Microsoft describes the signals as
being captured through the Microsoft 365 Copilot browser extension where that is
deployed
([manage
connections](https://learn.microsoft.com/en-us/graph/connecting-external-content-manage-connections)).
Microsoft's Copilot guidance lists it alongside activities, with the note that
content shared with a user is more likely to be shown to them.

This source is unusually well suited to it, because its URLs are real, stable
and per-item, and its item ids are trivially derivable from them:

| Item type | URL | Item id |
|---|---|---|
| `Customer` | `https://portal.consultco.com/customers/{n}` | `cust{n}` |
| `Engagement` | `https://portal.consultco.com/engagements/{n}` | `eng{n}` |
| `TimeEntry` | `https://portal.consultco.com/time/{n}` | `time{n}` |

Which maps onto the resolver shape exactly — up to eight `itemIdResolver`
entries, each with a `baseUrls` list, a named-capture `urlPattern` and an
`itemId` template referencing the capture, evaluated in ascending `priority`
until one matches:

```json
{
  "@odata.type": "#microsoft.graph.externalConnectors.activitySettings",
  "urlToItemResolvers": [
    {
      "@odata.type": "#microsoft.graph.externalConnectors.itemIdResolver",
      "itemId": "cust{customerId}",
      "priority": 1,
      "urlMatchInfo": {
        "@odata.type": "microsoft.graph.externalConnectors.urlMatchInfo",
        "baseUrls": [ "https://portal.consultco.com" ],
        "urlPattern": "/customers/(?<customerId>[0-9]+)"
      }
    }
  ]
}
```

It is a connection-level `PATCH` with the permission the app already has, no
schema change, no recrawl, and no source data. It is not implemented in
`Set-SearchResultTypes.ps1` — that script does one thing — but it is the same
call, and it is the recommendation this section ends on. It is also the one
place in this whole area where "activities" is achievable for a source that
knows nothing about who read what.

---

## 8. Who can do what

The permission question, because it decides who has to be in the room.

| Action | Permission or role | Held by the push app? |
|---|---|---|
| `PATCH` `searchSettings` (display templates) | `ExternalConnection.ReadWrite.OwnedBy` | **Yes** |
| `PATCH` `activitySettings` (URL resolvers) | `ExternalConnection.ReadWrite.OwnedBy` | **Yes** |
| `PATCH` `contentCategory` | `ExternalConnection.ReadWrite.OwnedBy` | **Yes** |
| `POST` `addActivities` | `ExternalItem.ReadWrite.OwnedBy` | **Yes** |
| Read the schema and the connection | `ExternalConnection.ReadWrite.OwnedBy` | **Yes** |
| Create a search **vertical** | Search Administrator (or Global Administrator), in the admin centre | **No such application permission exists** |
| Create an admin-centre **result type** | Search Administrator, in the admin centre | **No such application permission exists** |
| Tick a connection for inline results in `All` | Search Administrator, in the admin centre | **No such application permission exists** |
| `POST /search/query` to prove retrieval | delegated only; app-only is not supported | n/a — use `Verify-GraphConnection.ps1` |

So the headline answer to "does registering result types need a permission this
app does not hold?" is **no for the half with an API, and moot for the other
half** — the admin-centre objects need a *human* role that no application
registration can be granted at all. That changes who runs what: the scripts in
`deploy/` can be run by whoever runs the push tool, on the schedule they choose,
with no admin involvement; the admin-centre work needs a named person with the
Search Administrator role and a change window, and it cannot be automated,
scripted, tested in a pipeline, or verified afterwards by any API call.

Note also the OwnedBy trap, which applies to everything in the first block:
`OwnedBy` means *owned by this application registration*. Run these scripts with
a different app's credentials and the connection is not forbidden, it is
**absent** — a `404`, or an empty list. An empty list is never evidence that a
connection does not exist. This is documented at greater length in
`deploy/Verify-GraphConnection.ps1`.

---

## 9. Order of operations

Dependencies are real here: doing these out of order means doing some of them
twice.

1. **Schema first, because it is the only one that needs a recrawl.** Add the
   `iconUrl` property and label (section 6), together with any other label work
   — `lastModifiedBy` and `createdBy` are the two Microsoft ranks highest among
   those still missing, and both are derivable from the source with a view
   change. Re-register the schema, then run a full crawl.
2. **`contentCategory`.** One `PATCH`, no recrawl. Decide `crm` vs
   `taskManagement` first.
3. **Display templates.** `Set-SearchResultTypes.ps1`, dry run, read the
   payload, then `-Apply` with a real `-IconUrl`.
4. **`urlToItemResolvers`** (section 7). Same call, same permission.
5. **Admin centre: inline results.** Verticals → All → Manage connection
   results → *Show results inline*, connection ticked. This is the one that most
   often explains "the connector does not work".
6. **Admin centre: a vertical and a result type**, together or not at all, and
   only if a dedicated tab is actually wanted.
7. **Verify.** `Get-SearchSurfacing.ps1` for the configuration,
   `Verify-GraphConnection.ps1 -SearchFor` for retrieval, and then a human
   looking at the results page — because that is the thing none of the above
   measures.

Caching is the reason step 7 is not immediate. A result type takes minutes to
appear; a vertical takes hours. Appending `cacheClear=true` to the SharePoint or
Office search URL shortens the wait, and the change may still take up to 30
minutes
([manage
verticals](https://learn.microsoft.com/en-us/microsoftsearch/manage-verticals)).
Do not conclude the payload was wrong five minutes in.

---

## 10. What was and was not done

Stated explicitly, because the distinction between "scripted" and "proven"
matters more in this area than in most.

**Done, against the live tenant, read-only:** the connection was listed and
read, the schema was read with and without `Prefer: include-unknown-enum-members`,
the beta connection was read for the item count. Everything in section 2 is
observed. Nothing was created, modified or deleted.

**Not done:** `Set-SearchResultTypes.ps1 -Apply` was **not executed**. The
tenant's `searchSettings` is still `null`. The script was dry-run on both
PowerShell hosts, its payload validated against the live schema, and its revert
path exercised against a missing backup — but the `PATCH` itself has not been
sent, so the assumption in section 3 that `PATCH` accepts `searchSettings`
remains an inference from Microsoft's documentation rather than an observation.
That is the one open question in this document and the first `-Apply` closes it.

**Not attempted:** anything in the Microsoft 365 admin centre. There is no API,
and no credential in this repository could reach it if there were.

---

## Sources

All Microsoft Learn, read 2026-08-30.

- [displayTemplate](https://learn.microsoft.com/en-us/graph/api/resources/externalconnectors-displaytemplate) — id, layout, priority, rules
- [searchSettings](https://learn.microsoft.com/en-us/graph/api/resources/externalconnectors-searchsettings) — the maximum of two templates
- [propertyRule](https://learn.microsoft.com/en-us/graph/api/resources/externalconnectors-propertyrule) — the operations
- [externalConnection](https://learn.microsoft.com/en-us/graph/api/resources/externalconnectors-externalconnection) and [beta](https://learn.microsoft.com/en-us/graph/api/resources/externalconnectors-externalconnection?view=graph-rest-beta) — `contentCategory`, `enabledContentExperiences`, `ingestedItemsCount`
- [Update externalConnection](https://learn.microsoft.com/en-us/graph/api/externalconnectors-externalconnection-update) — the incomplete updatable-properties table
- [Create, update, and delete connections](https://learn.microsoft.com/en-us/graph/connecting-external-content-manage-connections) — connection settings, `urlToItemResolvers`, the `PATCH` example
- [property](https://learn.microsoft.com/en-us/graph/api/resources/externalconnectors-property) — attributes, the label enum, the `Prefer` header
- [Register and manage schema](https://learn.microsoft.com/en-us/graph/connecting-external-content-manage-schema) — labels, retrievable, relevance order, schema update capabilities
- [Copilot connector experiences](https://learn.microsoft.com/en-us/graph/connecting-external-content-experiences) — the `iconUrl`/`title`/`url` requirement, the Copilot checklist
- [Copilot connectors FAQ](https://learn.microsoft.com/en-us/microsoft-365/copilot/connectors/frequently-asked-questions) — Copilot Search does not use Adaptive Card layouts; why results do not appear
- [Copilot connectors overview](https://learn.microsoft.com/en-us/microsoft-365/copilot/extensibility/overview-copilot-connector) — semantic indexing on title and content
- [Manage search verticals](https://learn.microsoft.com/en-us/microsoftsearch/manage-verticals) — admin centre only, one connection one vertical, caching
- [Manage result types](https://learn.microsoft.com/en-us/microsoftsearch/manage-result-types) — a vertical needs a result type
- [Manage connector results in All vertical](https://learn.microsoft.com/en-us/microsoftsearch/connectors-in-all-vertical) — inline by default, the `title` label requirement
- [Create a layout](https://learn.microsoft.com/en-us/microsoftsearch/customize-results-layout) — `ResultSnippet`, the do and don't list
- [externalActivity](https://learn.microsoft.com/en-us/graph/api/resources/externalconnectors-externalactivity) and [addActivities](https://learn.microsoft.com/en-us/graph/api/externalconnectors-externalitem-addactivities) — types, permission, `207`
- [Manage connector connections](https://learn.microsoft.com/en-us/microsoft-365/copilot/connectors/manage-connector) — the four labels the admin centre checks for
- [Known issues](https://learn.microsoft.com/en-us/graph/known-issues) — `503` on a broken Adaptive Card
