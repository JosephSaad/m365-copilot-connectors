<#
.SYNOPSIS
    Proves the Cloudera cluster is reachable AS THE SERVICE IDENTITY before the
    first crawl: Kerberos, HttpFS, Ranger and, when asked, the Hive ODBC driver.

.DESCRIPTION
    Run this ON THE CONNECTOR HOST, as the account CdpGraphPush.exe will run as.
    Read-only throughout: it issues GETFILESTATUS, LISTSTATUS, GETACLSTATUS and
    one Ranger policy read, and it opens nothing, writes nothing and creates
    nothing. Every check prints PASS, FAIL or WARN and, on a failure, one
    sentence saying what to do about it, so the output pastes into a ticket.

    WHOSE IDENTITY IS BEING TESTED IS THE WHOLE POINT. The connector
    authenticates over HTTP Negotiate and SSPI as the account it runs as — a
    gMSA for preference, whose password Active Directory owns and this process
    never sees. There is no keytab, no password and no connection string here
    holding one. A probe run interactively by a human therefore tests the
    HUMAN's Kerberos ticket, their Ranger grants and their HDFS group
    memberships, all of which can pass while the service account fails. Check 0
    reports which of the two you are and says so plainly. Run it as the service
    account where you can — psexec, or a scheduled task under the gMSA; see
    docs/RUNBOOK.md §2a for both.

    Four things here are worth knowing before you read the output.

    1. A 401 OR A 403 IS EXIT CODE 3, NOT A BUG. The contract is 0 success,
       2 configuration invalid, 3 the credential rejected — by Entra or by the
       source — and 4 ingestion failed. HDFS and Ranger refusing this identity
       both leave the connector as exit 3, so a FAIL on check 2 or 6 tells you
       exactly what the service will report before it reports it.

    2. AN UNREACHABLE RANGER STOPS THE RUN. Ranger is what says which paths and
       tables may be indexed at all — a row filter or a column mask means the
       data must be queried live rather than copied into an index — and the
       connector refuses to index a source whose policies it cannot read. It
       never defaults to indexing. Check 6 failing is not a degraded crawl; it
       is no crawl.

    3. NAMED GROUP ACL ENTRIES ARE WHO ELSE CAN READ A FILE. The owning group
       grants read only when the group permission digit says so; every other
       reader arrives as a "group:NAME:r--" entry in GETACLSTATUS or as a Ranger
       path policy. Check 4 shows them because they are the part of an item's
       ACL that is invisible in an ls.

    4. WHAT THIS DOES NOT PROVE. It never issues OPEN, so on a WebHDFS
       deployment it does not prove this host can reach the DataNodes a read
       redirects to. HttpFS has no such hop, which is why the shipped
       configuration prefers it. Nor does it prove a group resolves to an Entra
       group: an unresolved cluster group is DROPPED, and an item left with no
       grant is skipped rather than published to everybody. That surfaces in the
       first `CdpGraphPush.exe --connector cdphdfsdocs --dry-run`.

.PARAMETER HdfsBaseUrl
    Settings:HdfsBaseUrl — the HttpFS or WebHDFS base, https and ending in
    /webhdfs/v1. The connector refuses anything else with exit code 2.

.PARAMETER HdfsRoots
    Settings:HdfsRoots — the absolute paths to crawl. There is no default here
    for the same reason there is none in the connector: crawling / is not a
    scope decision anybody made.

.PARAMETER RangerBaseUrl
    Settings:RangerBaseUrl — Ranger Admin, read over SPNEGO.

.PARAMETER RangerHdfsService
    Settings:RangerHdfsService — the CM service name for HDFS policies.

.PARAMETER HiveHost
    Settings:HiveHost. Supplying it turns on check 7, the ODBC checks. Omit it
    when this host only runs the HDFS connector.

.PARAMETER HivePort
    Settings:HivePort. 10001 is HiveServer2 over HTTP transport.

.PARAMETER HiveDriver
    Settings:HiveDriver — the installed ODBC driver name, matched against the
    registry exactly as the driver manager matches it.

.PARAMETER HiveTransport
    Settings:HiveTransport — http or sasl. Kerberos does not support the binary
    Thrift transport, which is why there is no third option.

.PARAMETER HiveHttpPath
    Settings:HiveHttpPath — used only by the http transport.

.PARAMETER HiveRealm
    Settings:HiveRealm — the realm the HiveServer2 principal belongs to. Omit
    where the driver can infer it.

