<#
.SYNOPSIS
    Serves a local folder as if it were WebHDFS, so CdpGraphPush can be run end
    to end on a laptop or a test VM with no Cloudera cluster anywhere near it.

.DESCRIPTION
    THIS IS A TEST DOUBLE. IT PERFORMS NO AUTHENTICATION AND NO AUTHORISATION.

    It exists so that the crawl, the ordering, the watermark and the extraction
    can be exercised without a cluster. Every request is answered, whoever asks.
    It must never be used to serve anything real, and it must never be reachable
    from another machine - which is why it refuses to bind to anything but the
    loopback interface and why the header below spends as long on what it does
    not prove as on what it does.

    WHAT IT DOES NOT PROVE. There is no Kerberos here. The connector reaches a
    real cluster over HTTP Negotiate as the running identity, and the whole
    SSPI path - the gMSA's ticket, the SPN, the realm trust, HttpFS accepting
    the exchange - is skipped entirely by this stub. A crawl that works against
    it says nothing whatever about whether the service account can read the
    cluster. Only a real cluster proves that; deploy\Test-GraphPushPrereqs.ps1
    and a run against HttpFS are what prove it.

    WHAT IT IS GOOD FOR. Two things, and they are worth having.

    1. Seeing the connector work. The crawl walks the tree, the extraction runs
       over real bytes, the watermark is written, and the trace this script
       prints shows each request as it is served, so the walk is visible rather
       than inferred from a log afterwards.

    2. Demonstrating the fail-closed ACL rule. Give a folder mode 600 in
       -PermissionMap and no group is granted read on its files, so no grant can
       be derived, so those files are SKIPPED rather than indexed with a
       fallback grant. An item granted to nobody is indexed and then returned to
       no one, which is the harmless case; the case this rule exists to prevent
       is the opposite one, and a stub is a cheap place to watch it hold.

    Two consequences of the design that will otherwise waste an afternoon.

    Changing -PermissionMap and re-running does NOT change the ACLs on items
    already written. A permission change does not alter a file's modification
    time, so the incremental crawl does not revisit the file at all. Re-derive
    them by deleting the watermark file (state\cdphdfsdocs.watermark.json under
    the executable), by letting Settings:FullRecrawlEveryRuns come round, or by
    touching the files themselves. This is the same upper bound on ACL staleness
    that the connector documents against a real cluster.

    With -RangerStub the policy endpoint answers an empty array, so Ranger adds
    no grants and declares no row filter, column mask or deny. The routing rules
    are therefore NOT exercised here at all: nothing in this stub can show a
    row-filtered table being refused. Those are proved by the unit tests and by
    02-create-ranger-test-policies.sh against a real Ranger.

    Without -RangerStub the policy endpoint returns 404, the connector's Ranger
    read fails, and the run stops. That is correct behaviour and not a bug in
    this script: a source whose access policies cannot be read is never indexed.

.PARAMETER Root
    The local folder to serve. It becomes the HDFS root, so a file at
    <Root>\data\caseworks\contracts\a.txt is /data/caseworks/contracts/a.txt and
    Settings:HdfsRoots would name /data/caseworks. Hidden and system files are
    not served: a folder on a laptop contains .git and desktop.ini, and neither
    belongs in a crawl demonstration.

.PARAMETER Prefix
    The HttpListener prefix, default http://localhost:14000/. The host must be
    loopback. A prefix bound to + or * or to a real hostname would publish an
    unauthenticated read of -Root to every machine that can route to this one,
    and this script stops rather than doing that.

