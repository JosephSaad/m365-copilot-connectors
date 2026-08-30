<#
.SYNOPSIS
    Pre-flight for SqlGraphPush: proves the certificate, the token, the granted
    application permissions and the ownership of the connection — before the
    tool is run, rather than after it has half finished.

.DESCRIPTION
    Run this on the workstation or jump box that runs SqlGraphPush, as the
    account that runs it. Read-only: it acquires a token and issues GETs, and
    creates, changes and deletes nothing.

    Unlike the agent path, this tool DOES call Microsoft Graph, so its failures
    are Graph failures — and most of them arrive as a bare 403 with no
    indication of which of four causes it is. Each check below separates one.

    1. THE ROLES CLAIM. An application permission listed in the portal but never
       admin-consented simply does not appear in the token. This decodes the
       token it just acquired and lists the roles actually present, which turns
       "403 Forbidden" into "ExternalItem.ReadWrite.OwnedBy was never
       consented". Nothing else distinguishes those two states from the client.

    2. OWNERSHIP. OwnedBy applies only to connections this app created. If the
       Graph connector agent created the connection, this app cannot touch it —
       every call 403s — and the reverse is equally true. Note that here, unlike
       interactive verification, an OwnedBy listing IS meaningful evidence: this
       app owns what it created, so a connection missing from the list is one
       this app cannot manage.

    3. THE CERTIFICATE STORE. SqlGraphPush reads CurrentUser\My, not
       LocalMachine\My, because it runs as a person rather than a service. A
       certificate imported into the machine store is invisible to it.

    4. KEY VAULT. The same credential authenticates to Graph and to the vault.
       Graph consent working says nothing about the vault role assignment.

.PARAMETER ConfigPath
    src/SqlGraphPush/appsettings.json, or the copy beside the built executable.

.PARAMETER ClientSecret
    Only for Auth:Mode = ClientSecret; prompted for if omitted. Held as a
    SecureString and marshalled only for the moment of the token request.

.EXAMPLE
    .\Test-GraphPushPrereqs.ps1 -ConfigPath ..\src\SqlGraphPush\appsettings.json

.EXAMPLE
    .\Test-GraphPushPrereqs.ps1 -ConfigPath .\appsettings.json -SkipSql
#>

[CmdletBinding()]
param(
    [string]$ConfigPath = '.\appsettings.json',
    [System.Security.SecureString]$ClientSecret,
    [switch]$SkipSql,

    # When the config omits Graph:ConnectionId (the tool fills it per
    # connector), this names the connection to validate. The script refuses to
    # guess: 'sqltickets' for SqlGraphPush, 'consultingwork' for SqlHierarchyPush.
    [string]$ConnectionIdOverride
)

$ErrorActionPreference = 'Stop'
$script:failures = 0

. (Join-Path $PSScriptRoot 'GraphPushAuth.ps1')

function Step([string]$name) { Write-Host "`n== $name ==" -ForegroundColor Cyan }
function Pass([string]$msg) { Write-Host "  PASS  $msg" -ForegroundColor Green }
function Fail([string]$msg) { Write-Host "  FAIL  $msg" -ForegroundColor Red; $script:failures++ }
function Warn([string]$msg) { Write-Host "  WARN  $msg" -ForegroundColor Yellow }
function Note([string]$msg) { Write-Host "  note  $msg" -ForegroundColor DarkGray }

# ---------------------------------------------------------------------------

Step '0. Configuration'

$config = Get-PushConfig -Path $ConfigPath
Pass "read $ConfigPath"

$placeholders = @()
foreach ($m in [regex]::Matches((Get-Content $ConfigPath -Raw), '"([^"]*REPLACE-WITH[^"]*)"')) {
    $placeholders += $m.Groups[1].Value
}
if ($placeholders.Count -gt 0) {
    Fail "$($placeholders.Count) unreplaced placeholder(s): $($placeholders -join ', ')"
    Note 'SqlGraphPush exits with code 2 and lists every invalid field at once.'
}
else {
    Pass 'no REPLACE-WITH placeholders remain'
}

