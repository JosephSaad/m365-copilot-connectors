<#
.SYNOPSIS
    Deploys the SQL Tickets connector on a server that already runs the
    Microsoft Graph connector agent.

.DESCRIPTION
    Copies published binaries into place, creates the Windows event log source,
    verifies that the service account can read the authentication certificate's
    private key, merges the connector ID and port into the agent's
    CustomConnectorPortMap.json without discarding existing entries, registers a
    Windows service and restarts GcaHostService.

    The certificate check runs before the service is started, because a
    certificate the service account cannot read produces an authentication
    failure hours later with no obvious cause.

    Run from an elevated PowerShell session on the agent machine.

.EXAMPLE
    .\Install-Connector.ps1 -SourcePath .\publish -ServiceAccount 'CONTOSO\svc_gca_reader'

.EXAMPLE
    .\Install-Connector.ps1 -SourcePath .\publish -SkipCertificateCheck
#>

[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$SourcePath,

    [string]$InstallPath = 'C:\Connectors\SqlTickets',

    [string]$AgentPath = 'C:\Program Files\Graph connector agent',

    [string]$ConnectorId = '9e5e2b95-e7ab-4266-98c7-4f7868d377bf',

    [int]$Port = 30303,

    [string]$ServiceName = 'SqlTicketsConnector',

    # A domain identity is required for SqlAuthMode=WindowsIntegrated, because the
    # SQL grant in sql/01-least-privilege.sql is made to that account. Prefer a
    # group managed service account ('CONTOSO\svc_gca_reader$'): it has no password
    # for anyone to store, type or leak. NETWORK SERVICE authenticates to SQL as
    # the machine account, which is acceptable only if the grant names the machine.
    [string]$ServiceAccount = 'NT AUTHORITY\NETWORK SERVICE',

    [string]$EventLogSource = 'SqlTicketsConnector',

    [string]$EventLogName = 'Application',

    # Only for a first install onto a host where the certificate is not yet
    # present. Never use it to get past a failing check on a production host.
    [switch]$SkipCertificateCheck
)

$ErrorActionPreference = 'Stop'

function Assert-Elevated {
    $identity = [Security.Principal.WindowsIdentity]::GetCurrent()
    $principal = New-Object Security.Principal.WindowsPrincipal($identity)
    if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
        throw 'This script must be run from an elevated PowerShell session.'
    }
}

function Get-ConnectorConfig {
    param([string]$Path)

    $configPath = Join-Path $Path 'appsettings.json'
    if (-not (Test-Path $configPath)) {
        throw "appsettings.json not found at $configPath. Publish the connector project first."
    }

    return Get-Content $configPath -Raw | ConvertFrom-Json
}

function Resolve-ServiceAccountSid {
    param([string]$Account)

    try {
        return (New-Object System.Security.Principal.NTAccount($Account)).Translate(
            [System.Security.Principal.SecurityIdentifier])
    }
    catch {
        throw "Could not resolve the service account '$Account'. Check the domain and spelling."
    }
}

