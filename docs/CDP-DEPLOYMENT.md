# Deploying the Cloudera CDP connector

Step-by-step instructions for `CdpGraphPush`, the connector that indexes HDFS
documents, Hive tables and the Apache Atlas catalogue from a Cloudera CDP Private
Cloud Base 7.1.9 cluster into Microsoft 365.

**What you are about to do.** Stand up a service identity the cluster already
trusts, decide from the cluster's own Ranger policies which paths and tables may
be copied into an index at all and which may be described in a catalogue, write
down what each cluster group means in Entra, prove the whole chain with a dry run
that writes nothing, and then let a scheduled task keep it current. Most of the
work is the deciding. The running is three commands.

| | |
|---|---|
| **What it is** | A console tool. **Not** a Windows service, and **not** installed by `Install-Connector.ps1`. |
| **Where it runs** | A Windows Server host inside the network that can reach HttpFS, HiveServer2, Ranger Admin, Atlas **and** `graph.microsoft.com`. |
| **What it needs on the host** | The package, the Cloudera ODBC driver, and a Kerberos identity. The .NET runtime is bundled. |
| **Graph connections** | `cdphdfsdocs`, `cdphivecontracts` and `cdpatlascatalog` — one each, never shared. |
| **Exit codes** | `0` success · `2` configuration invalid · `3` credential rejected · `4` ingestion failed |

If what you want is a **different** Hive table rather than a different cluster,
that is one class and one configuration file:
[`ADDING-A-PUSH-CONNECTOR.md`](ADDING-A-PUSH-CONNECTOR.md) is the recipe, and
`HiveContractsConnector.cs` is the worked example. Deploying it then looks
exactly like the steps below with your own names.

---

## Step 1 — What this connector is, and what it is not

**It pushes straight to Microsoft Graph.** There is no Graph connector agent, no
on-premises agent service, no gateway, and nothing registered in the Microsoft
365 admin centre's connector wizard. The tool reads the cluster, builds items,
and calls `PUT /external/connections/{id}/items/{id}` itself. The agent-hosted
ticket connector in [`../README.md`](../README.md) is a different pipeline
entirely; the two share code, not deployments.
[`architecture.png`](architecture.png) draws both paths, the three sources and
the Ranger gate in front of them on one page.

**It is three connectors, three connections, one executable.**

| Connector | Reads | Connection | Configuration file |
|---|---|---|---|
| `cdphdfsdocs` | Files under `Settings:HdfsRoots`, over HttpFS or WebHDFS | `cdphdfsdocs` | `appsettings.cdphdfsdocs.json` |
| `cdphivecontracts` | Rows of `Source:ItemView`, over ODBC | `cdphivecontracts` | `appsettings.cdphivecontracts.json` |
| `cdpatlascatalog` | Atlas entities of the types in `Settings:AtlasTypes`, over the Atlas REST API | `cdpatlascatalog` | `appsettings.cdpatlascatalog.json` |

All three live beside `CdpGraphPush.exe` and are selected by argument:

```powershell
.\CdpGraphPush.exe --connector cdphdfsdocs
.\CdpGraphPush.exe --connector cdphivecontracts
.\CdpGraphPush.exe --connector cdpatlascatalog
```

The configuration file is chosen by the connector key, so no one of them reads
another's settings, and a file naming a *different* connector's
`Graph:ConnectionId` is rejected at startup rather than allowed to overwrite its
items.

**What the third one indexes.** One external item per Atlas entity — `hive_db`
and `hive_table` by default, `hdfs_path` if you ask for it — carrying the
entity's name, its qualified name, its owner, its description, its columns, its
Atlas classifications, its glossary terms, one dataset hop of lineage each way, and the
time Atlas last recorded it changing. It is a searchable answer to "which table
holds the customer's address, who owns it, and what feeds it", which today is
usually answered by asking around.

**What it is not.** It is not a synchroniser: a push never deletes, so anything
removed at the source stays in the index until somebody removes it (step 11). It
is not a live-query surface: the **rows** of a table Ranger filters or masks are
deliberately not indexed, and reaching them needs a different mechanism (step 3)
— whether that table may be *described* in the catalogue is a separate question,
answered in the access model in step 3 and answered differently. It is not a
permissions engine: it mirrors the grants the cluster already gives, group-only,
and it resolves nothing it was not told about (step 4).

---

## Step 2 — Prerequisites

### A gMSA to run as

Create a group managed service account and install it on the connector host.
Everything the cluster side of this connector does — HttpFS, HiveServer2, Ranger
Admin, Atlas — is Kerberos over SSPI **as the identity the process is already
running as**. There is no keytab, no password prompt, and no field anywhere in
the configuration to put one in.

A gMSA is the point of that design, and the reason is not convenience. Active
Directory owns the password, rotates it on its own schedule, and hands it to the
host's LSA at logon; the connector process never sees it, no operator ever types
it, and there is nothing on disk for a backup or a support bundle to leak. A
password that does not exist in the deployment cannot expire in the deployment,
be pasted into a runbook, or be found in a log.

```powershell
# On a domain controller, once. -PrincipalsAllowedToRetrieveManagedPassword is
# the connector host (or a group containing it) and nothing else.
New-ADServiceAccount -Name svc-cdp -DNSHostName svc-cdp.corp.example `
    -PrincipalsAllowedToRetrieveManagedPassword 'CORP\CdpConnectorHosts'
