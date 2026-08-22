<#
.SYNOPSIS
    Watches an external connection move from draft to ready, and explains every
    state it passes through on the way.

.DESCRIPTION
    Schema registration is a server-side long-running operation. It typically
    takes 5 to 15 minutes and there is no progress to report during it — the
    connection simply sits in 'draft' until it does not. That silence is why
    people conclude it has failed and start again, which is the one action that
    makes things worse: deleting and recreating the connection restarts the same
    wait, and re-pushes every item.

    So this exists to make waiting bearable and to name the states that are not
    just waiting. Read-only: it polls GET on the connection and nothing else.

    Run it in a second window while SqlGraphPush is doing its own polling, or on
    its own after a run timed out — the operation continues server-side whether
    or not the tool that started it is still running. A SqlGraphPush that exits
    with a TimeoutException has NOT cancelled anything.

    WHAT YOU CANNOT DO LATER. Once a connection is ready its schema is
    effectively append-only: new properties can be added, but an existing
    property's type, its search annotations and its semantic labels cannot be
    changed, and properties cannot be removed. Correcting a mistake means
    deleting the connection — which deletes every item in it — and starting
    again. Read the schema this prints while it is still cheap to care.

.PARAMETER ConnectionId
    Defaults to Graph:ConnectionId from the configuration.

.PARAMETER TimeoutMinutes
    Give up after this long. Defaults to Graph:SchemaReadyTimeoutMinutes.

.EXAMPLE
    .\Watch-SchemaRegistration.ps1 -ConfigPath ..\src\SqlGraphPush\appsettings.json

.EXAMPLE
    .\Watch-SchemaRegistration.ps1 -IntervalSeconds 60 -TimeoutMinutes 45
#>

[CmdletBinding()]
param(
    [string]$ConfigPath = '.\appsettings.json',
    [string]$ConnectionId,
    [int]$IntervalSeconds = 30,
    [int]$TimeoutMinutes = 0,
    [System.Security.SecureString]$ClientSecret
)

$ErrorActionPreference = 'Stop'

. (Join-Path $PSScriptRoot 'GraphPushAuth.ps1')

function Note([string]$msg) { Write-Host "  note  $msg" -ForegroundColor DarkGray }

$config = Get-PushConfig -Path $ConfigPath
if (-not $ConnectionId) { $ConnectionId = $config.Graph.ConnectionId }
if ($TimeoutMinutes -le 0) {
    $TimeoutMinutes = if ($config.Graph.SchemaReadyTimeoutMinutes) { [int]$config.Graph.SchemaReadyTimeoutMinutes } else { 30 }
}

$credential = Get-PushCredential -Config $config -ClientSecret $ClientSecret
$auth = Get-PushToken -Config $config -Certificate $credential.Certificate -ClientSecret $credential.ClientSecret

if (-not $auth.Token) {
    Write-Host "Could not acquire a token: $($auth.Error)" -ForegroundColor Red
    if ($auth.Advice) { Note $auth.Advice }
    Write-Host 'Run Test-GraphPushPrereqs.ps1 — it separates the four causes of this.'
    exit 1
}

$uri = "https://graph.microsoft.com/v1.0/external/connections/$ConnectionId"
$headers = @{ Authorization = "Bearer $($auth.Token)" }
$started = Get-Date
$deadline = $started.AddMinutes($TimeoutMinutes)
$lastState = ''

Write-Host "Watching '$ConnectionId'. Giving up at $($deadline.ToString('HH:mm:ss')) ($TimeoutMinutes minutes)."
Write-Host 'Ctrl-C stops watching. It does not stop the registration — that runs server side.'
Write-Host ''

