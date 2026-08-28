# The genesis prompt — the Copilot Router

This is the prompt that produces `docs/copilot-router.html` and the
artifacts coupled to it.

Not a description — an instruction set. Given an empty directory and a capable
agent with web access to `learn.microsoft.com`, everything below the rule is
what has to be said to arrive at what is checked in here.

## Why it is written down

The page says *what* routes where. Nothing in it says why a question is a gate
rather than a warning, why the storage-mode picker consults deletion before
residency, or why the caveats section holds four items when it used to hold
twelve. Roughly half of what follows exists because something was wrong once —
a routing engine that sent vendor-licensed data into a persisted copy, a
capability matrix transcribed faithfully from a slide that was itself wrong, a
"fact" about Cowork that had been stale for two months on the day it was
written. Those are invisible in the finished page, because the finished page is
the version where they were caught.

Three uses:

1. **Rebuild for a different customer.** Sections 2 to 5 are doctrine and are
   true of any organisation routing data to AI surfaces. Section 6 is the
   platform as it stood on a stated date and must be re-verified, not copied.
2. **Onboarding.** Hand this to whoever inherits the tool before they open the
   HTML. It states the constraints as constraints, not as things to infer from
   fourteen hundred lines of JavaScript.
3. **A drift check.** Every fact in section 6 carries the discipline that
   produced it: verified against vendor documentation on a stated date. When
   the page's revision stamp is older than the platform's latest move, this
   document says exactly which claims to re-check first.

## What it is not

It is not maintained as a specification. Where this document and
`copilot-router.html` disagree, **the page is right** — it carries the
revision stamp and the harness. This document restarts a build; it does not
adjudicate one.

---

# ▸ THE PROMPT

## 0. Role

You are building the **Copilot Router**, a decision-support tool for a consultant advising a
**financial-services customer under regulatory review** on how enterprise data
reaches AI assistants. The reader will price real investments and approve real
architectures from this page. The two ways it can fail are not symmetric:

- **Recommending a route that stores or over-grants data** the customer said
  may not be stored or shared is the failure that ends engagements. Every
  routing default fails closed: when in doubt, route to the option that stores
  nothing.
- **Costing a route wrongly** merely embarrasses. Still avoid it.

Write plain, direct prose. State what a thing costs, what it does not do, and
who has to keep it working. Every claim about a vendor product is checked
against that vendor's current documentation before it is written down —
**never from memory**, however confident the memory. Where documentation and a
supplied slide disagree, the slide is wrong until proven otherwise.

## 1. What you are building

One page plus the artifacts that cannot be allowed to drift from it:

| Artifact | What it is |
|---|---|
| `copilot-router.html` | The tool. One self-contained file: no build step, no network calls, renders from `file://` and offline |
| `copilot-route-decision-tree.svg` | The editable drawing of the routing tree |
| `copilot-route-decision-tree.png` / `-dark.png` | The same drawing rendered at 2×, light and dark |
| `copilot-surface-matrix.png` / `copilot-delivery-paths.png` | The two big tables, rendered **from the page** so they cannot disagree with it |
| `COPILOT-ROUTING.md` | The prose version of the whole argument |

The page contains, in order: the rule card; the routing tree (inline SVG); an
nineteen-question router with a JavaScript engine; a capability-by-surface
matrix; a delivery-paths matrix; a side-by-side of the three live-call
packagings; a two-planes section; owned-vs-licensed panels; the hybrid pattern;
the short-version questions; and a caveats section holding **only what the
reader alone can check**.

Self-containment is absolute: fonts embedded as base64 `@font-face` data URIs
(IBM Plex Sans variable, IBM Plex Mono, Spectral — all SIL OFL), every colour
from CSS custom properties, the tree inline. The page is pinned dark
(`color-scheme: dark` on `:root`) because it is a reference read on screens,
and the palette is part of how it reads.

A visible **revision stamp** sits in the footer — date, `rN`, and a one-line
delta. It exists because stale browser caches produced three rounds of "your
fix isn't there" during the original build. Bump it on every publish. Never
grep for the stamp loosely: the embedded fonts are base64 and match almost any
short alphanumeric pattern, so `grep r15` false-positives — match
`r15</strong>` or the full stamp sentence.

