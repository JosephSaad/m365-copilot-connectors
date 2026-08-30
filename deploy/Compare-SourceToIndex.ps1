<#
.SYNOPSIS
    Reconciles dbo.Tickets against what is actually in the external connection,
    row by row — and finds the orphans the direct push path leaves behind.

.DESCRIPTION
    Run this on the machine that runs SqlGraphPush, which is the one machine
    that can reach both SQL Server and Microsoft Graph. Strictly read-only: it
    SELECTs, it GETs, and it PRINTS the commands that would fix what it finds
    without running any of them.

    THE PROBLEM THIS EXISTS FOR. SqlGraphPush never deletes anything. It selects
    live rows — soft-deleted ones are excluded by its WHERE clause — and PUTs
    each one. A ticket that was pushed and later deleted is therefore not
    removed from the index: it is merely no longer refreshed. It stays
    searchable, and Copilot keeps citing it, indefinitely.

    That is not a bug in the tool; it is the property that makes the direct push
    path a seeding and repair tool rather than a synchronisation one. The
    agent-hosted connector handles deletions properly, incrementally, through
    IsDeleted. This one cannot, so the deletions have to be found and applied by
    hand — which first means finding them.

    Four states, all reported:

      OK       live row, item present, timestamps agree
      STALE    live row, item present, but the row has changed since the push
      MISSING  live row, no item — the push never reached it
      ORPHAN   no live row, item still present — deleted at the source, still
               indexed, still being cited

    There is no list-items API (the externalItem resource documents no
    enumeration, and the List parameter set on Get-MgExternalConnectionItem is
    generated from OData metadata rather than an implemented operation), so
    orphans are found the only way they can be: by reading the source for rows
    that are gone or soft-deleted and asking Graph about each ID directly. An
    item whose source row was hard-deleted outside the ID range this sees cannot
    be found by any client — that gap is reported, not hidden.

.PARAMETER MaxItems
    Cap on Graph GETs, default 500 — one request per row adds up, and this is
    the kind of script people run against a million-row table by accident.
    Whatever is skipped is reported explicitly.

.PARAMETER Detail
    List every row and its state, not just the ones needing attention.

.EXAMPLE
    .\Compare-SourceToIndex.ps1 -ConfigPath ..\src\SqlGraphPush\appsettings.json

.EXAMPLE
    .\Compare-SourceToIndex.ps1 -MaxItems 2000 -Detail
#>

[CmdletBinding()]
param(
    [string]$ConfigPath = '.\appsettings.json',
    [string]$ConnectionId,
    [int]$MaxItems = 500,
    [switch]$Detail,
    [System.Management.Automation.PSCredential]$SqlCredential,
    [System.Security.SecureString]$ClientSecret,
    [int]$TimeoutSeconds = 30
)

$ErrorActionPreference = 'Stop'

. (Join-Path $PSScriptRoot 'GraphPushAuth.ps1')

function Note([string]$msg) { Write-Host "  note  $msg" -ForegroundColor DarkGray }

function Get-PropertyValue($properties, [string]$name) {
    if (-not $properties) { return $null }
    $match = $properties.PSObject.Properties | Where-Object { $_.Name -eq $name } | Select-Object -First 1
    if ($match) { return $match.Value }
    return $null
}

$config = Get-PushConfig -Path $ConfigPath
if (-not $ConnectionId) { $ConnectionId = $config.Graph.ConnectionId }

# ---------------------------------------------------------------------------
# The source
# ---------------------------------------------------------------------------

Write-Host "Reading $($config.DataSource.Server) / $($config.DataSource.Database)"

$builder = New-Object System.Data.SqlClient.SqlConnectionStringBuilder
$builder['Data Source'] = $config.DataSource.Server
$builder['Initial Catalog'] = $config.DataSource.Database
$builder['Connect Timeout'] = $TimeoutSeconds
$builder['Application Name'] = 'Compare-SourceToIndex'
$builder['Encrypt'] = $true
$builder['Integrated Security'] = -not $SqlCredential