function Test-CertificateReadableBy {
    <#
        Confirms the certificate is present in LocalMachine\My, has a private key,
        and that the service account has Read on the key container. This is the
        single most common cause of a connector that installs cleanly and then
        fails every crawl.
    #>
    param(
        [string[]]$Thumbprints,
        [string]$Subject,
        [string]$Account,
        [int]$WarningDays
    )

    $store = New-Object System.Security.Cryptography.X509Certificates.X509Store('My', 'LocalMachine')
    $store.Open('ReadOnly')

    try {
        $candidates = @()

        foreach ($thumbprint in $Thumbprints) {
            $normalized = ($thumbprint -replace '[^0-9A-Fa-f]', '').ToUpperInvariant()
            if ($normalized.Length -ne 40) {
                Write-Warning "Auth:CertificateThumbprints contains '$thumbprint', which is not a 40 character thumbprint. Replace the placeholder before starting the service."
                continue
            }

            $match = $store.Certificates | Where-Object { $_.Thumbprint -eq $normalized }
            if ($match) { $candidates += $match } else { Write-Warning "Certificate $normalized is not present in LocalMachine\My." }
        }

        if (-not $candidates -and $Subject) {
            $candidates = $store.Certificates | Where-Object { $_.Subject -like "*$Subject*" }
        }

        if (-not $candidates) {
            throw "No configured certificate was found in LocalMachine\My. Import the certificate with its private key, then re-run this script."
        }

        $usable = $false

        foreach ($certificate in $candidates) {
            Write-Host ("  {0}  {1}  expires {2:yyyy-MM-dd}" -f $certificate.Thumbprint, $certificate.Subject, $certificate.NotAfter)

            if ($certificate.NotAfter -lt (Get-Date)) {
                Write-Warning "  expired on $($certificate.NotAfter.ToString('u')). It cannot be used."
                continue
            }

            if ($certificate.NotAfter -lt (Get-Date).AddDays($WarningDays)) {
                Write-Warning "  expires in less than $WarningDays days. Start the rotation in docs/RUNBOOK.md."
            }

            if (-not $certificate.HasPrivateKey) {
                Write-Warning '  has no private key. Re-import the certificate including its key.'
                continue
            }

            # Read the key container ACL and look for the service account.
            $key = [System.Security.Cryptography.X509Certificates.RSACertificateExtensions]::GetRSAPrivateKey($certificate)
            if ($null -eq $key) {
                Write-Warning '  private key is present but could not be opened by this process.'
                continue
            }

            $keyPath = $null
            if ($key -is [System.Security.Cryptography.RSACng]) {
                $keyPath = Join-Path $env:ProgramData "Microsoft\Crypto\Keys\$($key.Key.UniqueName)"
            }
            elseif ($key.CspKeyContainerInfo) {
                $keyPath = Join-Path $env:ProgramData "Microsoft\Crypto\RSA\MachineKeys\$($key.CspKeyContainerInfo.UniqueKeyContainerName)"
            }

            if ($keyPath -and (Test-Path $keyPath)) {
                $sid = Resolve-ServiceAccountSid -Account $Account
                $acl = Get-Acl $keyPath
                $hasRead = $acl.Access | Where-Object {
                    $_.IdentityReference.Translate([System.Security.Principal.SecurityIdentifier]) -eq $sid -and
                    $_.FileSystemRights -band [System.Security.AccessControl.FileSystemRights]::Read -and
                    $_.AccessControlType -eq 'Allow'
                }

                if (-not $hasRead) {
                    Write-Warning "  $Account has no Read permission on the private key. Granting it now."
                    $rule = New-Object System.Security.AccessControl.FileSystemAccessRule($sid, 'Read', 'Allow')
                    $acl.AddAccessRule($rule)
                    Set-Acl -Path $keyPath -AclObject $acl
                    Write-Host "  granted Read on the key container to $Account."
                }
                else {
                    Write-Host "  $Account can read the private key."
                }
            }
            else {
                Write-Warning '  could not locate the key container on disk; verify key permissions manually with certlm.msc.'
            }

            $usable = $true
        }

        if (-not $usable) {
            throw 'No usable certificate: every candidate was expired, missing its key, or unreadable. The service would fail on its first token request.'
        }
    }
    finally {
        $store.Close()
    }
}

Assert-Elevated

if (-not (Test-Path $SourcePath)) {
    throw "Source path not found: $SourcePath"
}

if (-not (Test-Path $AgentPath)) {
    throw "Graph connector agent not found at: $AgentPath. Install it from https://aka.ms/gca first."
}

# From Step 1 onward the script mutates the host: the existing service may be
# stopped and its binaries overwritten. A throw after that must not exit with a
# bare error and no statement of state - the operator needs to know the service
# was left stopped, and whether the old binaries are still in place.
$script:ServiceWasStopped = $false
$script:BinariesReplaced = $false
$script:PortMapWritten = $false

