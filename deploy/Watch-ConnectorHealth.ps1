#Requires -Version 5.1
<#
.SYNOPSIS
    Polls connection health - over GET /health or straight off
    crawl.vwConnectionHealth - decides what is alert-worthy, writes the verdict
    to the Windows Event Log, and exits with a code a scheduler can page on.
    This is the thing that notices the connector has stopped.

.DESCRIPTION
    WHY THIS FILE EXISTS.

    When this connector dies, search does not go down - it goes stale. Graph
    keeps serving the last-pushed items indefinitely, so a stopped scheduled task
    produces no outage, no error page, no slow query and no user complaint. The
    index answers every search exactly as well as it did the day the connector
    stopped. What stops happening is the two things that only a running crawl can
    do: deletions stop propagating, and permission revocations stop propagating.
    A terminated employee's access removal and a deleted customer record both
    stay searchable for the whole outage plus one crawl.

    So this is a SECURITY control wearing a monitoring control's clothes, and
    that changes the design in one specific way: a watchdog that only notices
    when something errors is useless here, because the dangerous state is
    silence. Every default below is chosen so that the quiet outcomes - nothing
    ran, nothing answered, nothing is registered - are loud, and only a
    measured, in-date, positive answer is quiet.

    GET /health and crawl.vwConnectionHealth both already existed when this was
    written. Neither was polled by anything. That is the whole gap: the estate
    had two good answers and no question.

    WHY IT DOES NOT TRUST THE HEALTH WORD FOR THE STALENESS CHECK, WHICH IS THE
    SINGLE MOST IMPORTANT DECISION IN THIS FILE.

    The obvious implementation is "alert when crawl.vwConnectionHealth says
    late". It does not work, and it fails silently in the exact scenario this
    script exists for. Three separate reasons, all read out of the CASE
    expression in sql/22 and one of them measured against a live database:

      1. The 'late' arm is gated on `c.ExpectedIntervalMinutes IS NOT NULL`.
         Nothing requires that column to be populated, and on the estate this
         was developed against it is NULL - crawl.Connection holds one row,
         'consultingwork', with ExpectedIntervalMinutes NULL, and the view
         returns 'healthy' for it. On that connection the word 'late' is
         unreachable. A watchdog keyed on it would have sat green through an
         indefinitely dead scheduler, which is precisely the failure it was
         installed to catch.

      2. 'failing' outranks 'late' in the CASE. A connection that has been
         failing for a month reports 'failing' and never reports 'late', so a
         rule that pages on lateness alone never sees the longest outages.

      3. 'items refused' outranks 'late' too, and a partial run is NOT a success
         (the LastSuccess CTE is `WHERE Status = 2`, and partial is 5). A
         connection stuck partial therefore accumulates staleness while
         permanently displaying 'items refused'.

    So the staleness test here is computed from `minutesSinceLastSuccess`
    against `expectedIntervalMinutes`, with an explicit fallback for the
    unconfigured case, and it runs INDEPENDENTLY of the health word. The health
    word is still read, still reported and still drives the other conditions -
    this script never re-derives it, because sql/22 owns that rule and a second
    copy of it here would be free to disagree with the dashboard on the one
    afternoon somebody is comparing the two. But it is not the freshness signal,
    because by construction it cannot be.

    HTTP 200 IS NOT HEALTHY, AND THIS SCRIPT NEVER TREATS IT AS SUCH.

    HealthEndpoint.cs answers 200 with an unhealthy body and reserves 503 for
    "this process could not read crawl state". That is deliberate and its header
    explains why: a failing connection and a dead dashboard must not arrive as
    the same red. The consequence for a consumer is that the status LINE carries
    almost no information and the BODY carries all of it. A check written as
    "Invoke-WebRequest, no exception, pass" is blind to every condition the
    endpoint exists to report. This script therefore treats a 200 as nothing more
    than permission to parse, and takes its verdict entirely from the payload.

    IT ALSO CHECKS THAT THE PAYLOAD IS FRESH. The endpoint sends Cache-Control:
    no-store specifically so that no intermediary can answer a monitor with a
    verdict from ten minutes ago, and it publishes generatedUtc specifically so
    that a monitor can tell. Nothing was checking it. A cached 200 body is the
    most dangerous single response this script can receive - it is green, it is
    well-formed, and it is a lie - so generatedUtc older than
    -MaxPayloadAgeMinutes is treated as an unusable source rather than as data.
    The same test catches clock skew between this host and the dashboard host,
    which is worth knowing about for its own sake.

    WHY THE EVENT LOG RATHER THAN A NEW CHANNEL.

    Item 8 of section 7 of GO-LIVE-READINESS.md is "route the alert-worthy events
    to people", and its point is that the events already exist and land in a log
    or a table that nobody reads. Adding a fourth destination would make that
    worse. The Event Log is already this system's convergence point: sql/27 and
    sql/32 register their SQL Agent jobs with @notify_level_eventlog = 2, and the
    Serilog Event Log sink is already a packaged dependency of the connector. It
    is also the one sink every Windows monitoring agent can subscribe to with no
    bespoke integration at all - SCOM, Zabbix, the Azure Monitor agent, Datadog
    and NSClient++ all read event logs out of the box. Writing here means the
    paging matrix in docs/ALERTING.md is implemented by SUBSCRIPTION rules in one
    place rather than by five integrations.

    Event IDs are a contract - a monitoring rule is written against a number, and
    a number reused for a different meaning breaks that rule silently. They are
    listed in docs/ALERTING.md and must not be repurposed.

    EVENT ID 9000 IS EMITTED ON SUCCESS, ON PURPOSE, AND IT IS NOT NOISE. It is
    the heartbeat, and it is the only thing that makes a dead watchdog
    detectable. A watchdog that logs only when it finds something wrong is
    indistinguishable, from outside, from a watchdog that has been disabled,
    deleted, or is on a host that is switched off. See the "WHAT WATCHES THE
    WATCHDOG" note further down, and section 6 of docs/ALERTING.md, which gives
    the customer-side rule that has to be written against the ABSENCE of this
    event.

    IT NEVER FAILS QUIET. Every path that could end in "no answer" ends in a
    non-zero exit and an Error-level event instead: an unreachable host, a
    refused connection, a 401 because the watch account fell out of
    CrawlState:ReaderGroups, a 404 because the deployed build predates the
    endpoint, an HTML body where JSON was expected, a stale payload, zero
    connections registered, and the script's own unhandled errors. "The check
    could not run" and "the check found nothing" must never produce the same Last
    Run Result - the same sentence Test-TriggerHealth.ps1 is built around, and
    the same reason.

    WHY IT SPEAKS BOTH HTTP AND SQL, AND WHY IT NEVER FAILS OVER BETWEEN THEM.

    The dashboard is an optional component - Install-Dashboard-IIS.ps1 is a
    separate install and an estate can run connectors without it. A watchdog that
    only speaks HTTP cannot be deployed there, and "the watchdog needs a web site
    first" is exactly the prerequisite that ends with no watchdog installed at
    all. The SQL path exists for that case. It reads crawl.vwConnectionHealth
    read-only and needs nothing but SELECT.

    They are not equivalent and the difference matters when choosing. The HTTP
    path proves IIS, the app pool, the reader policy, the dashboard process and
    crawl state are all alive, in one request; the SQL path proves only that
    crawl state is readable. HTTP is therefore the stronger check and is used
    whenever -HealthUrl is supplied.

    There is deliberately NO runtime failover from HTTP to SQL. It is the obvious
    convenience and it would destroy the control: a dashboard that has been down
    for a month would be silently papered over by the SQL path every fifteen
    minutes, and the estate would believe it had a working /health. The source is
    selected once, at startup, from the parameters. If the selected source cannot
    answer, that is the alert.

    WHAT WATCHES THE WATCHDOG - THE HONEST ANSWER.

    Nothing in this repository can, and no code that lives on this host can,
    because the failure mode is "this code did not run". If the task is disabled,
    deleted, or the host is off, there is no instruction of mine left executing
    to report it. Three things close as much of it as can be closed from here,
    and the third is a gap that has to be closed elsewhere:

      1. The heartbeat. Event 9000 on every clean run, plus lastRunUtc in the
         state file, means the ABSENCE of a recent heartbeat is detectable.
      2. The scheduled task's own record. Task Scheduler keeps LastTaskResult and
         LastRunTime, and Get-ScheduledTaskInfo reads them without elevation, so
         "the task exists, is enabled, and ran recently" is a second cheap check.
      3. The gap: if the host is powered off, neither of those is readable from
         the host. That can only be closed by something OFF this box - a
         dead-man rule in the customer's monitoring system on the absence of
         event 9000, or Windows Event Forwarding to a collector. Section 6 of
         docs/ALERTING.md gives the rule to write. This script cannot write it,
         and pretending otherwise would be the same silent failure one level up.

    EXIT CODES ARE MONOTONIC IN SEVERITY, WHICH IS THE PROPERTY THAT MAKES THEM
    USEFUL. 0 clean, 1 ticket, 2 page, 3 source unanswerable, 4 the watchdog
    itself broken. So the two rules an operator has to write are "not zero means
    look" and "two or more means page", and neither has to enumerate codes. The
    full table is in docs/ALERTING.md section 5.

.PARAMETER HealthUrl
    The dashboard's health endpoint, for example https://sqlprod01:8443/health.
    Supplying it selects the HTTP source unless -Source says otherwise.

    It must be https. HealthEndpoint.cs warns that Program.cs installs
    UseHttpsRedirection, so an http:// probe receives a 307 and a check that does
    not follow redirects records that as a failure indistinguishable from the
    site being down. Rather than follow the redirect - which would let a
    misconfigured watch appear to work - an http:// URL is refused outright
    unless -AllowInsecureHttp is typed.

.PARAMETER Source
    Auto, Http or Sql. Auto - the default - means HTTP when -HealthUrl was
    supplied and SQL otherwise. This is a selection, not a fallback: see the
    description.

.PARAMETER SqlInstance
    The instance holding ConnectorState, for the SQL source. Default localhost.

.PARAMETER Database
    The crawl state database. Default ConnectorState.

.PARAMETER MissedIntervalFactor
    How many expected intervals a connection may go without a SUCCESSFUL run
    before this pages. Default 3.

    sql/22 turns the pill amber at 2 intervals. This is deliberately looser,
    because the dashboard's word and a paging threshold are different jobs: 2 is
    right for "show me amber", and a page at 2 fires on a single missed run - a
    patch reboot, one Graph 429 - which is the fastest way to get a watchdog
    muted. Muted is worse than absent, because it still looks installed.

