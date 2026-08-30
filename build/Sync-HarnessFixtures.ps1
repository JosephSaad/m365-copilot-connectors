<#
.SYNOPSIS
    Derives the harness reference copies, the agent port map and the two deploy
    scripts' fallback defaults from appsettings.json, and fails when any of them
    has drifted from it.

.DESCRIPTION
    The Graph connector agent ships a local test harness - TestApp, or
    GraphConnectorAgentTest.exe - which exercises validate, schema and crawl
    without touching the Microsoft index. It is fed by copying two files from the
    deployment package into its Config folder, and a third file tells the agent
    host which port the connector listens on:

        deploy\ConnectionInfo.json          "TestApp input, no credentials"
        deploy\Manifest.json                also uploaded in the admin wizard
        deploy\CustomConnectorPortMap.json  "Reference copy of the agent port
                                             map entry"

    Two of those three describe themselves as reference copies, and that is what
    all three are. But they are not the only copies, and treating them as the
    whole problem is how this item stayed half done. The connector ID and the
    port are each written down in several files that must move together, and the
    file that decides both is src\SqlTicketsConnector\appsettings.json - the one
    the connector itself actually reads.

    WHERE THE CONNECTOR ID IS WRITTEN DOWN. Six places, of which one decides:

        src\SqlTicketsConnector\appsettings.json        Connector:Id - decides
        src\...\Connector\ConnectorInfoServiceImpl.cs   DefaultConnectorId
        deploy\Manifest.json                            connectorId
        deploy\ConnectionInfo.json                      ConnectorId
        deploy\CustomConnectorPortMap.json              the single key
        deploy\Install-Connector.ps1                    the -ConnectorId default

    WHERE THE PORT IS WRITTEN DOWN. Four places that must agree, of which one
    decides:

        src\SqlTicketsConnector\appsettings.json        Connector:Port - decides
        deploy\CustomConnectorPortMap.json              the single value
        deploy\Install-Connector.ps1                    the -Port default
        deploy\Test-ConnectorHost.ps1                   the port it probes when
                                                        it cannot read a config

    And two values that are written down twice:

        DataSource:Server + DataSource:Database  -> ConnectionInfo.DatasourceUrl,
                                                    in the form the wizard wants
        DataSource:SqlAuthMode                   -> Manifest.authTypes
                                                    ConnectionInfo.AuthenticationKind

    WHAT DRIFT ACTUALLY COSTS. The connector ID is permanent - changing it after
    connections exist breaks every one of them - so the failure here is never
    "the files disagree". It is that somebody edits appsettings.json, runs the
    harness against a Manifest.json still carrying the old GUID, and gets a
    working harness run for a connector that is not the one they are about to
    deploy. The agent host caches the port map at startup, so a stale port entry
    fails later still, as "connector unavailable on specified port" from a
    process that is demonstrably listening. And a stale fallback port in
    Test-ConnectorHost.ps1 is worse than either, because it only matters on a
    host where the config could not be read - which is to say during an outage -
    where it reports that nothing is listening on the port it still believes in,
    about a connector that is listening perfectly well on the configured one.

    ConnectorInfoServiceImpl.DefaultConnectorId is compared against the
    configured ID at startup, but the mismatch is logged at Warning and the
    server starts anyway. A warning in a log file on an on-premises agent host is
    not a guard; it is a thing found afterwards while looking for something else.
    Checking it here turns it into a build failure, which is the only form of it
    anybody reads in time.

    THE AUTHENTICATION MAPPING IS DELIBERATELY NARROW. WindowsIntegrated is the
    only SQL auth mode this repository ships to the wizard: credentials typed
    into the wizard are logged as ignored and discarded, and Manifest.json
    therefore advertises Windows only. What the wizard calls the other two modes
    is not recorded anywhere in this repository, so this script refuses rather
    than guessing - an invented AuthenticationKind would be a value nobody
    checked, written into the file an operator trusts.

    WHY THESE ARE CHECKED AND PATCHED, NOT GENERATED. The obvious reading of
    "state it once and derive the rest" is to generate the derived files outright
    and keep no copy in git. That was considered and rejected, for four reasons,
    in descending order of how much they decided it:

    1. Three of the five derived carriers cannot be generated at all. The
       -ConnectorId and -Port defaults live inside a 450-line installer, the
       fallback port lives inside a 460-line diagnostic script, and
       DefaultConnectorId lives inside a C# service implementation. Nothing
       generates those files from a 60-line JSON document; each is hand-written
       code with one derived literal in it. Generating the three JSON files would
       therefore leave three copies still needing a drift check, and the repo
       would carry two mechanisms where one does the whole job.

    2. ConnectionInfo.json is mostly prose, and the prose is the valuable part.
       Its _comment block - no credential belongs in this file, anything typed
       into the wizard's credential fields is logged as ignored and discarded -
       is the single most load-bearing thing in it, and it is not derived from
       anything. Generating the file would move that paragraph into a string
       literal inside this script, where it is harder to read, harder to review
       in a diff, and further from the operator who needs it.

    3. These files ship. README's "What the package contains" lists Manifest.json,
       ConnectionInfo.json and CustomConnectorPortMap.json in the release zip, and
       a search admin opens and edits ConnectionInfo.json by hand. A reviewer
       diffing a release tag should see the bytes that shipped, not a generator
       and an assurance.

    4. Checking composes with what already exists. CI runs this script on every
       build, so drift fails the build it was introduced in, with a message
       naming the file and the value. A generator would have to be run and its
       output committed to achieve the same thing, which is the hand-sync this
       item exists to remove, wearing a hat.

    The one exception proves the rule and is applied per file: a file that is
    entirely the derived value and carries no prose is regenerated whole rather
    than patched. CustomConnectorPortMap.json is the only such file - it is one
    JSON object whose key is the connector ID and whose value is the port, so
    there is no surrounding content to preserve.

    WHAT IS DELIBERATELY NOT CHECKED, so the next person does not have to work
    this out again:

    - deploy\Test-TriggerHealth.ps1's -Database default ('Ops'). It pairs with a
      -SqlInstance default of 'localhost', which is deliberately not the
      configured server: that script is scheduled on the SQL host itself. Half of
      that pair is already decoupled from appsettings.json on purpose, and
      coupling the other half would assert a relationship nobody chose.
    - deploy\Test-SqlSource.ps1's .EXAMPLE line naming sql01.contoso.local and
      Ops. It is help text. Stale help is a documentation bug, and pinning
      example prose to configuration makes this script fail for edits that broke
      nothing.
    - src\...\Server\ConnectorOptions.cs's Port property initialiser, and the
      test data in tests\...\TestSupport\TestData.cs. These are ordinary
      defaults for an absent setting rather than second statements of the
      deployment's port. DefaultConnectorId is checked and these are not because
      DefaultConnectorId declares itself to be the same fact - its own doc
      comment says it must appear in CustomConnectorPortMap.json and
      Manifest.json - and is compared against the configured value at startup.
    - README.md, docs\ASSUMPTIONS.md, docs\RUNBOOK.md and docs\GENESIS-PROMPT.md,
      which all quote the connector ID in prose. Documentation is reviewed by
      reading it. A build that fails because a sentence is stale trains people to
      pass -Update without reading, which is the habit this script exists to
      replace.

