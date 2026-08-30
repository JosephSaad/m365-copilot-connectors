<#
    Install-Dashboard-IIS.ps1

    Hosts ConnectorState.Dashboard on IIS with Windows Authentication, which is
    the only host Program.cs supports.

    RUN THIS FROM AN ELEVATED POWERSHELL. Every step needs administrator rights.

    Order matters: the ASP.NET Core Hosting Bundle registers the ASP.NET Core
    Module into IIS, so IIS must exist first. Installing the bundle before IIS
    leaves the module unregistered and every request returns 500.19.
#>

[CmdletBinding()]
param(
    [string]$SiteName     = 'ConnectorState',
    [string]$PhysicalPath = 'C:\inetpub\ConnectorState',
    # Relative to this script, so it follows the repository rather than naming
    # one person's checkout. Produce it with:
    #   dotnet publish src\ConnectorState.Dashboard -c Release -o artifacts\dashboard
    [string]$SourcePath   = (Join-Path $PSScriptRoot '..\artifacts\dashboard'),
    [int]$HttpPort        = 8080,
    [int]$HttpsPort       = 8443
)

$ErrorActionPreference = 'Stop'

if (-not ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()
        ).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    throw 'Not elevated. Re-run this from an administrator PowerShell.'
}

function Step([string]$m) { Write-Host "`n== $m ==" -ForegroundColor Cyan }

# -------------------------------------------------------------------------
Step '1. IIS features'

# WindowsAuthentication is the one that matters: Program.cs names the IIS
# authentication scheme and has no fallback handler of its own.
$features = @(
    'IIS-WebServerRole'
    'IIS-WebServer'
    'IIS-CommonHttpFeatures'
    'IIS-StaticContent'
    'IIS-DefaultDocument'
    'IIS-HttpErrors'
    'IIS-Security'
    'IIS-RequestFiltering'
    'IIS-WindowsAuthentication'
    'IIS-ManagementConsole'
    'IIS-ManagementScriptingTools'
)

foreach ($f in $features) {
    $state = (Get-WindowsOptionalFeature -Online -FeatureName $f).State
    if ($state -eq 'Enabled') {
        Write-Host "  already enabled  $f"
    }
    else {
        Write-Host "  enabling         $f"
        [void](Enable-WindowsOptionalFeature -Online -FeatureName $f -All -NoRestart)
    }
}

# -------------------------------------------------------------------------
Step '2. ASP.NET Core 9 Hosting Bundle (registers the ASP.NET Core Module)'

# IIS must be REGISTERED, not merely requested, before the bundle runs. The
# installer probes for IIS and silently omits the ASP.NET Core Module when it
# does not find it - leaving a bundle that reports success and an IIS with no
# handler. Enable-WindowsOptionalFeature -NoRestart returns before that is true,
# so wait for the service to actually exist.
$deadline = (Get-Date).AddMinutes(3)
while (-not (Get-Service W3SVC -ErrorAction SilentlyContinue)) {
    if ((Get-Date) -gt $deadline) {
        throw 'W3SVC never appeared. The IIS features need a reboot; restart and re-run this script.'
    }
    Write-Host '  waiting for IIS to register...'
    Start-Sleep -Seconds 5
}

$ancm = 'C:\Windows\System32\inetsrv\aspnetcorev2.dll'
$bundle = Get-ChildItem 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall',
                        'HKLM:\SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall' `
                        -ErrorAction SilentlyContinue |
    ForEach-Object { $_.GetValue('DisplayName') } |
    Where-Object { $_ -like '*ASP.NET Core*Hosting Bundle*' }

if (Test-Path $ancm) {
    Write-Host '  ANCM already present'
}
elseif ($bundle) {
    # Installed, but without the IIS half - the exact state a bundle-before-IIS
    # ordering leaves behind. Repair adds the module now that IIS exists.
    Write-Host '  bundle installed but ANCM missing; repairing'
    winget repair --id Microsoft.DotNet.HostingBundle.9 -e --accept-source-agreements
}
else {
    winget install --id Microsoft.DotNet.HostingBundle.9 -e `
        --accept-package-agreements --accept-source-agreements --disable-interactivity
}

net stop was /y | Out-Null
net start w3svc | Out-Null