$connection = New-Object System.Data.SqlClient.SqlConnection $builder.ConnectionString
if ($SqlCredential) {
    $password = $SqlCredential.Password.Copy()
    $password.MakeReadOnly()
    $connection.Credential = New-Object System.Data.SqlClient.SqlCredential($SqlCredential.UserName, $password)
}

$softDelete = [bool]$config.DataSource.SoftDeleteEnabled
$columns = if ($softDelete) { 'TicketId, LastModified, IsDeleted' } else { 'TicketId, LastModified, CAST(0 AS BIT) AS IsDeleted' }

$rows = New-Object System.Data.DataTable
try {
    $connection.Open()
    $command = $connection.CreateCommand()
    $command.CommandText = "SELECT $columns FROM dbo.Tickets ORDER BY TicketId;"
    $command.CommandTimeout = $TimeoutSeconds
    $adapter = New-Object System.Data.SqlClient.SqlDataAdapter $command
    $null = $adapter.Fill($rows)
}
catch {
    Write-Host "SQL failed: $($_.Exception.Message)" -ForegroundColor Red
    Write-Host 'Run Test-SqlSource.ps1 for the grant, the columns and the connection itself.'
    exit 1
}
finally {
    $connection.Close()
    $connection.Dispose()
}

$live = @($rows.Rows | Where-Object { -not [bool]$_.IsDeleted })
$tombstones = @($rows.Rows | Where-Object { [bool]$_.IsDeleted })
Write-Host "$($rows.Rows.Count) row(s): $($live.Count) live, $($tombstones.Count) soft-deleted"

if (-not $softDelete) {
    Note 'DataSource:SoftDeleteEnabled is false, so the source cannot say which rows were deleted. Orphans from'
    Note 'hard-deleted rows are invisible to this script and to every other client — see the gap note at the end.'
}

# ---------------------------------------------------------------------------
# The index
# ---------------------------------------------------------------------------

$credential = Get-PushCredential -Config $config -ClientSecret $ClientSecret
$auth = Get-PushToken -Config $config -Certificate $credential.Certificate -ClientSecret $credential.ClientSecret

if (-not $auth.Token) {
    Write-Host "Could not acquire a token: $($auth.Error)" -ForegroundColor Red
    if ($auth.Advice) { Note $auth.Advice }
    Write-Host 'Run Test-GraphPushPrereqs.ps1 first.'
    exit 1
}

$headers = @{ Authorization = "Bearer $($auth.Token)" }
$base = "https://graph.microsoft.com/v1.0/external/connections/$ConnectionId/items"

# Tombstones first: an orphan is the finding that actually needs action, and a
# -MaxItems cap should never be the reason one goes unreported.
$ordered = @($tombstones) + @($live)
$examined = if ($ordered.Count -gt $MaxItems) { $ordered[0..($MaxItems - 1)] } else { $ordered }
$skipped = $ordered.Count - $examined.Count

Write-Host "Checking $($examined.Count) item(s) against connection '$ConnectionId'"
Write-Host ''

$results = New-Object System.Collections.ArrayList
$aclSeen = $false
$throttled = 0
$position = 0

