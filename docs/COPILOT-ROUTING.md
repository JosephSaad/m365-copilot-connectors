# Which route — connector, semantic model, action, MCP or Foundry

The decision **upstream** of this repository: whether a Graph connector is the
right answer at all, and what the alternatives cost.

Everything else in `docs/` assumes the answer is yes and tells you how to build
one. This document is the check that it should be.

---

## The rule

> **Own it → index it. Licence it → call it.**
>
> *And since federated connectors shipped, calling it no longer costs you M365 reach.*

A Graph connector makes a persistent copy in a Microsoft-hosted index and grants
it to Entra groups. Against content you own, that is cheap, broad and correct —
one build, and it appears in Copilot chat, Word, Outlook, Teams and Search
without a retrieval story per surface.

Against data you licence from a vendor — Moody's, S&P, Bloomberg, MSCI — the same
mechanism collides with four clauses at once: redistribution, per-seat
entitlement, derived data, and AI use. Index it, ACL it to a group, and you have
entitled everyone in that group. Pay for 200 seats, expose 3,000, and that is
exactly what a vendor audit finds.

Licensed content is fetched **live**, entitled by the vendor per caller,
attributed, timestamped, and never stored.

**The mechanism for that used to be something you built. It is now something you
enable.** Microsoft's connector gallery holds two kinds of connector that behave
oppositely:

| | Synced connector | Federated connector |
|---|---|---|
| What it does with your content | crawls it and indexes it into the semantic index | nothing — it fetches at question time over **MCP** |
| Whose credentials | admin, at **organisation level** | each **user's own**, per user |
| Who enforces access | the Entra ACL written at push time | the **source system**, live |
| Read or write | read (a connector never writes) | **read-only**, by design |
| Where the answer appears | every M365 surface, implicitly | M365 Copilot Chat, Copilot in Excel, Researcher, Cowork |
| Audit | limited | Microsoft **Purview** |
| Effort | hours to weeks | configuration, plus an admin approval |

Several market-data vendors publish federated connectors. That matters more than
it sounds: the argument this document has always made — *index the metadata, call
for the value* — now has a configuration answer for the calling half, in the same
gallery as the indexing half, reaching the same chat window. Confirm the specific
connector exists and that an admin will enable it (partner-published ones need
explicit approval), and read the licence anyway: per-seat counting moves from
"everyone in the Entra group" to "everyone who connected it", which is better,
but it is still counting.

---

## The tree

![Decision tree: a question for Copilot routes first on whether the askers hold M365 licences, then on who owns the data. Owned content with group-shaped access must also clear a freshness and deletion-SLA gate before it is indexed by a synced Copilot connector, which splits into an agent-hosted connector and a direct push. Data that is row-level secured or computed, and that models cleanly, goes to a Power BI semantic model, whose storage mode — Direct Lake, Import or DirectQuery — is picked by OneLake residency and by whether a second copy is permitted. Everything else, including almost all vendor-licensed data, is fetched by a live tool call: a federated Copilot connector, an API action, an MCP server you build, or a ready-made one somebody publishes. Each leaf carries its build effort and running meter.](copilot-route-decision-tree.png)

Four gates and three outcomes. The first gate is about reach, the second about
ownership, and the last two are the ones that actually decide. The dashed path
is the only route that puts licensed content into an index — it exists, but it
needs a rider your market-data team has to negotiate.

Each outcome then has exactly one decision it does not get to make on preference:

| Outcome | The decision it cannot make on preference | What decides it instead |
|---|---|---|
| `INDEX IT` | agent-hosted or direct push | the deletion SLA, then the hosting. Only the agent deletes |
| `MODEL IT` | Import, DirectQuery or Direct Lake | OneLake residency, and whether a second copy is permitted |
| `CALL IT` | federated, action, MCP or ready-made MCP | where the answer must appear, and whether you are writing |

**`MODEL IT` is the third outcome, and it is not a flavour of the other two.** A
semantic model *can* hold a copy the way an index does, and it computes per user
with row-level security enforced at query time the way a live call does. It is the
right answer when the question is a number computed across rows, or when RLS has
to survive into the answer. It is the wrong answer for a catalogue: descriptive
metadata is a search problem over names and descriptions, so it belongs in the
index however structured it looks.

### The three storage modes, and why they are a routing decision

Power BI is a workload inside Fabric; the semantic model is one artifact inside
that workload; the storage mode decides **where the rows physically sit**. That
last one is not a performance tuning knob — it sets classification scope,
labelling obligations and residency compliance, which is why it belongs in a
routing document rather than in a modelling guide.