$connectionId = $config.Graph.ConnectionId

if ([string]::IsNullOrWhiteSpace($connectionId) -and $ConnectionIdOverride) {
    $connectionId = $ConnectionIdOverride
}

if ([string]::IsNullOrWhiteSpace($connectionId)) {
    # An omitted ConnectionId is valid configuration - PushHost fills it from
    # whichever connector runs - but this script cannot know which tool the
    # config belongs to, and validating the WRONG connection would pass or fail
    # the wrong deployment. Guessing is worse than asking.
    Fail ("Graph:ConnectionId is not set in this file. Pass -ConnectionIdOverride with the connection the tool " +
        "will use ('sqltickets' for SqlGraphPush, 'consultingwork' for SqlHierarchyPush) so the ownership " +
        'checks below test the right one.')
    exit 1
}

if ($connectionId.Length -lt 3 -or $connectionId.Length -gt 32 -or $connectionId -notmatch '^[a-zA-Z0-9]+$') {
    Fail "Graph:ConnectionId '$connectionId' must be 3 to 32 alphanumeric characters"
}
elseif ($connectionId -match '^(?i)Microsoft' -or $connectionId -eq 'None') {
    Fail "Graph:ConnectionId '$connectionId' is reserved: it cannot start with 'Microsoft' and cannot be 'None'"
}
else {
    Pass "connection ID '$connectionId'"
}

$acl = @($config.Acl.GrantGroupObjectIds) | Where-Object { $_ -and $_ -notmatch 'REPLACE' }
if ($acl.Count -eq 0) {
    Fail 'Acl:GrantGroupObjectIds is empty. Every item would be written with an empty ACL and be invisible to everyone.'
}
else {
    Pass "$($acl.Count) ACL group(s) configured"
    Note 'These are written into each item at push time. Changing them later requires pushing every item again.'
}

Note "Auth mode: $($config.Auth.Mode); store: $($config.Auth.CertificateStoreLocation); SQL auth: $($config.DataSource.SqlAuthMode)"

# ---------------------------------------------------------------------------

Step '1. Credential'

$certificate = $null

if ($config.Auth.Mode -eq 'Certificate') {
    $location = $config.Auth.CertificateStoreLocation
    if ($location -and $location -ne 'CurrentUser') {
        Warn "Auth:CertificateStoreLocation is '$location'; the shipped value for this tool is CurrentUser."
        Note 'SqlGraphPush runs as a person, not a service. A certificate in LocalMachine\My is invisible to a'
        Note 'CurrentUser lookup, and vice versa — this is the most common first-run failure on this path.'
    }

    foreach ($thumbprint in @($config.Auth.CertificateThumbprints)) {
        if (-not $thumbprint -or $thumbprint -match 'REPLACE') { continue }
        $found = Get-ChildItem "Cert:\$location\My\$thumbprint" -ErrorAction SilentlyContinue
        if (-not $found) {
            Fail "certificate $thumbprint is not in $location\My"
            $other = if ($location -eq 'CurrentUser') { 'LocalMachine' } else { 'CurrentUser' }
            if (Get-ChildItem "Cert:\$other\My\$thumbprint" -ErrorAction SilentlyContinue) {
                Note "It IS in $other\My. Import it into $location\My, or change Auth:CertificateStoreLocation."
            }
            continue
        }

        $days = [int]($found.NotAfter - (Get-Date)).TotalDays
        if ($days -lt 0) { Fail "$($thumbprint.Substring(0,8))… EXPIRED $([Math]::Abs($days)) day(s) ago" }
        elseif ($days -le 30) { Warn "$($thumbprint.Substring(0,8))… expires in $days day(s)" }
        else { Pass "$($thumbprint.Substring(0,8))… ($($found.Subject)) valid for $days more day(s)" }

        if (-not $found.HasPrivateKey) {
            Fail "$($thumbprint.Substring(0,8))… has no private key — only the .cer was imported"
        }
    }

    $certificate = Get-PushCertificate -Config $config
    if (-not $certificate) {
        Fail 'no usable certificate. No token can be acquired, so every check below is skipped.'
    }
}
elseif ($config.Auth.Mode -eq 'ClientSecret') {
    $target = $config.Auth.ClientSecretCredentialTarget
    if (-not $target) {
        Fail 'Auth:Mode is ClientSecret but Auth:ClientSecretCredentialTarget is empty.'
    }
    else {
        # Read the entry the tool itself reads. Prompting instead would test the
        # secret you typed rather than the one the push will use, which is
        # precisely the failure this check exists to catch.
        $resolved = Get-PushCredential -Config $config -ClientSecret $ClientSecret
        $ClientSecret = $resolved.ClientSecret

        switch ($resolved.Source) {
            'store' {
                Pass "client secret read from Credential Manager target '$target'"
                Note 'Credential Manager is per account. This proves the entry is readable by THIS account —'
                Note 'which is the right answer only if this session is the one that runs the push.'
            }
            'prompt' {
                Warn "no Credential Manager entry named '$target' is readable by $([Security.Principal.WindowsIdentity]::GetCurrent().Name)"
                Note 'Falling back to the value you just typed. Everything below therefore tests that value, not'
                Note 'the deployment: the push will still fail at startup with exit code 3 until the entry exists.'
                Note "Store it as the account that runs the push:  cmdkey /generic:$target /user:<client-id> /pass:<secret>"
            }
            'parameter' {
                Warn 'using the secret passed to -ClientSecret, not the stored entry'
                Note 'This tests the value you supplied. Re-run without -ClientSecret to test the deployment itself.'
            }
        }
    }
    Note 'A client secret has no expiry warning of any kind. Track its Entra expiry wherever you track certificates.'
}
else {
    Fail "Auth:Mode '$($config.Auth.Mode)' is not one of Certificate or ClientSecret"
}

