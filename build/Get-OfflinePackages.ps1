<#
.SYNOPSIS
    Downloads every NuGet package this solution needs, for a build machine that
    cannot reach api.nuget.org.

.DESCRIPTION
    Run this on any machine with internet access, copy the output folder to the
    isolated build machine, and restore from it with --source. The list is
    grouped into three sets, because you probably do not need all three:

      Base
          Everything 'dotnet build' and 'dotnet test' of the solution need,
          across every project. Always downloaded. This branch's list is
          larger than main's: net9.0 needs System.Text.Json,
          System.IO.Pipelines and their neighbours as packages, where net10.0
          has them in the shared framework. The count is whatever the list
          below actually holds - a number in this sentence went stale twice,
          once on each line, and Test-OfflinePackageList.ps1 is the thing that
          actually checks.

      Runtime packs (4 packages, about 92 MB)
          Only for 'Build.ps1 -SelfContained', which is how the release package
          is produced: they are the .NET runtime that gets bundled into it.
          Skip with -SkipRuntimePacks.

          These are win-x64, the deployment target, and they also cover the
          build machine's own runtime pack request when that machine is Windows
          x64 — which the documented one is. A self-contained publish from a
          different host asks for its own RID's packs as well. Two of the four
          are requested only from some hosts; see the list itself below.

      OpenTelemetry (16 packages, about 6 MB)
          Only for 'Build.ps1 -EnableOtlpExporter'. Two exporters share the
          flag: the Serilog sink that sends LOG RECORDS from the agent-hosted
          connector, and the OpenTelemetry SDK that sends TRACES AND METRICS
          from every push executable.

          That configuration raises Google.Protobuf and Grpc.Core.Api rather
          than merely adding packages, so the higher versions are listed here
          alongside the pinned ones in the base set. The gRPC packages belong to
          the Serilog sink alone; the OpenTelemetry SDK speaks both OTLP
          protocols over HttpClient and adds no gRPC stack of its own.

          Skip with -SkipOtlp.

    Runtime pack versions are chosen by the SDK, not by this repository, so the
    script asks MSBuild for BundledNETCoreAppPackageVersion rather than
    hard-coding one. Run it from a clone, or from the source\ tree inside a
    release package, and it will pick the right version by itself. Pass
    -RuntimeVersion to override, and note that the version must match the SDK on
    the machine that will do the offline build, not the one downloading here.

    The lists are verified in CI against the actual restore graph by
    Test-OfflinePackageList.ps1, so a dependency bump that leaves this file
    behind fails the build rather than failing an offline restore months later.

.PARAMETER ListOnly
    Prints "<id> <version>" per line and downloads nothing. This is what the CI
    drift check consumes.

.EXAMPLE
    .\build\Get-OfflinePackages.ps1
    Compress-Archive .\offline-packages\* offline-packages.zip

.EXAMPLE
    .\build\Get-OfflinePackages.ps1 -SkipRuntimePacks -SkipOtlp
    Just enough to build and test, without a self-contained publish.
#>

[CmdletBinding()]
param(
    [string]$OutputDirectory = './offline-packages',

    # Empty means "ask the SDK". See the note above about which machine's SDK.
    [string]$RuntimeVersion = '',

    [switch]$SkipRuntimePacks,

    [switch]$SkipOtlp,

    [switch]$ListOnly
)

$ErrorActionPreference = 'Stop'

