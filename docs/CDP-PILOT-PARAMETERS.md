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

Example values are illustrative and use the reserved `corp.example` domain.

---

## 1 · Identity and trust

**Ask for these first.** Sections 2 to 5 can be answered in any order; these
gate everything, and two of them go through other people's queues.

| # | What we need | Why | Your answer |
|---|---|---|---|
| 1.1 | The **Kerberos realm** name, and whether it **trusts our Active Directory domain** | Everything cluster-side is Kerberos over SSPI as the service account. Without a cross-realm trust — or a cluster whose Kerberos is AD-integrated — no ticket we present is accepted and the pilot cannot begin. If there is no trust, say so early: the fallback puts a keytab on the connector host, which is a decision to record rather than a default | |
| 1.2 | Confirmation the service account is granted **read on the HDFS paths**, **`select` on the Hive object**, and **entity-read in the `cm_atlas` Ranger service** | The Atlas grant is the one most often missed. Ranger's Hive service says nothing about who may read the *catalogue* — that is a separate service. Without it Atlas answers 403 and the run exits 3 | |
| 1.3 | The **cluster group names** that should see this content | The connector mirrors **group** grants only, never per-user ones. Every group Ranger names has to be mapped; an unmapped group means the item is granted to nobody and is silently skipped | |
| 1.4 | The **Entra group object ID** for each of those groups | Group names alone are not enough — the object ID is what is written onto each indexed item's access list | |

## 2 · Endpoints

| # | What we need | Why | Your answer |
|---|---|---|---|
| 2.1 | **HttpFS / WebHDFS base URL**, ending `/webhdfs/v1` | Must be **https** — the connector refuses plain http. Please give the **TLS** port specifically: 14000 is the plaintext default, and pairing `https://` with it is the most common first-run failure | |
| 2.2 | **HiveServer2 host, port and transport mode** — binary or HTTP | CDP's default is *binary* on 10000; our shipped configuration assumes *HTTP* on 10001. Getting this explicitly avoids a first connection that simply hangs. HTTP mode also needs the HTTP path, usually `cliservice` | |
| 2.3 | The **Hive service principal name** (usually `hive`), and whether TLS is enabled | A wrong service principal is rejected with a message that reads like a network fault | |
| 2.4 | **Ranger Admin base URL** | The TLS port, 6182 rather than 6080. A Ranger we cannot read is deliberately fatal: the run stops rather than indexing content whose access policies it could not check | |
| 2.5 | **Atlas base URL, without the `/api/atlas` suffix** | The port varies by topology — 31443 in a stock install, different again behind Knox. There is deliberately no default, because a wrong guess fails at the least helpful moment | |
| 2.6 | The **exact Ranger service names** for HDFS, Hive and Atlas | `cm_hdfs`, `cm_hive` and `cm_atlas` are Cloudera Manager's defaults but are not guaranteed. A wrong name stops the run naming the service, so this is cheap to get wrong — and cheaper to ask | |

## 3 · Scope of the pilot

| # | What we need | Why | Your answer |
|---|---|---|---|
| 3.1 | The **HDFS paths** in scope, absolute | Keep a pilot to one or two directories. This bounds both the cost and the blast radius of getting something wrong | |
| 3.2 | The **Hive table or view**, as `database.object` | A **view you control** is preferable to a base table: it puts the filtering somewhere a DBA can read | |
| 3.3 | A **watermark column** and a **key column** on it | An ascending timestamp and a stable key are what make incremental runs possible. Without them every run re-reads everything | |
| 3.4 | Rough **row and file counts** | Sets the item budget, and tells us whether the pilot approaches the tenant's Copilot item quota before any schema is designed | |
| 3.5 | Which **Atlas entity types** to catalogue | `hive_db` and `hive_table` are the useful pair. Please note that `hdfs_path` will catalogue **nothing** — see the note at the end | |

## 4 · Network and host

**The other long pole.** Firewall changes are rarely quick, so please start
these in parallel with everything else.

| # | What we need | Why | Your answer |
|---|---|---|---|
| 4.1 | Firewall openings from the connector host to **HttpFS, HiveServer2, Ranger and Atlas** | The host has to reach all four | |
| 4.2 | Outbound **443** from that host to `login.microsoftonline.com` and `graph.microsoft.com` | This connector pushes to Microsoft Graph directly rather than through an on-premises agent, so the host itself needs to reach Microsoft | |
| 4.3 | Whether **Knox** fronts any of these services | Knox changes every URL to a gateway path and port | |
| 4.4 | Confirmation the **cluster's CA is trusted** by the Windows host | All four endpoints are TLS, and the connector will not disable certificate validation | |

## 5 · Ranger policy model

These decide whether six findings from our policy-fidelity review are real work
or inert on your cluster — and whether the connector will refuse to run at all.

| # | What we need | Why | Your answer |
|---|---|---|---|
| 5.1 | Do you use **Ranger Security Zones**? If so, which services and resources does each cover? | The connector **refuses to run** against a service whose policies carry a zone name, rather than reading them zone-blind. Ranger evaluates a zoned resource against that zone's policies only; reading them together would grant indexed content to people the cluster refuses | |
| 5.2 | Do you use **tag-based policies** — any policy on `cm_tag`, and is Tagsync running against Atlas? | A mask or deny written against an Atlas classification lives on the tag service. We read only the resource services, so a tag policy is invisible to us and a PII-tagged column would be indexed unmasked | |
| 5.3 | Does any policy use **allow-exceptions, validity periods, or item conditions**? | On a policy edit screen: *Exclude from Allow Conditions*, *Validity Period*, *Deny All Other Accesses*. Each is currently read as though absent, which grants more widely than you intend | |
| 5.4 | The **policy count** shown in Ranger Admin for the Hive service | We compare it against what the connector reports reading. A round number on our side — 200 is the stock page size — against a larger number on yours means the policy list was being truncated | |

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