# ---------------------------------------------------------------------------

Step '2. Token, and the permissions actually granted'

$auth = $null
if ($certificate -or $ClientSecret) {
    $auth = Get-PushToken -Config $config -Certificate $certificate -ClientSecret $ClientSecret

    if ($auth.Token) {
        if ($certificate) { Pass 'the private key signed a client assertion' }
        Pass "token acquired for tenant $($config.Auth.TenantId)"
        Note "app id $($auth.Claims.appid); valid until $([DateTimeOffset]::FromUnixTimeSeconds($auth.Claims.exp).ToLocalTime())"
    }
    else {
        Fail "token request failed: $($auth.Error)"
        if ($auth.Detail) { Note "response body: $($auth.Detail)" }
        if ($auth.Aadsts) { Note "AADSTS$($auth.Aadsts)" }
        if ($auth.Advice) { Note $auth.Advice }
    }
}
else {
    Note 'skipped: no credential'
}

$required = @('ExternalConnection.ReadWrite.OwnedBy', 'ExternalItem.ReadWrite.OwnedBy')

if ($auth -and $auth.Token) {
    # The check worth running the script for. A permission listed in the portal
    # but not admin-consented is simply absent here, and the only other symptom
    # is a bare 403 on whichever call needed it.
    foreach ($role in $required) {
        if ($auth.Roles -contains $role) {
            Pass "$role granted"
        }
        else {
            Fail "$role is NOT in the token"
            Note 'Listed in the portal is not the same as consented. Entra -> the app -> API permissions -> Grant admin consent.'
        }
    }

    $extra = @($auth.Roles | Where-Object { $_ -notin $required })
    if ($extra.Count -gt 0) {
        Warn "this app also holds: $($extra -join ', ')"
        Note 'docs/APP-REGISTRATION.md section 6 lists what these identities must never have. Anything .All is over-privileged.'
    }
}

# ---------------------------------------------------------------------------

Step "3. Connection '$connectionId', and who owns it"