| | At rest | When queried | In OneLake? | The consequence |
|---|---|---|---|---|
| **Import** | VertiPaq columnar store, persisted in Power BI managed storage in the capacity region | loaded into capacity node memory | no — unless OneLake integration is on, which writes a further read-only Delta copy | you have created a governed copy that must be classified, labelled and audited independently |
| **DirectQuery** | nothing stored. The model holds metadata, relationships and DAX only | translated to native queries run against the source at report runtime | no. Nothing lands anywhere | zero copy, but latency and source load scale with user concurrency |
| **Direct Lake** | in OneLake as Delta Parquet, and nowhere else. No second copy, no refresh cycle | column segments transcoded into capacity memory on demand, evicted under pressure | yes, by definition — OneLake *is* the storage | import-class performance without creating a new copy to govern |

Two things this table does not say, and both catch people out. **Metadata is
always persisted**: the model definition, DAX measures, relationships, row-level
security roles and lineage live in the Power BI metadata store whatever mode you
pick — so "DirectQuery stores nothing" is a claim about your rows, not about your
model. And **composite models mix modes per table**, so one table forcing a mode
does not force the whole model.

The rule that falls out of it:

> **If the source data cannot leave its current store, Import is disqualified
> because it creates a copy, and Direct Lake is disqualified because the data
> must be resident in OneLake. That leaves DirectQuery — or a shortcut, which
> registers the data in OneLake without moving it.**

That last clause is the one worth remembering, because it turns most
"DirectQuery only" conclusions into Direct Lake ones. There are four ways data
reaches OneLake and they are not equivalent: a **shortcut** references data in
place (ADLS Gen2, S3, GCS, Dataverse — no copy, no schedule, no compute, source
stays authoritative); **mirroring** maintains a near-real-time replica from a
change feed with no pipeline to build (Azure SQL DB and MI, SQL Server 2025,
Cosmos DB, Snowflake, PostgreSQL, Databricks); a **pipeline or dataflow** is an
explicit scheduled copy; and a **direct write** from Spark or T-SQL is native to
the workload. Only the shortcut is genuinely zero-copy, and only the shortcut
survives a no-second-copy rule.

### Capacity: two thresholds, and both bite

`MODEL IT` is the only outcome that needs a Fabric or Premium capacity, and it
needs it twice over:

- **F2 or higher** (or Premium P1+) is where Copilot and Fabric data agents
  become available at all. Pro or PPU alone is not a Copilot capacity, and
  **trial SKUs are not supported** — which is the usual reason a proof of
  concept works and the pilot does not.
- **F64 or higher** is where report consumers stop needing an individual Power
  BI Pro licence. Below it, every viewer needs Pro on top of the capacity. That
  line usually decides the SKU rather than the compute does.

Capacity attaches to a workspace, so different workspaces can sit on different
capacities — which is how chargeback and blast-radius isolation get implemented.

### How a model gets asked in words — and why that is a separate question

A semantic model does not answer questions; something asks it. There are three
askers and they reach different places:

| | What it grounds on | Where the answer appears |
|---|---|---|
| **Power BI Q&A** | one semantic model | inside a report or the service |
| **Copilot in Fabric** | the workload you are authoring in | inside Fabric, as authoring assistance |
| **Fabric data agent** | selected lakehouses, warehouses, KQL databases and semantic models | inside Fabric — or, published to **Copilot Studio**, in Teams or as a declarative agent in M365 Copilot |

All of them need F2+ and tenant-level enablement. **None of them makes anything
retrievable in Microsoft 365 Copilot chat on its own.** Reaching that window from
the Fabric side means publishing the data agent as a tool to a Copilot Studio
agent — a second build, a second approval, and a second thing to operate.

### Power BI against Cloudera, specifically

Relevant here because this repository's other connector points at CDP. Power BI
connects to query engines and storage endpoints, never to a cluster in the
abstract, and the engine decides which modes are available:

| Data shape | How Power BI reaches it | Viable modes |
|---|---|---|
| Metastore tables via **Impala** | native Impala connector against a CDW Impala virtual warehouse, JDBC or ODBC. Kudu-backed tables the same way | Import **and** DirectQuery — the recommended path |
| Same tables via **Hive** | Cloudera Hive ODBC driver, or the Spark connector against HiveServer2 | Import only in practice. Hive on Tez is batch, so DirectQuery lands in tens of seconds |
| **Spark SQL** | Spark connector against a Thrift server endpoint | both, but inherits session startup latency without a long-running Thrift server |
| Raw files on **HDFS** | Hadoop File (HDFS) connector over WebHDFS | Import only. No predicate pushdown, file-by-file. Not viable at bank data volumes |
| **HBase** | Apache Phoenix ODBC driver, or a Hive external table over the HBase table | Import only; added latency rules out DirectQuery |
| **Iceberg** tables | through Impala or Spark — or a OneLake shortcut, which virtualizes Iceberg metadata as Delta via Apache XTable | DirectQuery through Impala, or **Direct Lake via shortcut** where storage is cloud object storage, not local HDFS |
| **Kafka / NiFi** streams | no direct path | must land in a queryable store first |

