<#
.SYNOPSIS
    Derives the agent test harness's reference copies from appsettings.json, and
    fails when they have drifted from it.

.DESCRIPTION
    The Graph connector agent ships a local test harness - TestApp, or
    GraphConnectorAgentTest.exe - which exercises validate, schema and crawl
    without touching the Microsoft index. It is fed by copying two files from
    the deployment package into its Config folder, and a third file tells the
    agent host which port the connector listens on:

        deploy\ConnectionInfo.json          "TestApp input, no credentials"
        deploy\Manifest.json                also uploaded in the admin wizard
        deploy\CustomConnectorPortMap.json  "Reference copy of the agent port
                                             map entry"

    Two of those three describe themselves as reference copies, and that is
    exactly what all three are. Every value in them that is not prose is a copy
    of something in src\SqlTicketsConnector\appsettings.json, which is the file
    the connector itself actually reads:

        Connector:Id           -> Manifest.connectorId
                                  ConnectionInfo.ConnectorId
                                  the single key of CustomConnectorPortMap
                                  the -ConnectorId default in Install-Connector.ps1
        Connector:Port         -> the single value of CustomConnectorPortMap
        DataSource:Server      -> ConnectionInfo.DatasourceUrl, with
        DataSource:Database       Database, in the form the wizard expects
        DataSource:SqlAuthMode -> Manifest.authTypes
                                  ConnectionInfo.AuthenticationKind

    Keeping five copies of one GUID in step was a hand job, and README.md's own
    warning about it lists three of the five places it appears. This script is
    the other way round: one place decides, and the rest are derived from it or
    the build says so.

    WHAT DRIFT ACTUALLY COSTS. The connector ID is permanent - changing it after
    connections exist breaks every one of them - so the failure here is never
    "the files disagree". It is that somebody edits appsettings.json, runs the
    harness against a Manifest.json still carrying the old GUID, and gets a
    working harness run for a connector that is not the one they are about to
    deploy. The agent host caches the port map at startup, so a stale port
    entry fails later still, as "connector unavailable on specified port" from a
    process that is demonstrably listening.

    THE AUTHENTICATION MAPPING IS DELIBERATELY NARROW. WindowsIntegrated is the
    only SQL auth mode this repository ships to the wizard: credentials typed
    into the wizard are logged as ignored and discarded, and Manifest.json
    therefore advertises Windows only. What the wizard calls the other two modes
    is not recorded anywhere in this repository, so this script refuses rather
    than guessing - an invented AuthenticationKind would be a value nobody
    checked, written into the file an operator trusts.

.PARAMETER Update
    Rewrites the derived values in place instead of comparing against them.

    Only the values above are touched. The prose in ConnectionInfo.json - the
    _comment block explaining that no credential belongs there - is not derived
    from anything and is left exactly as it was, along with every other byte of
    all three files. Every replacement is asserted: a run that cannot find one
    of its targets writes nothing at all, rather than half-updating a file.

.PARAMETER RepositoryRoot
    Defaults to the parent of this script's folder.

.EXAMPLE
    pwsh build/Sync-HarnessFixtures.ps1
    Compares, and exits 1 on the first disagreement. This is what CI runs.

.EXAMPLE
    pwsh build/Sync-HarnessFixtures.ps1 -Update
    Regenerates them after a change to appsettings.json. Commit the result with
    the change that caused it.
#>

[CmdletBinding()]
param(
    [switch]$Update,

    [string]$RepositoryRoot = (Split-Path $PSScriptRoot -Parent)
)

$ErrorActionPreference = 'Stop'

$settingsPath = Join-Path $RepositoryRoot 'src\SqlTicketsConnector\appsettings.json'
$manifestPath = Join-Path $RepositoryRoot 'deploy\Manifest.json'
$connectionPath = Join-Path $RepositoryRoot 'deploy\ConnectionInfo.json'
$portMapPath = Join-Path $RepositoryRoot 'deploy\CustomConnectorPortMap.json'
$installerPath = Join-Path $RepositoryRoot 'deploy\Install-Connector.ps1'

foreach ($path in @($settingsPath, $manifestPath, $connectionPath, $portMapPath, $installerPath)) {
    if (-not (Test-Path -LiteralPath $path)) {
        throw "Not found: $path. This script derives the harness copies from the connector's own configuration, so it has nothing to compare until both sides exist."
    }
}