if ($auth -and $auth.Token) {
    $headers = @{ Authorization = "Bearer $($auth.Token)" }
    try {
        $owned = Invoke-RestMethod -Method GET -Headers $headers -Uri 'https://graph.microsoft.com/v1.0/external/connections'
        $ids = @($owned.value | ForEach-Object { $_.id })

        # The inverse of the trap in the agent-path guide: under OwnedBy this
        # list is exactly what this app owns, so absence here IS evidence.
        if ($ids -contains $connectionId) {
            $existing = $owned.value | Where-Object { $_.id -eq $connectionId }
            Pass "owned by this app — state: $($existing.state)"
            switch ("$($existing.state)") {
                'draft' { Warn "state 'draft': schema registration has not completed. Items cannot be written yet." }
                'ready' { Note 'ready: schema registered; items can be written and indexed.' }
                'obsolete' { Fail "state 'obsolete': this connection has been superseded and will not serve results." }
                'limitExceeded' { Fail "state 'limitExceeded': tenant item quota is full. No further items will be accepted." }
            }
        }
        elseif ($ids.Count -eq 0) {
            Note "this app owns no connections yet. '$connectionId' will be created on the first run."
        }
        else {
            Fail "'$connectionId' is not owned by this app. Owned: $($ids -join ', ')"
            Note 'If the Graph connector agent created this connection, this app cannot touch it — every call 403s — and'
            Note 'it cannot be created again because the ID is taken. The two pipelines must not share a connection ID.'
        }
    }
    catch {
        Fail "GET /external/connections -> $($_.Exception.Message)"
        if ($_.ErrorDetails.Message) { Note "response body: $($_.ErrorDetails.Message)" }
        Note 'A 403 here while the roles above are present means consent was granted after this token was issued. Re-run.'
    }
}
else {
    Note 'skipped: no token'
}

# ---------------------------------------------------------------------------

Step '4. Key Vault'

if ($config.DataSource.SqlAuthMode -eq 'WindowsIntegrated') {
    Note 'SqlAuthMode is WindowsIntegrated: no secret is resolved, so vault access is not on the critical path.'
}
elseif (-not $config.KeyVault.Uri) {
    Note 'no KeyVault:Uri configured'
}
elseif ($certificate -or $ClientSecret) {
    # A different audience entirely: Graph consent proves nothing about the
    # vault, and the two fail independently.
    $vault = Get-PushToken -Config $config -Scope 'https://vault.azure.net/.default' `
        -Certificate $certificate -ClientSecret $ClientSecret

    if ($vault.Token) {
        Pass 'a Key Vault token was issued for this app'
        Note "It still needs a data-plane role on $($config.KeyVault.Uri) — Key Vault Secrets User is enough."
        Note "A 403 resolving '$($config.KeyVault.Secrets.SqlPassword)' at run time is that role assignment or its scope."
    }
    else {
        Fail "no Key Vault token: $($vault.Error)"
    }
}

# ---------------------------------------------------------------------------

Step '5. SQL source'

if ($SkipSql) {
    Note 'skipped by -SkipSql'
}
else {
    $server = $config.DataSource.Server
    $sqlPort = 1433
    $hostName = $server
    if ($server -match '^(.+),(\d+)$') { $hostName = $Matches[1]; $sqlPort = [int]$Matches[2] }
    elseif ($server -match '\\') { $hostName = $server.Split('\')[0] }

    $probe = Test-NetConnection -ComputerName $hostName -Port $sqlPort -WarningAction SilentlyContinue
    if ($probe.TcpTestSucceeded) {
        Pass "TCP $hostName`:$sqlPort open"
        Note 'For the grant, the columns and the item sizes, run Test-SqlSource.ps1 — it applies to both pipelines.'
    }
    else {
        Fail "TCP $hostName`:$sqlPort refused or filtered"
        Note 'SqlGraphPush usually runs from a workstation or jump box, which is often not a host allowed to reach SQL.'
    }
}

# ---------------------------------------------------------------------------

Write-Host ''
if ($script:failures -eq 0) {
    Write-Host 'Pre-flight passed. SqlGraphPush has what it needs.' -ForegroundColor Green
}
else {
    Write-Host "$($script:failures) check(s) failed. See docs/TROUBLESHOOTING-DIRECT-PUSH.md." -ForegroundColor Red
    exit 1
}
