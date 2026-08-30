#Requires -Version 5.1
<#
.SYNOPSIS
    Proves the dashboard's ReaderGroups policy admits a member and refuses a
    non-member. This is live test L4 in docs/GO-LIVE-READINESS.md.

.DESCRIPTION
    RUN THIS FROM AN ELEVATED POWERSHELL. It edits the deployed appsettings.json
    and restarts the application pool, and both need administrator rights.

    THE NEGATIVE CASE IS THE POINT. An authorization rule that admits everybody
    behaves exactly like a correct one until somebody who should have been
    refused is not, and there is no error and no log line when that happens. So
    this script is not finished when a member gets in; it is finished when a
    non-member is kept out.

    WHY SIDs AND NOT GROUP NAMES, by default. A Windows identity's role claims
    are group SIDs - S-1-5-32-545, not BUILTIN\Users. A name still matches when
    the request principal is a WindowsPrincipal, whose IsInRole resolves a name
    before comparing, and does not when it arrives as a plain ClaimsPrincipal
    comparing claim values literally. Which one IIS supplies is a property of the
    host and cannot be answered by any test in this repository, so the default
    here is the spelling that works either way. Pass -AlsoTestGroupName to settle
    it for this host.

    The defaults need no domain and no second account: S-1-5-32-545 (BUILTIN\
    Users) is a group essentially every interactive account is in, and
    S-1-5-32-551 (BUILTIN\Backup Operators) is one almost nobody is in. Check
    both against your own token before trusting the result - the script does
    this for you and refuses to run if the assumption is wrong.

    It restores the original ReaderGroups value on the way out, including when a
    step throws.

.PARAMETER MemberSid
    A group the signed-in account IS in. Expect 200.

.PARAMETER NonMemberSid
    A group the signed-in account is NOT in. Expect 403.

.EXAMPLE
    .\Test-DashboardAuthorization.ps1
#>
[CmdletBinding()]
param(
    [string]$SiteName     = 'ConnectorState',
    [string]$PhysicalPath = 'C:\inetpub\ConnectorState',
    [string]$Url          = 'https://localhost:8443/',
    [string]$MemberSid    = 'S-1-5-32-545',
    [string]$NonMemberSid = 'S-1-5-32-551',
    [switch]$AlsoTestGroupName,
    [int]$SettleSeconds   = 4
)

$ErrorActionPreference = 'Stop'

if (-not ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()
        ).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    throw 'Not elevated. This edits the deployed configuration and restarts an app pool; re-run from an administrator PowerShell.'
}

$config = Join-Path $PhysicalPath 'appsettings.json'
if (-not (Test-Path $config)) { throw "No deployed configuration at $config. Run Install-Dashboard-IIS.ps1 first." }

Import-Module WebAdministration

function Name-Of([string]$sid) {
    try { (New-Object Security.Principal.SecurityIdentifier($sid)).Translate([Security.Principal.NTAccount]).Value }
    catch { $sid }
}

# CHECK THE ASSUMPTION BEFORE TESTING AGAINST IT. If the account is not actually
# in MemberSid, a 403 proves nothing about the policy; if it IS in NonMemberSid,
# neither does a 200. Either way the run would produce a confident wrong answer.
$mine = ([Security.Principal.WindowsIdentity]::GetCurrent()).Groups |
    ForEach-Object { $_.Value }

if ($mine -notcontains $MemberSid) {
    throw "This account is not in $MemberSid ($(Name-Of $MemberSid)), so it cannot be the member case. Pass -MemberSid with a group you are in."
}
if ($mine -contains $NonMemberSid) {
    throw "This account IS in $NonMemberSid ($(Name-Of $NonMemberSid)), so it cannot be the non-member case. Pass -NonMemberSid with a group you are not in."
}

# ABSENT AND EMPTY ARE DIFFERENT, and the difference broke the first version of
# this script. A deployed appsettings.json published before ReaderGroups existed
# has no such property, and PowerShell will not create one by assignment -
# `$json.CrawlState.ReaderGroups = ...` throws "the property cannot be found".
# Worse, reading it back gives $null, whose .Count is 0, so a check for "is it
# empty" cannot tell the two apart and reports a reassuring zero either way.
$crawlState = (Get-Content $config -Raw | ConvertFrom-Json).CrawlState
$hadProperty = [bool]($crawlState.PSObject.Properties | Where-Object Name -eq 'ReaderGroups')
$original = if ($hadProperty) { $crawlState.ReaderGroups } else { $null }