while ($true) {
    $elapsed = [int]((Get-Date) - $started).TotalMinutes
    $stamp = (Get-Date).ToString('HH:mm:ss')

    try {
        $connection = Invoke-RestMethod -Method GET -Uri $uri -Headers $headers
        $state = "$($connection.state)"
    }
    catch {
        $status = $_.Exception.Response.StatusCode.value__
        if ($status -eq 404) {
            Write-Host "$stamp  the connection does not exist yet" -ForegroundColor Yellow
            Note 'SqlGraphPush creates it on its first run. Nothing to watch until then.'
        }
        elseif ($status -eq 403) {
            Write-Host "$stamp  403 Forbidden" -ForegroundColor Red
            Note 'Under OwnedBy this app can only see connections it created. If the agent created this one, it cannot.'
            exit 1
        }
        elseif ($status -eq 429) {
            $retry = Get-RetryAfterSeconds -ErrorRecord $_ -Default $IntervalSeconds
            Write-Host "$stamp  throttled; waiting $retry s" -ForegroundColor Yellow
            Start-Sleep -Seconds $retry
        }
        else {
            Write-Host "$stamp  $($_.Exception.Message)" -ForegroundColor Red
        }

        if ((Get-Date) -gt $deadline) { break }
        Start-Sleep -Seconds $IntervalSeconds
        continue
    }

    if ($state -ne $lastState) {
        Write-Host "$stamp  ($elapsed min)  state: $state" -ForegroundColor Cyan
        switch ($state) {
            'draft' {
                Note 'Registration is running server side. 5 to 15 minutes is normal. Items written now are rejected.'
            }
            'ready' {
                Write-Host ''
                Write-Host "Ready after $elapsed minute(s)." -ForegroundColor Green

                try {
                    $schema = Invoke-RestMethod -Method GET -Uri "$uri/schema" -Headers $headers
                    Write-Host ''
                    Write-Host 'Registered schema — check it now, while changing it is still cheap:'
                    @($schema.properties) | Format-Table -AutoSize @(
                        @{ Label = 'Property'; Expression = { $_.name } }
                        @{ Label = 'Type'; Expression = { $_.type } }
                        @{ Label = 'Search'; Expression = { if ($_.isSearchable) { 'yes' } else { '' } } }
                        @{ Label = 'Query'; Expression = { if ($_.isQueryable) { 'yes' } else { '' } } }
                        @{ Label = 'Retrieve'; Expression = { if ($_.isRetrievable) { 'yes' } else { '' } } }
                        @{ Label = 'Refine'; Expression = { if ($_.isRefinable) { 'yes' } else { '' } } }
                        @{ Label = 'Labels'; Expression = { (@($_.labels) -join ', ') } }
                    ) | Out-String | Write-Host

                    $labels = @($schema.properties | ForEach-Object { $_.labels }) | Where-Object { $_ }
                    foreach ($needed in @('title', 'url')) {
                        if ($labels -notcontains $needed) {
                            Write-Host "  WARN  no property carries the '$needed' semantic label." -ForegroundColor Yellow
                            Note 'Title and Url, plus content, are what make items retrievable by Copilot rather than only by search.'
                        }
                    }
                    Note 'This schema is now effectively append-only: properties can be added, but types, annotations and'
                    Note 'labels cannot be changed and nothing can be removed. Correcting it means deleting the connection,'
                    Note 'which deletes every item in it.'
                }
                catch {
                    Write-Host "  Could not read the schema: $($_.Exception.Message)" -ForegroundColor Red
                }
                exit 0
            }
            'obsolete' {
                Write-Host 'This connection is obsolete and will not serve results. It cannot be revived.' -ForegroundColor Red
                exit 1
            }
            'limitExceeded' {
                Write-Host 'Tenant item quota is full. No further items will be accepted.' -ForegroundColor Red
                Note 'Connector items are metered separately from Copilot seats. Check the search licensing page for the tenant.'
                exit 1
            }
            default {
                Note "Undocumented state '$state'. Treat it as not-ready and check the admin centre."
            }
        }
        $lastState = $state
    }
    else {
        Write-Host "$stamp  ($elapsed min)  still $state"
    }

    if ((Get-Date) -gt $deadline) {
        Write-Host ''
        Write-Host "Still '$state' after $TimeoutMinutes minute(s)." -ForegroundColor Yellow
        Note 'Registration continues server side regardless. Re-run this to keep watching rather than recreating the'
        Note 'connection — deleting and recreating restarts the same wait and discards every item already written.'
        Note 'Beyond about 30 minutes in draft, raise it with support rather than retrying.'
        exit 1
    }

    Start-Sleep -Seconds $IntervalSeconds
}
