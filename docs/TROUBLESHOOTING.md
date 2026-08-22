# Troubleshooting — from a SQL row to an answer in Copilot

Almost every report about this connector arrives as one sentence: *the tickets
aren't in Copilot*. That sentence covers eight different failures on two
different machines, and the fastest way through it is to stop guessing and find
the last stage that provably works.

This document is the order to do that in. `docs/RUNBOOK.md` covers rotation and
routine operation; this one covers the case where something is wrong and you do
not yet know what.

---

## The pipeline

```
  ┌────────────┐   ┌───────────┐   ┌───────┐   ┌──────────────┐        ┌─────────────────┐
  │ SQL Server │ 1 │ Connector │ 2 │ Agent │ 3 │ Graph        │ ┌────4─→│ Microsoft Search│
  │ dbo.Tickets│──→│ gRPC      │──→│ GCA   │──→│ ingestion    │─┤       └─────────────────┘
  └────────────┘   └───────────┘   └───────┘   └──────────────┘ │       ┌─────────────────┐
                                                                └────5─→│ Copilot semantic│
                                                                        │ index           │
                                                                        └─────────────────┘
```

Two things about that drawing are worth stating explicitly, because the usual
version of it gets both wrong.

**The connector does not call Graph.** It has no Graph SDK, no
`GraphServiceClient` and no Graph permissions — that is a deliberate
architectural constraint, recorded in `docs/SECURITY.md` §1. Hop 2 is gRPC over
loopback; hop 3 is the agent's outbound HTTPS. They fail for entirely different
reasons, and collapsing them into one arrow hides the most common failure on the
whole path. When a diagram shows "connector → Graph", it is describing the
`SqlGraphPush` tool, not this connector.