# --- What appsettings.json says -------------------------------------------

$settings = Get-Content -LiteralPath $settingsPath -Raw | ConvertFrom-Json

$connectorId = $settings.Connector.Id
$connectorPort = $settings.Connector.Port
$server = $settings.DataSource.Server
$database = $settings.DataSource.Database
$sqlAuthMode = $settings.DataSource.SqlAuthMode

if (-not $connectorId) { throw "Connector:Id is empty in $settingsPath. There is nothing to derive from." }
if (-not $connectorPort) { throw "Connector:Port is empty in $settingsPath." }
if (-not $server -or -not $database) { throw "DataSource:Server and DataSource:Database must both be set in $settingsPath; the wizard's data source URL is built from the pair." }

# See the note in the header about why this mapping stops here.
$authenticationKind = switch ($sqlAuthMode) {
    'WindowsIntegrated' { 'Windows' }
    default {
        throw "DataSource:SqlAuthMode is '$sqlAuthMode'. This script only knows what the connection wizard calls WindowsIntegrated, which is Windows. Decide what it calls '$sqlAuthMode', record it, and add it here - do not let this script guess it."
    }
}

# The wizard's Data source URL, as README.md and ConnectionInfo.json spell it.
$datasourceUrl = "Server=$server;Database=$database"

# The port map's value is a JSON string, not a number. The agent reads it as
# text and an integer there is a mapping it silently does not apply.
$portText = [string]$connectorPort

# --- What the copies say ---------------------------------------------------

$manifest = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json
$connection = Get-Content -LiteralPath $connectionPath -Raw | ConvertFrom-Json
$portMap = Get-Content -LiteralPath $portMapPath -Raw | ConvertFrom-Json
$installerText = Get-Content -LiteralPath $installerPath -Raw

$portMapKeys = @($portMap.PSObject.Properties.Name)

$installerIdPattern = "(?m)^(\s*\[string\]\`$ConnectorId\s*=\s*')([^']*)('.*)$"
$installerMatch = [regex]::Match($installerText, $installerIdPattern)

if (-not $installerMatch.Success) {
    throw "Could not find the -ConnectorId parameter default in $installerPath. It is one of the copies this script keeps honest; if it has moved, this script has to move with it rather than quietly stop checking it."
}

$checks = @(
    [pscustomobject]@{ Where = 'Manifest.json'; What = 'connectorId'; Expected = $connectorId; Actual = $manifest.connectorId }
    [pscustomobject]@{ Where = 'Manifest.json'; What = 'authTypes'; Expected = $authenticationKind; Actual = (@($manifest.authTypes) -join ', ') }
    [pscustomobject]@{ Where = 'ConnectionInfo.json'; What = 'ConnectorId'; Expected = $connectorId; Actual = $connection.ConnectorId }
    [pscustomobject]@{ Where = 'ConnectionInfo.json'; What = 'DatasourceUrl'; Expected = $datasourceUrl; Actual = $connection.DatasourceUrl }
    [pscustomobject]@{ Where = 'ConnectionInfo.json'; What = 'AuthenticationKind'; Expected = $authenticationKind; Actual = $connection.AuthenticationKind }
    [pscustomobject]@{ Where = 'CustomConnectorPortMap.json'; What = 'the connector ID key'; Expected = $connectorId; Actual = ($portMapKeys -join ', ') }
    [pscustomobject]@{ Where = 'CustomConnectorPortMap.json'; What = 'the port'; Expected = $portText; Actual = ($portMapKeys | ForEach-Object { [string]$portMap.$_ }) -join ', ' }
    [pscustomobject]@{ Where = 'Install-Connector.ps1'; What = 'the -ConnectorId default'; Expected = $connectorId; Actual = $installerMatch.Groups[2].Value }
)

# --- Compare ---------------------------------------------------------------

if (-not $Update) {
    $drifted = @()

    foreach ($check in $checks) {
        if ($check.Expected -ceq $check.Actual) {
            Write-Host ("  ok    {0,-28} {1,-24} {2}" -f $check.Where, $check.What, $check.Actual)
        }
        else {
            $drifted += $check
            Write-Host ("  DRIFT {0,-28} {1,-24} is '{2}', appsettings.json says '{3}'" -f $check.Where, $check.What, $check.Actual, $check.Expected) -ForegroundColor Yellow
        }
    }

    if ($drifted) {
        throw "$($drifted.Count) of $($checks.Count) derived value(s) in the harness reference copies no longer match src\SqlTicketsConnector\appsettings.json. Regenerate them in this same change: pwsh build/Sync-HarnessFixtures.ps1 -Update"
    }

    Write-Host "All $($checks.Count) derived value(s) match src\SqlTicketsConnector\appsettings.json." -ForegroundColor Green
    return
}

