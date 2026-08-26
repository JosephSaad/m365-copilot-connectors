#!/usr/bin/env bash
# ---------------------------------------------------------------------------
# 02-create-ranger-test-policies.sh
# The policies that make the test case mean something.
#
# Two of the three exist to be OBEYED rather than used. The connector reads
# Ranger to decide what may be indexed at all, and the interesting behaviour is
# the refusals:
#
#   1. Grant select on contracts.contract to a group.
#      => The connector indexes it and stamps that group on every item.
#
#   2. Put a ROW FILTER on contracts.contract_ppi.
#      => The connector must REFUSE to index it. A row filter shows different
#         rows to different people when a query runs; an index holds one copy
#         and cannot do that, so indexing it would publish the service
#         account's view of the table to everyone granted the item.
#
#   3. Grant read on the HDFS document roots to a group.
#      => Adds a grant on top of what the files' own POSIX permissions give.
#
# Run it on a host that can reach Ranger Admin, as a principal with the policy
# admin role. It uses SPNEGO - there is no password here and there must not be
# one; if your Ranger Admin only accepts basic auth, that is a cluster-side
# setting to change rather than a credential to put in this file.
#
# Idempotent: an existing policy of the same name is updated in place.
# ---------------------------------------------------------------------------

set -euo pipefail

RANGER_URL="${RANGER_URL:-https://ranger01.corp.example:6182}"
HDFS_SERVICE="${HDFS_SERVICE:-cm_hdfs}"
HIVE_SERVICE="${HIVE_SERVICE:-cm_hive}"
ROOT="${ROOT:-/data/caseworks}"

CONTRACTS_GROUP="${CONTRACTS_GROUP:-hadoop-contracts-read}"
POLICIES_GROUP="${POLICIES_GROUP:-hadoop-policies-read}"

if command -v klist >/dev/null 2>&1 && ! klist -s 2>/dev/null; then
    echo "No Kerberos ticket. Run kinit first." >&2
    exit 1
fi

# --negotiate with an empty -u is how curl offers SPNEGO from the ticket cache.
# No credential is read from anywhere.
CURL=(curl --silent --show-error --fail-with-body --negotiate -u : -H 'Content-Type: application/json')

post_policy() {
    local service="$1"
    local name="$2"
    local body="$3"

    printf '\n== %s on %s\n' "$name" "$service"

    # Ranger has no upsert, so delete-then-create is how a rerun stays clean.
    "${CURL[@]}" -X DELETE \
        "$RANGER_URL/service/public/v2/api/policy/service/$service/name/$name" >/dev/null 2>&1 || true

    "${CURL[@]}" -X POST "$RANGER_URL/service/public/v2/api/policy" -d "$body" \
        | python3 -c 'import json,sys; p=json.load(sys.stdin); print("  created policy id", p.get("id"), p.get("name"))'
}

# --------------------------------------------------------------------------
# 1. The indexable table.
# --------------------------------------------------------------------------
post_policy "$HIVE_SERVICE" "caseworks-contract-select" "$(cat <<EOF
{
  "service": "$HIVE_SERVICE",
  "name": "caseworks-contract-select",
  "description": "Table-wide select for the CaseWorks connector test case. Indexable.",
  "isEnabled": true,
  "policyType": 0,
  "resources": {
    "database": { "values": ["contracts"], "isExcludes": false, "isRecursive": false },
    "table":    { "values": ["contract"],  "isExcludes": false, "isRecursive": false },
    "column":   { "values": ["*"],         "isExcludes": false, "isRecursive": false }
  },
  "policyItems": [
    {
      "groups": ["$CONTRACTS_GROUP"],
      "accesses": [{ "type": "select", "isAllowed": true }],
      "delegateAdmin": false
    }
  ]
}
EOF
)"

# --------------------------------------------------------------------------
# 2. The table the connector must refuse.
#
# policyType 2 is a row filter. The expression restricts each user to their own
# desk, which is exactly the thing one indexed copy of a row cannot express.
# --------------------------------------------------------------------------
post_policy "$HIVE_SERVICE" "caseworks-contract-ppi-rowfilter" "$(cat <<EOF
{
  "service": "$HIVE_SERVICE",
  "name": "caseworks-contract-ppi-rowfilter",
  "description": "Row filter. The connector must route this table to a live query and never index it.",
  "isEnabled": true,
  "policyType": 2,
  "resources": {
    "database": { "values": ["contracts"],    "isExcludes": false, "isRecursive": false },
    "table":    { "values": ["contract_ppi"], "isExcludes": false, "isRecursive": false }
  },
  "rowFilterPolicyItems": [
    {
      "groups": ["$CONTRACTS_GROUP"],
      "accesses": [{ "type": "select", "isAllowed": true }],
      "rowFilterInfo": { "filterExpr": "owning_desk = 'emea-desk'" }
    }
  ]
}
EOF
)"

# --------------------------------------------------------------------------
# 3. HDFS read, on top of the files' own permissions.
# --------------------------------------------------------------------------
post_policy "$HDFS_SERVICE" "caseworks-hdfs-documents-read" "$(cat <<EOF
{
  "service": "$HDFS_SERVICE",
  "name": "caseworks-hdfs-documents-read",
  "description": "Read on the CaseWorks document roots for the connector test case.",
  "isEnabled": true,
  "policyType": 0,
  "resources": {
    "path": { "values": ["$ROOT/contracts", "$ROOT/policies"], "isExcludes": false, "isRecursive": true }
  },
  "policyItems": [
    {
      "groups": ["$CONTRACTS_GROUP", "$POLICIES_GROUP"],
      "accesses": [
        { "type": "read",    "isAllowed": true },
        { "type": "execute", "isAllowed": true }
      ],
      "delegateAdmin": false
    }
  ]
}
EOF
)"

cat <<EOF

Done. Verify from the connector host:

    .\\deploy\\Test-RangerRouting.ps1 -RangerBaseUrl $RANGER_URL -SqlService $HIVE_SERVICE

Expect exactly this:

    contracts.contract       INDEX       table-wide select, no filter or mask
    contracts.contract_ppi   LIVE QUERY  Ranger applies a row-level filter

and then:

    .\\CdpGraphPush.exe --connector cdphivecontracts --dry-run

Expect the contract rows listed. Point Source:ItemView at contracts.contract_ppi
and re-run: the connector must read NO rows and log why. If it reads them, the
routing rule has regressed and that is a finding, not a configuration problem.
EOF