.PARAMETER HiveServiceName
    Settings:HiveServiceName — hive, or impala for an Impala endpoint.

.EXAMPLE
    .\Test-CdpSource.ps1 -HdfsBaseUrl 'https://httpfs01.corp.example:14000/webhdfs/v1' `
        -HdfsRoots '/data/caseworks/contracts','/data/caseworks/policies' `
        -RangerBaseUrl 'https://ranger01.corp.example:6182'

.EXAMPLE
    .\Test-CdpSource.ps1 -HdfsBaseUrl 'https://httpfs01.corp.example:14000/webhdfs/v1' `
        -HdfsRoots '/data/caseworks/contracts' `
        -RangerBaseUrl 'https://ranger01.corp.example:6182' `
        -HiveHost 'hs2-01.corp.example' -HiveRealm 'CORP.EXAMPLE'
#>

[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$HdfsBaseUrl,

    [Parameter(Mandatory = $true)]
    [string[]]$HdfsRoots,

    [Parameter(Mandatory = $true)]
    [string]$RangerBaseUrl,

    [string]$RangerHdfsService = 'cm_hdfs',

    [string]$HiveHost,
    [int]$HivePort = 10001,
    [string]$HiveDriver = 'Cloudera ODBC Driver for Apache Hive',
    [ValidateSet('http', 'sasl')]
    [string]$HiveTransport = 'http',
    [string]$HiveHttpPath = 'cliservice',
    [string]$HiveRealm,
    [string]$HiveServiceName = 'hive'
)

$ErrorActionPreference = 'Stop'
$script:failures = 0
$script:timeoutSeconds = 30
$script:clusterDate = ''
$script:clusterDateReadAt = $null

function Step([string]$name) { Write-Host "`n== $name ==" -ForegroundColor Cyan }
function Pass([string]$msg) { Write-Host "  PASS  $msg" -ForegroundColor Green }
function Fail([string]$msg) { Write-Host "  FAIL  $msg" -ForegroundColor Red; $script:failures++ }
function Warn([string]$msg) { Write-Host "  WARN  $msg" -ForegroundColor Yellow }
function Note([string]$msg) { Write-Host "  note  $msg" -ForegroundColor DarkGray }

# Windows PowerShell 5.1 negotiates whatever SecurityProtocol says, and on an
# unpatched host that still means TLS 1.0 — which a hardened HttpFS or Ranger
# refuses at the handshake, long before Kerberos is reached. The connector runs
# on .NET and picks the protocol itself, so this only makes the probe agree
# with it rather than fail where it would succeed.
if ($PSVersionTable.PSEdition -eq 'Desktop') {
    [Net.ServicePointManager]::SecurityProtocol =
        [Net.SecurityProtocolType]::Tls12 -bor [Net.ServicePointManager]::SecurityProtocol
}

function Get-StatusCode($record) {
    # Windows PowerShell raises a WebException carrying an HttpWebResponse;
    # PowerShell 7 raises an HttpResponseException carrying an
    # HttpResponseMessage. Both expose StatusCode, and both enumerations are
    # the HTTP number underneath.
    $response = $record.Exception.Response
    if (-not $response) { return 0 }
    try { return [int]$response.StatusCode } catch { return 0 }
}

function Get-HeaderValue($response, [string]$name) {
    if (-not $response -or -not $response.Headers) { return '' }

    # Windows PowerShell hands back a Dictionary, which throws on a key it does
    # not hold, and PowerShell 7 hands back one whose values are arrays. A
    # missing Date header must cost this script a skipped check, not a run.
    try {
        $value = $response.Headers[$name]
    }
    catch {
        return ''
    }

    if ($null -eq $value) { return '' }
    if ($value -is [string]) { return $value }
    return @($value)[0]
}

function ConvertTo-HdfsUrlPath([string]$path) {
    # Mirrors WebHdfsClient.EncodePath: each segment escaped, the separators
    # left alone. A lake is full of names with spaces, ampersands and non-ASCII
    # letters in them, and each of those breaks a URL differently.
    return (($path -split '/') | ForEach-Object { [uri]::EscapeDataString($_) }) -join '/'
}

