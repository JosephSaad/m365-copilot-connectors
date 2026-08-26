# Troubleshooting — the CDP connectors (`CdpGraphPush`)

This document exists because the CDP path fails in places the SQL push path has
no equivalent of. `SqlGraphPush` reads one table with one credential;
`CdpGraphPush` reads a Kerberised cluster with a second identity, asks Ranger
what it is allowed to index before it indexes anything, and derives a different
ACL for every item. Three of those four are new failure surfaces, so this is a
separate document rather than a section in
[`TROUBLESHOOTING-DIRECT-PUSH.md`](TROUBLESHOOTING-DIRECT-PUSH.md).

```
                      ┌──────────────┐
                      │ Ranger Admin │  read FIRST, every run, or the run stops
                      └──────┬───────┘
                             │ SPNEGO
  ┌────────────┐   ┌─────────┴────────┐   ┌────────────┐        ┌──────────────────┐
  │ HDFS       │ 1 │ CdpGraphPush     │ 3 │ Graph      │ ┌──4───→│ Microsoft Search │
  │ HttpFS     │──→│ Windows Server   │──→│ ingestion  │─┤       └──────────────────┘
  ├────────────┤   │ running as gMSA  │   └────────────┘ │       ┌──────────────────┐
  │ Hive/Impala│ 2 │                  │                  └──5───→│ Copilot semantic │
  │ ODBC       │──→│                  │                          │ index            │
  └────────────┘   └──────────────────┘                          └──────────────────┘
```

