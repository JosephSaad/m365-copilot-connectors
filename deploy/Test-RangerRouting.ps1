<#
.SYNOPSIS
    Answers, before any data moves, the one question a reviewer of this connector
    actually has: for each table in scope, does CdpGraphPush index it or route it
    to a live query, and which Ranger policy decided that.

.DESCRIPTION
    THIS IS A REPORT, NOT A SECOND IMPLEMENTATION. Read that sentence before
    trusting anything below it. The connector decides routing in
    src/CdpConnector.Source/Ranger/RoutingEvaluator.cs, and that C# is the only
    thing that decides what is indexed. This script re-applies the same ordered
    rules against the same policy API purely so the decisions can be READ and
    signed off in advance, on a screen, by somebody who is not going to read C#.

    It is therefore a mirror, and mirrors can drift. If this script and the
    connector ever disagree about one table, the connector's C# is authoritative
    and the disagreement is a bug in this file — report it, and do not "fix" the
    routing by editing policy to satisfy the script. Nothing here is consulted at
    run time, and nothing here can grant or refuse indexing.

    Strictly read-only. It issues one GET per Ranger service and writes nothing
    anywhere except the optional -OutputJson file.

    THE RULES, IN THE ORDER THE CONNECTOR APPLIES THEM. Each one describes a way
    that one indexed copy of a row cannot represent what the source would show
    two different people, and each one fails closed:

      1. A row filter (policyType 2) or a column mask (policyType 1) is a
         per-user transform. An index holds one copy, so it either leaks the
         unfiltered rows to everyone granted the item or stores the masked
         version and lies to the people entitled to the real one. LIVE QUERY.
      2. Any denyPolicyItems. Graph has deny ACEs, which makes mirroring a deny
         look safe, but a deny only protects while the translation is right every
         time and a translation that drifts fails open. LIVE QUERY.
      3. A grant scoped to some columns rather than ["*"]. That is a mask wearing
         different clothes: different people are entitled to different parts of
         the row. LIVE QUERY.
      4. No group granted select. Not a refusal to index so much as a refusal to
         guess: there is no principal to put on the items, and an item granted to
         nobody is indexed and then returned to no one. LIVE QUERY.

    Anything else is INDEX, with the granted groups listed — those become the
    item ACL, mapped to Entra group object IDs via Settings:EntraGroupMap. Users
    are never used as principals, and an unresolved group is dropped.

    A LIVE QUERY verdict is not a failure and not an error. It is the
    architecture: the table stays in the cluster and is queried under the user's
    own identity, where Ranger keeps enforcing the filter, the mask or the deny
    itself. So this script exits 0 whatever the verdicts are. The single
    condition that makes it exit 1 is being unable to read Ranger, which matches
    the connector: an unreadable Ranger stops a run, it never defaults to
    indexing.

    Authentication is SPNEGO as the identity you run this as. There is no
    credential in this file and there must not be one. If Ranger Admin here only
    accepts basic authentication against local users, that is a cluster-side
    setting to change (enable Kerberos on the Ranger Admin API), not a password
    to add here. Run it as the connector's service account where you can — run it
    as yourself and a pass proves Ranger answers you, which says nothing about
    whether it answers the gMSA.

.PARAMETER RangerBaseUrl
    Ranger Admin, for example https://ranger01.corp.example:6182. Defaults from
    Settings:RangerBaseUrl in -ConfigPath when that file is present.

.PARAMETER SqlService
    The Ranger service governing Hive and Impala; Settings:RangerSqlService,
    default cm_hive. One service definition covers both engines, so the verdict
    holds whichever one the ODBC driver connects to.

.PARAMETER HdfsService
    The Ranger service governing HDFS; Settings:RangerHdfsService, default
    cm_hdfs. Optional: supply it only when you are also asking about paths.

.PARAMETER Table
    One or more "database.table" names. Omit it to report on every table named
    concretely by the SQL service's own policies, which is the useful default
    when the question is "what would a crawl do to this cluster".

.PARAMETER HdfsPath
    One or more absolute HDFS paths to report on. Requires -HdfsService. Omit it
    and every path named by the HDFS policies is reported instead.

