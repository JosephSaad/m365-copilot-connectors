<#
.SYNOPSIS
    Builds the solution, runs the tests, and produces a deployment zip.

.DESCRIPTION
    Run this on a workstation with the .NET 10 SDK and internet access for NuGet
    restore. The output zip contains compiled binaries plus the deployment
    scripts, SQL scripts and documentation, so the target server needs neither
    the SDK nor NuGet connectivity.

    Four gates run before anything is published, and each of them can stop the
    build: the secret hygiene scan, a build with warnings treated as errors, the
    tests, and the dependency audit. They are the same four the CI workflow
    runs, deliberately: the zip is the artefact a change advisory board
    approves, so it should not be possible to produce one from a tree that would
    not pass review. If you add a gate here, add it to .github/workflows/build.yml.

    -SkipTests names its output SqlTicketsConnector-diagnostic-*.zip rather than
    -deploy-*, so a package built without the tests cannot be mistaken for a
    release candidate.

.EXAMPLE
    .\Build.ps1
    .\Build.ps1 -SelfContained
    .\Build.ps1 -SkipTests            # only for a diagnostic build, never for release
#>

[CmdletBinding()]
param(
    [string]$Configuration = 'Release',

    [string]$Runtime = 'win-x64',

    # Bundles the .NET runtime so the target server needs no runtime install.
    # Roughly triples the package size.
    [switch]$SelfContained,

    [switch]$SkipTests,

    # Includes the OpenTelemetry OTLP exporter. Off by default because it adds a
    # second gRPC stack to the dependency graph. See docs/SECURITY.md.
    [switch]$EnableOtlpExporter,

    [string]$OutputRoot = "$PSScriptRoot\artifacts"
)

$ErrorActionPreference = 'Stop'
$solution = Join-Path $PSScriptRoot 'SqlTicketsConnector.sln'
$connectorProject = Join-Path $PSScriptRoot 'src\SqlTicketsConnector\SqlTicketsConnector.csproj'
$pushProject = Join-Path $PSScriptRoot 'src\SqlGraphPush\SqlGraphPush.csproj'

if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) {
    throw 'dotnet SDK not found. Install the .NET 10 SDK from https://dotnet.microsoft.com/download'
}

$otlp = if ($EnableOtlpExporter) { 'true' } else { 'false' }

Write-Host '== Secret hygiene scan ==' -ForegroundColor Cyan
dotnet build (Join-Path $PSScriptRoot 'build\SecretHygiene.proj') -t:ScanAppSettingsForSecrets -nologo -v:m
if ($LASTEXITCODE -ne 0) { throw 'Secret hygiene scan failed. Move the value into Key Vault and keep only the secret name in configuration.' }

Write-Host '== Restoring ==' -ForegroundColor Cyan
# The OTLP switch adds packages through a conditional PackageReference, so the
# restore has to carry it: otherwise the dependency audit below inspects a graph
# the published package does not have.
dotnet restore $solution -p:EnableOtlpExporter=$otlp
if ($LASTEXITCODE -ne 0) { throw 'Restore failed.' }

Write-Host '== Building ==' -ForegroundColor Cyan
dotnet build $solution -c $Configuration --no-restore -warnaserror -p:EnableOtlpExporter=$otlp
if ($LASTEXITCODE -ne 0) { throw 'Build failed.' }

Write-Host '== Dependency audit ==' -ForegroundColor Cyan
# dotnet list exits 0 whether or not it finds anything, so the output is what
# has to be inspected. This is the gate that would have kept the Kiota
# header-leak advisory out of a release package.
$vulnerable = dotnet list $solution package --vulnerable --include-transitive 2>&1 | Out-String
Write-Host $vulnerable
if ($vulnerable -match 'has the following vulnerable packages') {
    throw 'Vulnerable packages found. Raise the version, or pin the transitive dependency with a comment naming the advisory.'
}

if (-not $SkipTests) {
    Write-Host '== Testing ==' -ForegroundColor Cyan
    dotnet test $solution -c $Configuration --no-build --nologo
    if ($LASTEXITCODE -ne 0) { throw 'Tests failed. The redaction, watermark and rotation tests are control evidence; do not package a build that fails them.' }
}
else {
    Write-Warning 'Tests skipped. This build must not be released.'
}

# A package built without the tests is named so that it cannot be mistaken for a
# release candidate on a change advisory board's desk.
$packagePrefix = if ($SkipTests) { 'SqlTicketsConnector-diagnostic' } else { 'SqlTicketsConnector-deploy' }

if (Test-Path $OutputRoot) {
    Remove-Item $OutputRoot -Recurse -Force
}
$publishDir = Join-Path $OutputRoot 'publish'
New-Item -ItemType Directory -Path $publishDir -Force | Out-Null

Write-Host '== Publishing connector ==' -ForegroundColor Cyan
$selfContainedFlag = if ($SelfContained) { 'true' } else { 'false' }

