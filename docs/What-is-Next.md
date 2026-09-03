---
title: What is next
description: What is open and what is closed — the Ranger constructs that fail open, the CDP cluster gate, the two absent labels, the signing certificate, and one accepted risk.
corrected: 2026-08-31. Item 0 added from the first CDP answers; items 2 and 4 corrected; item 4 closed as accepted.
---

# What is next

**Four things are open, and a fifth is closed.** This is not the backlog — the
exhaustive per-feature record is
[GO-LIVE-READINESS.md](GO-LIVE-READINESS.md), and the routing questions are
settled in [ROUTING-DECISIONS.md](ROUTING-DECISIONS.md). This page is the short
list a person needs when they pick the work up again, with enough context to act
without reading either.

**Three of the four are work; the fourth is a decision.** The distinction
matters, because a decision sitting in a work queue looks like something nobody
got round to rather than something waiting on a person. Item 4 is kept below
rather than deleted, because a closed item stops being rediscovered as a new one.

**Start at item 0.** It is the only open item that is blocking and waiting on
nobody, and one half of it writes an over-permissive ACL rather than failing.

Date: 2026-08-31, at `v1.8.1`.

| # | | Kind | Blocks |
|---|---|---|---|
| ~~**0**~~ | ~~The Ranger evaluator ignores four constructs, and two fail open~~ | **Step 1 done — the four now refuse** | Steps 2 to 4 remain |
| 1 | CDP has never run against a real cluster | Work, gated on a customer | Three of the five routed scenarios |
| 2 | Two semantic labels are absent | Work | Attribution and creation dates, everywhere |
| 3 | No production code-signing certificate | Decision, then work | Any install where an operator checks a signature |
| ~~4~~ | ~~A machine name is in committed history~~ | **Closed — accepted 2026-08-31** | Nothing |

Item 0 is numbered zero because it arrived after the others and displaces all of
them. It is the only thing on this page that is **blocking, unblocked and ours**
at once — no customer, no procurement and no decision standing in front of it.

---

## 0 · The Ranger evaluator ignores four constructs, and two fail open

**What.** A first customer answered section 5 of
[CDP-PILOT-PARAMETERS.md](CDP-PILOT-PARAMETERS.md) on 2026-08-31: **no Security
Zones**, **`cm_tag` policies configured**, **policy exceptions configured**. The
first answer is good — `RefuseSecurityZones` will not fire. The other two name
constructs this connector does not read.

| Construct | Where | Behaviour | Direction |
|---|---|---|---|
| `allowExceptions`, `denyExceptions` | `RangerPolicyClient.cs:340` reads `policyItems` and `denyPolicyItems` only | Silently ignored | **Fails open** |
| Tag policies on `cm_tag` | only `RangerHdfsService` and `RangerSqlService` are fetched | Never seen | **Fails open** |
| User-level grants | `RoutingEvaluator` reads `item.Groups`, never `item.Users` | Silently dropped | Fails closed |
| `validitySchedules`, item `conditions`, `isDenyAllElse` | not parsed anywhere | Read as absent | Mixed |

**Why it matters, and why it is item 0.** An `allowExceptions` block exists
precisely to carve principals **out** of a grant. Ignoring it means the connector
computes an ACL that admits exactly the people the policy excludes, and writes
that to the index — which is the failure this codebase exists to refuse. It is
worse than the tag gap because it is silent: Security Zones stop the run with a
message, and these do not. The user-level gap points the other way and is
therefore only expensive — content quietly missing rather than quietly exposed.

**Step 1 is done, and control CDP-18 records it.**
`RangerPolicyClient.RefuseUnreadableConstructs` now stops the run when a live
policy carries `allowExceptions`, a condition on the policy or on any item, a
`validitySchedule`, or `isDenyAllElse` — the four that make the cluster more
restrictive than this connector computes, and therefore the four whose absence
writes an ACL that is too generous. `denyExceptions` and grants to named users
are logged instead: both cost content rather than exposing it, and a guard that
fires on the safe direction teaches operators to disable guards. A disabled
policy is exempt, because it decides nothing. Eleven tests pin it, including one
asserting the guard does **not** fire on an ordinary policy set.