```

```powershell
# On the connector host.
Install-ADServiceAccount -Identity svc-cdp
Test-ADServiceAccount -Identity svc-cdp
```

The account needs read on the HDFS paths, select on the Hive table, and
entity-read on the Atlas entities it is to catalogue, granted the way every other
cluster identity is granted them — through a group, in Ranger. It needs nothing
else on the cluster and no local administrator rights on the host.

### A realm that trusts the domain

The connector obtains its ticket from Windows. For that ticket to be accepted by
`HTTP/httpfs01...`, `hive/hs2-01...`, `HTTP/atlas01...` and Ranger Admin, the
cluster's Kerberos realm must trust the Active Directory domain — either a
cross-realm trust from the cluster's MIT realm to the AD domain, or a cluster
whose Kerberos is AD-integrated and whose principals live in AD already. AD
integration is the simpler of the two here, because it also makes the cluster's
group names AD group names, which is what step 4 rests on.

If the realm has no trust to AD at all, `Settings:KerberosMode` has an
`MitKeytab` value for that case. It is opt-in for a reason: it puts a keytab —
a secret at rest — on the connector host, which is exactly what the gMSA design
above exists to avoid. Treat it as a decision to be recorded, not a fallback to
reach for, and note that SSPI cannot consume an MIT ticket cache: the two modes
are alternatives, not layers.

### The Cloudera ODBC driver

`cdphivecontracts` talks to HiveServer2 or Impala through ODBC, and the driver
named in `Settings:HiveDriver` must be installed on the connector host from
**Cloudera's own MSI**.

It is not in this package and will not be. The driver is licensed by Cloudera and
redistributing it is theirs to permit, so it is deliberately not bundled, not
vendored, and not fetched by the build. Download it from Cloudera, install it as
an administrator, and confirm the driver name matches the configuration exactly:

```powershell
Get-OdbcDriver -Name '*Hive*', '*Impala*' | Select-Object Name, Platform
```

Install the **64-bit** driver. `CdpGraphPush.exe` is a 64-bit process and cannot
load a 32-bit driver, and the failure reads as a missing driver rather than as an
architecture mismatch.

`cdphdfsdocs` and `cdpatlascatalog` need no driver at all — HDFS and Atlas are
both plain HTTPS.

### Atlas, and the port it answers on

`Settings:AtlasBaseUrl` is required and has **no default**, and that is
deliberate. Atlas answers on `31443` in a stock CDP 7.1.9 install, on `31000`
where TLS is off — which this connector refuses — on `21443` in upstream Atlas,
and on the Knox gateway's own port and path where Knox fronts it. A guessed
default that happens to be wrong produces a connection error at the least helpful
moment, and it produces it against a host the operator was not told to check.
Better to make somebody state the value.

Take it from Cloudera Manager — the Atlas service, its Atlas Server role, and the
web address that role publishes — or from the Knox topology where Knox fronts
Atlas. Give the **base URL only, without `/api/atlas`**: the connector appends
the API path itself, which is also what makes a Knox gateway path work. A URL
that already contains `/api/atlas` is refused at validation, and so is one that
is not `https`.

The `https` rule is not decoration. Kerberos on Atlas requires TLS in CDP, so a
cleartext endpoint cannot authenticate this connector at all; and what travels
back is a description of the shape of the lake — table names, column names,
owners — which is not something to put on the wire in clear.

**The service account needs entity-read in the `cm_atlas` Ranger service.**
Atlas's authorisation is a separate Ranger service from Hadoop SQL, so a grant on
`cm_hive` says nothing about whether this identity may read the catalogue. CDP
ships `cm_atlas` with a policy called `public` that grants every authenticated
user read on every entity; where that policy has been narrowed — and on a
governed cluster it should be — this account has to be inside what remains.
Atlas does not fail a search the caller is not entitled to: it blanks the
entities instead, so an under-granted account produces a catalogue that comes
back nearly empty rather than one that comes back refused.

Atlas is read with **SPNEGO as the account the process runs as**, the same as
Ranger and HttpFS, and the connector holds no password to offer it. Two details
of that exchange are worth knowing before something goes wrong. The client never
sends an `Authorization: Basic` header, because Atlas's filter prefers Basic over
Kerberos and would then reject the run as a bad password rather than negotiate at
all. And the catalogue is read with `GET /api/atlas/v2/search/basic` rather than
the POST form of the same search, because Atlas installs its own CSRF filter in
front of non-`GET` REST calls, and whether that filter demands a header is a
cluster setting this connector cannot see and should not depend on.

Preflight the endpoint from the connector host before configuring anything else:

```powershell
Invoke-WebRequest -UseBasicParsing https://atlas01.corp.example:31443/api/atlas/admin/status
```

`/api/atlas/admin/status` answers **without authentication** and returns `ACTIVE`
on a healthy instance. That is what separates "the host, port or TLS is wrong"
from "Kerberos is wrong", which are otherwise blamed for each other for an
afternoon.

### The Entra app registration and its certificate

Identical to the SQL push tools. An app registration with
`ExternalConnection.ReadWrite.OwnedBy` and `ExternalItem.ReadWrite.OwnedBy`,
**admin-consented**, and a certificate whose private key is on the connector
host. [`APP-REGISTRATION.md`](APP-REGISTRATION.md) covers whether to reuse an
existing registration or create a second one.

One difference from `SqlHierarchyPush`: this tool runs as a service account, not
as a person, so `Auth:CertificateStoreLocation` is **`LocalMachine`** in all
three shipped configuration files. The gMSA needs read access to the
certificate's private key. A certificate in `CurrentUser\My` is invisible to it
and produces exit code 3 for a certificate you can plainly see in `certmgr.msc`.

Only enable `Settings:ResolveGroupsFromDirectory` if you intend to grant
`GroupMember.Read.All` as well. Nothing else in this connector uses it, so it is
opt-in rather than a silent widening of the app registration.

### Ranger read access

The service account needs read on the Ranger policy API:

```
GET {Settings:RangerBaseUrl}/service/public/v2/api/service/{service}/policy
```

for both `Settings:RangerHdfsService` (default `cm_hdfs`) and
`Settings:RangerSqlService` (default `cm_hive`). Ranger Admin must accept
Kerberos — the connector authenticates with SPNEGO and has no other mode.

`cdpatlascatalog` reads only `Settings:RangerSqlService`. The policies on a
table are what decide who may see its catalogue entry, and this connector asks
Ranger no other question.

**If Ranger cannot be read, the run fails.** It does not fall back to the file
permissions, and it does not default to indexing. Ranger is what says whether a
resource may be copied into an index at all, so a connector that cannot ask has
no basis on which to proceed.

---

## Step 3 — Decide what to index

This is the step that decides whether the deployment is defensible, and it is
cheapest now. Run the routing probe against the cluster before configuring
anything else:

```powershell
.\deploy\Test-RangerRouting.ps1 -RangerBaseUrl https://ranger01.corp.example:6182 -SqlService cm_hive
```

It reads the same policies through the same API the connector does, applies the
same rules, and prints a verdict per table:

```
contracts.contract       INDEX       table-wide select, no filter or mask
contracts.contract_ppi   LIVE QUERY  Ranger applies a row-level filter
```

### The doctrine

**Own it, index it. Entitle it at the source, call it.**

A row filter or a column mask is a per-user transform, and an index holds exactly
one copy of each item — so indexing a filtered or masked table either publishes
the service account's unfiltered view to everyone granted the item, or stores the
masked version and lies to the people entitled to the real one. Neither outcome
is a defect that more care could fix, which is why a filtered or masked table is
routed to a **live query** under the user's own identity instead, where the
cluster keeps doing the filtering it was configured to do.

`RoutingEvaluator` refuses in four cases, in this order, and all four fail closed:

| The policy says | Verdict | Why |
|---|---|---|
| Row filter (`policyType` 2) or column mask (`policyType` 1) | **Live query** | One indexed copy cannot show two people different rows or different values. |
| Any deny policy items | **Live query** | Graph has deny ACEs, but a mirrored deny that drifts fails open. Refusing to index is the version that fails closed. |
| A column-scoped grant | **Live query** | A mask in different clothes: different people are entitled to different parts of each row. |
| No group granted select | **Live query** | There is no principal to put on the item. An item granted to nobody is indexed and then returned to nobody. |
| Table-wide select to one or more groups, nothing above | **Index** | Those groups become the item ACL. |

A refusal is **not an error**. The run continues and reports it. "This table is
queried live instead" is an architecture, not a failure — see
[`COPILOT-ROUTING.md`](COPILOT-ROUTING.md) for the surfaces that serve it.

HDFS paths are judged more narrowly: a deny on a path stops it, but a path with
no matching Ranger policy is still walked, because the Ranger HDFS plugin itself
falls back to the file's POSIX permissions and ACL in that case, and so does this
connector. An empty Ranger group list on a path means "Ranger adds nothing", not
"nobody may read it".

Write the verdict for every table in scope into the deployment record before
going further. A table that comes back `LIVE QUERY` does not get fixed by
configuration and must not be pointed at with `Source:ItemView` in the hope that
it will.

### The catalogue's access model

The verdicts above are about **data**. A catalogue entry is not data: it is a
description of a table — its name, its columns, its owner, what it is tagged
with, and what feeds it. `cdpatlascatalog` therefore asks a different question of
the same policies, and gets different answers. This is the section to read
carefully, because it is the one that decides who learns what the lake contains.
`Test-RangerRouting.ps1` does not answer it; the rules below are how to read the
same report for the catalogue.

**The connector is deliberately stricter than the cluster.** Atlas has its own
authorisation, in the separate `cm_atlas` Ranger service, and CDP ships it with a
policy called `public` that grants every authenticated user read on every entity.
A deny in Hadoop SQL does not hide a table's metadata in Atlas. So on a default
cluster the catalogue is readable by everybody with an account, and this
connector does not mirror that. An entry is granted to exactly the groups Ranger
grants select on the table it describes — `RoutingEvaluator.EvaluateCatalogueEntry`
— and skipped when that is nobody. "Everyone with a cluster account" and
"everyone in the Microsoft 365 tenant" are different populations, and an index
that made the second inherit the first would publish the shape of the lake —
table names, column names, owners — to people who cannot reach the cluster at
all. Narrower than the source is the safe direction to be wrong in.

**A row filter or a column mask does not refuse an entry, where it refuses the
data.** This is the single most important thing on this page, and it is the point
a reviewer should satisfy themselves on rather than take on trust. A filter
governs which rows a person sees; a mask governs which values. Neither hides the
table's existence, its columns or its owner from somebody granted select on it —
they see all of that the moment they run `DESCRIBE`. So the metadata of a
filtered or masked table is indexable for exactly those people even though its
rows are not, and refusing to describe it would withhold from them something the
cluster already shows them. It is not a loophole; it is the same entitlement,
applied to a different object.

That is what makes this the connector that can describe data it may not index —
and the tables whose data can never be indexed are frequently the ones most worth
describing, precisely because nothing else in this deployment will ever surface
them. A table like `contracts.contract_ppi` is routed to a live query and none of
its rows reach the index; wherever a policy grants a group select on it, its
catalogue entry still says that the table exists, who owns it, what its columns
are called and what produced it — to exactly that group and nobody else.

**A deny does refuse the entry.** A description of a table is still a disclosure
about it, and a deny is the cluster saying that at least one principal must not
have the table. Mirroring a deny into an index fails open when it drifts, so the
entry is not written at all.

**A column-scoped grant narrows rather than refuses.** Only the columns the grant
names are described, because a column name discloses on its own — one called
`hiv_status` says something by existing — and somebody granted three columns has
not been shown forty. A grant naming every column, or naming none, constrains
nothing and the whole table is described.

| The policies on the described table say | Catalogue entry | Why |
|---|---|---|
| Any deny policy items | **Refused** | A description of a table is a disclosure about it, and a mirrored deny that drifts fails open. |
| No group granted select | **Refused** | There is nobody to grant the entry to. An entry granted to nobody is written and then returned to nobody. |
| Select granted, with a row filter or a column mask | **Indexed**, granted to those groups | The filter governs rows and the mask governs values. Neither hides the table's existence, columns or owner from somebody granted select. |
| Select granted, scoped to named columns | **Indexed, narrowed** to those columns | A column name discloses by existing. The entry describes what the grant covers and no more. |
| Table-wide select to one or more groups | **Indexed**, granted to those groups | The ordinary case. Those groups become the entry's ACL. |

A database entry (`hive_db`) is granted to whoever may read anything in the
database, which is the same evaluation with the table treated as a wildcard. A
path entry (`hdfs_path`, only if you add it to `Settings:AtlasTypes`) is judged
by the path rules above — and see step 11 for why it usually resolves to nobody
and is skipped.

**Atlas scrubs rather than removes.** When a search hit is one the caller may not
read, Ranger's authoriser inside Atlas blanks the header in place and sets its
GUID to `-1`, leaving the array the same length. The connector skips those. It
has to: indexing one would put a nameless entry in the catalogue, and a run
against an under-granted service account would produce a catalogue of blanks
rather than an error. If a run comes back with far fewer entries than the cluster
has tables, this is the first thing to check, and the `cm_atlas` grants in step 2
are where to check it.

---

## Step 4 — Map the cluster's groups to Entra groups

An item's ACL is a list of **Entra group object IDs**. The cluster knows group
*names*. `Settings:EntraGroupMap` is where somebody writes down what each name
means in this tenant:

```json
"EntraGroupMap": "hadoop-contracts-read=00000000-0000-0000-0000-000000000000;hadoop-policies-read=11111111-1111-1111-1111-111111111111;hadoop-audit-read=22222222-2222-2222-2222-222222222222"
```

Semicolon-separated `name=objectId` pairs. It needs no Graph permission, it is
reviewable, and it is what a regulated deployment should prefer: the statement
"`hadoop-analysts` means this Entra group" is in a file, and changing it is a
change under review.

All three connectors read the same setting and apply it the same way. A cluster
group that means one Entra group for a Hive row means the same Entra group for
the catalogue entry describing the table that row came from, which is what makes
one map reviewable rather than three.

Where the cluster's Kerberos is AD-integrated, the group names *are* AD group
names, and `Settings:ResolveGroupsFromDirectory` will look up anything the map
does not cover by `onPremisesSamAccountName`. That needs `GroupMember.Read.All`,
so it is off by default. A name matching two Entra groups resolves to neither —
picking one would be picking an audience.

### What a wrong mapping actually does

**An unresolved group is dropped. An item left with zero grants is skipped and
never written.** Nothing falls back to a default group, and nothing falls back to
`Acl:GrantGroupObjectIds`, which is empty in all three shipped files for exactly
this reason.

That is the whole design, and it is worth being explicit about the direction of
the error it produces. A wrong or missing mapping makes documents **disappear
from search**. It does not make them visible to the wrong people. The failure
mode of this connector is missing items, never over-sharing — because widening
the audience of precisely the item whose permissions could not be established is
the least defensible thing it could do.

### How to spot it in the log

Two lines, and the first is printed once per group per run rather than once per
file:

```
[WRN] Cluster group hadoop-audit-read does not resolve to an Entra group, so it grants nothing.
      Items readable only by it will be skipped. Add it to Settings:EntraGroupMap, or enable
      Settings:ResolveGroupsFromDirectory if its name matches an AD group synchronised to Entra.