.PARAMETER Update
    Rewrites the derived values in place instead of comparing against them.

    Only the values above are touched. The prose in ConnectionInfo.json - the
    _comment block explaining that no credential belongs there - is not derived
    from anything and is left exactly as it was, along with every other byte of
    all four files it can write. Every replacement is asserted: a run that cannot
    find one of its targets writes nothing at all, rather than half-updating a
    file.

    -Update cannot fix everything it checks. DefaultConnectorId is in a C# file
    that this script deliberately only reads, so a drift there is reported with
    the edit to make by hand, and -Update exits non-zero rather than printing a
    success line that would read as "everything now agrees".

.PARAMETER RepositoryRoot
    Defaults to the parent of this script's folder.

.EXAMPLE
    pwsh build/Sync-HarnessFixtures.ps1
    Compares, and lists every disagreement before failing. This is what CI runs.

.EXAMPLE
    pwsh build/Sync-HarnessFixtures.ps1 -Update
    Regenerates them after a change to appsettings.json. Commit the result with
    the change that caused it.
#>

[CmdletBinding()]
param(
    [switch]$Update,

    [string]$RepositoryRoot
)

$ErrorActionPreference = 'Stop'

# Resolved here rather than as a param() default, because $PSScriptRoot is empty
# inside a param block under Windows PowerShell 5.1 and populated under
# PowerShell 7. As a default it therefore made `powershell -NoProfile -File
# build\Sync-HarnessFixtures.ps1` die on argument binding before running a single
# check, while the identical command under pwsh passed - and CI only ever runs it
# under pwsh, so the half of the estate still on 5.1 got an error message about
# Split-Path instead of a drift report. In the script body both versions agree.
if (-not $RepositoryRoot) {
    $RepositoryRoot = Split-Path $PSScriptRoot -Parent
}