foreach ($row in $examined) {
    $position++
    if ($position % 50 -eq 0) { Write-Progress -Activity 'Comparing' -Status "$position of $($examined.Count)" -PercentComplete (100 * $position / $examined.Count) }

    $ticketId = [int]$row.TicketId
    $itemId = "ticket$ticketId"
    $deleted = [bool]$row.IsDeleted
    $state = $null
    $detail = ''

    $attempt = 0
    while ($true) {
        $attempt++
        try {
            $item = Invoke-RestMethod -Method GET -Uri "$base/$itemId" -Headers $headers

            if ($deleted) {
                $state = 'ORPHAN'
                $detail = 'deleted at the source, still in the index'
            }
            else {
                $indexed = Get-PropertyValue $item.properties 'lastModified'
                if ($indexed) {
                    # Both sides normalised to UTC explicitly. On Windows
                    # PowerShell 5.1 a bare [datetime] cast converts the indexed
                    # 'Z' string to LOCAL time while the SQL value stays raw, so
                    # every comparison would be skewed by the UTC offset - false
                    # STALE west of UTC, masked staleness east of it.
                    $indexedUtc = [DateTime]::Parse(
                        "$indexed",
                        [Globalization.CultureInfo]::InvariantCulture,
                        [Globalization.DateTimeStyles]::AdjustToUniversal)
                    $sourceUtc = [DateTime]::SpecifyKind([datetime]$row.LastModified, [DateTimeKind]::Utc)
                    $drift = ($sourceUtc - $indexedUtc).TotalSeconds
                    if ($drift -gt 2) {
                        $state = 'STALE'
                        $detail = "source is $([Math]::Round($drift / 60, 1)) minute(s) newer than the indexed copy"
                    }
                    else {
                        $state = 'OK'
                    }
                }
                else {
                    $state = 'OK'
                    $detail = 'no lastModified property to compare'
                }
            }

            # One ACL check is enough: every item is written with the same list.
            if (-not $aclSeen) {
                $aclSeen = $true
                $aclCount = @($item.acl).Count
                if ($aclCount -eq 0) {
                    Write-Host "  WARN  $itemId has an EMPTY ACL — it is invisible to every user." -ForegroundColor Yellow
                    Note 'Acl:GrantGroupObjectIds was empty or unresolved when this was pushed. Fix it and push again:'
                    Note 'the ACL is written into each item, so existing items keep the wrong one until they are rewritten.'
                }
                else {
                    Write-Host "  ACL on $itemId`: $aclCount entr(y/ies) — $((@($item.acl) | ForEach-Object { "$($_.accessType) $($_.type) $($_.value)" }) -join '; ')" -ForegroundColor DarkGray
                    Write-Host ''
                }
            }
            break
        }
        catch {
            $status = $_.Exception.Response.StatusCode.value__

            if ($status -eq 404) {
                if ($deleted) { $state = 'OK'; $detail = 'deleted at the source and absent from the index' }
                else { $state = 'MISSING'; $detail = 'live row that was never pushed, or the push stopped before it' }
                break
            }

            if ($status -eq 429 -and $attempt -le 5) {
                # The tool itself has no backoff, so a large push is exactly
                # where this is met. Honour Retry-After rather than hammering.
                $throttled++
                $wait = Get-RetryAfterSeconds -ErrorRecord $_ -Default ([int][Math]::Min(60, [Math]::Pow(2, $attempt + 2)))
                Write-Host "  throttled at $itemId; waiting $wait s (attempt $attempt)" -ForegroundColor DarkYellow
                Start-Sleep -Seconds $wait
                continue
            }

            if ($status -eq 403) {
                Write-Host "403 on $itemId. Under OwnedBy this app can only read items in connections it created." -ForegroundColor Red
                Write-Host 'Run Test-GraphPushPrereqs.ps1 — it separates missing consent from a connection owned by another app.'
                exit 1
            }

            $state = 'ERROR'
            $detail = $_.Exception.Message
            break
        }
    }

    $null = $results.Add([pscustomobject]@{
        TicketId = $ticketId
        ItemId   = $itemId
        State    = $state
        Detail   = $detail
    })
}

Write-Progress -Activity 'Comparing' -Completed

# ---------------------------------------------------------------------------
# What it found
# ---------------------------------------------------------------------------

$byState = $results | Group-Object State
Write-Host ''
Write-Host '== Result ==' -ForegroundColor Cyan
foreach ($group in $byState | Sort-Object Name) {
    $colour = switch ($group.Name) {
        'OK' { 'Green' }
        'ORPHAN' { 'Red' }
        'MISSING' { 'Yellow' }
        'STALE' { 'Yellow' }
        default { 'Red' }
    }
    Write-Host ("  {0,-8} {1}" -f $group.Name, $group.Count) -ForegroundColor $colour
}

if ($throttled -gt 0) {
    Write-Host ''
    Note "Throttled $throttled time(s) while reading. SqlGraphPush has no backoff of its own, so a push over a large"
    Note 'table can lose items to 429 without failing loudly — that is one way MISSING rows appear.'
}

