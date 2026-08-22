<#
.SYNOPSIS
    Verifies the Copilot connection exists, its schema is registered, and items
    were actually ingested — using only operations the Graph API documents.

.DESCRIPTION
    Cross-checked against Microsoft Graph v1.0 reference (2026-08):

      externalconnection-list  GET /external/connections
      externalitem-get         GET /external/connections/{id}/items/{itemId}
      search/query             POST /search/query   (delegated only)

    Two corrections to advice commonly seen elsewhere:

    1. THE OWNEDBY TRAP. ExternalConnection.ReadWrite.OwnedBy is the documented
       least-privileged scope, but OwnedBy means "connections owned by the app
       making the call". In interactive PowerShell that app is Microsoft Graph
       Command Line Tools — which does not own this connection; the agent's (or
       SqlGraphPush's) app registration does. Under OwnedBy the list comes back
       EMPTY with no error, which reads as "the connection does not exist" when
       it exists and is healthy. Delegated verification therefore needs
       ExternalConnection.Read.All (admin consent), or run as the owning app
       with -AsOwningApp.

    2. THERE IS NO DOCUMENTED LIST-ITEMS API. The externalItem resource
       documents Create, Get, Update, Delete and addActivities — no
       enumeration, and the v1.0 reference has no "List externalItems" page.

       Note the trap: Get-MgExternalConnectionItem DOES advertise a List
       parameter set (-ExternalConnectionId alone, with -All, -Top, -Filter).
       That parameter set is generated from the OData metadata, where items is
       a navigation collection; it is not evidence the service implements
       enumeration. Do not build a verification step on it.

       To prove ingestion, fetch a KNOWN item by ID (this connector's
       convention: "ticket" + TicketId), run a search query, or read the item
       count in the admin centre.

.PARAMETER ConnectionId
    The external connection ID. Default: sqltickets.

.PARAMETER ItemId
    A known item to fetch, proving ingestion end to end. Default: ticket1 —
    pass the ID of a row you know exists, e.g. ticket1001.

.PARAMETER SearchFor
    Optional search term. When present (delegated mode only), runs
    POST /search/query scoped to this connection — the same retrieval path
    Copilot uses, so a hit proves the item is INDEXED, not merely accepted.

.PARAMETER AsOwningApp
    Authenticate as the app registration that owns the connection (app-only,
    certificate). OwnedBy scopes then suffice and no .All consent is needed.
    The search check is skipped: /search/query does not support app-only.

.EXAMPLE
    .\Verify-GraphConnection.ps1 -ItemId ticket1001 -SearchFor "payment gateway"

.EXAMPLE
    .\Verify-GraphConnection.ps1 -AsOwningApp -TenantId $tid -ClientId $cid -CertificateThumbprint $thumb
#>

[CmdletBinding(DefaultParameterSetName = 'Delegated')]
param(
    [string]$ConnectionId = 'sqltickets',
    [string]$ItemId = 'ticket1',
    [string]$SearchFor = '',

    [Parameter(ParameterSetName = 'OwningApp', Mandatory)]
    [switch]$AsOwningApp,
    [Parameter(ParameterSetName = 'OwningApp', Mandatory)]
    [string]$TenantId,
    [Parameter(ParameterSetName = 'OwningApp', Mandatory)]
    [string]$ClientId,
    [Parameter(ParameterSetName = 'OwningApp', Mandatory)]
    [string]$CertificateThumbprint
)

$ErrorActionPreference = 'Stop'
$failures = 0

function Step([string]$name) { Write-Host "`n== $name ==" -ForegroundColor Cyan }
function Pass([string]$msg) { Write-Host "  PASS  $msg" -ForegroundColor Green }
function Fail([string]$msg) { Write-Host "  FAIL  $msg" -ForegroundColor Red; $script:failures++ }
function Note([string]$msg) { Write-Host "  note  $msg" -ForegroundColor DarkGray }

if (-not (Get-Module -ListAvailable Microsoft.Graph.Authentication)) {
    throw 'Microsoft Graph PowerShell is not installed. Install-Module Microsoft.Graph.Authentication, Microsoft.Graph.Search -Scope CurrentUser'
}

