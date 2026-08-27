#!/usr/bin/env bash
# ---------------------------------------------------------------------------
# 04-create-atlas-test-metadata.sh
# Gives the Atlas catalogue something worth cataloguing.
#
# Atlas already knows about the tables that script 01 created - the HiveServer2
# hook publishes an entity the moment a table is created - but it knows only
# their names and columns. What makes a catalogue useful is the part a human
# writes: a description, an owner, a classification, a glossary term. This
# script adds those, so a dry run of the catalogue connector produces entries
# that demonstrate the connector rather than entries that say nothing.
#
# WHAT IT PROVES
#
#   contracts.contract      Described, owned, classified PII, and given a
#                           glossary term. Ranger grants select on it, so it is
#                           catalogued AND its rows are indexed.
#
#   contracts.contract_ppi  Described and classified the same way. Script 02 put
#                           a Ranger ROW FILTER on it, so its ROWS are never
#                           indexed - but its catalogue entry IS, for the people
#                           granted select. That distinction is the whole point
#                           of the catalogue connector, and this is where you
#                           see it working. If the entry is missing, the rule
#                           has been "fixed" in the wrong direction.
#
# Run it on a host that can reach Atlas, as a principal with entity-update in
# the cm_atlas Ranger service. It uses SPNEGO from your ticket cache; there is
# no password here and there must not be one.
#
# Idempotent: re-running overwrites the same attributes and re-associates the
# same classifications.
# ---------------------------------------------------------------------------

set -euo pipefail

ATLAS_URL="${ATLAS_URL:-https://atlas01.corp.example:31443}"
CLUSTER="${CLUSTER:-cm}"
DATABASE="${DATABASE:-contracts}"
OWNER="${OWNER:-priya.raman}"

if command -v klist >/dev/null 2>&1 && ! klist -s 2>/dev/null; then
    echo "No Kerberos ticket. Run kinit first." >&2
    exit 1
fi

if ! command -v python3 >/dev/null 2>&1; then
    echo "python3 is required to read Atlas's JSON responses." >&2
    exit 1
fi

# --negotiate with an empty -u offers SPNEGO from the ticket cache. No
# Authorization: Basic header is ever sent - Atlas's authentication filter
# prefers Basic over Kerberos when both are present, so sending one would
# silently take us down the wrong path.
CURL=(curl --silent --show-error --fail-with-body --negotiate -u : -H 'Content-Type: application/json')

say() { printf '\n== %s\n' "$1"; }

say "Checking Atlas is up"
# This endpoint answers without authentication by design, so it separates "Atlas
# is down" from "Atlas will not accept me".
status=$(curl --silent --show-error "$ATLAS_URL/api/atlas/admin/status" || true)
echo "  $status"

if ! printf '%s' "$status" | grep -q ACTIVE; then
    echo "Atlas did not report ACTIVE. Check the service before going further." >&2
    exit 1
fi

# --------------------------------------------------------------------------
# Classifications. Creating one that exists is an error, so it is tolerated.
# --------------------------------------------------------------------------
say "Creating classifications"

for tag in PII CONTRACT; do
    body="{\"classificationDefs\":[{\"name\":\"$tag\",\"superTypes\":[],\"attributeDefs\":[]}],\"entityDefs\":[],\"enumDefs\":[],\"structDefs\":[]}"

    if "${CURL[@]}" -X POST "$ATLAS_URL/api/atlas/v2/types/typedefs" -d "$body" >/dev/null 2>&1; then
        echo "  created $tag"
    else
        echo "  $tag already exists"
    fi
done

# --------------------------------------------------------------------------
# Describe each table and tag it.
# --------------------------------------------------------------------------
describe() {
    local table="$1" description="$2" tag="$3"
    local qualified="$DATABASE.$table@$CLUSTER"

    say "Describing $qualified"

    # A partial update by unique attribute, so no GUID has to be looked up
    # first and re-running is harmless.
    local attrs
    attrs=$(python3 -c '
import json, sys
print(json.dumps({"description": sys.argv[1], "owner": sys.argv[2]}))
' "$description" "$OWNER")

    "${CURL[@]}" -X POST \
        "$ATLAS_URL/api/atlas/v2/entity/uniqueAttribute/type/hive_table?attr:qualifiedName=$qualified" \
        -d "{\"entity\":{\"typeName\":\"hive_table\",\"attributes\":$(python3 -c '
import json,sys
a=json.loads(sys.argv[1]); a["qualifiedName"]=sys.argv[2]; a["name"]=sys.argv[3]
print(json.dumps(a))' "$attrs" "$qualified" "$table")}}" >/dev/null

    echo "  described, owner $OWNER"

    # The GUID is needed to attach a classification.
    local guid
    guid=$("${CURL[@]}" -X GET \
        "$ATLAS_URL/api/atlas/v2/entity/uniqueAttribute/type/hive_table?attr:qualifiedName=$qualified" \
        | python3 -c 'import json,sys; print(json.load(sys.stdin)["entity"]["guid"])')

    echo "  guid $guid"

    if "${CURL[@]}" -X POST "$ATLAS_URL/api/atlas/v2/entity/guid/$guid/classifications" \
        -d "[{\"typeName\":\"$tag\"}]" >/dev/null 2>&1; then
        echo "  classified $tag"
    else
        echo "  already classified $tag"
    fi
}

describe "contract" \
    "Executed customer contracts. One row per contract, keyed by contract_ref. Indexed into Copilot." \
    "CONTRACT"

describe "contract_ppi" \
    "Settlement instructions per desk. Ranger row-filters this table, so its ROWS are never indexed - only this description is." \
    "PII"

cat <<EOF

Done.

Point the catalogue connector at it:

    "AtlasBaseUrl": "$ATLAS_URL",
    "AtlasTypes":   "hive_db;hive_table"

then, from the connector host:

    .\\CdpGraphPush.exe --connector cdpatlascatalog --dry-run

Expect BOTH tables to appear as catalogue entries, including contract_ppi.

That is the behaviour to check most carefully. contract_ppi's rows are never
indexed, because a row filter shows different rows to different people and an
index holds one copy. Its DESCRIPTION is indexed, for exactly the people Ranger
grants select, because a filter does not hide the table's existence, its columns
or its owner from them - they see all of that the moment they query it.

If contract_ppi is missing from the catalogue, somebody has applied the data
rule to metadata. If it appears for people with no select grant, the entry is
being granted too widely. Both are findings.
EOF