[WRN] /data/caseworks/policies/policy-retention.txt resolves to no Entra group and is not indexed.
      Its cluster groups were: hadoop-policies-read, hadoop-audit-read
```

The catalogue says the same thing about an entry, named by its Atlas qualified
name:

```
[WRN] The catalogue entry for contracts.contract@cm resolves to no Entra group and is not indexed.
```

The second line names the cluster groups the file actually had, which is the list
to check the map against. In the run summary the same files appear as
`skipped=`. A `skipped=` count that is high, or that jumped after a cluster
change, is this and not an extraction problem:

```powershell
Select-String -Path .\CdpGraphPush\Logs\CdpGraphPush.log -Pattern 'does not resolve to an Entra group'
```

`Settings:OtherReadableGroupId` is the one place a grant can be widened, and it
is empty by default. It names the Entra group that a world-readable file maps to.
Leave it empty unless somebody has decided, in writing, that "everyone with an
account on the cluster" and "everyone in the tenant" are the same set of people.
It applies to HDFS files only: there is no equivalent for the catalogue, and
deliberately so — the argument in step 3 for not inheriting Atlas's `public`
policy is the same argument.

---

## Step 5 — Create the test data

Three scripts, on the cluster, in order. They create their own database and their
own directories and touch nothing else. Run them on an edge node as a principal
that can create the directories and set their groups, after `kinit`.

```bash
./hadoop/00-create-hdfs-test-data.sh /data/caseworks
```

```bash
beeline -u "jdbc:hive2://hs2-01.corp.example:10001/default;transportMode=http;httpPath=cliservice;principal=hive/_HOST@CORP.EXAMPLE;ssl=true" \
        -f hadoop/01-create-hive-test-data.hql