function Get-PermissionDigit([string]$permission, [int]$index) {
    if ([string]::IsNullOrWhiteSpace($permission)) { return $null }

    # A four-digit value carries the sticky, setuid and setgid bits first; the
    # triple this cares about is always the last three.
    $triple = if ($permission.Length -gt 3) { $permission.Substring($permission.Length - 3) } else { $permission }
    if ($index -ge $triple.Length -or -not [char]::IsDigit($triple[$index])) { return $null }
    return [int]::Parse([string]$triple[$index])
}

function Invoke-Negotiated([string]$url) {
    # -UseDefaultCredentials is the whole authentication story: it puts this
    # process's logon session behind an HTTP Negotiate exchange, which is
    # exactly what the connector's HttpClientHandler does. No credential is
    # constructed here because there is none to construct.
    $result = [pscustomobject]@{
        Url = $url; Ok = $false; Status = 0; Json = $null; Message = ''
    }

    try {
        $response = Invoke-WebRequest -Uri $url -UseDefaultCredentials -UseBasicParsing `
            -TimeoutSec $script:timeoutSeconds -ErrorAction Stop

        $script:clusterDate = Get-HeaderValue $response 'Date'
        $script:clusterDateReadAt = [DateTimeOffset]::UtcNow

        $result.Status = [int]$response.StatusCode
        if ($response.Content) { $result.Json = $response.Content | ConvertFrom-Json }
        $result.Ok = $true
    }
    catch {
        $result.Status = Get-StatusCode $_
        $result.Message = $_.Exception.Message
    }

    return $result
}

function Refused([string]$what, [int]$status) {
    Fail "$what refused this identity with $status."
    Note 'This is the connector''s exit code 3 — a credential rejected by the source, not an ingestion failure.'
    Note 'Either this account holds no valid Kerberos ticket for the cluster''s realm, or Ranger no longer grants'
    Note 'it read here. Both are cluster-side; there is no password to correct on this host.'
}

$HdfsBaseUrl = $HdfsBaseUrl.TrimEnd('/')
$RangerBaseUrl = $RangerBaseUrl.TrimEnd('/')

Write-Host "CDP source diagnostics — $(Get-Date -Format 'yyyy-MM-dd HH:mm:ss')"
Write-Host "HDFS $HdfsBaseUrl / Ranger $RangerBaseUrl / service $RangerHdfsService"

# ---------------------------------------------------------------------------

Step '0. Identity, and whether it is the one that matters'

$identity = [System.Security.Principal.WindowsIdentity]::GetCurrent()
Pass "running as $($identity.Name) (authentication: $($identity.AuthenticationType))"

# A gMSA and a computer account both present as DOMAIN\name$. A name without
# the trailing dollar is a person, and a person's Kerberos ticket, group
# memberships and Ranger grants are not the service account's.
if ($identity.Name -like '*$') {
    Pass 'this looks like a machine or group managed service account, which is what the connector runs as'
}
else {
    Warn 'this is an interactive user account, not the service account the connector runs as.'
    Note 'CdpGraphPush.exe authenticates as its service identity — a gMSA for preference — over Negotiate and'
    Note 'SSPI. Everything below therefore tests YOUR ticket, YOUR cluster group memberships and YOUR Ranger'
    Note 'grants. A pass here is encouraging and is not evidence about the service account. Re-run it as that'
    Note 'account (psexec, or a scheduled task under the gMSA) before treating this as a pre-deployment sign-off.'
}

$klist = Get-Command klist.exe -ErrorAction SilentlyContinue

if (-not $klist) {
    Warn 'klist.exe is not on the path, so the ticket cache cannot be listed here.'
    Note 'Checks 2 and 6 exercise Kerberos for real, so their result is the answer that counts.'
}
else {
    $tickets = @()
    $klistExit = 0

    # Under $ErrorActionPreference = 'Stop' a native command writing to stderr
    # raises a terminating error, and klist writes there when the cache is
    # empty — which is precisely the state this check exists to report. Relaxing
    # it for the one call is how the report survives the case it is about.
    $previous = $ErrorActionPreference
    try {
        $ErrorActionPreference = 'Continue'
        $tickets = @(& $klist.Source 2>&1)
        $klistExit = $LASTEXITCODE
    }
    finally {
        $ErrorActionPreference = $previous
    }

    $cached = @($tickets | Select-String -Pattern '^#\d+>').Count

    if ($klistExit -ne 0 -or $cached -eq 0) {
        Fail "klist reported no cached Kerberos tickets for this logon session (exit $klistExit)."
        Note 'Without a TGT every Negotiate exchange below fails with 401. For a gMSA the operating system'
        Note 'obtains and renews the ticket at logon, so an empty cache under the service account usually means'
        Note 'the account is not actually logged on as a service, or the host cannot reach a domain controller.'
    }
    else {
        Pass "$cached cached Kerberos ticket(s) in this logon session"

        $services = @($tickets |
            Select-String -Pattern 'Server:\s*(\S+)' |
            ForEach-Object { $_.Matches[0].Groups[1].Value } |
            Where-Object { $_ -notlike 'krbtgt/*' } |
            Select-Object -Unique)

        if ($services.Count -gt 0) {
            Note "service tickets held: $($services -join ', ')"
        }

        Note 'A ticket for HTTP/<the HttpFS host> appears only after a Negotiate exchange has succeeded, so'
        Note 'running klist again after this script is a second, independent confirmation of check 2.'
    }
}

# ---------------------------------------------------------------------------

Step '1. HttpFS reachability'

$parsedBase = $null
if (-not [uri]::TryCreate($HdfsBaseUrl, [UriKind]::Absolute, [ref]$parsedBase)) {
    Fail "-HdfsBaseUrl '$HdfsBaseUrl' is not an absolute URL. Nothing below can run."
    Write-Host ''
    Write-Host "$($script:failures) check(s) failed. Correct -HdfsBaseUrl and run again." -ForegroundColor Red
    exit 1
}

if ($parsedBase.Scheme -ne 'https') {
    # Same rule as CdpSettings.ValidateHdfs, and for the same reason.
    Fail "-HdfsBaseUrl is $($parsedBase.Scheme), not https. The connector refuses this with exit code 2."
    Note 'The Kerberos exchange and every byte of file content would otherwise cross the network in clear.'
}
else {
    Pass "https to $($parsedBase.Host) port $($parsedBase.Port)"
}

if ($HdfsBaseUrl -notmatch '(?i)/webhdfs/v1$') {
    Fail "-HdfsBaseUrl does not end in /webhdfs/v1. The connector refuses this with exit code 2."
    Note 'HttpFS and WebHDFS share that path, for example https://httpfs01.corp:14000/webhdfs/v1.'
}
else {
    Pass 'base URL ends in /webhdfs/v1'
}

# A socket rather than Test-NetConnection: this is precisely what the connector
# opens, it needs no optional module on a Server Core host, and an ICMP result
# says nothing about a port.
$tcp = New-Object System.Net.Sockets.TcpClient
try {
    $connecting = $tcp.BeginConnect($parsedBase.Host, $parsedBase.Port, $null, $null)

    if ($connecting.AsyncWaitHandle.WaitOne(([TimeSpan]::FromSeconds(10))) -and $tcp.Connected) {
        Pass "TCP $($parsedBase.Host)`:$($parsedBase.Port) open"
    }
    else {
        Fail "TCP $($parsedBase.Host)`:$($parsedBase.Port) refused, filtered or unresolved."
        Note 'Open the port from this host to the HttpFS gateway. On a WebHDFS deployment this host must also'
        Note 'reach every DataNode, because a read redirects to whichever one holds the block; HttpFS is one'
        Note 'gateway and has no such hop, which is why the shipped configuration prefers it.'
    }
}
catch {
    Fail "TCP $($parsedBase.Host)`:$($parsedBase.Port) could not be opened: $($_.Exception.Message)"
    Note 'A name that does not resolve fails here too. Check DNS from this host before the firewall.'
}
finally {
    $tcp.Close()
}