dotnet publish $connectorProject -c $Configuration -r $Runtime --self-contained $selfContainedFlag `
    -o $publishDir -p:EnableOtlpExporter=$otlp
if ($LASTEXITCODE -ne 0) { throw 'Publish failed.' }

Write-Host '== Publishing the direct push tool ==' -ForegroundColor Cyan
$pushDir = Join-Path $OutputRoot 'SqlGraphPush'
dotnet publish $pushProject -c $Configuration -r $Runtime --self-contained $selfContainedFlag -o $pushDir
if ($LASTEXITCODE -ne 0) { throw 'Publish of SqlGraphPush failed.' }

Write-Host '== Staging deployment assets ==' -ForegroundColor Cyan
Copy-Item (Join-Path $PSScriptRoot 'deploy\Install-Connector.ps1') $OutputRoot -Force
Copy-Item (Join-Path $PSScriptRoot 'deploy\Manifest.json') $OutputRoot -Force
Copy-Item (Join-Path $PSScriptRoot 'deploy\CustomConnectorPortMap.json') $OutputRoot -Force
Copy-Item (Join-Path $PSScriptRoot 'deploy\ConnectionInfo.json') $OutputRoot -Force
Copy-Item (Join-Path $PSScriptRoot 'README.md') $OutputRoot -Force

New-Item -ItemType Directory -Path (Join-Path $OutputRoot 'sql') -Force | Out-Null
Copy-Item (Join-Path $PSScriptRoot 'sql\*.sql') (Join-Path $OutputRoot 'sql') -Force

New-Item -ItemType Directory -Path (Join-Path $OutputRoot 'docs') -Force | Out-Null
Copy-Item (Join-Path $PSScriptRoot 'docs\*.md') (Join-Path $OutputRoot 'docs') -Force

Write-Host '== Staging source ==' -ForegroundColor Cyan
# The package carries a buildable copy of the tree under source\, so one
# download serves both deployment and rebuild. Note what this means: source code
# lands on the connector host. That was a deliberate customer decision, recorded
# in docs/SECURITY.md section 4; if your review disallows it, drop this block and
# publish the source archive as a separate release asset instead.
#
# Excluded: build output, the git directory, editor state, any package zips, and
# nupkgs staged by build\Get-OfflinePackages.ps1 into the default
# offline-packages\ folder — roughly 215 MB that would otherwise ride along.
# bin\ and obj\ in particular must never travel, both for size and because they
# hold artefacts from whichever machine last built.
$sourceRoot = Join-Path $OutputRoot 'source'
$excludedDirectories = @('bin', 'obj', 'artifacts', '.git', '.vs', 'packages', 'offline-packages')

$sourceItems = Get-ChildItem -Path $PSScriptRoot -Recurse -File | Where-Object {
    $relative = $_.FullName.Substring($PSScriptRoot.Length).TrimStart('\')
    $segments = $relative -split '\\'

    # Skip anything under an excluded directory, the output root itself, and any
    # archive or package file wherever it happens to sit.
    -not ($segments | Where-Object { $excludedDirectories -contains $_ }) -and
    -not $relative.StartsWith('artifacts') -and
    $_.Extension -ne '.zip' -and
    $_.Extension -ne '.nupkg'
}

foreach ($item in $sourceItems) {
    $relative = $item.FullName.Substring($PSScriptRoot.Length).TrimStart('\')
    $destination = Join-Path $sourceRoot $relative
    $parent = Split-Path $destination -Parent

    if (-not (Test-Path $parent)) {
        New-Item -ItemType Directory -Path $parent -Force | Out-Null
    }

    Copy-Item $item.FullName $destination -Force
}

# build\SecretHygiene.targets is imported by every project file, so a source tree
# without it does not even restore. Fail here rather than shipping a copy that
# cannot be built.
foreach ($required in @('SqlTicketsConnector.sln', 'build\SecretHygiene.targets', 'src\SqlTicketsConnector\SqlTicketsConnector.csproj')) {
    if (-not (Test-Path (Join-Path $sourceRoot $required))) {
        throw "The staged source tree is missing $required, so it would not build."
    }
}

$sourceCount = (Get-ChildItem $sourceRoot -Recurse -File).Count
Write-Host "Staged $sourceCount source file(s) under source\."

# A deployment package must never contain a filled-in configuration file from a
# developer machine, or certificate material of any kind.
$forbidden = Get-ChildItem -Path $OutputRoot -Recurse -File -Include *.pfx, *.p12, *.pem, *.key
if ($forbidden) {
    $forbidden | ForEach-Object { Write-Error "Certificate material in the package: $($_.FullName)" }
    throw 'Refusing to package certificate or key material.'
}

$stamp = Get-Date -Format 'yyyyMMdd-HHmm'
$zipPath = Join-Path $PSScriptRoot "$packagePrefix-$stamp.zip"

if (Test-Path $zipPath) { Remove-Item $zipPath -Force }
Compress-Archive -Path (Join-Path $OutputRoot '*') -DestinationPath $zipPath

Write-Host ''
Write-Host 'Build complete.' -ForegroundColor Green
Write-Host "Deployment package: $zipPath"
Write-Host ''
Write-Host 'Upload that zip to SharePoint, download it on the agent server, then run:'
Write-Host '  Unblock-File .\SqlTicketsConnector-deploy-*.zip'
Write-Host '  Expand-Archive .\SqlTicketsConnector-deploy-*.zip -DestinationPath C:\Staging\SqlTickets'
Write-Host '  cd C:\Staging\SqlTickets'
Write-Host '  .\Install-Connector.ps1 -SourcePath .\publish -ServiceAccount ''CONTOSO\svc_gca_reader$'''
Write-Host ''
Write-Host 'Before starting the service, replace every REPLACE-WITH- placeholder in'
Write-Host 'appsettings.json. Startup validation rejects them and names each one.'