.PARAMETER MinimumStaleMinutes
    A floor under the staleness threshold, in minutes. Default 60.

    Without it, a connection on a five-minute cadence pages after fifteen
    minutes, and one host reboot pages. The threshold actually used is
    max(expectedIntervalMinutes * MissedIntervalFactor, MinimumStaleMinutes).

.PARAMETER MaxMinutesSinceSuccess
    The staleness threshold used when a connection has no
    ExpectedIntervalMinutes configured. Default 1440, one day.

    This is the parameter that closes the hole described in the description: with
    that column NULL the database can never say 'late', so without a fallback
    here a dead scheduler is invisible in both sources at once. One day is chosen
    to be defensible rather than tight - it is the point past which "deletions
    have not propagated" is worth waking somebody for, on an estate that has not
    told us what its cadence is. An estate that HAS told us gets the interval
    arithmetic instead, which is far better, and the NoExpectedInterval finding
    exists to nag until it does.

.PARAMETER MaxPayloadAgeMinutes
    How old GET /health's generatedUtc may be before the payload is treated as
    unusable rather than as data. Default 10. HTTP source only.

.PARAMETER FailuresToPage
    Consecutive failed runs at which 'failing' becomes a page rather than a
    ticket. Default 2.

    One failed run is very often transient - a Graph 429 or 503, a source
    restart, a network blip - and the connector retries on its next firing. Two
    consecutive means the retry did not work, which is a trend rather than an
    event. This debounce is only safe because the staleness check above is NOT
    debounced and is not gated on the failure count: a connection failing on a
    weekly cadence would take a fortnight to reach two failures, and the
    staleness threshold pages it on time regardless.

.PARAMETER ConsecutivePolls
    How many consecutive polls a self-clearing condition must be observed on
    before it counts. Default 2.

    Applies only to conditions that are normal for part of a run - 'deletes
    pending' is non-zero for a few seconds of every sweep, and a single 'items
    refused'/partial run is retried automatically because a refused write records
    no hash. Paging on those every time is how an alert becomes wallpaper. It
    does NOT apply to staleness, to never-having-succeeded, to an unusable
    source, or to an empty estate, because those are not transient and treating
    them as if they might be would make the control quieter than the failure.

.PARAMETER TimeoutSeconds
    Request and command timeout. Default 30.

.PARAMETER StatePath
    Where the debounce state and heartbeat are kept. Defaults to
    %ProgramData%\ConnectorState\health-watch\state.json.

    If it cannot be read or written the script does not become quieter: it
    disables demotion - so every finding counts at full severity - raises a
    ticket-level finding saying so, and carries on. Losing the ability to
    debounce must cost noise, never coverage.

.PARAMETER EventLogName
    Which log to write to. Default Application.

.PARAMETER EventSource
    The event source to write as. Default ConnectorState. Creating it needs
    administrator rights and is done by -Register.

.PARAMETER NoEventLog
    Do not write to the Event Log at all. For a manual run at a console, where
    the report on stdout is the output and an event would be noise in the
    monitoring system. Never set this on the scheduled task - the exit code alone
    tells a monitor a number and no reason.

.PARAMETER AllowUntrustedServerCertificate
    Skip TLS certificate validation, for the HTTPS probe and for the SQL
    connection. Off, and it stays off unless somebody types it - same stance as
    Test-TriggerHealth.ps1. An instance or a site with a self-signed certificate
    is a development one, and a scheduled task that silently accepts any
    certificate is a scheduled task that will accept the wrong one. Encryption
    itself is not negotiable and is set either way.

.PARAMETER AllowInsecureHttp
    Permit an http:// -HealthUrl. Only for a lab or a stub. See -HealthUrl for
    what goes wrong in production.

.PARAMETER Register
    Register the Event Log source, create the state directory, and register the
    Windows scheduled task that runs this script. Does not perform a watch in the
    same invocation - see the note above the registration block for why.

.PARAMETER TaskName
    The scheduled task to create under -Register. Default 'ConnectorState health
    watch'.

.PARAMETER EveryMinutes
    How often the registered task runs. Default 15.

.PARAMETER RunAsUser
    The identity the registered task runs as. Default SYSTEM. Accepts SYSTEM,
    NETWORK SERVICE, LOCAL SERVICE and a group managed service account
    (DOMAIN\name$). An ordinary domain account needs a password, this script
    takes no interactive input by design, and so it prints the schtasks.exe
    command to run rather than half-registering a task that cannot start.

.PARAMETER Force
    Under -Register, replace an existing task of the same name.

.EXAMPLE
    # One watch against the dashboard, as the current user, reporting to stdout.
    .\Watch-ConnectorHealth.ps1 -HealthUrl https://sqlprod01:8443/health

.EXAMPLE
    # No dashboard in this estate. Read crawl state directly, read-only.
    .\Watch-ConnectorHealth.ps1 -Source Sql -SqlInstance SQLPROD01

.EXAMPLE
    # Install it: event source, state directory, and a task every 15 minutes as
    # SYSTEM. Elevated.
    .\Watch-ConnectorHealth.ps1 -Register -HealthUrl https://sqlprod01:8443/health -EveryMinutes 15

.EXAMPLE
    # The scheduled task's action, if registering it by hand.
    powershell -NoProfile -ExecutionPolicy Bypass -File C:\connectors\deploy\Watch-ConnectorHealth.ps1 -HealthUrl https://sqlprod01:8443/health
#>
[CmdletBinding()]
param(
    [string]$HealthUrl,
    [ValidateSet('Auto', 'Http', 'Sql')]
    [string]$Source                       = 'Auto',
    [string]$SqlInstance                  = 'localhost',
    [string]$Database                     = 'ConnectorState',
    [ValidateRange(1, 100)]
    [int]$MissedIntervalFactor            = 3,
    [ValidateRange(0, 10080)]
    [int]$MinimumStaleMinutes             = 60,
    [ValidateRange(1, 525600)]
    [int]$MaxMinutesSinceSuccess          = 1440,
    [ValidateRange(1, 1440)]
    [int]$MaxPayloadAgeMinutes            = 10,
    [ValidateRange(1, 1000)]
    [int]$FailuresToPage                  = 2,
    [ValidateRange(1, 100)]
    [int]$ConsecutivePolls                = 2,
    [ValidateRange(5, 600)]
    [int]$TimeoutSeconds                  = 30,
    # NOT defaulted with $PSScriptRoot or any other automatic variable. Under
    # Windows PowerShell 5.1 $PSScriptRoot is empty while param() defaults are
    # being evaluated and populated afterwards, so a path built from it here
    # silently resolves relative to the caller's working directory on 5.1 and
    # correctly on 7. This repository has been bitten by that before. Anything
    # derived goes below the param block, where both versions agree.
    [string]$StatePath,
    [string]$EventLogName                 = 'Application',
    [string]$EventSource                  = 'ConnectorState',
    [switch]$NoEventLog,
    [switch]$AllowUntrustedServerCertificate,
    [switch]$AllowInsecureHttp,
    [switch]$Register,
    [string]$TaskName                     = 'ConnectorState health watch',
    [ValidateRange(1, 1440)]
    [int]$EveryMinutes                    = 15,
    [string]$RunAsUser                    = 'SYSTEM',
    [switch]$Force
)

# THIS FILE IS DELIBERATELY PURE ASCII AND CARRIES NO BYTE ORDER MARK, which is
# the one convention here that differs from most of deploy/. Windows PowerShell
# 5.1 reads a BOM-less file as the system ANSI code page, so any file with a
# curly quote or an em dash in it needs the BOM or 5.1 misreads those bytes -
# which is why fifteen of the sixteen scripts in this directory have one. Having
# no character above 0x7F removes the dependency entirely rather than managing
# it, so this file reads identically under either version and under any code
# page. Test-TriggerHealth.ps1, the other unattended scheduled check here, is
# written the same way. Keep it that way: a smart quote pasted into a comment
# would break this script under 5.1 and nowhere else, and the symptom would be a
# parse error in production on the host that has no dashboard.

# Stop rather than continue, so an unanticipated failure lands in the trap
# declared below and exits 4 instead of running on with half its data and
# reporting a verdict it did not compute.
$ErrorActionPreference = 'Stop'

# ---------------------------------------------------------------------------
# Exit codes. Monotonic in severity so that "not zero means look" and "two or
# more means page" are the only two rules anybody has to write. Documented in
# docs/ALERTING.md section 5; changing a meaning here changes a contract with
# every scheduler rule in every estate that has deployed this.
# ---------------------------------------------------------------------------
$ExitClean       = 0   # Asked, answered, everything inside its thresholds.
$ExitTicket      = 1   # Findings that want a person in working hours.
$ExitPage        = 2   # Findings that want a person now.
$ExitNoAnswer    = 3   # The source could not be read. The question was NOT answered.
$ExitWatchdogBad = 4   # This script could not run: bad parameters, unhandled error.

# Event IDs. A monitoring rule is written against the number, so a number must
# never be repurposed. Add new ones; do not redefine these.
$EventClean      = 9000  # Information. The heartbeat. Absence of it is the signal.
$EventTicket     = 9001  # Warning.
$EventPage       = 9002  # Error.
$EventNoAnswer   = 9003  # Error. Source unreachable or unusable.
$EventWatchdog   = 9004  # Error. The watchdog itself failed.

$ScriptName = 'Watch-ConnectorHealth'

# ---------------------------------------------------------------------------
# The catch-all. $ErrorActionPreference is Stop, so an unanticipated terminating
# error would otherwise unwind out of the script with whatever exit code the
# host felt like - which on Windows PowerShell is 1, the code this script uses
# for "a ticket". A watchdog that crashes and reports a ticket is a watchdog
# reporting a lower severity for its own death than for a pending delete, and
# that is exactly the direction this control must never fail in.
#
# Declared here, above everything that can throw, and it writes with
# Write-Output rather than the Write-Report helper below because a failure early
# enough to matter can precede that function's definition.
# ---------------------------------------------------------------------------
trap {
    Write-Output ''
    Write-Output "FAIL     $ScriptName failed before it could reach a verdict: $($_.Exception.Message)"
    Write-Output '         Nothing was measured. This is not a report that the estate is healthy.'
    Write-Output "         $($_.ScriptStackTrace)"

    try {
        $sourceOk = $false
        try { $sourceOk = [System.Diagnostics.EventLog]::SourceExists($EventSource) } catch { $sourceOk = $false }
        if ($sourceOk -and -not $NoEventLog) {
            [System.Diagnostics.EventLog]::WriteEntry(
                $EventSource,
                "$ScriptName failed before it could reach a verdict: $($_.Exception.Message)`n`n$($_.ScriptStackTrace)`n`nNothing was measured. While this persists nobody can tell whether the connector is running, which means nobody can tell whether deletions and permission revocations are still reaching the index.",
                [System.Diagnostics.EventLogEntryType]::Error,
                9004)
        }
    }
    catch { }

    Write-Output 'Exit 4.'
    exit 4
}