# ---------------------------------------------------------------------------

Step '2. GETFILESTATUS on each root, over Negotiate'

$firstRoot = ''

foreach ($root in $HdfsRoots) {
    if (-not $root.StartsWith('/')) {
        Fail "'$root' is not an absolute path. Settings:HdfsRoots takes absolute paths only."
        continue
    }

    $status = Invoke-Negotiated "$HdfsBaseUrl$(ConvertTo-HdfsUrlPath $root)?op=GETFILESTATUS"

    if ($status.Ok) {
        $file = $status.Json.FileStatus
        Pass "$root — owner $($file.owner), group $($file.group), permission $($file.permission), type $($file.type)"

        if ($firstRoot -eq '') { $firstRoot = $root }

        $groupDigit = Get-PermissionDigit $file.permission 1
        if ($null -ne $groupDigit -and ($groupDigit -band 4) -eq 0) {
            Note "The group digit does not grant read, so '$($file.group)' contributes no grant to items here;"
            Note 'only named ACL entries and Ranger path policies would. See check 4.'
        }
        continue
    }

    switch ($status.Status) {
        { $_ -in @(401, 403) } { Refused "HDFS ($root)" $status.Status }
        404 {
            Fail "$root does not exist on this cluster."
            Note 'The crawl would log it and skip it, so the connector would report a clean run over nothing.'
            Note 'Correct Settings:HdfsRoots, or create the path.'
        }
        default {
            Fail "GETFILESTATUS on $root failed: $($status.Message)"
            Note 'Not an authentication answer. Check that the HttpFS gateway is up and that a proxy is not'
            Note 'intercepting the request — a proxy that strips the Negotiate header looks exactly like this.'
        }
    }
}