**Authentication is what usually decides it.** CDP clusters are typically
Kerberized. On-premises CDP needs the on-premises data gateway plus Kerberos SSO
through constrained delegation for per-user identity to reach Impala. Knox with
LDAP over HTTPS is simpler but breaks per-user filtering unless Ranger policies
are applied through a service-account proxy — at which point every caller is one
identity and row-level enforcement is gone.

> **Settle Ranger enforcement versus Power BI row-level security before design,
> not after.** They are two enforcement engines over the same rows. Deciding late
> means either duplicating the policy in DAX or discovering that the gateway
> flattened every user into a proxy account.

The interactive version of this page — seventeen questions that route one source
to one delivery path, with the cost and the warnings attached — ships beside it
as [`copilot-decision-matrix.html`](copilot-decision-matrix.html). Open it in a
browser; it is a single self-contained file with no build step and no network
calls.

The editable source of the drawing is
[`copilot-route-decision-tree.svg`](copilot-route-decision-tree.svg); the PNG
above (2480×1692) is what the markdown embeds, so the picture renders
identically everywhere — including viewers that do not rasterise SVG. A
dark-theme render of the same drawing ships as
[`copilot-route-decision-tree-dark.png`](copilot-route-decision-tree-dark.png)
for decks and dark documents.

**Read the four `CALL IT` leaves as wire protocols, not deployables.** An API
action is an OpenAPI spec and a manifest that a declarative agent or a Copilot
Studio agent hosts; you never deploy one on its own, and its cost is *on top of*
whatever the host agent already meters. An MCP server is a process, but a client
still has to be wired to it. A **ready-made** MCP server is the same thing with
somebody else's name on it: you inherit its tool surface, whose identity it acts
as, and its release cadence, so review all three before connecting it.

**A federated connector is the fourth, and it is the one that changes the shape
of the argument.** It is an MCP server too — but one Microsoft fronts, lists in
the gallery, and wires into M365 Copilot Chat, Copilot in Excel, Researcher and
Cowork with nothing told to them. So it has the reach property that used to
belong only to indexing, and the no-copy property that used to require giving
that reach up. What you pay for it: it is **read-only**, it reaches four
experiences rather than all of them, and **every user connects it individually**
with their own credentials — so adoption is per head, and a user who never
connects it gets no answer rather than an error you can monitor.

A synced connector is still the odd one out among the index paths, and that is
still its main advantage: publish once, and every M365 surface picks it up with
nothing told to it.

**The two `INDEX IT` custom leaves are one decision you do not get to make on
preference.** Agent-hosted needs a Windows host and crawls incrementally, which
is what lets it delete; direct push runs anywhere with outbound HTTPS and never
deletes anything. So the deletion SLA picks it, and hosting picks it when the
SLA does not care.

Five axes are in play, and only same-axis choices are alternatives:

| Axis | Choices |
|---|---|
| How the data reaches the model | pre-indexed retrieval · semantic model · live invocation |
| Which custom connector, once indexing | agent-hosted · direct push |
| **Which storage mode, once modelling** | **Import · DirectQuery · Direct Lake** |
| How a live tool is exposed | **federated connector** · API action · MCP server · ready-made MCP server |
| Where the whole thing runs | M365 surfaces · an application you host |

**Azure AI Foundry sits on the third axis, not the first two.** Inside it you
would still choose retrieval versus invocation, with your own index and your own
tools. It appears in the drawing because "the askers are not M365 users" is a
real branch, not because it is a peer of a connector.

**Four things force it, and the tool asks about all four.**

| Forcing condition | Why nothing in M365 answers it |
|---|---|
| The askers have no Entra identity in the tenant | M365 Copilot is not reachable by them at any price |
| Network isolation — VNet, private endpoint | No M365 surface offers one. Note that Power BI Copilot is **explicitly unsupported** with Private Link and in closed networks, so it is not the fallback either — and neither is a Fabric data agent, which sits on the same capacity and the same constraint |
| You must pin and validate the model | Under model-risk governance an assistant whose model changes on Microsoft's cadence is hard to attest to. Foundry is the only route where you choose the model and its version |
| You need retrieval you control | Your own chunking, embeddings, hybrid search and reranking. A Graph connector gives you *its* retrieval, not yours — what you can change is the schema, the semantic labels, and which content is in scope |

