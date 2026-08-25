<#
.SYNOPSIS
    Shared client-credentials authentication for the SqlGraphPush diagnostic
    scripts. Dot-source it; it defines functions and does nothing on its own.

.DESCRIPTION
    Three scripts need the same app-only token against the same app
    registration, so the flow lives here once:

        . .\GraphPushAuth.ps1
        $config = Get-PushConfig -Path .\appsettings.json
        $auth   = Get-PushToken -Config $config

    Raw REST rather than the Microsoft.Graph module, for two reasons. The
    module cannot hand back the raw access token, and the roles claim inside it
    is the only client-side evidence of which application permissions were
    actually consented. And a jump box that runs SqlGraphPush need not have
    PowerShell modules installed at all — this depends on nothing.

    A client secret, when one is used, is held as a SecureString and marshalled
    to plain text only for the moment of the POST, inside a try/finally that
    zeroes the buffer. It is never assigned to a variable that outlives the
    call, never logged and never returned.
#>

function ConvertTo-Base64Url {
    param([byte[]]$Bytes)
    [Convert]::ToBase64String($Bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_')
}

function ConvertFrom-JwtPayload {
    param([string]$Jwt)
    $part = $Jwt.Split('.')[1].Replace('-', '+').Replace('_', '/')
    switch ($part.Length % 4) { 2 { $part += '==' } 3 { $part += '=' } }
    [Text.Encoding]::UTF8.GetString([Convert]::FromBase64String($part)) | ConvertFrom-Json
}

function Get-PushConfig {
    <#
    .SYNOPSIS
        Reads and lightly validates src/SqlGraphPush/appsettings.json.
    #>
    param([Parameter(Mandatory)][string]$Path)

    if (-not (Test-Path $Path)) {
        throw "No appsettings.json at $Path. Point -ConfigPath at src\SqlGraphPush\appsettings.json or the copy beside the executable."
    }

    $config = Get-Content $Path -Raw | ConvertFrom-Json

    # Graph is not in this list: PushHost.ApplyDefaults fills an omitted Graph
    # section from the connector's own defaults, so its absence is valid.
    foreach ($required in @('Auth', 'DataSource')) {
        if (-not $config.$required) {
            throw "$Path has no '$required' section. Is this the connector's appsettings.json rather than SqlGraphPush's?"
        }
    }

    return $config
}

function Get-PushCertificate {
    <#
    .SYNOPSIS
        Finds the first configured thumbprint that is present with a usable
        private key, looking in the configured store location.
    .DESCRIPTION
        Returns $null when none is usable. The store location comes from
        Auth:CertificateStoreLocation; when the key is omitted the fallback is
        LocalMachine, because that is what the C# AuthOptions defaults to - this
        pre-flight must prove the store the tool will actually read, not a
        different one. The shipped appsettings set CurrentUser explicitly, since
        the push tools run as a person.
    #>
    param([Parameter(Mandatory)]$Config)

    $location = if ($Config.Auth.CertificateStoreLocation) { $Config.Auth.CertificateStoreLocation } else { 'LocalMachine' }

    foreach ($thumbprint in @($Config.Auth.CertificateThumbprints)) {
        if (-not $thumbprint -or $thumbprint -match 'REPLACE') { continue }
        $cert = Get-ChildItem "Cert:\$location\My\$thumbprint" -ErrorAction SilentlyContinue
        if ($cert -and $cert.HasPrivateKey) { return $cert }
    }

    # Fall back to the subject, matching StoreCertificateResolver: a renewal
    # from the same issuer is then picked up without a configuration edit.
    if ($Config.Auth.CertificateSubject) {
        $bySubject = Get-ChildItem "Cert:\$location\My" -ErrorAction SilentlyContinue |
            Where-Object { $_.Subject -eq $Config.Auth.CertificateSubject -and $_.HasPrivateKey } |
            Sort-Object NotAfter -Descending |
            Select-Object -First 1
        if ($bySubject) { return $bySubject }
    }

    return $null
}

function Get-PushToken {
    <#
    .SYNOPSIS
        Acquires an app-only token for the configured app registration.
    .OUTPUTS
        A hashtable: Token, Claims, Roles, Error, Aadsts, Advice. Token is null
        on failure and Advice explains the AADSTS code when there is one.
    #>
    param(
        [Parameter(Mandatory)]$Config,
        [string]$Scope = 'https://graph.microsoft.com/.default',
        $Certificate,
        [System.Security.SecureString]$ClientSecret
    )

    $tenantId = $Config.Auth.TenantId
    $clientId = $Config.Auth.ClientId
    $tokenUri = "https://login.microsoftonline.com/$tenantId/oauth2/v2.0/token"

    $result = @{ Token = $null; Claims = $null; Roles = @(); Error = $null; Detail = $null; Aadsts = $null; Advice = $null }

    $body = @{
        grant_type = 'client_credentials'
        client_id  = $clientId
        scope      = $Scope
    }

    if ($Certificate) {
        $now = [DateTimeOffset]::UtcNow.ToUnixTimeSeconds()
        $header = @{
            alg = 'RS256'
            typ = 'JWT'
            x5t = (ConvertTo-Base64Url -Bytes $Certificate.GetCertHash())
        } | ConvertTo-Json -Compress

        $claims = @{
            aud = $tokenUri
            iss = $clientId
            sub = $clientId
            jti = [guid]::NewGuid().ToString()
            nbf = $now
            exp = $now + 300
        } | ConvertTo-Json -Compress

        $unsigned = (ConvertTo-Base64Url -Bytes ([Text.Encoding]::UTF8.GetBytes($header))) + '.' +
                    (ConvertTo-Base64Url -Bytes ([Text.Encoding]::UTF8.GetBytes($claims)))

        try {
            $rsa = [System.Security.Cryptography.X509Certificates.RSACertificateExtensions]::GetRSAPrivateKey($Certificate)
            if (-not $rsa) { throw 'the private key is not an RSA key, or it is not readable by this account' }
            $signature = $rsa.SignData(
                [Text.Encoding]::UTF8.GetBytes($unsigned),
                [System.Security.Cryptography.HashAlgorithmName]::SHA256,
                [System.Security.Cryptography.RSASignaturePadding]::Pkcs1)
        }
        catch {
            $result.Error = "the private key cannot sign: $($_.Exception.Message)"
            $result.Advice = 'The certificate is present but its key is unusable by this account. Re-import it with its private key.'
            return $result
        }

        $body['client_assertion_type'] = 'urn:ietf:params:oauth:client-assertion-type:jwt-bearer'
        $body['client_assertion'] = $unsigned + '.' + (ConvertTo-Base64Url -Bytes $signature)
    }
    elseif ($ClientSecret) {
        $bstr = [Runtime.InteropServices.Marshal]::SecureStringToBSTR($ClientSecret)
        try { $body['client_secret'] = [Runtime.InteropServices.Marshal]::PtrToStringBSTR($bstr) }
        finally { [Runtime.InteropServices.Marshal]::ZeroFreeBSTR($bstr) }
    }
    else {
        $result.Error = 'no certificate and no client secret were supplied'
        return $result
    }

    try {
        $response = Invoke-RestMethod -Method POST -Uri $tokenUri -Body $body -ContentType 'application/x-www-form-urlencoded'
        $result.Token = $response.access_token
        $result.Claims = ConvertFrom-JwtPayload -Jwt $result.Token
        $result.Roles = @($result.Claims.roles)
    }
    catch {
        $result.Error = $_.Exception.Message
        $detail = "$($_.ErrorDetails.Message)"

        # The response body is the actual diagnosis. Mining it only for an
        # AADSTS number and then discarding it left non-AADSTS failures (proxy
        # errors, malformed requests) with a bare status line and nothing else.
        if ($detail) { $result.Detail = $detail }

        if ($detail -match 'AADSTS(\d+)') {
            $result.Aadsts = $Matches[1]
            $result.Advice = switch ($result.Aadsts) {
                '700027'  { 'The certificate is not registered on this app. Upload the .cer under Certificates & secrets.' }
                '700016'  { 'The application was not found in this tenant. Check Auth:ClientId and Auth:TenantId.' }
                '7000215' { 'Invalid client secret. It may simply have expired — a client secret warns about nothing.' }
                '7000222' { 'The client secret has EXPIRED. Add a new one in Entra, then update Credential Manager.' }
                '900023'  { 'That tenant ID is not a tenant. Check Auth:TenantId.' }
                '90002'   { 'Tenant not found. Check Auth:TenantId.' }
                default   { 'See the AADSTS code in the Entra sign-in logs for this service principal.' }
            }
        }
    }
    finally {
        # The plain text lived only inside $body. Drop it before the hashtable
        # can be inspected, dumped by an unhandled error, or captured by a
        # transcript.
        if ($body.ContainsKey('client_secret')) { $body['client_secret'] = $null }
        $body.Remove('client_secret')
    }

    return $result
}


function Get-StoredClientSecret {
    <#
    .SYNOPSIS
        Reads a client secret from Windows Credential Manager, the same entry the
        push tools read at startup.
    .DESCRIPTION
        Without this the pre-flight would have to prompt, and would then be
        testing the secret you typed rather than the one the tool will actually
        use — which is the failure it exists to catch. Credential Manager is per
        account, so a hit here means the account running this script can read it;
        run the script as whoever runs the tool.

        Read only. CredReadW and CredFree, and deliberately no CredWrite —
        mirroring Security/Secrets/WindowsCredentialStore.cs, which is the same
        decision for the same reason.

        Returns a SecureString, or $null when the entry does not exist or this is
        not Windows. The value passes through a .NET string on the way out of the
        unmanaged buffer, which is unavoidable with this API and matches what the
        tools themselves do; it is never written anywhere.
    #>
    param([Parameter(Mandatory)][string]$Target)

    $onWindows = $PSVersionTable.PSVersion.Major -lt 6 -or $IsWindows
    if (-not $onWindows) { return $null }

    if (-not ('SqlConnectorCredMan' -as [type])) {
        Add-Type -Namespace '' -Name 'SqlConnectorCredMan' -MemberDefinition @'
[StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
public struct CREDENTIAL
{
    public uint Flags;
    public uint Type;
    public IntPtr TargetName;
    public IntPtr Comment;
    public long LastWritten;
    public uint CredentialBlobSize;
    public IntPtr CredentialBlob;
    public uint Persist;
    public uint AttributeCount;
    public IntPtr Attributes;
    public IntPtr TargetAlias;
    public IntPtr UserName;
}

[DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
public static extern bool CredReadW(string target, uint type, uint flags, out IntPtr credential);

[DllImport("advapi32.dll")]
public static extern void CredFree(IntPtr buffer);
'@ -UsingNamespace 'System.Runtime.InteropServices'
    }

    $handle = [IntPtr]::Zero
    # 1 is CRED_TYPE_GENERIC, which is what cmdkey /generic writes.
    if (-not [SqlConnectorCredMan]::CredReadW($Target, 1, 0, [ref]$handle)) {
        return $null
    }

    try {
        $credential = [Runtime.InteropServices.Marshal]::PtrToStructure(
            $handle, [Type][SqlConnectorCredMan+CREDENTIAL])
        if ($credential.CredentialBlobSize -eq 0) { return $null }

        $plain = [Runtime.InteropServices.Marshal]::PtrToStringUni(
            $credential.CredentialBlob, $credential.CredentialBlobSize / 2)

        $secure = New-Object System.Security.SecureString
        foreach ($character in $plain.ToCharArray()) { $secure.AppendChar($character) }
        $secure.MakeReadOnly()
        return $secure
    }
    finally {
        [SqlConnectorCredMan]::CredFree($handle)
    }
}

function Get-PushCredential {
    <#
    .SYNOPSIS
        Resolves whichever credential Auth:Mode calls for.
    .DESCRIPTION
        For ClientSecret mode the Credential Manager entry named in
        Auth:ClientSecretCredentialTarget is read first, because that is what the
        tool itself reads — testing anything else proves nothing. Prompting is
        the fallback for when this session cannot see the entry, and the caller
        is told which of the two happened, since "it worked when I typed it" and
        "it worked from the store" are very different results.
    .OUTPUTS
        Certificate, ClientSecret, and Source: 'certificate', 'store',
        'prompt' or 'parameter'.
    #>
    param(
        [Parameter(Mandatory)]$Config,
        [System.Security.SecureString]$ClientSecret
    )

    if ($Config.Auth.Mode -eq 'ClientSecret') {
        if ($ClientSecret) {
            return @{ Certificate = $null; ClientSecret = $ClientSecret; Source = 'parameter' }
        }

        $target = $Config.Auth.ClientSecretCredentialTarget
        if ($target) {
            $stored = Get-StoredClientSecret -Target $target
            if ($stored) {
                return @{ Certificate = $null; ClientSecret = $stored; Source = 'store' }
            }
        }

        $typed = Read-Host -AsSecureString "Client secret for app $($Config.Auth.ClientId)"
        return @{ Certificate = $null; ClientSecret = $typed; Source = 'prompt' }
    }

    return @{ Certificate = (Get-PushCertificate -Config $Config); ClientSecret = $null; Source = 'certificate' }
}

function Get-RetryAfterSeconds {
    <#
    .SYNOPSIS
        Reads Retry-After from a failed response, across both PowerShell hosts.
    .DESCRIPTION
        Windows PowerShell throws a WebException whose Headers indexes by name;
        PowerShell 7 throws an HttpResponseException whose Headers do not. Both
        shapes are tried, and a caller-supplied default is used when neither
        yields a number — never guess low, since guessing low is what turns one
        429 into a run of them.
    #>
    param($ErrorRecord, [int]$Default = 30)

    $response = $ErrorRecord.Exception.Response
    if (-not $response) { return $Default }

    try {
        $delta = $response.Headers.RetryAfter.Delta
        if ($delta) { return [int]$delta.TotalSeconds }
    }
    catch { }

    try {
        $named = $response.Headers['Retry-After']
        if ($named) { return [int]$named }
    }
    catch { }

    return $Default
}