# --- Base: required by any build of the solution ---------------------------
# The entries between the two markers are generated from the restore graph. A
# dependency bump makes them wrong, so regenerate rather than hand-edit:
#     pwsh build/Test-OfflinePackageList.ps1 -Configuration Base -Update
$packages = @(
    # BEGIN BASE LIST
    @{ Id = 'Azure.Core'; Version = '1.54.0' }
    @{ Id = 'Azure.Identity'; Version = '1.21.0' }
    @{ Id = 'Azure.Security.KeyVault.Secrets'; Version = '4.11.0' }
    @{ Id = 'Google.Protobuf'; Version = '3.18.0' }
    @{ Id = 'Grpc.Core'; Version = '2.40.0' }
    @{ Id = 'Grpc.Core.Api'; Version = '2.40.0' }
    @{ Id = 'Grpc.Tools'; Version = '2.40.0' }
    @{ Id = 'Microsoft.Bcl.AsyncInterfaces'; Version = '10.0.3' }
    @{ Id = 'Microsoft.CodeCoverage'; Version = '18.9.0' }
    @{ Id = 'Microsoft.CSharp'; Version = '4.5.0' }
    @{ Id = 'Microsoft.Data.SqlClient'; Version = '5.2.2' }
    @{ Id = 'Microsoft.Data.SqlClient.SNI.runtime'; Version = '5.2.0' }
    @{ Id = 'Microsoft.Extensions.Configuration.Abstractions'; Version = '10.0.3' }
    @{ Id = 'Microsoft.Extensions.DependencyInjection.Abstractions'; Version = '10.0.3' }
    @{ Id = 'Microsoft.Extensions.Diagnostics.Abstractions'; Version = '10.0.3' }
    @{ Id = 'Microsoft.Extensions.FileProviders.Abstractions'; Version = '10.0.3' }
    @{ Id = 'Microsoft.Extensions.Hosting.Abstractions'; Version = '10.0.3' }
    @{ Id = 'Microsoft.Extensions.Logging.Abstractions'; Version = '10.0.3' }
    @{ Id = 'Microsoft.Extensions.Options'; Version = '10.0.3' }
    @{ Id = 'Microsoft.Extensions.Primitives'; Version = '10.0.3' }
    @{ Id = 'Microsoft.Graph'; Version = '5.105.0' }
    @{ Id = 'Microsoft.Graph.Core'; Version = '3.2.5' }
    @{ Id = 'Microsoft.Identity.Client'; Version = '4.83.1' }
    @{ Id = 'Microsoft.Identity.Client.Extensions.Msal'; Version = '4.83.1' }
    @{ Id = 'Microsoft.IdentityModel.Abstractions'; Version = '8.15.0' }
    @{ Id = 'Microsoft.IdentityModel.JsonWebTokens'; Version = '8.15.0' }
    @{ Id = 'Microsoft.IdentityModel.Logging'; Version = '8.15.0' }
    @{ Id = 'Microsoft.IdentityModel.Protocols'; Version = '8.15.0' }
    @{ Id = 'Microsoft.IdentityModel.Protocols.OpenIdConnect'; Version = '8.15.0' }
    @{ Id = 'Microsoft.IdentityModel.Tokens'; Version = '8.15.0' }
    @{ Id = 'Microsoft.IdentityModel.Validators'; Version = '8.6.1' }
    @{ Id = 'Microsoft.Kiota.Abstractions'; Version = '1.22.2' }
    @{ Id = 'Microsoft.Kiota.Authentication.Azure'; Version = '1.21.1' }
    @{ Id = 'Microsoft.Kiota.Http.HttpClientLibrary'; Version = '1.21.1' }
    @{ Id = 'Microsoft.Kiota.Serialization.Form'; Version = '1.21.1' }
    @{ Id = 'Microsoft.Kiota.Serialization.Json'; Version = '1.21.1' }
    @{ Id = 'Microsoft.Kiota.Serialization.Multipart'; Version = '1.21.1' }
    @{ Id = 'Microsoft.Kiota.Serialization.Text'; Version = '1.21.1' }
    @{ Id = 'Microsoft.NET.Test.Sdk'; Version = '18.9.0' }
    @{ Id = 'Microsoft.NETCore.Platforms'; Version = '1.1.0' }
    @{ Id = 'Microsoft.NETCore.Targets'; Version = '1.1.0' }
    @{ Id = 'Microsoft.SqlServer.Server'; Version = '1.0.0' }
    @{ Id = 'Microsoft.TestPlatform.ObjectModel'; Version = '18.9.0' }
    @{ Id = 'Microsoft.TestPlatform.TestHost'; Version = '18.9.0' }
    @{ Id = 'Serilog'; Version = '4.4.0' }
    @{ Id = 'Serilog.Sinks.Console'; Version = '6.1.1' }
    @{ Id = 'Serilog.Sinks.EventLog'; Version = '4.0.0' }
    @{ Id = 'Serilog.Sinks.File'; Version = '7.0.0' }
    @{ Id = 'Std.UriTemplate'; Version = '2.0.8' }
    @{ Id = 'System.ClientModel'; Version = '1.10.0' }
    @{ Id = 'System.Configuration.ConfigurationManager'; Version = '8.0.0' }
    @{ Id = 'System.Data.Odbc'; Version = '9.0.0' }
    @{ Id = 'System.Diagnostics.DiagnosticSource'; Version = '10.0.3' }
    @{ Id = 'System.Diagnostics.EventLog'; Version = '8.0.0' }
    @{ Id = 'System.IdentityModel.Tokens.Jwt'; Version = '8.15.0' }
    @{ Id = 'System.IO.Pipelines'; Version = '10.0.3' }
    @{ Id = 'System.Memory'; Version = '4.5.3' }
    @{ Id = 'System.Memory.Data'; Version = '10.0.3' }
    @{ Id = 'System.Runtime'; Version = '4.3.0' }
    @{ Id = 'System.Runtime.Caching'; Version = '8.0.0' }
    @{ Id = 'System.Security.Cryptography.Cng'; Version = '4.5.0' }
    @{ Id = 'System.Security.Cryptography.ProtectedData'; Version = '8.0.0' }
    @{ Id = 'System.Text.Encoding'; Version = '4.3.0' }
    @{ Id = 'System.Text.Encodings.Web'; Version = '10.0.3' }
    @{ Id = 'System.Text.Json'; Version = '10.0.3' }
    @{ Id = 'System.ValueTuple'; Version = '4.5.0' }
    @{ Id = 'xunit'; Version = '2.9.3' }
    @{ Id = 'xunit.abstractions'; Version = '2.0.3' }
    @{ Id = 'xunit.analyzers'; Version = '1.18.0' }
    @{ Id = 'xunit.assert'; Version = '2.9.3' }
    @{ Id = 'xunit.core'; Version = '2.9.3' }
    @{ Id = 'xunit.extensibility.core'; Version = '2.9.3' }
    @{ Id = 'xunit.extensibility.execution'; Version = '2.9.3' }
    @{ Id = 'xunit.runner.visualstudio'; Version = '4.0.0' }
    # END BASE LIST
)

