#!/usr/bin/env bash
# ---------------------------------------------------------------------------
# 00-create-hdfs-test-data.sh
# Builds the HDFS half of the CDP test case: three directories of documents
# with deliberately different permissions, so the connector's ACL rules can be
# seen working rather than assumed.
#
# Run it on a cluster edge node, as a principal that can create the directories
# and set their groups. It is idempotent: re-running replaces the files and
# re-applies the permissions.
#
# WHAT IT PROVES
#
#   /data/caseworks/contracts   mode 640, group hadoop-contracts-read
#                               The ordinary case. Indexed, granted to the
#                               Entra group that hadoop-contracts-read maps to.
#
#   /data/caseworks/policies    mode 640, group hadoop-policies-read, plus a
#                               named ACL entry for hadoop-audit-read.
#                               Two groups on one file: the item should come
#                               back with two grants.
#
#   /data/caseworks/private     mode 600. Nobody's group can read it, so no
#                               grant can be derived and the connector must
#                               SKIP it - not index it with a fallback grant.
#                               If this file ever appears in search, the
#                               fail-closed rule has regressed.
#
# One file in each directory is a .docx, because Open XML takes a different
# extraction path from text and both should be exercised. Building a .docx
# needs python3; without it that step is skipped and the rest still runs.
#
# There is nothing secret here and nothing that needs one. The script
# authenticates as whoever ran kinit.
# ---------------------------------------------------------------------------

set -euo pipefail

ROOT="${1:-/data/caseworks}"
CONTRACTS_GROUP="${CONTRACTS_GROUP:-hadoop-contracts-read}"
POLICIES_GROUP="${POLICIES_GROUP:-hadoop-policies-read}"
AUDIT_GROUP="${AUDIT_GROUP:-hadoop-audit-read}"

STAGE="$(mktemp -d)"
trap 'rm -rf "$STAGE"' EXIT

say() { printf '\n== %s\n' "$1"; }

if ! command -v hdfs >/dev/null 2>&1; then
    echo "hdfs is not on PATH. Run this on a cluster edge node." >&2
    exit 1
fi

if command -v klist >/dev/null 2>&1 && ! klist -s 2>/dev/null; then
    echo "No Kerberos ticket. Run kinit first." >&2
    exit 1
fi

say "Staging sample documents in $STAGE"

cat > "$STAGE/contract-C-1000.txt" <<'EOF'
Contract C-1000
Counterparty: Northwind Traders Limited
Status: Open
Owner: priya.raman
Value: 1,250,000 GBP
Term: 2026-01-01 to 2028-12-31

Northwind supplies settlement reconciliation services under the master
services agreement. Termination for convenience requires ninety days written
notice. Liability is capped at the fees paid in the preceding twelve months.
EOF

cat > "$STAGE/contract-C-1001.md" <<'EOF'
# Contract C-1001

**Counterparty:** Contoso Financial Services
**Status:** Under review
**Owner:** daniel.okafor

Renewal of the custody agreement. The counterparty has asked for an increase
in the liability cap and a shorter notice period. Legal review is outstanding.
EOF

cat > "$STAGE/contracts-register.csv" <<'EOF'
contract_ref,counterparty,status,owner,value_amount,currency,start_date,end_date
C-1000,Northwind Traders Limited,Open,priya.raman,1250000,GBP,2026-01-01,2028-12-31
C-1001,Contoso Financial Services,Under review,daniel.okafor,480000,GBP,2026-04-01,2027-03-31
C-1002,Fabrikam Custody,Open,priya.raman,95000,EUR,2025-11-15,2026-11-14
EOF

cat > "$STAGE/policy-retention.txt" <<'EOF'
Records Retention Policy

Client agreements are retained for seven years after termination. Settlement
records are retained for six years. Anything under legal hold is retained until
the hold is released in writing by the General Counsel.
EOF

cat > "$STAGE/policy-access-review.txt" <<'EOF'
Access Review Policy

Entitlements to production data are reviewed quarterly. A reviewer may not
approve their own access. Evidence of each review is retained for three years
and is subject to audit.
EOF

cat > "$STAGE/board-pack-restricted.txt" <<'EOF'
RESTRICTED - Board Pack, Q3

This file exists to prove a negative. Its mode is 600, so no group can read it,
so the connector can derive no grant for it and must skip it entirely.

If this text is ever returned by a Copilot or Microsoft Search query, the
fail-closed ACL rule has regressed and the connector is indexing documents it
cannot establish permissions for.
EOF

# A minimal but genuinely valid .docx: Open XML is a zip of XML parts, so one
# can be built without Word. Skipped when python3 is unavailable.
if command -v python3 >/dev/null 2>&1; then
    python3 - "$STAGE/contract-C-1002.docx" <<'PY'
import sys, zipfile

path = sys.argv[1]