$settingsPath = Join-Path $RepositoryRoot 'src\SqlTicketsConnector\appsettings.json'
$manifestPath = Join-Path $RepositoryRoot 'deploy\Manifest.json'
$connectionPath = Join-Path $RepositoryRoot 'deploy\ConnectionInfo.json'
$portMapPath = Join-Path $RepositoryRoot 'deploy\CustomConnectorPortMap.json'
$installerPath = Join-Path $RepositoryRoot 'deploy\Install-Connector.ps1'
$hostCheckPath = Join-Path $RepositoryRoot 'deploy\Test-ConnectorHost.ps1'
$infoServicePath = Join-Path $RepositoryRoot 'src\SqlTicketsConnector\Connector\ConnectorInfoServiceImpl.cs'

foreach ($path in @($settingsPath, $manifestPath, $connectionPath, $portMapPath, $installerPath, $hostCheckPath, $infoServicePath)) {
    if (-not (Test-Path -LiteralPath $path)) {
        throw "Not found: $path. This script derives the harness copies from the connector's own configuration, so it has nothing to compare until both sides exist."
    }
}

# --- Reading and writing files without changing them behind your back ------

# Get-Content -Raw consumes a UTF-8 BOM and does not give it back, and
# File.WriteAllText(path, text) writes without one. Between them they silently
# strip the BOM from any file this script rewrites, which for a .ps1 is not
# cosmetic: Windows PowerShell 5.1 reads a BOM-less file as the ANSI code page
# and mangles every non-ASCII character in it. Test-ConnectorHost.ps1 has a BOM
# and 23 non-ASCII characters, so an -Update run that did not preserve it would
# corrupt a diagnostic script that is only ever reached for during an incident.
# The two functions below read and write bytes, and put back exactly the byte
# order mark the file arrived with - a JSON file that had none keeps none, since
# a BOM in front of JSON breaks parsers that are entitled to assume it is absent.
function Read-TextFile {
    param([string]$Path)

    $bytes = [System.IO.File]::ReadAllBytes($Path)

    $hasBom = $bytes.Length -ge 3 -and $bytes[0] -eq 0xEF -and $bytes[1] -eq 0xBB -and $bytes[2] -eq 0xBF
    $offset = 0
    if ($hasBom) { $offset = 3 }

    return [pscustomobject]@{
        Text   = [System.Text.Encoding]::UTF8.GetString($bytes, $offset, $bytes.Length - $offset)
        HasBom = $hasBom
    }
}

function Write-TextFile {
    param([string]$Path, [string]$Text, [bool]$HasBom)

    # UTF8Encoding's constructor argument is "emit the byte order mark", so this
    # reproduces whatever Read-TextFile found rather than imposing a house style.
    [System.IO.File]::WriteAllText($Path, $Text, (New-Object System.Text.UTF8Encoding($HasBom)))
}

# --- Reading one value out of a file that is mostly not that value ---------

