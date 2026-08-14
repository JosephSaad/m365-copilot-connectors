<#
.SYNOPSIS
    Checks Get-OfflinePackages.ps1 against the restore graph on disk.

.DESCRIPTION
    A checked-in package list rots the moment a dependency moves, and it rots
    silently: nothing here needs it, so the first person to find out is whoever
    is standing at an air-gapped build machine with a folder of nupkgs that no
    longer restores. This turns that into a build failure instead.

    It reads project.assets.json for every project, which is what NuGet actually
    resolved, and compares it with what Get-OfflinePackages.ps1 says to download.
    Run it after the matching restore:

      Base           after 'dotnet restore SqlTicketsConnector.sln'
      Otlp           after 'dotnet restore ... -p:EnableOtlpExporter=true'
      RuntimePacks   after a self-contained publish, i.e. after Build.ps1
                     -SelfContained. Runtime packs are download dependencies
                     rather than libraries, which is precisely why an early
                     version of the list omitted them.

    Base is compared for equality in both directions: a package the list misses
    breaks an offline restore, and one it invents wastes a download and implies
    a dependency that is no longer there.

    Otlp is compared one way only. That configuration raises Google.Protobuf and
    Grpc.Core.Api rather than adding to them, so the pinned versions are absent
    from this graph while remaining correct entries in the base set. What is
    checked is that nothing the OTLP graph needs is missing, and that every
    package the list calls OTLP-only genuinely appears here.

    RuntimePacks compares ids and the version, the version being the interesting
    half: the list asks the SDK for BundledNETCoreAppPackageVersion rather than
    hard-coding one, and this is what proves that question is still answered
    correctly on a machine with a current SDK. Two of the four packs are
    requested only from certain build hosts — the list covers all of them, so it
    is checked as a superset there and strictly everywhere else.

.PARAMETER Update
    Rewrites the base list in Get-OfflinePackages.ps1 from the restore graph
    instead of comparing against it, and reports what changed.

    Every dependency bump is drift by definition, so without this the check
    turns each one into a chore of hand-editing sixty-odd lines — which is the
    kind of chore that ends with the check being deleted. Run it in the same
    pull request as the bump. Base only: the OTLP entries and the runtime packs
    are decisions rather than a transcript of the graph, and both are small
    enough to edit by hand when they genuinely change.

.EXAMPLE
    pwsh build/Test-OfflinePackageList.ps1 -Configuration Base

.EXAMPLE
    dotnet restore SqlTicketsConnector.sln
    pwsh build/Test-OfflinePackageList.ps1 -Configuration Base -Update
    Regenerates the list after a dependency bump. Restore first: this reads what
    NuGet resolved, so an out-of-date assets file writes an out-of-date list.
#>

[CmdletBinding()]
param(
    [ValidateSet('Base', 'Otlp', 'RuntimePacks')]
    [string]$Configuration = 'Base',

    [switch]$Update,

    [string]$RepositoryRoot = (Split-Path $PSScriptRoot -Parent)
)

$ErrorActionPreference = 'Stop'
$listScript = Join-Path $PSScriptRoot 'Get-OfflinePackages.ps1'

function Get-AssetsFile {
    param([string]$Root)

    $searchRoots = @('src', 'tests') |
        ForEach-Object { Join-Path $Root $_ } |
        Where-Object { Test-Path $_ }

    $files = @(Get-ChildItem -Path $searchRoots -Recurse -File -Filter 'project.assets.json' -ErrorAction SilentlyContinue)

    if (-not $files) {
        throw "No project.assets.json found under $Root. Restore first: this script inspects what NuGet resolved, so it has nothing to read until it has."
    }

    return $files
}

# Every package NuGet resolved, as "id version" strings.
function Get-ResolvedPackage {
    param([object[]]$AssetsFiles)

    $resolved = New-Object 'System.Collections.Generic.HashSet[string]'

    foreach ($file in $AssetsFiles) {
        $json = Get-Content $file.FullName -Raw | ConvertFrom-Json

        if (-not $json.libraries) { continue }

        foreach ($library in $json.libraries.PSObject.Properties) {
            # Project references appear here too, with type 'project'.
            if ($library.Value.type -ne 'package') { continue }

            # The key is "<id>/<version>".
            $parts = $library.Name -split '/', 2
            if ($parts.Count -eq 2) { [void]$resolved.Add("$($parts[0]) $($parts[1])") }
        }
    }

    return $resolved
}