# --- Runtime packs: only for a self-contained publish ----------------------
if (-not $SkipRuntimePacks) {
    if (-not $RuntimeVersion) {
        $project = [System.IO.Path]::Combine(
            $PSScriptRoot, '..', 'src', 'SqlTicketsConnector', 'SqlTicketsConnector.csproj')

        if ((Get-Command dotnet -ErrorAction SilentlyContinue) -and (Test-Path $project)) {
            # The SDK decides which runtime pack a self-contained publish pulls,
            # so ask it rather than pinning a version here that goes stale on the
            # next SDK servicing update.
            $probe = (& dotnet msbuild $project -getProperty:BundledNETCoreAppPackageVersion -nologo 2>&1 | Out-String).Trim()
            if ($LASTEXITCODE -eq 0 -and $probe -match '^\d+\.\d+\.\d+') {
                $RuntimeVersion = $Matches[0]
                Write-Verbose "Runtime pack version $RuntimeVersion, from the SDK on this machine."
            }
        }
    }

    if (-not $RuntimeVersion) {
        $RuntimeVersion = '10.0.9'
        Write-Warning "Could not ask the SDK for its runtime pack version; assuming $RuntimeVersion. On the build machine, run: dotnet msbuild src\SqlTicketsConnector\SqlTicketsConnector.csproj -getProperty:BundledNETCoreAppPackageVersion"
    }

    # Which of these a given machine actually downloads depends on the host it
    # builds from, so the list is the union rather than any one machine's view,
    # and Test-OfflinePackageList.ps1 knows which two are host-dependent.
    #
    #   NETCore.App.Runtime      always
    #   AspNetCore.App.Runtime   always
    #   WindowsDesktop.App.Runtime   requested when building on Windows;
    #                            absent when cross-publishing from macOS or Linux
    #   NETCore.App.Host         the apphost. A Windows x64 SDK already has its
    #                            own, so only a cross-build downloads it
    $packages += @(
        @{ Id = 'Microsoft.NETCore.App.Runtime.win-x64'; Version = $RuntimeVersion }
        @{ Id = 'Microsoft.AspNetCore.App.Runtime.win-x64'; Version = $RuntimeVersion }
        @{ Id = 'Microsoft.WindowsDesktop.App.Runtime.win-x64'; Version = $RuntimeVersion }
        @{ Id = 'Microsoft.NETCore.App.Host.win-x64'; Version = $RuntimeVersion }
    )
}

