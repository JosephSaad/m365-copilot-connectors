---
title: What is next
description: The four things known to be open — what each one is, why it matters, what would close it, and which of them are decisions rather than work.
corrected: 2026-08-31, items 2 and 4. See the note at the foot of each.
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
| 2 | Two semantic labels are absent | Work | Attribution and creation dates, everywhere |
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

## 2 · Two semantic labels are absent

**What.** The connectors set `title`, `url`, `fileName`, `fileExtension`,
`itemPath`, `containerName`, `containerUrl` — and `lastModifiedDateTime`, which
is set in **all six**: `TicketsPushConnector.cs:68`,
`HierarchyPushConnector.cs:131`, `HdfsDocumentsConnector.cs:68`,
`HiveContractsConnector.cs:74`, `AtlasCatalogueConnector.cs:111`, and
`SqlDataSource.cs:120` on the agent-hosted path.

Absent are **`createdDateTime`** and **`createdBy`/`authors`**. No source file
references either.

**Why it matters, and less than the first draft of this page claimed.**
Freshness ranking, "what changed recently" and date filtering all work today,
because `lastModifiedDateTime` carries them. What is missing is **creation** and
**attribution**: Copilot cannot distinguish when something was raised from when
it was last touched, and cannot attribute or filter by person. Invisible in
testing, because nothing errors — the answers are simply thinner.

**What would close it, and the two halves are very different.**

**`createdDateTime` needs a source column that does not exist.** Every table
carries `LastModified` only — `dbo.Tickets` (`sql/00`) and `dbo.Customers`,
`dbo.Engagements`, `dbo.TimeEntries` (`sql/10`). So the change runs the whole
depth of the stack: add the column, populate it in `sql/11` and `sql/14`, project
it through all three UNION arms of `sql/12`, keep `sql/26` the same shape or the
`sql/35` parity check fails, add it to `BuildQuery`, `BuildSchema` and `MapRow`
in both SQL push connectors. **Backfilling a creation date nobody recorded is
inventing data** — acceptable on the rig, a source-system question at a customer,
where the only defensible derivations are `WorkDate` for a time entry and
`StartDate` for an engagement. Tickets and customers have nothing to derive from.

**`createdBy` needs no SQL at all, and mostly should not be set.** The person
columns are already selected and already mapped, just unlabelled: `assignedTo`,
`accountManager`, `projectManager`, `consultantName`. But **a label cannot be
added to an existing property** — append-only covers labels, not only types — so
each one means a *new* property carrying the same value. And three of the four
would be false: an assignee is not an author. Labelling `AssignedTo` as
`createdBy` answers "who raised this ticket?" with the current owner, confidently
and wrongly, which is worse than leaving it unset. `ConsultantName` on a time
entry genuinely is the author of that narrative; that one is honest.

**The cost is a new connection, not a re-crawl.**
`PushEngine.EnsureSchemaAsync` logs *"Schema already registered"* and returns the
moment a connection is `Ready` (`src/PushCore/PushEngine.cs:552`), so a property
added later is never PATCHed onto it. `VerifySchemaOwnership` records the
addition as pending and **the run proceeds** — and every item then carrying a
value for an unregistered property is refused by Graph one at a time into
`Failed`. Pointing a new build at the existing connection therefore produces a
broken crawl rather than a partial improvement. It needs a new
`Graph:ConnectionId`, 5 to 15 minutes of server-side registration, a full push
from empty, and the old connection deleted afterwards.

Which is the argument for doing it **once**: settle the created date, the
`consultantName` decision and anything else wanted in the schema in a single new
connection, because every schema mistake costs a connection and its whole corpus.

> **Corrected 2026-08-31.** This item first said *three* labels were "set
> nowhere", including `lastModifiedDateTime`. That was wrong: the check searched
> for the property-name string `lastModifiedDateTime` rather than the enum
> `Label.LastModifiedDateTime`, and missed all six sites. Freshness ranking
> already works. The gap is two labels, and the recency half of the original
> claim was false.

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
`<host>`. It entered in **`8914197`**, *"Every readiness row now carries a Live
Test 2 verdict"*, and stands in the tree of **six** commits between there and the
redaction.

**Why it matters.** Less than it sounds, and it is recorded here so that nobody
rediscovers it and treats it as an incident. It is a hostname, not a credential:
no secret, no tenant identifier, no connection string. `gitleaks` has since run
over the full history and found nothing. The repository is public, so it is
readable — that is the whole of the exposure.

**What it would cost to remove**, which is the part worth knowing before anybody
decides:

- **Twelve commits are rewritten**, not one. The rewrite has to begin at
  `8914197`; starting anywhere later removes nothing, because the text survives
  in every earlier tree.
- **Four tags move** — `v1.8.0`, `v1.8.0-net9`, `v1.8.1`, `v1.8.1-net9`. Two of
  them are the tags the `v1.8.1` release notes send people to **install from**,
  and a published release does not follow a re-pointed tag on its own.
- **Both branches are force-pushed.** `main` and `release/net9`; every clone
  needs a hard reset.
- **It may not actually remove it.** A force push leaves the old objects
  reachable by SHA on github.com and through the API until garbage collection,
  and **any fork keeps them indefinitely**. Actual removal needs a request to
  GitHub Support to purge the cache. The whole cost can be paid without the
  benefit arriving.
- **This page breaks itself.** The hashes cited in this very entry, `8914197`
  and `227a8f9`, are both inside the rewritten range. So are the four green CI
  runs, which would point at commits belonging to no branch.
- **It rewrites evidence.** The line is a verbatim quotation of a live test
  refusal inside the readiness document. In a regulated estate, altering the
  recorded output of a test is a harder question to answer than the hostname is.

**Whose call, and only yours.** Rewriting shared history is not a change to make
on someone's behalf. The standing position is that it has **not** been done and
will not be without an explicit instruction. **"Leave it" is the recommendation**
— and this entry can then be closed as accepted rather than left open. Knowing
that the blast radius is half again larger than first recorded, and that removal
is not even guaranteed at the end of it, only strengthens that.

> **Corrected 2026-08-31.** This item first named `bd7fdc2` as the commit holding
> the original text. `bd7fdc2` contains it, but is not where it entered: a
> rewrite starting there would have removed nothing while breaking everything
> downstream of it. The cost paragraph above replaces a single sentence that
> described the price as a reset of open clones.

---

*Related: [go-live readiness](GO-LIVE-READINESS.md) · [the five sources, routed](ROUTING-DECISIONS.md) · [what we need from the CDP team](CDP-PILOT-PARAMETERS.md) · [security control mapping](SECURITY.md)*