# Derived here rather than in the param block, for the 5.1 reason given above.
if ([string]::IsNullOrWhiteSpace($StatePath)) {
    $programData = $env:ProgramData
    if ([string]::IsNullOrWhiteSpace($programData)) { $programData = 'C:\ProgramData' }
    $StatePath = Join-Path $programData 'ConnectorState\health-watch\state.json'
}

# ---------------------------------------------------------------------------
# Small helpers, all of them written to behave identically on 5.1 and 7. Each
# one that differs between the two carries the measurement that proved it.
# ---------------------------------------------------------------------------

function ConvertTo-UtcInstant {
    <#
        Turns whatever ConvertFrom-Json produced for a timestamp into a UTC
        DateTime, or $null if it cannot.

        THIS FUNCTION EXISTS BECAUSE THE TWO POWERSHELL VERSIONS DISAGREE ABOUT
        THE TYPE. Measured on this machine against the endpoint's own format:

            "generatedUtc":"2026-08-30T15:09:44.2591234Z"
            5.1.26100.9168 -> System.String
            7.6.5          -> System.DateTime (Kind = Utc)

        So $payload.generatedUtc.ToUniversalTime() works on 7 and throws on 5.1,
        and [datetime]::Parse($payload.generatedUtc) on 7 round-trips a DateTime
        through its culture-formatted string. Both are wrong on one version, and
        the failure is an offset rather than an error - the same class of bug
        HealthProjection.AsUtc guards against from the other side, arriving here
        by a different route. An hour of skew in summer would silently move
        every freshness measurement across its threshold.

        RoundtripKind alone, deliberately: the endpoint always emits a trailing
        Z, and .NET refuses RoundtripKind combined with AssumeUniversal or
        AdjustToUniversal. A value that somehow arrives without a zone is
        stamped UTC rather than read as local, because the schema stores UTC and
        nothing else.
    #>
    param($Value)

    if ($null -eq $Value) { return $null }

    if ($Value -is [datetime]) {
        if ($Value.Kind -eq [System.DateTimeKind]::Utc)   { return $Value }
        if ($Value.Kind -eq [System.DateTimeKind]::Local) { return $Value.ToUniversalTime() }
        return [datetime]::SpecifyKind($Value, [System.DateTimeKind]::Utc)
    }

    $text = [string]$Value
    if ([string]::IsNullOrWhiteSpace($text)) { return $null }

    $parsed = [datetime]::MinValue
    $ok = [datetime]::TryParse(
        $text,
        [System.Globalization.CultureInfo]::InvariantCulture,
        [System.Globalization.DateTimeStyles]::RoundtripKind,
        [ref]$parsed)

    if (-not $ok) { return $null }

    if ($parsed.Kind -eq [System.DateTimeKind]::Local) { return $parsed.ToUniversalTime() }
    if ($parsed.Kind -eq [System.DateTimeKind]::Utc)   { return $parsed }
    return [datetime]::SpecifyKind($parsed, [System.DateTimeKind]::Utc)
}

function ConvertTo-NullableInt {
    <#
        JSON null and SQL NULL both have to survive as $null and must never
        become 0. minutesSinceLastSuccess is the column this matters most for:
        null means "has never once succeeded", which is the WORST possible state
        for this control, and coalescing it to zero would report it as the
        freshest thing in the estate. HealthReport.cs makes the same point about
        the same field, twice.
    #>
    param($Value)

    if ($null -eq $Value)             { return $null }
    if ($Value -is [System.DBNull])   { return $null }
    if ($Value -is [string] -and [string]::IsNullOrWhiteSpace($Value)) { return $null }

    try { return [int]$Value } catch { return $null }
}

function ConvertTo-CleanString {
    <#
        DBNull and $null both become the empty string.

        A helper rather than an inline `if` inside the hashtable literals below,
        because `if` used as an expression in a hashtable value is a construct
        that parses differently across PowerShell versions, and this script is
        not the place to find out which ones.
    #>
    param($Value)

    if ($null -eq $Value)           { return '' }
    if ($Value -is [System.DBNull]) { return '' }
    return [string]$Value
}

function Write-Report {
    param([string]$Text = '')
    Write-Output $Text
}

function New-Finding {
    <#
        One alert-worthy fact. Severity is 'page' or 'ticket'; Transient marks
        the ones -ConsecutivePolls debounces.
    #>
    param(
        [string]$ConnectionId,
        [string]$Condition,
        [ValidateSet('page', 'ticket')]
        [string]$Severity,
        [bool]$Transient,
        [string]$Detail
    )

    return [pscustomobject]@{
        ConnectionId = $ConnectionId
        Condition    = $Condition
        Severity     = $Severity
        Transient    = $Transient
        Detail       = $Detail
        Age          = 1
        IsNew        = $true
        Demoted      = $false
    }
}

function Write-EventSafely {
    <#
        Writes one entry, and NEVER lets a logging failure change the verdict.

        [System.Diagnostics.EventLog] is used rather than Write-EventLog because
        the type resolves on both versions - measured, 5.1.26100.9168 and 7.6.5
        both return System.Diagnostics.EventLog - whereas the *-EventLog cmdlets
        were absent from PowerShell 6 and much of 7 and are not something to
        depend on across the range this script claims to support.

        SourceExists IS CALLED INSIDE A TRY AND ITS THROW MEANS "NO", NOT
        "CRASH". Measured on this machine under a non-elevated token, both
        versions:

            [System.Diagnostics.EventLog]::SourceExists('ConnectorState')
            -> The source was not found, but some or all event logs could not be
               searched.  Inaccessible logs: Security, State.

        It THROWS rather than returning false, because determining absence
        requires reading every log and a least-privilege service account cannot
        read Security. An unguarded call there would take the whole watchdog down
        at its logging step under exactly the identity it is meant to run as -
        the failure would look like a broken script rather than a missing
        registration, and the estate would have no watchdog and no idea why.
    #>
    param(
        [int]$EventId,
        [System.Diagnostics.EventLogEntryType]$EntryType,
        [string]$Message
    )

    if ($NoEventLog) { return $null }

    # 31,839 characters is the hard limit for an event message. A large estate's
    # report can exceed it, and an over-long message is refused outright - which
    # would turn a page into silence.
    $capped = $Message
    if ($capped.Length -gt 30000) {
        $capped = $capped.Substring(0, 30000) + "`n... truncated. Full report in the task's output."
    }

    try {
        $exists = $false
        try { $exists = [System.Diagnostics.EventLog]::SourceExists($EventSource) } catch { $exists = $false }

        if (-not $exists) {
            return "Event source '$EventSource' is not registered (or cannot be confirmed under this identity), so nothing was written to the $EventLogName log. Run this script once with -Register from an elevated PowerShell. Until then the exit code is the only signal this watch produces."
        }

        [System.Diagnostics.EventLog]::WriteEntry($EventSource, $capped, $EntryType, $EventId)
        return $null
    }
    catch {
        return "Could not write event $EventId to the $EventLogName log: $($_.Exception.Message). The exit code is unaffected and remains the authoritative signal."
    }
}