# --- Rewrite ---------------------------------------------------------------

# Every replacement is prepared and asserted before anything is written. A
# script that rewrites two files and then discovers it cannot find its target in
# the third leaves the tree in a state nobody chose.
function New-Replacement {
    param(
        [string]$Path,
        [string]$Text,
        [string]$Pattern,
        [string]$Replacement,
        [string]$Describes
    )

    $matches = [regex]::Matches($Text, $Pattern)

    if ($matches.Count -ne 1) {
        throw "In $Path, the pattern for $Describes matched $($matches.Count) time(s) and must match exactly once. Nothing has been written. Fix this script rather than the file it could not read."
    }

    return [regex]::Replace($Text, $Pattern, $Replacement)
}

$manifestText = Get-Content -LiteralPath $manifestPath -Raw
$connectionText = Get-Content -LiteralPath $connectionPath -Raw
$portMapText = Get-Content -LiteralPath $portMapPath -Raw

$newManifest = New-Replacement -Path $manifestPath -Text $manifestText `
    -Pattern '("connectorId"\s*:\s*")[^"]*(")' -Replacement "`${1}$connectorId`${2}" -Describes 'connectorId'
$newManifest = New-Replacement -Path $manifestPath -Text $newManifest `
    -Pattern '("authTypes"\s*:\s*\[\s*")[^"]*("\s*\])' -Replacement "`${1}$authenticationKind`${2}" -Describes 'authTypes'

$newConnection = New-Replacement -Path $connectionPath -Text $connectionText `
    -Pattern '("ConnectorId"\s*:\s*")[^"]*(")' -Replacement "`${1}$connectorId`${2}" -Describes 'ConnectorId'
$newConnection = New-Replacement -Path $connectionPath -Text $newConnection `
    -Pattern '("DatasourceUrl"\s*:\s*")[^"]*(")' -Replacement "`${1}$datasourceUrl`${2}" -Describes 'DatasourceUrl'
$newConnection = New-Replacement -Path $connectionPath -Text $newConnection `
    -Pattern '("AuthenticationKind"\s*:\s*")[^"]*(")' -Replacement "`${1}$authenticationKind`${2}" -Describes 'AuthenticationKind'

# The port map's key is the connector ID, so there is no field to replace inside
# a structure that survives: the object IS the derived value. It carries no
# prose, which is why this one is regenerated whole and the other two are not.
$newPortMap = New-Replacement -Path $portMapPath -Text $portMapText `
    -Pattern '(?s)\{.*\}' -Replacement ("{`n  `"$connectorId`": `"$portText`"`n}") -Describes 'the single port map entry'

$newInstaller = New-Replacement -Path $installerPath -Text $installerText `
    -Pattern $installerIdPattern -Replacement "`${1}$connectorId`${3}" -Describes 'the -ConnectorId parameter default'

# Line endings are preserved by construction - nothing above touches them - and
# the files are written with the newline they already had at the end.
$written = 0

foreach ($pair in @(
        @{ Path = $manifestPath; Old = $manifestText; New = $newManifest }
        @{ Path = $connectionPath; Old = $connectionText; New = $newConnection }
        @{ Path = $portMapPath; Old = $portMapText; New = $newPortMap }
        @{ Path = $installerPath; Old = $installerText; New = $newInstaller })) {

    if ($pair.Old -ceq $pair.New) {
        Write-Host ("  unchanged  {0}" -f (Split-Path $pair.Path -Leaf))
        continue
    }

    [System.IO.File]::WriteAllText($pair.Path, $pair.New)
    Write-Host ("  rewrote    {0}" -f (Split-Path $pair.Path -Leaf)) -ForegroundColor Green
    $written++
}

if ($written -eq 0) {
    Write-Host 'Nothing to update: the harness copies already match appsettings.json.' -ForegroundColor Green
}
else {
    Write-Host "Rewrote $written file(s) from src\SqlTicketsConnector\appsettings.json. Commit them with the change that moved the value." -ForegroundColor Green
}
