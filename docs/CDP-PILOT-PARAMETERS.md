# What we need from the CDP team

**Purpose.** Everything the Copilot connector needs in order to reach your
cluster and run a proof-of-concept pilot, in the order the answers are needed.
Fill in the right-hand column and send it back — nothing here needs a meeting.

**Nothing in this document is a secret, and none of it should be.** No password,
keytab, certificate or token is requested anywhere below, and there is no field
in the connector's configuration to put one in: it authenticates as a group
Managed Service Account over Kerberos, and Active Directory owns that password.
If a question here looks like it is asking for a credential, it is not — please
query it rather than answering it.

**You do not have to answer all of it.** The connector is three independent
pieces — **HDFS documents**, **Hive contracts** and the **Atlas catalogue** —
and a pilot may run any one of them on its own. **Section 0 decides which**, and
the **Needed for** column on every question says which pieces it serves.
Nineteen of the twenty-nine questions apply whichever you choose; beyond those,
HDFS adds three, Hive five and Atlas two. Leave the rest blank.

Example values are illustrative and use the reserved `corp.example` domain.

---

## 0 · Which connector do you need?

**Answer this section first — it decides which of the rest apply.**

CDP is a stack rather than a set of alternatives, and the three connectors read
different layers of it:

| Layer | What it is | Connector |
|---|---|---|
| **HDFS** | The distributed filesystem. Everything ultimately lives here | **HDFS documents** — for files that are genuinely documents |
| **Hive** | Schema and SQL *over* HDFS. A table is a directory of files, plus an entry in the Metastore saying those files have columns | **Hive contracts** — for the rows |
| **Atlas** | The catalogue: table names, columns, lineage, classifications. Metadata *about* the data, not the data | **Atlas catalogue** — for "which table holds X" |

So "our cluster is mostly tables" and "our cluster acts like a filesystem" are
both true at once and describe the same estate from two layers. What decides the
connector is which layer holds the content you want people to find.

| If the content is | You want | |
|---|---|---|
| Rows in Hive tables | **Hive contracts** | The common case |
| Real documents on HDFS — PDFs, Office files, reports in a landing zone | **HDFS documents** | Only if such files exist |
| A searchable data dictionary | **Atlas catalogue** | Worth running *alongside* Hive rather than instead of it |

**One trap worth naming.** Pointing the HDFS connector at the Hive warehouse
directory does not index your tables. The files there are Parquet or ORC —
binary columnar formats from which no text is extracted — so the crawl produces
items named `part-00000-a3f2b1c8.snappy.parquet` carrying no content, and looks
like it worked. On CDP 7.1.9 managed tables are transactional as well, so the
directory holds `base_` and `delta_` subdirectories that mean nothing until
they are merged. Tables are read through Hive, always.

**There is a security reason for the same rule.** Two different Ranger services
decide who may read the same bytes: `cm_hive` governs `select` on the table,
`cm_hdfs` governs read on the underlying files. They routinely disagree, and
deliberately — many clusters grant `select` broadly and direct file access to
almost nobody. Column masking and row filtering exist **only** in the Hive
layer, so reading the files directly bypasses both and returns the unmasked
column. This connector refuses to read a Hive table carrying a mask or a row
filter at all, for the same reason.

| # | Needed for | What we need | Why | Your answer |
|---|---|---|---|---|
| 0.1 | All | Which pieces the pilot should run — **HDFS documents**, **Hive contracts**, **Atlas catalogue**, or a combination | This routes every other question on the sheet. It is also reversible: adding a piece later costs configuration rather than redesign | |
| 0.2 | All | The output of `DESCRIBE FORMATTED <database>.<table>` for **one representative table** | Three fields settle the choice. `Table Type` — managed or external. `Location` — inside the warehouse or not. `InputFormat` and `SerDe` — Parquet, ORC or text. A warehouse path holding Parquet or ORC means Hive is the only sensible route. An external table over CSV or JSON is the one case where both connectors are technically viable, and worth a conversation rather than a guess | |

## 1 · Identity and trust

**Ask for these next.** Sections 2 to 5 can then be answered in any order; these
gate everything, and two of them go through other people's queues.