# Runtime packs are download dependencies, not libraries: they are unpacked into
# the publish output rather than referenced, so they never appear in the
# libraries section and never appear in 'dotnet list package' either.
function Get-DownloadDependency {
    param([object[]]$AssetsFiles)

    $found = @{}

    foreach ($file in $AssetsFiles) {
        $json = Get-Content $file.FullName -Raw | ConvertFrom-Json

        if (-not $json.project.frameworks) { continue }

        # The project that asked is worth keeping: which packs appear depends on
        # the build host, and naming the requester is what turns a surprise entry
        # into something a reader can act on.
        $project = Split-Path (Split-Path $file.FullName -Parent) -Parent | Split-Path -Leaf

        foreach ($framework in $json.project.frameworks.PSObject.Properties) {
            foreach ($dependency in @($framework.Value.downloadDependencies)) {
                if (-not $dependency) { continue }

                # The version is a range, "[10.0.9, 10.0.9]".
                $version = if ($dependency.version -match '\d+\.\d+\.\d+(-[0-9A-Za-z.]+)?') { $Matches[0] } else { $dependency.version }

                if (-not $found.ContainsKey($dependency.name)) {
                    $found[$dependency.name] = [pscustomobject]@{ Version = $version; Projects = @() }
                }

                $found[$dependency.name].Version = $version
                $found[$dependency.name].Projects = @($found[$dependency.name].Projects + $project | Sort-Object -Unique)
            }
        }
    }

    return $found
}

function Write-Difference {
    param([string]$Heading, [string[]]$Items)

    Write-Host $Heading -ForegroundColor Yellow
    $Items | Sort-Object | ForEach-Object { Write-Host "    $_" }
}

# Rewrites the entries between the BEGIN/END markers in Get-OfflinePackages.ps1.
# Markers rather than "find the array literal": the file is meant to stay
# readable and hand-editable everywhere else, and a rewriter that guesses at
# where a block ends is one refactor away from eating the rest of the script.
function Set-BaseList {
    param([string[]]$Packages)

    $lines = Get-Content $listScript
    $begin = ($lines | Select-String -SimpleMatch '# BEGIN BASE LIST').LineNumber
    $end = ($lines | Select-String -SimpleMatch '# END BASE LIST').LineNumber

    if (-not $begin -or -not $end -or $end -le $begin) {
        throw "Could not find the BEGIN BASE LIST / END BASE LIST markers in $listScript. Restore them, or update the list by hand."
    }

    $entries = $Packages | ForEach-Object {
        $parts = $_ -split ' ', 2
        "    @{{ Id = '{0}'; Version = '{1}' }}" -f $parts[0], $parts[1]
    }

    # LineNumber is 1-based; the slices keep everything up to and including the
    # BEGIN marker and from the END marker onwards.
    $rewritten = @($lines[0..($begin - 1)]) + $entries + @($lines[($end - 1)..($lines.Count - 1)])

    # The repository is built on Windows and read on both, so the file keeps the
    # line endings it already had rather than gaining whatever this host prefers.
    $newline = if ((Get-Content $listScript -Raw) -match "`r`n") { "`r`n" } else { "`n" }
    [System.IO.File]::WriteAllText($listScript, ($rewritten -join $newline) + $newline)
}

$assetsFiles = Get-AssetsFile -Root $RepositoryRoot
Write-Host "Read $($assetsFiles.Count) project.assets.json file(s)."

if ($Update -and $Configuration -ne 'Base') {
    throw "-Update applies to the base list only. The OTLP entries and the runtime packs are decisions rather than a transcript of the graph; edit build/Get-OfflinePackages.ps1 by hand and let this check confirm the result."
}

