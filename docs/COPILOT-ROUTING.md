# Which route — connector, action, MCP or Foundry

The decision **upstream** of this repository: whether a Graph connector is the
right answer at all, and what the alternatives cost.

Everything else in `docs/` assumes the answer is yes and tells you how to build
one. This document is the check that it should be.

---

## The rule

> **Own it → index it. Licence it → call it.**

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

---

## The tree

![Decision tree: a question for Copilot routes first on whether the askers hold M365 licences, then on who owns the data. Owned content with group-shaped access must also clear a freshness and deletion-SLA gate before it is indexed by a Graph connector, which splits into an agent-hosted connector and a direct push. Data that is row-level secured or computed, and that models cleanly, goes to a Power BI semantic model. Everything else, including almost all vendor-licensed data, is fetched by a live tool call: an API action, an MCP server you build, or a ready-made one somebody publishes. Each leaf carries its build effort and running meter.](copilot-route-decision-tree.png)

Four gates and three outcomes. The first gate is about reach, the second about
ownership, and the last two are the ones that actually decide. The dashed path
is the only route that puts licensed content into an index — it exists, but it
needs a rider your market-data team has to negotiate.

**`MODEL IT` is the third outcome, and it is not a flavour of the other two.** A
semantic model holds a copy the way an index does, and computes per user with
row-level security enforced at query time the way a live call does. It is the
right answer when the question is a number computed across rows, or when RLS has
to survive into the answer. It is the wrong answer for a catalogue: descriptive
metadata is a search problem over names and descriptions, so it belongs in the
index however structured it looks.

The interactive version of this page — fourteen questions that route one source
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

**Read the three `CALL IT` leaves as wire protocols, not deployables.** An API
action is an OpenAPI spec and a manifest that a declarative agent or a Copilot
Studio agent hosts; you never deploy one on its own, and its cost is *on top of*
whatever the host agent already meters. An MCP server is a process, but a client
still has to be wired to it. A **ready-made** MCP server is the same thing with
somebody else's name on it: you inherit its tool surface, whose identity it acts
as, and its release cadence, so review all three before connecting it. A Graph
connector is the odd one out and that is its main advantage: publish once, and
every M365 surface picks it up with nothing told to it.

**The two `INDEX IT` custom leaves are one decision you do not get to make on
preference.** Agent-hosted needs a Windows host and crawls incrementally, which
is what lets it delete; direct push runs anywhere with outbound HTTPS and never
deletes anything. So the deletion SLA picks it, and hosting picks it when the
SLA does not care.

Three axes are in play, and only same-axis choices are alternatives:

| Axis | Choices |
|---|---|
| How the data reaches the model | pre-indexed retrieval · semantic model · live invocation |
| Which custom connector, once indexing | agent-hosted · direct push |
| How a live tool is exposed | API action · MCP server · ready-made MCP server |
| Where the whole thing runs | M365 surfaces · an application you host |

**Azure AI Foundry sits on the third axis, not the first two.** Inside it you
would still choose retrieval versus invocation, with your own index and your own
tools. It appears in the drawing because "the askers are not M365 users" is a
real branch, not because it is a peer of a connector.

**Four things force it, and the tool asks about all four.**

| Forcing condition | Why nothing in M365 answers it |
|---|---|
| The askers have no Entra identity in the tenant | M365 Copilot is not reachable by them at any price |
| Network isolation — VNet, private endpoint | No M365 surface offers one. Note that Power BI Copilot is **explicitly unsupported** with Private Link and in closed networks, so it is not the fallback either |
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
  surface can carry regulated or licensed data at all;
- a second matrix for the delivery layer — gallery connector, custom connector,
  API action, MCP server, Foundry — with build effort, running meter, deletion
  latency and infrastructure required;
- the tree above;
- **an eleven-question router.** Answer what you know; the first hard gate to
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

## The three questions, if you want the short version

1. **Where is the person standing when they ask?** Teams, Word or Outlook → an
   M365 surface, grounded by a connector. Your public site or a call-centre
   desktop → Foundry or Copilot Studio; M365 Copilot is not reachable from
   there. An IDE or an analyst's tooling → MCP.

2. **Is the answer content, or is it computed?** Text that can sit still — a
   ticket, a case note, a policy, a measure's *definition* — index it. A number
   that moves, or anything that writes — call it. Indexing a computed value
   freezes it, and a frozen number that disagrees with the live report is a
   control problem rather than a nuisance.

3. **Who enforces access, and can they still do it afterwards?** If the answer
   is "the source system, live, per user", you cannot index it without losing
   the enforcement. That one question resolves row-level security, MNPI,
   information barriers and vendor seat licensing in a single move.

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

**There are no prices anywhere.** Effort bands and cost *shape* are stable;
current rate cards are not, and invented figures in a business case are worse
than none. Get the numbers from your licensing desk.

**Vendor licence terms are the gating item**, and they decide the architecture
rather than decorate it. The third-party guidance here reflects the common shape
of market-data agreements, not yours. Redistribution, derived data, caching, AI
use and audit clauses need reading by your market-data licensing team, in
writing, before engineering starts. Several vendors now sell an explicit AI-use
tier — ask what it costs before designing around its absence.
