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
| [**Copilot decision matrix**](copilot-decision-matrix.html) | Fourteen questions that route one source to one delivery path, with the cost and the warnings attached. Self-contained — no build step, no network calls |
| [**Routing: own it or call it**](COPILOT-ROUTING.md) | Why ownership decides the architecture before cost does, and the decision tree in full |
| [**Assumptions**](ASSUMPTIONS.md) | Every decision taken on the reader's behalf, and what would change if it were wrong |

## Deploying

| | |
|---|---|
| [**What we need from the CDP team**](CDP-PILOT-PARAMETERS.md) | The parameters to collect before a pilot — what is asked, why, and a column to answer in |
| [**What we need from the SQL team**](SQL-PILOT-PARAMETERS.md) | The same, for a SQL Server source — covering both the agent-hosted and direct-push paths |
| [**Production onboarding**](PRODUCTION-ONBOARDING.md) | What has to be true before either connector is a supported service rather than a pilot that happens to be running |
| [CDP connector](CDP-DEPLOYMENT.md) | HDFS documents, Hive tables and the Atlas catalogue, from a Kerberised cluster |
| [Hierarchy connector](HIERARCHY-DEPLOYMENT.md) | The worked three-level example, flattened for a flat index |
| [App registration](APP-REGISTRATION.md) | Entra setup, certificate auth, and the permissions each path actually needs |
| [Runbook](RUNBOOK.md) | Scheduling, certificate rotation, the ACL staleness bound, and what each exit code means |

## Reviewing

| | |
|---|---|
| [Security control mapping](SECURITY.md) | Every control, where it is implemented, and the test that proves it |
| [Adding a connector](ADDING-A-PUSH-CONNECTOR.md) | The source seam, and what a new source has to supply |

## Troubleshooting

[CDP](TROUBLESHOOTING-CDP.md) · [direct push](TROUBLESHOOTING-DIRECT-PUSH.md) ·
[agent-hosted](TROUBLESHOOTING.md)

---

<p style="color:#57606a;font-size:.9em">
Sample data throughout is fictional — Contoso, Northwind and Consultco names, and
<code>corp.example</code> hosts. Nothing here describes a real customer's cluster.
</p>
