---
title: SqlTicketsConnector
description: Microsoft 365 Copilot connectors for SQL Server and Cloudera CDP — the code, the decisions behind it, and the operator documentation.
---

# SqlTicketsConnector

Microsoft 365 Copilot connectors for **SQL Server** and **Cloudera CDP Private
Cloud Base 7.1.9**, built for a regulated environment: group-only ACLs mirrored
from the source, no secret anywhere in configuration, and a crawl that stops
rather than indexing under access rules it cannot read faithfully.

The [repository]({{ site.github.repository_url }}) is the code. This site is the
documentation that ships with it.

## Start here

| | |
|---|---|
| [**Copilot Router**](copilot-router.html) | Nineteen questions that route one source to one delivery path — synced or federated connector, one of the three Power BI storage modes, a live call, or an application you host — with the cost and the warnings attached. Self-contained: no build step, no network calls |
| [**Routing: own it or call it**](COPILOT-ROUTING.md) | Why ownership decides the architecture before cost does, why residency then picks the storage mode, and the decision tree in full |
| [**Assumptions**](ASSUMPTIONS.md) | Every decision taken on the reader's behalf, and what would change if it were wrong |
| [**Go-live readiness**](GO-LIVE-READINESS.md) | Every feature in the direct-push path at v1.5.0 — what is built, what is part-built, what is not — and the six verification tasks between the current release and a supported service |

## Deploying

| | |
|---|---|
| [**What a source must guarantee**](SOURCE-CONTRACT.md) | The four hard requirements a source has to meet before a direct push can detect deletions, skip unchanged items and resume — and what a source that meets only some of them still gets |
| [**What we need from the CDP team**](CDP-PILOT-PARAMETERS.md) | The parameters to collect before a pilot — what is asked, why, and a column to answer in. Opens by deciding **which of the three connectors** the pilot needs, then marks every question with the pieces it serves, so a customer running one of them answers a third of the sheet |
| [**What we need from the SQL team**](SQL-PILOT-PARAMETERS.md) | The same, for a SQL Server source — covering both the agent-hosted and direct-push paths |
| [**Production onboarding**](PRODUCTION-ONBOARDING.md) | The other half of go-live readiness: who owns the connection, who is woken when a run fails, and which numbers somebody has to accept in writing — every row named and owned |
| [**How the items actually appear**](COPILOT-SURFACING.md) | Result types, verticals and activities: what Microsoft Search renders from, what Copilot renders from instead, which half has a Graph API and which needs a Search Administrator — and why a timesheet database cannot produce activity signals |
| [CDP connector](CDP-DEPLOYMENT.md) | HDFS documents, Hive tables and the Atlas catalogue, from a Kerberised cluster |
| [Hierarchy connector](HIERARCHY-DEPLOYMENT.md) | The worked three-level example, flattened for a flat index |
| [Crawl state database](CRAWL-STATE-DEPLOYMENT.md) | Standing up `ConnectorState`: the six state-database scripts in order, the two service accounts, retention, and the delete guard an operator has to know before the first refusal — plus `sql/26`, the seventh, which changes the source rather than the state |
| [App registration](APP-REGISTRATION.md) | Entra setup, certificate auth, and the permissions each path actually needs |
| [Runbook](RUNBOOK.md) | Scheduling, certificate rotation, the ACL staleness bound, and what each exit code means |
| [**Disaster recovery**](DISASTER-RECOVERY.md) | What is actually lost per table and the RPO that supports, why the recovery objective here is a *security* number rather than an availability one, rebuilding on a replacement host, re-provisioning the Entra credential that no backup contains — and the record of the restore rehearsal |
| [**Upgrade and rollback**](UPGRADE-RUNBOOK.md) | v1.4 → v1.5 script by script, how to back out, and the additive-only rule that makes rollback possible — including the one migration that currently breaks it |
| [**Alerting**](ALERTING.md) | Why a dead connector is a security incident rather than an outage, the watch that detects it, and the paging matrix — which conditions wake somebody at 03:00, which wait for morning, which are dashboard-only, and which nothing on the host can detect at all |
| [**Scheduling**](SCHEDULING.md) | How to schedule incremental crawls and what they cost in deletion latency; several connectors on one host serialised behind one queue so their crawls cannot stack; and the weekly reconciliation, with the exit codes that page and the one that must not |
| [**Telemetry**](TELEMETRY.md) | The span tree and the eleven instruments every run emits, why they cost nothing when nobody is listening, how to send them to a collector, and the two decisions behind them — delta temporality, and why the exception message never reaches a span |
| [**Sensitivity labels**](SENSITIVITY-LABELS.md) | Mapping a source's own classifications to a label, publishing it, and declining to index what the mapping says must not be — including the four things it cannot see, and why refusal is the only closed option available |

## Reviewing

| | |
|---|---|
| [Security control mapping](SECURITY.md) | Every control, where it is implemented, and the test that proves it |
| [Crawl state reference](CRAWL-STATE-REFERENCE.md) | Every table, view and procedure in the state database, with columns, parameters and error numbers |
| [**Capacity planning**](CAPACITY-PLANNING.md) | Will this still work at ten times the corpus? Graph's published ceilings and the one it does not publish, this rig's measured throughput and storage per item, what scales linearly and what has stopped, and the five queries that produce another estate's own version of these numbers |
| [Adding a connector](ADDING-A-PUSH-CONNECTOR.md) | The source seam, and what a new source has to supply |

## Generated documents

Two documents here are **built from source in this repository** rather than
hand-edited, so that they cannot drift out of date invisibly. Edit the source,
re-run the generator, and the output regenerates.

| Source | Generator | Produces |
|---|---|---|
| [`guides/deployment-and-test-guide.html`](guides/deployment-and-test-guide.html) | `guides/New-DeploymentGuide.ps1` | A 22-page Word guide to deploying and testing this connector **from no prior knowledge** — vocabulary, prerequisites, every command explained, eleven tests in order, eleven traps, and troubleshooting indexed by symptom |
| the layout inside `diagrams/New-ArchitectureDiagram.ps1` | the same script | [`architecture.svg`](architecture.svg) and [`architecture.png`](architecture.png), from one declared layout so the two cannot disagree |

Both need Word or `System.Drawing` on the machine, are documentation builds
rather than part of `Build.ps1`, and nothing in CI depends on either. Their
output is written **beside** the repository, not into it: a binary that changes
wholesale on every rebuild makes every diff useless.

## Troubleshooting

[CDP](TROUBLESHOOTING-CDP.md) · [direct push](TROUBLESHOOTING-DIRECT-PUSH.md) ·
[agent-hosted](TROUBLESHOOTING.md)

---

<p style="color:#57606a;font-size:.9em">
Sample data throughout is fictional — Contoso, Northwind and Consultco names, and
<code>corp.example</code> hosts. Nothing here describes a real customer's cluster.
</p>
