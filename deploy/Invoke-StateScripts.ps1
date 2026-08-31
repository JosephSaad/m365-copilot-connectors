<#
.SYNOPSIS
    Runs the crawl state or source scripts against a database whose name is not
    the one they are written for.

.DESCRIPTION
    Every script in sql/ carries a hard-coded USE statement near the top:
    USE [ConnectorState] for the state set, USE [Ops] for the source fixture.
    That is right for the deployment they document and wrong for every other
    database, and the failure mode is the dangerous kind.

    THE -d SWITCH DOES NOT SAVE YOU. sqlcmd's -d sets the INITIAL database; the
    USE statement then moves the session somewhere else. So

        sqlcmd -d ConnectorState_DrillRestore -i sql/21-crawl-state-tables.sql

    creates its tables in ConnectorState. On an estate where the live database
    is read-only by policy and the drill database differs from it by a suffix,
    that is not a typo, it is a silent write to production - and it looks like
    it worked.

    This script stages each file with the name substituted, ASSERTS that no
    reference to the original name survives the substitution, and only then
    runs anything. A rename that half worked is worse than no rename, so a
    single surviving reference stops the whole set before the first statement
    executes.

    WHERE IT IS NEEDED. Three places, and all three are real:

      * A disaster recovery drill. docs/DISASTER-RECOVERY.md restores to
        ConnectorState_DrillRestore precisely so the live database is never the
        target; re-applying or verifying the schema there needs this.
      * A second test rig beside a live one, which is how Live Test 2 was built.
      * Standing up a second connector estate on one instance.

.PARAMETER Database
    The database to run against. Must not be a protected name; see -Protected.

.PARAMETER OriginalName
    The name written into the scripts. ConnectorState for the state set, Ops for
    the source fixture.

.PARAMETER Scripts
    Paths relative to the repository root, in the order they must run. The order
    is not incidental: docs/CRAWL-STATE-DEPLOYMENT.md documents it, and sql/40
    must precede sql/24.

.PARAMETER Protected
    Databases this script refuses to touch whatever it is asked, compared
    case-insensitively because SQL Server is. Defaults to the two names a
    production estate uses.

.PARAMETER WhatIf
    Stage and assert, run nothing, and list what would have run.

.EXAMPLE
    .\deploy\Invoke-StateScripts.ps1 -Database ConnectorState_DrillRestore -Scripts @(
        'sql/21-crawl-state-tables.sql', 'sql/22-crawl-state-views.sql')

.EXAMPLE
    .\deploy\Invoke-StateScripts.ps1 -Database Ops2 -OriginalName Ops -Scripts @(
        'sql/10-timesheet-source.sql', 'sql/11-timesheet-sample-data.sql') -WhatIf
#>

[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$Database,

    [string]$Server = '.',

    [Parameter(Mandatory)][string[]]$Scripts,

    [string]$OriginalName = 'ConnectorState',

    [string]$RepositoryRoot = (Split-Path $PSScriptRoot -Parent),

    [string]$StageRoot,

    [string[]]$Protected = @('ConnectorState', 'Ops'),

    [switch]$TrustServerCertificate,

    [switch]$WhatIf
)

$ErrorActionPreference = 'Stop'

if (-not $StageRoot) { $StageRoot = Join-Path ([System.IO.Path]::GetTempPath()) "state-scripts-$Database" }

# The guard runs before anything is read, let alone executed. Case-insensitive
# because SQL Server is: "ops" and "OPS" are one database, and a guard that
# missed either would be decoration.
foreach ($name in @($Protected + $OriginalName)) {
    if ($Database -eq $name) {
        throw "Refusing to run against [$Database]. It is either the name the scripts are written for or one of the protected databases ($($Protected -join ', ')). Name a different database."
    }
}

New-Item -ItemType Directory -Force -Path $StageRoot | Out-Null
Get-ChildItem $StageRoot -File -ErrorAction SilentlyContinue | Remove-Item -Force

Write-Host "Staging $($Scripts.Count) script(s): [$OriginalName] -> [$Database] on $Server" -ForegroundColor Cyan

$staged = @()

foreach ($name in $Scripts) {
    $source = Join-Path $RepositoryRoot $name
    if (-not (Test-Path $source)) { throw "Script not found: $source" }

    $text = Get-Content $source -Raw

    # CASE SENSITIVE, deliberately. PowerShell's -replace is case-insensitive,
    # and renaming "Ops" that way also rewrites the "ops" inside "drops" and
    # "operations" - the source scripts hold three times as many
    # case-insensitive matches as real ones. [regex]::Replace is case-sensitive
    # by default, which is the behaviour wanted.
    #
    # No word boundary either: ConnectorState_log is a real logical file name
    # that must be renamed with the rest, and \b refuses it because underscore
    # is a word character.
    $text = [regex]::Replace($text, [regex]::Escape($OriginalName), $Database)

    # THE ASSERTION, and the reason this script is worth having rather than a
    # one-line sed. If the new name begins with the old one - the common
    # ConnectorState -> ConnectorState2 case - then searching for the old name
    # finds the new one too, so the test has to exclude the renamed form.
    # Otherwise any surviving occurrence at all is a miss.
    $survivors = if ($Database.StartsWith($OriginalName, [StringComparison]::Ordinal)) {
        [regex]::Matches($text, [regex]::Escape($OriginalName) +
            '(?!' + [regex]::Escape($Database.Substring($OriginalName.Length)) + ')')
    }
    else {
        [regex]::Matches($text, [regex]::Escape($OriginalName))
    }

    if ($survivors.Count -gt 0) {
        throw "Staging $name left $($survivors.Count) reference(s) to [$OriginalName]. Refusing to run ANY script in this set: a partial rename would address two databases at once."
    }

    $target = Join-Path $StageRoot (Split-Path $name -Leaf)
    Set-Content -Path $target -Value $text -Encoding UTF8
    $staged += [pscustomobject]@{ Name = $name; Path = $target }
}

Write-Host "  clean: no surviving reference to [$OriginalName] in any of $($staged.Count) file(s)." -ForegroundColor DarkGray

if ($WhatIf) {
    Write-Host 'WhatIf: nothing executed. Would run, in order:' -ForegroundColor Yellow
    $staged | ForEach-Object { "  $($_.Name)" }
    return
}

# -b so sqlcmd reports failure through its exit code, -I so QUOTED_IDENTIFIER is
# ON for the connection. The second matters more than it looks: crawl.Run
# carries filtered indexes, and an INSERT against a table with one fails with
# Msg 1934 when the setting is OFF - which is how sqlcmd connects by default.
$common = @('-S', $Server, '-E', '-b', '-I')
if ($TrustServerCertificate) { $common += '-C' }

foreach ($s in $staged) {
    Write-Host "== $($s.Name)" -ForegroundColor Cyan

    & sqlcmd @common '-d' $Database '-i' $s.Path 2>&1 |
        Where-Object { $_ -match '\S' } | ForEach-Object { "   $_" }

    if ($LASTEXITCODE -ne 0) {
        throw "$($s.Name) failed with exit $LASTEXITCODE. Stopping: the set is ordered and every script assumes the one before it."
    }
}

Write-Host "All $($staged.Count) script(s) applied to [$Database]." -ForegroundColor Green
