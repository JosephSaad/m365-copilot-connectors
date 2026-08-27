# What we need from the SQL and platform teams

**Purpose.** Everything needed to reach a SQL Server source and run a
proof-of-concept pilot, for **both** delivery paths — the agent-hosted connector
and the direct push. Fill in the right-hand column and send it back.

**Nothing here is a secret, and nothing should be.** Where the source needs a
SQL login rather than Windows authentication, we ask for the **name of a Key
Vault secret**, never the password itself — the connector resolves it at
runtime and never writes it anywhere. No certificate file, keytab or token is
requested on any row below. If a question looks like it is asking for a
credential, please query it rather than answering it.

Example values use the reserved `contoso.local` domain.

---

## 0 · Which path — answer this first

The two paths differ in what they can promise, not in what Copilot does with the
result. An item pushed directly is indistinguishable in the index from one the
agent crawled; the difference is everything around the write.

| | **Agent-hosted** | **Direct push** |
|---|---|---|
| Needs a Windows host | **Yes** — runs the Graph connector agent | No — anywhere with outbound HTTPS |
| Deletes removed records | **Yes**, on the next incremental crawl | **Never** — items stay until removed by hand |
| Scheduling | The admin centre runs it | You own the timer |
| Health visible in the admin centre | **Yes** | No — only in your own logs |
| Change detection | The agent hashes and skips unchanged rows | You send everything, or build your own |
| Reaches Microsoft from | The agent host only | Whatever host runs the push |

| # | What we need | Why | Your answer |
|---|---|---|---|
| 0.1 | **How fast must a deleted record stop appearing in Copilot?** | This is the question that decides the path, and it is usually answered too casually. Anything tighter than "by the next crawl" rules out direct push entirely, because a direct push never deletes | |
| 0.2 | **Is there a Windows host available** to run the agent, and who patches it? | Agent-hosted is only an option if the answer is yes. If not, the deletion answer above has to be renegotiated rather than engineered around | |
| 0.3 | Is this a **one-off backfill or proof of concept**, or a standing sync? | Direct push is the right tool for backfills, smoke tests and proving the tenant. It is the wrong tool for a standing sync, for the reasons in the table above | |

## 1 · The SQL source — needed for both paths

| # | What we need | Why | Your answer |
|---|---|---|---|
| 1.1 | **Server** and **database** name | The connection target. A named instance or non-default port should be given in full | |
| 1.2 | The **view** to read, as `schema.object` | We ask for a **view, not a table**. It puts the column selection, the joins and the soft-delete filter somewhere a DBA can read and `EXPLAIN`, and it lets the grant be on the view alone so the reader cannot see the base tables | |
| 1.3 | A **stable key column** and a **watermark column** (a `datetime2` maintained by the application) | The key identifies an item across runs; the watermark is what makes a run incremental. Without a reliable ascending timestamp, every run re-reads everything | |
| 1.4 | Whether rows are **soft-deleted** (a flag) or hard-deleted | A soft-delete flag inside the view is what lets removals be detected at all. Hard deletes are invisible to any query-based crawl | |
| 1.5 | Approximate **row count**, and growth per month | Sets the item budget and tells us whether the pilot approaches the tenant's Copilot item quota before any schema is designed | |
| 1.6 | A **URL template** for one record, e.g. `https://tickets.contoso.com/ticket/{0}` | Copilot cites the item by URL. Without a deep link the citation goes nowhere, which is usually noticed only after go-live | |
| 1.7 | Which columns are **free text**, and which are **filterable** values | Text is what gets matched semantically; values become refiners. Getting this wrong is expensive: a registered schema is append-only and cannot be corrected without deleting the connection and every item in it | |

## 2 · How the connector authenticates to SQL

| # | What we need | Why | Your answer |
|---|---|---|---|
| 2.1 | **Windows authentication or a SQL login?** | Windows integrated is strongly preferred: the service account is a gMSA whose password Active Directory owns and rotates, so no credential exists in the deployment to leak or expire | |
| 2.2 | If Windows: the **service account** to grant, and confirmation it has `SELECT` **on the view only** | Least privilege, and it is what makes 1.2's "grant on the view" real | |
| 2.3 | If a SQL login: the **Key Vault URI** and the **name of the secret** holding the password | We need the secret's *name*, never its value. Please do not send the password — the connector reads it from the vault at runtime, caches it in memory only, and re-reads on an authentication failure | |
| 2.4 | Any **required connection options** — encryption, a named instance, a non-default port | These go into the connection string. Note we will not set `TrustServerCertificate=true`; if the server's certificate is not trusted, that needs fixing rather than bypassing | |

## 3 · Who should see the data