**Step 2 is done for the half that can be done.** `RoutingEvaluator` now
evaluates `allowExceptions` and `isDenyAllElse` — the two static constructs —
rather than refusing on them. The other half is not pending work: `conditions`
and `validitySchedules` depend on the clock, and a Graph permission has nowhere
to put one, so evaluating them would replace a loud refusal with a quiet
divergence. They stay refused, and that is the answer rather than a stop-gap.

**Step 3 is done too, as control CDP-19.** `RefuseTagPoliciesAsync` reads
`Settings:RangerTagService` once per client and stops the run when any enabled
tag policy denies or masks. A tag policy that only grants is ignored: not
reading it under-grants, and refusing on it would block a crawl over a policy
that could only have made this connector more cautious. The check sits inside
`PoliciesAsync` rather than beside the three call sites that construct the
client, so a fourth cannot forget it.

**That converts the silent over-grant into a stopped run, which is what step 1
was for. It does not evaluate anything**, so steps 2 to 4 stand unchanged, and a
customer whose policies use these constructs now cannot crawl at all until they
are evaluated or the crawl is scoped around them. That is the intended trade:
the alternative was indexing under an ACL known to be wrong.

**What would close it.** The fix shape already exists **in the same file**.
`RefuseSecurityZones` is the precedent: detect the construct, refuse the run,
name what to do. In rough order of value:

1. **Refuse on a non-empty `allowExceptions`/`denyExceptions`**, before anything
   is written. That converts a silent over-grant into a stopped run, and it is a
   day's work rather than a design.
2. **Then evaluate them**, which is the real fix and needs Ranger's precedence
   rules honoured rather than guessed.
3. **Refuse, or warn loudly, when `cm_tag` holds any masking or deny policy** —
   already documented as a known gap in
   [SENSITIVITY-LABELS.md](SENSITIVITY-LABELS.md), now a live one.
4. **Decide what a user-level grant means.** A Graph ACL here carries Entra group
   object IDs, so a Ranger grant to an individual may be unrepresentable rather
   than merely unimplemented. That is a design answer, not a code change.

**Gated on nothing.** The five follow-up questions that size it — masking and
row-filter policies in scope, whether the exceptions name groups or users, how
many in-scope policies carry one, whether any tag policy denies or masks, and a
read-only JSON export of `cm_hive` and `cm_hdfs` — are with the customer. The
refusal at step 1 does not wait for any of them.

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

**Gated on** a customer answering the sheet — but no longer *only* on them. The
first three answers arrived on 2026-08-31 and produced **item 0**, which is
entirely ours. Point 3 above said the failures should set the build order; these
are the first failures, and they landed before a single cluster call.

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

## 4 · A machine name is in committed history — **closed, accepted**

> **Decided 2026-08-31: leave it.** The risk is accepted and recorded in
> [SECURITY.md](SECURITY.md) section 4, item 8, which is the register that
> outlives this page. Nothing further is owed on it, and it needs no re-decision
> unless the trigger below fires.
>
> **What would reopen it:** a secret, tenant identifier or production host name
> found anywhere in the same commit range. That changes the calculus entirely and
> the rewrite happens regardless of cost. Tidiness does not qualify.
>
> The rest of this section is kept as the record of what was weighed, so the
> decision can be audited rather than taken on trust — and so nobody rediscovers
> the hostname and opens it a second time.

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

**What was decided.** Leave it. Rewriting shared history is not a change to make
on someone's behalf, and it was not made: **no history has been rewritten, no
branch force-pushed, and no tag moved.** The repository is exactly as it was. The
decision was taken with the blast radius measured rather than estimated — twelve
commits, four tags, and no guarantee of removal at the end of it — against an
exposure that is a hostname on a development rig.

> **Corrected 2026-08-31.** This item first named `bd7fdc2` as the commit holding
> the original text. `bd7fdc2` contains it, but is not where it entered: a
> rewrite starting there would have removed nothing while breaking everything
> downstream of it. The cost paragraph above replaces a single sentence that
> described the price as a reset of open clones.

---

*Related: [go-live readiness](GO-LIVE-READINESS.md) · [the five sources, routed](ROUTING-DECISIONS.md) · [what we need from the CDP team](CDP-PILOT-PARAMETERS.md) · [security control mapping](SECURITY.md)*