document = (
    '<?xml version="1.0" encoding="UTF-8" standalone="yes"?>'
    '<w:document xmlns:w="http://schemas.openxmlformats.org/wordprocessingml/2006/main">'
    '<w:body>'
    '<w:p><w:r><w:t>Contract C-1002 - Fabrikam Custody</w:t></w:r></w:p>'
    '<w:p><w:r><w:t>Status: Open. Owner: priya.raman. Value: 95,000 EUR.</w:t></w:r></w:p>'
    '<w:p><w:r><w:t>Fabrikam provides custody services for the European book. '
    'The agreement auto-renews annually unless either party gives sixty days notice.</w:t></w:r></w:p>'
    '</w:body></w:document>'
)

content_types = (
    '<?xml version="1.0" encoding="UTF-8" standalone="yes"?>'
    '<Types xmlns="http://schemas.openxmlformats.org/package/2006/content-types">'
    '<Default Extension="rels" ContentType="application/vnd.openxmlformats-package.relationships+xml"/>'
    '<Default Extension="xml" ContentType="application/xml"/>'
    '<Override PartName="/word/document.xml" ContentType="application/vnd.openxmlformats-officedocument.wordprocessingml.document.main+xml"/>'
    '</Types>'
)

rels = (
    '<?xml version="1.0" encoding="UTF-8" standalone="yes"?>'
    '<Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships">'
    '<Relationship Id="rId1" '
    'Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument" '
    'Target="word/document.xml"/></Relationships>'
)

with zipfile.ZipFile(path, "w", zipfile.ZIP_DEFLATED) as z:
    z.writestr("[Content_Types].xml", content_types)
    z.writestr("_rels/.rels", rels)
    z.writestr("word/document.xml", document)

print("built", path)
PY
else
    echo "python3 not found; skipping the .docx sample. Text extraction of Open XML will not be exercised."
fi

say "Creating $ROOT"
hdfs dfs -mkdir -p "$ROOT/contracts" "$ROOT/policies" "$ROOT/private"

say "Uploading"
hdfs dfs -put -f "$STAGE/contract-C-1000.txt" "$STAGE/contract-C-1001.md" \
    "$STAGE/contracts-register.csv" "$ROOT/contracts/"

if [ -f "$STAGE/contract-C-1002.docx" ]; then
    hdfs dfs -put -f "$STAGE/contract-C-1002.docx" "$ROOT/contracts/"
fi

hdfs dfs -put -f "$STAGE/policy-retention.txt" "$STAGE/policy-access-review.txt" "$ROOT/policies/"
hdfs dfs -put -f "$STAGE/board-pack-restricted.txt" "$ROOT/private/"

# Hadoop's own litter, so the crawler can be seen ignoring it rather than
# indexing a job marker as if it were a document.
hdfs dfs -touchz "$ROOT/contracts/_SUCCESS"
echo 'half written' | hdfs dfs -put -f - "$ROOT/contracts/part-00000.tmp"

say "Applying ownership and permissions"

# 640: owner writes, owning group reads, nobody else. The connector derives its
# grant from the group bit, so a file whose group cannot read grants nothing.
hdfs dfs -chgrp -R "$CONTRACTS_GROUP" "$ROOT/contracts"
hdfs dfs -chmod -R 640 "$ROOT/contracts"
hdfs dfs -chmod 750 "$ROOT/contracts"

hdfs dfs -chgrp -R "$POLICIES_GROUP" "$ROOT/policies"
hdfs dfs -chmod -R 640 "$ROOT/policies"
hdfs dfs -chmod 750 "$ROOT/policies"

# A second reader on one file, through a named ACL entry rather than ownership.
# The indexed item should come back with two grants.
hdfs dfs -setfacl -m "group:$AUDIT_GROUP:r--" "$ROOT/policies/policy-retention.txt"

# 600: no group can read it. Nothing to grant, so the connector must skip it.
hdfs dfs -chmod -R 600 "$ROOT/private"
hdfs dfs -chmod 700 "$ROOT/private"

say "Result"
hdfs dfs -ls -R "$ROOT"
echo
echo "ACL on policy-retention.txt (expect a named group entry):"
hdfs dfs -getfacl "$ROOT/policies/policy-retention.txt"

cat <<EOF

Done.

Point the connector at it:

    "HdfsRoots": "$ROOT/contracts;$ROOT/policies"

and map the cluster groups to Entra groups:

    "EntraGroupMap": "$CONTRACTS_GROUP=<entra-group-guid>;$POLICIES_GROUP=<entra-group-guid>;$AUDIT_GROUP=<entra-group-guid>"

Then, from the connector host:

    .\\CdpGraphPush.exe --connector cdphdfsdocs --dry-run

Expect: the contracts and policies files listed, policy-retention.txt showing
2 ACL entries, and board-pack-restricted.txt NOT listed - it is under
$ROOT/private, which is not in HdfsRoots, and is mode 600 even if it were.

To test the fail-closed rule directly, add $ROOT/private to HdfsRoots and
re-run the dry run. The file must still not be indexed, and the log must say
it resolves to no Entra group.
EOF