if ($skipped -gt 0) {
    Write-Host ''
    Write-Host "  $skipped row(s) not examined: -MaxItems is $MaxItems." -ForegroundColor Yellow
    Note 'Every soft-deleted row was checked first, so no orphan was skipped by this cap — but MISSING and STALE'
    Note 'counts are partial. Raise -MaxItems for a complete answer.'
}

foreach ($state in @('ORPHAN', 'MISSING', 'STALE', 'ERROR')) {
    $these = @($results | Where-Object { $_.State -eq $state })
    if ($these.Count -eq 0) { continue }

    Write-Host ''
    Write-Host "== $state ($($these.Count)) ==" -ForegroundColor Cyan
    $these | Select-Object -First 40 | Format-Table -AutoSize ItemId, Detail | Out-String | Write-Host
    if ($these.Count -gt 40) { Note "$($these.Count - 40) more not listed" }
}

if ($Detail) {
    Write-Host ''
    Write-Host '== Every row ==' -ForegroundColor Cyan
    $results | Format-Table -AutoSize | Out-String | Write-Host
}

# ---------------------------------------------------------------------------
# Remediation, printed rather than performed
# ---------------------------------------------------------------------------

$orphans = @($results | Where-Object { $_.State -eq 'ORPHAN' })
$missing = @($results | Where-Object { $_.State -eq 'MISSING' -or $_.State -eq 'STALE' })
$errors  = @($results | Where-Object { $_.State -eq 'ERROR' })

Write-Host ''
Write-Host '== What to do ==' -ForegroundColor Cyan

if ($missing.Count -gt 0) {
    Write-Host "  $($missing.Count) item(s) are missing or stale. Re-run SqlGraphPush: it PUTs every live row, which"
    Write-Host '  creates what is absent and overwrites what is stale. There is no partial mode.'
}

if ($orphans.Count -gt 0) {
    Write-Host ''
    Write-Host "  $($orphans.Count) orphan(s). Re-running SqlGraphPush will NOT remove these — it only ever writes." -ForegroundColor Yellow
    Write-Host '  They have to be deleted explicitly. Review the list above, then run these:'
    Write-Host ''
    foreach ($orphan in $orphans | Select-Object -First 40) {
        Write-Host "    Invoke-MgGraphRequest -Method DELETE -Uri 'v1.0/external/connections/$ConnectionId/items/$($orphan.ItemId)'" -ForegroundColor DarkGray
    }
    if ($orphans.Count -gt 40) {
        Write-Host "    … and $($orphans.Count - 40) more" -ForegroundColor DarkGray
    }
    Write-Host ''
    Note 'Deliberately printed rather than run: deleting an item is not reversible, and a wrong connection ID here'
    Note 'would delete from a connection you did not mean. Connect with ExternalItem.ReadWrite.OwnedBy as the owning app.'
    Note 'If this list is long or keeps growing, the direct push path is being used as a synchroniser, which it is not.'
    Note 'The agent-hosted connector removes deletions incrementally and needs none of this.'
}

if ($errors.Count -gt 0) {
    # A row that could not be read is a row about which nothing is known. An
    # all-clear over unread rows would be the reconciliation equivalent of a
    # green build with skipped tests. The exit 1 happens at the very end, so
    # the closing caveats (the hard-delete gap) still print.
    Write-Host ''
    Write-Host "  $($errors.Count) row(s) could not be checked - the comparison is incomplete." -ForegroundColor Red
    Write-Host '  Fix the errors above (throttling, expiry, network) and re-run before trusting any verdict.'
}

if ($errors.Count -eq 0 -and $orphans.Count -eq 0 -and $missing.Count -eq 0) {
    Write-Host '  Nothing to do: the index matches the source.' -ForegroundColor Green
}

if ($softDelete) {
    Write-Host ''
    Note 'One gap remains, and no script can close it: a row HARD-deleted from dbo.Tickets leaves no trace to look up,'
    Note 'so its item cannot be found by this or any other client — there is no list-items API to enumerate against.'
    Note 'If hard deletes have happened, the only reliable repair is to delete the connection and push again.'
}

if ($errors.Count -gt 0) { exit 1 }
