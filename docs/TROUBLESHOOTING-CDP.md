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
  │ HDFS       │ 1 │ CdpGraphPush     │ 4 │ Graph      │ ┌──5───→│ Microsoft Search │
  │ HttpFS     │──→│ Windows Server   │──→│ ingestion  │─┤       └──────────────────┘
  ├────────────┤   │ running as gMSA  │   └────────────┘ │       ┌──────────────────┐
  │ Hive/Impala│ 2 │                  │                  └──6───→│ Copilot semantic │
  │ ODBC       │──→│                  │                          │ index            │
  ├────────────┤   │                  │                          └──────────────────┘
  │ Atlas      │ 3 │                  │
  │ REST       │──→│                  │
  └────────────┘   └──────────────────┘
```

The same picture with both delivery paths on it, and a panel on what
deliberately never reaches the index, is [`architecture.png`](architecture.png).

**Two identities, one process.** Hops 1 to 3 are Kerberos as the Windows
account the process runs as — a gMSA, over SSPI and HTTP Negotiate, with no
password and no keytab anywhere. Hop 4 is the Entra app registration and its
certificate. They fail independently and for unrelated reasons, and the exit
code deliberately does not distinguish them; see
[stage 1](#stage-1--the-two-rejections-that-share-exit-3).

**Ranger is on the critical path, not beside it.** Nothing is indexed before the
policies are read, and an unreadable Ranger fails the run. That is the single
behaviour most often reported as a defect, and it is
[stage 2](#stage-2--ranger-and-why-an-unreachable-one-stops-the-run).

**The catalogue connector is stricter than the cluster, on purpose.** CDP ships
Atlas with a Ranger policy that lets every authenticated user read every entity,
and `cdpatlascatalog` does not mirror it. That, and the row-filtered table whose
catalogue entry is indexed while its rows are not, are
[stage 8](#stage-8--the-catalogue-connector).

---

## The three connectors

One executable hosts all three. Each has its own connection, its own schema and
its own checkpoint file, and they must never share a connection ID.

| | `cdphdfsdocs` | `cdphivecontracts` | `cdpatlascatalog` |
|---|---|---|---|
| Run | `CdpGraphPush.exe --connector cdphdfsdocs` | `CdpGraphPush.exe --connector cdphivecontracts` | `CdpGraphPush.exe --connector cdpatlascatalog` |
| Configuration | `appsettings.cdphdfsdocs.json` | `appsettings.cdphivecontracts.json` | `appsettings.cdpatlascatalog.json` |
| Source | HttpFS or WebHDFS at `Settings:HdfsBaseUrl` | Hive or Impala over ODBC | Atlas REST at `Settings:AtlasBaseUrl` |
| Indexes | file content | rows | metadata about databases, tables and paths |
| Ranger service | `Settings:RangerHdfsService` (`cm_hdfs`) | `Settings:RangerSqlService` (`cm_hive`) | `Settings:RangerSqlService` (`cm_hive`), for who may see an entry |
| Watermark | (modification time, path) | (`HiveWatermarkColumn`, `HiveKeyColumn`) | (Atlas modification time, entity GUID) |
| Checkpoint | `state\cdphdfsdocs.watermark.json` | `state\cdphivecontracts.watermark.json` | `state\cdpatlascatalog.watermark.json` |
| Item ID | `h` + SHA-256 of the path | `t` + SHA-256 of table + key | `a` + the Atlas GUID with its hyphens removed |

The catalogue's item ID is the only one that is not a hash, because it does not
need to be: an Atlas GUID is a UUID, so stripping its hyphens leaves 32
characters that are already ASCII alphanumeric and well inside the 128 Graph
allows. Hashing it would only make it unreadable in a log, and the ID is what
lets an entry be found in Graph from the GUID Atlas shows in its own UI.

The configuration file is resolved as `appsettings.{connector key}.json` beside
the executable, falling back to `appsettings.json`. The log is named after the
**executable**, so all three connectors write `Logs\CdpGraphPush.log`.

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
| Exit 3, "Atlas refused this identity" | [8](#exit-3-and-the-ranger-service-that-is-not-hadoop-sql) — `cm_atlas`, which is not the Hadoop SQL service |
| Exit 4, "Atlas at ... could not be reached" or "... returned N" | [8](#an-atlas-that-cannot-be-read-stops-the-run) |
| Exit 4, "has polic(y/ies) carrying an allowExceptions block / a condition / a validity schedule / isDenyAllElse" | [Ranger constructs](#a-policy-carrying-something-the-evaluator-cannot-honour) — control CDP-18 |
| Exit 4, "tag service ... holds N polic(y/ies) that deny or mask" | [The tag plane](#a-tag-policy-that-denies-or-masks) — control CDP-19 |
| A table has no catalogue entry in search | [8](#an-entry-missing-from-search) — four causes, only one of which logs |
| A catalogue entry exists but the table's rows are not indexed | [8](#the-entry-is-there-and-the-data-is-not) — **correct, and not a defect** |
| A catalogue entry describes fewer columns than the table has | [8](#fewer-columns-described-than-the-table-has) |
| A catalogue entry with no lineage on it | [8](#an-entry-with-no-lineage) |
| The catalogue connector re-reading everything every run | [8](#every-run-reads-the-whole-catalogue) — expected, and cheap |
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
- It fails `Acl:GrantGroupObjectIds` for being empty. Empty is correct for all
  three CDP connectors: every item carries the grants HDFS and Ranger give it —
  for a catalogue entry, the groups Ranger grants select on the table it
  describes — so a connection-wide grant would be wrong for almost every item,
  and an item whose groups cannot be resolved is skipped rather than granted
  that list.

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
| `Settings:AtlasBaseUrl` | it is empty | "is required but was empty." — there is no default, for the reason at [stage 8](#why-atlasbaseurl-has-no-default) |
| `Settings:AtlasBaseUrl` | the scheme is not https | "must be https. Kerberos on Atlas requires TLS in CDP, and the catalogue describes the shape of the lake - table names, column names and owners - which is not something to put on the wire in clear." |
| `Settings:AtlasBaseUrl` | it contains `/api/atlas` | "must be the base URL only, without /api/atlas. The connector appends the API path itself, which is also what makes a Knox gateway path work." |
| `Settings:AtlasTypes` | it is empty | "must list at least one Atlas entity type, separated by semicolons, for example hive_db;hive_table." |
| `Settings:AtlasPageSize` | it is outside 1 to 10000 | "must be between 1 and 10000; found 25000." Atlas caps a page at `atlas.search.maxlimit`, which is 10,000 by default |

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

**Atlas**, on the catalogue connector only:

```
Atlas refused this identity with 403. The service account needs read access to the entities it is
to catalogue, and Atlas must accept Kerberos - this connector holds no password to offer it.
```

Atlas authorises through a Ranger service of its own, which is not the one that
decides what may be indexed, and a 401 and a 403 here mean different things.
Both are at [stage 8](#exit-3-and-the-ranger-service-that-is-not-hadoop-sql).

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
`LIMIT` on the query. The catalogue crawl enforces neither — the setting is
still range-checked at stage 0, and the catalogue is thousands of entities
rather than millions, so the only cap that acts on it is
`Settings:MaxItemsPerRun`, which on that connector has a consequence worth
reading first at [stage 8](#every-run-reads-the-whole-catalogue).

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

## Stage 8 — the catalogue connector

`cdpatlascatalog` indexes what the lake **contains** rather than what is in it:
one item per Atlas entity — `hive_db` and `hive_table` by default, `hdfs_path`
when `Settings:AtlasTypes` asks for it — carrying the name, the qualified name,
the owner, the description, the columns, Atlas's classifications and glossary
terms, one dataset hop of lineage each way, and a modified timestamp. It is the only
connector here that can describe data it may not index, and most of what gets
reported against it comes from that one sentence.

**Two Ranger services are involved, and they decide different things.** Atlas
authorises through a Ranger service of its own, separate from Hadoop SQL.
Confusing the two accounts for more time lost on this connector than everything
else in this stage put together.

| Service | Decides |
|---|---|
| `cm_atlas` | whether the **service account** may read an entity out of Atlas at all |
| `Settings:RangerSqlService` (`cm_hive`) | who a catalogue entry is **granted to** once it has been read |

A deny in Hadoop SQL does not hide a table's metadata in Atlas — Atlas never
consults that service, and on a stock cluster the "public" policy in `cm_atlas`
lets every authenticated user read every entity. It is this connector, not the
cluster, that refuses to publish such an entry, and
[why](#who-an-entry-is-granted-to-and-why-it-is-narrower-than-the-cluster) is
the part worth reviewing.

### Exit 3, and the Ranger service that is not Hadoop SQL

The run-level line is the shared one, `The source rejected this identity.`, and
the exception it wraps names Atlas:

```
Atlas refused this identity with 403. The service account needs read access to the entities it is
to catalogue, and Atlas must accept Kerberos - this connector holds no password to offer it.
```

The status code separates two different faults:

- **401** is Atlas not accepting the Kerberos exchange at all. Same question as
  every other 401 on this path — which account is the process running as, and
  does it hold a ticket for the cluster's realm. Work through
  [stage 1's list](#what-to-check-in-order); nothing about it is
  Atlas-specific.
- **403** is Kerberos having worked and the account not being granted. The
  service account needs **entity-read in the `cm_atlas` Ranger service**. That
  is a different policy list from the one deciding which tables may be indexed,
  and an account holding every Hadoop SQL grant in the cluster can still be
  refused by it. Grant it under `cm_atlas`, not under `cm_hive`.

Authentication is SPNEGO as the service account, exactly as it is for HDFS, Hive
and Ranger. The client sends no `Authorization` header of its own and must not:
Atlas's authentication filter prefers basic authentication over Kerberos when
both are enabled, so a password put in a header would quietly replace the
identity the rest of this path uses — and it would be a secret in a
configuration file, which stage 0 already refuses on the ODBC side for the same
reason.

One further Atlas-specific detail: the catalogue is read with `GET
/api/atlas/v2/search/basic` rather than the POST form. Atlas installs its own
CSRF filter in front of non-GET REST calls, and whether that filter demands a
header depends on `atlas.rest-csrf.enabled` at the cluster — a setting this
connector cannot see and should not depend on. The GET form takes the same
parameters, so a CSRF configuration change on the cluster cannot break this run.

### An Atlas that cannot be read stops the run

Two messages, both exit 4, both fatal. The first is a connection that never
completed:

```
Atlas at https://atlas01.corp.example:31443 could not be reached, so the catalogue cannot be read.
Check the base URL - Atlas's port differs between CDP topologies and again when Knox fronts it -
and that this host can reach it.
```

The second is Atlas answering with something that is not a success:

```
Atlas at https://atlas01.corp.example:31443 returned 503, so the catalogue cannot be read. The run
stops rather than indexing part of it. Check that the Atlas service is healthy -
/api/atlas/admin/status answers without authentication and returns ACTIVE on a working instance -
and that this host may reach it.
```

**Why a partial read is refused** is the same argument as the unreachable Ranger
at [stage 2](#stage-2--ranger-and-why-an-unreachable-one-stops-the-run), and it
is sharper here. A catalogue read that half worked publishes a partial map of
the lake and presents it as the whole one. Nothing in the index distinguishes a
table Atlas failed to return from a table that does not exist, so somebody
searching for a dataset that is missing concludes it is not there and builds a
second copy of it. An index whose gaps are invisible is worse than no index.

A **404 is not fatal**, and the asymmetry is deliberate: an entity deleted
between the search and the detail read is normal in a live catalogue, so that
one entity is indexed from what the search already returned rather than failing
the run.

**The preflight is one command and needs no credential:**

```powershell
curl.exe -sk https://atlas01.corp.example:31443/api/atlas/admin/status
```

`/api/atlas/admin/status` answers without authentication, and a healthy instance
returns `ACTIVE`. That makes it the fastest way to separate the three faults
that look identical from the log: no answer at all means the host, port or
firewall is wrong; `PASSIVE` means this is the standby of an HA pair and the
active one is elsewhere; `ACTIVE` means Atlas is healthy and the problem is the
identity, which is exit 3 rather than exit 4.

Neither message carries a response body, and that is on purpose. An Atlas error
body echoes the request and a Java stack trace, and neither belongs in a log a
wider group can read than can read the catalogue.

### Why `AtlasBaseUrl` has no default

Every other URL setting on this path has a shape that can be guessed;
`Settings:AtlasBaseUrl` does not. Atlas answers on **31443** in a stock CDP 7.1.9
install (31000 without TLS, which this connector refuses), on **21443** in
upstream Atlas, and on the **Knox gateway's own port and path** when Knox fronts
it. A default that happens to be wrong for the topology in front of you produces
a connection error at the least helpful moment — during the first run, on a
Kerberised host, where every other explanation is more plausible than a guessed
port. So the operator states it, and an empty value is exit 2 at
[stage 0](#stage-0--configuration).

The value must be the base URL **without** `/api/atlas`. The connector appends
the API path itself, which is also what lets a Knox gateway path work: with the
gateway's path in the base URL and the API path appended to it, one setting
covers both topologies. Pasting a full API URL out of a browser is the common
mistake, and it is refused at startup rather than producing a 404 halfway
through a run.

### Who an entry is granted to, and why it is narrower than the cluster

An entry is granted to **exactly the groups Ranger grants select on the table it
describes**, and skipped when that is nobody. It does not inherit Atlas's own
answer, which on a stock CDP cluster is "everyone with an account".

"Everyone with a cluster account" and "everyone in the Microsoft 365 tenant" are
different populations. Inheriting the first into the second would publish the
shape of the lake — table names, column names, owners, what is classified as PII
— to people who cannot reach the cluster at all, and who would have no way of
knowing the search result they are reading describes data they may not see.
Narrower than the source is the safe direction to be wrong in, and this is the
one connector where the source is deliberately not followed.

The four symptoms that follow are all that decision, seen from the outside.

### An entry missing from search

Four causes, and **only the first logs anything per entity.** Check them in this
order.

1. **The table's groups did not resolve.** The only per-entity line there is:

   ```
   The catalogue entry for contracts.contract@cm resolves to no Entra group and is not indexed.
   ```

   Ranger granted select to a group, and the group mapped to nothing. Same cause
   and same fix as [stage 4](#1-the-group-did-not-resolve):
   `Settings:EntraGroupMap`, or `Settings:ResolveGroupsFromDirectory`. An
   unresolved group is dropped rather than guessed at, here as everywhere else.

2. **No Ranger policy grants select on the table to any group.** There is nobody
   to grant the entry to, and an entry granted to nobody is indexed and then
   returned to no one.

3. **A deny covers the table.** A deny refuses the catalogue entry as well as
   the rows, because **a description of a table is still a disclosure about
   it**: the column list of `contracts.contract_ppi` says what the cluster holds
   about people even to somebody who can never read a row of it. Deny rules are
   not mirrored into the index anywhere in this connector, for the reason at
   [stage 3](#stage-3--a-table-that-returns-no-rows) — a mirrored deny that
   drifts fails open — so the entry is simply not written.

4. **Atlas scrubbed the entity before the connector saw it.** Ranger's plugin
   inside Atlas does not remove a search hit the caller may not read: it **blanks
   the header in place and sets its GUID to `-1`**, so the page length is
   unchanged and the entity arrives as an empty shell. Those are dropped, since
   indexing one would put a nameless item in the catalogue. This is the
   `cm_atlas` service refusing the **service account**, not a routing decision.

**Telling case 4 apart from cases 2 and 3** is a counting exercise, and the log
has the count. Every type logs one line:

```
Atlas returned 412 live hive_table entit(y/ies).
```

That number is what Atlas was willing to show the service account. Compare it
with the number of tables Atlas reports to an administrator in its own UI.

| What you see | Where the entity was lost |
|---|---|
| The count is **short** | Scrubbed by Atlas, or the type is not in `Settings:AtlasTypes`. The service account is missing entity-read in `cm_atlas` |
| The count is **right**, entries still missing, `skipped=` in the run summary is large | Refused after the read, by the Hadoop SQL policies — cause 2 or 3 |
| The count is right and the per-entity warning above is in the log | Cause 1, the group mapping |

Causes 2 and 3 log nothing per entity; they show only in the run summary's
`skipped=` count, alongside entities of a type this connector does not describe:

```
Ingestion complete. 412 row(s) processed (catalogue=412) for connection cdpatlascatalog;
412 distinct item(s). truncated=0 skipped=1,180 duplicates=0 throttleWaits=0
```

`Test-RangerRouting.ps1` will tell you which of 2 and 3 applies, because a deny
and an ungranted table both name their reason and their policy IDs there. Read
its verdicts with the caveat in the next section: **its `LIVE QUERY` is the
verdict for the table's data, not for its catalogue entry**, and for a filtered
or masked table the two deliberately disagree.

### The entry is there and the data is not

**This is correct. Do not fix it.** A table that
[stage 3](#stage-3--a-table-that-returns-no-rows) routes to a live query, because
Ranger applies a row-level filter or a column mask to it, still has a catalogue
entry in the index — and it is meant to.

A filter governs which **rows** a person sees. A mask governs which **values**
they see. Neither hides the table's existence, its column names or its owner
from somebody granted select on it: they see all of that the moment they run
`DESCRIBE`. So the entry discloses nothing to those people that the cluster does
not already show them, while the rows — the thing the filter and the mask
actually protect — are never read at all.

The tables hardest to index are frequently the ones a catalogue is most needed
for, precisely because their contents can never be indexed. A row-filtered
patient table that nobody can find is a table somebody rebuilds from scratch
rather than requesting access to.

The entry carries no rows and no values: the body is the name, the owner, the
description, the column names, the classifications, the glossary terms and one
hop of lineage. Nothing filtered and nothing masked is in it.

What this looks like when it is misread as a fault:

```
contracts.contract_ppi   LIVE QUERY  Ranger applies a row-level filter
```

That is `Test-RangerRouting.ps1` reporting the **data** verdict, and it is
correct. The catalogue rule is a different rule with a different answer, and the
five Atlas tests pinned in `ControlEvidenceTests` exist so that a well-meant
"fix" aligning the two fails the build rather than quietly removing the entries.
If the catalogue entry for a filtered table has genuinely gone missing, it is
cause 1, 2, 3 or 4 in the previous section — not the filter.

### Fewer columns described than the table has

A **column-scoped Ranger grant narrows rather than refuses**. Where a grant
names specific columns instead of `*`, only the named columns are described, and
`columnCount` on the item counts what was described rather than what the table
holds.

A column name discloses. One called `hiv_status` says something by existing, and
somebody granted three columns of a table has not been shown the other forty. So
the entry is written for exactly what the grant covers, which is narrower than
refusing the entry outright and much narrower than describing the whole table.

The tell is `columnCount` and the `Columns:` line in the body being short
against the real table. Check the policy's column resource in Ranger; a grant
naming `*`, or naming no column at all, constrains nothing and every column is
described. No Atlas setting changes this, and `Settings:AtlasTypes` is not
involved — a `hive_column` entity is never an item of its own, because a column
is described as part of its table and one item per column would multiply the
item count by fifty for no new answer.

### An entry with no lineage

Four causes, and nothing else. The last two are the connector working.

- **`Settings:AtlasIncludeLineage` is `false`.** It defaults to true and costs
  one extra request per table written, so it is the first thing turned off
  while proving the rest of the pipeline works, and the first thing forgotten
  afterwards.
- **The entity genuinely has none.** Lineage exists only where something
  recorded it — a Hive query through the Atlas hook, a Spark job, a Sqoop import
  — and a table loaded by a process that does not report to Atlas has no
  upstream to describe. Its lineage tab in Atlas is empty too, which is the
  check that separates the causes in a minute.
- **The entry is for a database.** Atlas serves lineage for entities deriving
  from `DataSet` or `Process`, and a `hive_db` derives from neither, so the
  connector does not ask. Asking returns HTTP 400 from a completely healthy
  Atlas — which is worth knowing, because it is what a `hive_db` in
  `Settings:AtlasTypes` used to do to a whole run.
- **Not everybody granted the entry is granted the neighbour.** A neighbour's
  *name* is a disclosure: "Produced from `hr.salaries_raw`" tells everyone
  granted the downstream table that a table of salaries exists and what it is
  called, and this entry's ACL has nothing to do with who may read that one.
  Atlas will not stop this — on a stock cluster its own policy shows every
  authenticated user every entity — so the connector checks each neighbour
  against Ranger itself and names it only when *every* group on this entry is
  also granted it. A neighbour that is not a Hive table, or whose qualified name
  will not parse, is dropped rather than guessed at. The count of hidden
  neighbours is logged at debug against the entry's qualified name.

`upstream` and `downstream` are omitted from the item rather than written empty,
so every cause looks the same on the item itself.

**What a hop means here.** Hive does not join two tables directly: it records
`table → hive_process → table`, and the process's own name is the query text
that produced it. The walk therefore goes *through* transformation nodes —
anything whose Atlas type name contains `process` or `lineage` — to the datasets
on the far side, which is why `direction=BOTH&depth=2` is requested for what is
described as one hop each way. Naming the immediate neighbour instead would put
raw SQL in the index, and that SQL names tables of its own. A second dataset hop
beyond that is not named: "what feeds this" is a useful answer and "the
transitive closure of what feeds this" is a graph nobody reads in a search
result.

### Every run reads the whole catalogue

Expected, and not worth investigating. **Atlas 2.1.0 — the version CDP 7.1.9
ships — cannot filter a basic search by modification time**, so there is no
incremental read to ask Atlas for. Every run enumerates every entity of every
type in `Settings:AtlasTypes`, orders them by (Atlas modification time, GUID) so
that the checkpoint means the same thing it means everywhere else here, and
writes the entries the routing check allows. Writes are upserts, so an entry
written again is an entry corrected, not a duplicate.

This is affordable because a catalogue is small — thousands of entities, not
millions — and it is stated plainly rather than dressed up as an optimisation.
The cost is one search request per `Settings:AtlasPageSize` entities, plus one
detail request per entry written, plus one lineage request when
`Settings:AtlasIncludeLineage` is true. Only entries that pass the routing check
pay the second and third.

Two consequences follow, and the first is a limit rather than a reassurance:

1. **The ACL staleness bound is `Settings:FullRecrawlEveryRuns` runs, exactly
   as it is for the other two connectors.** Reading every entity every run is
   not the same as re-deriving every entry's ACL every run. The marker filter is
   applied *before* the routing check, and a Ranger policy edit does not change
   an Atlas entity's modification time — so an entry whose grant changed but
   whose entity did not is dropped before any ACL is derived, and keeps the ACL
   it last had until the next full recrawl. What the whole-catalogue read buys
   is that nothing has to be *found* again, not that everything is re-decided.
   Record the `FullRecrawlEveryRuns` number for this connector too.
2. **When a table's grant is removed entirely, or a deny is added, the entry
   stops being written rather than being rewritten.** A push never deletes
   ([stage 7](#stage-7--a-push-never-deletes)), so the item stays in the index
   with the ACL it last had. This is the one revocation on this path that a
   later run cannot repair, and the remedy is to delete the item — which is
   straightforward here, because the ID is derivable from the GUID Atlas shows:
   `a` followed by the GUID with its hyphens removed, deleted against
   `v1.0/external/connections/cdpatlascatalog/items/{itemId}`.

**Leave `Settings:MaxItemsPerRun` at 0 on this connector.** Because the run is
not filtered by the watermark, a cap does not defer the rest to the next run —
it reads the same oldest N entries every time, and entries beyond the cap are
never written at all. The warning names the count that was left:

```
Stopping at Settings:MaxItemsPerRun (500). 1,180 catalogue entr(y/ies) were not read this run.
```

On the HDFS and Hive connectors that line means "resumed next run". Here it
means "not indexed", and it should be read as a configuration mistake rather
than as progress.

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
| A Hadoop SQL deny assumed to hide a table from Atlas | It does not. Atlas authorises through `cm_atlas`, and it is this connector that refuses the entry |
| A `cm_hive` grant assumed to let the service account read Atlas | It does not either; the read needs entity-read in `cm_atlas`, and its absence is exit 3 |
| `Settings:AtlasBaseUrl` pasted with `/api/atlas` on the end | Exit 2 at startup, before anything is read |
| A row-filtered table's catalogue entry read as a leak | Correct behaviour; those people already see the table's shape when they query it |
| `Test-RangerRouting.ps1` `LIVE QUERY` read as "no catalogue entry" | That is the verdict for the data; the catalogue rule deliberately differs |
| The whole catalogue re-read every run | Expected: Atlas 2.1.0 cannot filter a basic search by modification time |
| `Settings:MaxItemsPerRun` set on the catalogue connector | Entries beyond the cap are never written, not deferred to the next run |

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

For the catalogue connector, add two more things: the `Atlas returned N live
{type} entit(y/ies)` line for every type, which is what Atlas was willing to
show the service account, and the output of the unauthenticated status check,
which needs no credential and separates an unreachable Atlas from a refused
identity before anybody looks at Kerberos.

```powershell
curl.exe -sk https://atlas01.corp.example:31443/api/atlas/admin/status > atlas-status.txt
```

None of those files contains file content, a row value, a credential or a
keytab path. The log writes item IDs, paths, counts and byte sizes only, by
design; WebHDFS and Atlas error bodies are deliberately dropped rather than
logged, because they echo the request and a Java stack trace into a file a wider
group can read than can read the cluster. `klist` prints principal names and
ticket lifetimes, not keys, and the Atlas status document is a service state,
not a catalogue: it names no database, table or column.


## A policy carrying something the evaluator cannot honour

**Symptom.** Exit 4 before anything is written, naming one or more of
`allowExceptions`, `a condition`, `a validity schedule` or `isDenyAllElse`, with
policy ids.

**What it means.** This connector evaluates `policyItems`, `denyPolicyItems` and
`allowExceptions`. It does **not** evaluate conditions or validity schedules,
and reading either as absent makes the cluster more permissive than it is — so
the run stops rather than writing an access-control list it knows to be too
generous. Control CDP-18.

**Why it is not a warning.** A warning is read once and then not again, and the
failure it would be warning about is invisible: nothing downstream can tell an
over-granted item from a correct one.

**What to do.** Either remove the construct from the policies covering the
crawled resources, or scope the crawl to resources no such policy covers. The
message names the policy ids so you can open them directly in Ranger Admin.

**What will not work.** There is no setting that disables it. A time-varying
rule — a condition on a date, a validity window — has no representation in a
Microsoft 365 permission at all, which is a static snapshot with no clock, so
there is nothing to fall back to that would not be a guess.

**One case that does not stop the run:** `denyExceptions`, and a grant to a
named user. Both are logged as a warning. They fail closed — content the cluster
would show is left out of the index rather than content it hides being put in —
and a guard that fired on the safe direction would teach operators to disable
guards.

## A tag policy that denies or masks

**Symptom.** Exit 4, naming `Settings:RangerTagService` and a count of policies
that deny or mask.

**What it means.** Tag-based policies live on a separate Ranger service and are
evaluated against Atlas classifications rather than against resources. This
connector reads resource services only, so a tag deny is invisible to it.
Control CDP-19.

**What to do.** Establish whether Tagsync is running against Atlas and whether
any in-scope table or column actually carries a classification one of those
policies acts on. If nothing in scope does, the risk is theoretical and the
service name can be cleared with that established. If something does, tag
fidelity has to be designed for before the first crawl.

**What will not work.** Pointing `Settings:RangerTagService` at a service that
does not exist, to make the message go away. A missing service is treated as
absence and skips the check, which is correct only when the absence is real.