.PARAMETER OutputJson
    Writes routing-report.json: an array of {resource, verdict, reason,
    policyIds, groups}. This is the hand-off manifest. Every entry whose verdict
    is LIVE QUERY is a table the index will not contain, which means somebody has
    to build the live-query surface for it or the data is simply not findable —
    hand this file to whoever owns that work, because it is the complete list and
    it carries the policy IDs that justify each refusal. The INDEX entries are
    the other half of the hand-off: their groups are the cluster group names that
    need a row in Settings:EntraGroupMap before a crawl can grant anything.

.EXAMPLE
    .\Test-RangerRouting.ps1 -RangerBaseUrl https://ranger01.corp.example:6182

.EXAMPLE
    .\Test-RangerRouting.ps1 -Table contracts.contract, contracts.contract_ppi

.EXAMPLE
    .\Test-RangerRouting.ps1 -HdfsService cm_hdfs -HdfsPath /data/caseworks/contracts, /data/caseworks/private

.EXAMPLE
    .\Test-RangerRouting.ps1 -OutputJson .\routing-report.json
#>

[CmdletBinding()]
param(
    [string]$ConfigPath = '.\appsettings.cdphivecontracts.json',
    [string]$RangerBaseUrl,
    [string]$SqlService,
    [string]$HdfsService,
    [string[]]$Table,
    [string[]]$HdfsPath,
    [string]$OutputJson,
    [int]$TimeoutSeconds = 30
)

$ErrorActionPreference = 'Stop'

function Step([string]$name) { Write-Host "`n== $name ==" -ForegroundColor Cyan }
function Note([string]$msg) { Write-Host "  note  $msg" -ForegroundColor DarkGray }
function Warn([string]$msg) { Write-Host "  WARN  $msg" -ForegroundColor Yellow }

# ---------------------------------------------------------------------------
# Defaults from the deployed configuration
#
# Read from the same file the connector reads so the two cannot be pointed at
# different Ranger services and still both look right. The CDP appsettings files
# carry // comments, which ConvertFrom-Json rejects on Windows PowerShell 5.1, so
# whole-line comments are stripped first. Only whole-line ones: a // inside a
# string is part of a URL.
# ---------------------------------------------------------------------------

if (Test-Path $ConfigPath) {
    $json = (Get-Content $ConfigPath) | Where-Object { $_.TrimStart() -notlike '//*' }
    $config = ($json -join "`n") | ConvertFrom-Json

    if (-not $RangerBaseUrl) { $RangerBaseUrl = $config.Settings.RangerBaseUrl }
    if (-not $SqlService) { $SqlService = $config.Settings.RangerSqlService }
    if (-not $HdfsService -and $HdfsPath) { $HdfsService = $config.Settings.RangerHdfsService }

    Write-Host "Defaults from $ConfigPath"
}

if (-not $SqlService) { $SqlService = 'cm_hive' }
if ($HdfsPath -and -not $HdfsService) { $HdfsService = 'cm_hdfs' }

if (-not $RangerBaseUrl) {
    throw ('Ranger Admin is not known. Pass -RangerBaseUrl, or point -ConfigPath at a deployed ' +
        'appsettings.cdphivecontracts.json so Settings:RangerBaseUrl can supply it.')
}

$RangerBaseUrl = $RangerBaseUrl.TrimEnd('/')

# Validated before Ranger is contacted. A mistyped table name is a caller error
# rather than a routing outcome, and reporting it as one verdict among many is
# how a typo gets read as "the connector refuses that table".
$requested = @()
foreach ($name in @($Table)) {
    if ([string]::IsNullOrWhiteSpace($name)) { continue }
    $parts = $name.Split('.')
    if ($parts.Count -ne 2 -or -not $parts[0] -or -not $parts[1]) {
        throw "-Table '$name' is not database.table. Qualify every table: contracts.contract, not contract."
    }
    $requested += , @($parts[0], $parts[1])
}