## 2. The doctrine

Everything routes from one rule and its two riders:

> **Own it → index it. Licence it → call it.**
> *A semantic model is the third outcome, not a flavour of the other two.*
> *Since federated connectors shipped, calling it no longer costs M365 reach.*

Three outcome bands, each with exactly one decision its occupants do not get to
make on preference:

| Band | The forced decision | What forces it |
|---|---|---|
| `INDEX IT` | agent-hosted or direct push | deletion SLA, then hosting — only a crawl detects deletions for you |
| `MODEL IT` | Import, DirectQuery, or Direct Lake | OneLake residency and whether a second copy is permitted |
| `CALL IT` | federated, API action, MCP server, ready-made MCP | where the answer must appear, and whether it writes |

Doctrine that must survive any rebuild:

- **Retrieval versus delegation, and they are not substitutable.** The Graph
  plane puts content where Copilot looks; the Fabric plane exposes a query
  capability an agent calls. A team that picks Fabric to dodge a connector
  approval discovers the M365 grounding requirement was never met.
- **A catalogue is a search problem however structured it looks.** Descriptive
  metadata belongs in the index even when the data it describes may never be
  indexed. A model earns its place only for numbers computed across rows or
  row-level security enforced per user.
- **Merging (joining, integration) is the question nobody asks until late.**
  An index ranks but cannot relate; a live call returns one source and the
  model stitches — plausibly, not correctly, because it has no key and no
  cardinality; only a model joins. Carry both words, *merge* and *join* — the
  first is what stakeholders say, the second is what the platform does, and a
  reader searching either must find it.
- **Audience size is a cost question, not only a load question.** Standing
  capacity punishes ten users; per-seat and per-call meters punish fifty
  thousand; the index is the one path whose cost stops growing with headcount.
- **The hybrid resolves the hard cases:** index the metadata, call for the
  value. It is also the shape that lets licensed content serve Copilot without
  ever landing in the index.
- **Federated and MCP are not rivals** — a federated connector *is* an MCP
  server after registration. The three live-call packagings differ only in who
  can reach them; build the comparison table so nobody rebuilds a server that
  registration would have promoted.

## 3. The routes

Twelve terminal routes. `custom` exists internally but always resolves to
agent or push before rendering.

`gallery` (synced, configuration) · `agent` (custom synced, Windows host, the
only custom index model where deletion is detected for you) · `push` (custom
synced, runs anywhere, nothing detects deletions — the API deletes only when
your code calls it) · `federated` (MCP fronted by Microsoft; gallery or your
own registered server; read-only; per-user auth; Copilot add-on per user) ·
`pbiimport` · `pbidq` · `pbidirectlake` (three distinct routes, not one with a
footnote) · `action` · `mcp` · `mcpready` · `foundry` · `blocked`.

## 4. The nineteen questions

Fields in engine order: `reach, surface, own, access, kind, model, onelake,
join, conc, fresh, del, infra, gw, src, vol, reuse, iso, gov, ret`. The ones that
exist because their absence cost something:

- **Q2 `surface`** — *where must the answer appear.* The question most often
  skipped, and the one that separates a connector from a Fabric data agent. A
  connector built for a surface that does not read connectors has no consumer.
- **Q4 `access`** — four options, not three: groups, row-level,
  **column/object-level**, dynamic barriers. Column rules fail differently: an
  external item is one flat document, so indexing publishes the restricted
  fields to everyone the item is granted to.
- **Q7 `onelake`** — six options including both **"no path to OneLake, but a
  copy into the model is acceptable"** and **"cannot leave its current store
  at all, not even into a model."** These were one option once, and the engine
  routed "cannot leave its store" to Import — the mode that copies the rows
  out. Two constraints, two options.
- **Q8 `join`** — merged or joined with other data. Shapes warnings, never the
  route: a join requirement cannot override ownership or access, and when the
  answer is computed and models cleanly the model branch already wins. What it
  does is attach the bolded warning that an index estate answering
  cross-source questions is "a modelling problem wearing a search problem's
  clothes."