| # | What we need | Why | Your answer |
|---|---|---|---|
| 3.1 | The **Entra group object IDs** that should be able to find this content | Every indexed item carries an access list of Entra groups. Group names are not enough — the object ID is what is written onto the item | |
| 3.2 | Is access **uniform across all rows**, or does it vary per row? | If different people should see different rows, an index is the wrong shape: one stored copy cannot represent a per-user view. That case needs a live query instead, and is better known now than after the schema is built | |
| 3.3 | Is any of this content **licensed from a third party**? | Indexing licensed content is a redistribution and entitlement event. If the answer is yes, we need to see the agreement's redistribution, derived-data and AI-use clauses before designing anything | |

## 4 · Path A — agent-hosted only

Skip this section if 0.1 and 0.2 pointed at direct push.

| # | What we need | Why | Your answer |
|---|---|---|---|
| 4.1 | The **Windows host** for the Graph connector agent, and its patching owner | The agent is a service on a machine somebody has to own. This is the cost the direct push avoids and the capability it gives up | |
| 4.2 | Confirmation that only **that host** needs outbound 443 to Microsoft | The connector process talks to the agent over loopback only; the agent alone reaches Microsoft. On a locked-down network this is the main argument for this path — one firewall case rather than one per push host | |
| 4.3 | A **TLS certificate** for the loopback listener, by thumbprint from the machine store | The connector and the agent talk gRPC over localhost with TLS. We need the thumbprint of a certificate already installed — not the certificate itself | |
| 4.4 | The **crawl schedule** you want, full and incremental | The admin centre runs these. Incremental can go down to every 15 minutes; the deletion SLA from 0.1 has to fit inside whatever is chosen | |

## 5 · Path B — direct push only

Skip this section if 0.1 and 0.2 pointed at agent-hosted.

| # | What we need | Why | Your answer |
|---|---|---|---|
| 5.1 | An **Entra app registration**: tenant ID, client ID | The push authenticates to Graph as an application. It needs `ExternalConnection.ReadWrite.OwnedBy` and `ExternalItem.ReadWrite.OwnedBy`, admin-consented | |
| 5.2 | A **certificate** for that app — the **thumbprint**, and which store it is installed in | Certificate authentication, not a client secret. We need the thumbprint of an installed certificate; the private key never leaves the machine store. If a client secret is unavoidable, it goes in Windows Credential Manager by target *name* | |
| 5.3 | Which **host** will run the push, and on what schedule | Direct push has no scheduler of its own. Something has to invoke it, and somebody has to notice when it stops | |
| 5.4 | Agreement on **who reconciles orphaned items** | A direct push never deletes. Rows that leave scope keep their items in the index indefinitely. This needs a named owner and a periodic job, agreed before the first run rather than after the first audit | |
| 5.5 | A **connection ID** for the Graph connection | Lowercase alphanumeric, fixed for the life of the connection. Changing it later means a new connection and a full re-push | |

## 6 · Network

| # | What we need | Why | Your answer |
|---|---|---|---|
| 6.1 | Firewall opening from the connector host to **SQL Server**, port confirmed | The obvious one, and still worth writing down | |
| 6.2 | Outbound **443** to `login.microsoftonline.com` and `graph.microsoft.com` — from the **agent host** (Path A) or the **push host** (Path B) | Which host needs it is the practical difference between the two paths on a restricted network | |
| 6.3 | Confirmation the **SQL Server certificate is trusted** by the connector host | The connection is encrypted and certificate validation is not disabled | |

---

## Three notes worth reading before you answer

**0.1 decides the architecture, not the schedule.** "How fast must a deleted
record disappear" sounds operational and is structural: direct push never
deletes at all, and the agent deletes only on its next incremental crawl. A
same-day answer forces the agent-hosted path even where there is no host to run
it on — in which case the SLA, not the design, is what has to change.

**A registered schema is append-only (1.7).** No property's type, annotation or
label can be changed after registration. Correcting a mistake means deleting the
connection and every item in it. Fifteen minutes of care over which columns are
searchable and which are filterable is the cheapest fifteen minutes in the
project.

**Please do not attach credentials to your reply (2.3, 4.3, 5.2).** Every row
that touches authentication asks for a *reference* — a secret's name, a
certificate's thumbprint, an account to grant. A parameter request is exactly the
shape of document somebody helpfully attaches a keytab or a password to, and we
would then have to treat it as an incident.

---

*Related: [app registration](APP-REGISTRATION.md) · [hierarchy deployment](HIERARCHY-DEPLOYMENT.md) · [security control mapping](SECURITY.md) · [choosing a path](COPILOT-ROUTING.md)*
