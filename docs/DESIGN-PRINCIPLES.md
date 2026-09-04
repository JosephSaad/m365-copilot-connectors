---
title: Design principles
description: The reasoning behind every routing and refusal decision in this repository, stated independently of any customer, any source technology and any release — the part meant to outlive the engagement that produced it.
---

# Design principles

**This document is the transferable part.** The connectors are worth one project;
the reasoning is worth many. Everything below is stated without reference to a
customer, and applies to a source nobody here has met yet.

Read it before `ROUTING-DECISIONS.md` (which applies this to eight specific
sources) or `CONNECTOR-ONBOARDING.md` (which turns it into questions). Those two
are instances. This is the thing they are instances of.

---

## The three principles

Every guard, refusal and control in this repository descends from one of these.
A rule that descends from none of them is probably not worth having.

### 1. The copy must never out-grant the original

A connector translates one system's access decision into another's. Every
translation is lossy. What matters is the **direction** of the loss:

- **Under-granting** leaves content out of the index. It costs value, is visible
  to the people missing it, and can be scheduled.
- **Over-granting** puts data in front of someone the source refuses. It is
  silent, it is discovered by the wrong person, and it cannot be undone by a
  later crawl — the item was already served.

They are not equally bad, so they do not get equal treatment. **This asymmetry
is the single most load-bearing idea in the repository.** It decides which
constructs stop a run and which are merely logged, and it is why a guard that
fires on the safe direction is considered a defect: it teaches operators to
disable guards.

### 2. Know what the target cannot represent

A Microsoft 365 permission is a **static list of Entra group object IDs, written
at crawl time**. That sentence contains three limits, and each is absolute:

| Limit | Consequence |
|---|---|
| **Static** | A rule whose answer changes with the clock cannot be mirrored. Re-crawling bounds the drift; nothing removes it |
| **Group-scoped** | A grant to a named individual has nowhere to go |
| **Written at crawl time** | A permission change at the source is invisible until the next **full** crawl, because permission changes rarely change content |

Where the source expresses something on this list, no amount of connector work
mirrors it. The honest responses are **refuse**, or **exclude from scope**, or
**state a bound and have somebody accept it**. Approximating is not on the list:
an approximation of an access-control decision is a wrong access-control
decision that looks like a right one.

### 3. Usefulness is a separate axis from correctness

A perfectly safe index that carries nothing readable fails as completely as one
that leaks. Encrypted fields, tokenised columns and numeric measures all index
without complaint and answer nothing — and no dashboard can tell that connection
from a healthy one.

Security reviews reliably ask the first question and reliably skip the second.
Ask both.

---

## Per-user enforcement

The concept the other three keep colliding with, and the one worth naming
precisely because every platform calls it something different.

**Per-user enforcement is when the source decides what you see at the moment you
ask, based on who you are.** Two people running an identical query get different
answers.

### Why it cannot be indexed

A connector reads as **one identity**. Whatever it sees is that identity's view.

- A **privileged** crawl identity sees everything, indexes everything, and grants
  it to everyone with object-level access. That is a leak.
- A **restricted** crawl identity sees its own slice, indexes that, and serves it
  to every reader. No leak — and the index is silently wrong for everybody else.

There is no identity that produces the right answer, because the right answer
differs per reader and an index holds one copy. This is why the response is
refusal rather than a differently-privileged service account.

### The same idea, five vocabularies

The reference worth keeping. When meeting a new platform, the question is not
"does it have RLS" but "what does *this* platform call the thing where two
callers see different results, and which catalogue view exposes it".

| Platform | Row-level | Value-level | Where to look |
|---|---|---|---|
| SQL Server | Row-Level Security | Dynamic Data Masking, Always Encrypted | `sys.security_predicates`, `sys.masked_columns` |
| Oracle | VPD, Label Security, Real Application Security | Data Redaction | `ALL_POLICIES`, `ALL_SA_TABLE_POLICIES`, `REDACTION_POLICIES` |
| Teradata | Row-level security constraints | Column constraints | `DBC.SecConstraintsV` + constraint columns |
| Hive / Ranger | Row filters, tag policies | Column masks | `rowFilterPolicyItems`, `dataMaskPolicyItems`, the tag service |
| MongoDB | Views using `$redact` / `$$USER_ROLES` | CSFLE, Queryable Encryption | `listCollections` type, BSON binary subtype 6 |

### The fix worth asking for first

**Change the shape at the source, not the route.** A view that applies the
restriction *at rest* and is granted group-wise stops being per-user and becomes
ordinary indexable content — flat cost, every surface, no per-user identity
plumbing.

It is almost always cheaper than the alternatives, and the alternatives are
federating the query (which keeps enforcement at the source and costs a licence
and a consent per user) or modelling the data on a different plane entirely.

---

## What a source concept becomes

| Source expresses | Becomes | Why |
|---|---|---|
| Group grant on an object | An Entra group on the item | The shape the target was built for |
| Grant to a named individual | **Nothing** | Unrepresentable, not unimplemented |
| A deny | **A refusal to index** | Never mirrored. Obeying a deny by indexing-and-hoping is the failure this exists to prevent |
| Row filter or column mask | **A refusal to index** | One copy cannot vary per reader |
| Time-bounded or conditional grant | **A refusal, or a stated staleness bound** | The target has no clock |
| An exception carved out of a grant | Subtracted from the grant | Static, so it can be evaluated honestly |

---

## Applying this to a source nobody has met

The method, in the order that lets you stop earliest:

1. **Ask what one item is.** It decides the schema, the identifier and the count.
2. **Ask who may see it, and where that is recorded.** If the answer is a
   principal type the target cannot express, stop here — the rest is wasted.
3. **Ask what the platform calls per-user enforcement, and find the catalogue
   view that exposes it.** Every platform has both; only the names differ.
4. **Decide the direction of every gap.** For each construct the reader will not
   evaluate, establish whether ignoring it over-grants or under-grants. Refuse on
   the first, log the second. Nothing else is a defensible middle.
5. **Ask whether the content is worth indexing** — separately, and out loud.
6. **Write the refusal before the reader.** A guard added afterwards is a guard
   added after the first crawl has already run.

Step 4 is the one that is skipped and the one that matters. It is a two-minute
exercise per construct and it is the difference between a connector that fails
loudly and one that fails silently.

---

## What is engagement-specific, and what is not

Reuse the left column. Replace the right one wholesale for a new customer.

| Transfers unchanged | Rewrite per engagement |
|---|---|
| This document | `ROUTING-DECISIONS.md` — its premises and verdicts are one customer's |
| `COPILOT-ROUTING.md` — the routing rule and its evidence | `*-PILOT-PARAMETERS.md` — the parameter sheets |
| `copilot-router.html` — the routing tool | `GO-LIVE-READINESS.md` — a live-test record |
| `CONNECTOR-ONBOARDING.md` and its form | `What-is-Next.md` — an open-items list |
| `ADDING-A-PUSH-CONNECTOR.md` | |
| `SECURITY.md`'s control **shapes**, if not its specific findings | |

**A test for the left column:** if a document names a hostname, a cluster, a data
domain or a customer, it belongs on the right. The onboarding form failed this
test once — it carried a customer's data domain in a tooltip and would have
published it to a public URL. Check before adding, not after.
