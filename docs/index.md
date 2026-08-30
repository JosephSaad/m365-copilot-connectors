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
| [**What we need from the CDP team**](CDP-PILOT-PARAMETERS.md) | The parameters to collect before a pilot — what is asked, why, and a column to answer in |
| [**What we need from the SQL team**](SQL-PILOT-PARAMETERS.md) | The same, for a SQL Server source — covering both the agent-hosted and direct-push paths |
| [**Production onboarding**](PRODUCTION-ONBOARDING.md) | The other half of go-live readiness: who owns the connection, who is woken when a run fails, and which numbers somebody has to accept in writing — every row named and owned |
| [CDP connector](CDP-DEPLOYMENT.md) | HDFS documents, Hive tables and the Atlas catalogue, from a Kerberised cluster |
| [Hierarchy connector](HIERARCHY-DEPLOYMENT.md) | The worked three-level example, flattened for a flat index |
| [Crawl state database](CRAWL-STATE-DEPLOYMENT.md) | Standing up `ConnectorState`: the six state-database scripts in order, the two service accounts, retention, and the delete guard an operator has to know before the first refusal — plus `sql/26`, the seventh, which changes the source rather than the state |
| [App registration](APP-REGISTRATION.md) | Entra setup, certificate auth, and the permissions each path actually needs |
| [Runbook](RUNBOOK.md) | Scheduling, certificate rotation, the ACL staleness bound, and what each exit code means |
| [**Scheduling**](SCHEDULING.md) | How to schedule incremental crawls and what they cost in deletion latency; several connectors on one host serialised behind one queue so their crawls cannot stack; and the weekly reconciliation, with the exit codes that page and the one that must not |

## Reviewing

| | |
|---|---|
| [Security control mapping](SECURITY.md) | Every control, where it is implemented, and the test that proves it |
| [Crawl state reference](CRAWL-STATE-REFERENCE.md) | Every table, view and procedure in the state database, with columns, parameters and error numbers |
| [**Capacity planning**](CAPACITY-PLANNING.md) | Will this still work at ten times the corpus? Graph's published ceilings and the one it does not publish, this rig's measured throughput and storage per item, what scales linearly and what has stopped, and the five queries that produce another estate's own version of these numbers |
| [Adding a connector](ADDING-A-PUSH-CONNECTOR.md) | The source seam, and what a new source has to supply |

## Troubleshooting

[CDP](TROUBLESHOOTING-CDP.md) · [direct push](TROUBLESHOOTING-DIRECT-PUSH.md) ·
[agent-hosted](TROUBLESHOOTING.md)

---

<p style="color:#57606a;font-size:.9em">
Sample data throughout is fictional — Contoso, Northwind and Consultco names, and
<code>corp.example</code> hosts. Nothing here describes a real customer's cluster.
</p>