if ($firstRoot -eq '') {
    Note 'No root could be read, so checks 3 and 4 have nothing to run against and are skipped.'
}

# ---------------------------------------------------------------------------

Step '3. LISTSTATUS on the first readable root'

if ($firstRoot -eq '') {
    Note 'skipped: no readable root'
}
else {
    $listing = Invoke-Negotiated "$HdfsBaseUrl$(ConvertTo-HdfsUrlPath $firstRoot)?op=LISTSTATUS"

    if (-not $listing.Ok) {
        if ($listing.Status -in @(401, 403)) { Refused "HDFS LISTSTATUS ($firstRoot)" $listing.Status }
        else { Fail "LISTSTATUS on $firstRoot failed: $($listing.Message)" }
    }
    else {
        $entries = @($listing.Json.FileStatuses.FileStatus)
        $files = @($entries | Where-Object { $_.type -eq 'FILE' }).Count
        $directories = @($entries | Where-Object { $_.type -eq 'DIRECTORY' }).Count

        Pass "$firstRoot holds $files file(s) and $directories directory(ies) at this level"

        if ($entries.Count -eq 0) {
            Warn 'the root is empty. A crawl of it would succeed and index nothing.'
        }

        Note 'The crawl recurses into those directories, so this is the top of the tree rather than its size.'
        Note 'Names beginning with a dot or an underscore, and anything ending .tmp, are skipped as Hadoop''s own'
        Note 'litter — in-progress writes, Hive staging and the _SUCCESS marker are not documents.'
    }
}

# ---------------------------------------------------------------------------

Step '4. GETACLSTATUS on the first readable root'

if ($firstRoot -eq '') {
    Note 'skipped: no readable root'
}
else {
    $acl = Invoke-Negotiated "$HdfsBaseUrl$(ConvertTo-HdfsUrlPath $firstRoot)?op=GETACLSTATUS"

    if (-not $acl.Ok) {
        if ($acl.Status -in @(401, 403)) { Refused "HDFS GETACLSTATUS ($firstRoot)" $acl.Status }
        else { Fail "GETACLSTATUS on $firstRoot failed: $($acl.Message)" }
    }
    else {
        $entries = @($acl.Json.AclStatus.entries)

        if ($entries.Count -eq 0) {
            Pass "$firstRoot has no extended ACL entries; its POSIX permissions decide on their own"
        }
        else {
            Pass "$firstRoot carries $($entries.Count) extended ACL entr(y/ies): $($entries -join ', ')"

            $named = @($entries |
                Where-Object { $_ -notlike 'default:*' -and $_ -match '^(?i)group:[^:]+:.*r' })

            if ($named.Count -gt 0) {
                Note "$($named.Count) of them are named group entries granting read: $($named -join ', ')"
            }
        }

        Note 'Named "group:NAME:r--" entries are what widen who may read a file beyond its owning group, and they'
        Note 'are the part of an item''s ACL that an ls does not show. Each one becomes a grant on the item, so'
        Note 'each name must appear in Settings:EntraGroupMap or resolve in the directory — an unresolved group'
        Note 'is dropped, and a file left with no grant is skipped rather than published to everybody.'
        Note 'Entries prefixed "default:" are deliberately ignored: they describe what a file created here will'
        Note 'inherit, not who may read what is here now.'
    }
}

# ---------------------------------------------------------------------------

Step '5. Clock skew against the cluster'

