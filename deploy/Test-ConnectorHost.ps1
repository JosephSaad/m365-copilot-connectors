<#
.SYNOPSIS
    Diagnoses the on-premises half of the pipeline: configuration, the service,
    the certificate, the port, the agent hop and SQL reachability.

.DESCRIPTION
    Run this ON THE AGENT HOST. It is read-only — it starts nothing, restarts
    nothing and writes nothing. Every check prints PASS, FAIL or a note saying
    what the result means, so the output can be pasted into a ticket as it is.

    It covers stages 0 to 3 of docs/TROUBLESHOOTING.md:

      0  configuration is valid and free of placeholders
      1  SQL Server is reachable from this host
      2  the connector process is up and listening
      3  the agent can reach it: port map, TLS, restart ordering

    Two checks here exist because the failure they catch is silent:

    1. PORT MAP EDITED AFTER THE AGENT STARTED. GcaHostService reads
       CustomConnectorPortMap.json once, at startup. Edit the map and forget the
       restart and the agent keeps using the old mapping — the connector is
       listening, the configuration on disk is correct, and calls still never
       arrive. This script compares the file's LastWriteTime against the agent
       process start time and fails when the map is newer.

    2. THE LOG HAS GONE QUIET. A connector with nothing recent in its log is not
       healthy and idle; HealthCheck is polled continuously, so silence means
       the agent has stopped calling. Staleness is reported as a first-class
       result rather than left for you to notice.

.PARAMETER InstallPath
    Where the connector is installed. Default C:\Connectors\SqlTickets.

.PARAMETER AgentPath
    The Graph connector agent directory, which holds CustomConnectorPortMap.json.

.PARAMETER ServiceName
    The connector's Windows service name. Default SqlTicketsConnector.

.PARAMETER SkipSql
    Do not attempt the TCP probe to SQL Server. Use when the probe is slow
    because the host is firewalled off and you already know it.

.EXAMPLE
    .\Test-ConnectorHost.ps1

.EXAMPLE
    .\Test-ConnectorHost.ps1 -InstallPath D:\Connectors\SqlTickets -SkipSql
#>

[CmdletBinding()]
param(
    [string]$InstallPath = 'C:\Connectors\SqlTickets',
    [string]$AgentPath = 'C:\Program Files\Graph connector agent',
    [string]$ServiceName = 'SqlTicketsConnector',
    [string]$AgentServiceName = 'GcaHostService',
    [switch]$SkipSql
)

$ErrorActionPreference = 'Stop'
$script:failures = 0

function Step([string]$name) { Write-Host "`n== $name ==" -ForegroundColor Cyan }
function Pass([string]$msg) { Write-Host "  PASS  $msg" -ForegroundColor Green }
function Fail([string]$msg) { Write-Host "  FAIL  $msg" -ForegroundColor Red; $script:failures++ }
function Warn([string]$msg) { Write-Host "  WARN  $msg" -ForegroundColor Yellow }
function Note([string]$msg) { Write-Host "  note  $msg" -ForegroundColor DarkGray }