# Used for the values that live inside code rather than inside JSON. Both the
# comparison and the rewrite go through a pattern that must match exactly once:
# a pattern that stops matching is a value that has moved, and this script has to
# move with it rather than quietly stop checking it. The alternative - matching
# zero times and reporting nothing - is the failure mode where a check that
# measures nothing passes forever.
function Get-SingleCapture {
    param([string]$Path, [string]$Text, [string]$Pattern, [int]$Group, [string]$Describes)

    $found = [regex]::Matches($Text, $Pattern)

    if ($found.Count -ne 1) {
        throw "In $Path, the pattern for $Describes matched $($found.Count) time(s) and must match exactly once. If that value has moved or been renamed, move this script's pattern with it - do not let it silently stop checking."
    }

    return $found[0].Groups[$Group].Value
}

# Every replacement is prepared and asserted before anything is written. A script
# that rewrites two files and then discovers it cannot find its target in the
# third leaves the tree in a state nobody chose.
function New-Replacement {
    param([string]$Path, [string]$Text, [string]$Pattern, [string]$Replacement, [string]$Describes)

    $found = [regex]::Matches($Text, $Pattern)

    if ($found.Count -ne 1) {
        throw "In $Path, the pattern for $Describes matched $($found.Count) time(s) and must match exactly once. Nothing has been written. Fix this script rather than the file it could not read."
    }

    return [regex]::Replace($Text, $Pattern, $Replacement)
}

# --- What appsettings.json says --------------------------------------------

$settings = (Read-TextFile -Path $settingsPath).Text | ConvertFrom-Json

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

# The port map's value is a JSON string, not a number. The agent reads it as text
# and an integer there is a mapping it silently does not apply.
$portText = [string]$connectorPort

# --- What the copies say ---------------------------------------------------

$manifestFile = Read-TextFile -Path $manifestPath
$connectionFile = Read-TextFile -Path $connectionPath
$portMapFile = Read-TextFile -Path $portMapPath
$installerFile = Read-TextFile -Path $installerPath
$hostCheckFile = Read-TextFile -Path $hostCheckPath
$infoServiceFile = Read-TextFile -Path $infoServicePath

$manifest = $manifestFile.Text | ConvertFrom-Json
$connection = $connectionFile.Text | ConvertFrom-Json
$portMap = $portMapFile.Text | ConvertFrom-Json

$portMapKeys = @($portMap.PSObject.Properties.Name)

$installerIdPattern = "(?m)^(\s*\[string\]\`$ConnectorId\s*=\s*')([^']*)('.*)$"
$installerPortPattern = '(?m)^(\s*\[int\]\$Port\s*=\s*)(\d+)(\s*,)'
$hostCheckPortPattern = '(?m)^(\$port\s*=\s*)(\d+)'
$defaultIdPattern = '(public\s+const\s+string\s+DefaultConnectorId\s*=\s*")([^"]*)(")'

# CustomConnectorPortMap.json is regenerated whole rather than patched - see the
# header. Its newline is taken from the file it is replacing so that -Update does
# not quietly convert the one CRLF file it rewrites wholesale into LF.
$portMapNewline = "`n"
if ($portMapFile.Text -match "`r`n") { $portMapNewline = "`r`n" }
$portMapBody = "{$portMapNewline  ""$connectorId"": ""$portText""$portMapNewline}"

# --- Every derived value, declared once ------------------------------------