.PARAMETER PermissionMap
    Which group and POSIX mode each folder's entries report, as a hashtable or
    as a string of clauses separated by semicolons:

        'contracts=hadoop-contracts-read:640;private=hadoop-private:600'

    The key is a folder path relative to -Root and applies to everything beneath
    it; the longest matching key wins. The mode's group digit is what decides a
    grant: 640 grants the owning group read, 600 grants nobody. Extra groups
    after a comma become named ACL entries - 'policies=hadoop-policies-read,
    hadoop-audit-read:640' reports one owning group and one group:NAME:r--
    entry, which is the two-grants-on-one-file case from
    00-create-hdfs-test-data.sh.

    Unmapped folders report hadoop-contracts-read and 640, matching the ordinary
    case in that script. Every group named here must also appear in
    Settings:EntraGroupMap or the connector drops it, and a file whose groups are
    all dropped is skipped.

.PARAMETER RangerStub
    Also answer /service/public/v2/api/service/{service}/policy with an empty
    array, so the connector's mandatory Ranger read succeeds. See the note above
    about what an empty policy list does and does not demonstrate.

.EXAMPLE
    .\03-Start-LocalWebHdfsStub.ps1 -Root C:\stub-hdfs -RangerStub

    Serves C:\stub-hdfs on http://localhost:14000/ with default permissions.

.EXAMPLE
    .\03-Start-LocalWebHdfsStub.ps1 -Root C:\stub-hdfs -RangerStub -PermissionMap @{
        'data/caseworks/contracts' = 'hadoop-contracts-read:640'
        'data/caseworks/policies'  = 'hadoop-policies-read,hadoop-audit-read:640'
        'data/caseworks/private'   = 'hadoop-private:600'
    }

    The three-folder test case: one ordinary folder, one with two groups on its
    files, and one that no group can read and whose files must therefore be
    skipped rather than indexed.

.EXAMPLE
    The matching Settings overrides go in appsettings.cdphdfsdocs.json beside
    CdpGraphPush.exe. The host reads that file and nothing else - there is no
    command-line or environment override - so edit a copy, not the deployed one:

        "Settings": {
          "HdfsBaseUrl":   "http://localhost:14000/webhdfs/v1",
          "HdfsRoots":     "/data/caseworks",
          "RangerBaseUrl": "http://localhost:14000",
          "EntraGroupMap": "hadoop-contracts-read=<group object id>;
                            hadoop-policies-read=<group object id>"
        }

    Then:  CdpGraphPush.exe --connector cdphdfsdocs --dry-run

    BE AWARE THAT THIS WILL NOT RUN AS WRITTEN. CdpSettings requires
    Settings:HdfsBaseUrl and Settings:RangerBaseUrl to be https - the Kerberos
    exchange and every byte of file content would otherwise cross the network in
    clear - so a plain-http URL is a configuration error and CdpGraphPush.exe
    exits 2 having read nothing. That validation is doing its job; this stub is
    the thing that is unusual, not the rule.

    Three honest ways round it, in order of preference:

    1. Put this stub behind https. Run it with -Prefix https://localhost:14000/
       and bind a self-signed certificate to the port with
       'netsh http add sslcert ipport=127.0.0.1:14000 certhash=<thumbprint>
       appid=<guid>', then trust that certificate in LocalMachine\Root so the
       connector's handler accepts it. No secret goes in any file.
    2. Use a test build or a deliberately relaxed local configuration that
       skips the scheme check, and never let that build near a deployment.
    3. Accept that against plain http this stub exercises the crawl only
       through the unit tests, which drive the same code over FakeWebHdfs
       (tests\SqlTicketsConnector.Tests\TestSupport\FakeWebHdfs.cs).

    Whichever you pick, https here proves transport and nothing else. The stub
    still never asks for a credential, so the SSPI/Negotiate path remains
    unproven until the connector runs against a real cluster.

.EXAMPLE
    .\03-Start-LocalWebHdfsStub.ps1 -Root .\sample -Prefix http://localhost:15000/

    Exit codes: 0 stopped cleanly with Ctrl+C, 2 the parameters are unusable,
    4 the listener could not start.
#>

[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [ValidateNotNullOrEmpty()]
    [string]$Root,

    [string]$Prefix = 'http://localhost:14000/',

    [object]$PermissionMap,

    [switch]$RangerStub
)