Write-Host $(if ($hadProperty) {
    "Deployed ReaderGroups before this run: [$($original -join ', ')]"
} else {
    'Deployed configuration has no ReaderGroups property; it will be added and removed again.'
})
Write-Host "Testing as $(([Security.Principal.WindowsIdentity]::GetCurrent()).Name)"
Write-Host ''

# $null removes the property; an array sets it. Add-Member -Force rather than
# assignment, so this works whether or not the deployed file already has the key.
function Set-ReaderGroups($groups) {
    $json = Get-Content $config -Raw | ConvertFrom-Json

    if ($null -eq $groups) {
        $json.CrawlState.PSObject.Properties.Remove('ReaderGroups')
    }
    else {
        $json.CrawlState | Add-Member -NotePropertyName 'ReaderGroups' -NotePropertyValue @($groups) -Force
    }

    $json | ConvertTo-Json -Depth 20 | Set-Content -Path $config -Encoding UTF8

    # The policy is built once at startup, so a configuration reload is not
    # enough - the process has to come back.
    Restart-WebAppPool -Name $SiteName
    Start-Sleep -Seconds $SettleSeconds
}

function Get-Status {
    try { (Invoke-WebRequest -Uri $Url -UseDefaultCredentials -TimeoutSec 30 -UseBasicParsing).StatusCode }
    catch {
        if ($_.Exception.Response) { [int]$_.Exception.Response.StatusCode }
        else { throw }
    }
}

$results = New-Object System.Collections.Generic.List[object]

try {
    foreach ($case in @(
        @{ Label = "member    $MemberSid ($(Name-Of $MemberSid))";        Groups = @($MemberSid);    Expect = 200 }
        @{ Label = "non-member $NonMemberSid ($(Name-Of $NonMemberSid))"; Groups = @($NonMemberSid); Expect = 403 }
    )) {
        Set-ReaderGroups $case.Groups
        $status = Get-Status
        $ok = $status -eq $case.Expect

        $results.Add([pscustomobject]@{
            Case = $case.Label; Expected = $case.Expect; Actual = $status; Verdict = if ($ok) { 'PASS' } else { 'FAIL' }
        })

        Write-Host ("  {0,-6} {1}  expected {2}, got {3}" -f
            $(if ($ok) { 'PASS' } else { 'FAIL' }), $case.Label, $case.Expect, $status) `
            -ForegroundColor $(if ($ok) { 'Green' } else { 'Red' })
    }

    if ($AlsoTestGroupName) {
        # Settles the open question in ReaderPolicy: does a NAME match, or only
        # a SID? 200 means IIS supplies a WindowsPrincipal and names resolve.
        $name = Name-Of $MemberSid
        Set-ReaderGroups @($name)
        $status = Get-Status

        Write-Host ''
        Write-Host ("  name form '$name' -> $status  ({0})" -f
            $(if ($status -eq 200) { 'names resolve on this host' } else { 'names do NOT resolve; configure SIDs' })) `
            -ForegroundColor Cyan

        $results.Add([pscustomobject]@{
            Case = "name form $name"; Expected = 'either'; Actual = $status
            Verdict = if ($status -eq 200) { 'names work' } else { 'SIDs required' }
        })
    }
}
finally {
    # THE RESTORE MUST NOT THROW. When the first version failed, this block
    # failed the same way a line later and the message a reader saw was the
    # restore's, not the one that actually stopped the run - the second error
    # standing in front of the first.
    Write-Host ''
    Write-Host 'Restoring the deployed configuration.'

    try {
        Set-ReaderGroups $original
        Write-Host $(if ($hadProperty) {
            "  restored to [$($original -join ', ')], pool restarted"
        } else {
            '  ReaderGroups removed again, pool restarted'
        })
    }
    catch {
        Write-Host "  RESTORE FAILED: $($_.Exception.Message)" -ForegroundColor Red
        Write-Host "  Put $config back by hand before leaving the site in this state." -ForegroundColor Red
    }
}

Write-Host ''
$results | Format-Table -AutoSize | Out-String | Write-Host

if ($results | Where-Object Verdict -eq 'FAIL') {
    Write-Host 'L4 FAILED. A 200 for the non-member means the rule is not biting; a 401 anywhere means authentication failed rather than authorization, which is an IIS problem and not this feature.' -ForegroundColor Red
    exit 1
}

Write-Host 'L4 PASSED: the member was admitted and the non-member was refused.' -ForegroundColor Green
exit 0