# Comparing and rewriting used to be two hand-maintained lists, which is the
# same duplication this script exists to remove, one level up: a value added to
# the checks and forgotten in the rewrites is a value -Update silently does not
# fix. Both passes below walk this one table.
#
#   Fix = 'patch'    rewritten in place by Pattern/Replacement
#   Fix = 'covered'  rewritten by another entry's replacement in the same file
#   Fix = 'manual'   read only; this script never writes that file
$derived = @(
    [pscustomobject]@{
        Where = 'Manifest.json'; What = 'connectorId'
        Expected = $connectorId; Actual = $manifest.connectorId
        Fix = 'patch'; File = $manifestPath
        Pattern = '("connectorId"\s*:\s*")[^"]*(")'; Replacement = "`${1}$connectorId`${2}"
    }
    [pscustomobject]@{
        Where = 'Manifest.json'; What = 'authTypes'
        Expected = $authenticationKind; Actual = (@($manifest.authTypes) -join ', ')
        Fix = 'patch'; File = $manifestPath
        Pattern = '("authTypes"\s*:\s*\[\s*")[^"]*("\s*\])'; Replacement = "`${1}$authenticationKind`${2}"
    }
    [pscustomobject]@{
        Where = 'ConnectionInfo.json'; What = 'ConnectorId'
        Expected = $connectorId; Actual = $connection.ConnectorId
        Fix = 'patch'; File = $connectionPath
        Pattern = '("ConnectorId"\s*:\s*")[^"]*(")'; Replacement = "`${1}$connectorId`${2}"
    }
    [pscustomobject]@{
        Where = 'ConnectionInfo.json'; What = 'DatasourceUrl'
        Expected = $datasourceUrl; Actual = $connection.DatasourceUrl
        Fix = 'patch'; File = $connectionPath
        Pattern = '("DatasourceUrl"\s*:\s*")[^"]*(")'; Replacement = "`${1}$datasourceUrl`${2}"
    }
    [pscustomobject]@{
        Where = 'ConnectionInfo.json'; What = 'AuthenticationKind'
        Expected = $authenticationKind; Actual = $connection.AuthenticationKind
        Fix = 'patch'; File = $connectionPath
        Pattern = '("AuthenticationKind"\s*:\s*")[^"]*(")'; Replacement = "`${1}$authenticationKind`${2}"
    }
    [pscustomobject]@{
        # The port map's key is the connector ID, so there is no field to replace
        # inside a structure that survives: the object IS the derived value.
        Where = 'CustomConnectorPortMap.json'; What = 'the connector ID key'
        Expected = $connectorId; Actual = ($portMapKeys -join ', ')
        Fix = 'patch'; File = $portMapPath
        Pattern = '(?s)\{.*\}'; Replacement = $portMapBody
    }
    [pscustomobject]@{
        Where = 'CustomConnectorPortMap.json'; What = 'the port'
        Expected = $portText; Actual = (($portMapKeys | ForEach-Object { [string]$portMap.$_ }) -join ', ')
        Fix = 'covered'; File = $portMapPath
        Pattern = $null; Replacement = $null
    }
    [pscustomobject]@{
        Where = 'Install-Connector.ps1'; What = 'the -ConnectorId default'
        Expected = $connectorId
        Actual = (Get-SingleCapture -Path $installerPath -Text $installerFile.Text -Pattern $installerIdPattern -Group 2 -Describes 'the -ConnectorId parameter default')
        Fix = 'patch'; File = $installerPath
        Pattern = $installerIdPattern; Replacement = "`${1}$connectorId`${3}"
    }
    [pscustomobject]@{
        # Overridden from the deployed appsettings.json at install time, so this
        # is a fallback rather than the live value - but it is what Get-Help
        # prints and what an operator reads as the contract, and a -Port default
        # that disagrees with the port map is a documented lie.
        Where = 'Install-Connector.ps1'; What = 'the -Port default'
        Expected = $portText
        Actual = (Get-SingleCapture -Path $installerPath -Text $installerFile.Text -Pattern $installerPortPattern -Group 2 -Describes 'the -Port parameter default')
        Fix = 'patch'; File = $installerPath
        Pattern = $installerPortPattern; Replacement = "`${1}$portText`${3}"
    }
    [pscustomobject]@{
        # Only reached when the host's appsettings.json could not be read, which
        # is to say during an incident, where a wrong port turns a diagnostic
        # into a false negative. See the header.
        Where = 'Test-ConnectorHost.ps1'; What = 'the fallback port'
        Expected = $portText
        Actual = (Get-SingleCapture -Path $hostCheckPath -Text $hostCheckFile.Text -Pattern $hostCheckPortPattern -Group 2 -Describes 'the fallback port')
        Fix = 'patch'; File = $hostCheckPath
        Pattern = $hostCheckPortPattern; Replacement = "`${1}$portText"
    }
    [pscustomobject]@{
        # Read only. This script does not write C#.
        Where = 'ConnectorInfoServiceImpl.cs'; What = 'DefaultConnectorId'
        Expected = $connectorId
        Actual = (Get-SingleCapture -Path $infoServicePath -Text $infoServiceFile.Text -Pattern $defaultIdPattern -Group 2 -Describes 'the DefaultConnectorId constant')
        Fix = 'manual'; File = $infoServicePath
        Pattern = $null; Replacement = $null
    }
)