$ErrorActionPreference = 'Stop'

# The owner is decoration. The connector never grants to a user - group
# principals only, never users, never everyone - so no owner string this stub
# reports can change what is written, and a fixed one keeps the trace readable.
$script:StubOwner = 'svc_ingest'
$script:DefaultGroup = 'hadoop-contracts-read'
$script:DefaultFilePermission = '640'
$script:DefaultDirectoryPermission = '755'

function Step([string]$name) { Write-Host "`n== $name ==" -ForegroundColor Cyan }
function Pass([string]$msg) { Write-Host "  PASS  $msg" -ForegroundColor Green }
function Fail([string]$msg) { Write-Host "  FAIL  $msg" -ForegroundColor Red }
function Warn([string]$msg) { Write-Host "  WARN  $msg" -ForegroundColor Yellow }
function Note([string]$msg) { Write-Host "  note  $msg" -ForegroundColor DarkGray }

# ---------------------------------------------------------------------------
# JSON by hand, in Apache's shapes.
#
# ConvertTo-Json is not used deliberately. The shapes below are WebHDFS's, not
# this connector's, and writing them out is what keeps them so: a stub built
# from whatever the parser happened to want would prove only that the parser
# agrees with itself. It is the same decision FakeWebHdfs.cs records.
# ---------------------------------------------------------------------------