if ($HdfsPath) {
    foreach ($path in $HdfsPath) {
        if ($path -notlike '/*') {
            throw "-HdfsPath '$path' is not absolute. Paths are matched against Ranger path policies as /data/..."
        }
    }
}

# Windows PowerShell 5.1 negotiates whatever the host defaults to, which on an
# unpatched Windows Server is below TLS 1.2. Ranger Admin on 6182 is TLS only,
# and the failure it produces is an opaque "underlying connection was closed"
# that reads like a network fault rather than a protocol one.
if ($PSVersionTable.PSVersion.Major -lt 6) {
    [Net.ServicePointManager]::SecurityProtocol =
        [Net.ServicePointManager]::SecurityProtocol -bor [Net.SecurityProtocolType]::Tls12
}

Write-Host "Ranger routing report — $RangerBaseUrl"
Write-Host "Running as $([Security.Principal.WindowsIdentity]::GetCurrent().Name)"
Note 'This reports what CdpGraphPush will do. It does not decide it, and it changes nothing.'

# ---------------------------------------------------------------------------
# Reading Ranger
# ---------------------------------------------------------------------------

function Get-RangerPolicy {
    <#
    .SYNOPSIS
        GETs one service's policies over SPNEGO and reduces them to what the
        routing rules read.
    .DESCRIPTION
        -UseDefaultCredentials is the whole authentication story: the running
        identity's Kerberos ticket is offered to Ranger through Negotiate. No
        credential is constructed, prompted for or held.

        A failure here is fatal by design, matching RangerPolicyClient: which
        tables carry a filter or a mask is exactly what cannot be guessed, so an
        unreadable Ranger produces no report at all rather than a cheerful one.
    #>
    param(
        [Parameter(Mandatory)][string]$BaseUrl,
        [Parameter(Mandatory)][string]$Service,
        [int]$Timeout = 30
    )

    $url = "$BaseUrl/service/public/v2/api/service/$([uri]::EscapeDataString($Service))/policy"

    try {
        $raw = Invoke-RestMethod -Uri $url -Method GET -UseDefaultCredentials -TimeoutSec $Timeout
    }
    catch {
        $status = $null
        if ($_.Exception.Response) { $status = $_.Exception.Response.StatusCode.value__ }

        Write-Host ''
        Write-Host "Could not read Ranger service '$Service': $($_.Exception.Message)" -ForegroundColor Red

        if ($status -eq 401 -or $status -eq 403) {
            Note 'Ranger refused this identity. That is exit code 3 from the connector, the same category as a'
            Note 'rejected Entra credential: the source rejected the caller. The account needs read access to the'
            Note 'policy API, and Ranger Admin must accept Kerberos — nothing here holds a password to offer it.'

            # klist is the fastest way to separate "no ticket at all" from "a
            # ticket Ranger will not accept", and the two have nothing in common.
            if (Get-Command klist.exe -ErrorAction SilentlyContinue) {
                & klist.exe | Out-Null
                if ($LASTEXITCODE -ne 0) {
                    Note 'klist reports no Kerberos tickets in this logon session. Run kinit, or run this as the service.'
                }
                else {
                    Note 'A Kerberos ticket exists, so this is authorisation rather than authentication: check the'
                    Note "Ranger role assignment for this principal, and that an SPN exists for $BaseUrl."
                }
            }
        }
        elseif ($status -eq 404) {
            Note "Ranger has no service called '$Service'. It is the Cloudera Manager service name — cm_hive, cm_hdfs —"
            Note 'and it is visible in the Ranger Admin service list. Check it against Settings:RangerSqlService.'
        }
        else {
            Note 'The connector treats this as fatal too: it stops rather than indexing a source whose access'
            Note 'policies it cannot read. Nothing about routing can be reported until Ranger answers.'
        }

        exit 1
    }

    $policies = @()
    foreach ($element in @($raw)) { $policies += , (ConvertTo-RoutingPolicy $element) }
    return , $policies
}