**Two identities, one process.** Hops 1 and 2 are Kerberos as the Windows
account the process runs as — a gMSA, over SSPI and HTTP Negotiate, with no
password and no keytab anywhere. Hop 3 is the Entra app registration and its
certificate. They fail independently and for unrelated reasons, and the exit
code deliberately does not distinguish them; see
[stage 1](#stage-1--the-two-rejections-that-share-exit-3).

**Ranger is on the critical path, not beside it.** Nothing is indexed before the
policies are read, and an unreadable Ranger fails the run. That is the single
behaviour most often reported as a defect, and it is
[stage 2](#stage-2--ranger-and-why-an-unreachable-one-stops-the-run).

---

## The two connectors

One executable hosts both. Each has its own connection, its own schema and its
own checkpoint file, and they must never share a connection ID.

| | `cdphdfsdocs` | `cdphivecontracts` |
|---|---|---|
| Run | `CdpGraphPush.exe --connector cdphdfsdocs` | `CdpGraphPush.exe --connector cdphivecontracts` |
| Configuration | `appsettings.cdphdfsdocs.json` | `appsettings.cdphivecontracts.json` |
| Source | HttpFS or WebHDFS at `Settings:HdfsBaseUrl` | Hive or Impala over ODBC |
| Ranger service | `Settings:RangerHdfsService` (`cm_hdfs`) | `Settings:RangerSqlService` (`cm_hive`) |
| Watermark | (modification time, path) | (`HiveWatermarkColumn`, `HiveKeyColumn`) |
| Checkpoint | `state\cdphdfsdocs.watermark.json` | `state\cdphivecontracts.watermark.json` |
| Item ID | `h` + SHA-256 of the path | `t` + SHA-256 of table + key |

The configuration file is resolved as `appsettings.{connector key}.json` beside
the executable, falling back to `appsettings.json`. The log is named after the
**executable**, so both connectors write `Logs\CdpGraphPush.log`.

`--dry-run` builds the schema, checks the connection is not another connector's
with read-only GETs, reads the source and maps every item, and writes nothing.
It logs `Would write {ItemId} (...)` per item and **never calls the commit
callback**, so a dry run cannot advance the watermark. It is the right first
command after any configuration change.

---

## Where to start

| What you are seeing | Start at |
|---|---|
| `FATAL:` on stderr and an empty log | [0](#stage-0--configuration) — the failure predates the logger |
| Exit 2 and a numbered list of `Settings:` problems | [0](#stage-0--configuration) — every problem is listed at once |
| Exit 3, "The source rejected this identity." | [1](#stage-1--the-two-rejections-that-share-exit-3) — Kerberos, the gMSA, the realm |
| Exit 3, "The credential was rejected by Entra ID." | [1](#stage-1--the-two-rejections-that-share-exit-3) — the certificate, same exit code, different half |
| Exit 4, "Ranger Admin at ... could not be reached" | [2](#stage-2--ranger-and-why-an-unreachable-one-stops-the-run) |
| Exit 4 before a single write, with an item count in it | [2](#the-item-budget) — `Settings:ItemBudget` |
| A table produced no items and the run still succeeded | [3](#stage-3--a-table-that-returns-no-rows) |
| Files are indexed but particular ones are missing | [4](#stage-4--why-one-item-is-missing-from-search) |
| Items are in the index with no body | [5](#stage-5--extraction-and-the-error-budget) — read `extractStatus` first |
| Exit 4 partway, "above Settings:MaxErrorRatePercent" | [5](#the-error-budget) |
| An item is still visible after access was revoked at the source | [6](#stage-6--the-watermark-and-the-acl-staleness-bound) — **this one matters** |
| A file moved or renamed into a crawled directory never appears | [6](#a-file-renamed-into-scope) |
| A deleted file or dropped row is still returned | [7](#stage-7--a-push-never-deletes) |
| In search but not in Copilot | [`TROUBLESHOOTING.md` stage 7](TROUBLESHOOTING.md#stage-7--copilot-grounds-on-them) — shared with every path |

---

## Exit codes

The contract is the same for every push executable, and the exit code is the
fastest diagnosis available.

| Code | Meaning | Where to look |
|---|---|---|
| 0 | The crawl completed and the checkpoint advanced | — |
| 2 | Configuration invalid, unreadable, or no connector selected | [Stage 0](#stage-0--configuration) |
| 3 | A credential was rejected — **by Entra ID or by the cluster** | [Stage 1](#stage-1--the-two-rejections-that-share-exit-3) |
| 4 | Ingestion failed after both credentials worked | Stages [2](#stage-2--ranger-and-why-an-unreachable-one-stops-the-run) to [5](#stage-5--extraction-and-the-error-budget) |

Two things about code 2 are worth knowing separately. A **configuration value**
that fails validation is logged, so `Logs\CdpGraphPush.log` holds the full list:

```
Configuration in C:\Connectors\Cdp\appsettings.cdphdfsdocs.json is invalid. 3 problem(s):
```

A configuration file that cannot be **read or parsed** — bad JSON, a missing
file, an unknown `--connector` value, a mismatched DLL beside the executable —
fails before the logger exists and prints `FATAL:` to stderr with nothing in the
log at all. An empty log with a non-zero exit is that case, not a missing log.

---

## The scripts

There is no CDP equivalent of `Compare-SourceToIndex.ps1`: that script queries
`dbo.Tickets` and does not apply here. What does apply:

| Script | Covers |
|---|---|
| [`Test-GraphPushPrereqs.ps1`](../deploy/Test-GraphPushPrereqs.ps1) | The Entra half only — certificate, token, **the roles actually consented**, connection ownership |
| `Test-RangerRouting.ps1` | The routing verdicts, read from Ranger the same way the connector reads them |
| [`Verify-GraphConnection.ps1`](../deploy/Verify-GraphConnection.ps1) | Search, as a user in one of the mapped groups rather than as the app |
| [`hadoop/00-create-hdfs-test-data.sh`](../hadoop/00-create-hdfs-test-data.sh) | The three permission cases, including the mode-600 file that must **not** appear |
| [`hadoop/02-create-ranger-test-policies.sh`](../hadoop/02-create-ranger-test-policies.sh) | The indexable table, the row-filtered one, and the HDFS path grant |

Run the pre-flight with `-SkipSql`; there is no SQL Server on this path.

```powershell
.\Test-GraphPushPrereqs.ps1 -ConfigPath C:\Connectors\Cdp\appsettings.cdphdfsdocs.json -SkipSql
```

Two of its findings are written for `SqlGraphPush` and are expected here:

- It warns that `Auth:CertificateStoreLocation` is not `CurrentUser`.
  `SqlGraphPush` runs as a person; these connectors run as a service, so
  `LocalMachine` is correct.
- It fails `Acl:GrantGroupObjectIds` for being empty. Empty is correct for both
  CDP connectors: every item carries the grants HDFS and Ranger give it, so a
  connection-wide grant would be wrong for almost every item, and a file whose
  groups cannot be resolved is skipped rather than granted that list.

Everything else it reports — placeholders, the certificate, the token, the roles
actually consented, connection ownership and state — applies unchanged.

Nothing on this path proves the cluster half from a workstation. `klist` as the
service account, and `--dry-run`, are the only honest tests of hops 1 and 2.

---

## Stage 0 — configuration

**What has to be true.** Every `Settings:` value validates. Validation collects
every problem and reports them together, so one restart clears all of them
rather than one per mistake.

The ones that are refused for a reason worth knowing:

| Setting | Refused when | The message says |
|---|---|---|
| `Settings:HdfsBaseUrl` | the scheme is not https | "must be https. The Kerberos exchange and every byte of file content would otherwise cross the network in clear." |
| `Settings:HdfsBaseUrl` | it does not end in `/webhdfs/v1` | "must end with /webhdfs/v1, for example https://httpfs01.corp:14000/webhdfs/v1. HttpFS and WebHDFS share that path." |
| `Settings:HdfsRoots` | it is empty | "must list at least one absolute HDFS path, separated by semicolons. There is no default: crawling / is not a scope decision anyone made." |
| `Settings:HdfsRoots` | a root is relative | "'data/contracts' is not an absolute path." |
| `Settings:HdfsRoots` | a root contains `..` | "'/data/../etc' contains '..', which is not a path this will follow." |
| `Settings:HiveKeyColumn` | a watermark column is set and this is not | "is required when Settings:HiveWatermarkColumn is set. Two rows can share a timestamp, so the key is what makes the ordering total and the resume point exact." |
| `Settings:HiveExtraOptions` | it carries a credential keyword | "must not set 'pwd'. This connector authenticates with Kerberos as the service identity; a credential in configuration is refused here and would fail the build's secret hygiene gate anyway." |
| `Settings:HiveExtraOptions` | it weakens the transport | "must not set 'allowselfsignedservercert'. TLS and certificate validation are decided by Settings:HiveUseSsl and the Windows trust store, not by an override that would silently accept any certificate." |
| `Settings:GroupMappingMode` | it is `ExternalGroups` | "ExternalGroups is not implemented. An external group can only contain Entra users and groups, so a cluster-local group whose members have no Entra identity cannot be mirrored into one that grants anybody anything." |
| `Settings:RangerBaseUrl` | it is empty | "is required. Ranger decides which tables and paths may be indexed at all ... and this connector will not index a source whose policies it cannot read." |
| `Settings:HiveUseSsl` | it is `false` | "is false. A Kerberised HiveServer2 endpoint in a regulated deployment is TLS terminated; turning this off puts every row this connector reads on the wire in clear." |
| `Settings:HiveTransport` | it is anything else | "must be one of [http, sasl]; found 'binary'." |

### Why `HiveExtraOptions` is inspected keyword by keyword

The ODBC connection string is **composed**, never pasted. The shape is fixed —
`AuthMech=1`, `UseOnlySSPI=1`, `UseSystemTrustStore=1`, `KrbServiceName`,
`ThriftTransport` from `Settings:HiveTransport`, `SSL` from
`Settings:HiveUseSsl` — and `Settings:HiveExtraOptions` is the only free text in
it. A pasted connection string is where `UID` and `PWD` end up, so the free text
is read pair by pair and the eight credential keywords and five downgrade
keywords are refused by name. This is not advice; it is exit code 2.

### `binary` is not a transport option here

Kerberos does not support the binary Thrift transport at all. `http` maps to
`ThriftTransport=2` and `sasl` to `1`, and nothing else is accepted. Port 10001
is HiveServer2 over HTTP, 10000 is SASL, 21050 is Impala — a port and a
transport that disagree produce a connection failure at
[stage 1](#stage-1--the-two-rejections-that-share-exit-3), not a configuration
error, because the driver is the one that finds out.

### `FullRecrawlEveryRuns = 0` is reported, not silently accepted

Zero is legal and is refused loudly, because it disables the only mechanism that
re-derives an item's ACL after a permission change at the source. See
[stage 6](#stage-6--the-watermark-and-the-acl-staleness-bound). The message
names the consequence rather than the setting.

---

## Stage 1 — the two rejections that share exit 3

**Exit 3 means "this identity is no longer accepted".** It does not say which
identity, and that is deliberate. Entra rejecting the app registration and the
cluster rejecting the gMSA are the same class of fault, and a monitoring rule
keyed to exit 3 has to fire for both — otherwise a Kerberos ticket that stopped
renewing at three in the morning is investigated as a bug in the data path.

The log line is what separates them:

| Log line | Which credential | Read |
|---|---|---|
| `The source rejected this identity.` | The Windows service account, at HDFS, Hive or Ranger | below |
| `The credential was rejected by Entra ID.` | The app registration's certificate or client secret | [`TROUBLESHOOTING-DIRECT-PUSH.md` stage 1](TROUBLESHOOTING-DIRECT-PUSH.md#stage-1--credential-and-consent) |
| `Graph rejected the caller (403).` | The app registration's consented permissions | [`TROUBLESHOOTING-DIRECT-PUSH.md` stage 1](TROUBLESHOOTING-DIRECT-PUSH.md#the-roles-claim) |

### "The source rejected this identity."

That is the run-level line. The exception it wraps names which component said
no, and each one is a different thing to check.

**HDFS.** A 401 or 403 from any WebHDFS or HttpFS operation — `LISTSTATUS`,
`GETFILESTATUS`, `GETACLSTATUS`, `OPEN`, `GETCONTENTSUMMARY`:

```
HDFS refused this identity with 403 for GETACLSTATUS on /data/caseworks/private.
Check that the service account still holds a Kerberos ticket for the cluster's realm
and that Ranger still grants it read on this path.
```

**Hive or Impala.** SQLSTATE `28000` or `08004`, or any driver message
mentioning Kerberos, GSS, SSPI or authentication:

```
Hive refused this identity. The service account needs a valid Kerberos ticket for the
cluster's realm - check that it is running as the intended account and that the realm
still trusts it. No password is involved: this connector authenticates over SSPI.
```

**Ranger Admin.**

```
Ranger Admin refused this identity with 401. The service account needs read access to
the policy API, and Ranger Admin must accept Kerberos - this connector holds no password
to offer it.
```

That last one has a specific and common cause: **many Ranger installations front
the REST API with basic authentication against local Ranger users rather than
SPNEGO.** This client does Kerberos only, on purpose, because a password here
would be a secret in a configuration file. Enabling Kerberos authentication on
the Ranger Admin API is a cluster-side change, not a connector change.

### What to check, in order

1. **Which account is the process actually running as.** All three failures
   above are the same underlying question. `Get-Process` on the service, or the
   scheduled task's principal — an interactive test run as yourself proves
   nothing about the gMSA.
2. **Whether that account has a ticket.** `klist` as the service account. A gMSA
   obtains and renews its own ticket through the operating system; nothing in
   this process holds one, so there is nothing here to refresh by hand.
3. **Ticket lifetime and renewal.** A run that starts fine and fails hours in,
   partway through a large crawl, is the renewable lifetime expiring rather than
   a permission problem. The symptom is exit 3 with a partial index and a
   watermark that stopped where the last write landed.
4. **Realm trust.** If the cluster's realm has no trust to Active Directory, an
   AD identity cannot authenticate to it at all, and `Settings:KerberosMode` is
   the deliberate alternative: `MitKeytab` has the ODBC driver use its own MIT
   GSSAPI path against an operator-provisioned keytab. Note that it is the
   **driver** that reads the keytab, not this process, that SSPI cannot consume
   an MIT ticket cache — the two modes are alternatives, not layers — and that a
   keytab is a secret at rest and therefore never travels in the repository or
   the package.
5. **Ranger's grant to the service account itself.** HDFS 403 on one path while
   others work is a Ranger policy, not Kerberos. The account needs read on the
   paths it crawls, in addition to a ticket.

### One more exit 3, from the Graph side of group resolution

Only when `Settings:ResolveGroupsFromDirectory` is true:

```
Graph refused a group lookup. Settings:ResolveGroupsFromDirectory needs the
GroupMember.Read.All application permission, which the rest of this connector does not
use - grant it deliberately, or map the groups in Settings:EntraGroupMap instead.
```

`Settings:EntraGroupMap` needs no Graph permission at all and is the reviewable
form. A regulated deployment should prefer it.

---

## Stage 2 — Ranger, and why an unreachable one stops the run

**What has to be true.** `GET /service/public/v2/api/service/{service}/policy`
answers, over SPNEGO, for the service named in `Settings:RangerHdfsService` or
`Settings:RangerSqlService`.

**On success** the run logs, before it lists a single directory:

```
Read 42 Ranger polic(y/ies) from service cm_hdfs.
```

**When Ranger cannot be reached** the run fails with exit 4:

```
Ranger Admin at https://ranger01.corp.example:6182 could not be reached, so which tables
and paths may be indexed is unknown. The run stops rather than indexing a source whose
access policies it cannot read.
```

This is asked about more than anything else on this path, usually as "why did a
network blip lose us a night's crawl". The answer is in the sentence. Ranger is
the component that says which tables carry row filters and column masks and
which paths may be read at all. A connector that indexed the lake while unable
to see that would be copying exactly the data whose access rules it could not
evaluate — and it would do so silently, producing a successful-looking run and
an index nobody can justify. So an unreachable Ranger is a failure, never a
default, and never a warning followed by a crawl.

The cost of the refusal is one delayed run. The cost of the alternative is an
index that has to be deleted.

**A wrong service name** looks different, and says so:

```
Ranger Admin returned 404 for service 'cm_hive2'. Check the service name against
Ranger's own list; it is the CM service name, for example cm_hdfs or cm_hive.
```

### An HDFS root that Ranger denies

A root covered by any deny policy is skipped with its reason and the policy IDs,
and the crawl continues with the remaining roots:

```
Root /data/caseworks/private is not indexed: Ranger denies access to this path for at
least one principal. Deny rules are not mirrored into the index, so nothing under it is
indexed. (Ranger polic(y/ies) 118)
```

Note the asymmetry with tables: a **path** with no matching Ranger policy is not
refused, because the Ranger HDFS plugin falls back to the file's own POSIX
permissions and ACL, and those are read separately. "No Ranger grant" means
"Ranger adds nothing", not "nobody may read it".

### The item budget

Checked after the listing and before a single write:

```
1,830,402 item(s) are in scope, above the configured Settings:ItemBudget of 1000000.
Raise the budget deliberately, or narrow Settings:HdfsRoots and Settings:IncludeExtensions.
Nothing was written.
```

That is exit 4 with a number in it, which is the point: a connection discovering
its own tenant ceiling halfway through a crawl leaves a half-populated index and
no clear answer about what is in it. The budget turns that into a refusal with
the real count, before anything is written. `Settings:ItemBudget` is enforced by
the HDFS crawl; the Hive path is bounded by `Source:MaxItems`, which becomes a
`LIMIT` on the query.

A budget outside 0 to 100000000 is a configuration error at
[stage 0](#stage-0--configuration) instead: "must be between 0 and 100000000".

---

## Stage 3 — a table that returns no rows

**The run succeeds and the table produces nothing.** That is not a failure and
the run does not report it as one. `"Own it, index it. Entitle it at the source,
call it."` — the routing rules decide per table, from the cluster's own
policies, and a refusal means the table belongs in a live query.

The log line names the table, the reason and the policies:

```
contracts.contract_ppi is not indexed. Ranger applies a row-level filter to this table.
A filter shows different rows to different people at query time, and an index holds one
copy, so this table is queried live rather than indexed. Ranger polic(y/ies): 214.
Route this table to a live query under the user's own identity instead.
```

The five reasons, in the order they are evaluated:

| Reason in the log | What it means |
|---|---|
| "Ranger applies a row-level filter to this table. A filter shows different rows to different people at query time, and an index holds one copy, so this table is queried live rather than indexed." | Ranger policy type 2 covers the table |
| "Ranger masks at least one column of this table. A mask shows different values to different people at query time, and an index holds one copy, so this table is queried live rather than indexed." | Ranger policy type 1 covers the table |
| "Ranger denies access to this table for at least one principal. Deny rules are not mirrored into the index, because a mirrored deny that drifts fails open; the table is queried live so the source keeps enforcing its own denial." | The policy carries `denyPolicyItems` |
| "Ranger grants access to some columns of this table rather than all of them. Different people are entitled to different parts of each row, which one indexed copy cannot represent." | The grant is column-scoped rather than `*` |
| "No Ranger policy grants select on this table to any group. There is no principal to put on the indexed items, and an item granted to nobody is indexed and then returned to nobody." | No group is granted select |

Each of the first three describes the same impossibility from a different angle:
**one indexed copy cannot represent what the source would show two different
people.** A filter or a mask is a per-user transform, so indexing it either
leaks the unfiltered rows to everyone granted the item or stores the masked
version and lies to the people entitled to the real one. A deny is not mirrored
into a Graph deny ACE even though Graph has them and they take precedence,
because a deny only protects while the translation is right every time, and a
translation that drifts fails open. Refusing to index is the version that fails
closed.

The routing check runs **before the query**, not after it. The rows the service
account would see through its own row filter are never read at all.

There is a sixth way to get nothing, and it is not a routing decision:

```
contracts.contract resolves to no Entra group, so nothing from it is indexed.
```

That is [stage 4](#stage-4--why-one-item-is-missing-from-search): Ranger granted
the table to a group, and the group did not map to anything.

### Confirming it from outside the connector

`hadoop/02-create-ranger-test-policies.sh` finishes by telling you to run the
routing check from the connector host, and states the expected result:

```powershell
.\deploy\Test-RangerRouting.ps1 -RangerBaseUrl https://ranger01.corp.example:6182 -SqlService cm_hive
```

```
contracts.contract       INDEX       table-wide select, no filter or mask
contracts.contract_ppi   LIVE QUERY  Ranger applies a row-level filter
```

It reads the same API the connector reads, as the same identity, and applies the
same rules — so a disagreement between it and the connector is a finding, not a
configuration problem. Point `Source:ItemView` at `contracts.contract_ppi` and
run with `--dry-run`: the connector must read **no** rows and log why. If it
reads them, the routing rule has regressed.

---

## Stage 4 — why one item is missing from search

Four things skip an item, and only two of them log anything specific to it.
Check them in this order.

### 1. The group did not resolve

The commonest cause by a distance. A cluster group with no Entra mapping grants
nothing, and it is reported **once per group per run** rather than once per file:

```
Cluster group hadoop-audit-read does not resolve to an Entra group, so it grants nothing.
Items readable only by it will be skipped. Add it to Settings:EntraGroupMap, or enable
Settings:ResolveGroupsFromDirectory if its name matches an AD group synchronised to Entra.
```

An unresolved group is **dropped**, never guessed at and never replaced with a
fallback. Carrying on with a guess would widen the audience of exactly the item
whose permissions could not be established, which is the one item where widening
is least defensible.

A near-miss has its own line, and it also grants nothing:

```
Cluster group hadoop-contracts-read matches more than one Entra group by
onPremisesSamAccountName. It grants nothing until Settings:EntraGroupMap says which one
is meant.
```

The directory lookup matches on `onPremisesSamAccountName` rather than
`displayName` deliberately: a display name match would find a different group
that merely reads the same, which is the kind of near-miss that grants the wrong
people access.

### 2. The file's mode does not grant its group read

Grants come from three places, unioned: the **owning group** when the group
permission digit grants read, named `group:NAME:r--` entries in `GETACLSTATUS`,
and any Ranger path policy groups. A file owned by group `finance` with mode
`600` grants `finance` nothing, and treating ownership as access would be
inventing a grant the cluster does not give.

When nothing survives, the file is skipped before extraction and the warning
names the cluster groups it had:

```
/data/caseworks/private/board-pack-restricted.txt resolves to no Entra group and is not
indexed. Its cluster groups were:
```

**Read the group list in that line.** Empty means the file's own permissions
grant no group anything — mode 600, and correctly skipped. Populated means the
groups exist and did not map, which is cause 1 above, and the fix is
`Settings:EntraGroupMap`.

Two further things are deliberately not represented and neither is a defect:

- **The owning user.** Item ACLs are group principals only, never users and
  never everyone. Expanding memberships into item ACLs turns one HR change into
  a rewrite of every item. The effect is that the index can show a file to fewer
  people than the cluster would, never more.
- **The other-read bit**, unless `Settings:OtherReadableGroupId` names a group
  for it. "Everyone with an account on the cluster" and "everyone in the
  Microsoft 365 tenant" are different sets of people, and quietly treating them
  as one is how a lake becomes searchable by the whole company.

`default:` ACL entries are ignored. A default entry describes what a file
created in that directory will inherit, not who may read what is there now.

If an item somehow reaches the engine with no grants at all, the engine refuses
it too:

```
Item h3f2a... has no grants and was not written. The source could resolve no group for it.
```

An item granted to nobody is accepted by Graph and then returned to no one,
which reads as success and is not.

### 3. The table was routed to a live query

Covered at [stage 3](#stage-3--a-table-that-returns-no-rows). A Hive row that
never existed as an item is missing for that reason and no other.

### 4. The file extension is not in `IncludeExtensions`

**This one logs nothing per file.** `Settings:IncludeExtensions` is applied
during the walk, so a `.pdf` in a directory of `.docx` files is never a
candidate and never appears anywhere in the log. The only tell is the count:

```
14,203 file(s) in scope, 1,180 to read this run (incremental).
```

Compare "in scope" against what you expect the directory to hold. Empty
`IncludeExtensions` means every format this build can extract, which is not the
same as every file present — and the build matters: PDF is only present when the
build was made with `-p:EnablePdfExtraction=true`.

Names starting with `.` or `_`, and names ending `.tmp`, are also excluded
always. That is Hadoop's own litter — in-progress writes, Hive staging
directories, the `_SUCCESS` marker a job leaves behind — and none of it is a
document.

---

## Stage 5 — extraction and the error budget

A file whose text cannot be extracted is **still indexed**, by name, path,
owner, group and date, with a property saying why there is no body. A document
nobody can find is worse than a document found without its contents.

`extractStatus` is queryable and refinable, so the index itself will answer "how
much of the lake has no body, and why" — which is the question that decides
whether OCR is worth buying.

| `extractStatus` | Means | What to do |
|---|---|---|
| `Extracted` | Text was extracted | Nothing |
| `Empty` | The file parsed cleanly and genuinely holds no text | See below |
| `Unsupported` | No extractor in this build handles the type | Add the format, or accept metadata-only indexing |
| `TooLarge` | Above `Settings:MaxRawFileBytes`; the file was **not opened at all** | Raise the ceiling deliberately, knowing why it is there |
| `Failed` | An extractor recognised the type and could not parse it | See below |

**`Empty` at scale on PDFs means scanned documents.** A scan parses perfectly
and yields nothing, so it is `Empty` rather than `Failed`, and the difference is
the whole point of having two statuses: *`Empty` at scale means "buy OCR",
`Failed` means "something is wrong with these files".* Neither is a connector
fault, and reading the wrong one sends the investigation to the wrong place.

**`TooLarge` is decided from the reported size before a byte moves.** A lake
holds multi-gigabyte archives, and the cost of discovering that by streaming one
is a run that dies on memory.

**PDF needs the build switch.** Without `-p:EnablePdfExtraction=true` the PDF
extractor compiles to nothing and every PDF is `Unsupported`. If the whole lake
came back `Unsupported` for `pdf`, check the build before checking the files.
The parser is PdfPig, chosen because Apache-2.0 is a licence that can be
redistributed to a customer; do not swap it for a copyleft or per-server
licensed parser without that conversation.

### The error budget

```
83 of 1,204 file(s) examined failed to read or extract (6%), above
Settings:MaxErrorRatePercent of 5. The run stops rather than reporting a successful crawl
that was mostly skips. The watermark has not moved past the last item that was written.
```

Exit 4, partway through, with a partial index — and that is the better outcome.
The alternative is a systemically broken extractor or a sick DataNode being
laundered into a **successful** crawl of skips: exit 0, a green monitoring
dashboard, a watermark advanced over thousands of files nobody read, and an
index quietly missing a week of documents that nothing will ever revisit,
because their modification times are now behind the marker.

Stopping keeps the watermark where the last real write left it, so the next run
covers the same ground. Only `Failed` counts against the budget, and only after
50 files have been examined — below that sample one bad file is 100% and would
abort a run that is perfectly healthy.

The budget is a percentage of **examined** files, so a run that is failing on
everything trips it early and a run with a few bad documents in a million never
does. If it trips, read the run's `Failed` details before raising it: the
setting exists to make you look.

---

## Stage 6 — the watermark, and the ACL staleness bound

This is the section to read before anyone signs off the deployment.

The crawl is incremental. HDFS files are gathered, sorted by **(modification
time, path)**, and only those strictly after the stored marker are read;
`Settings:ScanSlackSeconds` (default 900) is subtracted from the marker to
absorb clock skew between this host and the NameNode. Hive rows are ordered by
(`Settings:HiveWatermarkColumn`, `Settings:HiveKeyColumn`) and resumed the same
way. The marker is composite because two files can share a timestamp to the
millisecond, and a marker of only the timestamp either re-reads that whole group
for ever or loses whichever of them had not been written when the run stopped.

The checkpoint lives in `Settings:CheckpointDirectory` (default `state`) as
`{connector key}.watermark.json`, written temp-then-rename so that a process
killed mid-write leaves either the old checkpoint or the new one, never half of
either. An unreadable one is treated as absent, which means a full recrawl, and
says so:

```
The checkpoint at C:\Connectors\Cdp\state\cdphdfsdocs.watermark.json could not be read
(...). This run re-reads everything, which is safe because writes are upserts, and writes
a fresh checkpoint at the end.
```

### An item is still visible after the source revoked access

**An incremental crawl cannot see a permission change.** This is the important
one, and it follows from something with no workaround: changing a file's
permissions, its owning group, its ACL entries or its Ranger policy **does not
alter the file's modification time**. The file therefore sorts behind the
marker, an incremental pass never revisits it, and the ACL written onto its item
on some earlier run stays exactly as it was. Graph is serving a correct copy of
a permission that no longer exists.

Nothing about that is visible in a run's output. The crawl reports what it read
and the number is correct.

**`Settings:FullRecrawlEveryRuns` is the only thing that fixes it.** Every Nth
run ignores the marker and re-reads everything, which re-derives every item's
ACL from the cluster's current answer. It is announced in the log:

```
Run 8 is a full recrawl (every 7 runs). Every file is re-read, which is what re-derives
item ACLs after a permission change at the source and picks up files moved into scope
with older timestamps.
```

and in the scope line:

```
14,203 file(s) in scope, 14,203 to read this run (full recrawl).
```

Three consequences to be explicit about:

1. **This setting is the documented upper bound on ACL staleness.** At the
   default of 7 and a daily schedule, an entitlement revoked at the source can
   remain in the index for up to seven days. That is a number that belongs in
   the deployment's risk record, not a default to inherit silently.
2. **The cadence counts successful crawls, not attempts.** The run counter only
   advances when the enumeration ended and every write returned, so a week of
   failing runs does not consume the cadence and then quietly skip the recrawl.
3. **Zero disables it entirely**, and validation refuses to let that happen by
   accident: "is 0, which disables the periodic full recrawl. That is also the
   only thing that re-derives item ACLs after a permission change at the source
   ... Set it to the number of runs you are willing to have stale ACLs for, or
   record the decision in the deployment's risk register and set it to 1."

If an entitlement change must reach the index sooner than the cadence allows,
the answer is to lower `FullRecrawlEveryRuns` or run one recrawl by hand — not
to edit an item's ACL, which the next crawl would overwrite anyway.

### A file renamed into scope

Same cause, same remedy. A file moved or renamed into a crawled directory keeps
its **old** modification time, so it sorts behind the marker and is skipped for
ever by every incremental pass.

`Settings:ScanSlackSeconds` does not solve this and is not meant to: it absorbs
seconds of clock skew, and nothing bounded absorbs a file that arrived carrying
a timestamp from last year. The periodic full recrawl is what catches it, which
is the second reason that setting exists.

The tell is a file that is plainly in a crawled root, plainly matches
`IncludeExtensions`, and never appears in the "to read this run" count. Confirm
it with `--dry-run` after temporarily moving the checkpoint file aside: a full
read will find it.

### `MaxItemsPerRun`, and why it is safe

```
Stopping at Settings:MaxItemsPerRun (5000). 9,203 file(s) in scope were not read this
run; the watermark advances only over what was written, so the next run continues from
there.
```

A cap is not a loss. The marker moves only to items the engine confirmed
written, so a capped run leaves an exact resume point.

Two things make that true, and both were bugs once:

- **The marker never moves backwards.** A run writes a marker only when it is
  strictly after the one already stored. Without that, a *full recrawl* — which
  deliberately ignores the marker and restarts oldest-first — would write its
  truncated position over the high-water mark, and the reachable corpus would be
  permanently bounded at `MaxItemsPerRun` × `FullRecrawlEveryRuns`, with the
  newest files unreachable by any run.
- **A truncated run does not count towards the recrawl cadence.** If the cap
  stops a full recrawl partway, `runCount` stays where it was, so the next run
  re-enters the recrawl and continues it. The cadence exists to re-derive item
  ACLs, and a recrawl that read a fifth of the corpus did not do that.

The symptom to watch for, if you ever see it again, is a backlog that saws:
35,000 unread, then 30,000, down to 5,000, then back to 35,000. That is a
recrawl rewinding the marker, not a cluster growing.

### Rows whose watermark column is NULL are not indexed

```
Rows with a NULL value in last_modified_ts are not indexed: an incremental crawl
cannot order a row it cannot compare. 3 such row(s) exist in contracts.contract.
```

An incremental Hive crawl resumes by comparing each row's watermark against the
stored marker, and a NULL compares to nothing. Hive also sorts NULLs **first** on
an ascending order, so leaving them in is worse than losing them: with
`Source:MaxItems` set, a whole window can fill with rows that commit no marker,
the checkpoint never moves, and the crawl re-reads the same first N rows on every
run while reporting success each time.

They are therefore excluded from the query, and the exclusion is logged rather
than silent. If the count is not zero, the fix is at the source — backfill the
column, or point `Settings:HiveWatermarkColumn` at one that is never null.

---

## Stage 7 — a push never deletes

**Nothing on this path removes an item, ever.** A file deleted from HDFS, a row
dropped from a table, a directory moved out of `Settings:HdfsRoots`, a table
that Ranger has since row-filtered — all of them simply stop being produced by
the source. Their items stay in the index, still searchable, still cited, and
never refreshed again.

The deletions themselves are handled gracefully during the crawl and leave no
trace worth alerting on:

```
/data/caseworks/contracts/old.txt does not exist and was skipped.
```

That is a file deleted between the listing and the read. Normal in a live lake.

### Finding orphans

There is **no list-items API**. The `externalItem` resource documents Create,
Get, Update, Delete and `addActivities` and nothing else, so an index cannot be
enumerated and compared. What CDP has that the SQL path does not is a
**derivable item ID**: the ID is a hash of the source's own identifier, so if
you know the path or the key, you can compute the item and ask Graph about it
directly.

```powershell
# The item ID of an HDFS path, derived exactly as the connector derives it.
$path   = '/data/caseworks/contracts/contract-C-1000.txt'
$sha    = [System.Security.Cryptography.SHA256]::Create()
$hash   = $sha.ComputeHash([System.Text.Encoding]::UTF8.GetBytes($path))
$itemId = 'h' + (($hash | ForEach-Object { $_.ToString('x2') }) -join '')
$itemId
```

A Hive row's ID is the same construction with a `t` prefix, over the qualified
table name followed by the row's natural key.

Removing one is a `DELETE` against
`v1.0/external/connections/cdphdfsdocs/items/{itemId}`. Do not automate it
against a list you have not checked: deleting an index item is not reversible,
and a mistyped connection ID deletes from a connection you did not mean.

**The gap that cannot be closed.** A file deleted from a directory nobody
remembers leaves nothing to derive an ID from. Those orphans cannot be found by
any client. If large-scale deletion has happened, the only reliable repair is to
delete the connection — which deletes every item in it — and crawl again from
scratch, including the 5-to-15-minute schema registration.

### Duplicates

```
Item t9b1c... appeared more than once (row 4,012); the later row overwrote the earlier item.
```

The write is an upsert, so a repeated ID silently overwrites while the count
claims both. The run summary reports it:

```
Ingestion complete. 4,180 row(s) processed (contract=4180) for connection cdphivecontracts;
4,176 distinct item(s). truncated=0 skipped=4 duplicates=4 throttleWaits=0
```

A gap between "row(s) processed" and "distinct item(s)" is a source producing
more than one row per item — for Hive, a key column that is not unique. For
HDFS, paths are unique, so a duplicate there means a root list that walks the
same directory twice by two different names.

---

## Traps that cost an afternoon

| Trap | Presents as |
|---|---|
| Ranger unreachable for a minute | Exit 4 and a lost run, deliberately — the run never defaults to indexing |
| Ranger Admin behind basic auth | Exit 3 at `Ranger Admin refused this identity with 401` before anything is crawled |
| Running the connector interactively as yourself | Everything passes, and says nothing about the gMSA the service uses |
| Kerberos ticket lifetime shorter than the crawl | Exit 3 hours in, with a partial index and a stopped watermark |
| `HiveTransport` and `HivePort` disagreeing | A driver connection failure at stage 1, not a configuration error |
| A cluster group with no `EntraGroupMap` entry | Items silently skipped; one warning per group per run, not per file |
| A file with mode 600 | Correctly skipped — read the empty group list in the warning before changing anything |
| `IncludeExtensions` omitting a format | No log line at all; only the "in scope" count moves |
| A build without `-p:EnablePdfExtraction=true` | Every PDF `Unsupported`, and the lake looks like it has no documents |
| `Empty` read as `Failed` | Buying a fix for scanned PDFs that OCR was the answer to |
| A permission change at the source | Invisible to every incremental crawl until the next full recrawl |
| A file renamed into a crawled directory | Never indexed; `ScanSlackSeconds` will not save it |
| `FullRecrawlEveryRuns` lowered to 0 to "save time" | The ACL staleness bound removed, and refused at startup for saying so |
| `--dry-run` expected to advance the watermark | It never does, by design |
| Two connectors pointed at one connection | Refused: "is the connection belonging to the 'cdphivecontracts' connector" |
| A connection created by another connector | "carries a schema this connector did not register" |
| A push never deleting | Deleted files cited by Copilot indefinitely |

---

## Before escalating

```powershell
.\Test-GraphPushPrereqs.ps1 -ConfigPath .\appsettings.cdphdfsdocs.json -SkipSql > prereqs.txt
Get-Content .\Logs\CdpGraphPush.log -Tail 300                                   > push.txt
Get-Content .\state\cdphdfsdocs.watermark.json                                   > watermark.txt
klist                                                                            > tickets.txt
```

Add the exit code of the failing run, which connector was selected, and the
Ranger policy count from the `Read N Ranger polic(y/ies)` line — a run that
never reached that line failed before the crawl started, which halves the search
immediately. As with every other path here, the single most useful sentence is
which stage was the **last one that passed**.

None of those four files contains file content, a row value, a credential or a
keytab path. The log writes item IDs, paths, counts and byte sizes only, by
design; WebHDFS error bodies are deliberately dropped rather than logged,
because they echo the path and a Java stack trace into a file a wider group can
read than can read the cluster. `klist` prints principal names and ticket
lifetimes, not keys.