Two more reasons argue for it without forcing it: a **per-token cost model**
rather than per-seat, which flips the arithmetic at both very low and very high
user counts; and **prompt-and-response logging into your own SIEM**, where the
M365 audit trail gives you what Microsoft chooses to give you.

---

## The interactive version

[`copilot-decision-matrix.html`](copilot-decision-matrix.html) is a
self-contained page — no build step, no dependencies, no network calls. It holds:

- your surface capability matrix, extended with the rows that decide whether a
  surface can carry regulated or licensed data at all — including the two that
  catch people out: which surfaces need a Fabric capacity, and which of them
  actually put an answer into M365 Copilot chat;
- a second matrix for the delivery layer — synced connector, federated
  connector, the three Power BI storage modes, Fabric data agent, API action,
  MCP server, Foundry — with **where the rows physically sit**, build effort,
  running meter, deletion latency and infrastructure required;
- the tree above;
- a section on **the two planes** — what belongs to Microsoft 365 and Graph, what
  belongs to Fabric, and why retrieval and delegation are not substitutable;
- **a seventeen-question router.** Answer what you know; the first hard gate to
  fire picks the route and the rest becomes watch-outs.

**GitHub shows this file as source, not as a page.** Download it and open it in
a browser, or `git clone` and open it from disk. It works offline.

Both matrices also ship as standalone images, rendered from this page so they
cannot drift from it — drop them straight into a deck or a review pack:

**The surfaces** — where the person is standing, including the extension rows
for regulated data, licensing and cost:

![Capability-by-surface matrix: nine Copilot surfaces scored across grounding, actions, reach and effort, plus the extension rows — live entitlement enforcement, licensed third-party data fit, network isolation, model choice, build effort, recurrent meter and budget owner.](copilot-surface-matrix.png)

**The delivery paths** — how data reaches those surfaces, with cost, freshness,
deletion latency, security boundary and data fit:

![Delivery-path matrix: gallery connector, custom connector, API action, MCP server and Foundry application compared on layer, standalone-ness, wiring per surface, freshness, deletion latency, infrastructure, build and recurrent cost, access enforcement, persistent copies, audit trail, and fit for first-party, RLS-guarded and licensed third-party data.](copilot-delivery-paths.png)

---

## The four questions, if you want the short version

1. **Where is the person standing when they ask — and where must the answer
   appear?** These are two questions, not one, and answering only the first is
   how a connector gets built for a surface that does not read connectors.
   Teams, Word or Outlook → an M365 surface, grounded by a connector. A report
   or a Fabric workspace → a semantic model and a data agent, which will not
   reach M365 chat without a Copilot Studio hop. Your public site or a
   call-centre desktop → Foundry or Copilot Studio; M365 Copilot is not
   reachable from there. An IDE or an analyst's tooling → MCP.

2. **Is the answer content, or is it computed?** Text that can sit still — a
   ticket, a case note, a policy, a measure's *definition* — index it. A number
   that moves, or anything that writes — call it. Indexing a computed value
   freezes it, and a frozen number that disagrees with the live report is a
   control problem rather than a nuisance.

3. **Who enforces access, and can they still do it afterwards?** If the answer
   is "the source system, live, per user", you cannot index it without losing
   the enforcement. That one question resolves row-level security, MNPI,
   information barriers and vendor seat licensing in a single move — and it no
   longer costs you M365 reach, because a federated connector keeps enforcement
   at the source and still answers in Copilot chat.

4. **And if it is computed: where may the rows sit?** In OneLake already, or
   reachable by a shortcut → Direct Lake. A governed copy is permitted → Import.
   Neither → DirectQuery, and budget for the source load.

---

## Two planes, and what belongs to each

Grounding mechanisms get attributed to Fabric constantly, and most of them are
not Fabric at all. The two planes have separate governance, separate funding and
separate approval paths.

| | Microsoft 365 & Graph plane | Fabric plane |
|---|---|---|
| Governed by | the M365 admin center and the connectors API | Fabric tenant settings and capacity admin |
| Funded by | M365 licensing. **No Fabric capacity, no OneLake footprint** | the F SKU |
| What lives there | the semantic index; synced connectors (gallery, custom agent-hosted, direct push); federated connectors; the Graph Connector Agent | semantic models; Fabric data agents; the Fabric MCP servers; OneLake; Fabric REST APIs |
| What it does | **retrieval** — content is put where Copilot looks | **delegation** — a query capability an agent calls at runtime |

