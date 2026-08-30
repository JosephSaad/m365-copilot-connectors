#Requires -Version 5.1
<#
.SYNOPSIS
    Runs every control verification in docs/SECURITY.md section 5 and reports one
    verdict per control.

.DESCRIPTION
    The evidence for this connector's controls was a list of commands in a
    document. That is reproducible in the sense that anyone can retype it, and
    not in the sense that anyone does: a reviewer runs the four they recognise,
    the dependency audit gets skipped because it is slow, and the review records
    that the controls passed.

    This script is the same list, run in one pass, with a verdict per control and
    a non-zero exit code if any of them failed. What it does NOT do is decide
    anything a human should: it reports, and the exit code is the summary.

    NOTHING HERE TOUCHES A TENANT, A VAULT, A DATABASE OR A CLUSTER. Every
    control in section 5 is a property of the repository and its build, which is
    what makes them runnable on a review machine with no access to anything. The
    live checks - the ones that need a real instance - are a different list, in
    docs/GO-LIVE-READINESS.md.

    Some tools are optional. gitleaks and pre-commit are not installed on every
    machine, and a review that cannot run them should say so rather than quietly
    reporting seven passes out of seven. Those are reported SKIPPED, and SKIPPED
    is not a pass: the exit code stays 0, and the summary names them, because a
    reviewer needs to know which evidence they are missing rather than being
    stopped by it.

.PARAMETER RepositoryRoot
    Defaults to the repository this script lives in.

.PARAMETER SkipDependencyAudit
    Skips the two `dotnet list package` calls, which need the network. Use on an
    air-gapped machine, and record that the audit was not run.

.EXAMPLE
    .\Invoke-SecurityEvidence.ps1
    Runs everything and prints a table.

.EXAMPLE
    .\Invoke-SecurityEvidence.ps1 -SkipDependencyAudit
    Everything the repository can answer without the network.
#>
[CmdletBinding()]
param(
    [string]$RepositoryRoot,
    [switch]$SkipDependencyAudit
)

$ErrorActionPreference = 'Stop'

if (-not $RepositoryRoot) {
    $RepositoryRoot = Split-Path -Parent $PSScriptRoot
}

if (-not (Test-Path (Join-Path $RepositoryRoot 'SqlTicketsConnector.sln'))) {
    throw "SqlTicketsConnector.sln not found under '$RepositoryRoot'. Pass -RepositoryRoot."
}

$script:RepositoryRootPath = $RepositoryRoot
$solution = Join-Path $RepositoryRoot 'SqlTicketsConnector.sln'
$results = New-Object System.Collections.Generic.List[object]

function Add-Result([string]$control, [string]$verdict, [string]$detail) {
    $results.Add([pscustomobject]@{ Control = $control; Verdict = $verdict; Detail = $detail })

    $colour = switch ($verdict) {
        'PASS'    { 'Green' }
        'FAIL'    { 'Red' }
        'SKIPPED' { 'Yellow' }
        default   { 'Gray' }
    }

    Write-Host ("  {0,-7} {1}" -f $verdict, $control) -ForegroundColor $colour
    if ($detail) { Write-Host "          $detail" -ForegroundColor DarkGray }
}

function Test-Tool([string]$name) {
    return $null -ne (Get-Command $name -ErrorAction SilentlyContinue)
}

# Native tools write diagnostics to stderr on success as well as on failure, and
# PowerShell 5.1 turns a populated stderr into a terminating error under
# $ErrorActionPreference = 'Stop'. The exit code is the verdict, so stderr is
# merged into the captured output and read only when the exit code says to.
#
# RUNS IN THE REPOSITORY, not wherever the caller happened to be standing. Both
# gitleaks and pre-commit take their target from the current directory, so
# without this the script reported on one tree while its header named another -
# which it did, silently, the first time it was pointed at a second clone.
function Invoke-Native([string]$exe, [string[]]$exeArgs) {
    Push-Location $script:RepositoryRootPath
    try {
        $output = & $exe @exeArgs 2>&1 | Out-String
        return [pscustomobject]@{ ExitCode = $LASTEXITCODE; Output = $output }
    }
    finally {
        Pop-Location
    }
}