# ---------------------------------------------------------------------------
# -Register. Everything that needs administrator rights, and nothing else.
#
# FOLDED INTO THIS SCRIPT RATHER THAN A SEPARATE Register-HealthWatch.ps1, ON
# PURPOSE. The registration's whole job is to encode the exact argument list the
# watch runs with - the URL, the thresholds, the source. Split across two files
# those two lists drift, and they drift in the direction that is hardest to
# notice: somebody tunes -MaxMinutesSinceSuccess here, believes the task changed,
# and the task goes on passing the number the other file hard-coded. Generated
# from the same param() block that the watch itself reads, they cannot. It is
# also one fewer file to copy to a host, and the registration prints the command
# line it produced so it can be read back and diffed against the task.
#
# IT REFUSES TO ALSO PERFORM A WATCH IN THE SAME INVOCATION. A state-changing,
# privileged operation and an unattended read-only check have no business sharing
# an exit code: if -Register also watched, a registration typo could return 0 and
# read as a clean estate, or a genuine page could read as a failed install.
# ---------------------------------------------------------------------------
if ($Register) {
    try {
        $identity = [Security.Principal.WindowsIdentity]::GetCurrent()
        $elevated = ([Security.Principal.WindowsPrincipal]$identity).IsInRole(
            [Security.Principal.WindowsBuiltInRole]::Administrator)

        if (-not $elevated) {
            Write-Report 'FAIL     -Register needs an elevated PowerShell. Creating an event source and a scheduled task are both administrator operations.'
            exit $ExitWatchdogBad
        }

        Write-Report ''
        Write-Report "Registering the connector health watch on $($env:COMPUTERNAME)"
        Write-Report ''

        # 1. The event source.
        $sourceExists = $false
        try { $sourceExists = [System.Diagnostics.EventLog]::SourceExists($EventSource) } catch { $sourceExists = $false }

        if ($sourceExists) {
            Write-Report "  event source   exists           $EventSource"
        }
        else {
            [System.Diagnostics.EventLog]::CreateEventSource($EventSource, $EventLogName)
            Write-Report "  event source   created          $EventSource in $EventLogName"
            Write-Report '                 A newly created source can take a few seconds to become writable, and the first write after creation is occasionally lost. Run the watch once by hand and confirm event 9000 before trusting the schedule.'
        }

        # 2. The state directory. Created here, while elevated, so the watch
        #    identity never has to create anything under ProgramData itself.
        $stateDir = Split-Path -Path $StatePath -Parent
        if (-not (Test-Path -LiteralPath $stateDir)) {
            [void](New-Item -ItemType Directory -Path $stateDir -Force)
            Write-Report "  state          created          $stateDir"
        }
        else {
            Write-Report "  state          exists           $stateDir"
        }

        # 3. The task action. The CURRENT host is used deliberately: whichever
        #    of powershell.exe or pwsh.exe the operator validated this script
        #    against is the one the task will run, rather than a hard-coded
        #    powershell.exe that may behave differently from the run they just
        #    watched succeed.
        $hostExe = [System.Diagnostics.Process]::GetCurrentProcess().MainModule.FileName
        $selfPath = $MyInvocation.MyCommand.Path

        $argumentList = New-Object System.Collections.Generic.List[string]
        [void]$argumentList.Add('-NoProfile')
        [void]$argumentList.Add('-NonInteractive')
        [void]$argumentList.Add('-ExecutionPolicy')
        [void]$argumentList.Add('Bypass')
        [void]$argumentList.Add('-File')
        [void]$argumentList.Add('"' + $selfPath + '"')

        $effectiveSource = $Source
        if ($effectiveSource -eq 'Auto') {
            if ([string]::IsNullOrWhiteSpace($HealthUrl)) { $effectiveSource = 'Sql' } else { $effectiveSource = 'Http' }
        }

        [void]$argumentList.Add('-Source');   [void]$argumentList.Add($effectiveSource)

        if ($effectiveSource -eq 'Http') {
            if ([string]::IsNullOrWhiteSpace($HealthUrl)) {
                Write-Report 'FAIL     -Source Http was chosen but no -HealthUrl was supplied. Nothing was registered.'
                exit $ExitWatchdogBad
            }
            [void]$argumentList.Add('-HealthUrl'); [void]$argumentList.Add('"' + $HealthUrl + '"')
            [void]$argumentList.Add('-MaxPayloadAgeMinutes'); [void]$argumentList.Add([string]$MaxPayloadAgeMinutes)
            if ($AllowInsecureHttp) { [void]$argumentList.Add('-AllowInsecureHttp') }
        }
        else {
            [void]$argumentList.Add('-SqlInstance'); [void]$argumentList.Add('"' + $SqlInstance + '"')
            [void]$argumentList.Add('-Database');    [void]$argumentList.Add('"' + $Database + '"')
        }

        # Every threshold is written out explicitly, including the ones left at
        # their defaults. A task whose arguments name only the overrides silently
        # changes behaviour the day somebody edits a default in this file, and
        # the change is invisible in the task definition an operator reads.
        [void]$argumentList.Add('-MissedIntervalFactor');   [void]$argumentList.Add([string]$MissedIntervalFactor)
        [void]$argumentList.Add('-MinimumStaleMinutes');    [void]$argumentList.Add([string]$MinimumStaleMinutes)
        [void]$argumentList.Add('-MaxMinutesSinceSuccess'); [void]$argumentList.Add([string]$MaxMinutesSinceSuccess)
        [void]$argumentList.Add('-FailuresToPage');         [void]$argumentList.Add([string]$FailuresToPage)
        [void]$argumentList.Add('-ConsecutivePolls');       [void]$argumentList.Add([string]$ConsecutivePolls)
        [void]$argumentList.Add('-TimeoutSeconds');         [void]$argumentList.Add([string]$TimeoutSeconds)
        [void]$argumentList.Add('-StatePath');              [void]$argumentList.Add('"' + $StatePath + '"')
        [void]$argumentList.Add('-EventLogName');           [void]$argumentList.Add('"' + $EventLogName + '"')
        [void]$argumentList.Add('-EventSource');            [void]$argumentList.Add('"' + $EventSource + '"')
        if ($AllowUntrustedServerCertificate) { [void]$argumentList.Add('-AllowUntrustedServerCertificate') }

        $arguments = $argumentList -join ' '

        Write-Report ''
        Write-Report '  The task will run:'
        Write-Report "    $hostExe $arguments"
        Write-Report ''

        # 4. The identity. No password is asked for anywhere in this script, so
        #    the only identities it can register unattended are the ones that
        #    need none.
        $normalised = $RunAsUser.Trim()
        $passwordless = @('SYSTEM', 'NT AUTHORITY\SYSTEM', 'NETWORK SERVICE', 'NT AUTHORITY\NETWORK SERVICE',
                          'LOCAL SERVICE', 'NT AUTHORITY\LOCAL SERVICE')

        $isGmsa = $normalised.EndsWith('$')
        $isPasswordless = ($passwordless -contains $normalised.ToUpperInvariant()) -or $isGmsa

        if (-not $isPasswordless) {
            Write-Report "FAIL     '$normalised' is an ordinary account, and registering a task to run as one requires its password."
            Write-Report '         This script takes no interactive input by design - it runs unattended - so it will not ask for one and will not half-register a task that cannot start.'
            Write-Report ''
            Write-Report '         Either re-run with -RunAsUser SYSTEM (the machine account authenticates to the dashboard and to SQL as DOMAIN\HOSTNAME$, which can be added to CrawlState:ReaderGroups and granted SELECT), or with a group managed service account, or register it yourself with:'
            Write-Report ''
            Write-Report "           schtasks /Create /TN `"$TaskName`" /SC MINUTE /MO $EveryMinutes /RL HIGHEST ``"
            Write-Report "             /RU `"$normalised`" /RP * ``"
            Write-Report "             /TR `"'$hostExe' $arguments`""
            Write-Report ''
            Write-Report '         Nothing was registered. The event source and state directory above were.'
            exit $ExitWatchdogBad
        }

        # 5. The task itself.
        $existing = Get-ScheduledTask -TaskName $TaskName -ErrorAction SilentlyContinue
        if ($existing -and -not $Force) {
            Write-Report "FAIL     A scheduled task named '$TaskName' already exists. Re-run with -Force to replace it, after checking that nothing else depends on its current definition."
            exit $ExitWatchdogBad
        }

        $action = New-ScheduledTaskAction -Execute $hostExe -Argument $arguments

        # A repetition trigger with no duration - "forever" - rather than a
        # daily trigger with a repetition window, because a window that expires
        # stops the watch at a time nobody chose and nothing reports it.
        $trigger = New-ScheduledTaskTrigger -Once -At (Get-Date) `
            -RepetitionInterval (New-TimeSpan -Minutes $EveryMinutes)

        $principal = New-ScheduledTaskPrincipal -UserId $normalised -LogonType ServiceAccount -RunLevel Highest

        # StartWhenAvailable so a firing missed because the host was off is
        # caught up rather than skipped; MultipleInstances IgnoreNew so a slow
        # poll cannot stack; a bounded ExecutionTimeLimit so a wedged run is
        # killed and the next one gets a turn rather than the watch appearing
        # to be "running" indefinitely.
        $settings = New-ScheduledTaskSettingsSet `
            -StartWhenAvailable `
            -MultipleInstances IgnoreNew `
            -ExecutionTimeLimit (New-TimeSpan -Minutes 10) `
            -DontStopOnIdleEnd `
            -AllowStartIfOnBatteries `
            -DontStopIfGoingOnBatteries

        [void](Register-ScheduledTask -TaskName $TaskName -Action $action -Trigger $trigger `
            -Principal $principal -Settings $settings -Force:$Force `
            -Description "Polls connector health every $EveryMinutes minutes and writes the verdict to the $EventLogName log as $EventSource, events 9000-9004. A dead connector does not take search down - it lets deletions and permission revocations stop propagating - so this task's SILENCE is the failure to alert on. See docs/ALERTING.md.")

        Write-Report "  task           registered       $TaskName, every $EveryMinutes minute(s), as $normalised"
        Write-Report ''
        Write-Report 'Registered. Two things are still required and neither can be done from here:'
        Write-Report ''
        Write-Report "  1. Grant the watch identity read access. HTTP: add it to CrawlState:ReaderGroups. SQL: grant it SELECT on crawl.vwConnectionHealth. As SYSTEM the identity on the wire is DOMAIN\$($env:COMPUTERNAME)`$."
        Write-Report "  2. Write the dead-man rule in the monitoring system: alert if no event 9000 from source $EventSource appears within $([int]($EveryMinutes * 3)) minutes. Nothing on this host can detect that this task has stopped - see docs/ALERTING.md section 6."
        Write-Report ''
        Write-Report 'Then run this script once by hand and confirm the event appears.'
        exit $ExitClean
    }
    catch {
        Write-Report ''
        Write-Report "FAIL     Registration failed: $($_.Exception.Message)"
        exit $ExitWatchdogBad
    }
}

# ---------------------------------------------------------------------------
# From here down: the watch itself. Read-only against both sources.
# ---------------------------------------------------------------------------

$startedUtc = [datetime]::UtcNow

# The source is chosen ONCE, here, from the parameters, and never revisited.
# See the description: a runtime fallback from HTTP to SQL would paper over a
# dashboard that has been down for a month.
$effectiveSource = $Source
if ($effectiveSource -eq 'Auto') {
    if ([string]::IsNullOrWhiteSpace($HealthUrl)) { $effectiveSource = 'Sql' } else { $effectiveSource = 'Http' }
}

$sourceLabel = ''
$connections = @()
$sourceProblem = $null          # Non-null means the question was not answered.
$payloadStatus = $null          # The endpoint's own roll-up, reported not trusted.
$payloadGeneratedUtc = $null

function Get-HealthOverHttp {
    <#
        Returns @{ Problem = <string or $null>; Connections = @(); Status = ''; GeneratedUtc = <datetime> }.

        EVERY WAY OF NOT GETTING A USABLE PAYLOAD IS A PROBLEM, INCLUDING THE
        ONES THAT ARRIVE AS A SUCCESSFUL-LOOKING RESPONSE. A 200 carrying HTML
        is the ASP.NET error page or an authentication challenge page, not
        health; a 200 carrying JSON without a connections array is some other
        service on that port; a 200 carrying a payload generated an hour ago is
        an intermediary's cache. All three are green to a naive check and all
        three mean the estate is unobserved.
    #>
    param([string]$Url)

    $result = @{ Problem = $null; Connections = @(); Status = $null; GeneratedUtc = $null }

    $uri = $null
    if (-not [System.Uri]::TryCreate($Url, [System.UriKind]::Absolute, [ref]$uri)) {
        $result.Problem = "-HealthUrl '$Url' is not an absolute URL."
        return $result
    }

    if ($uri.Scheme -eq 'http' -and -not $AllowInsecureHttp) {
        $result.Problem = "-HealthUrl is http://. Program.cs installs UseHttpsRedirection, so this request would be answered with a 307 rather than with health, and a redirect a monitor does not follow is indistinguishable from the site being down. Use https://, or type -AllowInsecureHttp if this is a lab."
        return $result
    }
    elseif ($uri.Scheme -ne 'https' -and $uri.Scheme -ne 'http') {
        $result.Problem = "-HealthUrl scheme '$($uri.Scheme)' is not http or https."
        return $result
    }

    # TLS on Windows PowerShell 5.1 only, and REPAIRED RATHER THAN FORCED.
    #
    # The folklore fix is an unconditional `SecurityProtocol = ... -bor Tls12`,
    # which is right for an old .NET Framework whose default was Ssl3|Tls and
    # wrong for a current one. Measured on this host, 5.1.26100.9168:
    #
    #     Default SecurityProtocol = SystemDefault
    #
    # SystemDefault means "let Schannel negotiate", which is strictly the best
    # setting available - a raw SslStream to the dashboard from 5.1 negotiates
    # TLS 1.3 under it. Or-ing Tls12 into SystemDefault does not add 1.2 to a
    # list; it REPLACES the negotiate-anything value with a fixed one, pinning
    # the connection at 1.2 and forbidding 1.3. So the unconditional fix is a
    # silent downgrade on every modern host.
    #
    # Applied only where it is actually a repair: a value that has been pinned
    # to something legacy and does not already include 1.2. SystemDefault is
    # left exactly alone. PowerShell 7 uses HttpClient and ignores this
    # property entirely, so it is not touched there at all.
    if ($PSVersionTable.PSVersion.Major -lt 6) {
        try {
            $current = [System.Net.ServicePointManager]::SecurityProtocol
            if ($current -ne [System.Net.SecurityProtocolType]::SystemDefault -and
                -not ($current -band [System.Net.SecurityProtocolType]::Tls12)) {
                [System.Net.ServicePointManager]::SecurityProtocol = $current -bor [System.Net.SecurityProtocolType]::Tls12
            }
        }
        catch { }
    }

    # FEATURE DETECTION, NOT A VERSION CHECK. -SkipCertificateCheck is a
    # PowerShell 7 parameter and passing it on 5.1 is a hard parameter-binding
    # error; measured on this machine, (Get-Command
    # Invoke-WebRequest).Parameters.ContainsKey('SkipCertificateCheck') is False
    # on 5.1.26100.9168 and True on 7.6.5. Asking the cmdlet what it supports
    # survives a future version that adds or removes it; asking $PSVersionTable
    # does not. This is one of the three cross-version traps this repository has
    # already been bitten by.
    $splat = @{
        Uri                  = $uri.AbsoluteUri
        UseDefaultCredentials = $true
        UseBasicParsing      = $true
        TimeoutSec           = $TimeoutSeconds
        MaximumRedirection   = 0
        Method               = 'GET'
        ErrorAction          = 'Stop'
    }

    $iwr = Get-Command Invoke-WebRequest
    $supportsSkipCert = $iwr.Parameters.ContainsKey('SkipCertificateCheck')

    # THE TWO VERSIONS DISAGREE ABOUT WHETHER THIS REQUEST IS ALLOWED TO LEAVE
    # AT ALL, and finding out cost a full run of the failure matrix. PowerShell
    # 7 refuses to send -UseDefaultCredentials over an unencrypted connection:
    #
    #   The cmdlet cannot protect plain text secrets sent over unencrypted
    #   connections. To suppress this warning and send plain text secrets over
    #   unencrypted networks, reissue the command specifying the
    #   AllowUnencryptedAuthentication parameter.
    #
    # Windows PowerShell 5.1 has no such guard and sends the Negotiate handshake
    # in the clear without comment. Measured: every http:// case in the matrix
    # passed on 5.1 and returned that message on 7.6.5.
    #
    # PowerShell 7 is RIGHT, and this does not weaken it. The parameter is added
    # only when -AllowInsecureHttp was typed, which is already the operator
    # saying "this is a lab"; an https:// URL - the only supported production
    # configuration - never reaches this line. Without it, -AllowInsecureHttp is
    # simply broken on 7, and it fails in the most confusing possible way: as an
    # unreachable host, on a host that is plainly reachable.
    if ($uri.Scheme -eq 'http' -and $iwr.Parameters.ContainsKey('AllowUnencryptedAuthentication')) {
        $splat['AllowUnencryptedAuthentication'] = $true
    }

    if ($AllowUntrustedServerCertificate -and $uri.Scheme -eq 'https') {
        if ($supportsSkipCert) {
            $splat['SkipCertificateCheck'] = $true
            Write-Warning 'Server certificate validation is disabled for this request. Do not schedule the watch this way against a production dashboard - a watchdog that accepts any certificate will accept the wrong one, and it is the component whose answer everything else trusts.'
        }
        else {
            # REFUSED ON WINDOWS POWERSHELL 5.1, AND THIS IS NOT LAZINESS - IT
            # IS THE ONLY HONEST OPTION, ESTABLISHED BY MEASUREMENT.
            #
            # The documented 5.1 idiom is to assign a scriptblock to
            # ServicePointManager.ServerCertificateValidationCallback. Against
            # the live dashboard on this host it does not work, in any form.
            # Measured, 5.1.26100.9168, GET https://localhost:8443/ :
            #
            #   NO CALLBACK AT ALL          -> OK 200
            #   CALLBACK { $true }          -> ERR : The underlying connection
            #                                  was closed: An unexpected error
            #                                  occurred on a send.
            #   CALLBACK { param(4) $true } -> same
            #   CALLBACK cast to delegate   -> same
            #
            # The handshake itself is fine - a raw SslStream to the same port
            # completes at Tls, Tls11, Tls12 AND Tls13 on 5.1 - so this is the
            # callback failing, not TLS. .NET Framework invokes it on the
            # connection's own thread, where a PowerShell scriptblock has no
            # runspace to run in; it throws, and HttpWebRequest reports the
            # throw as a send error. The symptom is therefore the worst possible
            # one for this script: a perfectly healthy dashboard is reported as
            # UNREACHABLE, which is a page, on a 5.1 host and nowhere else.
            #
            # An Add-Type ICertificatePolicy shim does work on 5.1 and was
            # rejected: it puts a runtime C# compile inside the one component
            # whose job is to be more reliable than everything it watches, and
            # it buys a switch that must never be used in production anyway.
            # Refusing is better. The two real answers - install a certificate
            # that chains to a trusted root, or run the watch under PowerShell 7
            # - are both improvements over what the switch would have bought.
            #
            # SCOPED TO THE HTTP PATH ONLY. The SQL path implements the same
            # switch through TrustServerCertificate in the connection string,
            # which is SqlClient's own code and works identically on both
            # versions. That is untouched by this.
            $result.Problem = "-AllowUntrustedServerCertificate cannot be honoured for an https:// URL under Windows PowerShell $($PSVersionTable.PSVersion). Setting ServicePointManager.ServerCertificateValidationCallback there makes every request fail with 'An unexpected error occurred on a send', which this script would have to report as an unreachable dashboard - a page, raised against a dashboard that is up. Rather than do that: install a certificate on the dashboard that chains to a root this host trusts, or run this watch under PowerShell 7, where -SkipCertificateCheck does the job properly. Nothing was checked."
            return $result
        }
    }

    $response = $null
    $body = $null

    try {
        $response = Invoke-WebRequest @splat
        $body = [string]$response.Content
    }
    catch {
        # Status-code extraction differs between the versions but both surface
        # .Response.StatusCode as the enum, so one path reads both:
        #   5.1 -> System.Net.WebException           / HttpWebResponse
        #   7.x -> HttpResponseException             / HttpResponseMessage
        $status = $null
        $reason = $_.Exception.Message

        if ($_.Exception.PSObject.Properties.Name -contains 'Response' -and $null -ne $_.Exception.Response) {
            try { $status = [int]$_.Exception.Response.StatusCode } catch { $status = $null }
        }

        if ($null -ne $status) {
            # The error body carries the endpoint's own words on a 503 and is
            # worth reporting. $_.ErrorDetails.Message holds it on 7 and often
            # on 5.1; the response stream is the 5.1 fallback.
            $errorBody = $null
            try { if ($_.ErrorDetails -and $_.ErrorDetails.Message) { $errorBody = [string]$_.ErrorDetails.Message } } catch { }
            if ([string]::IsNullOrWhiteSpace($errorBody)) {
                try {
                    $stream = $_.Exception.Response.GetResponseStream()
                    if ($stream) {
                        $sr = New-Object System.IO.StreamReader($stream)
                        $errorBody = $sr.ReadToEnd()
                        $sr.Dispose()
                    }
                }
                catch { }
            }

            $hint = switch ($status) {
                301 { 'a redirect. Program.cs redirects http to https; point -HealthUrl at the https URL.' }
                302 { 'a redirect. Program.cs redirects http to https; point -HealthUrl at the https URL.' }
                307 { 'a redirect. This is what an http:// URL gets. Point -HealthUrl at the https URL.' }
                401 { 'unauthorised. The watch identity did not authenticate. Under a scheduled task as SYSTEM the identity on the wire is DOMAIN\HOSTNAME$ - check IIS Windows Authentication is enabled and that Negotiate is offered.' }
                403 { 'forbidden. The watch identity authenticated but is not in CrawlState:ReaderGroups. /health is gated by the reader policy by name, exactly as the seven pages are.' }
                404 { 'not found. Either the URL is wrong, or the deployed build predates GET /health - the endpoint was added after the first dashboard release, and an older publish answers / but not /health.' }
                500 { 'the dashboard faulted before it could answer. Its own log has the detail; this endpoint deliberately does not put it on the wire.' }
                503 { 'the dashboard is up but could not read crawl state. That is what 503 means here, and it is the one code the endpoint reserves for it.' }
                default { 'an unexpected status.' }
            }

            $result.Problem = "GET $($uri.AbsoluteUri) answered HTTP $status - $hint"
            if (-not [string]::IsNullOrWhiteSpace($errorBody)) {
                $result.Problem += " Body: $($errorBody.Trim())"
            }
        }
        else {
            $result.Problem = "GET $($uri.AbsoluteUri) did not answer at all: $reason. From this script's seat that is indistinguishable from the whole host being down, and it is treated as the more serious of the two."
        }

        return $result
    }

    # NO finally RESTORING A CERTIFICATE CALLBACK, because nothing here installs
    # one any more - see the -AllowUntrustedServerCertificate branch above for
    # what was measured and why that whole approach was abandoned on 5.1. Left
    # as a note rather than deleted silently, so the next person to reach for
    # ServicePointManager here finds out first.

    # A 3xx that did not throw. Windows PowerShell 5.1 and PowerShell 7 disagree
    # about whether -MaximumRedirection 0 raises on a redirect or hands the 3xx
    # back as a normal response, so both outcomes are handled: the throwing one
    # in the catch above, this one here. Without this the redirect would fall
    # through to the "empty body" message below and an operator would be told
    # the endpoint returned nothing when what actually happened is that they
    # pointed the watch at http://.
    $statusNumber = 0
    try { $statusNumber = [int]$response.StatusCode } catch { $statusNumber = 0 }

    if ($statusNumber -ge 300) {
        $result.Problem = "GET $($uri.AbsoluteUri) answered HTTP $statusNumber, a redirect, which this watch deliberately does not follow. Program.cs installs UseHttpsRedirection, so this is what an http:// URL gets. Point -HealthUrl at the https URL."
        return $result
    }

    # A 200. That is permission to parse and nothing more - see the file header.
    if ([string]::IsNullOrWhiteSpace($body)) {
        $result.Problem = "GET $($uri.AbsoluteUri) answered HTTP $($response.StatusCode) with an empty body. The endpoint always writes a payload, so this is not it."
        return $result
    }

    $trimmed = $body.TrimStart([char]0xFEFF, ' ', "`t", "`r", "`n")
    if (-not $trimmed.StartsWith('{')) {
        $head = $trimmed.Substring(0, [Math]::Min(120, $trimmed.Length)).Replace("`r", ' ').Replace("`n", ' ')
        $result.Problem = "GET $($uri.AbsoluteUri) answered HTTP $($response.StatusCode) with something that is not a JSON object. That is usually an HTML error page or an authentication challenge arriving with a 200. First 120 characters: $head"
        return $result
    }

    $payload = $null
    try { $payload = $trimmed | ConvertFrom-Json }
    catch {
        $result.Problem = "GET $($uri.AbsoluteUri) answered HTTP $($response.StatusCode) with a body that is not valid JSON: $($_.Exception.Message)"
        return $result
    }

    # CONTRACT CHECK. HealthReport.cs says adding a field is safe and renaming
    # or removing one is not, and warns that a check which can no longer find
    # its field usually evaluates to "not alerting" rather than to an error.
    # This is the guard against that: the three fields this script's verdict
    # depends on must be present, and their absence is an unusable source rather
    # than an empty estate.
    $names = @($payload.PSObject.Properties.Name)
    foreach ($required in @('status', 'generatedUtc', 'connections')) {
        if ($names -notcontains $required) {
            $result.Problem = "The payload from $($uri.AbsoluteUri) has no '$required' field, so it is not GET /health's contract. Either the URL points at something else, or the endpoint's shape changed - see HealthReport.cs, which is that contract."
            return $result
        }
    }

    $generated = ConvertTo-UtcInstant $payload.generatedUtc
    if ($null -eq $generated) {
        $result.Problem = "The payload's generatedUtc ('$($payload.generatedUtc)') could not be read as a timestamp, so its freshness cannot be established and it will not be trusted."
        return $result
    }

    $ageMinutes = [int][Math]::Round(([datetime]::UtcNow - $generated).TotalMinutes)
    if ($ageMinutes -gt $MaxPayloadAgeMinutes) {
        $result.Problem = "The payload was generated $ageMinutes minute(s) ago, beyond the $MaxPayloadAgeMinutes minute limit. The endpoint sends Cache-Control: no-store precisely so that no intermediary can answer a monitor with an old verdict, so this is either a cache in the path or a clock difference between this host and the dashboard host. A cached green body is the most dangerous answer this script can receive, so it is refused rather than believed."
        return $result
    }
    if ($ageMinutes -lt -$MaxPayloadAgeMinutes) {
        $result.Problem = "The payload's generatedUtc is $([Math]::Abs($ageMinutes)) minute(s) in the future. The clocks on this host and the dashboard host disagree by more than the freshness window, which makes every staleness measurement below meaningless."
        return $result
    }

    $result.Status = [string]$payload.status
    $result.GeneratedUtc = $generated

    $rows = @()
    foreach ($c in @($payload.connections)) {
        $rows += [pscustomobject]@{
            ConnectionId            = ConvertTo-CleanString $c.connectionId
            DisplayName             = ConvertTo-CleanString $c.displayName
            Enabled                 = [bool]$c.enabled
            Health                  = ConvertTo-CleanString $c.health
            LastRunStatus           = ConvertTo-CleanString $c.lastRunStatus
            MinutesSinceLastSuccess = ConvertTo-NullableInt $c.minutesSinceLastSuccess
            ExpectedIntervalMinutes = ConvertTo-NullableInt $c.expectedIntervalMinutes
            ConsecutiveFailures     = [int](ConvertTo-NullableInt $c.consecutiveFailures)
            LiveItems               = [int](ConvertTo-NullableInt $c.liveItems)
            PendingDeletes          = [int](ConvertTo-NullableInt $c.pendingDeletes)
            ErrorKind               = ConvertTo-CleanString $c.errorKind
        }
    }

    $result.Connections = $rows
    return $result
}

function Get-HealthOverSql {
    <#
        The same shape, read straight off crawl.vwConnectionHealth.

        System.Data.SqlClient rather than Microsoft.Data.SqlClient: measured on
        this machine, System.Data.SqlClient.SqlConnection constructs on both
        5.1.26100.9168 and 7.6.5 and Microsoft.Data.SqlClient resolves on
        neither, because PowerShell ships the former and nothing ships the
        latter. Test-TriggerHealth.ps1 makes the same choice for the same reason.

        SELECT ONLY, and the column list is explicit. Nothing here writes, and
        the identity this runs as needs no more than SELECT on one view.
    #>
    $result = @{ Problem = $null; Connections = @(); Status = $null; GeneratedUtc = $null }

    $builder = New-Object System.Data.SqlClient.SqlConnectionStringBuilder
    $builder['Data Source']         = $SqlInstance
    $builder['Initial Catalog']     = $Database
    $builder['Integrated Security'] = $true
    $builder['Application Name']    = 'Watch-ConnectorHealth'
    $builder['Connect Timeout']     = 15
    # Encryption is not negotiable and is not a parameter, which is the line
    # SqlConnectionStringFactory and Test-SqlSource.ps1 both hold. Certificate
    # VALIDATION is separable and is the only part a switch turns off.
    $builder['Encrypt']             = $true

    if ($AllowUntrustedServerCertificate) {
        $builder['TrustServerCertificate'] = $true
    }

    $sql = @'
SET NOCOUNT ON;
SELECT  ConnectionId,
        DisplayName,
        IsEnabled,
        Health,
        LastRunStatus,
        MinutesSinceLastSuccess,
        ExpectedIntervalMinutes,
        ConsecutiveFailures,
        LiveItemCount,
        PendingDeleteCount,
        ErrorKind
FROM    [crawl].[vwConnectionHealth];
'@

    $connection = $null
    try {
        $connection = New-Object System.Data.SqlClient.SqlConnection $builder.ConnectionString
        $connection.Open()

        $command = $connection.CreateCommand()
        $command.CommandText = $sql
        $command.CommandTimeout = $TimeoutSeconds

        $reader = $command.ExecuteReader()
        $rows = @()

        while ($reader.Read()) {
            $rows += [pscustomobject]@{
                ConnectionId            = [string]$reader['ConnectionId']
                DisplayName             = [string]$reader['DisplayName']
                Enabled                 = [bool]$reader['IsEnabled']
                Health                  = ConvertTo-CleanString $reader['Health']
                LastRunStatus           = ConvertTo-CleanString $reader['LastRunStatus']
                MinutesSinceLastSuccess = ConvertTo-NullableInt $reader['MinutesSinceLastSuccess']
                ExpectedIntervalMinutes = ConvertTo-NullableInt $reader['ExpectedIntervalMinutes']
                ConsecutiveFailures     = [int](ConvertTo-NullableInt $reader['ConsecutiveFailures'])
                LiveItems               = [int](ConvertTo-NullableInt $reader['LiveItemCount'])
                PendingDeletes          = [int](ConvertTo-NullableInt $reader['PendingDeleteCount'])
                ErrorKind               = ConvertTo-CleanString $reader['ErrorKind']
            }
        }

        $reader.Close()

        $result.Connections = $rows
        # Generated NOW, by definition: this read went to the database rather
        # than through anything that could have cached it. The freshness check
        # that the HTTP path needs does not apply and is not faked.
        $result.GeneratedUtc = [datetime]::UtcNow
        return $result
    }
    catch {
        $result.Problem = "crawl.vwConnectionHealth on $SqlInstance/$Database could not be read: $($_.Exception.Message). A connection or permission failure is not a passing check."
        return $result
    }
    finally {
        if ($null -ne $connection) { $connection.Dispose() }
    }
}

# ---------------------------------------------------------------------------
# The debounce and heartbeat state.
#
# READ AND WRITTEN THROUGH [System.IO.File] WITH AN EXPLICIT ENCODING, not
# Get-Content/Set-Content. Set-Content -Encoding UTF8 writes a BOM on 5.1 and
# none on 7, so the two versions would produce files that are byte-different for
# the same content, and a file written by one and read by the other is a
# needless variable in the component whose whole job is to be trustworthy.
# ---------------------------------------------------------------------------

$stateDegraded    = $null   # A message if state could not be used.
$previousState    = $null

try {
    if (Test-Path -LiteralPath $StatePath) {
        $raw = [System.IO.File]::ReadAllText($StatePath)
        $raw = $raw.TrimStart([char]0xFEFF)
        if (-not [string]::IsNullOrWhiteSpace($raw)) {
            $previousState = $raw | ConvertFrom-Json
        }
    }
}
catch {
    # NOT FATAL, AND NOT SILENT. Losing the ability to debounce must cost noise,
    # never coverage: demotion is switched off below so every finding counts at
    # full severity, and a ticket-level finding says why.
    $stateDegraded = "The watch state at $StatePath could not be read ($($_.Exception.Message)). Debouncing is disabled for this run, so self-clearing conditions are reported at full severity rather than being held for $ConsecutivePolls polls. Coverage is unaffected; noise is not."
    $previousState = $null
}

$previousConditions = @{}
if ($null -ne $previousState -and $null -ne $previousState.conditions) {
    foreach ($p in $previousState.conditions.PSObject.Properties) {
        $previousConditions[$p.Name] = $p.Value
    }
}

# ---------------------------------------------------------------------------
# Read the selected source.
# ---------------------------------------------------------------------------

if ($effectiveSource -eq 'Http') {
    if ([string]::IsNullOrWhiteSpace($HealthUrl)) {
        Write-Report ''
        Write-Report 'FAIL     -Source Http was selected but no -HealthUrl was supplied. Nothing was checked.'
        [void](Write-EventSafely -EventId $EventWatchdog -EntryType ([System.Diagnostics.EventLogEntryType]::Error) `
            -Message "$ScriptName could not run: -Source Http with no -HealthUrl. Nothing was checked.")
        exit $ExitWatchdogBad
    }

    $sourceLabel = "$HealthUrl (GET /health)"
    $read = Get-HealthOverHttp -Url $HealthUrl
}
else {
    $sourceLabel = "$SqlInstance/$Database (crawl.vwConnectionHealth)"
    $read = Get-HealthOverSql
}

$sourceProblem       = $read.Problem
$connections         = @($read.Connections)
$payloadStatus       = $read.Status
$payloadGeneratedUtc = $read.GeneratedUtc

# ---------------------------------------------------------------------------
# The source could not answer. This is the branch the whole design turns on:
# from here, "the connector is dead" and "the box that would have told me is
# dead" carry the same exposure, so silence is never success.
# ---------------------------------------------------------------------------
if ($null -ne $sourceProblem) {
    $report = New-Object System.Text.StringBuilder
    [void]$report.AppendLine("Connector health watch on $($env:COMPUTERNAME) at $($startedUtc.ToString('yyyy-MM-dd HH:mm:ss'))Z")
    [void]$report.AppendLine("Source: $sourceLabel")
    [void]$report.AppendLine('')
    [void]$report.AppendLine('NO ANSWER  The health source could not be read.')
    [void]$report.AppendLine("           $sourceProblem")
    [void]$report.AppendLine('')
    [void]$report.AppendLine('This is NOT a report that the estate is healthy, and it must not be treated as one. Nothing was measured. While this persists, nobody can tell whether the connector is running, which means nobody can tell whether deletions and permission revocations are still reaching the index.')

    $text = $report.ToString()
    Write-Report ''
    Write-Report $text

    $logProblem = Write-EventSafely -EventId $EventNoAnswer -EntryType ([System.Diagnostics.EventLogEntryType]::Error) -Message $text
    if ($null -ne $logProblem) { Write-Report "WARNING  $logProblem" }

    Write-Report "Exit $ExitNoAnswer."
    exit $ExitNoAnswer
}

# ---------------------------------------------------------------------------
# Evaluate. One rule set, whichever source produced the rows.
#
# NOTHING BELOW RE-DERIVES THE HEALTH WORD. sql/22 owns that CASE expression and
# a second copy here would be free to disagree with the dashboard - quietly, and
# on the one afternoon somebody is comparing an alert to a page and cannot work
# out which is lying. The words are read; only the freshness arithmetic and the
# severity mapping happen here, and the freshness arithmetic is here precisely
# because the view cannot do it (see the file header).
# ---------------------------------------------------------------------------

$findings = @()
$measurements = @()

if ($connections.Count -eq 0) {
    # AN EMPTY ESTATE IS THE LOUDEST QUIET ANSWER THERE IS, and it is a page.
    # Zero rows is what an empty crawl.Connection looks like, and it is also
    # what a dashboard pointed at a freshly created database looks like.
    # Reporting clean would mean the healthiest possible verdict is the one
    # returned when nothing at all is being watched. HealthProjection.cs makes
    # the identical call - zero rows is a warning there, not an ok - for the
    # identical reason, and the delete-sweep evidence in GO-LIVE-READINESS ran
    # into the same thing from the other side: an empty result means "measured
    # nothing" at least as often as it means "nothing wrong".
    $findings += New-Finding -ConnectionId '-' -Condition 'NothingWatched' -Severity 'page' -Transient $false `
        -Detail 'No connections are registered at all, so this watch measured nothing. On an estate that had connections this is a state-store loss; on a new one it means the watch was scheduled before anything was registered. Either way the green answer would have been a lie.'
}

foreach ($c in $connections) {
    $id = $c.ConnectionId

    if (-not $c.Enabled) {
        # Disabled is a decision somebody took, not a fault, and holding an
        # alert amber through planned maintenance is how a check ends up
        # suppressed and then left suppressed. It is still MEASURED and printed,
        # so a connection disabled in March and forgotten is visible in the
        # report every time it runs.
        $measurements += ('IDLE     {0,-24} disabled. Not assessed for freshness.' -f $id)
        continue
    }

    # --- 1. Freshness. The dead-scheduler check, and the reason this exists. ---
    $threshold = $null
    $thresholdWhy = ''

    if ($null -ne $c.ExpectedIntervalMinutes -and $c.ExpectedIntervalMinutes -gt 0) {
        $threshold = $c.ExpectedIntervalMinutes * $MissedIntervalFactor
        $thresholdWhy = "$($c.ExpectedIntervalMinutes) min interval x $MissedIntervalFactor"
        if ($threshold -lt $MinimumStaleMinutes) {
            $threshold = $MinimumStaleMinutes
            $thresholdWhy = "floor of $MinimumStaleMinutes min (interval x $MissedIntervalFactor was lower)"
        }
    }
    else {
        $threshold = $MaxMinutesSinceSuccess
        $thresholdWhy = "-MaxMinutesSinceSuccess, because ExpectedIntervalMinutes is not set"

        # Nagged about every run, on purpose. With that column NULL the view's
        # own 'late' arm is unreachable, so this connection's lateness is
        # invisible in the database and depends entirely on the fallback above.
        # Measured: on the estate this was written against, crawl.Connection's
        # single row has ExpectedIntervalMinutes NULL and the view returns
        # 'healthy'. That is the hole this whole script exists to cover, and it
        # should be closed properly rather than covered forever.
        $findings += New-Finding -ConnectionId $id -Condition 'NoExpectedInterval' -Severity 'ticket' -Transient $false `
            -Detail "ExpectedIntervalMinutes is not set on this connection, so crawl.vwConnectionHealth can never return 'late' for it - the view's CASE arm is gated on that column being non-null. Its freshness is being judged against the -MaxMinutesSinceSuccess fallback of $MaxMinutesSinceSuccess minutes instead of against its real cadence. Set the column to the crawl interval in crawl.Connection."
    }

    if ($null -eq $c.MinutesSinceLastSuccess) {
        # NULL IS NOT ZERO AND IS NOT FRESH. It means no run has ever succeeded,
        # and HealthReport.cs says twice that coalescing it to zero would report
        # the connection that has never once worked as the freshest in the
        # estate. It is not fresh. But it is also not ONE condition, and the
        # difference is the difference between a page and a ticket.
        #
        # THE SEVERITY IS DECIDED BY liveItems, AND IT FOLLOWS STRAIGHT FROM THE
        # EXPOSURE MODEL RATHER THAN FROM HOW ALARMING THE WORD "NEVER" SOUNDS.
        # This control exists because a dead connector lets deletions and
        # permission revocations stop propagating. A connection that has never
        # succeeded AND holds no live items has pushed nothing to Graph, so
        # there is nothing in the index to go stale and the security exposure is
        # exactly zero. It is a broken deployment - real, worth fixing, and not
        # worth waking anybody at 03:00 for.
        #
        # Live items with no successful run is a different animal entirely, and
        # it is genuinely anomalous rather than merely bad. sql/23's
        # uspPurgeHistory refuses to remove a run that any live inventory row
        # still points at through LastSeenRunId, so retention CANNOT produce
        # this state however aggressive its settings - its own header says the
        # most recent successful full run "and anything after it survive any
        # retention setting". So content is live in the index while crawl state
        # has no record of anything having put it there: a rewound or restored
        # state database, a connection re-registered over an existing Graph
        # connection, or somebody having deleted from crawl.Run by hand. In all
        # three the index is serving content that nothing now knows how to keep
        # current, which is total exposure and the loudest case in this script.
        if ($c.LiveItems -gt 0) {
            $findings += New-Finding -ConnectionId $id -Condition 'NeverSucceeded' -Severity 'page' -Transient $false `
                -Detail "No run has EVER succeeded for this connection, yet it holds $($c.LiveItems) live item(s) in the index (minutesSinceLastSuccess is null, health '$($c.Health)', last run '$($c.LastRunStatus)'). Retention cannot cause this - uspPurgeHistory will not remove a run that live inventory points at - so crawl state has been rewound, restored, or edited, or the connection was registered over an existing Graph connection. Content is being served that nothing knows how to keep current, so deletions and permission revocations for those items are not propagating and there is no timer that will make that visible."
            $measurements += ('PAGE     {0,-24} has NEVER succeeded, but holds {1} live item(s)' -f $id, $c.LiveItems)
        }
        else {
            $findings += New-Finding -ConnectionId $id -Condition 'NeverSucceeded' -Severity 'ticket' -Transient $false `
                -Detail "No run has ever succeeded for this connection and it holds no live items (health '$($c.Health)', last run '$($c.LastRunStatus)'). Nothing has been pushed, so nothing in the index is going stale and there is no security exposure yet - this is an onboarding or deployment fault rather than an incident. It stays a ticket every run until it is fixed or the connection is disabled, and it becomes a page the moment a partial push puts items in the index."
            $measurements += ('TICKET   {0,-24} has never succeeded; no live items, so nothing is going stale' -f $id)
        }
    }
    elseif ($c.MinutesSinceLastSuccess -gt $threshold) {
        $findings += New-Finding -ConnectionId $id -Condition 'Stale' -Severity 'page' -Transient $false `
            -Detail "No successful run for $($c.MinutesSinceLastSuccess) minutes, against a threshold of $threshold ($thresholdWhy). The index is still answering searches with what was pushed before that, so this is not visible to any user - what has stopped is deletion and permission-revocation propagation. Health word: '$($c.Health)'. Last run: '$($c.LastRunStatus)'."
        $measurements += ('PAGE     {0,-24} {1} min since last success, threshold {2} ({3}); health ''{4}''' -f
            $id, $c.MinutesSinceLastSuccess, $threshold, $thresholdWhy, $c.Health)
    }
    else {
        $measurements += ('OK       {0,-24} {1} min since last success, threshold {2} ({3}); health ''{4}''' -f
            $id, $c.MinutesSinceLastSuccess, $threshold, $thresholdWhy, $c.Health)
    }

    # --- 2. Failing. Trend, not event. See -FailuresToPage. ---
    if ($c.ConsecutiveFailures -ge $FailuresToPage) {
        $findings += New-Finding -ConnectionId $id -Condition 'Failing' -Severity 'page' -Transient $false `
            -Detail "$($c.ConsecutiveFailures) consecutive failed runs since the last success, at or above the -FailuresToPage threshold of $FailuresToPage. Last error kind: '$($c.ErrorKind)'. NOTE: the delete guard (sql/23, THROW 50007) surfaces here - it is rethrown as an InvalidOperationException, so its error KIND is not distinguishable from any other; the run's ErrorMessage on the connection page carries the counts that say whether it was the guard."
    }
    elseif ($c.ConsecutiveFailures -gt 0) {
        $findings += New-Finding -ConnectionId $id -Condition 'Failing' -Severity 'ticket' -Transient $false `
            -Detail "$($c.ConsecutiveFailures) consecutive failed run(s), below the -FailuresToPage threshold of $FailuresToPage. One failure is frequently transient and the connector retries on its next firing. Last error kind: '$($c.ErrorKind)'."
    }

    # --- 3. Items refused / partial. Self-healing, so a ticket - and the
    #        staleness check above is what stops it being self-hiding. A partial
    #        run is status 5, which the view's LastSuccess CTE (WHERE Status = 2)
    #        does not count, so minutesSinceLastSuccess keeps climbing and a
    #        connection stuck partial reaches the staleness page on schedule. ---
    if ($c.Health -eq 'items refused' -or $c.LastRunStatus -eq 'partial') {
        $findings += New-Finding -ConnectionId $id -Condition 'ItemsRefused' -Severity 'ticket' -Transient $true `
            -Detail "The last run was partial - Graph refused at least one item. Refused writes record no hash, so they are retried on the next run automatically; this is a ticket rather than a page because it usually clears itself. It does not hide: a partial run does not count as a success, so if it stops clearing, the freshness check above pages on schedule."
    }

    # --- 4. Deletes pending. The textbook flapper: non-zero for a few seconds
    #        of every sweep. Debounced, and reported at ticket level only. ---
    if ($c.PendingDeletes -gt 0) {
        $findings += New-Finding -ConnectionId $id -Condition 'DeletesPending' -Severity 'ticket' -Transient $true `
            -Detail "$($c.PendingDeletes) delete(s) that Graph has not confirmed. Normal for a few seconds of every sweep, which is why this needs $ConsecutivePolls consecutive polls before it counts. Persisting across polls means a DELETE is being refused and retried - an item the source dropped, still answering searches. crawl.vwPendingDeletes has the item list and their ages."
    }
}

# ---------------------------------------------------------------------------
# Debounce. Transient conditions must be seen on -ConsecutivePolls consecutive
# runs before they count; everything else escalates immediately.
#
# THE DEBOUNCE APPLIES TO ERROR-SHAPED CONDITIONS ONLY, NEVER TO TIME-SHAPED
# ONES. Staleness, never-succeeded, an unusable source and an empty estate are
# not debounced, because a threshold measured in minutes is already debounced by
# construction - it cannot fire on a blip - and adding a second delay to it
# would only make the control slower at the one job it has.
# ---------------------------------------------------------------------------

$newConditions = @{}

foreach ($f in $findings) {
    $key = "$($f.ConnectionId)|$($f.Condition)"
    $previous = $null
    if ($previousConditions.ContainsKey($key)) { $previous = $previousConditions[$key] }

    $age = 1
    $firstSeen = $startedUtc.ToString('o')
    if ($null -ne $previous) {
        $age = 1 + [int](ConvertTo-NullableInt $previous.count)

        # THE TIMESTAMP IS NORMALISED ON THE WAY BACK IN, NOT COPIED THROUGH,
        # and this was a bug before it was a comment. The first version of this
        # line was `$firstSeen = [string]$previous.firstSeenUtc`, which is
        # correct on 5.1 and wrong on 7: ConvertFrom-Json leaves an ISO-8601
        # string as a String on 5.1 and materialises it as a DateTime on 7, so
        # casting to string on 7 formats it in the current culture. A state file
        # written by 5.1 and rewritten by 7 came back holding
        # "08/30/2026 15:40:39" instead of "2026-08-30T15:40:39.0000000Z" - a
        # value that is ambiguous between locales and would eventually fail to
        # parse at all. Observed by running the two versions against the same
        # state file in sequence and reading the file between them. Round-
        # tripping through the same helper the payload uses makes the file's
        # format independent of which host last wrote it.
        $recovered = ConvertTo-UtcInstant $previous.firstSeenUtc
        if ($null -ne $recovered) { $firstSeen = $recovered.ToString('o') }

        $f.IsNew = $false
    }

    $f.Age = $age
    $newConditions[$key] = [pscustomobject]@{ firstSeenUtc = $firstSeen; count = $age }

    if ($f.Transient -and $null -eq $stateDegraded -and $age -lt $ConsecutivePolls) {
        $f.Demoted = $true
    }
}

if ($null -ne $stateDegraded) {
    $findings += New-Finding -ConnectionId '-' -Condition 'StateUnavailable' -Severity 'ticket' -Transient $false -Detail $stateDegraded
}

# ---------------------------------------------------------------------------
# The verdict.
# ---------------------------------------------------------------------------

$pages   = @($findings | Where-Object { $_.Severity -eq 'page'   -and -not $_.Demoted })
$tickets = @($findings | Where-Object { $_.Severity -eq 'ticket' -and -not $_.Demoted })
$held    = @($findings | Where-Object { $_.Demoted })

$exitCode = $ExitClean
if ($tickets.Count -gt 0) { $exitCode = $ExitTicket }
if ($pages.Count   -gt 0) { $exitCode = $ExitPage }

# ---------------------------------------------------------------------------
# The report. Every connection is printed whether or not it produced a finding,
# because a run that measured nothing and a run that measured everything and
# found it fine must not look the same on the page. An empty result is not a
# passing check.
# ---------------------------------------------------------------------------

$report = New-Object System.Text.StringBuilder
[void]$report.AppendLine("Connector health watch on $($env:COMPUTERNAME) at $($startedUtc.ToString('yyyy-MM-dd HH:mm:ss'))Z")
[void]$report.AppendLine("Source: $sourceLabel")
if ($null -ne $payloadStatus) {
    # Reported, not trusted. The endpoint's roll-up compresses, and
    # HealthReport.ByHealth's own header tells a careful monitor to use the
    # per-connection words instead - which is what the verdict above is built
    # from. This line is here so a person comparing this report to the dashboard
    # can see both numbers.
    [void]$report.AppendLine("Endpoint roll-up: '$payloadStatus', generated $($payloadGeneratedUtc.ToString('yyyy-MM-dd HH:mm:ss'))Z. Reported for comparison; the verdict below is computed from the per-connection words.")
}
[void]$report.AppendLine('')

foreach ($m in $measurements) { [void]$report.AppendLine($m) }
if ($measurements.Count -eq 0) { [void]$report.AppendLine('(no connections were measured)') }

[void]$report.AppendLine('')

foreach ($f in ($pages + $tickets)) {
    $flag = 'CONTINUING'
    if ($f.IsNew) { $flag = 'NEW' }
    $label = 'TICKET  '
    if ($f.Severity -eq 'page') { $label = 'PAGE    ' }
    [void]$report.AppendLine(('{0} {1,-24} {2}  [{3}, poll {4}]' -f $label, $f.ConnectionId, $f.Condition, $flag, $f.Age))
    [void]$report.AppendLine("         $($f.Detail)")
}

foreach ($f in $held) {
    [void]$report.AppendLine(('HELD     {0,-24} {1}  [seen on {2} of {3} required consecutive polls]' -f $f.ConnectionId, $f.Condition, $f.Age, $ConsecutivePolls))
    [void]$report.AppendLine("         $($f.Detail)")
}

[void]$report.AppendLine('')
[void]$report.AppendLine("$($connections.Count) connection(s), $(@($connections | Where-Object { $_.Enabled }).Count) enabled. $($pages.Count) page, $($tickets.Count) ticket, $($held.Count) held below the persistence threshold.")

if ($exitCode -eq $ExitClean) {
    [void]$report.AppendLine('')
    [void]$report.AppendLine('Every enabled connection has succeeded inside its freshness threshold. This event is the heartbeat: its ABSENCE is what the monitoring system must alert on, because a watchdog that only speaks when something is wrong is indistinguishable from one that has been switched off.')
}

$reportText = $report.ToString()

Write-Report ''
Write-Report $reportText

# ---------------------------------------------------------------------------
# Persist state. Written AFTER the verdict, so a write failure cannot change it.
# ---------------------------------------------------------------------------
try {
    $stateDir = Split-Path -Path $StatePath -Parent
    if (-not (Test-Path -LiteralPath $stateDir)) {
        [void](New-Item -ItemType Directory -Path $stateDir -Force)
    }

    $conditionsOut = New-Object psobject
    foreach ($k in $newConditions.Keys) {
        Add-Member -InputObject $conditionsOut -MemberType NoteProperty -Name $k -Value $newConditions[$k]
    }

    # Conditions absent from $newConditions are simply not carried forward, so a
    # cleared condition resets its count and the state file cannot grow without
    # bound as connections come and go.
    $stateOut = [pscustomobject]@{
        version         = 1
        lastRunUtc      = $startedUtc.ToString('o')
        lastExitCode    = $exitCode
        source          = $sourceLabel
        conditions      = $conditionsOut
    }

    # -Depth explicitly: ConvertTo-Json defaults to 2 on both versions, which
    # would flatten the per-condition objects into type names.
    $json = $stateOut | ConvertTo-Json -Depth 6
    [System.IO.File]::WriteAllText($StatePath, $json, (New-Object System.Text.UTF8Encoding($false)))
}
catch {
    Write-Report "WARNING  The watch state at $StatePath could not be written ($($_.Exception.Message)). Debouncing will be disabled on the next run, which makes it noisier and not quieter. The verdict above is unaffected."
}

# ---------------------------------------------------------------------------
# The event, and the exit code.
# ---------------------------------------------------------------------------

$eventId = $EventClean
$entryType = [System.Diagnostics.EventLogEntryType]::Information

if ($exitCode -eq $ExitTicket) {
    $eventId = $EventTicket
    $entryType = [System.Diagnostics.EventLogEntryType]::Warning
}
elseif ($exitCode -eq $ExitPage) {
    $eventId = $EventPage
    $entryType = [System.Diagnostics.EventLogEntryType]::Error
}

$logProblem = Write-EventSafely -EventId $eventId -EntryType $entryType -Message $reportText
if ($null -ne $logProblem) {
    Write-Report "WARNING  $logProblem"
}

Write-Report "Exit $exitCode."
exit $exitCode