# --- OpenTelemetry: only for -EnableOtlpExporter ---------------------------
#
# TWO EXPORTERS, NOT ONE, AND THEY ARRIVED SEPARATELY. The Serilog sink carries
# LOG RECORDS and lives in the agent-hosted connector. The OpenTelemetry SDK and
# its OTLP exporter carry TRACES AND METRICS and live in PushCore, so they reach
# every push executable. Both are behind the same MSBuild flag; neither is in
# the default graph.
#
# The gRPC block below belongs to the Serilog sink alone. OpenTelemetry 1.18.0
# speaks both OTLP protocols over HttpClient and pulls no gRPC stack of its own,
# which is why adding it did not disturb the Google.Protobuf pinning that the
# sink forces.
#
# The seven Microsoft.Extensions.* entries are deliberately NOT pinned in
# Directory.Packages.props. They arrive only under this flag, nothing in the
# base build references them, and seven entries the default configuration never
# uses would be seven more things to keep true. They are transcribed here
# instead, which is the file whose job is to be a transcript - and CI compares
# it against the real graph, so a floor that moves shows up as a failed check
# rather than as a folder of nupkgs that does not restore.
if (-not $SkipOtlp) {
    $packages += @(
        @{ Id = 'Google.Protobuf'; Version = '3.35.1' }
        @{ Id = 'Grpc.Core.Api'; Version = '2.62.0' }
        @{ Id = 'Grpc.Net.Client'; Version = '2.62.0' }
        @{ Id = 'Grpc.Net.Common'; Version = '2.62.0' }
        @{ Id = 'Serilog.Sinks.OpenTelemetry'; Version = '4.1.1' }

        @{ Id = 'OpenTelemetry'; Version = '1.18.0' }
        @{ Id = 'OpenTelemetry.Api'; Version = '1.18.0' }
        @{ Id = 'OpenTelemetry.Api.ProviderBuilderExtensions'; Version = '1.18.0' }
        @{ Id = 'OpenTelemetry.Exporter.OpenTelemetryProtocol'; Version = '1.18.0' }

        @{ Id = 'Microsoft.Extensions.Configuration'; Version = '10.0.0' }
        @{ Id = 'Microsoft.Extensions.Configuration.Binder'; Version = '10.0.0' }
        @{ Id = 'Microsoft.Extensions.Configuration.EnvironmentVariables'; Version = '10.0.0' }
        @{ Id = 'Microsoft.Extensions.DependencyInjection'; Version = '10.0.0' }
        @{ Id = 'Microsoft.Extensions.Logging'; Version = '10.0.0' }
        @{ Id = 'Microsoft.Extensions.Logging.Configuration'; Version = '10.0.0' }
        @{ Id = 'Microsoft.Extensions.Options.ConfigurationExtensions'; Version = '10.0.0' }
    )
}