trap {
    Write-Host ''
    Write-Host 'INSTALL FAILED - host state:' -ForegroundColor Red

    # Each mutation reports independently: a fresh install has no service to
    # stop but still overwrites binaries, so gating everything on the service
    # flag would print 'nothing was changed' over a half-written install.
    $mutated = $false

    if ($script:ServiceWasStopped) {
        $mutated = $true
        if ($script:BinariesReplaced) {
            Write-Host ("  Service '$ServiceName' is STOPPED and its binaries were replaced with the new " +
                'build. Fix the error above and re-run this script; do not start the service by hand until ' +
                'it completes.') -ForegroundColor Yellow
        }
        else {
            Write-Host ("  Service '$ServiceName' is STOPPED; its binaries were NOT yet replaced. " +
                "'Start-Service $ServiceName' restores the previous deployment.") -ForegroundColor Yellow
        }
    }
    elseif ($script:BinariesReplaced) {
        $mutated = $true
        Write-Host ("  Binaries were copied into '$InstallPath'. No service was stopped. " +
            'Fix the error above and re-run this script to finish the install.') -ForegroundColor Yellow
    }

    if ($script:PortMapWritten) {
        $mutated = $true
        Write-Host '  The agent port map was updated. Re-running this script is safe; the merge is idempotent.' -ForegroundColor Yellow
    }

    if (-not $mutated) {
        Write-Host '  Nothing was changed on this host.' -ForegroundColor Yellow
    }

    Write-Host "  Error: $_" -ForegroundColor Red
    exit 1
}

Write-Host '== Step 1: Stop existing service if present ==' -ForegroundColor Cyan
$existing = Get-Service -Name $ServiceName -ErrorAction SilentlyContinue
if ($existing) {
    if ($existing.Status -ne 'Stopped') {
        Stop-Service -Name $ServiceName -Force
        $script:ServiceWasStopped = $true
        Write-Host "Stopped $ServiceName."
    }
}

Write-Host '== Step 2: Copy binaries ==' -ForegroundColor Cyan
if (-not (Test-Path $InstallPath)) {
    New-Item -ItemType Directory -Path $InstallPath -Force | Out-Null
}
Copy-Item -Path (Join-Path $SourcePath '*') -Destination $InstallPath -Recurse -Force
$script:BinariesReplaced = $true

# Files downloaded from SharePoint carry the mark of the web; .NET refuses to
# load blocked assemblies.
Get-ChildItem -Path $InstallPath -Recurse -File | Unblock-File
Write-Host "Copied and unblocked files in $InstallPath."

$logPath = Join-Path $InstallPath 'Logs'
if (-not (Test-Path $logPath)) {
    New-Item -ItemType Directory -Path $logPath -Force | Out-Null
}

$config = Get-ConnectorConfig -Path $InstallPath
if ($config.Logging.Directory) { $logPath = $config.Logging.Directory }
if ($config.Connector.Port) { $Port = [int]$config.Connector.Port }
if ($config.Connector.Id) { $ConnectorId = $config.Connector.Id }
if ($config.Logging.EventLogSource) { $EventLogSource = $config.Logging.EventLogSource }