- **Q11 `del`** — the deletion SLA, phrased precisely: *nothing detects
  deletions on a direct push; removal is an API call your own code makes; a
  crawled connection detects them on its next pass.* Never write "a push
  never deletes" — the API deletes by id; what is absent is detection.
- **Q13 `gw`** — is there an approved gateway pattern for MCP in the estate.
  Shapes warnings, never the route: where no pattern exists, the MCP-flavoured
  routes inherit a critical path of pattern approval that dwarfs their build
  cost, and the near-term answer shifts to routes that ride ordinary HTTPS.
  Came from a real working group discovering it late; the capability (APIM's
  remote MCP server mode) was never the gap — the deployed, approved pattern
  was.

## 5. The engine

**Gate order is the safety argument.** First match wins, so the sequence
encodes which failure is worse: `blocked` → network isolation → askers outside
the tenant → retrieval-you-own → pinned model (all four → `foundry`) → the
**federated** gate (source has a federated connector, not a write, and
something forbids a copy) → no-second-copy → **vendor / unread licence** →
barriers → RLS → OLS → write → computed → live freshness → immediate deletion
(all → `action`) → gallery → the custom default.

**Post-gate upgrades, each with its guard:**

- `action` → Power BI mode, only when the reason was computed/RLS/OLS *and the
  data models cleanly* — **and never when `own` is vendor or unknown.** The
  unguarded version shipped briefly and sent vendor-licensed data to Import: a
  persisted copy of exactly what the rule card forbids. An adversarial review
  caught it; the guard is not optional.
- `action` → `mcp` when many more sources are coming; either → `mcpready` when
  a published server exists (it never overrides an index verdict).
- `custom` → `agent` on a same-day deletion SLA or an on-prem host; → `push`
  otherwise. `del === "immediate"` cannot reach this branch — an earlier gate
  owns it; do not test for it here.

**The storage-mode picker (`pbiMode`) consults, in order:** immediate deletion
(→ DirectQuery — only the mode that stores nothing makes removal at the source
removal everywhere) → no-copy compliance unless a shortcut zero-copies →
unanswered residency (→ DirectQuery, the reversible mode) → cannot-leave-store
(→ DirectQuery always) → in-lake (→ Direct Lake; DirectQuery when a scheduled
copy meets a live-freshness need) → no-lake-but-copy-ok (→ Import, or
DirectQuery if live). Every branch has a `modeWhy` sentence; a mode without a
reason is a bug.

**Warnings philosophy:** a warning fires only on routes where it is true.
`index &&` conditions misfired direct-push advice onto the gallery route until
each was keyed to `agent`/`push` explicitly. No unreachable branches, no route
keys that do not exist in `ROUTES` — both happened, both were found by review,
not by use. Warnings carry the load the gates cannot: Power BI's licence
thresholds (F2/P1 to exist, F64 to drop viewer Pro), Direct Lake's security
caveats, federated licensing, fan-out cost, Hadoop-specific connectivity (keyed
to the Power BI routes only), the per-head arithmetic at both ends of the
audience scale.

## 6. Platform facts to design around — verified 2026-08-28, re-verify before reuse

Each of these is encoded in cells, gates or warnings. Each was checked against
`learn.microsoft.com` on the date above; three of them had changed within
weeks of being written the first time.

- **Direct Lake on OneLake does not apply SQL-endpoint row-level, object-level
  or column-level security.** It reads the files; file access does not observe
  SQL RLS, so a query the warehouse would filter succeeds in full. Model-level
  RLS works but Microsoft recommends a fixed-identity connection — the same
  caller-flattening this page condemns elsewhere. "Direct Lake" is two modes:
  on-OneLake never falls back; on-SQL honours endpoint security by falling
  back to DirectQuery for RLS and erroring for object/column rules.
- **Direct Lake cannot use any gateway** — on-premises or VNet — so no
  on-premises source qualifies however it is shortcut. Same-region workspaces
  required. Framing is a real refresh: metadata-only, seconds, and on
  guardrail breach the model stops answering. F2–F8 guardrails: 10 GB model,
  3 GB memory, 300M rows. Premium P SKUs work; "F2+" alone misleads.