if (-not $script:clusterDate -or -not $script:clusterDateReadAt) {
    Note 'skipped: no HttpFS response carried a Date header to compare against'
}
else {
    $clusterTime = [DateTimeOffset]::MinValue
    if (-not [DateTimeOffset]::TryParse($script:clusterDate, [ref]$clusterTime)) {
        Warn "the Date header '$($script:clusterDate)' could not be parsed, so skew is unknown"
    }
    else {
        $skew = [Math]::Abs(($clusterTime - $script:clusterDateReadAt).TotalSeconds)

        if ($skew -le 300) {
            Pass "$([Math]::Round($skew, 1))s between this host and the HttpFS gateway"
        }
        else {
            Warn "$([Math]::Round($skew, 1))s between this host and the HttpFS gateway."
            Note 'Kerberos rejects an authenticator whose skew exceeds five minutes, so past 300 seconds every'
            Note 'Negotiate exchange fails with a clock-skew error that reads like a credential problem and is'
            Note 'not one. Point this host at the same time source as the cluster.'
        }

        Note "cluster $($clusterTime.ToString('u')) / this host $($script:clusterDateReadAt.ToString('u'))"
        Note 'Settings:ScanSlackSeconds absorbs NameNode skew in the WATERMARK; it does nothing for Kerberos.'
    }
}

# ---------------------------------------------------------------------------

Step "6. Ranger policies for service '$RangerHdfsService'"

$policyUrl = "$RangerBaseUrl/service/public/v2/api/service/" +
    "$([uri]::EscapeDataString($RangerHdfsService))/policy"
$ranger = Invoke-Negotiated $policyUrl

if ($ranger.Ok) {
    $policies = @($ranger.Json)
    Pass "$($policies.Count) policy(ies) readable on $RangerHdfsService"

    if ($policies.Count -eq 0) {
        Warn 'Ranger returned no policies for this service. Check the service name against Ranger''s own list —'
        Warn 'it is the CM service name, cm_hdfs by default.'
        Note 'With no path policy matching, each file''s own POSIX permissions and ACL decide on their own.'
    }
    else {
        $denying = @($policies | Where-Object { @($_.denyPolicyItems).Count -gt 0 }).Count
        $disabled = @($policies | Where-Object { $_.isEnabled -eq $false }).Count

        if ($denying -gt 0) {
            Note "$denying of them carry deny items. Nothing under a denied path is indexed: a deny is never"
            Note 'mirrored into a Graph deny ACE, because a mirrored deny that drifts fails open.'
        }
        if ($disabled -gt 0) { Note "$disabled are disabled, and a disabled policy decides nothing." }
    }
}
else {
    if ($ranger.Status -in @(401, 403)) {
        Refused 'Ranger Admin' $ranger.Status
        Note 'Many Ranger installations front the REST API with basic authentication against local users. This'
        Note 'connector does Kerberos only, on purpose — a password here would be a secret in configuration —'
        Note 'so enabling SPNEGO on the Ranger Admin API is a cluster-side change, not a change to this host.'
    }
    else {
        Fail "Ranger Admin at $RangerBaseUrl could not be read: $($ranger.Message)"
    }

    Note 'THIS STOPS THE CONNECTOR. Ranger decides which paths and tables may be indexed at all, and the run'
    Note 'fails rather than indexing a source whose access policies it cannot read. There is no fallback to'
    Note '"index it anyway", so an unreachable Ranger means nothing is crawled — not that less is crawled.'
}

# ---------------------------------------------------------------------------

Step '7. Hive ODBC driver'