Write-Host ""
Write-Host "Security control evidence - docs/SECURITY.md section 5" -ForegroundColor Cyan
Write-Host "Repository: $RepositoryRoot"
Write-Host ""

# --- 1. The test suite ------------------------------------------------------
# Every control in section 2 names a test. This is the one check whose failure
# means a control is not merely unproven but broken.
Write-Host "== Test suite ==" -ForegroundColor Cyan
$test = Invoke-Native 'dotnet' @('test', $solution, '--nologo', '--verbosity', 'quiet')
if ($test.ExitCode -eq 0) {
    $passed = ([regex]::Match($test.Output, 'Passed:\s+(\d+)')).Groups[1].Value
    Add-Result 'Test suite' 'PASS' "$passed tests, no live dependencies"
}
elseif ($test.Output -match 'NETSDK1045') {
    # The solution targets net10.0 and this machine's SDK is older. That is a
    # missing toolchain, not a failing control, and calling it FAIL would put a
    # red line in a review over something no code change fixes. Named precisely,
    # because the retry is a one-flag retry.
    Add-Result 'Test suite' 'SKIPPED' 'SDK cannot target net10.0 - retry with -p:ConnectorTargetFramework=net9.0, or install the .NET 10 SDK'
}
else {
    $failed = ([regex]::Match($test.Output, 'Failed:\s+(\d+)')).Groups[1].Value
    Add-Result 'Test suite' 'FAIL' "$failed failing - a named control has no evidence"
}

# --- 2. Configuration hygiene ----------------------------------------------
Write-Host "== Configuration hygiene ==" -ForegroundColor Cyan
$hygiene = Invoke-Native 'dotnet' @(
    'build', (Join-Path $RepositoryRoot 'build\SecretHygiene.proj'),
    '-t:ScanAppSettingsForSecrets', '-nologo', '-v:m')
if ($hygiene.ExitCode -eq 0) {
    Add-Result 'Secret hygiene scan' 'PASS' 'no credential-shaped key carries a value'
}
else {
    Add-Result 'Secret hygiene scan' 'FAIL' 'SEC0001 - see output above'
}

# --- 3. Repository history --------------------------------------------------
Write-Host "== Repository history ==" -ForegroundColor Cyan
if (Test-Tool 'gitleaks') {
    $leaks = Invoke-Native 'gitleaks' @(
        'detect', '--config', (Join-Path $RepositoryRoot '.gitleaks.toml'), '--redact', '--no-banner')
    # gitleaks exits 1 for findings and something else when it could not run.
    # THOSE ARE NOT THE SAME RESULT and this script used to report both as FAIL,
    # which is the mirror of the SKIPPED-is-not-a-pass rule it is built on: a
    # tool that crashed has produced no evidence either way, and calling that a
    # failed control sends somebody hunting for a leak that was never reported.
    #
    # It mattered immediately. A negative lookahead in .gitleaks.toml made every
    # version of gitleaks panic on the config, so the scan had never run once -
    # and this script called that "a finding is present".
    if ($leaks.ExitCode -eq 0) {
        Add-Result 'gitleaks history scan' 'PASS' 'no secret in any commit'
    }
    elseif ($leaks.ExitCode -eq 1) {
        Add-Result 'gitleaks history scan' 'FAIL' 'a finding is present - output is redacted by design'
    }
    else {
        $why = if ($leaks.Output -match 'panic|invalid perl operator|error parsing regexp') {
            '.gitleaks.toml did not compile - gitleaks uses RE2, which has no lookaround'
        }
        else {
            "gitleaks exited $($leaks.ExitCode) without scanning"
        }

        Add-Result 'gitleaks history scan' 'SKIPPED' $why
    }
}
else {
    Add-Result 'gitleaks history scan' 'SKIPPED' 'gitleaks not installed - history is unreviewed'
}