if ($Configuration -eq 'RuntimePacks') {
    $expectedList = @(& $listScript -ListOnly -SkipOtlp) |
        Where-Object { $_ -match '^Microsoft\.(NETCore|AspNetCore|WindowsDesktop)\.App\.(Runtime|Host)\.' }

    if (-not $expectedList) {
        throw 'Get-OfflinePackages.ps1 listed no runtime packs. It should list four unless -SkipRuntimePacks was passed.'
    }

    $expected = @{}
    foreach ($line in $expectedList) {
        $parts = $line -split ' ', 2
        $expected[$parts[0]] = $parts[1]
    }

    # Two of the four packs depend on which OS the build runs from, so a list
    # that covers every supported build machine is necessarily a superset of any
    # one machine's graph. Their absence here is expected and reported rather
    # than treated as rot; anything else in the list that goes unused is rot.
    $hostDependent = @{
        'Microsoft.WindowsDesktop.App.Runtime.win-x64' = 'requested when building on Windows, not when cross-publishing'
        'Microsoft.NETCore.App.Host.win-x64'           = 'the apphost, already present in a Windows x64 SDK'
    }

    # Filter to the publish RID: a build host with a different RID also downloads
    # its own packs, and those are not what the release package is made of.
    $actual = @{}
    foreach ($entry in (Get-DownloadDependency -AssetsFiles $assetsFiles).GetEnumerator()) {
        if ($entry.Key -like '*.win-x64') { $actual[$entry.Key] = $entry.Value }
    }

    if (-not $actual.Count) {
        throw 'No win-x64 runtime packs in the restore graph. Run this after a self-contained publish; a framework-dependent build downloads none.'
    }

    $problems = @()

    foreach ($id in $actual.Keys) {
        if (-not $expected.ContainsKey($id)) {
            $problems += "$id $($actual[$id].Version) is downloaded by $($actual[$id].Projects -join ', ') but missing from the list."
        }
        elseif ($expected[$id] -ne $actual[$id].Version) {
            $problems += "$id : the list says $($expected[$id]), the build resolved $($actual[$id].Version)."
        }
    }

    foreach ($id in $expected.Keys) {
        if ($actual.ContainsKey($id)) { continue }

        if ($hostDependent.ContainsKey($id)) {
            Write-Host "  $id is listed but unused on this host — $($hostDependent[$id])." -ForegroundColor DarkGray
        }
        else {
            $problems += "$id is in the list but no project downloads it."
        }
    }

    if ($problems) {
        $problems | ForEach-Object { Write-Host "  $_" -ForegroundColor Yellow }
        throw 'The runtime pack list does not match the restore graph. A self-contained offline build would fail. Fix build/Get-OfflinePackages.ps1.'
    }

    foreach ($entry in $actual.GetEnumerator() | Sort-Object Key) {
        Write-Host "  $($entry.Key) $($entry.Value.Version)  <- $($entry.Value.Projects -join ', ')"
    }

    Write-Host "Runtime packs match: $($actual.Count) of $($expected.Count) listed pack(s) used on this host." -ForegroundColor Green
    return
}

$actual = Get-ResolvedPackage -AssetsFiles $assetsFiles
$expectedBase = @(& $listScript -ListOnly -SkipRuntimePacks -SkipOtlp)
$expectedOtlp = @(& $listScript -ListOnly -SkipRuntimePacks)

if ($Configuration -eq 'Base') {
    $missing = @($actual | Where-Object { $expectedBase -notcontains $_ })
    $extra = @($expectedBase | Where-Object { -not $actual.Contains($_) })

    if ($Update) {
        if (-not $missing -and -not $extra) {
            Write-Host "Nothing to update: the list already matches the restore graph ($($actual.Count) packages)." -ForegroundColor Green
            return
        }

        if ($missing) { Write-Difference "Adding:" $missing }
        if ($extra) { Write-Difference "Removing:" $extra }

        Set-BaseList -Packages (@($actual) | Sort-Object)
        Write-Host "Rewrote the base list in build/Get-OfflinePackages.ps1: $($actual.Count) packages. Commit it with the change that moved them." -ForegroundColor Green
        return
    }

    if ($missing) { Write-Difference "Resolved by the build but missing from the list:" $missing }
    if ($extra) { Write-Difference "In the list but not resolved by the build:" $extra }

    if ($missing -or $extra) {
        throw "build/Get-OfflinePackages.ps1 no longer matches the base restore graph ($($actual.Count) packages resolved, $($expectedBase.Count) listed). Regenerate it in this same change: pwsh build/Test-OfflinePackageList.ps1 -Configuration Base -Update"
    }

    Write-Host "Base package list matches the restore graph: $($actual.Count) packages." -ForegroundColor Green
    return
}

# Otlp.
$missing = @($actual | Where-Object { $expectedOtlp -notcontains $_ })
$otlpOnly = @($expectedOtlp | Where-Object { $expectedBase -notcontains $_ })
$notSeen = @($otlpOnly | Where-Object { -not $actual.Contains($_) })

if ($missing) { Write-Difference "Resolved with the OTLP exporter enabled but missing from the list:" $missing }
if ($notSeen) { Write-Difference "Listed as OTLP-only but absent from the OTLP restore graph:" $notSeen }

if ($missing -or $notSeen) {
    throw 'build/Get-OfflinePackages.ps1 no longer matches the OTLP restore graph. An offline build with -EnableOtlpExporter would fail. Update the list.'
}

Write-Host "OTLP package list matches the restore graph: $($actual.Count) packages resolved, $($otlpOnly.Count) of them OTLP-only." -ForegroundColor Green