function Read-PolicyItem {
    <#
    .SYNOPSIS
        Reduces one policyItems array to groups and the accesses actually
        allowed.
    .DESCRIPTION
        An access whose isAllowed is false is dropped, mirroring the parser: a
        listed but unallowed access grants nothing and must not count as a grant.
    #>
    param($Element, [string]$Name)

    # Emitted unwrapped rather than as a protected array: every caller collects
    # this with @() or +=, and a comma-protected empty array collected that way
    # becomes ONE element containing nothing - which would give every policy a
    # non-empty Deny and route every table to a live query.
    $items = @()
    if (-not $Element.PSObject.Properties[$Name]) { return $items }

    foreach ($raw in @($Element.$Name)) {
        if (-not $raw) { continue }

        $accesses = @()
        foreach ($access in @($raw.accesses)) {
            if (-not $access) { continue }
            $allowed = $true
            if ($access.PSObject.Properties['isAllowed']) { $allowed = [bool]$access.isAllowed }
            if ($allowed -and $access.type) { $accesses += $access.type }
        }

        $grantsRead = @($accesses | Where-Object { $_ -eq 'read' -or $_ -eq 'select' }).Count -gt 0

        $items += , [pscustomobject]@{
            Groups     = @(@($raw.groups) | Where-Object { $_ })
            Users      = @(@($raw.users) | Where-Object { $_ })
            Accesses   = $accesses
            GrantsRead = $grantsRead
        }
    }

    return $items
}

function ConvertTo-RoutingPolicy {
    <#
    .SYNOPSIS
        One Ranger policy, reduced to the four things routing reads: what it
        covers, whether it grants read and to whom, whether it denies, and
        whether it filters or masks.
    #>
    param($Element)

    $resources = @{}
    if ($Element.resources) {
        foreach ($property in $Element.resources.PSObject.Properties) {
            $resources[$property.Name] = @(@($property.Value.values) | Where-Object { $_ })
        }
    }

    $enabled = $true
    if ($Element.PSObject.Properties['isEnabled']) { $enabled = [bool]$Element.isEnabled }

    # A masking or row-filter policy carries its items under its own name. They
    # are read into Allow because what routing needs from them is only "these
    # exist", which policyType has already said; keeping the principals lets the
    # report name who the filter was written for.
    $allow = @()
    $allow += Read-PolicyItem $Element 'policyItems'
    $allow += Read-PolicyItem $Element 'dataMaskPolicyItems'
    $allow += Read-PolicyItem $Element 'rowFilterPolicyItems'

    return [pscustomobject]@{
        Id         = [long]$Element.id
        Name       = [string]$Element.name
        Enabled    = $enabled
        PolicyType = if ($Element.PSObject.Properties['policyType']) { [int]$Element.policyType } else { 0 }
        Resources  = $resources
        Allow      = @($allow)
        Deny       = @(Read-PolicyItem $Element 'denyPolicyItems')
    }
}

# ---------------------------------------------------------------------------
# The rules
#
# Everything below is a transcription of RoutingEvaluator.EvaluateTable and
# EvaluatePath, in their order, including the reason strings word for word. The
# strings are copied rather than paraphrased on purpose: an operator comparing
# this report against a crawl log should be reading identical sentences, and a
# paraphrase is where a mirror starts to drift without anybody noticing.
# ---------------------------------------------------------------------------

$script:PolicyTypeAccess = 0
$script:PolicyTypeMasking = 1
$script:PolicyTypeRowFilter = 2

function Get-PolicyResource {
    param($Policy, [string]$Name)

    # Comma-protected, so a resource with one value stays an array here rather
    # than arriving at the caller as a bare string with a Length of its own.
    if ($Policy.Resources.ContainsKey($Name)) { return , @($Policy.Resources[$Name]) }
    return , @()
}

function Test-PolicyCovers {
    <#
    .SYNOPSIS
        Whether one resource value matches, treating a trailing * as a prefix.
    #>
    param($Policy, [string]$ResourceName, [string]$Candidate)

    foreach ($value in (Get-PolicyResource $Policy $ResourceName)) {
        if ($value -eq '*') { return $true }

        if ($value.EndsWith('*')) {
            $prefix = $value.Substring(0, $value.Length - 1)
            if ($Candidate.StartsWith($prefix, [StringComparison]::OrdinalIgnoreCase)) { return $true }
            continue
        }

        if ([string]::Equals($value, $Candidate, [StringComparison]::OrdinalIgnoreCase)) { return $true }
    }

    return $false
}