- **Power BI Q&A retires December 2026.** Copilot for Power BI is the
  replacement — and it needs paid F2+/P1+ (no Pro/PPU alone, no trials, no
  Private Link, no closed networks), which is a cost change as well as a
  feature change, because Q&A was free on any licence.
- **Federated Copilot connectors:** MCP fetch at question time, nothing
  indexed, per-user Entra SSO/OAuth, read-only, four experiences (Copilot
  Chat, Excel, Researcher, Cowork). Licensing is a hard edge: Copilot add-on
  or E7 per querying user; not Copilot Studio licences, not pay-as-you-go.
  Organisations can **register their own MCP server** as a custom federated
  connector in the admin center ("Created by your org"); Microsoft-published
  gallery ones are enabled by default, partner ones need approval. The "no
  auth" registration option flattens every caller — treat as a trap.
- **A Fabric data agent publishes directly to the M365 Agent Store**
  (preview; Copilot licence per user) as well as through Copilot Studio, and
  a published agent is something a person invokes, not ambient grounding.
  Consumed in M365, its responses leave Fabric's compliance boundary under
  M365's terms — Microsoft says so explicitly; treat as a control decision.
- **Deletion:** on a direct push Microsoft's first sight of any change is your
  API call; crawled connections detect on the next pass (Microsoft advises a
  crawl at least every 14 days for reliable detection); a 28-day
  non-rediscovery backstop removes items when detection is failing — a
  compliance net, not a design surface.
- **The per-licence connector index quota is retired.** Every tenant carries a
  flat 50-million-item index at no extra cost. Never write "check quota
  against licence count."
- **Cowork is GA worldwide** (June 2026): Copilot licence plus Copilot Credits
  usage billing, no Frontier enrolment. Workflows remains Frontier-gated on
  consumer SKUs. A capability matrix drawn from a photographed slide got five
  cells wrong here — transcribe nothing you have not verified.
- **Copilot Studio agents run without an M365 Copilot licence** (own licence
  or pay-as-you-go), with synced-connector content serving agents but not chat
  grounding on those terms. M365 Copilot chat grounding on connector content
  requires the add-on.

## 7. Design system

Dark, pinned. Ground `#0C1214`, surfaces `#141C1E`/`#1B2427`, ink `#E4EBEA`,
muted `#94A3A6`, rules `#253032`/`#354245`, accent `#58C4AF` on `#16302E`,
semantic marks: full `#4FB187`, partial `#D3A244`, risk `#E38175` on
`#33201D`. Spectral for headings, IBM Plex Sans for body, IBM Plex Mono for
eyebrows, stamps, table captions and everything tabular. Tables live in
`overflow-x: auto` scrollers; the wide delivery table gets `table.wide` with a
raised min-width. Marks are `<span class="mark m-full|m-partial|m-none">` —
never bare glyphs in a `val` cell, which render grey and break the legend.

Questionnaire cards use `<div class="qhead">` with `aria-labelledby` — **never
`<legend>`**, which renders on the fieldset's border box, ignores padding, and
strikes through two-line titles. That one cost three rounds of user-reported
misalignment.

## 8. The tree

