<#
.SYNOPSIS
    Builds the solution, runs the tests, and produces a deployment zip.

.DESCRIPTION
    Run this on a workstation with the .NET 8 SDK and internet access for NuGet
    restore. The output zip contains compiled binaries plus the deployment
    scripts, SQL scripts and documentation, so the target server needs neither
    the SDK nor NuGet connectivity.

    The build fails if the secret hygiene scan finds a credential shaped value in
    any appsettings.json, and if any test fails. Both are deliberate: the zip is
    the artefact a change advisory board approves, so it should not be possible
    to produce one from a tree that would not pass review.

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
    throw 'dotnet SDK not found. Install the .NET 8 SDK from https://dotnet.microsoft.com/download'
}

$otlp = if ($EnableOtlpExporter) { 'true' } else { 'false' }

Write-Host '== Secret hygiene scan ==' -ForegroundColor Cyan
dotnet build (Join-Path $PSScriptRoot 'build\SecretHygiene.proj') -t:ScanAppSettingsForSecrets -nologo -v:m
if ($LASTEXITCODE -ne 0) { throw 'Secret hygiene scan failed. Move the value into Key Vault and keep only the secret name in configuration.' }

Write-Host '== Restoring ==' -ForegroundColor Cyan
dotnet restore $solution
if ($LASTEXITCODE -ne 0) { throw 'Restore failed.' }

Write-Host '== Building ==' -ForegroundColor Cyan
dotnet build $solution -c $Configuration --no-restore -p:EnableOtlpExporter=$otlp
if ($LASTEXITCODE -ne 0) { throw 'Build failed.' }

if (-not $SkipTests) {
    Write-Host '== Testing ==' -ForegroundColor Cyan
    dotnet test $solution -c $Configuration --no-build --nologo
    if ($LASTEXITCODE -ne 0) { throw 'Tests failed. The redaction, watermark and rotation tests are control evidence; do not package a build that fails them.' }
}
else {
    Write-Warning 'Tests skipped. This build must not be released.'
}

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

# A deployment package must never contain a filled-in configuration file from a
# developer machine, or certificate material of any kind.
$forbidden = Get-ChildItem -Path $OutputRoot -Recurse -File -Include *.pfx, *.p12, *.pem, *.key
if ($forbidden) {
    $forbidden | ForEach-Object { Write-Error "Certificate material in the package: $($_.FullName)" }
    throw 'Refusing to package certificate or key material.'
}

$stamp = Get-Date -Format 'yyyyMMdd-HHmm'
$zipPath = Join-Path $PSScriptRoot "SqlTicketsConnector-deploy-$stamp.zip"

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