```

```bash
RANGER_URL=https://ranger01.corp.example:6182 ./hadoop/02-create-ranger-test-policies.sh
```

`02` authenticates with SPNEGO from the ticket cache. If your Ranger Admin only
accepts basic auth, that is a cluster-side setting to change, not a credential to
put in the script.

### What each is meant to prove

| Fixture | Meant to prove |
|---|---|
| `/data/caseworks/contracts`, mode 640, group `hadoop-contracts-read` | The ordinary case. Indexed, granted to the Entra group that name maps to. |
| `/data/caseworks/policies/policy-retention.txt`, plus `group:hadoop-audit-read:r--` | Two groups on one file. The item comes back with **two** ACL entries — the owning group and the named ACL entry. |
| `contract-C-1002.docx` | The Open XML extraction path, which is not the text path. Built by `python3`; skipped if it is absent. |
| `_SUCCESS` and `part-00000.tmp` | Hadoop's own litter. Neither is a document and neither may be indexed. |
| `contracts.contract` | Table-wide select to a group, no filter, no mask. Indexed, with that group on every row. |
| The row with a `NULL` `contract_ref` | A row with no natural key is skipped rather than given an invented item ID. |
| `C-1001` and `C-1002` sharing `last_modified_ts` | The composite watermark. A run interrupted between them resumes at `C-1002`. |

### The two negatives

These matter more than the positives, because a connector that indexes too much
still looks like it is working.

**`/data/caseworks/private/board-pack-restricted.txt` must not appear in the
index.** Its mode is 600, so no group can read it, so no grant can be derived,
so it must be skipped — not indexed with a fallback grant. To test the rule
rather than the scope, add `/data/caseworks/private` to `Settings:HdfsRoots` and
re-run the dry run. The file must still not be indexed, and the log must say it
resolves to no Entra group.

**`contracts.contract_ppi` must not appear in the index.** Script `02` puts a
Ranger row filter on it. Point `Source:ItemView` at it and run the dry run: the
connector must read **no rows** and log why. If either of these ever reaches the
index, that is a finding against the connector, not a configuration problem.

### What the fixtures prove about the catalogue

`contracts.contract_ppi` is the case worth watching, and script `02` sets it up
deliberately: it grants the group table-wide **select**, and then puts a row
filter on top. That is the arrangement a real cluster has — a filter narrows what
an already-entitled group sees rather than entitling anybody — and an earlier
version of these fixtures got it wrong, applying the filter with no grant behind
it, which made the table refused for having no grant rather than for being
filtered. That proved nothing.

With the grant in place, one run of one tool produces the pair of outputs that
demonstrates the whole distinction: `cdphivecontracts` reads **no rows** from
that table, and `cdpatlascatalog` **does** produce its entry, granted to that
group, carrying the table's columns and none of its rows. That pair is the clearest
evidence of step 3 that this deployment can produce. Keep it.

---

## Step 6 — Probe the host

Two probes, both read-only, both run **as the gMSA** where you can — a pass as
yourself proves the cluster is fine and says nothing about whether the connector
can reach it.

```powershell
.\deploy\Test-CdpSource.ps1 -ConfigPath .\CdpGraphPush\appsettings.cdphdfsdocs.json
```

```powershell
.\deploy\Test-CdpSource.ps1 -ConfigPath .\CdpGraphPush\appsettings.cdphivecontracts.json
```

It checks the identity it is running as, the Negotiate exchange against
`Settings:HdfsBaseUrl`, the WebHDFS operations the crawl uses (`LISTSTATUS`,
`GETFILESTATUS`, `GETACLSTATUS`, `GETCONTENTSUMMARY`, `OPEN`), the ODBC driver
and the composed connection string, and the Ranger policy API for both services.

For `cdpatlascatalog` the host checks are the Ranger policy read that probe
already covers and the unauthenticated status call in step 2; the dry run in step
7 is what proves the Kerberos exchange with Atlas itself.

Then the tenant half, which is the same script the SQL tools use:

```powershell
.\deploy\Test-GraphPushPrereqs.ps1 -ConfigPath .\CdpGraphPush\appsettings.cdphdfsdocs.json -SkipSql
```

Two of its checks do not apply to this connector and both are expected:

- **`Acl:GrantGroupObjectIds` is empty** is reported as a failure. For these
  connectors an empty list is correct — every item carries its own ACL. Read the
  rest of the output and ignore that line.
- **`-SkipSql` is required.** There is no `DataSource:Server` in these files, so
  the SQL reachability check has nothing to probe.

Everything else it reports is real, and the roles check is the reason to run it:
an application permission listed in the portal but never admin-consented simply
does not appear in the token, and nothing else on the client side distinguishes
that from a wrong connection owner. Both arrive as a bare 403.

---

## Step 7 — Dry run

Read the source, map every item, and report exactly what would be written —
writing nothing:

```powershell
.\CdpGraphPush\CdpGraphPush.exe --connector cdphdfsdocs --dry-run
```

**A dry run writes nothing to Graph and does not advance the watermark.** It
never calls the commit callback, so the checkpoint is exactly where it was before
and can be re-run as often as you like.

Four things happen at the desk that would otherwise happen against the tenant:
the schema is built, so the searchable-and-refinable and name-length rules fire
here; every file or row is mapped, so a mapping fault is found before any item
exists; Ranger is read, so a routing refusal is visible before it is a surprise;
and a read-only `GET` checks the connection is not another connector's. If Graph
is unreachable, that last check is skipped with a note and the dry run continues
— its main job needs only the cluster.

### What to read in the output

```
[INF] Dry run: schema builds cleanly (10 properties). Reading and mapping CDP HDFS documents,
      writing nothing to Graph.
