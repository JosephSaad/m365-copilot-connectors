# Troubleshooting — the direct push path (`SqlGraphPush`)

The other pipeline has an agent between the connector and Microsoft Graph. This
one does not: `SqlGraphPush` reads SQL and calls Graph itself. That removes four
failure modes and introduces five, so this is a separate document rather than a
section in [`TROUBLESHOOTING.md`](TROUBLESHOOTING.md).

```
  ┌────────────┐   ┌───────────────┐   ┌────────────┐        ┌──────────────────┐
  │ SQL Server │ 1 │ SqlGraphPush  │ 2 │ Graph      │ ┌──3───→│ Microsoft Search │
  │ dbo.Tickets│──→│ workstation / │──→│ ingestion  │─┤       └──────────────────┘
  └────────────┘   │ jump box      │   └────────────┘ │       ┌──────────────────┐
                   └───────┬───────┘                  └──4───→│ Copilot semantic │
                           │                                  │ index            │
                      ┌────┴─────┐                            └──────────────────┘
                      │ Key Vault│  (only when SqlAuthMode is SqlLogin)
                      └──────────┘
```

**One machine does everything.** It needs a route to SQL Server *and* outbound
HTTPS to Graph *and*, sometimes, to Key Vault — which is why the tool so often
fails on a jump box that has two of the three.

**Graph errors land in this tool's own log**, unlike the agent path where the
connector never calls Graph at all. If you are reading `SqlGraphPush.log`, a
403 in it is yours to fix.

---

## What this path is and is not

`SqlGraphPush` is a **seeding and repair** tool. It proves a tenant, an app
registration and Copilot grounding before anyone installs an agent, and it
re-seeds a connection that has gone wrong. Read the README's framing as load
bearing, not as modesty.

It is **not** a synchroniser, and four absences follow from that. None is a
defect; all four are things people assume are there:

| It does not | Consequence |
|---|---|
| Crawl incrementally | Every run reads and re-PUTs the whole table |
| Delete anything, ever | See [the deletion problem](#the-deletion-problem) — this is the big one |
| Run on a schedule | Nothing happens unless a person or a scheduled task runs it |
| Crawl on a watermark | Nothing resumes; a failed run starts again from the top |

If you find yourself running it on a timer to keep an index fresh, you have
outgrown it. That is what the agent-hosted connector is for, and
`docs/agent-bypass-tradeoffs.pptx` lists what else you gain by moving.

---

## Where to start

| What you are seeing | Start at |
|---|---|
| `FATAL:` before any log line, exit code 2 | [0](#stage-0--configuration) |
| Exit code 3, "Could not build the Entra credential" | [1](#stage-1--credential-and-consent) |
| A 403 from any Graph call | [1](#stage-1--credential-and-consent), then [3](#stage-3--the-connection-and-who-owns-it) |
| `AADSTS` anything | [1](#stage-1--credential-and-consent) |
| It sat at "Connection state draft" and then timed out | [4](#stage-4--schema-registration) |
| Exit code 4 partway through, some items written | [5](#stage-5--items-written) |
| Deleted tickets are still in Copilot | [the deletion problem](#the-deletion-problem) |
| Item count is lower than the row count | [5](#stage-5--items-written) — throttling, or the ID range |
| Items exist but nobody can find them | [5](#stage-5--items-written), the ACL |
| In search but not in Copilot | [7](TROUBLESHOOTING.md#stage-7--copilot-grounds-on-them) — same as the agent path |

---

## Exit codes

`SqlGraphPush` is a console tool, so the exit code is the fastest diagnosis
available and it costs nothing to read.

| Code | Meaning | Where to look |
|---|---|---|
| 0 | Every live row written | — |
| 2 | Configuration invalid, or unreadable | [Stage 0](#stage-0--configuration) — every problem is listed at once |
| 3 | The Entra credential could not be built | [Stage 1](#stage-1--credential-and-consent) — certificate, store, private key |
| 4 | Ingestion failed after the credential worked | Stages [3](#stage-3--the-connection-and-who-owns-it) to [5](#stage-5--items-written) — the log names the call |

Code 2 is emitted *before* logging is configured, so it prints to stderr as
`FATAL:` and nothing reaches `Logs/SqlGraphPush.log`. An empty log with a
non-zero exit is that case, not a missing log.

---

## The scripts

All read-only. They GET, they SELECT, and where something needs deleting they
print the command rather than running it.

| Script | Covers |
|---|---|
| [`Test-GraphPushPrereqs.ps1`](../deploy/Test-GraphPushPrereqs.ps1) | Stages 0–3: config, certificate, token, **the roles actually consented**, ownership, vault, SQL reachability |
| [`Watch-SchemaRegistration.ps1`](../deploy/Watch-SchemaRegistration.ps1) | Stage 4: the draft→ready wait, every state explained, the schema printed |
| [`Compare-SourceToIndex.ps1`](../deploy/Compare-SourceToIndex.ps1) | Stage 5: row-by-row reconciliation, and the orphans nothing else finds |
| [`Test-SqlSource.ps1`](../deploy/Test-SqlSource.ps1) | Stage 2 — shared with the agent path, unchanged |
| [`Verify-GraphConnection.ps1`](../deploy/Verify-GraphConnection.ps1) | Stages 6–7, from a user's perspective rather than the app's |

They share [`GraphPushAuth.ps1`](../deploy/GraphPushAuth.ps1), which does the
client-credentials flow directly rather than through the Microsoft.Graph module —
partly because a jump box need not have PowerShell modules installed, and partly
because the module cannot hand back the raw token, and the token is the evidence.

The usual sequence:

```powershell
.\Test-GraphPushPrereqs.ps1 -ConfigPath ..\src\SqlGraphPush\appsettings.json
dotnet run --project ..\src\SqlGraphPush
.\Watch-SchemaRegistration.ps1          # in a second window, if it sits in draft
.\Compare-SourceToIndex.ps1             # afterwards, and periodically
```

---

## Stage 0 — configuration

**What has to be true.** `src/SqlGraphPush/appsettings.json` has no
placeholders, a legal connection ID, and at least one ACL group.

**Prove it.** `.\Test-GraphPushPrereqs.ps1`

**The connection ID rules** are stricter than they look and are validated before
anything is sent: **3 to 32 characters, alphanumeric only** — no hyphens, no
underscores — and it cannot start with `Microsoft` or be `None`. `sql-tickets`
is rejected; `sqltickets` is the shipped value.

**The ACL is written into every item at push time.** An empty
`Acl:GrantGroupObjectIds` means every item is written visible to nobody, and
correcting it later means pushing every item again — there is no ACL-only
update. Fix this before the first run, not after.

---

## Stage 1 — credential and consent

**What has to be true.** A certificate in `CurrentUser\My` with a usable private
key, registered on the app, and both application permissions admin-consented.

**Prove it.** `.\Test-GraphPushPrereqs.ps1`

### Certificate or client secret

Both push tools accept either, through `Auth:Mode`, using the same shared code as
the connector. Certificate is the default. With `ClientSecret` the value lives in
Windows Credential Manager and only the entry's *name* is in configuration —
`Test-GraphPushPrereqs.ps1` reads that actual entry rather than prompting, and
says which of the two it used. Note that the entry is per Windows account and is
read **once at startup**, so a missing one is exit code 3 immediately rather than
a failure partway through a push.

### The store location

This applies to `Auth:Mode: Certificate`. `Auth:CertificateStoreLocation` is
**`CurrentUser`** here, not `LocalMachine` as in the connector. `SqlGraphPush` runs as a person; the connector runs as a
service. A certificate imported into the machine store is invisible to a
`CurrentUser` lookup and produces exit code 3 with a certificate that is plainly
right there in `certlm.msc`. The pre-flight script looks in both stores and tells
you when it finds it in the wrong one.

### The roles claim

This is the check worth running the script for.

An application permission that is **listed** in the portal but never
**admin-consented** does not appear in the access token at all. The only other
symptom is a bare `403 Forbidden` on whichever call needed it — and that same 403
is produced by an ownership problem, by a consent granted after the current token
was issued, and by a genuinely wrong permission. They are indistinguishable
without looking inside the token.

`Test-GraphPushPrereqs.ps1` decodes the token it just acquired and lists the
roles actually present:

```
  PASS  ExternalConnection.ReadWrite.OwnedBy granted
  FAIL  ExternalItem.ReadWrite.OwnedBy is NOT in the token
  note  Listed in the portal is not the same as consented.
```

It also flags anything **extra**. Both permissions should be `OwnedBy`; a `.All`
in that list is over-privileged and `docs/APP-REGISTRATION.md` §6 says why.

### AADSTS codes worth recognising

| Code | Meaning |
|---|---|
| `AADSTS700027` | The certificate is not registered on the app — upload the `.cer` |
| `AADSTS700016` | The application is not in this tenant — check `ClientId` and `TenantId` |
| `AADSTS7000222` | The client secret has **expired**. Nothing warns about this in advance |
| `AADSTS7000215` | Invalid client secret. Check the Credential Manager entry holds the *value*, not the secret ID from the Entra blade |
| `AADSTS900023` | That tenant ID is not a tenant |

### Key Vault is a separate audience

Only when `DataSource:SqlAuthMode` is `SqlLogin`. The same credential
authenticates to Graph *and* to the vault, so it is easy to assume that working
Graph consent implies vault access. It does not: the vault needs a data-plane
**role assignment** (Key Vault Secrets User is enough) and that is configured
somewhere else entirely. The pre-flight acquires a vault-scoped token separately
for exactly this reason.

---

## Stage 2 — the SQL source

Identical to the agent path, and the same script applies:

```powershell
.\Test-SqlSource.ps1 -TicketId 1001
```

Two differences in how the results matter here:

- **There is no watermark**, so the future-timestamp and local-time findings
  are not urgent for *this* pipeline — it re-reads everything each run. They
  still matter if you later move to the agent.
- **Reachability is the common failure.** The connector runs on a server chosen
  because it can reach SQL. `SqlGraphPush` runs wherever the operator is, which
  is frequently a jump box with outbound HTTPS and no route to port 1433.

---

## Stage 3 — the connection, and who owns it

**What has to be true.** The connection exists and **this app created it**.

**Prove it.** `.\Test-GraphPushPrereqs.ps1`

> ### The ownership collision
>
> `OwnedBy` means "connections owned by the application making the call". So:
>
> - If the **Graph connector agent** created `sqltickets`, `SqlGraphPush`
>   cannot touch it. Every call 403s, and it cannot create its own because the
>   ID is already taken.
> - If **`SqlGraphPush`** created it, the agent path cannot manage it either.
>
> **The two pipelines must not share a connection ID.** Give the push tool its
> own — `sqlticketsseed`, say — unless you are deliberately replacing one path
> with the other, in which case delete the connection first and accept that
> every item goes with it.
>
> Note the contrast with interactive verification, where an `OwnedBy` listing is
> *worthless* evidence because the calling app is Graph Command Line Tools. Here
> the calling app is the owner, so the listing is exactly right: a connection
> absent from it is one this app cannot manage. Same permission, opposite
> conclusion, depending on who is asking.

**Connection states**, all of which the pre-flight reports:

| State | Meaning |
|---|---|
| `draft` | Schema registration has not completed. Items written now are rejected |
| `ready` | Normal |
| `obsolete` | Superseded; will not serve results and cannot be revived |
| `limitExceeded` | Tenant item quota is full. No further items accepted |

---

## Stage 4 — schema registration

**What has to be true.** The connection reaches `ready`.

**Prove it.**

```powershell
.\Watch-SchemaRegistration.ps1
```

**This takes 5 to 15 minutes and reports no progress while it does.** The tool
polls every 30 seconds and gives up at `Graph:SchemaReadyTimeoutMinutes`
(default 30). That silence is why people conclude it has hung and delete the
connection — the one action that makes things worse, because it restarts the same
wait and discards everything already written.

**A timeout has cancelled nothing.** Registration continues server-side whether
or not the tool that started it is still running. Re-run the watcher; do not
re-run the push. Past about 30 minutes in `draft`, raise it with support.

> ### The schema is effectively append-only
>
> Once a connection is `ready` you can **add** properties. You cannot change an
> existing property's type, its search annotations or its semantic labels, and
> you cannot remove one.
>
> Correcting a mistake means deleting the connection — which deletes every item
> in it — and starting over, including the 5-to-15-minute wait. `Watch-SchemaRegistration.ps1`
> prints the registered schema the moment it goes ready, and warns if no property
> carries the `title` or `url` semantic label, because those plus content are
> what make items retrievable by Copilot rather than only by search.

The six properties this tool registers are `ticketId`, `title`, `status`,
`assignedTo`, `lastModified` and `url`. **`body` is deliberately not among
them**: the ticket body is sent as the item's *content*, not as a property.
This differs from the connector's seven-property schema — another reason the two
pipelines should not share a connection.

---

## Stage 5 — items written

**What has to be true.** One `PUT` per live row succeeded.

**Prove it.**

```powershell
.\Compare-SourceToIndex.ps1
```

It reads every row from SQL and asks Graph about each item ID, reporting four
states: `OK`, `STALE` (the row changed since the push), `MISSING` (a live row
with no item) and `ORPHAN` (an item whose row is gone).

**Why row-by-row and not a listing.** There is no list-items API. The
`externalItem` resource documents Create, Get, Update, Delete and
`addActivities` and nothing else, and the `List` parameter set that
`Get-MgExternalConnectionItem` advertises is generated from OData metadata
rather than an implemented operation. Fetching known IDs is the only way.

**`MISSING` rows, in order of likelihood:**

1. **Throttling that outlasted the backoff.** The engine honours `Retry-After`
   and retries an item five times before giving up, so a throttled write is
   normally survived rather than lost — the run summary reports
   `throttleWaits=`, and a number there is the tell. Five failures in a row
   fails the run with exit code 4 rather than skipping the item silently.
   `Compare-SourceToIndex.ps1` honours `Retry-After` itself and reports how
   often it was throttled reading.
2. **The run stopped partway.** Exit code 4. The log's last
   `Indexed ticketNNNN` line is the high-water mark, and because the query is
   `ORDER BY TicketId` everything above that ID is what is missing.
3. **The row is newer than the last push.** Nothing is scheduled here.

**Items exist but nobody can find them**: check the ACL. The script prints the
ACL of the first item it retrieves, and fails loudly on an empty one. Remember
the ACL is per item and written at push time — fixing the configuration requires
pushing every item again.

---

## The deletion problem

This is the defining characteristic of the direct push path and the thing most
likely to be discovered late, in a meeting, when Copilot cites a ticket that was
deleted months ago.

**`SqlGraphPush` never deletes anything.** Its query is:

```sql
SELECT TicketId, Title, Status, AssignedTo, Body, LastModified
FROM dbo.Tickets WHERE IsDeleted = 0 ORDER BY TicketId;
```

A soft-deleted row is **excluded from the push**. Excluded is not deleted: the
item written on an earlier run stays exactly where it is. It is simply never
refreshed again — still indexed, still searchable, still cited.

Nothing about that is visible in the tool's output. It reports how many items it
wrote, and the number is correct.

**Finding them:**

```powershell
.\Compare-SourceToIndex.ps1
```

Soft-deleted rows are checked *first*, before any `-MaxItems` cap applies, so a
capped run never hides an orphan. Each one found is printed with the exact
command that removes it:

```
Invoke-MgGraphRequest -Method DELETE -Uri 'v1.0/external/connections/sqltickets/items/ticket1042'
```

The script deliberately does not run these. Deleting an index item is not
reversible, and a mistyped connection ID would delete from a connection you did
not mean.

**The gap that cannot be closed.** A row *hard*-deleted from `dbo.Tickets`
leaves nothing to look up. Its item ID cannot be derived from a source that no
longer mentions it, and there is no list-items API to enumerate the index
against. No client can find those orphans. If hard deletes have happened, the
only reliable repair is to delete the connection and push again from scratch.

**The fix that is not a script.** The agent-hosted connector reports deletions
incrementally through the same `IsDeleted` column and the platform removes them
within a crawl cycle. If deletions matter to you, that is the answer — not a
scheduled reconciliation job.

---

## Stages 6 and 7 — search and Copilot

Identical to the agent path, because from Graph onward the two pipelines are the
same pipeline. See [`TROUBLESHOOTING.md` stage 6](TROUBLESHOOTING.md#stage-6--search-finds-them)
and [stage 7](TROUBLESHOOTING.md#stage-7--copilot-grounds-on-them).

One difference worth knowing: `POST /search/query` is **delegated-only**, so the
app-only credential this tool uses cannot run it. Verify search as a *user* — a
user in the ACL group:

```powershell
.\Verify-GraphConnection.ps1 -ConnectionId sqltickets -ItemId ticket1001 -SearchFor 'payment gateway'
```

---

## Traps that cost an afternoon

| Trap | Presents as |
|---|---|
| `CurrentUser` vs `LocalMachine` certificate store | Exit code 3 for a certificate visibly present |
| A permission listed but not consented | A bare 403 with no clue which permission |
| The agent owns the connection ID | A bare 403, and the ID cannot be reused |
| Deleting a "hung" draft connection | The same wait again, and every item lost |
| The schema is append-only | A one-character property mistake costs the whole connection |
| `throttleWaits=` in the summary | A slow run read as a hung one |
| Soft-deleted rows are excluded, not deleted | Deleted tickets cited by Copilot indefinitely |
| Hard-deleted rows | Orphans that no client can find |
| The ACL is written per item | Fixing the config changes nothing already pushed |
| Exit code 2 predates logging | An empty log file read as "it never ran" |

---

## Before escalating

```powershell
.\Test-GraphPushPrereqs.ps1 -ConfigPath ..\src\SqlGraphPush\appsettings.json > prereqs.txt
.\Compare-SourceToIndex.ps1 -Detail                                          > compare.txt
Get-Content ..\src\SqlGraphPush\bin\**\Logs\SqlGraphPush.log -Tail 200       > push.txt
```

Add the exit code of the failing run and the connection's state and item count
from the admin centre. As with the agent path, the single most useful sentence
is which stage was the **last one that passed**.

None of those three files contains a ticket title, a body, a property value or a
credential — the log writes item IDs and byte counts only, by design
(`SECURITY.md` LOG-3), and the scripts print names and thumbprint prefixes.