# --- Compare ---------------------------------------------------------------

if (-not $Update) {
    $drifted = @()

    foreach ($check in $derived) {
        if ($check.Expected -ceq $check.Actual) {
            Write-Host ("  ok    {0,-28} {1,-24} {2}" -f $check.Where, $check.What, $check.Actual)
        }
        else {
            $drifted += $check
            Write-Host ("  DRIFT {0,-28} {1,-24} is '{2}', appsettings.json says '{3}'" -f $check.Where, $check.What, $check.Actual, $check.Expected) -ForegroundColor Yellow
        }
    }

    if ($drifted) {
        # Every disagreement is listed above before this throws, because the
        # first one is rarely the whole story: one edited GUID drifts four
        # values at once, and a run that stopped at the first would send someone
        # off to fix a quarter of the problem.
        $manual = @($drifted | Where-Object { $_.Fix -eq 'manual' })

        $message = "$($drifted.Count) of $($derived.Count) derived value(s) no longer match src\SqlTicketsConnector\appsettings.json. Regenerate them in this same change: pwsh build/Sync-HarnessFixtures.ps1 -Update"

        if ($manual) {
            $message += " That fixes all but $($manual.Count) of them: " + (($manual | ForEach-Object { "$($_.Where) ($($_.What)) must be edited by hand to '$($_.Expected)'" }) -join '; ') + '.'
        }

        throw $message
    }

    Write-Host "All $($derived.Count) derived value(s) match src\SqlTicketsConnector\appsettings.json." -ForegroundColor Green
    return
}

# --- Rewrite ---------------------------------------------------------------

# Folded per file, in table order, so that a file carrying two derived values is
# read once and written once.
$originals = @{}
$rewritten = @{}
$order = @()

foreach ($value in $derived) {
    if ($value.Fix -ne 'patch') { continue }

    if (-not $originals.ContainsKey($value.File)) {
        $originals[$value.File] = Read-TextFile -Path $value.File
        $rewritten[$value.File] = $originals[$value.File].Text
        $order += $value.File
    }

    $rewritten[$value.File] = New-Replacement -Path $value.File -Text $rewritten[$value.File] `
        -Pattern $value.Pattern -Replacement $value.Replacement -Describes "$($value.What) in $($value.Where)"
}

# Line endings and the byte order mark are preserved by construction: nothing
# above touches them, and Write-TextFile puts back the BOM the file arrived with.
$written = 0

foreach ($path in $order) {
    $leaf = Split-Path $path -Leaf

    if ($originals[$path].Text -ceq $rewritten[$path]) {
        Write-Host ("  unchanged  {0}" -f $leaf)
        continue
    }

    Write-TextFile -Path $path -Text $rewritten[$path] -HasBom $originals[$path].HasBom
    Write-Host ("  rewrote    {0}" -f $leaf) -ForegroundColor Green
    $written++
}

# The values this script reads but will not write. Reported last and loudly,
# because a run that rewrote four files and printed a success line would read as
# "everything now agrees" when one thing still does not.
$stillWrong = @($derived | Where-Object { $_.Fix -eq 'manual' -and $_.Expected -cne $_.Actual })

if ($stillWrong) {
    throw ((($stillWrong | ForEach-Object { "$($_.Where): $($_.What) is '$($_.Actual)' and appsettings.json says '$($_.Expected)'" }) -join '; ') + ". This script only reads that file, so edit it by hand and run this again. $written other file(s) were rewritten.")
}

if ($written -eq 0) {
    Write-Host 'Nothing to update: every derived value already matches appsettings.json.' -ForegroundColor Green
}
else {
    Write-Host "Rewrote $written file(s) from src\SqlTicketsConnector\appsettings.json. Commit them with the change that moved the value." -ForegroundColor Green
}