function ConvertTo-JsonText([string]$value) {
    if ($null -eq $value) { return '""' }

    $escaped = $value.Replace('\', '\\').Replace('"', '\"')
    $escaped = $escaped.Replace("`r", '\r').Replace("`n", '\n').Replace("`t", '\t')

    return '"' + $escaped + '"'
}

function ConvertTo-EpochMilliseconds([datetime]$utc) {
    # WebHDFS reports modificationTime in milliseconds since the Unix epoch, and
    # the connector's watermark is (modification time, path), so this number is
    # what decides whether a file is seen again on the next run.
    $epoch = [datetime]::SpecifyKind([datetime]'1970-01-01T00:00:00', 'Utc')
    return [long](($utc - $epoch).TotalMilliseconds)
}

function New-RemoteExceptionJson([string]$exception, [string]$message) {
    # A real NameNode answers errors in this shape. The client only reads the
    # status code, but a test double that returned a bare body would quietly
    # stop being a fair imitation the moment anything else looked at it.
    return '{"RemoteException":{"exception":' + (ConvertTo-JsonText $exception) +
        ',"javaClassName":"org.apache.hadoop.stub","message":' + (ConvertTo-JsonText $message) + '}}'
}

# ---------------------------------------------------------------------------
# Permissions
# ---------------------------------------------------------------------------

function ConvertTo-PermissionRules([object]$map) {
    $rules = New-Object System.Collections.ArrayList
    if ($null -eq $map) { return $rules }

    $pairs = New-Object System.Collections.ArrayList

    if ($map -is [System.Collections.IDictionary]) {
        foreach ($key in $map.Keys) {
            [void]$pairs.Add(@([string]$key, [string]$map[$key]))
        }
    }
    elseif ($map -is [string]) {
        foreach ($clause in ($map -split ';')) {
            if ([string]::IsNullOrWhiteSpace($clause)) { continue }

            $equals = $clause.IndexOf('=')
            if ($equals -lt 1) {
                Fail "-PermissionMap clause '$($clause.Trim())' is not folder=group:mode"
                exit 2
            }

            [void]$pairs.Add(@($clause.Substring(0, $equals), $clause.Substring($equals + 1)))
        }
    }
    else {
        Fail "-PermissionMap must be a hashtable or a string, not $($map.GetType().Name)"
        exit 2
    }

    foreach ($pair in $pairs) {
        $folder = $pair[0].Trim().Replace('\', '/').Trim('/')
        $value = $pair[1].Trim()

        $colon = $value.LastIndexOf(':')
        if ($colon -lt 1) {
            Fail "-PermissionMap value '$value' is not group:mode, for example hadoop-contracts-read:640"
            exit 2
        }

        $groups = @($value.Substring(0, $colon).Split(',') |
            ForEach-Object { $_.Trim() } |
            Where-Object { $_ })
        $mode = $value.Substring($colon + 1).Trim()

        if ($groups.Count -eq 0) {
            Fail "-PermissionMap entry for '$folder' names no group"
            exit 2
        }

        if ($mode -notmatch '^[0-7]{3,4}$') {
            Fail "-PermissionMap mode '$mode' for '$folder' is not a POSIX octal mode"
            exit 2
        }

        [void]$rules.Add([pscustomobject]@{
            Path       = $folder
            Group      = $groups[0]
            Extra      = @($groups | Select-Object -Skip 1)
            Permission = $mode
        })
    }

    return $rules
}

function Resolve-StubPermission([string]$relative, [bool]$isDirectory) {
    $best = $null

    foreach ($rule in $script:rules) {
        $matched = ($rule.Path.Length -eq 0) -or
                   $relative.Equals($rule.Path, [StringComparison]::OrdinalIgnoreCase) -or
                   $relative.StartsWith($rule.Path + '/', [StringComparison]::OrdinalIgnoreCase)

        if ($matched -and (($null -eq $best) -or ($rule.Path.Length -gt $best.Path.Length))) {
            $best = $rule
        }
    }

    if ($null -eq $best) {
        $mode = $script:DefaultFilePermission
        if ($isDirectory) { $mode = $script:DefaultDirectoryPermission }

        return [pscustomobject]@{ Group = $script:DefaultGroup; Permission = $mode; Extra = @() }
    }

    return [pscustomobject]@{ Group = $best.Group; Permission = $best.Permission; Extra = $best.Extra }
}

# ---------------------------------------------------------------------------
# WebHDFS response bodies
# ---------------------------------------------------------------------------

function New-FileStatusJson([System.IO.FileSystemInfo]$item, [string]$relative, [bool]$includeName) {
    # -is rather than PSIsContainer: a typed parameter drops the provider's
    # added properties, and a directory silently reported as a FILE would send
    # the crawl looking for content that is not there.
    $isDirectory = $item -is [System.IO.DirectoryInfo]
    $acl = Resolve-StubPermission -relative $relative -isDirectory $isDirectory
    $name = if ($includeName) { $item.Name } else { '' }
    $length = if ($isDirectory) { 0 } else { $item.Length }

    return '{"pathSuffix":' + (ConvertTo-JsonText $name) +
        ',"type":"' + $(if ($isDirectory) { 'DIRECTORY' } else { 'FILE' }) + '"' +
        ',"length":' + $length +
        ',"modificationTime":' + (ConvertTo-EpochMilliseconds $item.LastWriteTimeUtc) +
        ',"owner":' + (ConvertTo-JsonText $script:StubOwner) +
        ',"group":' + (ConvertTo-JsonText $acl.Group) +
        ',"permission":' + (ConvertTo-JsonText $acl.Permission) + '}'
}

function New-AclStatusJson([System.IO.FileSystemInfo]$item, [string]$relative) {
    $isDirectory = $item -is [System.IO.DirectoryInfo]
    $acl = Resolve-StubPermission -relative $relative -isDirectory $isDirectory

    $entries = New-Object System.Collections.ArrayList
    foreach ($group in $acl.Extra) {
        [void]$entries.Add((ConvertTo-JsonText ('group:' + $group + ':r--')))

        # A directory on a real cluster carries the default: form as well, so
        # new files inherit the entry. The connector must ignore those: a
        # default entry describes what future files will get, not who may read
        # this one, and treating it as a grant would widen an item's ACL beyond
        # what the cluster actually allows. Emitted here so that mistake would
        # show up as an extra grant in the trace rather than never being tested.
        if ($isDirectory) {
            [void]$entries.Add((ConvertTo-JsonText ('default:group:' + $group + ':r--')))
        }
    }

    return '{"AclStatus":{"owner":' + (ConvertTo-JsonText $script:StubOwner) +
        ',"group":' + (ConvertTo-JsonText $acl.Group) +
        ',"permission":' + (ConvertTo-JsonText $acl.Permission) +
        ',"entries":[' + ($entries -join ',') + ']}}'
}

function New-ListStatusJson([string]$localPath, [string]$relative) {
    $children = @(Get-ChildItem -LiteralPath $localPath | Sort-Object -Property Name)
    $parts = New-Object System.Collections.ArrayList

    foreach ($child in $children) {
        $childRelative = if ($relative) { $relative + '/' + $child.Name } else { $child.Name }
        [void]$parts.Add((New-FileStatusJson -item $child -relative $childRelative -includeName $true))
    }

    return @{
        Body  = '{"FileStatuses":{"FileStatus":[' + ($parts -join ',') + ']}}'
        Count = $children.Count
    }
}

function New-ContentSummaryJson([string]$localPath) {
    # Answered because WebHdfsClient exposes GETCONTENTSUMMARY for the item
    # budget preflight. The shipped crawl derives its count from the walk
    # instead, so this operation may never appear in the trace.
    $files = @(Get-ChildItem -LiteralPath $localPath -Recurse -File)
    $directories = @(Get-ChildItem -LiteralPath $localPath -Recurse -Directory)
    $bytes = 0
    foreach ($file in $files) { $bytes += $file.Length }

    return '{"ContentSummary":{"directoryCount":' + $directories.Count +
        ',"fileCount":' + $files.Count +
        ',"length":' + $bytes + '}}'
}

# ---------------------------------------------------------------------------
# Serving
# ---------------------------------------------------------------------------

function Trace-Request([int]$status, [string]$op, [string]$path, [string]$detail) {
    $colour = 'Green'
    if ($status -ge 500) { $colour = 'Red' }
    elseif ($status -ge 400) { $colour = 'Yellow' }

    $line = '{0}  {1,3}  {2,-17} {3}' -f (Get-Date).ToString('HH:mm:ss'), $status, $op, $path
    if ($detail) { $line = $line + '  ' + $detail }

    Write-Host $line -ForegroundColor $colour
}

function Send-StubResponse {
    param(
        [System.Net.HttpListenerResponse]$Response,
        [int]$Status,
        [byte[]]$Body,
        [string]$ContentType = 'application/json'
    )

    $Response.StatusCode = $Status
    $Response.ContentType = $ContentType
    $Response.ContentLength64 = $Body.Length

    if ($Body.Length -gt 0) {
        $Response.OutputStream.Write($Body, 0, $Body.Length)
    }
}

function Get-QueryOperation([string]$query) {
    if ([string]::IsNullOrEmpty($query)) { return '' }

    foreach ($part in $query.TrimStart('?').Split('&')) {
        if ($part -match '^(?i)op=(.*)$') {
            return $Matches[1].ToUpperInvariant()
        }
    }

    return ''
}

# ---------------------------------------------------------------------------

Step 'Parameters'

if (-not (Test-Path -LiteralPath $Root -PathType Container)) {
    Fail "-Root '$Root' is not a folder that exists"
    exit 2
}

$separator = [System.IO.Path]::DirectorySeparatorChar
$rootFull = (Resolve-Path -LiteralPath $Root).ProviderPath.TrimEnd($separator)
Pass "serving $rootFull"

if (-not $Prefix.EndsWith('/')) {
    $Prefix = $Prefix + '/'
    Note 'appended the trailing slash HttpListener requires to -Prefix'
}

if ($Prefix -notmatch '^(?i)(https?)://([^/:]+)(?::(\d+))?/$') {
    Fail "-Prefix '$Prefix' is not http[s]://host[:port]/"
    exit 2
}

$prefixHost = $Matches[2]

if ($prefixHost -notin @('localhost', '127.0.0.1')) {
    # Fail closed. This stub answers every request without asking who is
    # asking, so a prefix bound to +, * or a routable name is an anonymous read
    # of -Root offered to the whole network. There is no legitimate reason for
    # a test double to be reachable from another machine.
    Fail "-Prefix binds '$prefixHost'. This stub authenticates nobody and must stay on loopback."
    Note 'Use http://localhost:14000/ and reach it from this machine only.'
    exit 2
}

Pass "prefix $Prefix"

# Wrapped in @() because a function returning a list unrolls it: one rule would
# otherwise arrive as a bare object and no rules as $null.
$script:rules = @(ConvertTo-PermissionRules $PermissionMap)

if ($script:rules.Count -eq 0) {
    Note "no -PermissionMap: every entry reports $script:DefaultGroup and mode 640 or 755"
}
else {
    foreach ($rule in $script:rules) {
        $groups = @($rule.Group) + @($rule.Extra)
        $readable = if ($rule.Permission.Substring($rule.Permission.Length - 2, 1) -match '[4567]') {
            'owning group reads'
        }
        else {
            'owning group cannot read - its files must be SKIPPED, not indexed'
        }
        Pass "/$($rule.Path) -> $($groups -join ', ') mode $($rule.Permission) ($readable)"
    }
    Note 'Every group here must also be in Settings:EntraGroupMap; an unresolved group is dropped,'
    Note 'and a file whose groups are all dropped has zero grants and is skipped.'
}

if ($RangerStub) {
    Pass "Ranger policy endpoint answers [] so the connector's mandatory Ranger read succeeds"
    Note 'An empty policy list means no row filter, no column mask and no deny is declared here,'
    Note 'so nothing in this session exercises the routing rules. Only a real Ranger does that.'
}
else {
    Warn 'no -RangerStub: the policy endpoint returns 404 and the connector will stop the run'
    Note 'That is correct - a source whose access policies cannot be read is never indexed.'
}

Step 'Reminders'
Note 'This is a test double. It performs NO authentication and NO authorisation.'
Note 'It does no Kerberos, so it proves nothing about the SSPI/Negotiate path. Only a real'
Note 'cluster proves that. Never point it at anything real and never expose it to a network.'

# ---------------------------------------------------------------------------

Step 'Listening'

$listener = New-Object System.Net.HttpListener
$listener.Prefixes.Add($Prefix)

try {
    $listener.Start()
}
catch [System.Net.HttpListenerException] {
    Fail "the listener could not start: $($_.Exception.Message)"

    if ($_.Exception.ErrorCode -eq 5) {
        Note 'Windows requires a URL reservation for a prefix this account does not own. Either run'
        Note 'this in an elevated session, or reserve it once:'
        Note "  netsh http add urlacl url=$Prefix user=$env:USERDOMAIN\$env:USERNAME"
        Note 'Reserve the localhost prefix exactly. A reservation for + or * would make every later'
        Note 'run of this stub reachable from the network, which is the one thing it must never be.'
    }
    else {
        Note 'Another process is probably already on this port. Pass a different -Prefix.'
    }

    exit 4
}

Write-Host "  ready on $Prefix - Ctrl+C to stop" -ForegroundColor Green
Write-Host ''

try {
    while ($listener.IsListening) {
        # BeginGetContext rather than GetContext. GetContext blocks inside the
        # HTTP stack, and PowerShell only observes Ctrl+C between statements, so
        # a blocking call would leave the listener undisposed and the port
        # reserved until the session ended. Waiting in short slices costs
        # nothing and keeps the finally block reachable.
        $pending = $listener.BeginGetContext($null, $null)

        while (-not $pending.AsyncWaitHandle.WaitOne(200)) { }

        $context = $listener.EndGetContext($pending)
        $request = $context.Request
        $response = $context.Response

        try {
            $urlPath = [Uri]::UnescapeDataString($request.Url.AbsolutePath)
            $op = Get-QueryOperation $request.Url.Query

            if ($urlPath -match '^(?i)/service/public/v2/api/service/(.+)/policy$') {
                $service = $Matches[1]

                if ($RangerStub) {
                    $body = [System.Text.Encoding]::UTF8.GetBytes('[]')
                    Send-StubResponse -Response $response -Status 200 -Body $body
                    Trace-Request 200 'RANGER' "/$service/policy" '(empty policy list)'
                }
                else {
                    $body = [System.Text.Encoding]::UTF8.GetBytes(
                        (New-RemoteExceptionJson 'NotFound' 'this stub was started without -RangerStub'))
                    Send-StubResponse -Response $response -Status 404 -Body $body
                    Trace-Request 404 'RANGER' "/$service/policy" '(start with -RangerStub)'
                }

                continue
            }

            if ($urlPath -notmatch '^(?i)/webhdfs/v1(/.*)?$') {
                $body = [System.Text.Encoding]::UTF8.GetBytes(
                    (New-RemoteExceptionJson 'NotFound' 'only /webhdfs/v1 is served here'))
                Send-StubResponse -Response $response -Status 404 -Body $body
                Trace-Request 404 '-' $urlPath
                continue
            }

            $hdfsPath = $urlPath.Substring('/webhdfs/v1'.Length)
            if ([string]::IsNullOrEmpty($hdfsPath)) { $hdfsPath = '/' }

            $relative = $hdfsPath.Trim('/')
            $localPath = $rootFull
            if ($relative) {
                $localPath = Join-Path $rootFull $relative.Replace('/', $separator)
            }

            # The last boundary this stub has. It authenticates nobody, so a
            # path containing .. must not be able to read a file outside -Root:
            # containment is checked on the resolved path, not on the text.
            $resolved = [System.IO.Path]::GetFullPath($localPath)
            $contained = $resolved.Equals($rootFull, [StringComparison]::OrdinalIgnoreCase) -or
                         $resolved.StartsWith(
                             $rootFull + $separator,
                             [StringComparison]::OrdinalIgnoreCase)

            if (-not $contained) {
                $body = [System.Text.Encoding]::UTF8.GetBytes(
                    (New-RemoteExceptionJson 'AccessControlException' 'outside the served folder'))
                Send-StubResponse -Response $response -Status 403 -Body $body
                Trace-Request 403 $op $hdfsPath '(escapes -Root)'
                continue
            }

            $item = Get-Item -LiteralPath $resolved -ErrorAction SilentlyContinue

            if ($null -eq $item) {
                # A 404 is ordinary, not fatal: the connector treats a file that
                # disappeared between the listing and the read as a skip.
                $body = [System.Text.Encoding]::UTF8.GetBytes(
                    (New-RemoteExceptionJson 'FileNotFoundException' 'no such file or directory'))
                Send-StubResponse -Response $response -Status 404 -Body $body
                Trace-Request 404 $op $hdfsPath
                continue
            }

            $isDirectory = $item -is [System.IO.DirectoryInfo]

            switch ($op) {
                'LISTSTATUS' {
                    if (-not $isDirectory) {
                        $body = [System.Text.Encoding]::UTF8.GetBytes(
                            (New-RemoteExceptionJson 'FileNotFoundException' 'not a directory'))
                        Send-StubResponse -Response $response -Status 404 -Body $body
                        Trace-Request 404 $op $hdfsPath '(not a directory)'
                        break
                    }

                    $listing = New-ListStatusJson -localPath $resolved -relative $relative
                    $body = [System.Text.Encoding]::UTF8.GetBytes($listing.Body)
                    Send-StubResponse -Response $response -Status 200 -Body $body
                    Trace-Request 200 $op $hdfsPath "($($listing.Count) entries)"
                }

                'GETFILESTATUS' {
                    $json = '{"FileStatus":' +
                        (New-FileStatusJson -item $item -relative $relative -includeName $false) + '}'
                    $body = [System.Text.Encoding]::UTF8.GetBytes($json)
                    Send-StubResponse -Response $response -Status 200 -Body $body
                    Trace-Request 200 $op $hdfsPath
                }

                'GETACLSTATUS' {
                    $acl = Resolve-StubPermission -relative $relative -isDirectory $isDirectory
                    $json = New-AclStatusJson -item $item -relative $relative
                    $body = [System.Text.Encoding]::UTF8.GetBytes($json)
                    Send-StubResponse -Response $response -Status 200 -Body $body
                    Trace-Request 200 $op $hdfsPath "($($acl.Group) $($acl.Permission))"
                }

                'GETCONTENTSUMMARY' {
                    if (-not $isDirectory) {
                        $body = [System.Text.Encoding]::UTF8.GetBytes(
                            (New-RemoteExceptionJson 'FileNotFoundException' 'not a directory'))
                        Send-StubResponse -Response $response -Status 404 -Body $body
                        Trace-Request 404 $op $hdfsPath '(not a directory)'
                        break
                    }

                    $body = [System.Text.Encoding]::UTF8.GetBytes((New-ContentSummaryJson $resolved))
                    Send-StubResponse -Response $response -Status 200 -Body $body
                    Trace-Request 200 $op $hdfsPath
                }

                'OPEN' {
                    if ($isDirectory) {
                        $body = [System.Text.Encoding]::UTF8.GetBytes(
                            (New-RemoteExceptionJson 'FileNotFoundException' 'not a file'))
                        Send-StubResponse -Response $response -Status 404 -Body $body
                        Trace-Request 404 $op $hdfsPath '(not a file)'
                        break
                    }

                    # No redirect to a DataNode. HttpFS answers OPEN itself, and
                    # the configuration prefers HttpFS for exactly that reason,
                    # so a stub that redirected would imitate the harder path
                    # without testing anything the connector does differently.
                    $bytes = [System.IO.File]::ReadAllBytes($resolved)
                    Send-StubResponse -Response $response -Status 200 -Body $bytes `
                        -ContentType 'application/octet-stream'
                    Trace-Request 200 $op $hdfsPath "($($bytes.Length) bytes)"
                }

                default {
                    $body = [System.Text.Encoding]::UTF8.GetBytes(
                        (New-RemoteExceptionJson 'UnsupportedOperationException' "op=$op is not served"))
                    Send-StubResponse -Response $response -Status 400 -Body $body
                    Trace-Request 400 $(if ($op) { $op } else { '(none)' }) $hdfsPath
                }
            }
        }
        catch {
            # One bad request must not end the session. The operator is usually
            # mid-crawl and watching, and a stub that died on a locked file
            # would look exactly like a connector bug.
            $message = $_.Exception.Message
            try {
                $json = New-RemoteExceptionJson 'IOException' $message
                $body = [System.Text.Encoding]::UTF8.GetBytes($json)
                Send-StubResponse -Response $response -Status 500 -Body $body
            }
            catch {
                Warn "the response could not be written: $($_.Exception.Message)"
            }

            Trace-Request 500 'ERROR' $request.Url.AbsolutePath "($message)"
        }
        finally {
            $response.Close()
        }
    }
}
finally {
    # Disposed here and not on the way out of the script: Ctrl+C unwinds through
    # this block, and a listener left running holds the port for the rest of the
    # session, which the next run of this script then cannot bind.
    if ($listener.IsListening) { $listener.Stop() }
    $listener.Close()
    Write-Host ''
    Write-Host 'Stub stopped and the listener released.' -ForegroundColor Cyan
}

exit 0
