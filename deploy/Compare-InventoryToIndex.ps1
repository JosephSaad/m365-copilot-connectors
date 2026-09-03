<#
.SYNOPSIS
    Reconciles what a connector RECORDED writing against what is actually in the
    external connection — for any source, including the ones no PowerShell
    script can query.

.DESCRIPTION
    Compare-SourceToIndex.ps1 reads dbo.Tickets. That query does not generalise:
    it names a SQL Server table, and four of the five connectors do not have
    one. Oracle and Teradata need their own providers, MongoDB needs a driver,
    and CDP needs a Kerberos ODBC connection, a WebHDFS client and an Atlas REST
    call before it can name a single item. Reconciliation for those four was
    therefore missing entirely, which is the state GO-LIVE-READINESS records:
    "a silent divergence would go undetected".

    THIS SCRIPT NEEDS NO SOURCE. crawl.vwItemInventory is the connector's own
    record of every item it has ever written, kept in ConnectorState by
    PushCore.State — which every connector shares. Reconciling the inventory
    against Graph therefore works identically for SQL, CDP, Oracle, Teradata and
    MongoDB, and needs nothing but a read-only login to the state database and a
    Graph token.

    Three states, all reported, grouped by item type — because the reference
    path is the HIERARCHY connector, which writes Customer, Engagement and
    TimeEntry into one connection, and a divergence confined to one type is
    invisible in a single total.

      OK      inventory holds it live, Graph has it
      LOST    inventory holds it live, Graph returns 404. The item the connector
              believes it wrote is not there — a push that reported success and
              did not land, or something removed it outside the connector
      ORPHAN  inventory holds it deleted, Graph still has it. The delete sweep
              recorded a removal that Graph did not apply, so the item is still
              searchable and Copilot is still citing it

    WHAT THIS CANNOT SEE, and it matters. The inventory is what the connector
    wrote. A source record that was NEVER pushed — the crawl never reached it,
    or a filter excluded it — has no inventory row, so nothing here will find
    it. That direction needs a source query, which is exactly the part that does
    not generalise. Compare-SourceToIndex.ps1 covers it for SQL Server; for the
    other four it is open, and the run says so rather than implying coverage it
    does not have.

    Strictly read-only. It SELECTs, it GETs, and it PRINTS the commands that
    would fix what it finds without running any of them.

.PARAMETER ConfigPath
    The connector's appsettings.json. Used for the connection id, the Graph
    credentials and Settings:StateConnectionString.

.PARAMETER StateConnectionString
    Overrides Settings:StateConnectionString. Needs SELECT on the crawl views —
    the connector's own crawl_writer login is DENYed exactly that by sql/25, so
    use the dashboard's crawl_reader or another read-only principal.

.PARAMETER ItemType
    Reconcile one item type only. Without it every type in the connection is
    reconciled and reported separately.

.EXAMPLE
    .\Compare-InventoryToIndex.ps1 -ConfigPath ..\src\OracleGraphPush\appsettings.json

.EXAMPLE
    .\Compare-InventoryToIndex.ps1 -ConfigPath ..\src\SqlHierarchyPush\appsettings.json -ItemType TimeEntry
#>

param(
    [string]$ConfigPath = '.\appsettings.json',
    [string]$ConnectionId,
    [string]$StateConnectionString,
    [string]$ItemType,
    [int]$MaxItems = 5000,
    [switch]$Detail,
    [System.Management.Automation.PSCredential]$SqlCredential,
    [System.Security.SecureString]$ClientSecret,
    [int]$TimeoutSeconds = 30
)

$ErrorActionPreference = 'Stop'

. (Join-Path $PSScriptRoot 'GraphPushAuth.ps1')

function Note([string]$msg) { Write-Host "  note  $msg" -ForegroundColor DarkGray }

$config = Get-PushConfig -Path $ConfigPath
if (-not $ConnectionId) { $ConnectionId = $config.Graph.ConnectionId }

if (-not $StateConnectionString -and $config.Settings) {
    $StateConnectionString = $config.Settings.StateConnectionString
}

if (-not $StateConnectionString) {
    Write-Host 'No state connection string.' -ForegroundColor Red
    Note 'This script reconciles the connector''s inventory, so without ConnectorState it has nothing to read.'
    Note 'Set Settings:StateConnectionString, or pass -StateConnectionString.'
    Note 'A connector running without a state store keeps no inventory at all and cannot be reconciled this way.'
    exit 2
}