> **Retrieval versus delegation. The two are not substitutable.**

The failure mode always runs the same direction: a team picks Fabric to avoid a
connector approval, ships it, and discovers the M365 Copilot grounding
requirement was never met. A federated connector is the one mechanism that sits
in both frames at once — delegation by wire, retrieval by placement — which is
exactly why it is the interesting answer for content that may not be copied.

**On the Fabric MCP servers specifically:** the local one is generally available;
the remote Core, Data Warehouse and data-agent-as-MCP servers are previews at the
time of writing, so check status before designing on them. All of them need F2+
or P1+ with Fabric enabled, plus *cross-geo processing and storage for AI*
switched on in tenant settings — and **responses returned to an MCP client may
leave Fabric's compliance boundary and geographic region**, handled thereafter
under the client's own data policies. For a regulated customer that is a control
question, not a configuration detail.

---

## Three factors people leave until too late

**Deletion SLA.** The question that most often invalidates a design after it is
built. A **direct push never deletes** — a row excluded from the query leaves its
item in the index indefinitely. The **agent-hosted** connector deletes, but only
on its next incremental crawl, so the best SLA it can offer is the crawl
interval. If a removed record has to stop appearing immediately, no index path
qualifies. `deploy/Compare-SourceToIndex.ps1` finds the orphans a push leaves
behind and prints the `DELETE` commands without running them.

**Freshness.** Bounded by crawl interval on every index path. Nothing makes an
index live. Hourly freshness needs a watermark column, incremental crawls, and
an alert when the watermark chain breaks — a silent break looks exactly like
"nothing changed today". `deploy/Get-CrawlHistory.ps1` checks that chain link by
link.

**Infrastructure.** The agent-hosted model needs a Windows host running the
Graph connector agent; without one you are on the direct push model, and the
deletion paragraph above applies. With no infrastructure at all, the only routes
are a gallery connector or a SaaS-hosted source.

---

## The hybrid, for when neither answer is right

**Index the metadata. Call for the value.**

Index *that* you hold a rating for an issuer, its as-of date, the methodology,
your own analyst commentary, and a link — all of which is either yours or
non-substantive. Then fetch the rating itself live, entitled and attributed.

Copilot can then answer *"which issuers did we downgrade internally last
quarter, and what does Moody's say now?"* — the first half from the index, the
second from the call. Neither half alone gets there, and the licensed content
never lands in the index.

The same shape resolves a Power BI semantic model (index the measure
definitions and ownership, call `executeQueries` for the numbers, and RLS stays
enforced by Power BI) and a Fabric warehouse.

**A data catalogue is the clearest case, and this repository now implements
it.** Which database holds which table, who owns it, what it is tagged with and
what feeds it is knowledge — asked in words, about named things, by people who
do not know where to look. The rows underneath are analytics, and belong in a
query. So indexing is right here even where the data described can never be
indexed: a table Ranger row-filters is refused by the data connector and
described by the catalogue one, for exactly the people already granted select on
it. `src/CdpGraphPush/AtlasCatalogueConnector.cs` is that connector, and
`docs/SECURITY.md` sets out the access rules it turns on.

---

## Caveats

The surface matrix in the HTML page was reconstructed from a photographed slide.
Three cells are reproduced as photographed but look wrong, and are flagged in
the page itself rather than silently corrected — chiefly a row showing M365
Copilot Chat as unable to use Graph connector content, which contradicts how
connector grounding works.

**The Fabric and federated-connector material is younger than the rest of this
document**, and it is the part most likely to have moved. Which experiences
consume a federated connector, which Fabric MCP servers have left preview, and
whether a data agent published through Copilot Studio behaves as a declarative
agent in every M365 surface are the three things to re-check against current
Microsoft documentation before quoting any of it in a design.

**There are no prices anywhere.** Effort bands and cost *shape* are stable;
current rate cards are not, and invented figures in a business case are worse
than none. Get the numbers from your licensing desk.

**Vendor licence terms are the gating item**, and they decide the architecture
rather than decorate it. The third-party guidance here reflects the common shape
of market-data agreements, not yours. Redistribution, derived data, caching, AI
use and audit clauses need reading by your market-data licensing team, in
writing, before engineering starts. Several vendors now sell an explicit AI-use
tier — ask what it costs before designing around its absence.
