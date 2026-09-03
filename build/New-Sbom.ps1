<#
.SYNOPSIS
    Produces a CycloneDX software bill of materials for a build of this solution.

.DESCRIPTION
    Reads the resolved dependency graph out of the obj\project.assets.json files
    that restore already wrote, and emits CycloneDX 1.6 JSON.

    WHY NOT A TOOL. The obvious answer is the CycloneDX dotnet tool. It is not
    used here for the reason that shapes most of this repository's build: the
    offline restore path. build\Get-OfflinePackages.ps1 pins an exact package
    set so that an air-gapped machine can rebuild what shipped, and a global
    tool acquired at build time is a dependency that path cannot satisfy and
    that build\Test-OfflinePackageList.ps1 cannot verify. project.assets.json is
    already on disk after restore, already carries the fully resolved graph
    including every transitive, and already carries each package's SHA512. So
    the input is better than what a tool would re-derive, and it costs nothing.

    WHY THE BUILD CONFIGURATION IS IN THE METADATA, AND WHY THAT MATTERS. There
    is no single bill of materials for this repository. Directory.Packages.props
    pins two different Google.Protobuf versions either side of
    EnableOtlpExporter, and the OTLP build additionally pulls
    Serilog.Sinks.OpenTelemetry and a second gRPC stack. Two builds of the same
    commit therefore ship genuinely different components. An SBOM that did not
    record which build it described would be answering "are we exposed to this
    advisory" for a package set the customer may not have. Every SBOM this
    script writes names its target framework, its runtime identifier, its
    configuration and its OTLP state, and the file name carries them too.

    WHAT IS AND IS NOT A COMPONENT. Every NuGet package in the resolved graph is
    a component, transitive included, each with its purl, its version and the
    SHA512 restore recorded. Project references are components of type
    "application" with no hash, because a project is built here rather than
    fetched and there is nothing to compare a hash against. The .NET runtime
    itself is recorded as a component when the build is self-contained, since in
    that case it ships inside the package and is part of what the customer runs.

.PARAMETER SolutionRoot
    Repository root. Defaults to the parent of this script's directory.

.PARAMETER OutputPath
    Where to write the SBOM. Defaults to a name derived from the build inputs.

.PARAMETER TargetFramework
    The framework the build targeted, for the metadata. Read from the assets
    files when not supplied.

.PARAMETER Runtime
    The runtime identifier the build published for.

.PARAMETER Configuration
    Debug or Release.

.PARAMETER EnableOtlpExporter
    Set when the build was produced with -p:EnableOtlpExporter=true.

.PARAMETER SelfContained
    Set when the build bundled the .NET runtime.

.PARAMETER Version
    The product version to stamp into the SBOM metadata.

.EXAMPLE
    .\New-Sbom.ps1 -Configuration Release -Runtime win-x64

.NOTES
    Windows PowerShell 5.1 and PowerShell 7 both. Two traps are handled and are
    commented where they are handled: ConvertFrom-Json returns PSCustomObject
    rather than a hashtable on 5.1 and has no -AsHashtable there, and
    ConvertTo-Json defaults to a depth of 2 on both, which would silently
    truncate the component tree into "System.Object[]".
#>