# ---------------------------------------------------------------------------
# The inventory. Source-agnostic: this is the connector's own record.
# ---------------------------------------------------------------------------

Write-Host "Reading crawl.vwItemInventory for connection '$ConnectionId'"

$sql = @'
SELECT  ItemId, ItemType, State, LastWrittenUtc
FROM    crawl.vwItemInventory
WHERE   ConnectionId = @ConnectionId
'@

if ($ItemType) { $sql += "  AND ItemType = @ItemType" }
$sql += ' ORDER BY ItemType, ItemId;'

$rows = New-Object System.Data.DataTable
$connection = New-Object System.Data.SqlClient.SqlConnection $StateConnectionString

try {
    $connection.Open()
    $command = $connection.CreateCommand()
    $command.CommandText = $sql
    $command.CommandTimeout = $TimeoutSeconds
    $null = $command.Parameters.Add((New-Object System.Data.SqlClient.SqlParameter('@ConnectionId', $ConnectionId)))
    if ($ItemType) {
        $null = $command.Parameters.Add((New-Object System.Data.SqlClient.SqlParameter('@ItemType', $ItemType)))
    }

    $adapter = New-Object System.Data.SqlClient.SqlDataAdapter $command
    $null = $adapter.Fill($rows)
}
catch {
    Write-Host "Could not read crawl.vwItemInventory: $($_.Exception.Message)" -ForegroundColor Red
    Note 'A permission error here is expected with the connector''s own login: sql/25 DENYs it SELECT on the crawl views.'
    exit 3
}
finally {
    $connection.Close()
    $connection.Dispose()
}

if ($rows.Rows.Count -eq 0) {
    Write-Host "The inventory holds no items for connection '$ConnectionId'." -ForegroundColor Yellow
    Note 'Either the connector has never completed a run against this connection, or it ran without a state store.'
    Note 'Nothing can be reconciled from an empty inventory, and an empty one is not evidence the index is empty.'
    exit 0
}

$types = @($rows.Rows | ForEach-Object { [string]$_.ItemType } | Sort-Object -Unique)
Write-Host "Inventory: $($rows.Rows.Count) item(s) on record across $($types.Count) type(s): $($types -join ', ')"

# ---------------------------------------------------------------------------
# Graph
# ---------------------------------------------------------------------------

$auth = Get-PushToken -Config $config -ClientSecret $ClientSecret

if (-not $auth.Token) {
    Write-Host "Could not acquire a token: $($auth.Error)" -ForegroundColor Red
    if ($auth.Advice) { Note $auth.Advice }
    Write-Host 'Run Test-GraphPushPrereqs.ps1 first.'
    exit 1
}

$headers = @{ Authorization = "Bearer $($auth.Token)" }
$base = "https://graph.microsoft.com/v1.0/external/connections/$ConnectionId/items"

# Tombstones first, for the reason Compare-SourceToIndex gives: an ORPHAN is the
# finding that needs action, and a -MaxItems cap must never be why one goes
# unreported.
$ordered = @($rows.Rows | Where-Object { [int]$_.State -ne 1 }) +
           @($rows.Rows | Where-Object { [int]$_.State -eq 1 })

$examined = if ($ordered.Count -gt $MaxItems) { $ordered[0..($MaxItems - 1)] } else { $ordered }
$skipped = $ordered.Count - $examined.Count

Write-Host "Checking $($examined.Count) item(s) against Graph"
Write-Host ''

$results = New-Object System.Collections.ArrayList
$throttled = 0
$position = 0