One drawing, two homes: inline in the page (sharing the page's tokens) and a
standalone SVG with its own light palette plus a `prefers-color-scheme` block.
Keep a single source for the body and assemble; after every change assert
**every `<text>` node of the standalone SVG appears verbatim in the page** —
the parity check that catches a half-applied edit.

Rendering PNGs: headless Chrome reports `prefers-color-scheme: dark`, so the
media query cannot select your theme — **pin the palette as an inline `style`
on the root `<svg>` element** per variant and render at
`--force-device-scale-factor=2`. `--force-dark-mode` is an inversion filter,
not your palette; never use it. Text-fit is arithmetic, not hope: ~7 px/char
for 13 px titles, ~6.3 px/char for 10.5 px mono, ~5.7 px/char at 9.5 px — check
every label against its rect before rendering, and check that no edge path
crosses a label's span.

The matrix PNGs are rendered from the live page's own markup and stylesheet in
a small harness (fixed `table-layout` so notes wrap instead of stretching
columns) — never drawn separately, so they cannot drift.

## 9. Verification — the page is code; treat it as code

- **A headless harness drives the actual page**: set radios, click the button,
  assert the rendered route name, for every gate, every storage mode, every
  upgrade and its guard (vendor data never reaches a model; immediate deletion
  forces DirectQuery; "cannot leave its store" never reaches Import; a write
  beats the federated gate; a catalogue stays indexed). Serve over HTTP —
  `--dump-dom` on `file://` returns before the script runs.
- **Structural asserts** after every table edit: each `<tbody>` row's `<td>`
  count equals its header's column count.
- **Stale-claim sweeps** by grep after every correction pass: the phrase you
  just corrected is also in the tree, the doc, a route sub, a warning, and a
  question hint. Fix all or none.
- **Facts are checked against vendor documentation, then adversarially:**
  independent finders per dimension (vendor facts, internal consistency,
  engine logic, structure, the drawing), then a skeptic per finding instructed
  to refute it, defaulting to refuted when uncertain. The confirmed set is
  what you fix. On this page that process found eleven real defects after two
  ordinary verification passes had run clean — including the vendor-to-Import
  routing hole. Budget for it.
- **Caveats discipline:** the caveats section holds only what the reader alone
  can check (their licence position, their vendor contract, a stated pricing
  policy, one reading instruction). Everything verifiable gets verified and
  corrected in place, then its caveat is deleted. Footnote markers are
  ordinals into that list — renumber when it changes, and assert the targets.

## 10. Decisions already taken — do not re-open

- Join/merge shapes warnings, never the route (§4).
- The federated gate sits **before** the no-copy and vendor gates so the
  configuration answer is offered first, but a write disqualifies it
  unconditionally.
- `mcpready` never overrides an index verdict; owned group-shaped content
  indexes even when a published server exists.
- No prices anywhere, deliberately: effort bands and cost shape are stable,
  rate cards are not, and invented figures in a financial-services business
  case are worse than none.
- "The model stitches" is not a synonym for "joins," and the page says so
  where the marks would otherwise flatter the live-call routes.
- Pilot success criteria are written per route, never conflated across routes.
- The page is pinned dark; the standalone SVG is theme-aware. Both on purpose.

## 11. Landmines — do not rediscover these

- `<legend>` on the fieldset border box (§7).
- Headless Chrome's dark default and the `--force-dark-mode` inversion (§8).
- Inline `<style>` inside an inlined SVG leaks into the page cascade — strip
  it and let the page's tokens style the drawing.
- Literal `—` vs `&mdash;` and `—`: exact-match edits fail silently
  across the boundary; when a replacement misses, print the actual bytes
  before assuming the text is absent.
- All-or-nothing edit scripts: assert every target string, and know that one
  miss means nothing was written.
- A failed commit followed by `git checkout <branch> -- docs/` destroys
  uncommitted work; the publish staging copy is the recovery path. Commits
  need an explicit repo-local identity — hostname-derived authorship breaks
  the day the hostname changes.
- Renaming the GitHub repository keeps web and git redirects but **kills the
  Pages URL with no redirect** — every shared link dies silently.
- Base64 font data defeats naive greps (§1).
- A photographed slide is testimony, not evidence (§6, Cowork).

## 12. Definition of done

The harness passes every case. The parity check passes. Both matrix PNGs and
both tree PNGs re-rendered if their sources changed. `COPILOT-ROUTING.md`
says what the page says. The revision stamp is bumped and describes the delta.
The artifact, the Pages deployment and both branches (`main`,
`release/net9`) carry the same content. A claim you did not verify today is
either deleted or dated.

# ▸ END OF PROMPT

## What this deliberately leaves open

The question set will grow — governance regimes and platform moves add gates.
What must not change without a fight: the fail-closed gate order, the
warnings-fire-only-where-true rule, the self-containment of the file, and the
discipline that every vendor claim carries the date somebody actually checked
it.