Write-Host '== Step 3: Create the event log source ==' -ForegroundColor Cyan
# Created here, never at runtime: registering a source needs administrative
# rights that the service account must not have.
if ([System.Diagnostics.EventLog]::SourceExists($EventLogSource)) {
    Write-Host "Event log source '$EventLogSource' already exists."
}
elseif ($config.Logging.EventLogEnabled -eq $false) {
    Write-Host 'Logging:EventLogEnabled is false; skipping event log source creation.'
}
else {
    New-EventLog -LogName $EventLogName -Source $EventLogSource
    Write-Host "Created event log source '$EventLogSource' in the $EventLogName log."
    Write-EventLog -LogName $EventLogName -Source $EventLogSource -EventId 1000 -EntryType Information `
        -Message "SqlTicketsConnector event log source created by Install-Connector.ps1."
}

Write-Host '== Step 4: Verify the certificate is present and readable ==' -ForegroundColor Cyan
if ($SkipCertificateCheck) {
    Write-Warning 'Certificate verification skipped by -SkipCertificateCheck. Do not do this on a production host.'
}
elseif ($config.Auth.Mode -eq 'ClientSecret') {
    $target = $config.Auth.ClientSecretCredentialTarget
    Write-Host "Auth:Mode is 'ClientSecret'; the secret comes from Windows Credential Manager, not a certificate."

    if ([string]::IsNullOrWhiteSpace($target)) {
        Write-Warning 'Auth:ClientSecretCredentialTarget is empty. The service will refuse to start until it names a Credential Manager entry.'
    }
    else {
        # Credential Manager is per account, and this script runs as an
        # administrator rather than as the service account. Finding the entry
        # here is therefore a warning sign, not a pass: it means the credential
        # was very likely stored under the wrong profile.
        # CredRead rather than parsing cmdkey output: cmdkey's text is
        # localized, so matching English markers like 'NONE' would misreport on
        # every non-English Windows. The API answers the only question asked -
        # does the entry exist for THIS account - directly.
        if (-not ('Probe.InstallCredProbe' -as [type])) {
            Add-Type -Namespace Probe -Name InstallCredProbe -MemberDefinition @'
[System.Runtime.InteropServices.DllImport("advapi32.dll", EntryPoint = "CredReadW", CharSet = System.Runtime.InteropServices.CharSet.Unicode, SetLastError = true)]
public static extern bool CredRead(string target, uint type, uint flags, out System.IntPtr credential);
[System.Runtime.InteropServices.DllImport("advapi32.dll")]
public static extern void CredFree(System.IntPtr buffer);
'@
        }

        $handle = [System.IntPtr]::Zero
        $entryExists = [Probe.InstallCredProbe]::CredRead($target, 1, 0, [ref]$handle)
        if ($handle -ne [System.IntPtr]::Zero) { [Probe.InstallCredProbe]::CredFree($handle) }

        if ($entryExists) {
            Write-Warning ("Credential Manager entry '$target' exists for $env:USERNAME, which is NOT the account the " +
                'service runs as. Credential Manager is per account, so the service will still fail to read it. ' +
                "Store it as $ServiceAccount instead, and remove this copy with: cmdkey /delete:$target")
        }
        else {
            Write-Host "  No entry named '$target' under $env:USERNAME, which is correct: it belongs to $ServiceAccount."
        }

        Write-Host "  Verify it as the service account before starting the service. docs/RUNBOOK.md section 2a has the psexec and scheduled task routes."
    }
}
elseif ($config.Auth.Mode -ne 'Certificate') {
    Write-Host "Auth:Mode is '$($config.Auth.Mode)'; no certificate check required."
}
else {
    Test-CertificateReadableBy `
        -Thumbprints @($config.Auth.CertificateThumbprints) `
        -Subject $config.Auth.CertificateSubject `
        -Account $ServiceAccount `
        -WarningDays ([int]$config.Auth.ExpiryWarningDays)
}

Write-Host '== Step 5: Merge port map entry ==' -ForegroundColor Cyan
$portMapPath = Join-Path $AgentPath 'CustomConnectorPortMap.json'
$portMap = @{}

if (Test-Path $portMapPath) {
    Copy-Item $portMapPath "$portMapPath.bak" -Force
    $raw = Get-Content $portMapPath -Raw
    if (-not [string]::IsNullOrWhiteSpace($raw)) {
        # Existing entries are preserved. Each connector needs its own port.
        (ConvertFrom-Json $raw).PSObject.Properties | ForEach-Object {
            $portMap[$_.Name] = $_.Value
        }
    }
}

$portMap[$ConnectorId] = "$Port"
$portMap | ConvertTo-Json -Depth 3 | Set-Content -Path $portMapPath -Encoding UTF8
$script:PortMapWritten = $true
Write-Host "Mapped $ConnectorId to port $Port."

Write-Host '== Step 6: Register Windows service ==' -ForegroundColor Cyan
$exePath = Join-Path $InstallPath 'SqlTicketsConnector.exe'
if (-not (Test-Path $exePath)) {
    throw "Executable not found at $exePath. Did you publish the project?"
}

# sc.exe reports failure through its exit code and puts the actual error text on
# stdout. Piping to Out-Null without checking $LASTEXITCODE would discard both
# and print a success message over a failed create - the worst kind of installer.
function Invoke-Sc {
    param([string]$What, [string[]]$ScArgs)

    $output = & sc.exe @ScArgs 2>&1 | Out-String

    if ($LASTEXITCODE -ne 0) {
        throw "sc.exe $What failed with exit code ${LASTEXITCODE}: $($output.Trim())"
    }
}

if ($existing) {
    Invoke-Sc -What 'config' -ScArgs @('config', $ServiceName, 'binPath=', "`"$exePath`"", 'start=', 'auto', 'obj=', "$ServiceAccount")
    Write-Host "Reconfigured service $ServiceName."
}
else {
    Invoke-Sc -What 'create' -ScArgs @('create', $ServiceName, 'binPath=', "`"$exePath`"", 'start=', 'auto', 'obj=', "$ServiceAccount", 'DisplayName=', 'SQL Tickets Copilot Connector')
    Invoke-Sc -What 'description' -ScArgs @('description', $ServiceName, 'Custom Copilot connector indexing dbo.Tickets from SQL Server.')
    Write-Host "Created service $ServiceName."
}

# Restart the connector on failure rather than leaving crawls to time out.
Invoke-Sc -What 'failure' -ScArgs @('failure', $ServiceName, 'reset=', '86400', 'actions=', 'restart/60000/restart/60000/restart/60000')

Write-Host '== Step 7: Grant the service account access to the install folder ==' -ForegroundColor Cyan
$acl = Get-Acl $InstallPath
$rule = New-Object System.Security.AccessControl.FileSystemAccessRule(
    $ServiceAccount, 'ReadAndExecute', 'ContainerInherit,ObjectInherit', 'None', 'Allow')
$acl.SetAccessRule($rule)
Set-Acl -Path $InstallPath -AclObject $acl

# Write access is needed for the log directory only, not for the binaries.
if (-not (Test-Path $logPath)) { New-Item -ItemType Directory -Path $logPath -Force | Out-Null }
$logAcl = Get-Acl $logPath
$logRule = New-Object System.Security.AccessControl.FileSystemAccessRule(
    $ServiceAccount, 'Modify', 'ContainerInherit,ObjectInherit', 'None', 'Allow')
$logAcl.SetAccessRule($logRule)
Set-Acl -Path $logPath -AclObject $logAcl
Write-Host "Granted $ServiceAccount read and execute on $InstallPath, modify on $logPath."

Write-Host '== Step 8: Start services ==' -ForegroundColor Cyan
Start-Service -Name $ServiceName
Write-Host "Started $ServiceName."

# The agent caches the port map at startup, so this restart is mandatory.
Restart-Service -Name 'GcaHostService' -Force
Write-Host 'Restarted GcaHostService.'

Write-Host ''
Write-Host 'Deployment complete.' -ForegroundColor Green
Write-Host "Connector ID   : $ConnectorId"
Write-Host "Port           : $Port"
Write-Host "Service account: $ServiceAccount"
Write-Host "Logs           : $logPath\ConnectorLog.log"
Write-Host "Event log      : $EventLogName / $EventLogSource (Warning and above)"
Write-Host ''
Write-Host 'Next: confirm the listener, then publish the connection in the Microsoft 365 admin center.'
Write-Host "  Get-NetTCPConnection -LocalPort $Port -State Listen"
Write-Host "  Get-EventLog -LogName $EventLogName -Source $EventLogSource -Newest 20"