foreach ($row in $examined) {
    $position++
    if ($position % 50 -eq 0) {
        Write-Progress -Activity 'Reconciling' -Status "$position of $($examined.Count)" `
            -PercentComplete (100 * $position / $examined.Count)
    }

    $itemId = [string]$row.ItemId
    $live = [int]$row.State -eq 1
    $state = $null
    $detail = ''

    $attempt = 0
    while ($true) {
        $attempt++
        try {
            $null = Invoke-RestMethod -Method GET -Uri "$base/$itemId" -Headers $headers

            if ($live) {
                $state = 'OK'
            }
            else {
                $state = 'ORPHAN'
                $detail = 'the sweep recorded a delete Graph did not apply'
            }
            break
        }
        catch {
            $status = $_.Exception.Response.StatusCode.value__

            if ($status -eq 429 -and $attempt -le 5) {
                $wait = Get-RetryAfterSeconds -Exception $_.Exception
                $throttled++
                Start-Sleep -Seconds $wait
                continue
            }

            if ($status -eq 404) {
                if ($live) {
                    $state = 'LOST'
                    $detail = 'recorded as written, not present in the index'
                }
                else {
                    $state = 'OK'
                    $detail = 'deleted at the source and gone from the index'
                }
            }
            else {
                $state = 'ERROR'
                $detail = "HTTP $status"
            }
            break
        }
    }

    $null = $results.Add([pscustomobject]@{
        ItemId   = $itemId
        ItemType = [string]$row.ItemType
        State    = $state
        Detail   = $detail
    })
}

Write-Progress -Activity 'Reconciling' -Completed

# ---------------------------------------------------------------------------
# Report, per item type
# ---------------------------------------------------------------------------

Write-Host ''
Write-Host 'Per item type' -ForegroundColor Cyan
Write-Host ''

$format = '{0,-22} {1,8} {2,8} {3,8} {4,8}'
Write-Host ($format -f 'Item type', 'OK', 'LOST', 'ORPHAN', 'ERROR')

foreach ($type in ($results.ItemType | Sort-Object -Unique)) {
    $forType = @($results | Where-Object { $_.ItemType -eq $type })

    Write-Host ($format -f $type,
        @($forType | Where-Object State -eq 'OK').Count,
        @($forType | Where-Object State -eq 'LOST').Count,
        @($forType | Where-Object State -eq 'ORPHAN').Count,
        @($forType | Where-Object State -eq 'ERROR').Count)
}

$lost = @($results | Where-Object State -eq 'LOST')
$orphans = @($results | Where-Object State -eq 'ORPHAN')
$errors = @($results | Where-Object State -eq 'ERROR')

if ($Detail -and $lost.Count -gt 0) {
    Write-Host ''
    Write-Host 'LOST' -ForegroundColor Yellow
    $lost | ForEach-Object { Write-Host "  $($_.ItemType)  $($_.ItemId)" }
}

if ($orphans.Count -gt 0) {
    Write-Host ''
    Write-Host "$($orphans.Count) ORPHAN(S) — still searchable, still citeable" -ForegroundColor Red
    Note 'Each was recorded as deleted and is still in the index. These commands remove them; none is run here.'
    foreach ($orphan in $orphans) {
        Write-Host "    Invoke-MgGraphRequest -Method DELETE -Uri 'v1.0/external/connections/$ConnectionId/items/$($orphan.ItemId)'" -ForegroundColor DarkGray
    }
}

if ($lost.Count -gt 0) {
    Write-Host ''
    Write-Host "$($lost.Count) item(s) LOST — recorded as written, absent from the index" -ForegroundColor Yellow
    Note 'A full crawl rewrites these: every write is an upsert, so re-reading what is already there costs time and nothing else.'
    Note 'If the count is large, suspect a run that reported success while refusing items rather than individual losses.'
}

if ($errors.Count -gt 0) {
    Write-Host ''
    Write-Host "$($errors.Count) item(s) could not be checked" -ForegroundColor Yellow
    Note 'These are neither clean nor divergent — they are unknown, and an unknown is not a pass.'
}

if ($throttled -gt 0) {
    Write-Host ''
    Note "Absorbed $throttled throttle wait(s) honouring Retry-After."
}

if ($skipped -gt 0) {
    Write-Host ''
    Note "$skipped item(s) not examined because of -MaxItems $MaxItems. Tombstones were checked first, so no orphan was skipped."
}

Write-Host ''
Write-Host 'What this run did NOT check' -ForegroundColor Cyan
Note 'A source record that was never pushed has no inventory row, so nothing above can find it. This run compared'
Note 'the connector''s record against the index; it did not read the source, which is what lets it work for CDP,'
Note 'Oracle, Teradata and MongoDB at all. For SQL Server, Compare-SourceToIndex.ps1 closes that direction.'

if ($lost.Count -eq 0 -and $orphans.Count -eq 0 -and $errors.Count -eq 0) {
    Write-Host ''
    Write-Host 'Inventory and index agree on every item examined.' -ForegroundColor Green
    exit 0
}

exit 4