$elevated = ([Security.Principal.WindowsPrincipal] `
    [Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole(
        [Security.Principal.WindowsBuiltInRole]::Administrator)

Write-Host "SqlTicketsConnector host diagnostics — $(Get-Date -Format 'yyyy-MM-dd HH:mm:ss')"
Write-Host "Running as $([Security.Principal.WindowsIdentity]::GetCurrent().Name)$(if (-not $elevated) { ' (not elevated)' })"
if (-not $elevated) {
    Note 'Not elevated: the private key readability check is skipped and some service properties may be hidden.'
}

# ---------------------------------------------------------------------------
# Stage 0 — configuration
# ---------------------------------------------------------------------------

Step '0. Configuration'

$configPath = Join-Path $InstallPath 'appsettings.json'
$config = $null

if (-not (Test-Path $configPath)) {
    Fail "appsettings.json not found at $configPath. Wrong -InstallPath, or the package was never deployed."
}
else {
    try {
        $config = Get-Content $configPath -Raw | ConvertFrom-Json
        Pass "read $configPath"
    }
    catch {
        Fail "appsettings.json is not valid JSON: $($_.Exception.Message)"
    }
}

if ($config) {
    # Placeholders are the single most common deployment miss. The connector
    # already refuses to start on them (exit code 2); saying so here turns a
    # cryptic service-start failure into one line.
    $placeholders = @()
    $raw = Get-Content $configPath -Raw
    foreach ($m in [regex]::Matches($raw, '"([^"]*REPLACE-WITH[^"]*)"')) {
        $placeholders += $m.Groups[1].Value
    }
    if ($placeholders.Count -gt 0) {
        Fail "$($placeholders.Count) unreplaced placeholder(s): $($placeholders -join ', ')"
        Note 'The service will not start. Exit code 2, with every invalid field listed at once in the log.'
    }
    else {
        Pass 'no REPLACE-WITH placeholders remain'
    }

    $acl = @($config.Acl.GrantGroupObjectIds)
    if ($acl.Count -eq 0 -or [string]::IsNullOrWhiteSpace($acl[0])) {
        Fail 'Acl:GrantGroupObjectIds is empty. The connector refuses to start without it — there is no "everyone" fallback.'
    }
    else {
        Pass "ACL grants $($acl.Count) group(s)"
        Note 'Every crawled item carries this ACL. If items exist but nobody can find them, this is the first thing to check.'
    }

    Note "Environment: $($config.Environment); Auth mode: $($config.Auth.Mode); SQL auth: $($config.DataSource.SqlAuthMode)"

    # A secret-shaped value in configuration is a review failure, not a warning.
    if ($config.Auth.Mode -eq 'ClientSecret') {
        $target = $config.Auth.ClientSecretCredentialTarget
        if ([string]::IsNullOrWhiteSpace($target)) {
            Fail 'Auth:Mode is ClientSecret but Auth:ClientSecretCredentialTarget is empty.'
        }
        else {
            Pass "client secret target: $target"
            Note 'Credential Manager is PER ACCOUNT. Checked below against the account THIS session runs as, which is'
            Note 'almost certainly not the service account — a miss here is inconclusive, a hit under the wrong account'
            Note 'is not proof either. See docs/RUNBOOK.md §2a.'
        }
    }
}

# ---------------------------------------------------------------------------
# Stage 2 — the connector service and its port
#   (stage 1, SQL, comes after: it is the slowest check and the least likely
#    to be the cause when the service itself is down)
# ---------------------------------------------------------------------------

Step '2. Connector service'

$svc = Get-Service -Name $ServiceName -ErrorAction SilentlyContinue
$svcPid = 0
if (-not $svc) {
    Fail "service '$ServiceName' is not installed. Run deploy\Install-Connector.ps1."
}
else {
    $cim = Get-CimInstance Win32_Service -Filter "Name='$ServiceName'" -ErrorAction SilentlyContinue
    if ($svc.Status -eq 'Running') {
        Pass "running as $($cim.StartName)"
        $svcPid = $cim.ProcessId
    }
    else {
        Fail "service is $($svc.Status), running as $($cim.StartName)"
        Note 'Exit code 2 means configuration; 3 means the server failed to start (certificate or port). The log names which.'
    }

    if ($config -and $config.DataSource.SqlAuthMode -eq 'WindowsIntegrated') {
        Note "SQL sees this connector as $($cim.StartName). The GRANT in sql\01-least-privilege.sql must name that exact account."
    }
}

$port = 30303
if ($config -and $config.Connector.Port) { $port = [int]$config.Connector.Port }

$listener = Get-NetTCPConnection -LocalPort $port -State Listen -ErrorAction SilentlyContinue
if (-not $listener) {
    Fail "nothing is listening on port $port"
    Note 'If the service is running but not listening, the server failed to bind — check the log for a Fatal at startup.'
}
else {
    $owner = @($listener)[0].OwningProcess
    if ($svcPid -gt 0 -and $owner -ne $svcPid) {
        $other = (Get-Process -Id $owner -ErrorAction SilentlyContinue).ProcessName
        Fail "port $port is held by PID $owner ($other), not by the connector (PID $svcPid)"
        Note 'Another process took the port first. Each connector on a host needs its own port.'
    }
    else {
        Pass "listening on $port (PID $owner)"
    }
}

# ---------------------------------------------------------------------------
# Stage 3 — the agent hop
# ---------------------------------------------------------------------------

Step '3. Agent hop (port map, restart ordering, TLS)'

$agentSvc = Get-Service -Name $AgentServiceName -ErrorAction SilentlyContinue
if (-not $agentSvc) {
    Fail "agent service '$AgentServiceName' is not installed. This host is not a Graph connector agent host."
}
elseif ($agentSvc.Status -ne 'Running') {
    Fail "agent service is $($agentSvc.Status). Nothing will call the connector until it runs."
}
else {
    Pass 'agent service is running'
}

$mapPath = Join-Path $AgentPath 'CustomConnectorPortMap.json'
if (-not (Test-Path $mapPath)) {
    Fail "port map not found at $mapPath. Wrong -AgentPath, or the agent is installed elsewhere."
}
else {
    try {
        $map = Get-Content $mapPath -Raw | ConvertFrom-Json
        $entries = @($map.PSObject.Properties)
        $connectorId = if ($config) { $config.Connector.Id } else { '' }
        $entry = $entries | Where-Object { $_.Name -eq $connectorId }

        if (-not $entry) {
            Fail "no entry for connector ID $connectorId in $mapPath"
            Note "The map has $($entries.Count) entry/entries: $(($entries | ForEach-Object { $_.Name }) -join ', ')"
        }
        elseif ([int]$entry.Value -ne $port) {
            Fail "port map sends $connectorId to $($entry.Value), but the connector listens on $port"
        }
        else {
            Pass "$connectorId -> $port"
        }

        # Two connectors on one port is silently wrong: one of them just never
        # receives a call, and which one is not deterministic.
        $dupes = $entries | Group-Object { $_.Value } | Where-Object { $_.Count -gt 1 }
        foreach ($d in $dupes) {
            Fail "port $($d.Name) is mapped to $($d.Count) connectors. Each connector needs its own port."
        }

        # The restart-ordering trap. The agent caches this file at startup.
        if ($agentSvc -and $agentSvc.Status -eq 'Running') {
            $agentCim = Get-CimInstance Win32_Service -Filter "Name='$AgentServiceName'" -ErrorAction SilentlyContinue
            $agentProc = if ($agentCim -and $agentCim.ProcessId) {
                Get-Process -Id $agentCim.ProcessId -ErrorAction SilentlyContinue
            }
            if ($agentProc) {
                $mapWritten = (Get-Item $mapPath).LastWriteTime
                if ($mapWritten -gt $agentProc.StartTime) {
                    Fail "the port map was edited at $mapWritten but the agent has been running since $($agentProc.StartTime)"
                    Note "The agent is still using the mapping it read at startup. Restart-Service $AgentServiceName."
                }
                else {
                    Pass "agent started $($agentProc.StartTime), after the map was last written ($mapWritten)"
                }
            }
        }
    }
    catch {
        Fail "could not read the port map: $($_.Exception.Message)"
    }
}

if ($config -and $config.Connector.UseTls) {
    Note 'Connector:UseTls is true. The agent must trust the issuer of the loopback TLS certificate and it must be'
    Note 'valid for "localhost". A handshake failure appears on the AGENT side, not in the connector log — that'
    Note 'asymmetry is the clue. Setting UseTls false briefly is the fastest way to confirm TLS is the fault.'
}

# ---------------------------------------------------------------------------
# Certificate
# ---------------------------------------------------------------------------

Step 'Certificate'

if ($config -and $config.Auth.Mode -eq 'Certificate') {
    $thumbs = @($config.Auth.CertificateThumbprints) | Where-Object { $_ }
    $location = if ($config.Auth.CertificateStoreLocation) { $config.Auth.CertificateStoreLocation } else { 'LocalMachine' }
    $warnDays = if ($config.Auth.ExpiryWarningDays) { [int]$config.Auth.ExpiryWarningDays } else { 30 }

    if ($thumbs.Count -eq 0) {
        Fail 'Auth:Mode is Certificate but no thumbprints are configured.'
    }

    foreach ($t in $thumbs) {
        $cert = Get-ChildItem "Cert:\$location\My\$t" -ErrorAction SilentlyContinue
        if (-not $cert) {
            Fail "certificate $t is not in $location\My"
            continue
        }

        $days = [int]($cert.NotAfter - (Get-Date)).TotalDays
        if ($days -lt 0) {
            Fail "$($t.Substring(0, 8))… ($($cert.Subject)) EXPIRED $([Math]::Abs($days)) day(s) ago"
        }
        elseif ($days -le $warnDays) {
            Warn "$($t.Substring(0, 8))… ($($cert.Subject)) expires in $days day(s) — rotation window is open, see docs/RUNBOOK.md §1"
        }
        else {
            Pass "$($t.Substring(0, 8))… ($($cert.Subject)) valid for $days more day(s)"
        }

        if (-not $cert.HasPrivateKey) {
            Fail "$($t.Substring(0, 8))… has no private key. It was imported as a public certificate only."
        }
        elseif ($elevated) {
            # The most common post-rebuild failure: the key is present, the
            # service account simply cannot read it.
            try {
                # GetRSAPrivateKey rather than the legacy .PrivateKey property:
                # on Windows PowerShell 5.1 the latter throws 'Invalid provider
                # type specified' for CNG/KSP-stored keys, failing a perfectly
                # healthy certificate.
                $null = [System.Security.Cryptography.X509Certificates.RSACertificateExtensions]::GetRSAPrivateKey($cert)
                Pass "$($t.Substring(0, 8))… private key is readable by this session"
                Note 'Readable by YOU is not readable by the service account. If the log says PrivateKeyUnreadable,'
                Note 'grant that account Read on the key: certlm.msc -> the certificate -> All Tasks -> Manage Private Keys.'
            }
            catch {
                Fail "$($t.Substring(0, 8))… private key present but unusable: $($_.Exception.Message)"
            }
        }
    }
}
elseif ($config -and $config.Auth.Mode -eq 'ClientSecret') {
    $target = $config.Auth.ClientSecretCredentialTarget
    if ($target) {
        # CredRead, not cmdkey text: cmdkey output is localized, so the
        # English markers would misreport on non-English Windows.
        if (-not ('Probe.HostCredProbe' -as [type])) {
            Add-Type -Namespace Probe -Name HostCredProbe -MemberDefinition @'
[System.Runtime.InteropServices.DllImport("advapi32.dll", EntryPoint = "CredReadW", CharSet = System.Runtime.InteropServices.CharSet.Unicode, SetLastError = true)]
public static extern bool CredRead(string target, uint type, uint flags, out System.IntPtr credential);
[System.Runtime.InteropServices.DllImport("advapi32.dll")]
public static extern void CredFree(System.IntPtr buffer);
'@
        }

        $credHandle = [System.IntPtr]::Zero
        $credExists = [Probe.HostCredProbe]::CredRead($target, 1, 0, [ref]$credHandle)
        if ($credHandle -ne [System.IntPtr]::Zero) { [Probe.HostCredProbe]::CredFree($credHandle) }

        if (-not $credExists) {
            Warn "no Credential Manager entry '$target' under $([Security.Principal.WindowsIdentity]::GetCurrent().Name)"
            Note 'Inconclusive unless this session IS the service account: entries are per account. Store it as the'
            Note 'service account (docs/RUNBOOK.md §2a) — the startup log tells you the truth either way.'
        }
        else {
            Pass "an entry named '$target' is readable by this account"
            Note 'Only proves it exists for THIS account. The service reads it as its own account.'
        }
    }
    Note 'A client secret has no expiry warning of any kind. Track its Entra expiry on a calendar.'
}

# ---------------------------------------------------------------------------
# Stage 1 — SQL reachability
# ---------------------------------------------------------------------------

Step '1. SQL Server reachability'

if ($SkipSql) {
    Note 'skipped by -SkipSql'
}
elseif (-not $config) {
    Note 'skipped: no configuration to read the server name from'
}
else {
    $server = $config.DataSource.Server
    $sqlPort = 1433
    $hostName = $server

    if ($server -match '^(.+),(\d+)$') {
        $hostName = $Matches[1]
        $sqlPort = [int]$Matches[2]
    }
    elseif ($server -match '\\') {
        $hostName = $server.Split('\')[0]
        Note "named instance '$server': the port is assigned dynamically and brokered by SQL Browser on UDP 1434."
        Note 'A failure below may only mean the instance is not on 1433, not that the host is unreachable.'
    }

    $probe = Test-NetConnection -ComputerName $hostName -Port $sqlPort -WarningAction SilentlyContinue
    if ($probe.TcpTestSucceeded) {
        Pass "TCP $hostName`:$sqlPort open"
        Note 'Reachable is not usable. Run deploy\Test-SqlSource.ps1 to prove the grant, the columns and the watermark.'
    }
    else {
        Fail "TCP $hostName`:$sqlPort refused or filtered"
        Note 'This is what the connection wizard reports as "the data source did not respond within 20 seconds".'
    }
}

