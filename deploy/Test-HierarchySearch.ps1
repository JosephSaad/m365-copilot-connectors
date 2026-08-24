<#
.SYNOPSIS
    Proves the point of the three level test case: that searching for a CUSTOMER
    returns engagement and time entry items too, not just the customer.

.DESCRIPTION
    Run this from a workstation, signed in as a person who is in the ACL group.
    Read-only: it issues search queries and GETs, and changes nothing.

    WHY THIS NEEDS PROVING RATHER THAN ASSUMING. A Graph external item is flat.
    There is no parent property, no child collection and no join at retrieval
    time — Copilot fetches individual items and does not traverse anything. So
    "search the customer, get their engagements and time entries" is not a
    property of the data model; it is a property of how the items were built.
    Each descendant has to physically contain its ancestors' text, which is what
    sql/12-timesheet-views.sql does. This script is the check that it worked.

    It runs the search as a USER, deliberately. POST /search/query has no
    app-only form, and more importantly the answer differs by identity: results
    are security trimmed, so the only meaningful question is what a person in
    the ACL group can actually find.

    Four checks:

      1. The connection exists and its schema is registered.
      2. Which properties are searchable — the flattening is only as good as
         the annotations, and a customerName that is queryable but not
         searchable would fail this test case silently.
      3. DOWNWARD: one customer name, and what comes back, grouped by level.
         All three levels must appear. This is the requirement.
      4. UPWARD: one consultant name, which should return the engagements they
         worked on and not only their own time entries.

.PARAMETER ConnectionId
    Default: consultingwork. Must not be the ticket connection.

.PARAMETER CustomerName
    The customer to search for. Default: Contoso Financial Services.

.PARAMETER ConsultantName
    The consultant for the upward check. Default: Priya Raman.

.EXAMPLE
    .\Test-HierarchySearch.ps1

.EXAMPLE
    .\Test-HierarchySearch.ps1 -CustomerName 'Northwind Health' -ConsultantName 'Liam Fitzgerald'
#>

[CmdletBinding()]
param(
    [string]$ConnectionId = 'consultingwork',
    [string]$CustomerName = 'Contoso Financial Services',
    [string]$ConsultantName = 'Priya Raman',
    [int]$Size = 25
)

$ErrorActionPreference = 'Stop'
$script:failures = 0

function Step([string]$name) { Write-Host "`n== $name ==" -ForegroundColor Cyan }
function Pass([string]$msg) { Write-Host "  PASS  $msg" -ForegroundColor Green }
function Fail([string]$msg) { Write-Host "  FAIL  $msg" -ForegroundColor Red; $script:failures++ }
function Warn([string]$msg) { Write-Host "  WARN  $msg" -ForegroundColor Yellow }
function Note([string]$msg) { Write-Host "  note  $msg" -ForegroundColor DarkGray }

function Invoke-ConnectionSearch {
    <#
    .SYNOPSIS
        One /search/query against this connection, returning the hits and the
        service's own total.
    #>
    param([string]$Query, [int]$Take)

    $body = @{
        requests = @(@{
            entityTypes    = @('externalItem')
            contentSources = @("/external/connections/$ConnectionId")
            query          = @{ queryString = $Query }
            from           = 0
            size           = $Take
            fields         = @('title', 'itemType', 'customerName', 'engagementName',
                               'consultantName', 'hierarchyPath', 'hours', 'url')
        })
    } | ConvertTo-Json -Depth 8

    $result = Invoke-MgGraphRequest -Method POST -Uri 'v1.0/search/query' -Body $body -ContentType 'application/json'
    $container = $result.value[0].hitsContainers[0]

    return [pscustomobject]@{
        Hits  = @($container.hits)
        Total = [int]$container.total
    }
}

function Get-Level([object]$hit) {
    $value = $hit.resource.properties.itemType
    if ([string]::IsNullOrWhiteSpace($value)) { return '(none)' }
    return "$value"
}

if (-not (Get-Module -ListAvailable Microsoft.Graph.Authentication)) {
    throw 'Microsoft Graph PowerShell is not installed. Install-Module Microsoft.Graph.Authentication -Scope CurrentUser'
}

Step 'Connect'
# Read-only scopes. ExternalItem.Read.All rather than an OwnedBy scope: this
# session is Microsoft Graph Command Line Tools, which owns nothing here, and
# under OwnedBy every call below would return empty with no error.
Connect-MgGraph -Scopes 'ExternalConnection.Read.All', 'ExternalItem.Read.All' -NoWelcome
$context = Get-MgContext
Pass "signed in as $($context.Account)"
Note 'Search is security trimmed. If this account is not in the ACL group, every check below returns nothing'
Note 'and that is the ACL, not the index.'

Step "1. Connection '$ConnectionId'"
try {
    $connection = Invoke-MgGraphRequest -Method GET -Uri "v1.0/external/connections/$ConnectionId"
    Pass "'$($connection.name)' — state: $($connection.state)"
    if ("$($connection.state)" -ne 'ready') {
        Fail "state is '$($connection.state)', not 'ready'. Nothing is searchable until it is."
        Note 'Watch it with deploy\Watch-SchemaRegistration.ps1 rather than recreating the connection.'
    }
}
catch {
    Fail "GET /external/connections/$ConnectionId -> $($_.Exception.Message)"
    Note "404 means SqlHierarchyPush has not run yet. Check you are not looking at the ticket connection instead."
    exit 1
}