[CmdletBinding()]
param(
    [string]$SolutionRoot,
    [string]$OutputPath,
    [string]$TargetFramework,
    [string]$Runtime = 'win-x64',
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Release',
    [switch]$EnableOtlpExporter,
    [switch]$SelfContained,
    [string]$Version = '1.6.0'
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

# $PSScriptRoot is empty inside a param() default block under Windows PowerShell
# 5.1, so the default is resolved here instead. This is the same trap the deploy
# scripts hit and is why none of them default a path in the parameter list.
if (-not $SolutionRoot) { $SolutionRoot = Split-Path $PSScriptRoot -Parent }
$SolutionRoot = (Resolve-Path $SolutionRoot).Path

# ---------------------------------------------------------------------------
# Collect the assets files.
#
# One per project, and they must exist: an SBOM built from a partial restore
# would list fewer components than the package actually carries, which is worse
# than no SBOM at all because it reads as authoritative.
# ---------------------------------------------------------------------------

# src only, and that is the whole point rather than an oversight: tests\ pulls
# xunit and its dependencies, none of which ship. An SBOM listing a test
# framework as a component of the delivered product invites a customer's
# vulnerability scanner to raise findings against code that is not on their
# server, and every one of those findings costs somebody a day to dismiss.
$assetsFiles = @(Get-ChildItem -Path (Join-Path $SolutionRoot 'src') -Recurse -Filter 'project.assets.json' -ErrorAction SilentlyContinue)

if ($assetsFiles.Count -eq 0) {
    throw "No project.assets.json found under $SolutionRoot\src. Run 'dotnet restore' first; without it there is no resolved graph to describe."
}

Write-Verbose "Reading $($assetsFiles.Count) assets file(s)."

# ---------------------------------------------------------------------------
# Walk them.
#
# Keyed on "name/version" rather than name, deliberately. Central package
# management with transitive pinning means one version normally wins, but the
# key must not assume it: if two versions ever do coexist the SBOM has to say
# so, and a name-keyed dictionary would quietly drop one.
# ---------------------------------------------------------------------------

$packages = @{}
$projects = @{}
$frameworksSeen = @{}

foreach ($file in $assetsFiles) {
    $json = Get-Content -LiteralPath $file.FullName -Raw | ConvertFrom-Json

    # The libraries section carries type, path and sha512 for everything the
    # targets section resolved. Both are needed: targets says what was chosen
    # for this framework, libraries says what it actually is.
    $libraries = $json.libraries
    $targets = $json.targets

    # Enumerated as objects and then read for .Name, rather than the shorter
    # .Properties.Name. Under Set-StrictMode -Version Latest, reading .Name off
    # an EMPTY property collection throws "The property 'Name' cannot be found
    # on this object" instead of yielding nothing, and a project with no package
    # references at all - Connector.Extraction is one - has exactly that.
    # The short form works on ten of the eleven projects and fails on the
    # eleventh, which is the worst way for it to be wrong.
    foreach ($tfmProperty in $targets.PSObject.Properties) {
        $frameworksSeen[$tfmProperty.Name] = $true
    }

    foreach ($libProperty in $libraries.PSObject.Properties) {
        $libKey = $libProperty.Name
        $lib = $libProperty.Value
        $name, $ver = $libKey -split '/', 2

        if ($lib.type -eq 'project') {
            if (-not $projects.ContainsKey($name)) {
                $projects[$name] = [pscustomobject]@{
                    Name    = $name
                    Version = $ver
                }
            }
            continue
        }

        if ($packages.ContainsKey($libKey)) { continue }

        # sha512 is absent for a library resolved from a local folder rather
        # than a feed. Recorded as absent rather than as empty, so a reader can
        # tell "no hash was published" from "the hash is blank".
        $sha = $null
        if ($lib.PSObject.Properties.Name -contains 'sha512' -and $lib.sha512) {
            $sha = $lib.sha512
        }

        $packages[$libKey] = [pscustomobject]@{
            Name    = $name
            Version = $ver
            Sha512  = $sha
        }
    }
}

if (-not $TargetFramework) {
    # Deterministic rather than whichever hashtable order happens to give.
    $TargetFramework = ($frameworksSeen.Keys | Sort-Object) -join ', '
}

Write-Verbose "Found $($packages.Count) package(s) and $($projects.Count) project(s) across $TargetFramework."

# ---------------------------------------------------------------------------
# Build the CycloneDX document.
# ---------------------------------------------------------------------------

function ConvertTo-Purl {
    param([string]$Name, [string]$Version)
    # pkg:nuget/<name>@<version>. NuGet ids are already restricted to characters
    # that need no percent-encoding here, but the version can carry a build
    # metadata "+" which does.
    $encodedVersion = $Version -replace '\+', '%2B'
    return "pkg:nuget/$Name@$encodedVersion"
}

$components = New-Object System.Collections.ArrayList

foreach ($key in ($packages.Keys | Sort-Object)) {
    $p = $packages[$key]

    $component = [ordered]@{
        type       = 'library'
        'bom-ref'  = ConvertTo-Purl -Name $p.Name -Version $p.Version
        name       = $p.Name
        version    = $p.Version
        purl       = ConvertTo-Purl -Name $p.Name -Version $p.Version
        scope      = 'required'
    }

    if ($p.Sha512) {
        # CycloneDX wants the hash hex-encoded. NuGet records it base64, which is
        # what the .nupkg.sha512 file beside the package also holds, so it is
        # converted rather than passed through: a reader comparing against a
        # freshly computed SHA-512 gets hex.
        try {
            $bytes = [Convert]::FromBase64String($p.Sha512)
            $hex = -join ($bytes | ForEach-Object { $_.ToString('x2') })
            $component['hashes'] = @(
                [ordered]@{ alg = 'SHA-512'; content = $hex }
            )
        }
        catch {
            Write-Warning "Could not decode the recorded hash for $($p.Name) $($p.Version); the component is listed without one."
        }
    }

    [void]$components.Add($component)
}

foreach ($key in ($projects.Keys | Sort-Object)) {
    $p = $projects[$key]
    [void]$components.Add([ordered]@{
        type      = 'application'
        'bom-ref' = "project:$($p.Name)"
        name      = $p.Name
        version   = $Version
        scope     = 'required'
        # No hash and no purl: this is built from the source in this repository
        # rather than fetched, so there is nothing external to compare against.
        # The source archive in the deployment package is the provenance.
        description = 'Built from source in this repository.'
    })
}

if ($SelfContained) {
    # A self-contained publish puts the runtime inside the package, so it is
    # part of what the customer runs and belongs in the bill. A framework
    # dependent build does not carry it and must not claim to.
    [void]$components.Add([ordered]@{
        type      = 'framework'
        'bom-ref' = "runtime:$TargetFramework/$Runtime"
        name      = 'Microsoft .NET Runtime'
        version   = $TargetFramework
        scope     = 'required'
        description = "Bundled by a self-contained publish for $Runtime."
    })
}

# A stable serial number derived from the build inputs, so that rebuilding the
# same configuration of the same commit produces the same identifier rather than
# a new one every run. A change advisory board comparing two SBOMs wants the
# differences to be real.
$identity = "$TargetFramework|$Runtime|$Configuration|$($EnableOtlpExporter.IsPresent)|$($SelfContained.IsPresent)|$Version"
$md5 = [System.Security.Cryptography.MD5]::Create()
try {
    $hashBytes = $md5.ComputeHash([System.Text.Encoding]::UTF8.GetBytes($identity))
}
finally {
    $md5.Dispose()
}
$guid = [guid]::new($hashBytes)

$timestamp = (Get-Date).ToUniversalTime().ToString('yyyy-MM-ddTHH:mm:ssZ')

$bom = [ordered]@{
    bomFormat    = 'CycloneDX'
    specVersion  = '1.6'
    serialNumber = "urn:uuid:$guid"
    version      = 1
    metadata     = [ordered]@{
        timestamp = $timestamp
        tools     = [ordered]@{
            components = @(
                [ordered]@{
                    type    = 'application'
                    name    = 'New-Sbom.ps1'
                    version = $Version
                    description = 'Generates this SBOM from the NuGet assets files restore produced.'
                }
            )
        }
        component = [ordered]@{
            type    = 'application'
            'bom-ref' = 'root:SqlTicketsConnector'
            name    = 'SqlTicketsConnector'
            version = $Version
            description = 'Microsoft 365 Copilot connector platform for SQL Server and Cloudera CDP sources.'
        }
        properties = @(
            [ordered]@{ name = 'build:targetFramework';     value = $TargetFramework }
            [ordered]@{ name = 'build:runtimeIdentifier';   value = $Runtime }
            [ordered]@{ name = 'build:configuration';       value = $Configuration }
            [ordered]@{ name = 'build:enableOtlpExporter';  value = $EnableOtlpExporter.IsPresent.ToString().ToLowerInvariant() }
            [ordered]@{ name = 'build:selfContained';       value = $SelfContained.IsPresent.ToString().ToLowerInvariant() }
            [ordered]@{ name = 'build:packageCount';        value = $packages.Count.ToString() }
            [ordered]@{ name = 'build:projectCount';        value = $projects.Count.ToString() }
        )
    }
    components = @($components)
}

# ---------------------------------------------------------------------------
# Write it.
# ---------------------------------------------------------------------------

if (-not $OutputPath) {
    $otlpTag = if ($EnableOtlpExporter) { 'otlp' } else { 'base' }
    $scTag = if ($SelfContained) { 'selfcontained' } else { 'framework' }
    $safeTfm = ($TargetFramework -replace '[^A-Za-z0-9._]', '-')
    $OutputPath = Join-Path $SolutionRoot "sbom-$safeTfm-$Runtime-$($Configuration.ToLowerInvariant())-$otlpTag-$scTag.cdx.json"
}

# Depth matters. ConvertTo-Json defaults to 2, which would render the component
# array as the string "System.Object[]" and produce a file that validates as
# JSON, looks plausible in a diff, and lists nothing.
$json = $bom | ConvertTo-Json -Depth 12

# UTF-8 without a byte order mark. A BOM is legal in JSON but several SBOM
# consumers reject it, and Set-Content on 5.1 writes one by default under
# -Encoding UTF8, so the encoding is constructed rather than named.
$utf8NoBom = New-Object System.Text.UTF8Encoding($false)
[System.IO.File]::WriteAllText($OutputPath, $json, $utf8NoBom)

Write-Host "SBOM written: $OutputPath" -ForegroundColor Green
Write-Host "  $($packages.Count) package component(s), $($projects.Count) project component(s)"
Write-Host "  build: $TargetFramework / $Runtime / $Configuration / otlp=$($EnableOtlpExporter.IsPresent) / selfcontained=$($SelfContained.IsPresent)"

return $OutputPath