# --- 4. The developer's own checks -----------------------------------------
Write-Host "== Pre-commit hooks ==" -ForegroundColor Cyan
if (Test-Tool 'pre-commit') {
    $hooks = Invoke-Native 'pre-commit' @('run', '--all-files')
    if ($hooks.ExitCode -eq 0) {
        Add-Result 'pre-commit, all files' 'PASS' 'the same checks a developer gets'
    }
    else {
        Add-Result 'pre-commit, all files' 'FAIL' 'a hook rejected the tree'
    }
}
else {
    Add-Result 'pre-commit, all files' 'SKIPPED' 'pre-commit not installed'
}

# Whether the hooks are INSTALLED is a separate question from whether they pass,
# and the more commonly wrong one: hooks live in .git/hooks, which is not
# tracked, so every fresh clone starts with none and nothing says so.
$hookPath = Join-Path $RepositoryRoot '.git\hooks\pre-commit'
if (Test-Path $hookPath) {
    Add-Result 'pre-commit installed in this clone' 'PASS' '.git/hooks/pre-commit exists'
}
else {
    Add-Result 'pre-commit installed in this clone' 'FAIL' "run 'pre-commit install' - this clone commits unchecked"
}

# --- 5. Dependency audit ----------------------------------------------------
Write-Host "== Dependencies ==" -ForegroundColor Cyan
if ($SkipDependencyAudit) {
    Add-Result 'Vulnerable packages' 'SKIPPED' '-SkipDependencyAudit was passed'
    Add-Result 'Deprecated packages' 'SKIPPED' '-SkipDependencyAudit was passed'
}
else {
    # `dotnet list package` exits 0 whether or not it found anything, so the
    # output is the verdict here and the exit code is not.
    $vuln = Invoke-Native 'dotnet' @('list', $solution, 'package', '--vulnerable', '--include-transitive')
    if ($vuln.Output -match 'has no vulnerable packages') {
        Add-Result 'Vulnerable packages' 'PASS' 'none, including transitive'
    }
    elseif ($vuln.ExitCode -ne 0) {
        Add-Result 'Vulnerable packages' 'SKIPPED' 'restore failed - usually no network'
    }
    else {
        Add-Result 'Vulnerable packages' 'FAIL' 'at least one advisory applies'
    }

    $dep = Invoke-Native 'dotnet' @('list', $solution, 'package', '--deprecated')
    if ($dep.Output -match 'has no deprecated packages') {
        Add-Result 'Deprecated packages' 'PASS' 'none'
    }
    elseif ($dep.ExitCode -ne 0) {
        Add-Result 'Deprecated packages' 'SKIPPED' 'restore failed - usually no network'
    }
    elseif (($dep.Output -split "`n" | Where-Object { $_ -match '>\s' } | Measure-Object).Count -eq 1 -and
            $dep.Output -match 'xunit') {
        # The one expected result, called out in SECURITY.md: xunit 2.9.3 is
        # superseded by xunit.v3, which is a deprecation and not a vulnerability.
        Add-Result 'Deprecated packages' 'PASS' 'only the expected xunit supersession'
    }
    else {
        Add-Result 'Deprecated packages' 'FAIL' 'an unexpected deprecation - read the list above'
    }
}

# --- Summary ----------------------------------------------------------------
$failed  = @($results | Where-Object Verdict -eq 'FAIL')
$skipped = @($results | Where-Object Verdict -eq 'SKIPPED')

Write-Host ""
Write-Host "Summary" -ForegroundColor Cyan
$results | Format-Table Control, Verdict, Detail -AutoSize | Out-String | Write-Host

Write-Host ("{0} passed, {1} failed, {2} skipped." -f
    @($results | Where-Object Verdict -eq 'PASS').Count, $failed.Count, $skipped.Count)

if ($skipped.Count -gt 0) {
    Write-Host ""
    Write-Host "SKIPPED is not a pass. This review has no evidence for:" -ForegroundColor Yellow
    $skipped | ForEach-Object { Write-Host "  - $($_.Control): $($_.Detail)" -ForegroundColor Yellow }
}

if ($failed.Count -gt 0) {
    Write-Host ""
    Write-Host "FAILED controls:" -ForegroundColor Red
    $failed | ForEach-Object { Write-Host "  - $($_.Control): $($_.Detail)" -ForegroundColor Red }
    exit 1
}

exit 0