Step '2. Searchable properties'
# The flattening is only as good as the annotations. customerName present but
# not searchable would make check 3 fail with no obvious cause.
try {
    $schema = Invoke-MgGraphRequest -Method GET -Uri "v1.0/external/connections/$ConnectionId/schema"
    $properties = @($schema.properties)
    $searchable = @($properties | Where-Object { $_.isSearchable } | ForEach-Object { $_.name })
    $refinable = @($properties | Where-Object { $_.isRefinable } | ForEach-Object { $_.name })

    Pass "$($properties.Count) properties; $($searchable.Count) searchable"
    Note "searchable: $($searchable -join ', ')"
    Note "refinable:  $($refinable -join ', ')"

    foreach ($required in @('customerName', 'engagementName', 'consultantName', 'hierarchyPath')) {
        if ($searchable -contains $required) {
            Pass "$required is searchable"
        }
        else {
            Fail "$required is NOT searchable — the cross level search cannot work without it"
        }
    }

    # The platform rejects the combination outright, so seeing both here would
    # mean reading a schema that is not the one this tool registers.
    $both = @($properties | Where-Object { $_.isSearchable -and $_.isRefinable })
    if ($both.Count -gt 0) {
        Fail "searchable and refinable on the same property: $($both.name -join ', ')"
    }
}
catch {
    Fail "GET .../schema -> $($_.Exception.Message)"
}

Step "3. THE REQUIREMENT — searching '$CustomerName'"
# Quoted, so the phrase is matched rather than the individual words.
$query = '"' + $CustomerName + '"'
try {
    $search = Invoke-ConnectionSearch -Query $query -Take $Size
    if ($search.Total -eq 0) {
        Fail "zero hits for $query"
        Note 'Recently pushed items take a few minutes to index. If it has been longer, check the ACL group'
        Note 'and confirm the push actually ran: deploy\Compare-SourceToIndex.ps1.'
    }
    else {
        Pass "$($search.Total) item(s) match, across all levels"

        $byLevel = $search.Hits | Group-Object { Get-Level $_ }
        foreach ($group in $byLevel | Sort-Object Name) {
            Write-Host ("       {0,-12} {1} of the first {2} hits" -f $group.Name, $group.Count, $search.Hits.Count)
        }

        $levels = @($byLevel | ForEach-Object { $_.Name })
        foreach ($level in @('Customer', 'Engagement', 'TimeEntry')) {
            if ($levels -contains $level) {
                Pass "$level items returned by a customer search"
            }
            else {
                Fail "NO $level item in the first $($search.Hits.Count) hits"
                if ($level -ne 'Customer') {
                    Note "That level does not carry the customer's name in its indexed text, or none has been pushed."
                    Note 'Check sql/12-timesheet-views.sql verification query 3, which asks the same question in SQL.'
                }
            }
        }

        Write-Host ''
        Write-Host '       Top hits:' -ForegroundColor DarkGray
        foreach ($hit in $search.Hits | Select-Object -First 8) {
            $props = $hit.resource.properties
            Write-Host ("       [{0,-10}] {1}" -f (Get-Level $hit), $props.hierarchyPath) -ForegroundColor DarkGray
        }
    }
}
catch {
    Fail "POST /search/query -> $($_.Exception.Message)"
    Note '/search/query is delegated only. If you authenticated app-only, that is the cause.'
}

Step "4. The other direction — searching '$ConsultantName'"
# A consultant's name is on their own time entries, and is also rolled UP into
# the engagement item's content. So this should return engagements as well.
try {
    $search = Invoke-ConnectionSearch -Query ('"' + $ConsultantName + '"') -Take $Size
    if ($search.Total -eq 0) {
        Warn "zero hits for '$ConsultantName' — check the name matches the sample data"
    }
    else {
        Pass "$($search.Total) item(s) match"
        $levels = @($search.Hits | Group-Object { Get-Level $_ })
        foreach ($group in $levels | Sort-Object Name) {
            Write-Host ("       {0,-12} {1}" -f $group.Name, $group.Count)
        }

        if (@($levels | ForEach-Object { $_.Name }) -contains 'Engagement') {
            Pass 'engagement items returned by a consultant search — the upward roll up works'
        }
        else {
            Warn 'no engagement item returned. The consultant list is rolled into the engagement content by'
            Warn 'sql/12-timesheet-views.sql; if this fails, that view was not applied or not re-pushed.'
        }
    }
}
catch {
    Fail "POST /search/query -> $($_.Exception.Message)"
}

Write-Host ''
if ($script:failures -eq 0) {
    Write-Host "A search for a customer returns that customer, their engagements and their time entries." -ForegroundColor Green
    Write-Host 'Now try it in Copilot — docs/HIERARCHY-TEST-CASE.md has the prompts worth asking.' -ForegroundColor Green
}
else {
    Write-Host "$script:failures check(s) failed. See docs/HIERARCHY-TEST-CASE.md." -ForegroundColor Red
    exit 1
}