[INF] 2412 file(s) in scope, 2412 to read this run (full recrawl).
[INF] Would write hdfs-... (file): 10 properties, 3184 content bytes, 2 ACL entr(y/ies).
...
[INF] Dry run complete. 2409 row(s) processed (file=2409) for connection cdphdfsdocs;
      2409 distinct item(s). truncated=0 skipped=3 duplicates=0 throttleWaits=0
```

- **`n file(s) in scope, m to read this run`** — `n` is what the roots and
  `Settings:IncludeExtensions` selected; `m` is what the watermark left. On a
  first run they are equal.
- **`ACL entr(y/ies)` per item** — this is the check the test data exists for.
  `policy-retention.txt` must show **2**. An item showing 0 is not written at
  all, so any item you can see here has at least one grant.
- **`skipped=`** — files the source declined: unresolved groups, a Ranger deny, a
  file deleted between the listing and the read. Step 4 says how to tell which.
- **`duplicates=`** — above zero means two rows produced one item ID, and the
  later one would silently overwrite the earlier item in a real push.
- **`truncated=`** — bodies cut at `DataSource:MaxContentBytes`.
- **Anything you did not expect to be there.** This is the last cheap moment to
  notice it.

Repeat for the other two connectors before going on:

```powershell
.\CdpGraphPush\CdpGraphPush.exe --connector cdphivecontracts --dry-run
```

```powershell
.\CdpGraphPush\CdpGraphPush.exe --connector cdpatlascatalog --dry-run
```

### What the catalogue's dry run says

```
[INF] Dry run: schema builds cleanly (15 properties). Reading and mapping CDP Atlas catalogue,
      writing nothing to Graph.
[INF] Atlas returned 41 live hive_db entit(y/ies).
[INF] Atlas returned 1863 live hive_table entit(y/ies).
[INF] Would write a1b2c3d4e5f64789abcd0123456789ab (catalogue): 15 properties, 412 content bytes,
      2 ACL entr(y/ies).
...
[INF] Dry run complete. 1712 row(s) processed (catalogue=1712) for connection cdpatlascatalog;
      1712 distinct item(s). truncated=0 skipped=192 duplicates=0 throttleWaits=0