function Test-PolicyCoversPath {
    <#
    .SYNOPSIS
        Whether a path policy covers a file, treating every path value as
        covering its subtree.
    .DESCRIPTION
        That is the conservative reading, and it matches the C#. Over-matching
        can only add a grant Ranger would also have granted deeper down, or
        refuse a subtree that a deny covers — and refusing too much is the safe
        error.
    #>
    param($Policy, [string]$Path)

    foreach ($value in (Get-PolicyResource $Policy 'path')) {
        $prefix = $value.TrimEnd('*').TrimEnd('/')

        if ($prefix.Length -eq 0 -or
            [string]::Equals($Path, $prefix, [StringComparison]::OrdinalIgnoreCase) -or
            $Path.StartsWith("$prefix/", [StringComparison]::OrdinalIgnoreCase)) {
            return $true
        }
    }

    return $false
}

function Test-PolicyColumnScoped {
    <#
    .SYNOPSIS
        Whether the policy covers only some columns rather than all of them.
    .DESCRIPTION
        Only a policy covering every column, or naming no column resource at
        all, describes something safe to copy into one indexed row.
    #>
    param($Policy)

    $columns = Get-PolicyResource $Policy 'column'
    return $columns.Count -gt 0 -and @($columns | Where-Object { $_ -ne '*' }).Count -gt 0
}

function Get-GroupsGrantedRead {
    <#
    .SYNOPSIS
        The distinct groups an access policy grants read or select to.
    .DESCRIPTION
        Case-insensitive, matching Distinct(StringComparer.OrdinalIgnoreCase) in
        the C#. The comparer is spelled out because Select-Object -Unique does
        not treat case the same way across Windows PowerShell and PowerShell 7,
        and a report that lists a group twice under two spellings would be read
        as two grants.

        Groups only. A user grant is deliberately ignored everywhere in this
        connector: item ACLs carry group principals, never users and never
        everyone.
    #>
    param($Policies)

    $seen = New-Object 'System.Collections.Generic.HashSet[string]' ([StringComparer]::OrdinalIgnoreCase)
    $groups = New-Object System.Collections.ArrayList

    foreach ($policy in @($Policies | Where-Object { $_.PolicyType -eq $script:PolicyTypeAccess })) {
        foreach ($item in @($policy.Allow | Where-Object { $_.GrantsRead })) {
            foreach ($group in $item.Groups) {
                if ([string]::IsNullOrWhiteSpace($group)) { continue }
                if ($seen.Add($group)) { $null = $groups.Add($group) }
            }
        }
    }

    return , $groups.ToArray()
}

function New-Decision {
    param(
        [string]$Resource,
        [string]$Verdict,
        [string]$Reason,
        $Policies,
        [string[]]$Groups = @()
    )

    return [pscustomobject]@{
        Resource  = $Resource
        Verdict   = $Verdict
        Reason    = $Reason
        PolicyIds = @(@($Policies) | ForEach-Object { $_.Id })
        Groups    = @($Groups)
    }
}

