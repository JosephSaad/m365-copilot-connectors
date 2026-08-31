---
title: What is next
description: The four things known to be open — what each one is, why it matters, what would close it, and which of them are decisions rather than work.
---

# What is next

Four things are open. This is not the backlog — the exhaustive per-feature
record is [GO-LIVE-READINESS.md](GO-LIVE-READINESS.md), and the routing
questions are settled in [ROUTING-DECISIONS.md](ROUTING-DECISIONS.md). This page
is the short list a person needs when they pick the work up again, with enough
context to act without reading either.

**Two are work and two are decisions.** The distinction matters, because a
decision sitting in a work queue looks like something nobody got round to rather
than something waiting on a person.

Date: 2026-08-31, at `v1.8.1`.

| # | | Kind | Blocks |
|---|---|---|---|
| 1 | CDP has never run against a real cluster | Work, gated on a customer | Three of the five routed scenarios |
| 2 | Three semantic labels are set nowhere | Work | Grounding quality on every scenario |
| 3 | No production code-signing certificate | Decision, then work | Any install where an operator checks a signature |
| 4 | A machine name is in committed history | Decision only | Nothing technical |

---

## 1 · CDP has never run against a real cluster

**What.** Live Test 1 and Live Test 2 both exercised the **SQL direct-push
path**. Every CDP assertion in this repository is against a fake: the identity
cache's store calls, the Ranger policy evaluation, the HDFS ACL builder, the
Hive watermark. No HDFS, Hive, Ranger or Atlas endpoint has ever answered this
code.

**Why it matters.** Three of the five sources in
[ROUTING-DECISIONS.md](ROUTING-DECISIONS.md) are CDP, and their verdicts rest on
behaviour that has been reasoned about rather than observed. The throughput
model, the ACL derivation and the refusal paths are all untested against a
service that can say no in its own way. `deploy/Compare-SourceToIndex.ps1` does
not generalise to CDP either — its query is literally `FROM dbo.Tickets` — so
the reconciliation that would catch a silent divergence does not yet exist for
these three.

**What would close it.** In order, and the first two are cheap:

1. Send [CDP-PILOT-PARAMETERS.md](CDP-PILOT-PARAMETERS.md) and get section 0
   answered, which decides whether one, two or three connectors are even in
   scope.
2. Run `deploy/Test-CdpSource.ps1` on the connector host **as the service
   account**. It is read-only and its check 0 says which identity is being
   tested — a probe run by a human passes on the human's Kerberos ticket, their
   Ranger grants and their HDFS group memberships, any of which can differ from
   the service account's.
3. Let the failures set the build order. Writing more CDP code before a cluster
   has answered a single request is how the second untested thing gets built.

**Gated on** a customer answering the sheet. Nothing here is blocked on us.

---

## 2 · Three semantic labels are set nowhere

**What.** The connector sets `title`, `url`, `fileName`, `fileExtension`,
`containerName` and `containerUrl`. It sets **`lastModifiedDateTime`,
`createdDateTime` and `createdBy`/`authors` nowhere at all** — verified across
every tracked file under `src/` and `sql/`.

**Why it matters.** Those labels are how Copilot knows an item's date and its
author. Without a date label it cannot distinguish a 2019 engagement from last
week's, cannot rank on freshness, and cannot answer "what changed recently".
Without an author label it cannot attribute or filter by person. This applies to
**every** scenario, SQL and CDP alike, and it is invisible in testing because
nothing errors — the answers are simply worse than they should be.

**What would close it.** A schema and mapping change rather than new plumbing:
the source data already carries these values. Add the labels to the schema
registration, map them in the `sql/12` views and the CDP sources, and re-crawl.
The re-crawl is the cost — a label change alters the schema, so the corpus has
to be rewritten rather than incrementally updated.

**The cheapest real improvement on this page**, and the one with the widest
reach.

---

## 3 · No production code-signing certificate

**What.** `build/Invoke-CodeSigning.ps1` exists and does the full job —
Authenticode over the assemblies this repository builds, over the PowerShell
that ships in the package, and a Windows file catalog signed over the whole
package. **No production certificate exists to run it with**, so every package
published so far, including `v1.8.0`, is unsigned.

**Why it matters.** An operator who checks a signature finds none. In a
regulated estate that is a deployment blocker discovered at deployment time, and
the machinery being ready does not help if the certificate procurement has not
started — it runs through a different queue from everything else on this page.

**What would close it.** A decision on which certificate authority and who owns
the private key, then procurement, then a run of the script that already exists.
The technical half is done.

**Whose call.** Yours, and worth starting before it is needed rather than when.

---

## 4 · A machine name is in committed history

**What.** A development machine's hostname reached
[GO-LIVE-READINESS.md](GO-LIVE-READINESS.md) inside a verbatim quotation of a
run-lock refusal message. The document was corrected in `227a8f9` and now reads
`<host>`. **The original text remains in the history of commit `bd7fdc2`.**

**Why it matters.** Less than it sounds, and it is recorded here so that nobody
rediscovers it and treats it as an incident. It is a hostname, not a credential:
no secret, no tenant identifier, no connection string. `gitleaks` has since run
over the full history and found nothing. The repository is public, so it is
readable — that is the whole of the exposure.

**What would close it.** A history rewrite over that commit, and a force push.
That is the entire cost: every clone and every open branch would need to be
reset, and any link to a commit hash after `bd7fdc2` would break.

**Whose call, and only yours.** Rewriting shared history is not a change to make
on someone's behalf. The standing position is that it has **not** been done and
will not be without an explicit instruction. If the answer is "leave it", this
entry can be closed as accepted rather than left open — which is the more likely
right answer, given what it is.

---

*Related: [go-live readiness](GO-LIVE-READINESS.md) · [the five sources, routed](ROUTING-DECISIONS.md) · [what we need from the CDP team](CDP-PILOT-PARAMETERS.md) · [security control mapping](SECURITY.md)*