```

- **One `Atlas returned ...` line per type in `Settings:AtlasTypes`**, and that
  count is the whole of the catalogue rather than a delta. There is no "in scope
  / to read this run" pair here because every entity is read every run (step 11).
- **`skipped=` is expected to be substantial, and it is the control working.**
  It counts entries the rules in step 3 refused: a deny, no group granted select,
  a group that resolves to no Entra group, and entities Atlas blanked because
  this account may not read them. A `skipped=` of **zero** on a real cluster is
  the number to be suspicious of, not a large one.
- **The item ID** is `a` followed by the Atlas GUID with its hyphens removed, so
  a line in this log names an entity you can paste straight into Atlas's own
  search and look at.
- **`catalogue=`** in the breakdown is the item type; databases, tables and paths
  all count under it.
- **`content bytes`** in the hundreds is right. A catalogue entry is a paragraph
  of sentences about a dataset, not the dataset.

---

## Step 8 — The first real run, and verifying it

```powershell
.\CdpGraphPush\CdpGraphPush.exe --connector cdphdfsdocs
```

The first run creates the connection, registers the schema, waits for it to reach
`ready`, and then writes every item. **Schema registration takes 5 to 15 minutes
and reports no progress while it runs.** That silence is why people conclude it
has hung and delete the connection, which is the one action that makes things
worse: it restarts the same wait and discards everything already written.

Watch it properly from a second window:

```powershell
.\deploy\Watch-SchemaRegistration.ps1 -ConfigPath .\CdpGraphPush\appsettings.cdphdfsdocs.json
```

**Read the schema it prints when the state reaches `ready`.** After that the
schema is append-only: properties can be added, but no property's type,
annotations or labels can be changed and none can be removed. Correcting a
mistake means deleting the connection — and every item in it — and starting
again, including the wait. A timeout has cancelled nothing; registration
continues server-side whether or not the tool that started it is still running,
so re-run the watcher, not the push.

Then the other two, each registering its own schema on its own connection, and
each paying that wait once:

```powershell
.\CdpGraphPush\CdpGraphPush.exe --connector cdphivecontracts
```

```powershell
.\CdpGraphPush\CdpGraphPush.exe --connector cdpatlascatalog
```

### Verifying in Microsoft Search

Sign in **as a person who is in one of the mapped Entra groups** and search from
the Microsoft Search results page or the SharePoint search box. `POST
/search/query` has no app-only form and results are security-trimmed, so what a
user can find is the only meaningful question — an app-only check would prove
that items exist, which was never in doubt.

Search for a phrase from the test data, for example `settlement reconciliation`.

Then verify the negatives with the same account, and with an account in **no**
mapped group:

| Query | In a mapped group | In no mapped group |
|---|---|---|
| `settlement reconciliation` | Finds `contract-C-1000.txt` | Finds nothing |
| `board pack` or `prove a negative` | Finds nothing | Finds nothing |
| Anything from `contract_ppi`, e.g. `Settlement instructions` | Finds nothing | Finds nothing |
| `contract_ppi` as a table name, once a select grant exists on it (step 5) | Finds its **catalogue entry** — the table, its columns, its owner | Finds nothing |

The last three rows are the deployment's evidence. Keep the screenshots. The
fourth belongs beside the third, captioned: the same table's rows are unfindable
and its description is findable, by the same person, at the same moment, on
purpose, for the reason in step 3.

Reconcile the index against the source afterwards:

```powershell
.\deploy\Compare-SourceToIndex.ps1 -ConfigPath .\CdpGraphPush\appsettings.cdphivecontracts.json
```

### Verifying in Copilot

Allow longer than search. The semantic index is built independently from the same
content and lags it, so a weak Copilot answer when search already passes means
semantic indexing has not caught up — not that the connector failed. That
distinction is the reason to check search first.

Prompts that exercise this connector rather than plain retrieval:

- *What are the termination terms in the Northwind contract?*
- *Summarise our records retention policy.*
- *Which contracts are with Fabrikam, and what are they worth?*

And the catalogue, which answers a different shape of question:

- *Which table holds customer addresses, and who owns it?*
- *What feeds `contracts.contract`, and what does it feed?*
- *Which tables are classified as PII?*

---

## Step 9 — Scheduling, and the watermark

Nothing runs this on a schedule. Register a task per connector, staggered, under
the gMSA:

```powershell
$action = New-ScheduledTaskAction -Execute 'C:\Connectors\Cdp\CdpGraphPush.exe' `
    -Argument '--connector cdphdfsdocs' -WorkingDirectory 'C:\Connectors\Cdp'

$trigger = New-ScheduledTaskTrigger -Daily -At 02:00

# LogonType Password with a gMSA supplies NO password: Windows retrieves the
# current one from Active Directory at logon. Nothing is typed here and nothing
# is stored here.
$principal = New-ScheduledTaskPrincipal -UserId 'CORP\svc-cdp$' `
    -LogonType Password -RunLevel Limited

Register-ScheduledTask -TaskName 'CdpGraphPush cdphdfsdocs' `
    -Action $action -Trigger $trigger -Principal $principal
```

Repeat with `--connector cdphivecontracts` and `--connector cdpatlascatalog`,
each at a different hour. Three tasks, three hours: they share a host, a service
account and a tenant, and a full HDFS crawl overlapping a catalogue run buys
nothing but throttling. Monitor each task's **Last Run Result**: it is the
process exit code, and step 10 says what each one means. A monitoring rule keyed
to `3` must page for credential rotation and must not be folded in with `4`.

### Where the watermark lives

`Settings:CheckpointDirectory` (default `state`, relative to the executable
unless rooted), one file per connector, named for the connector key:

```
C:\Connectors\Cdp\state\cdphdfsdocs.watermark.json
C:\Connectors\Cdp\state\cdphivecontracts.watermark.json
C:\Connectors\Cdp\state\cdpatlascatalog.watermark.json
```

```json
{
  "markerTime": "2026-08-25T22:41:07.0000000Z",
  "markerKey": "/data/caseworks/policies/policy-retention.txt",
  "runCount": 7,
  "lastCompletedUtc": "2026-08-26T01:04:33.9120000Z"
}
```

The marker is composite — `(modification time, path)` for HDFS,
`(Settings:HiveWatermarkColumn, Settings:HiveKeyColumn)` for Hive, and
`(Atlas update time, entity GUID)` for the catalogue — because two files can
share a timestamp to the millisecond, and a marker holding only the timestamp
either re-reads that whole group for ever or loses whichever of them had not been
written when a run stopped. It is written temp-then-rename, so a process killed
mid-write leaves the old checkpoint or the new one, never half of either.
`Settings:ScanSlackSeconds` (default 900) is subtracted on resume to absorb clock
skew between this host and the NameNode.

The marker only ever moves to an item whose write **returned**. A failed run
therefore cannot advance it past something the index does not have, and
`runCount` only advances on a crawl that completed — the full-recrawl cadence
below counts successful crawls, not attempts.

### If the file is deleted

An absent, unreadable or unparseable checkpoint is treated as absent, and absent
means **the next run re-reads and re-writes everything**. That is safe, because
every write is an upsert: reading a file twice costs time and changes nothing.
It is not free — a full crawl of a large lake is hours of cluster reads, tenant
item writes and possibly throttling — and it resets `runCount`, which restarts
the full-recrawl cadence from that run. Back the `state` directory up with the
host, and do not put it anywhere a cleanup job treats as scratch.

For `cdpatlascatalog` losing the file costs almost nothing, because that
connector re-reads the whole catalogue every run in any case (step 11).

### `Settings:FullRecrawlEveryRuns`, and why it is a security control

Default `7`. A run is a full recrawl when the completed-run count is a multiple
of it, so at the default that is the first run and every seventh one after it,
and the log says so:

```
[INF] Run 8 is a full recrawl (every 7 runs). Every file is re-read, which is what re-derives
      item ACLs after a permission change at the source and picks up files moved into scope
      with older timestamps.
```

**A permission change does not alter a file's modification time.** Revoke a
group's read on a file at the source and the file looks untouched to every
incremental pass, so its item keeps the ACL it was written with, and the people
whose access was revoked keep finding it in search. The periodic full recrawl is
the **only** thing in this connector that re-derives item ACLs, which makes this
setting the documented **upper bound on ACL staleness**: at a daily schedule and
the default of 7, a revocation at the source can take up to seven days to reach
the index.

Record that bound in the deployment's risk register, in those terms, with the
schedule it is derived from. It is a number the business accepts, not a default
somebody inherited. If seven days is too long, lower the setting and pay for it
in cluster reads and item writes; if a revocation must be immediate, the answer
is a live-query surface for that data rather than a smaller number here.