| # | Needed for | What we need | Why | Your answer |
|---|---|---|---|---|
| 1.1 | All | The **Kerberos realm** name, and whether it **trusts our Active Directory domain** | Everything cluster-side is Kerberos over SSPI as the service account. Without a cross-realm trust — or a cluster whose Kerberos is AD-integrated — no ticket we present is accepted and the pilot cannot begin. If there is no trust, say so early: the fallback puts a keytab on the connector host, which is a decision to record rather than a default | |
| 1.2 | All | Confirmation the service account is granted **read on the HDFS paths** *(HDFS)*, **`select` on the Hive object** *(Hive)*, and **entity-read in the `cm_atlas` Ranger service** *(Atlas)* | Answer only the clauses for the pieces you are running. The Atlas grant is the one most often missed. Ranger's Hive service says nothing about who may read the *catalogue* — that is a separate service. Without it Atlas answers 403 and the run exits 3 | |
| 1.3 | All | The **cluster group names** that should see this content | The connector mirrors **group** grants only, never per-user ones. Every group Ranger names has to be mapped; an unmapped group means the item is granted to nobody and is silently skipped | |
| 1.4 | All | The **Entra group object ID** for each of those groups | Group names alone are not enough — the object ID is what is written onto each indexed item's access list. Reading it needs Microsoft 365 tenant access, so it may not be yours to answer | |

## 2 · Endpoints

| # | Needed for | What we need | Why | Your answer |
|---|---|---|---|---|
| 2.1 | HDFS | **HttpFS / WebHDFS base URL**, ending `/webhdfs/v1` | Must be **https** — the connector refuses plain http. Please give the **TLS** port specifically: 14000 is the plaintext default, and pairing `https://` with it is the most common first-run failure | |
| 2.2 | Hive | **HiveServer2 host, port and transport mode** — binary or HTTP | CDP's default is *binary* on 10000; our shipped configuration assumes *HTTP* on 10001. Getting this explicitly avoids a first connection that simply hangs. HTTP mode also needs the HTTP path, usually `cliservice` | |
| 2.3 | Hive | The **Hive service principal name** (usually `hive`), and whether TLS is enabled | A wrong service principal is rejected with a message that reads like a network fault | |
| 2.4 | All | **Ranger Admin base URL** | The TLS port, 6182 rather than 6080. A Ranger we cannot read is deliberately fatal: the run stops rather than indexing content whose access policies it could not check. Every piece reads Ranger, including Atlas | |
| 2.5 | Atlas | **Atlas base URL, without the `/api/atlas` suffix** | The port varies by topology — 31443 in a stock install, different again behind Knox. There is deliberately no default, because a wrong guess fails at the least helpful moment | |
| 2.6 | All | The **exact Ranger service names** for the pieces in scope — HDFS, Hive and Atlas | `cm_hdfs`, `cm_hive` and `cm_atlas` are Cloudera Manager's defaults but are not guaranteed. A wrong name stops the run naming the service, so this is cheap to get wrong — and cheaper to ask | |

## 3 · Scope of the pilot

| # | Needed for | What we need | Why | Your answer |
|---|---|---|---|---|
| 3.1 | HDFS | The **HDFS paths** in scope, absolute | Keep a pilot to one or two directories. This bounds both the cost and the blast radius of getting something wrong | |
| 3.2 | Hive | The **Hive table or view**, as `database.object` | A **view you control** is preferable to a base table: it puts the filtering somewhere a DBA can read | |
| 3.3 | Hive | A **watermark column** and a **key column** on it | An ascending timestamp and a stable key are what make incremental runs possible. Without them every run re-reads everything. HDFS needs no answer here — it uses each file's modification time and path as the equivalent pair | |
| 3.4 | All | Rough **row and file counts** | Sets the item budget, and tells us whether the pilot approaches the tenant's Copilot item quota before any schema is designed. Weigh it against 3.7 | |
| 3.5 | Atlas | Which **Atlas entity types** to catalogue | `hive_db` and `hive_table` are the useful pair. Please note that `hdfs_path` will catalogue **nothing** — see the note at the end | |
| 3.6 | HDFS | What **file types** are in the paths at 3.1, roughly, by count | Text is extracted from **PDF** and the **OpenXML family** — `.docx`, `.docm`, `.xlsx`, `.xlsm`, `.pptx`, `.pptm` — and read directly from plain text. Everything else is indexed by **name and metadata only**: the legacy binary formats (`.doc`, `.xls`, `.ppt`), `.msg`, archives, images, and any PDF that is a scan rather than text, because there is no OCR. A pilot directory of scanned PDFs indexes filenames and looks like a failure | |
| 3.7 | All | The tenant's **remaining Copilot connector item quota**, if anyone can read it | The quota is **licensed, tenant-wide and shared** with every other connector in the tenant, so a pilot competes with connectors you may not own. It is readable only from `connectionQuota.itemsRemaining` on the Graph **beta** endpoint, and nothing in this connector watches it — you learn you were wrong through error 1008 or 1009, mid-crawl. **Probably not a CDP question:** it needs Microsoft 365 tenant access. Ask early anyway | |