**Search and Copilot are siblings, not a sequence.** Copilot's semantic index is
not fed by Microsoft Search; both are built from the same ingested content.
Search simply lights up first, which makes it a useful early probe. "It's in
search but not in Copilot" is therefore not "the next stage failed" — see
[stage 7](#stage-7--copilot-grounds-on-them).

---

## Where to start

| What you are seeing | Start at |
|---|---|
| The connection wizard says the data source did not respond | [1](#stage-1--sql-server-is-readable) |
| The wizard says the connector is unavailable on the specified port | [3](#stage-3--the-agent-can-reach-the-connector) |
| The connection published, but the item count stays at zero | [2](#stage-2--the-connector-is-serving), then [5](#stage-5--items-were-ingested) |
| Some tickets are indexed, others never appear | [1](#stage-1--sql-server-is-readable) — this is nearly always the watermark |
| Tickets stopped updating on a date you can name | [1](#stage-1--sql-server-is-readable) — check for a future timestamp |
| Deleted tickets are still findable | [5](#stage-5--items-were-ingested) |
| Items are in search but Copilot will not use them | [7](#stage-7--copilot-grounds-on-them) |
| You can find items, colleagues cannot | [0](#stage-0--configuration-is-valid), the ACL |
| It worked for months and stopped overnight | [0](#stage-0--configuration-is-valid) — a certificate or client secret expired |
| The connector log is completely empty | [3](#stage-3--the-agent-can-reach-the-connector) — calls are not arriving |

---

## The scripts, and where each one runs

Three machines are involved and no single one can see the whole path. That is
not an inconvenience to work around; it is the security boundary working.

| Script | Run it on | Covers |
|---|---|---|
| [`deploy/Test-ConnectorHost.ps1`](../deploy/Test-ConnectorHost.ps1) | The agent host | Stages 0–3: configuration, service, certificate, port, port map, SQL reachability, log health |
| [`deploy/Test-SqlSource.ps1`](../deploy/Test-SqlSource.ps1) | The agent host, ideally as the service account | Stage 1 in depth: grant, columns, index, watermark health, item sizes |
| [`deploy/Get-CrawlHistory.ps1`](../deploy/Get-CrawlHistory.ps1) | The agent host | Stages 2 and 5 from the log: what every crawl sent, and whether the watermark chain is intact |
| [`deploy/Verify-GraphConnection.ps1`](../deploy/Verify-GraphConnection.ps1) | A workstation with Graph access | Stages 4–6: connection, schema, a known item, search |

All four are read-only. None of them starts, stops or restarts anything.

The usual sequence:

```powershell
.\Test-ConnectorHost.ps1                              # on the host, first, always
.\Test-SqlSource.ps1 -TicketId 1001                   # if stage 1 looked doubtful
.\Get-CrawlHistory.ps1                                # if the host is healthy but items are missing
.\Verify-GraphConnection.ps1 -ItemId ticket1001 -SearchFor 'payment gateway'   # from a workstation
```

---

## Stage 0 — configuration is valid

**What has to be true.** `appsettings.json` has no placeholders left, names a
certificate (or credential target) that exists and has not expired, and grants at
least one ACL group.

**Prove it.**

```powershell
.\Test-ConnectorHost.ps1
```

**When it fails.** The service does not start at all, and the exit code says
which half is wrong:

| Exit code | Meaning |
|---|---|
| 2 | Configuration invalid. Every problem is listed at once in the log — fix them all, restart once. |
| 3 | The server failed to start: the certificate, its private key, or the port. |

The three that account for most of it:

- **Placeholders were never replaced.** `REPLACE-WITH-TENANT-GUID` and friends.
- **The service account cannot read the certificate's private key.** Most common
  after a host rebuild or a certificate re-import. The log says
  `PrivateKeyUnreadable` and names the process identity. `RUNBOOK.md` §4.1.
- **A credential expired.** A certificate warns daily for 30 days first
  (`Auth:ExpiryWarningDays`) and those warnings reach the Windows event log. An
  Entra **client secret warns about nothing at all** — its expiry is known only
  to Entra. If a working system stopped overnight and nobody changed anything,
  this is the first thing to check.

**The ACL is the quiet one.** An empty `Acl:GrantGroupObjectIds` refuses to
start, so that case is loud. The damaging case is an ACL that is *valid but
wrong*: everything ingests, the item count looks healthy, and only the people in
that group can find anything. If you can search successfully and a colleague
cannot, this is why — and note that changing it does not retrofit: existing items
keep their old ACL until they are re-crawled.

---

## Stage 1 — SQL Server is readable

**What has to be true.** The service account can open `Ops`, holds `SELECT` on
`dbo.Tickets`, and the table has the columns and timestamp discipline the crawl
depends on.

**Prove it.**

```powershell
.\Test-SqlSource.ps1 -TicketId 1001
```

Run it **as the service account** where you can — `psexec` or a scheduled task,
both shown in `RUNBOOK.md` §2a. Run it as yourself and a pass tells you the table
is fine while saying nothing about whether the connector can read it.

**When it fails.**

| Symptom | Cause |
|---|---|
| Wizard: "the data source did not respond within 20 seconds" | TCP is accepted and nothing comes back. Not credentials — those name the login. `RUNBOOK.md` §4.3a. |
| `Category: Authentication` in the log | SQL rejected the identity (18456, 4060, 18452). Confirm the service really runs as the account the `GRANT` names. |
| `Category: Transient` | Timeout, deadlock or reset. The connector returns `RetryDetails` and the platform re-drives the crawl. Repeated transients are a SQL health problem. |
| Validation fails naming `IsDeleted` | `sql/02-soft-delete.sql` was never run. Run it, or set `DataSource:SoftDeleteEnabled` to `false` and accept that deletions then only leave the index at a full crawl. |

### The watermark, which is what "some items are missing" almost always means

Incremental crawls resume from a composite `(LastModified, TicketId)` marker and
**only ever move forward**. Three things break that, none of which logs an error,
because from the connector's side nothing failed:

1. **A row timestamped in the future.** A local time written into a UTC column, a
   clock skew, a backdated import. That one row drags the watermark past every
   edit that has not been crawled yet, and those edits are never picked up. The
   symptom is "tickets stopped updating on the 14th" with a clean log.
   `Test-SqlSource.ps1` counts these explicitly.
2. **`LastModified` in local time.** The connector compares against UTC. In a
   UTC tenant in winter this is invisible; in summer it is one hour of changes
   quietly missing. The script detects the pattern by comparing the newest row
   against both server clocks.
3. **An application that updates a ticket without touching `LastModified`.**
   Invisible to the crawl by construction. Only a full crawl will pick it up.

To see what a crawl resuming from a given point *would* see, paste the watermark
straight out of a log line:

```powershell
.\Test-SqlSource.ps1 -Watermark 'v2|2026-08-13T09:35:12.4410000Z|1187'
```

Zero pending rows while rows are visibly changing is case 1 or 3.

---

## Stage 2 — the connector is serving

**What has to be true.** The service is running as the expected account and
listening on the configured port.

**Prove it.**

```powershell
.\Test-ConnectorHost.ps1
.\Get-CrawlHistory.ps1
```

**Reading the crawl history.** Each crawl logs three things: the watermark it
started from, the watermark it finished with, and a summary line of counts. The
summary is the answer to "what did the connector actually send":

```
Incremental crawl summary: items=42 deleted=3 skipped=0 truncated=1 contentBytes=918233
  sqlRoundTrips=1 durationMs=1180 errors={} watermarkIn=… watermarkOut=…
```

- `items=0` every time, with a source that is changing → stage 1, the watermark.
- `skipped=` non-zero → items over the platform cap. They were never sent.
- `truncated=` non-zero → content was cut to fit; the item is indexed, shortened.
- `errors={Transient=2}` → SQL health.

**The watermark chain.** In a healthy connector every crawl starts exactly where
the previous one ended, because the platform stores the checkpoint and hands it
back. `Get-CrawlHistory.ps1` checks that chain link by link. A break means the
checkpoint is not being persisted, so the connector is either re-reading the same
window forever or skipping one — and nothing else surfaces it, because from the
connector's side it simply used the marker it was given. A deliberate recrawl
from the admin centre looks identical, so check the recrawl history before
treating a break as a fault.

**What you will not find in this log.** No ticket titles, no bodies, no property
values, no connection strings, no secrets. That is a control, not an oversight
(`SECURITY.md` LOG-3). To see a row, use its item ID and query SQL.

**And no Graph errors, ever.** The connector does not call Graph, so a Graph
failure cannot appear here. If you are looking for one in this log, you are on
the wrong machine.

---

## Stage 3 — the agent can reach the connector

This hop is loopback gRPC, and it is the most common failure on the whole path.

**What has to be true.** `CustomConnectorPortMap.json` maps this connector's ID
to the port it is listening on, `GcaHostService` has been restarted *since* that
file was last edited, and — if `Connector:UseTls` is true — the agent trusts the
issuer of the loopback certificate.

**Prove it.**

```powershell
.\Test-ConnectorHost.ps1
```

**The symptom.** The connection fails in the admin centre and **this log has
nothing recent in it at all**. The calls never arrive, so there is nothing to
log. An empty connector log is itself the diagnosis.

**The four causes, in the order they occur:**

1. **The port map was edited and the agent was not restarted.** `GcaHostService`
   reads that file once, at startup. The connector is listening, the file on disk
   is correct, and the agent is still using the mapping it read hours ago.
   `Test-ConnectorHost.ps1` compares the file's write time against the agent's
   process start time and fails when the map is newer — it is the one check here
   that catches a configuration that *looks* right.
2. **Two connectors mapped to one port.** One of them silently never receives a
   call, and which one is not deterministic.
3. **Something else took the port.** The script compares the listening PID
   against the service PID.
4. **TLS.** The handshake failure appears on the **agent** side, not here. That
   asymmetry is the clue: connector log silent, agent log complaining. Confirm by
   setting `Connector:UseTls` to `false` briefly — if calls then arrive, the
   fault is the certificate, not the port map.

A related startup failure, distinct from the above: if the TLS certificate's
private key is not exportable, the server refuses to start at all with exit code
3, because gRPC Core needs PEM key material.

---

## Stage 4 — the connection and schema exist

From here on you are on a workstation with Graph access, not on the host.

**What has to be true.** The external connection exists, and its state is
`ready`.

**Prove it.**

```powershell
.\Verify-GraphConnection.ps1
```

**When it fails.**

- **State `draft`** — schema registration has not completed. Items cannot ingest
  until it is `ready`. This is a normal transient state for a few minutes after
  the wizard finishes and an outright fault an hour later.
- **404** — the connection was never created, or the ID differs from the one you
  passed.
- **403** — consent, not the connection. The `.All` scope was not granted.
- **An empty list from `GET /external/connections`** — read the trap below
  before concluding anything.

> ### The OwnedBy trap
>
> `ExternalConnection.ReadWrite.OwnedBy` is documented as the least-privileged
> scope, and it is the one most guidance tells you to use. But *OwnedBy* means
> "connections owned by the application making the call". In interactive
> PowerShell that application is **Microsoft Graph Command Line Tools**, which
> does not own this connection — the agent's registration does.
>
> Under `OwnedBy` the list comes back **empty, with no error**. That reads
> exactly like "the connection does not exist" for a connection that exists and
> is perfectly healthy. People have rebuilt working connections over this.
>
> Use `ExternalConnection.Read.All` (admin consent, read-only), or run as the
> owning app with `-AsOwningApp` and a certificate.

---

## Stage 5 — items were ingested

**What has to be true.** A known item can be fetched by ID.

**Prove it.**

```powershell
.\Verify-GraphConnection.ps1 -ItemId ticket1001
```

The item ID convention is `ticket` + `TicketId`, so ticket 1001 is `ticket1001`.

> ### There is no list-items API
>
> The `externalItem` resource documents Create, Get, Update, Delete and
> `addActivities`. There is no enumeration operation and no "List externalItems"
> page in the v1.0 reference.
>
> `Get-MgExternalConnectionItem` nonetheless advertises a List parameter set —
> `-ExternalConnectionId` on its own, with `-All`, `-Top` and `-Filter`. That
> parameter set is generated from the OData metadata, where `items` is a
> navigation collection. It is not evidence that the service implements
> enumeration. Do not build a verification step on it.
>
> Prove ingestion by fetching an ID you know the source contains, by running a
> search query, or by reading the item count in the admin centre.

**When the fetch 404s but the connection is healthy**, in order of likelihood:
no crawl has completed yet; the ID convention differs from what you assumed; or
the item was skipped at stage 2 for size — `Get-CrawlHistory.ps1` will have shown
`skipped=` non-zero.

**An item with an empty ACL** is invisible to every user. The script fails that
case explicitly rather than reporting the item as found.

### Deletions

Deleted tickets that are still findable are usually not a fault:

- **Incremental crawls only report a deletion when the row is soft-deleted** —
  `IsDeleted = 1` **and** `LastModified` touched, so the crawl sees the change.
  A hard `DELETE` is invisible to an incremental crawl by construction.
- **With `DataSource:SoftDeleteEnabled` false**, deletions reach the index only
  at the next periodic full crawl, which defaults to every 24 hours.
- **A tombstone purged before a crawl saw it** stays in the index until the next
  full crawl.

This connector is unusual in handling deletions incrementally at all: several of
Microsoft's own connectors detect them only on a full crawl.

---

## Stage 6 — search finds them

**What has to be true.** A query scoped to this connection returns the item.

**Prove it.**

```powershell
.\Verify-GraphConnection.ps1 -ItemId ticket1001 -SearchFor 'payment gateway'
```

Or, without any script: <https://www.microsoft365.com/search> → the **All** tab.

**Accepted and indexed are different states.** Stage 5 proves Graph accepted the
item. Indexing follows, and lags by minutes. Zero hits immediately after a first
crawl is expected; zero hits an hour later is not.

**Search is security-trimmed, and `/search/query` is delegated-only.** Both
matter for reading the result:

- Zero hits can mean "your account is not in the ACL group", not "not indexed".
  Test with an account that *is* in the group before concluding anything.
- There is no app-only form of this API, which is why the search check is skipped
  under `-AsOwningApp`.

---

## Stage 7 — Copilot grounds on them

**What has to be true.** The item carries the semantic labels Copilot needs, the
tenant is licensed, and the semantic index has caught up.

There is no API to assert this against. Ask Copilot a question whose answer only
exists in a ticket, and see whether it cites one.

**In search but not in Copilot** is a real and common state, and it is *not* a
broken pipeline stage — the two indexes are built independently from the same
content, so search leading Copilot is the normal ordering. Three causes, in the
order worth checking:

1. **Semantic processing has not caught up.** Allow hours after a first large
   crawl, not minutes.
2. **Missing semantic labels.** `Title` and `Url`, plus exactly one property
   flagged `IsContent`, are what make an item retrievable *by Copilot* rather
   than only by search. Confirm the wizard selected `body` as the content
   property — `Verify-GraphConnection.ps1` prints the registered schema.
3. **Licensing and quota.** Connector items consume tenant item quota, metered
   separately from Copilot seats. A crawl that exceeded quota stops ingesting
   and says so in the admin centre, not in the connector log.

---

## Traps that cost an afternoon

Each of these presents as something other than what it is.

| Trap | Presents as |
|---|---|
| `OwnedBy` scope in interactive PowerShell | "The connection doesn't exist" |
| `Get-MgExternalConnectionItem` with no item ID | "Zero items ingested" |
| Credential Manager is per account | "The secret is stored, but the service can't see it" |
| Port map edited, agent not restarted | "Connector unavailable on port" with correct config on disk |
| A single future timestamp | "Some tickets stopped updating" |
| Search is security-trimmed | "Nothing was indexed" |
| Incremental crawls and hard deletes | "Deleted tickets are still there" |
| ACL changes are not retrofitted | "The fix didn't work" — until the next full crawl |
| Client secrets warn about nothing | "It broke overnight and nobody changed anything" |
| The connector never calls Graph | Hours spent looking for a Graph error in the connector log |

---

## Before escalating

Collect these four. Together they cover every stage, and none of them contains
ticket data or a secret:

```powershell
# On the agent host
.\Test-ConnectorHost.ps1        > host.txt
.\Test-SqlSource.ps1            > sql.txt
.\Get-CrawlHistory.ps1 -Last 50 > crawls.txt

# On a workstation with Graph access
.\Verify-GraphConnection.ps1 -ItemId ticket1001 -SearchFor '<a phrase from that ticket>' > graph.txt
```

Add the connection's state and item count from the admin centre, and say which
stage was the **last one that passed**. That sentence is worth more than the four
files.