Setting it to `0` is **refused** at startup with exit code 2, not warned about.
There is no warning channel in the configuration validator — anything it reports
fails startup — and that is the right outcome: a deployment with no bound at all
on ACL staleness should not start by accident. A deployment that genuinely wants
the recrawl to be effectively never sets a large number instead, up to 365, which
leaves the bound stated rather than absent.

**The catalogue is no exception, and it is worth saying so explicitly because
the shape of the connector suggests otherwise.** `cdpatlascatalog` does enumerate
every entity every run — Atlas 2.1.0 cannot filter a basic search by
modification time, so there is no incremental read to ask for — but reading
everything is not the same as re-deciding everything. The marker filter is
applied before the routing check, and a Ranger policy edit does not change an
Atlas entity's modification time, so an entry whose grant changed but whose
entity did not is dropped before any ACL is derived. Its ACL staleness bound is
`Settings:FullRecrawlEveryRuns` runs, the same as the other two.

---

## Step 10 — Exit codes, and what to do about each

| Code | Means | What to do |
|---|---|---|
| **0** | Success. The crawl completed and the watermark advanced over what was written. | Nothing. Check `skipped=` in the summary is what you expect. |
| **2** | Configuration invalid. Nothing opened a socket. | Read the log: every problem is listed at once, each naming its setting path. Unreplaced `REPLACE-WITH` placeholders, a non-https `HdfsBaseUrl`, an `HdfsBaseUrl` not ending `/webhdfs/v1`, an empty `RangerBaseUrl`, `HiveWatermarkColumn` without `HiveKeyColumn`, a credential keyword smuggled into `HiveExtraOptions`. For the catalogue: an empty or non-https `AtlasBaseUrl`, one that includes `/api/atlas`, an `AtlasPageSize` outside 1 to 10000, an empty `AtlasTypes`, an `AtlasTypes` naming a type this connector cannot describe. Fix and re-run. |
| **3** | A credential was rejected — by **Entra** or by **the source**. | Both are "this identity is no longer accepted". Entra: an expired certificate, a revoked one, missing admin consent, or a connection this app does not own. The source: a Kerberos ticket that stopped renewing, a broken realm trust, HDFS answering 401 or 403, Ranger refusing the policy read, Atlas answering 401 or 403 because the account lacks entity-read in `cm_atlas`. The log line says which — `The credential was rejected by Entra ID`, `Graph rejected the caller`, or `The source rejected this identity`. Re-run `Test-CdpSource.ps1` and `Test-GraphPushPrereqs.ps1` as the gMSA. |
| **4** | Ingestion failed. | The run stopped part-way and the watermark is on the last item that really landed, so re-running resumes rather than restarting. Common causes are named in the log: the error budget tripped (`above Settings:MaxErrorRatePercent`), the item budget refused startup (`above the configured Settings:ItemBudget`, nothing written), a DataNode or HiveServer2 that went away, an Atlas that answered something other than 404, a Ranger service that uses security zones (`has N polic(y/ies) in security zone(s)` — see the security control mapping, CDP-17), or a cancellation. Fix the cause and re-run; the writes are upserts. |

Three of those deserve a note.

**`Settings:MaxErrorRatePercent`** (default 5) aborts a run whose failures exceed
it, once at least 50 files have been examined — below that sample one bad file is
100% and would abort a healthy run. It exists so a systemically broken extractor
or a sick DataNode cannot be laundered into a successful crawl that was mostly
skips.

**`Settings:ItemBudget`** is checked against the preflight count **before a
single write**, so an oversized scope fails at startup with the real number in
the message rather than a connection discovering its own ceiling halfway through
a crawl. Raise it deliberately, or narrow `Settings:HdfsRoots` and
`Settings:IncludeExtensions`.

**An Atlas failure that is not a 404 stops the run**, and that is the same rule
as an unreadable Ranger rather than a stricter one. A catalogue read that half
worked would publish a partial map of the lake and present it as the whole thing,
and nothing downstream can tell a catalogue that is complete from one that is
missing the third of the tables Atlas was too busy to return. A 404 on a single
entity is different and is treated as a skip: an entity deleted between the
search and the read is normal in a live catalogue. When a run stops this way,
check the service before the configuration —
`/api/atlas/admin/status` answers without authentication and returns `ACTIVE` on
a healthy instance.

---

## Step 11 — Known limits

Stated plainly, because each one is a thing an operator will otherwise discover
at the worst moment.

**PDF text needs an optional build flag.** The shipped build extracts
`txt`, `md`, `csv`, `json`, `xml`, `html` natively and `docx`, `xlsx`, `pptx`
through Open XML with no third-party package. PDF is compiled out. Build with
`-p:EnablePdfExtraction=true` to include it, which pulls in PdfPig (Apache-2.0):

```powershell
dotnet publish .\src\CdpGraphPush\CdpGraphPush.csproj -c Release -r win-x64 `
    --self-contained true -o .\out\CdpGraphPush -p:EnablePdfExtraction=true