## 4 · Network and host

**The other long pole.** Firewall changes are rarely quick, so please start
these in parallel with everything else.

| # | Needed for | What we need | Why | Your answer |
|---|---|---|---|---|
| 4.1 | All | Firewall openings from the connector host to the services in scope — **HttpFS** *(HDFS)*, **HiveServer2** *(Hive)*, **Atlas** *(Atlas)* — and to **Ranger**, always | Ranger is not optional for any piece, so at least two openings are needed whichever you choose | |
| 4.2 | All | Outbound **443** from that host to `login.microsoftonline.com` and `graph.microsoft.com` | This connector pushes to Microsoft Graph directly rather than through an on-premises agent, so the host itself needs to reach Microsoft | |
| 4.3 | All | Whether **Knox** fronts any of these services | Knox changes every URL to a gateway path and port | |
| 4.4 | All | Confirmation the **cluster's CA is trusted** by the Windows host | Every endpoint above is TLS, and the connector will not disable certificate validation | |
| 4.5 | Hive | Whether the **Cloudera Hive ODBC driver** may be installed on the connector host, and which version | The Hive connector reaches HiveServer2 through ODBC, and the driver's registered name goes into `Settings:HiveDriver` verbatim. It is a **Cloudera download with its own licence terms**, installed on the Windows host rather than on the cluster, so it usually needs a different approval from everything else here. HDFS and Atlas need no driver | |
| 4.6 | All | A **SQL Server database** the connector host can reach, for the crawl state store | Recommended rather than required. Without `Settings:StateConnectionString` the connector still crawls, but there is **no run history, no deletion detection and no dashboard** — which is most of what makes a pilot legible to anyone who was not watching the console. Express edition is enough. It holds **no cluster content**: only item IDs, hashes and run rows. See [crawl state deployment](CRAWL-STATE-DEPLOYMENT.md) | |

## 5 · Ranger policy model

These decide whether six findings from our policy-fidelity review are real work
or inert on your cluster — and whether the connector will refuse to run at all.

| # | Needed for | What we need | Why | Your answer |
|---|---|---|---|---|
| 5.1 | All | Do you use **Ranger Security Zones**? If so, which services and resources does each cover? | The connector **refuses to run** against a service whose policies carry a zone name, rather than reading them zone-blind. Ranger evaluates a zoned resource against that zone's policies only; reading them together would grant indexed content to people the cluster refuses | |
| 5.2 | All | Do you use **tag-based policies** — any policy on `cm_tag`, and is Tagsync running against Atlas? | A mask or deny written against an Atlas classification lives on the tag service. We read only the resource services, so a tag policy is invisible to us and a PII-tagged column would be indexed unmasked | |
| 5.3 | All | Does any policy use **allow-exceptions, validity periods, or item conditions**? | On a policy edit screen: *Exclude from Allow Conditions*, *Validity Period*, *Deny All Other Accesses*. Each is currently read as though absent, which grants more widely than you intend | |
| 5.4 | All | The **policy count** shown in Ranger Admin for **each service named at 2.6** | We compare it against what the connector reports reading. A round number on our side — 200 is the stock page size — against a larger number on yours means the policy list was being truncated. The truncation is a paging limit rather than a Hive one, so it is worth a count for every service in scope | |

---

## Two notes worth reading before you answer

**`hdfs_path` in Atlas will catalogue nothing (3.5).** A path entry is decided
against the Hadoop SQL policies, and an Atlas `hdfs_path` names itself as a URI
that no Hive policy matches — so nobody is granted and nothing is indexed. It
fails closed rather than wrongly, but it is not a way to catalogue the
filesystem. The HDFS connector is.

**A "yes" to 5.1 stops the pilot until it is designed for.** That is deliberate.
The connector treats a policy set it cannot interpret faithfully the same way it
treats a Ranger it cannot reach at all: it stops, rather than indexing under a
guess. If zones are in use, tell us before the first run rather than after it.

---

*Related: [CDP deployment guide](CDP-DEPLOYMENT.md) · [security control mapping](SECURITY.md) · [troubleshooting](TROUBLESHOOTING-CDP.md)*