if (-not $HiveHost) {
    Note 'skipped: no -HiveHost. Supply it on a host that also runs --connector cdphivecontracts.'
}
else {
    $installed = @()
    foreach ($key in @('HKLM:\SOFTWARE\ODBC\ODBCINST.INI\ODBC Drivers',
                       'HKLM:\SOFTWARE\WOW6432Node\ODBC\ODBCINST.INI\ODBC Drivers')) {
        if (-not (Test-Path $key)) { continue }

        $properties = Get-ItemProperty -Path $key
        $names = @($properties.PSObject.Properties |
            Where-Object { $_.Name -notlike 'PS*' } |
            ForEach-Object { $_.Name })

        $installed += [pscustomobject]@{ Key = $key; Names = $names }
    }

    $sixtyFour = @($installed | Where-Object { $_.Key -notlike '*WOW6432Node*' } | ForEach-Object { $_.Names })
    $thirtyTwo = @($installed | Where-Object { $_.Key -like '*WOW6432Node*' } | ForEach-Object { $_.Names })

    if ($sixtyFour.Count -eq 0 -and $thirtyTwo.Count -eq 0) {
        Fail 'no ODBC drivers are registered on this host at all.'
    }
    else {
        Note "64-bit drivers: $(if ($sixtyFour.Count) { $sixtyFour -join ', ' } else { '(none)' })"
    }

    if ($sixtyFour -contains $HiveDriver) {
        Pass "'$HiveDriver' is installed for 64-bit"
    }
    elseif ($thirtyTwo -contains $HiveDriver) {
        Fail "'$HiveDriver' is installed only for 32-bit, and CdpGraphPush.exe is a 64-bit process."
        Note 'Install the 64-bit Cloudera MSI. The driver manager will not bridge the two.'
    }
    else {
        Fail "'$HiveDriver' is not installed. The name must match Settings:HiveDriver exactly."
        Note 'The driver is an operator-installed Cloudera MSI, under Cloudera''s own licence, and is'
        Note 'deliberately not bundled with this connector — the licence does not permit redistribution.'
        Note 'Download it from Cloudera, install the 64-bit build, and check the name in the ODBC Data Source'
        Note 'Administrator (64-bit) against Settings:HiveDriver.'
    }

    # Composed exactly as HiveConnectionStringFactory composes it, in the same
    # order, so a driver that accepts this accepts what the connector sends.
    $parts = @(
        "Driver={$HiveDriver}",
        "Host=$HiveHost",
        "Port=$HivePort",
        'AuthMech=1',
        "KrbServiceName=$HiveServiceName",
        "ThriftTransport=$(if ($HiveTransport -eq 'http') { '2' } else { '1' })",
        'SSL=1',
        'UseSystemTrustStore=1'
    )
    if ($HiveTransport -eq 'http') { $parts += "HTTPPath=$HiveHttpPath" }
    if ($HiveRealm) { $parts += "KrbRealm=$HiveRealm" }
    $parts += 'UseOnlySSPI=1'

    $connectionString = ($parts -join ';') + ';'

    # Printable in full because there is nothing in it to redact: AuthMech=1 is
    # Kerberos, UseOnlySSPI=1 authenticates from this logon session, and the
    # string has no UID and no PWD by construction.
    Note "connection string (it carries no credential, by construction): $connectionString"

    $connection = $null
    try {
        $connection = New-Object System.Data.Odbc.OdbcConnection $connectionString
        $connection.Open()

        $command = $connection.CreateCommand()
        $command.CommandText = 'SELECT 1'
        $command.CommandTimeout = $script:timeoutSeconds
        $value = $command.ExecuteScalar()

        Pass "connected to $HiveHost`:$HivePort over Kerberos and SELECT 1 returned $value"
        Note 'Which tables are actually indexed is Ranger''s decision, not this one: a table with a row filter,'
        Note 'a column mask, any deny, a column-scoped grant, or no group granted select is routed to a live'
        Note 'query instead. Settings:RangerSqlService (cm_hive by default) is where that is read.'
    }
    catch {
        Fail "the ODBC connection to $HiveHost`:$HivePort failed: $($_.Exception.Message)"
        Note 'The driver is an operator-installed Cloudera MSI, under Cloudera''s own licence, and is'
        Note 'deliberately not bundled with this connector, so a missing or mismatched driver is a host'
        Note 'preparation step rather than a packaging bug.'
        Note 'If the message names Kerberos, GSS or SSPI, this is exit code 3 — the identity was rejected, and'
        Note 'no password is involved. If it names the transport, remember that Kerberos does not support the'
        Note 'binary Thrift transport: Settings:HiveTransport must be http or sasl.'
    }
    finally {
        if ($connection) { $connection.Dispose() }
    }
}

# ---------------------------------------------------------------------------

Write-Host ''
if ($script:failures -eq 0) {
    Write-Host 'The cluster is reachable as this identity. The first crawl has what it needs.' -ForegroundColor Green
    Note 'Next, and still writing nothing to Graph:'
    Note '  CdpGraphPush.exe --connector cdphdfsdocs --dry-run'
    Note 'It reads and maps every item and reports what it would write, and it does NOT advance the watermark,'
    Note 'so a dry run costs nothing and can be repeated.'
    exit 0
}
else {
    Write-Host "$($script:failures) check(s) failed. The first crawl will not succeed as it stands." -ForegroundColor Red
    Write-Host 'A refused identity is exit code 3, invalid configuration is 2, and an ingestion failure is 4.' -ForegroundColor Red
    exit 1
}