function Get-TableDecision {
    <#
    .SYNOPSIS
        Applies the four ordered rules to one Hive or Impala table.
    #>
    param($Policies, [string]$Database, [string]$TableName)

    $resource = "$Database.$TableName"

    $relevant = @($Policies | Where-Object {
        $_.Enabled -and (Test-PolicyCovers $_ 'database' $Database) -and (Test-PolicyCovers $_ 'table' $TableName)
    })

    # Rule 1. One Ranger service definition serves Hive and Impala, so this
    # covers both engines whichever one the connector reads through.
    $transforms = @($relevant | Where-Object {
        $_.PolicyType -eq $script:PolicyTypeRowFilter -or $_.PolicyType -eq $script:PolicyTypeMasking
    })

    if ($transforms.Count -gt 0) {
        $filtered = @($transforms | Where-Object { $_.PolicyType -eq $script:PolicyTypeRowFilter }).Count -gt 0

        $reason = if ($filtered) {
            'Ranger applies a row-level filter to this table. A filter shows different rows to ' +
            'different people at query time, and an index holds one copy, so this table is queried ' +
            'live rather than indexed.'
        }
        else {
            'Ranger masks at least one column of this table. A mask shows different values to ' +
            'different people at query time, and an index holds one copy, so this table is queried ' +
            'live rather than indexed.'
        }

        return New-Decision $resource 'LIVE QUERY' $reason $transforms
    }

    # Rule 2.
    $denies = @($relevant | Where-Object { $_.Deny.Count -gt 0 })

    if ($denies.Count -gt 0) {
        return New-Decision $resource 'LIVE QUERY' (
            'Ranger denies access to this table for at least one principal. Deny rules are not mirrored ' +
            'into the index, because a mirrored deny that drifts fails open; the table is queried live ' +
            'so the source keeps enforcing its own denial.') $denies
    }

    # Rule 3.
    $columnScoped = @($relevant | Where-Object {
        (Test-PolicyColumnScoped $_) -and @($_.Allow | Where-Object { $_.GrantsRead }).Count -gt 0
    })

    if ($columnScoped.Count -gt 0) {
        return New-Decision $resource 'LIVE QUERY' (
            'Ranger grants access to some columns of this table rather than all of them. Different people ' +
            'are entitled to different parts of each row, which one indexed copy cannot represent.') $columnScoped
    }

    # Rule 4.
    $groups = Get-GroupsGrantedRead $relevant

    if ($groups.Count -eq 0) {
        return New-Decision $resource 'LIVE QUERY' (
            'No Ranger policy grants select on this table to any group. There is no principal to put on ' +
            'the indexed items, and an item granted to nobody is indexed and then returned to nobody.') $relevant
    }

    return New-Decision $resource 'INDEX' (
        "Table-wide select granted to $($groups.Count) group(s), with no row filter, mask or deny.") $relevant $groups
}

function Get-PathDecision {
    <#
    .SYNOPSIS
        Applies the path rules to one HDFS path.
    .DESCRIPTION
        Two differences from a table, both deliberate in the C#.

        A deny stops the subtree being indexed, for the same reason a table deny
        does. There is no live-query surface for a file, so the verdict word
        LIVE QUERY means only "left in HDFS, not copied" here.

        A path with no matching policy is NOT refused. The Ranger HDFS plugin
        falls back to the file's own POSIX permissions and ACL when no policy
        matches, and the connector reads those separately through GETACLSTATUS.
        An empty group list means "Ranger adds nothing", not "nobody may read
        it".
    #>
    param($Policies, [string]$Path)

    $relevant = @($Policies | Where-Object { $_.Enabled -and (Test-PolicyCoversPath $_ $Path) })
    $denies = @($relevant | Where-Object { $_.Deny.Count -gt 0 })

    if ($denies.Count -gt 0) {
        return New-Decision $Path 'LIVE QUERY' (
            'Ranger denies access to this path for at least one principal. Deny rules are not mirrored ' +
            'into the index, so nothing under it is indexed.') $denies
    }

    $groups = Get-GroupsGrantedRead $relevant

    $reason = if ($groups.Count -eq 0) {
        "No Ranger path policy matches; the file's own POSIX permissions and ACL decide."
    }
    else {
        "Ranger grants read to $($groups.Count) group(s) on this path."
    }

    return New-Decision $Path 'INDEX' $reason $relevant $groups
}

# ---------------------------------------------------------------------------
# Printing
# ---------------------------------------------------------------------------

function Split-Reason {
    param([string]$Text, [int]$Width = 86)

    $lines = New-Object System.Collections.ArrayList
    $current = ''

    foreach ($word in ($Text -split '\s+')) {
        if ($current.Length -eq 0) { $current = $word }
        elseif (($current.Length + 1 + $word.Length) -le $Width) { $current = "$current $word" }
        else { $null = $lines.Add($current); $current = $word }
    }

    if ($current.Length -gt 0) { $null = $lines.Add($current) }
    return , $lines.ToArray()
}