```

Without the flag a PDF is still indexed — by name, path, owner and date, with
`extractStatus` set to `Unsupported` — because a document nobody can find is
worse than a document found without its contents. `extractStatus` is refinable
precisely so the index can be asked how much of the lake has no body, which is
the question that decides whether OCR is worth buying. Scanned PDFs have no text
layer and the flag does not change that.

**A cluster-local group with no Entra identity cannot be represented, and its
files are skipped.** `Settings:GroupMappingMode` has an `ExternalGroups` value
and it is refused at validation rather than half-implemented: an external group
can only contain Entra users and groups, so mirroring a group whose members exist
only on the cluster produces a group with nobody in it, and items granted to it
would be indexed and returned to no one. Item ACLs here are also group-only, so a
file's owning **user** gets no grant from ownership alone — an owner who is not
in a granted group does not see their own file. The effect throughout is that the
index shows a file to **fewer** people than the cluster would, never more. Map
the cluster's groups to Entra groups in `Settings:EntraGroupMap`, or accept that
those files are not indexed.

**A push never deletes, so removed files leave orphans.** An item that stops
appearing at the source — file deleted, row dropped, path removed from
`Settings:HdfsRoots`, table re-routed to a live query, table dropped from Hive
while its catalogue entry stays behind — keeps its item in the index and stays
searchable and citable. That is a property of this model, not an oversight. Find
the orphans, and get the exact `DELETE` for each printed rather than run:

```powershell
.\deploy\Compare-SourceToIndex.ps1 -ConfigPath .\CdpGraphPush\appsettings.cdphdfsdocs.json
```

Run it on a schedule of its own. If that list keeps growing, this path is being
used as a synchroniser, which it is not.

**The catalogue is fully enumerated every run.** Atlas 2.1.0 — which is what CDP
7.1.9 ships — cannot filter a basic search by modification time, so there is no
incremental read to be had and the connector does not pretend otherwise: every
run asks Atlas for every entity of every type in `Settings:AtlasTypes`. The
watermark still records where a run reached and how many runs have completed;
what it cannot do here is spare the Atlas reads. This is affordable because a
catalogue is small — thousands of entities, not the millions of files underneath
them. Turning `Settings:AtlasIncludeLineage` off during a first proving run is
the one lever that materially changes the cost, because lineage is an extra
request per table.

Reading everything is not the same as re-deciding everything, and step 9 sets
out the difference: the watermark filter runs before the routing check, so the
ACL staleness bound here is `Settings:FullRecrawlEveryRuns` runs, exactly as it
is for the other two connectors.

**`Settings:AtlasTypes` is checked at startup against the types this connector
can describe** — `hive_db`, `hive_table`, `hive_view` and `hdfs_path`. Anything
else, including a plausible typo like `HiveTable` and the tempting `hive_column`,
is refused with exit code 2 rather than accepted. An unknown type is enumerated
and detailed in full and then described not at all, which costs a whole crawl
and reports a clean run with nothing written — a failure that looks exactly like
an empty catalogue.

**`hdfs_path` in `Settings:AtlasTypes` will usually catalogue nothing.** The
catalogue connector reads only `Settings:RangerSqlService`, and a Hive policy
carries no path resource — so a path entity matches no policy, resolves to no
group, and is skipped rather than described. It is counted in `skipped=` like any
other refusal. Leave the setting at `hive_db;hive_table` unless the directories
you want described are covered by policies in the service you have pointed
`Settings:RangerSqlService` at, and check the dry run rather than assuming.

**`hive_column` is not catalogued as its own entity, deliberately.** Columns are
described as part of their table, which is where somebody searching for a column
name wants to land. One item per column would multiply the item count by roughly
fifty against the same `Settings:ItemBudget` and answer no question the table
entry does not already answer.

**Iceberg through Impala is untested on 7.1.9 and is not claimed.** The ODBC
reader is engine-agnostic and one Ranger service definition covers Hive and
Impala, so there is a reasonable expectation it works — but it has not been run
against an Iceberg table on this version, so nothing here says it does. Treat it
as unverified until somebody verifies it, and do not put it in a scope statement
on the strength of the paragraph above.

---

## What good looks like

A healthy scheduled run of `cdphdfsdocs`, from the log:

```
02:00:04 [INF] CdpGraphPush starting connector cdphdfsdocs (CDP HDFS documents) against
               connection cdphdfsdocs, configuration C:\Connectors\Cdp\appsettings.cdphdfsdocs.json.
02:00:06 [INF] Run 8 is a full recrawl (every 7 runs). Every file is re-read, which is what
               re-derives item ACLs after a permission change at the source and picks up files
               moved into scope with older timestamps.
02:00:31 [INF] 2412 file(s) in scope, 2412 to read this run (full recrawl).
02:03:18 [WRN] Cluster group hadoop-legacy-etl does not resolve to an Entra group, so it grants
               nothing. Items readable only by it will be skipped.
02:41:52 [INF] Crawl complete. 2412 file(s) examined, 3 failed extraction or read,
               watermark at 2026-08-25T22:41:07.0000000Z.
02:41:52 [INF] Ingestion complete. 2409 row(s) processed (file=2409) for connection cdphdfsdocs;
               2409 distinct item(s). truncated=1 skipped=3 duplicates=0 throttleWaits=2
```

Read it in this order.

- **`2412 file(s) in scope, 2412 to read`** — a full recrawl, so the two match.
  On an incremental run the second number is small and the first is the whole
  scope; a second number equal to the first on an incremental run means the
  checkpoint was lost.
- **`2412 examined, 3 failed`** — 0.1%, well under the default
  `Settings:MaxErrorRatePercent` of 5.
- **`skipped=3`** — accounted for, not ignored. One unresolved group named in the
  warning above, and that warning appears once rather than three times.
- **`duplicates=0`** — every item ID was produced once. Anything above zero is a
  source returning more than one row per item.
- **`throttleWaits=2`** — Graph asked the tool to slow down and it did. Normal on
  a full recrawl; a number climbing run on run means the schedule is too tight.
- **`watermark at 2026-08-25T22:41:07`** — it moved, and it moved only over items
  whose writes returned.
- **Exit code `0`**, which is what the scheduled task's Last Run Result shows.

And the same run as a dry run, for comparison — note there is no `Crawl complete`
line, because a dry run never completes a crawl and never touches the watermark:

```
09:14:02 [INF] Dry run: schema builds cleanly (10 properties). Reading and mapping
               CDP HDFS documents, writing nothing to Graph.
09:14:31 [INF] 2412 file(s) in scope, 2412 to read this run (full recrawl).
09:22:10 [INF] Dry run complete. 2409 row(s) processed (file=2409) for connection cdphdfsdocs;
               2409 distinct item(s). truncated=1 skipped=3 duplicates=0 throttleWaits=0
```

If the two disagree on anything but timing, read the difference before pushing.

A healthy run of `cdpatlascatalog` reads differently and should:

```
04:00:03 [INF] CdpGraphPush starting connector cdpatlascatalog (CDP Atlas catalogue) against
               connection cdpatlascatalog, configuration C:\Connectors\Cdp\appsettings.cdpatlascatalog.json.
04:00:09 [INF] Atlas returned 41 live hive_db entit(y/ies).
04:01:44 [INF] Atlas returned 1863 live hive_table entit(y/ies).
04:18:26 [INF] Ingestion complete. 1712 row(s) processed (catalogue=1712) for connection
               cdpatlascatalog; 1712 distinct item(s). truncated=0 skipped=192 duplicates=0
               throttleWaits=0
```

No "in scope / to read this run" pair, because the whole catalogue is read every
run; no `truncated=`, because an entry is a paragraph; and a `skipped=` in the
hundreds, because 192 databases and tables are ones nobody is granted select on,
or ones a deny covers, or ones whose groups do not map. That number is the
control doing its job, and it is worth writing down run on run: a sudden fall in
it means somebody widened a Ranger policy, and a sudden rise means a group
stopped resolving.