Step 'Connect'
if ($AsOwningApp) {
    # As the owner, the OwnedBy application permissions already granted to this
    # app are sufficient — nothing extra to consent.
    Connect-MgGraph -TenantId $TenantId -ClientId $ClientId -CertificateThumbprint $CertificateThumbprint -NoWelcome
    Pass "app-only as $ClientId (owning app; OwnedBy permissions apply)"
}
else {
    # Read-only .All scopes, deliberately: ReadWrite is not needed to verify,
    # and OwnedBy would silently show nothing (see .DESCRIPTION, correction 1).
    Connect-MgGraph -Scopes 'ExternalConnection.Read.All', 'ExternalItem.Read.All' -NoWelcome
    Pass 'delegated with ExternalConnection.Read.All + ExternalItem.Read.All'
    Note 'If consent was declined, every check below 403s — that is consent, not the connection.'
}

Step "1. Connection '$ConnectionId' exists"
try {
    $conn = Invoke-MgGraphRequest -Method GET -Uri "v1.0/external/connections/$ConnectionId"
    Pass "'$($conn.name)' — state: $($conn.state)"
    if ($conn.state -ne 'ready') {
        Note "state '$($conn.state)': 'draft' means schema registration has not completed; items cannot ingest until it is 'ready'."
    }
}
catch {
    Fail "GET /external/connections/$ConnectionId -> $($_.Exception.Message)"
    Note '404: the connection was never created, or the ID differs. 403 under delegated auth: the .All scope was not consented.'
    Note 'A full list (GET /external/connections) under OwnedBy returns [] for connections other apps own — an empty list is NOT proof of absence.'
}

Step '2. Schema is registered'
try {
    $schema = Invoke-MgGraphRequest -Method GET -Uri "v1.0/external/connections/$ConnectionId/schema"
    $props = @($schema.properties)
    Pass "$($props.Count) properties; searchable: $((@($props | Where-Object { $_.isSearchable }) | ForEach-Object { $_.name }) -join ', ')"
}
catch {
    Fail "GET .../schema -> $($_.Exception.Message)"
    Note 'No schema means the connection wizard (or SqlGraphPush) never completed registration; ingestion cannot have happened.'
}

Step "3. Item '$ItemId' was ingested"
# The one documented per-item read. There is NO list-items API — do not try to
# enumerate; prove ingestion with an ID you know the source contains.
try {
    $item = Invoke-MgGraphRequest -Method GET -Uri "v1.0/external/connections/$ConnectionId/items/$ItemId"
    $title = $item.properties.title
    $aclCount = @($item.acl).Count
    Pass "found. title: '$title'; ACL entries: $aclCount"
    if ($aclCount -eq 0) { Fail 'item has an empty ACL — it is invisible to every user.' }
}
catch {
    Fail "GET .../items/$ItemId -> $($_.Exception.Message)"
    Note "404 with a healthy connection usually means no crawl has completed yet, or the ID convention differs — this connector writes 'ticket' + TicketId."
}

if ($SearchFor -and -not $AsOwningApp) {
    Step "4. Search finds '$SearchFor' (the path Copilot uses)"
    # Item accepted (step 3) and item indexed are different states; indexing
    # lags ingestion by minutes. Delegated only: /search/query has no app-only.
    $body = @{
        requests = @(@{
            entityTypes    = @('externalItem')
            contentSources = @("/external/connections/$ConnectionId")
            query          = @{ queryString = $SearchFor }
            from           = 0
            size           = 5
            fields         = @('title')
        })
    } | ConvertTo-Json -Depth 6
    try {
        $result = Invoke-MgGraphRequest -Method POST -Uri 'v1.0/search/query' -Body $body -ContentType 'application/json'
        $hits = @($result.value[0].hitsContainers[0].hits)
        if ($hits.Count -gt 0) {
            Pass "$($hits.Count) hit(s); first: '$($hits[0].resource.properties.title)'"
        }
        else {
            Fail 'zero hits. Recently ingested items can take a few minutes to index; also confirm your account is in the ACL group — search is security-trimmed.'
        }
    }
    catch {
        Fail "POST /search/query -> $($_.Exception.Message)"
    }
}
elseif ($SearchFor) {
    Note 'Search check skipped: /search/query does not support app-only authentication. Re-run without -AsOwningApp to test retrieval.'
}

Write-Host ''
if ($failures -eq 0) {
    Write-Host 'All checks passed.' -ForegroundColor Green
}
else {
    Write-Host "$failures check(s) failed." -ForegroundColor Red
    exit 1
}