function Write-Verdict {
    param($Decision)

    $colour = if ($Decision.Verdict -eq 'INDEX') { 'Green' } else { 'Yellow' }

    Write-Host ''
    Write-Host ("  {0,-10} {1}" -f $Decision.Verdict, $Decision.Resource) -ForegroundColor $colour

    foreach ($line in (Split-Reason $Decision.Reason)) {
        Write-Host "             $line" -ForegroundColor Gray
    }

    if ($Decision.PolicyIds.Count -gt 0) {
        Write-Host "             decided by policy $(($Decision.PolicyIds | Sort-Object) -join ', ')" -ForegroundColor DarkGray
    }
    else {
        Write-Host '             no policy on this service covers it' -ForegroundColor DarkGray
    }

    if ($Decision.Groups.Count -gt 0) {
        Write-Host "             groups: $($Decision.Groups -join ', ')" -ForegroundColor DarkGray
    }
}

# ---------------------------------------------------------------------------
# The report
# ---------------------------------------------------------------------------

Step "Hive and Impala — service '$SqlService'"

$sqlPolicies = Get-RangerPolicy -BaseUrl $RangerBaseUrl -Service $SqlService -Timeout $TimeoutSeconds
Write-Host "  read $($sqlPolicies.Count) polic(y/ies)"

$disabled = @($sqlPolicies | Where-Object { -not $_.Enabled }).Count
if ($disabled -gt 0) {
    Note "$disabled of them are disabled and decide nothing, here or in the connector."
}

if ($requested.Count -eq 0) {
    # Every table the policies name concretely. A wildcard names no table, so a
    # service whose policies are all wildcards produces an empty list rather than
    # a guess — the tables exist, but only the metastore knows their names.
    $wildcards = 0
    $seenTables = New-Object 'System.Collections.Generic.HashSet[string]' ([StringComparer]::OrdinalIgnoreCase)

    foreach ($policy in @($sqlPolicies | Where-Object { $_.Enabled })) {
        foreach ($database in (Get-PolicyResource $policy 'database')) {
            foreach ($tableName in (Get-PolicyResource $policy 'table')) {
                if ($database.Contains('*') -or $tableName.Contains('*')) { $wildcards++; continue }
                if ($seenTables.Add("$database.$tableName")) { $requested += , @($database, $tableName) }
            }
        }
    }

    Write-Host "  no -Table given, so reporting on the $($requested.Count) table(s) the policies name"
    if ($wildcards -gt 0) {
        Note "$wildcards policy resource value(s) are wildcards and name no table. Pass -Table for anything"
        Note 'covered only by a wildcard policy — Ranger cannot be asked which tables exist, only the metastore can.'
    }
}

$decisions = New-Object System.Collections.ArrayList

foreach ($pair in $requested) {
    $decision = Get-TableDecision $sqlPolicies $pair[0] $pair[1]
    Write-Verdict $decision
    $null = $decisions.Add($decision)
}

if ($requested.Count -eq 0) {
    Warn 'No table was reported on. Pass -Table with the tables the crawl is meant to cover.'
}

# ---------------------------------------------------------------------------

if ($HdfsService) {
    Step "HDFS — service '$HdfsService'"

    $hdfsPolicies = Get-RangerPolicy -BaseUrl $RangerBaseUrl -Service $HdfsService -Timeout $TimeoutSeconds
    Write-Host "  read $($hdfsPolicies.Count) polic(y/ies)"

    $paths = @($HdfsPath | Where-Object { $_ })

    if ($paths.Count -eq 0) {
        $seenPaths = New-Object 'System.Collections.Generic.HashSet[string]' ([StringComparer]::OrdinalIgnoreCase)
        foreach ($policy in @($hdfsPolicies | Where-Object { $_.Enabled })) {
            foreach ($value in (Get-PolicyResource $policy 'path')) {
                $trimmed = $value.TrimEnd('*').TrimEnd('/')
                if ($trimmed.Length -gt 0 -and $seenPaths.Add($trimmed)) { $paths += $trimmed }
            }
        }
        Write-Host "  no -HdfsPath given, so reporting on the $($paths.Count) path(s) the policies name"
    }

    foreach ($path in $paths) {
        $decision = Get-PathDecision $hdfsPolicies $path
        Write-Verdict $decision
        $null = $decisions.Add($decision)

        if ($decision.Verdict -eq 'INDEX' -and $decision.Groups.Count -eq 0) {
            Write-Host "             Ranger grants nothing here; GETACLSTATUS and the POSIX group bits decide" -ForegroundColor DarkGray
        }
    }

    if ($paths.Count -eq 0) {
        Warn 'No path was reported on. Pass -HdfsPath with the document roots the crawl is meant to cover.'
    }
}