# ---------------------------------------------------------------------------
# The log
# ---------------------------------------------------------------------------

Step 'Log'

$logDir = if ($config -and $config.Logging.Directory) { $config.Logging.Directory } else { Join-Path $InstallPath 'Logs' }
$log = Get-ChildItem (Join-Path $logDir '*.log') -ErrorAction SilentlyContinue |
    Sort-Object LastWriteTime -Descending | Select-Object -First 1

if (-not $log) {
    Fail "no log file under $logDir"
    Note 'The service has never started, or Logging:Directory is not writable by the service account.'
}
else {
    $ageMinutes = [int]((Get-Date) - $log.LastWriteTime).TotalMinutes

    # HealthCheck is polled continuously, so a quiet log means calls stopped
    # arriving — not that the connector is idle. But HealthCheck logs at Verbose
    # only, so at the default Information level a healthy log is quiet between
    # crawls. Both facts are needed to read this number.
    $level = if ($config) { $config.Logging.MinimumLevel } else { 'Information' }
    if ($ageMinutes -gt 1440) {
        Fail "last log line is $ageMinutes minute(s) old ($($log.LastWriteTime))"
        Note 'Older than a day means no crawl has run. Check the crawl schedule in the admin centre.'
    }
    elseif ($ageMinutes -gt 60) {
        Warn "last log line is $ageMinutes minute(s) old"
        Note "At MinimumLevel=$level a healthy connector is quiet between crawls; incremental crawls default to 15 minutes."
    }
    else {
        Pass "last written $ageMinutes minute(s) ago"
    }

    $tail = Get-Content $log.FullName -Tail 400
    $fatal = @($tail | Select-String -Pattern '\[FTL\]' -SimpleMatch:$false)
    $errors = @($tail | Select-String -Pattern '\[ERR\]')
    $lastSummary = @($tail | Select-String -Pattern 'summary: items=') | Select-Object -Last 1

    if ($fatal.Count -gt 0) {
        Fail "$($fatal.Count) Fatal line(s) in the last 400: $($fatal[-1].Line.Trim())"
    }
    if ($errors.Count -gt 0) {
        Warn "$($errors.Count) Error line(s) in the last 400; most recent:"
        Note $errors[-1].Line.Trim()
    }
    if ($lastSummary) {
        Pass "last crawl summary: $($lastSummary.Line.Trim())"
    }
    else {
        Note 'No crawl summary in the last 400 lines. Run deploy\Get-CrawlHistory.ps1 for the full picture.'
    }
}

# ---------------------------------------------------------------------------

Write-Host ''
Write-Host 'Stages 4 to 7 (schema, ingestion, search, Copilot) run from a workstation'
Write-Host 'with Graph access: deploy\Verify-GraphConnection.ps1.'
Write-Host ''

if ($script:failures -eq 0) {
    Write-Host 'All host checks passed.' -ForegroundColor Green
}
else {
    Write-Host "$($script:failures) check(s) failed. See docs/TROUBLESHOOTING.md for the stage each one belongs to." -ForegroundColor Red
    exit 1
}