if (-not (Test-Path $ancm)) {
    throw "ANCM still not registered at $ancm. Reboot and re-run: the IIS features may need a restart to complete."
}

# -------------------------------------------------------------------------
Step '3. Copy the published payload'

# Deliberately NOT served from the OneDrive folder: sync churn under a live
# site, and an app pool identity that cannot read a per-user profile path.
if (-not (Test-Path $SourcePath)) { throw "Published output not found at $SourcePath. Run dotnet publish first." }
if (-not (Test-Path $PhysicalPath)) { [void](New-Item -ItemType Directory -Path $PhysicalPath -Force) }

Copy-Item -Path (Join-Path $SourcePath '*') -Destination $PhysicalPath -Recurse -Force
Write-Host "  copied to $PhysicalPath"

# -------------------------------------------------------------------------
Step '4. Application pool and site'

Import-Module WebAdministration

# No Managed Code: ASP.NET Core runs in the ANCM, not the CLR pipeline.
if (Test-Path "IIS:\AppPools\$SiteName") { Write-Host "  app pool exists  $SiteName" }
else {
    [void](New-WebAppPool -Name $SiteName)
    Set-ItemProperty "IIS:\AppPools\$SiteName" -Name managedRuntimeVersion -Value ''
    Write-Host "  app pool created $SiteName"
}

if (Test-Path "IIS:\Sites\$SiteName") { Write-Host "  site exists      $SiteName" }
else {
    [void](New-Website -Name $SiteName -PhysicalPath $PhysicalPath `
        -ApplicationPool $SiteName -Port $HttpPort)
    Write-Host "  site created     $SiteName on port $HttpPort"
}

# The app pool identity needs read access to the payload.
$poolSid = "IIS APPPOOL\$SiteName"
icacls $PhysicalPath /grant "${poolSid}:(OI)(CI)(RX)" /T /Q | Out-Null

# -------------------------------------------------------------------------
Step '5. HTTPS binding (self-signed, local only)'

# Program.cs calls UseHttpsRedirection. Without an HTTPS binding it cannot
# determine a port, logs a warning and serves plain HTTP instead.
$cert = Get-ChildItem Cert:\LocalMachine\My |
    Where-Object { $_.Subject -eq 'CN=localhost' -and $_.NotAfter -gt (Get-Date) } |
    Select-Object -First 1

if (-not $cert) {
    $cert = New-SelfSignedCertificate -DnsName 'localhost' -CertStoreLocation 'Cert:\LocalMachine\My'
    # Trust it, so the browser does not interrupt with a warning.
    $store = New-Object System.Security.Cryptography.X509Certificates.X509Store('Root','LocalMachine')
    $store.Open('ReadWrite'); $store.Add($cert); $store.Close()
}

if (-not (Get-WebBinding -Name $SiteName -Protocol https -ErrorAction SilentlyContinue)) {
    New-WebBinding -Name $SiteName -Protocol https -Port $HttpsPort
    $binding = Get-WebBinding -Name $SiteName -Protocol https
    $binding.AddSslCertificate($cert.GetCertHashString(), 'My')
    Write-Host "  https bound on port $HttpsPort"
}

# -------------------------------------------------------------------------
Step '6. Windows Authentication on, Anonymous off'

# This is the security control, not a convenience. Program.cs sets a fallback
# authorization policy requiring an authenticated user; leaving Anonymous on
# would publish crawl state to anyone who can reach the port.
Set-WebConfigurationProperty -Filter '/system.webServer/security/authentication/windowsAuthentication' `
    -Name enabled -Value $true -PSPath 'IIS:\' -Location $SiteName
Set-WebConfigurationProperty -Filter '/system.webServer/security/authentication/anonymousAuthentication' `
    -Name enabled -Value $false -PSPath 'IIS:\' -Location $SiteName

Restart-WebAppPool -Name $SiteName

Write-Host "`nDone. https://localhost:$HttpsPort/  (http://localhost:$HttpPort/)" -ForegroundColor Green
Write-Host "App pool identity: IIS APPPOOL\$SiteName" -ForegroundColor Yellow
Write-Host "That identity still needs a SQL login in ConnectorState - see the next step." -ForegroundColor Yellow