# ---------------------------------------------------------------------------
# What it found
# ---------------------------------------------------------------------------

$indexed = @($decisions | Where-Object { $_.Verdict -eq 'INDEX' })
$live = @($decisions | Where-Object { $_.Verdict -eq 'LIVE QUERY' })

Step 'Result'
Write-Host ("  {0,-10} {1}" -f 'INDEX', $indexed.Count) -ForegroundColor Green
Write-Host ("  {0,-10} {1}" -f 'LIVE QUERY', $live.Count) -ForegroundColor Yellow

if ($live.Count -gt 0) {
    Write-Host ''
    Write-Host '  Refused, and each refusal is a decision rather than a fault:' -ForegroundColor Yellow
    foreach ($decision in $live) { Write-Host "    $($decision.Resource)" -ForegroundColor Yellow }
    Note 'None of these reaches the index. If they need to be findable, they need a live-query surface,'
    Note 'which is a separate piece of work and the reason -OutputJson exists.'
}

$allGroups = New-Object 'System.Collections.Generic.HashSet[string]' ([StringComparer]::OrdinalIgnoreCase)
foreach ($decision in $indexed) {
    foreach ($group in $decision.Groups) { $null = $allGroups.Add($group) }
}

if ($allGroups.Count -gt 0) {
    Write-Host ''
    Write-Host "  $($allGroups.Count) cluster group(s) will be stamped on items: $(($allGroups) -join ', ')"
    Note 'Each one needs an entry in Settings:EntraGroupMap ("name=guid;name=guid"), or a directory lookup with'
    Note 'Settings:ResolveGroupsFromDirectory. An unresolved group is DROPPED, and an item left with zero grants'
    Note 'is SKIPPED rather than written — so a missing mapping shows up as missing items, not as an error.'
}

if ($OutputJson) {
    $manifest = @($decisions | ForEach-Object {
        [pscustomobject]@{
            resource  = $_.Resource
            verdict   = $_.Verdict
            reason    = $_.Reason
            policyIds = @($_.PolicyIds)
            groups    = @($_.Groups)
        }
    })

    # -Depth covers the two nested arrays. ConvertTo-Json emits an object rather
    # than an array for a single decision on 5.1, so the array is forced.
    $text = ConvertTo-Json -InputObject @($manifest) -Depth 4
    Set-Content -Path $OutputJson -Value $text -Encoding UTF8

    Write-Host ''
    Write-Host "  wrote $($manifest.Count) entr(y/ies) to $OutputJson"
    Note 'The hand-off manifest. Every LIVE QUERY entry is a resource the index will not hold, with the policy IDs'
    Note 'that justify it; hand the file to whoever builds the live-query surface. It is a snapshot of Ranger as of'
    Note 'now, so re-run it after any policy change rather than treating it as a standing contract.'
}

Write-Host ''
Note 'Verify the same tables against a real run with: CdpGraphPush.exe --connector cdphivecontracts --dry-run,'
Note 'which reads and maps but writes nothing to Graph and does not advance the watermark. If that run routes a'
Note 'table differently from this report, the connector is right and this script is wrong — raise it as a bug.'

# A refusal to index is a routing outcome, not a failure, so the verdicts never
# change the exit code. The only exit 1 is the unreadable Ranger above.
exit 0
