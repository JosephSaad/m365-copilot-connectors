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

![Decision tree: a question for Copilot routes first on whether the askers hold M365 licences, then on who owns the data. Owned content with group-shaped access must also clear a freshness and deletion-SLA gate before it is indexed by a Graph connector; everything else, including almost all vendor-licensed data, is fetched by a live tool call. Each leaf carries its build effort and running meter.](copilot-route-decision-tree.png)

Four gates. The first is about reach, the second about ownership, and the last
two are the ones that actually decide. The dashed path is the only route that
puts licensed content into an index — it exists, but it needs a rider your
market-data team has to negotiate.

The editable source of the drawing is
[`copilot-route-decision-tree.svg`](copilot-route-decision-tree.svg); the PNG
above (2000×1660) is what the markdown embeds, so the picture renders
identically everywhere — including viewers that do not rasterise SVG.

**Read the two `CALL IT` leaves as wire protocols, not deployables.** An API
action is an OpenAPI spec and a manifest that a declarative agent or a Copilot
Studio agent hosts; you never deploy one on its own, and its cost is *on top of*
whatever the host agent already meters. An MCP server is a process, but a client
still has to be wired to it. A Graph connector is the odd one out and that is
its main advantage: publish once, and every M365 surface picks it up with
nothing told to it.

Three axes are in play, and only same-axis choices are alternatives:

| Axis | Choices |
|---|---|
| How the data reaches the model | pre-indexed retrieval · live invocation |
| How a live tool is exposed | API action · MCP server |
| Where the whole thing runs | M365 surfaces · an application you host |

**Azure AI Foundry sits on the third axis, not the first two.** Inside it you
would still choose retrieval versus invocation, with your own index and your own
tools. It appears in the drawing because "the askers are not M365 users" is a
real branch, not because it is a peer of a connector.

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