if ($ListOnly) {
    $packages | ForEach-Object { "$($_.Id) $($_.Version)" }
    return
}

New-Item -ItemType Directory -Path $OutputDirectory -Force | Out-Null

$failed = @()
$i = 0

foreach ($package in $packages) {
    $i++
    $id = $package.Id.ToLowerInvariant()
    $version = $package.Version.ToLowerInvariant()
    $file = Join-Path $OutputDirectory "$id.$version.nupkg"

    if (Test-Path $file) {
        Write-Host ("[{0,3}/{1}] {2} {3} already present" -f $i, $packages.Count, $package.Id, $package.Version)
        continue
    }

    # The flat container is the download endpoint behind every NuGet client:
    #   https://api.nuget.org/v3-flatcontainer/<id>/<version>/<id>.<version>.nupkg
    $url = "https://api.nuget.org/v3-flatcontainer/$id/$version/$id.$version.nupkg"

    try {
        Write-Host ("[{0,3}/{1}] {2} {3}" -f $i, $packages.Count, $package.Id, $package.Version)

        # Download to a temp name and rename only on success. An interrupted
        # Invoke-WebRequest leaves a partial file, and the 'already present'
        # check above would count that fragment as a completed download on the
        # next run - a corrupt package set reported as complete.
        $tmp = "$file.download"
        Invoke-WebRequest -Uri $url -OutFile $tmp -UseBasicParsing
        Move-Item -Path $tmp -Destination $file -Force
    }
    catch {
        Remove-Item -Path "$file.download" -ErrorAction SilentlyContinue
        Write-Warning "  failed: $url ($($_.Exception.Message))"
        $failed += "$($package.Id) $($package.Version)"
    }
}

Write-Host ''

if ($failed) {
    Write-Warning "$($failed.Count) package(s) did not download:"
    $failed | ForEach-Object { Write-Warning "  $_" }
    if ($failed -match 'App\.(Runtime|Host)') {
        Write-Warning 'A missing runtime pack means the version does not exist on nuget.org, which usually means -RuntimeVersion does not match an SDK. See the help at the top of this script.'
    }
    throw 'Incomplete package set. An offline restore would still fail, so this is an error rather than a warning.'
}

$size = (Get-ChildItem $OutputDirectory -Filter *.nupkg | Measure-Object -Property Length -Sum).Sum / 1MB
Write-Host ("All {0} packages downloaded to {1} ({2:N0} MB)." -f $packages.Count, $OutputDirectory, $size) -ForegroundColor Green
Write-Host ''
Write-Host 'On this machine:'
Write-Host '  Compress-Archive .\offline-packages\* offline-packages.zip'
Write-Host ''
Write-Host 'On the build machine, from the source tree (a release package has one under source\):'
Write-Host '  Expand-Archive .\offline-packages.zip -DestinationPath C:\offline-packages'
Write-Host '  dotnet restore .\SqlTicketsConnector.sln --source C:\offline-packages'
Write-Host '  dotnet build   .\SqlTicketsConnector.sln -c Release --no-restore'
Write-Host '  dotnet test    .\SqlTicketsConnector.sln -c Release --no-build'
Write-Host ''
Write-Host 'For a self-contained publish, add the folder as a source first:'
Write-Host '  dotnet nuget add source C:\offline-packages -n offline'
Write-Host '  .\Build.ps1 -SelfContained'
Write-Host ''
Write-Host 'In Visual Studio, or anywhere a restore must never reach the network,'
Write-Host 'copy build\NuGet.offline.config to the solution root as NuGet.config and'
Write-Host 'point it at this folder. A source passed on the command line covers one'
Write-Host 'command; that file covers the tree, and it is the only one the IDE reads.'
