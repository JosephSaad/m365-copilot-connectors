#Requires -Version 5.1
<#
.SYNOPSIS
    Runs deploy/Compare-SourceToIndex.ps1 on a schedule, decides drift from no
    drift, and exits with a code a monitoring rule can page on. It wraps the
    comparison; it does not reimplement it.

.DESCRIPTION
    WHAT THIS EXISTS FOR. Section 7 of docs/GO-LIVE-READINESS.md, Tier 1 item 7:
    "Compare-SourceToIndex.ps1 exists and is run by hand. Schedule it weekly and
    alert on drift. It is the only check that catches the class of defect where
    the store and Graph agree with each other and both are wrong about the
    source."

    That sentence is the whole argument for this file. Every other safeguard in
    this repository is a comparison between the connector and its own memory.
    The delete sweep diffs the source against crawl.Item. Change detection
    compares the source's hash against crawl.Item. The dashboard reads
    crawl.Run. If crawl.Item is wrong - a hash recorded for a write Graph
    silently dropped, a row marked live that Graph 404s, an item confirmed
    deleted that Graph still serves - then every one of those checks agrees with
    every other one and all of them are wrong together. Nothing inside that loop
    can see out of it. Compare-SourceToIndex.ps1 is the only thing in the
    repository that asks the source and Graph directly, with the store as a
    third opinion rather than the arbiter, so it is the only thing that can
    catch a consistent lie.

    A check like that is worth nothing run by hand, because the failures it
    catches are silent by construction. Nobody goes looking.

    THE PROBLEM THIS WRAPPER SOLVES, AND IT IS NOT A SMALL ONE.
    Compare-SourceToIndex.ps1 exits 0 whether or not it finds drift. Read its
    tail: the only `exit 1` at the end is guarded by `if ($errors.Count -gt 0)`.
    A run that finds four hundred orphans, prints them in red, and prints forty
    DELETE commands for an operator to review exits 0 - green, in Task
    Scheduler's Last Run Result, indistinguishable from a clean week.

    So a scheduled task pointed straight at the comparison would alert on
    exactly the wrong thing. Its exit code answers "could I complete the
    comparison", not "did the comparison find anything", and those are different
    questions. That is not a defect in the comparison - it is an interactive
    tool whose output is read by the person who ran it - but it does mean the
    finding lives in the text, and something has to read the text. This does.

    Worse, the comparison's `exit 1` conflates four unrelated things: the SQL
    read failed, the token could not be acquired, Graph returned 403 on an item,
    and one or more rows finished in state ERROR. The first two are "the check
    could not run" and are frequently transient. The last is "the check ran and
    is incomplete", which is a different alert with a different owner. This
    wrapper separates them.

    WHAT IT DOES.

      1. Preflight. Resolves the configuration, refuses the credential
         parameters it cannot honour, and - where a state connection string is
         available - checks that the connection it is about to reconcile is
         actually a connection this comparison understands. See LIMITS below;
         this check is the guard on the sharpest edge here.

      2. Runs the comparison as a CHILD PROCESS, under the same PowerShell host
         that is running this script, with stdout and stderr redirected to a
         transcript file and stdin redirected from an empty file.

         A child process rather than dot-sourcing or `&`, for four reasons.
         The comparison calls `exit`, and `exit` semantics across an invocation
         boundary are subtle enough that no monitoring script should depend on
         them. Its `$ErrorActionPreference = 'Stop'` is set at its own top level
         and would otherwise be inherited into this script's session. A separate
         process can be given a hard deadline and killed. And its output can be
         captured to a file verbatim, which is what an operator reads at 09:00
         on Monday to see what the exit code was about.

         Stdin is redirected from an empty file deliberately. GraphPushAuth's
         Get-PushCredential falls back to `Read-Host -AsSecureString` when
         Auth:Mode is ClientSecret and the Credential Manager entry cannot be
         read under the running identity - which is exactly what happens when a
         scheduled task runs as a service account that did not store it. With a
         console, that hangs forever. With an empty stdin it fails immediately,
         and the deadline below catches anything that still does not.

      3. Parses the comparison's own report. The `== Result ==` block is
         machine-readable by construction - the comparison formats it as
         "  {0,-8} {1}" over Group-Object output - and the counts of ORPHAN,
         MISSING, STALE and ERROR are what the verdict is made of. The inventory
         lines, the -MaxItems truncation line and the closing gap notes are read
         too, because each of them changes what the counts MEAN.

      4. Decides, and exits with a code that says which decision it made.

    THE EXIT CODES, AND THE ONE SENTENCE A MONITORING RULE NEEDS.

      0  clean          Ran, coverage complete, no drift.
      1  transient      Could not run - SQL down, no token, timed out - and the
                        consecutive-failure count is still below the threshold.
      2  blind          Ran, but the verdict is withheld: rows finished in
                        ERROR, or -MaxItems cut the comparison short, or the
                        inventory was required and was not authoritative.
      3  drift          Ran, coverage complete, and found orphans, missing or
                        stale items above -DriftTolerance.
      4  stuck          Could not run, and has now failed -FailuresBeforeAlert
                        times in a row.
      5  misconfigured  This wrapper cannot proceed, or the comparison produced
                        output it could not parse. A deployment fault.

    The sentence: EXIT 0 AND 1 DO NOT PAGE. EXIT 2 AND ABOVE PAGE.

    The codes ascend by severity on purpose, so the precedence is the ordering
    and there is nothing to memorise: when a run is both blind and drifted it
    exits 3, because a confirmed finding outranks an unknown one, and the
    summary says both.

    WHY 1 IS NOT AN ALERT, AND WHY 4 EXISTS. The brief for this item says the
    check must not page on transient failure, and it should not: a weekly
    reconciliation that wakes somebody because the SQL instance was patching is
    a weekly reconciliation that gets muted, and a muted check is a deleted one.
    So a run that could not run is recorded, counted, and exits 1.

    But "does not page" must not become "is never mentioned". A check that has
    failed to run for three weeks is not a transient anything - it is a check
    that has silently stopped, which is precisely the failure mode Tier 0 item 1
    is about. The consecutive-failure counter lives in the state directory, is
    reset by any run that reaches a verdict, and turns the second consecutive
    could-not-run into exit 4. Two weeks blind is the longest this will stay
    quiet, and that number is a parameter.

    WHY A MISSING RESULT BLOCK IS 5 AND NOT 0. If the comparison exits 0 and
    this wrapper cannot find a `== Result ==` block in its output, the honest
    answer is that the two files no longer agree about the contract between
    them - somebody changed the comparison's output, or it stopped early in a
    way its own exit code did not report. Reporting "clean" from an absence of
    parsed data would be the same defect the comparison itself was fixed for:
    an empty inventory that read successfully was reporting the hard-delete gap
    CLOSED, which is a reassurance manufactured out of missing data. This
    wrapper never manufactures one. No parsed counts, no verdict.

    LIMITS. Read these before scheduling anything.

    THE COMPARISON RECONCILES dbo.Tickets AND ONLY dbo.Tickets. Its SELECT is
    literally `SELECT ... FROM dbo.Tickets ORDER BY TicketId`, and it builds the
    external item identifier as "ticket" + the integer TicketId. It does not
    generalise to the hierarchy connector, whose items are Customer, Engagement
    and TimeEntry rows drawn from three views with identifiers like `cust1` and
    `time5003`; it does not generalise to the CDP connectors at all. Section 3
    of docs/GO-LIVE-READINESS.md records this in the one place it matters: the
    inventory read and its per-connection scoping are proven live, and
    "end-to-end reconciliation is still unproven here - the only connection with
    an inventory is the hierarchy one, and this script reads dbo.Tickets".

    Scheduling the comparison against the hierarchy connection would either fail
    on a missing table or, worse, succeed: it would read whatever dbo.Tickets
    holds, ask Graph about `ticket<n>` identifiers the hierarchy connection has
    never contained, and report every one of them MISSING. A weekly alert that
    fires every week on a fiction is worse than no alert.

    So this wrapper does a preflight when it can. Given a state connection
    string it reads crawl.Connection.ConnectorKey for -ConnectionId and refuses
    with exit 5 when it is not -AssertConnectorKey. Without a state connection
    string it cannot check, says so, and proceeds - because refusing outright
    would make the state store a hard dependency of a script that is documented
    to work without one.

    IT COSTS ONE GRAPH GET PER ROW. There is no list-items API, so the
    comparison asks about every identifier individually. On this rig's corpus
    that is 111,900 GETs for one complete pass. That is a real draw on the same
    per-application Graph budget the crawls spend, and it is why this is a
    WEEKLY job placed outside the crawl window rather than a nightly one - see
    docs/SCHEDULING.md, which treats the reconciliation as one more entry in the
    queue rather than as something that happens for free.

    IT DOES NOT FIX ANYTHING. The comparison prints DELETE commands and does not
    run them, by its own deliberate design, and this wrapper does not change
    that. It reports; a person decides. Do not add a -Repair switch here: the
    connector's own full crawl performs the same reconciliation fenced by
    Settings:MaxDeletePercent, which a hand-run list of DELETEs does not have.

    IT CANNOT CARRY A SECRET. -SqlCredential and -ClientSecret cannot cross a
    process boundary, so this wrapper refuses them rather than pretending. Run
    the task under an identity that reaches SQL with Windows authentication and
    Graph with a certificate, or with a Credential Manager entry stored under
    that identity. A scheduled task with a password in its arguments is not an
    improvement on a check nobody runs.

.PARAMETER ConfigPath
    The connector's appsettings.json - the same file the push tool reads. When
    omitted, appsettings.json beside this script, which is almost certainly
    wrong for a real deployment and is reported rather than assumed.

    Not declared Mandatory on purpose: a mandatory parameter PROMPTS when it is
    missing, and a scheduled task that prompts is a scheduled task that hangs.
    Every missing input here exits 5 with a sentence instead.

.PARAMETER ConnectionId
    The external connection to reconcile. Defaults to Graph:ConnectionId from
    the configuration file, which is what the comparison does too.

.PARAMETER StateConnectionString
    Read-only connection to ConnectorState. Passed through to the comparison,
    which uses it for crawl.vwItemInventory, and used here for the connector-key
    preflight. Falls back to Settings:StateConnectionString in the configuration
    file. Needs SELECT on the crawl views: sql/25 DENYs the connector's own
    crawl_writer exactly that, so use crawl_reader or another read-only login.

.PARAMETER MaxItems
    Cap on Graph GETs, passed to the comparison. Zero, the default, means no cap
    - a complete pass. The comparison's own default is 500, which is right for a
    tool somebody is watching and wrong for a weekly reconciliation: a check
    that looked at 500 of 111,900 rows and reported nothing has not found
    nothing. A capped run is reported blind unless -AllowTruncated.

.PARAMETER AllowTruncated
    Accept a -MaxItems-truncated comparison as a verdict rather than as blind.
    Reasonable on a corpus too large to walk weekly, as long as whoever sets it
    understands that "clean" then means "clean in the part we looked at".

.PARAMETER DriftTolerance
    How many drifted items count as noise. Zero, and it should stay zero: a
    tolerance above zero is how a reconciliation check becomes decorative. It
    exists so that a source with a known, quantified, accepted churn window can
    say so in one number rather than by muting the alert.

.PARAMETER RequireInventory
    Treat a run whose inventory was absent, unreadable or empty as blind rather
    than as a verdict. Off by default, because the source-derived comparison is
    still a real comparison; on, for a connection where the hard-delete gap is
    the thing you are actually watching for.

.PARAMETER TimeoutMinutes
    Hard deadline for the comparison. Default 60. On expiry the child is killed,
    the run is recorded as could-not-run, and the transcript is kept. A
    reconciliation with no deadline is a scheduled task that can still be
    running when the next one starts.

.PARAMETER FailuresBeforeAlert
    How many consecutive could-not-run outcomes before exit 4 instead of exit 1.
    Default 2 - one missed week is weather, two is a pattern.

.PARAMETER StateDirectory
    Where the failure counter, the machine-readable summary and the transcripts
    live. Defaults to a `reconciliation` folder beside this script, resolved
    AFTER the param block: $PSScriptRoot is empty inside a param() default under
    Windows PowerShell 5.1, so a default written there would silently become the
    filesystem root.

.PARAMETER KeepTranscripts
    Transcripts retained per connection. Default 26 - six months of a weekly
    job, which is long enough to answer "when did this start".

.PARAMETER EventLogSource
    An already-registered Windows event log source to write the outcome to.
    Optional, and failing to write is never allowed to change the exit code: the
    exit code is the signal, the event is a convenience.

    Registering a source needs administrator rights and is a one-time
    deployment step, not something a scheduled task should attempt:
        New-EventLog -LogName Application -Source ConnectorReconciliation

.PARAMETER EventLogName
    The log to write to. Default Application.

.PARAMETER AssertConnectorKey
    The crawl.Connection.ConnectorKey this comparison is known to understand.
    Default sqltickets. See LIMITS.

.PARAMETER SkipConnectorKeyCheck
    Skip that preflight. For the operator who has read the limit above, and has
    a tickets-shaped connection registered under some other key.

.PARAMETER ComparePath
    The comparison to run. Defaults to Compare-SourceToIndex.ps1 beside this
    script. A parameter so a test can point it at a stub, not so an estate can
    substitute something else.

.PARAMETER Detail
    Pass -Detail through to the comparison, which then lists every row. The
    transcript gets large; the verdict does not change.

.EXAMPLE
    # What the weekly scheduled task runs. Nothing interactive, no secret.
    powershell -NoProfile -ExecutionPolicy Bypass -File C:\Connectors\deploy\Invoke-Reconciliation.ps1 -ConfigPath C:\Connectors\SqlTickets\appsettings.json -EventLogSource ConnectorReconciliation

.EXAMPLE
    # Preview against a capped pass, from an operator's own session.
    .\Invoke-Reconciliation.ps1 -ConfigPath ..\publish\appsettings.json -MaxItems 500 -AllowTruncated

.LINK
    docs/SCHEDULING.md
#>
[CmdletBinding()]
param(
    [string]$ConfigPath,
    [string]$ConnectionId,
    [string]$StateConnectionString,
    [int]$MaxItems = 0,
    [switch]$AllowTruncated,
    [int]$DriftTolerance = 0,
    [switch]$RequireInventory,
    [int]$TimeoutMinutes = 60,
    [int]$FailuresBeforeAlert = 2,
    [string]$StateDirectory,
    [int]$KeepTranscripts = 26,
    [string]$EventLogSource,
    [string]$EventLogName = 'Application',
    [string]$AssertConnectorKey = 'sqltickets',
    [switch]$SkipConnectorKeyCheck,
    [string]$ComparePath,
    [switch]$Detail
)

# Not 'Stop'. This script's entire job is to survive the failure of the thing it
# runs and report on it; a terminating error here would exit with whatever code
# the host chose rather than one of the six above. Every call that can fail is
# wrapped where it is made.
$ErrorActionPreference = 'Continue'

# ---------------------------------------------------------------------------
# The six outcomes, named once
#
# Named rather than numbered at every use site, because a bare `exit 3` in the
# middle of five hundred lines is unreviewable and the difference between 2 and
# 3 here is the difference between "we do not know" and "we know it is wrong".
# ---------------------------------------------------------------------------

$Outcome = [ordered]@{
    clean         = 0
    transient     = 1
    blind         = 2
    drift         = 3
    stuck         = 4
    misconfigured = 5
}

$script:Lines = New-Object System.Collections.ArrayList

function Say([string]$text) {
    [void]$script:Lines.Add($text)
    Write-Output $text
}

function Get-Utf8NoBom {
    # JSON with a byte order mark is a JSON file half the world cannot parse,
    # and these two files exist to be parsed by something else. The .ps1 files
    # in this repository carry a BOM; the data files this one writes must not.
    return (New-Object System.Text.UTF8Encoding($false))
}

function Write-TextFile([string]$Path, [string]$Content) {
    try {
        [System.IO.File]::WriteAllText($Path, $Content, (Get-Utf8NoBom))
        return $true
    }
    catch {
        Write-Warning "Could not write $Path`: $($_.Exception.Message)"
        return $false
    }
}

# ---------------------------------------------------------------------------
# Defaults that cannot be written in the param block
#
# $PSScriptRoot is EMPTY inside a param() default under Windows PowerShell 5.1.
# Not null - empty - so `Join-Path $PSScriptRoot 'x'` there yields '\x', which
# resolves against the current drive root and produces a script that writes its
# state to C:\ on 5.1 and beside itself on 7. This repository has been bitten by
# that before. Every path default is therefore resolved here, after the block,
# where $PSScriptRoot is populated on both hosts.
# ---------------------------------------------------------------------------

$here = $PSScriptRoot
if ([string]::IsNullOrWhiteSpace($here)) {
    # Dot-sourced from somewhere without a script root at all. Fall back to the
    # invocation path rather than to the current directory, which in a scheduled
    # task is whatever Task Scheduler felt like.
    $here = Split-Path -Parent $MyInvocation.MyCommand.Path
}

if ([string]::IsNullOrWhiteSpace($ComparePath)) {
    $ComparePath = Join-Path $here 'Compare-SourceToIndex.ps1'
}

if ([string]::IsNullOrWhiteSpace($ConfigPath)) {
    $ConfigPath = Join-Path $here 'appsettings.json'
}

if ([string]::IsNullOrWhiteSpace($StateDirectory)) {
    $StateDirectory = Join-Path $here 'reconciliation'
}

$startedUtc = [DateTime]::UtcNow
$stamp = $startedUtc.ToString('yyyyMMdd-HHmmss')

Say ''
Say "Reconciliation wrapper starting at $($startedUtc.ToString('u'))"
Say "  PowerShell $($PSVersionTable.PSVersion) on $env:COMPUTERNAME as $env:USERDOMAIN\$env:USERNAME"
Say ''

# ---------------------------------------------------------------------------
# The state directory, and the per-connection files inside it
# ---------------------------------------------------------------------------

function Get-SafeName([string]$value) {
    if ([string]::IsNullOrWhiteSpace($value)) { return 'unknown' }
    $clean = $value
    foreach ($bad in [System.IO.Path]::GetInvalidFileNameChars()) {
        $clean = $clean.Replace($bad, '_')
    }
    return $clean
}

function Complete-Run {
    <#
        The single exit point. Everything that decides an outcome calls this,
        so the state file, the summary, the transcript, the event log and the
        exit code can never disagree with each other - which they would, given
        five hundred lines and six exits scattered through them.
    #>
    param(
        [Parameter(Mandatory)][string]$OutcomeName,
        [Parameter(Mandatory)][string]$Reason,
        [hashtable]$Facts
    )

    $code = $Outcome[$OutcomeName]

    Say ''
    Say '== Verdict =='
    Say "  $OutcomeName (exit $code)"
    Say "  $Reason"
    if ($code -ge 2) {
        Say '  This outcome is alert-worthy: exit 0 and 1 do not page, exit 2 and above do.'
    }
    Say ''

    $summary = [ordered]@{
        outcome         = $OutcomeName
        exitCode        = $code
        reason          = $Reason
        connectionId    = $ConnectionId
        configPath      = $ConfigPath
        startedUtc      = $startedUtc.ToString('o')
        completedUtc    = [DateTime]::UtcNow.ToString('o')
        host            = $env:COMPUTERNAME
        identity        = "$env:USERDOMAIN\$env:USERNAME"
        powerShell      = $PSVersionTable.PSVersion.ToString()
        wrapperVersion  = 1
    }

    if ($Facts) {
        foreach ($key in $Facts.Keys) { $summary[$key] = $Facts[$key] }
    }

    if ($script:StateReady) {
        $safe = Get-SafeName $ConnectionId

        # The consecutive-failure counter. Only could-not-run outcomes advance
        # it; anything that reached a verdict - including a drifted one - clears
        # it, because the check demonstrably ran.
        $counterPath = Join-Path $StateDirectory ("counter-$safe.json")
        $consecutive = 0
        if (Test-Path -LiteralPath $counterPath) {
            try {
                $existing = Get-Content -LiteralPath $counterPath -Raw | ConvertFrom-Json
                if ($null -ne $existing.consecutiveFailures) {
                    $consecutive = [int]$existing.consecutiveFailures
                }
            }
            catch {
                Write-Warning "Could not read $counterPath ($($_.Exception.Message)); the failure counter restarts at zero."
            }
        }

        if ($OutcomeName -eq 'transient' -or $OutcomeName -eq 'stuck') {
            $consecutive = $consecutive + 1
        }
        elseif ($OutcomeName -ne 'misconfigured') {
            $consecutive = 0
        }

        $summary['consecutiveFailures'] = $consecutive

        $counter = [ordered]@{
            connectionId        = $ConnectionId
            consecutiveFailures = $consecutive
            lastOutcome         = $OutcomeName
            lastRunUtc          = [DateTime]::UtcNow.ToString('o')
        }
        [void](Write-TextFile $counterPath ($counter | ConvertTo-Json -Depth 5))

        [void](Write-TextFile (Join-Path $StateDirectory ("latest-$safe.json")) ($summary | ConvertTo-Json -Depth 5))

        $transcript = Join-Path $StateDirectory ("transcript-$safe-$stamp.log")
        [void](Write-TextFile $transcript (($script:Lines -join [Environment]::NewLine) + [Environment]::NewLine))

        # Retention, oldest first. A weekly job keeping every transcript for
        # ever is a directory nobody can read and a disk nobody expected.
        try {
            $old = @(Get-ChildItem -LiteralPath $StateDirectory -Filter "transcript-$safe-*.log" -ErrorAction Stop |
                Sort-Object LastWriteTimeUtc -Descending | Select-Object -Skip $KeepTranscripts)
            foreach ($file in $old) { Remove-Item -LiteralPath $file.FullName -Force -ErrorAction SilentlyContinue }
        }
        catch {
            Write-Warning "Transcript retention did not run: $($_.Exception.Message)"
        }

        Write-Output "  state directory: $StateDirectory"
    }

    # The event log write is best effort and is never allowed to change the
    # exit code. A monitoring estate that reads the event log gets a nicer
    # signal; one that reads Last Run Result loses nothing.
    if (-not [string]::IsNullOrWhiteSpace($EventLogSource)) {
        try {
            # The static API rather than Write-EventLog. The *-EventLog cmdlets
            # are Windows PowerShell cmdlets; under PowerShell 7 they resolve,
            # when they resolve at all, through the Windows PowerShell
            # compatibility session, which is a remoting round trip inside a
            # scheduled task. System.Diagnostics.EventLog is present on both
            # hosts and is what both cmdlets call anyway.
            if ([System.Diagnostics.EventLog]::SourceExists($EventLogSource)) {
                $entryType = [System.Diagnostics.EventLogEntryType]::Information
                if ($OutcomeName -eq 'blind') { $entryType = [System.Diagnostics.EventLogEntryType]::Warning }
                if ($code -ge 3) { $entryType = [System.Diagnostics.EventLogEntryType]::Error }

                $body = "Reconciliation of connection '$ConnectionId': $OutcomeName (exit $code).`r`n$Reason`r`n`r`nState directory: $StateDirectory"
                [System.Diagnostics.EventLog]::WriteEntry(
                    $EventLogSource, $body, $entryType, (1000 + $code))
            }
            else {
                Write-Warning "Event log source '$EventLogSource' is not registered, so nothing was written to the $EventLogName log. Register it once, as an administrator: New-EventLog -LogName $EventLogName -Source $EventLogSource"
            }
        }
        catch {
            Write-Warning "Writing to the event log failed and was ignored: $($_.Exception.Message)"
        }
    }

    exit $code
}

$script:StateReady = $false
try {
    if (-not (Test-Path -LiteralPath $StateDirectory)) {
        [void](New-Item -ItemType Directory -Path $StateDirectory -Force -ErrorAction Stop)
    }
    $script:StateReady = $true
}
catch {
    # Not fatal on its own - the exit code still works - but it does mean the
    # failure counter cannot advance, so exit 4 can never be reached and this
    # check has quietly lost its "has not run for weeks" alarm. Say so loudly.
    Write-Warning "State directory $StateDirectory is not usable: $($_.Exception.Message)"
    Write-Warning 'Without it the consecutive-failure counter cannot advance, so a check that stops running will report transient for ever and never reach exit 4.'
}

# ---------------------------------------------------------------------------
# Preflight
# ---------------------------------------------------------------------------

if (-not (Test-Path -LiteralPath $ComparePath)) {
    Complete-Run -OutcomeName 'misconfigured' -Reason "The comparison was not found at $ComparePath. This wrapper runs deploy/Compare-SourceToIndex.ps1; it does not contain a copy of it."
}

if (-not (Test-Path -LiteralPath $ConfigPath)) {
    Complete-Run -OutcomeName 'misconfigured' -Reason "Configuration file not found at $ConfigPath. Pass -ConfigPath pointing at the appsettings.json the push tool itself reads. The default is the one beside this script, which is almost never the deployed one."
}

$config = $null
try {
    $config = Get-Content -LiteralPath $ConfigPath -Raw -ErrorAction Stop | ConvertFrom-Json
}
catch {
    Complete-Run -OutcomeName 'misconfigured' -Reason "Configuration file $ConfigPath could not be read as JSON: $($_.Exception.Message)"
}

if ([string]::IsNullOrWhiteSpace($ConnectionId)) {
    if ($config.Graph -and -not [string]::IsNullOrWhiteSpace($config.Graph.ConnectionId)) {
        $ConnectionId = $config.Graph.ConnectionId
    }
}

if ([string]::IsNullOrWhiteSpace($ConnectionId)) {
    Complete-Run -OutcomeName 'misconfigured' -Reason "No connection to reconcile: -ConnectionId was not given and $ConfigPath carries no Graph:ConnectionId."
}

if ([string]::IsNullOrWhiteSpace($StateConnectionString)) {
    if ($config.Settings -and -not [string]::IsNullOrWhiteSpace($config.Settings.StateConnectionString)) {
        $StateConnectionString = $config.Settings.StateConnectionString
    }
}

Say "  configuration:  $ConfigPath"
Say "  connection:     $ConnectionId"
Say "  comparison:     $ComparePath"
Say ''

# A ClientSecret deployment with no Credential Manager target is the one
# configuration that makes the comparison try to PROMPT. Stdin is redirected
# from an empty file below so it cannot hang, but saying it here turns a
# mystifying "could not acquire a token" into a fixable sentence.
if ($config.Auth -and $config.Auth.Mode -eq 'ClientSecret') {
    if ([string]::IsNullOrWhiteSpace($config.Auth.ClientSecretCredentialTarget)) {
        Write-Warning 'Auth:Mode is ClientSecret and Auth:ClientSecretCredentialTarget is empty. The comparison will have no stored secret to read and would normally prompt; under this wrapper it will fail to acquire a token instead. Prefer Auth:Mode Certificate for a scheduled task, or store the secret under the identity the task runs as.'
    }
    else {
        Write-Warning "Auth:Mode is ClientSecret. The Credential Manager entry '$($config.Auth.ClientSecretCredentialTarget)' is per-machine AND per-user: it has to have been stored under the identity this scheduled task runs as, not under yours."
    }
}

# The connector-key preflight. See LIMITS in the header - this is the guard on
# the sharpest edge in scheduling this at all.
if ($SkipConnectorKeyCheck) {
    Say "  connector key:  not checked (-SkipConnectorKeyCheck)"
}
elseif ([string]::IsNullOrWhiteSpace($StateConnectionString)) {
    Say "  connector key:  not checked - no state connection string available"
    Write-Warning "Without a state connection string this wrapper cannot confirm that '$ConnectionId' is a connection the comparison understands. The comparison reads dbo.Tickets and builds item identifiers as 'ticket<n>'; pointed at any other connector it reports every row MISSING and does it every week."
}
else {
    $observedKey = $null
    $keyError = $null
    $stateConnection = $null
    try {
        $stateConnection = New-Object System.Data.SqlClient.SqlConnection $StateConnectionString
        $stateConnection.Open()
        $keyCommand = $stateConnection.CreateCommand()
        $keyCommand.CommandText = 'SELECT ConnectorKey FROM crawl.Connection WHERE ConnectionId = @ConnectionId;'
        $keyCommand.CommandTimeout = 30
        [void]$keyCommand.Parameters.Add(
            (New-Object System.Data.SqlClient.SqlParameter('@ConnectionId', $ConnectionId)))
        $observedKey = $keyCommand.ExecuteScalar()
    }
    catch {
        $keyError = $_.Exception.Message
    }
    finally {
        if ($null -ne $stateConnection) { $stateConnection.Dispose() }
    }

    if ($keyError) {
        # Not fatal: the state store is optional for the comparison, and a
        # permission error here is expected with the connector's own login,
        # which sql/25 DENYs SELECT on the crawl schema.
        Say "  connector key:  not checked - $keyError"
        Write-Warning 'The connector-key preflight could not read crawl.Connection. If this is a permission error, that is sql/25 working as designed with the connector''s own login; use crawl_reader for this task.'
    }
    elseif ($null -eq $observedKey -or $observedKey -is [DBNull]) {
        Say "  connector key:  '$ConnectionId' is not registered in crawl.Connection"
        Write-Warning "Connection '$ConnectionId' has no row in crawl.Connection. Either the connector has never run against it with a state store, or -ConnectionId does not name the connection you think it does."
    }
    elseif ([string]::Equals([string]$observedKey, $AssertConnectorKey, [StringComparison]::OrdinalIgnoreCase)) {
        Say "  connector key:  $observedKey (matches -AssertConnectorKey)"
    }
    else {
        Complete-Run -OutcomeName 'misconfigured' -Reason (
            "Connection '$ConnectionId' is registered under ConnectorKey '$observedKey', and this comparison only " +
            "understands '$AssertConnectorKey'. Compare-SourceToIndex.ps1 reads dbo.Tickets and constructs item " +
            "identifiers as 'ticket<n>'; against any other connector every row comes back MISSING, every week, " +
            "for ever. Point this task at the tickets connection, or pass -SkipConnectorKeyCheck if you have read " +
            "the LIMITS section and know this connection is tickets-shaped under another key.")
    }
}

Say ''

# ---------------------------------------------------------------------------
# Running the comparison
# ---------------------------------------------------------------------------

# The host running THIS script, so the child runs on the same PowerShell
# version the operator or the scheduled task chose. MainModule is not always
# readable - a constrained or virtualised process can refuse it - so the
# edition is the fallback rather than an assumption baked in.
$hostExe = $null
try {
    $hostExe = [System.Diagnostics.Process]::GetCurrentProcess().MainModule.FileName
}
catch {
    $hostExe = $null
}
if ([string]::IsNullOrWhiteSpace($hostExe)) {
    if ($PSVersionTable.PSEdition -eq 'Core') { $hostExe = 'pwsh.exe' } else { $hostExe = 'powershell.exe' }
}

$effectiveMaxItems = $MaxItems
if ($effectiveMaxItems -le 0) { $effectiveMaxItems = [int]::MaxValue }

$childArgs = @(
    '-NoProfile'
    '-NonInteractive'
    '-ExecutionPolicy', 'Bypass'
    '-File', $ComparePath
    '-ConfigPath', $ConfigPath
    '-ConnectionId', $ConnectionId
    '-MaxItems', $effectiveMaxItems
)
if (-not [string]::IsNullOrWhiteSpace($StateConnectionString)) {
    $childArgs += @('-StateConnectionString', $StateConnectionString)
}
if ($Detail) { $childArgs += '-Detail' }

$workDir = [System.IO.Path]::GetTempPath()
if ($script:StateReady) { $workDir = $StateDirectory }

$outFile = Join-Path $workDir ("compare-out-$stamp.tmp")
$errFile = Join-Path $workDir ("compare-err-$stamp.tmp")
$inFile  = Join-Path $workDir ("compare-in-$stamp.tmp")

[void](Write-TextFile $inFile '')

Say "Running the comparison (deadline $TimeoutMinutes minute(s))..."
Say ''

$childExit = $null
$timedOut = $false
$launchError = $null
$proc = $null

try {
    # Start-Process with file redirection rather than ProcessStartInfo with
    # redirected streams, because reading two redirected streams synchronously
    # from PowerShell is the classic way to deadlock a child that fills one
    # pipe buffer while you are blocked reading the other. Files have no such
    # buffer. -NonInteractive on the child and an empty stdin between them mean
    # a Read-Host inside the comparison fails rather than waits.
    $proc = Start-Process -FilePath $hostExe `
        -ArgumentList $childArgs `
        -NoNewWindow -PassThru `
        -RedirectStandardOutput $outFile `
        -RedirectStandardError $errFile `
        -RedirectStandardInput $inFile `
        -ErrorAction Stop

    # TOUCHING .Handle IS LOAD-BEARING AND IS NOT A SUPERSTITION.
    #
    # Under Windows PowerShell 5.1, a Process object returned by
    # Start-Process -PassThru comes back without its native handle cached. When
    # the child exits, Windows releases the process object, and .ExitCode then
    # reads back as $null - EMPTY, not zero - even though .HasExited is $true.
    # Reading .Handle once, while the child is still alive, makes the CLR keep
    # a SafeProcessHandle open, and the exit code survives.
    #
    # Measured on this rig, Windows PowerShell 5.1.26100.9168, with a child
    # that exits 7:
    #     without this line   ExitCode = []  HasExited=True
    #     with this line      ExitCode = [7] HasExited=True
    # PowerShell 7.6.5 returns 7 either way, which is precisely what makes this
    # dangerous: the bug is invisible to whoever develops on 7.
    #
    # It matters here more than anywhere. A null exit code would fall through
    # the `-not $sawResultBlock` branch below and be reported as "the comparison
    # exited  before producing a report" - a transient, non-paging outcome
    # manufactured from a successful run. The one thing this wrapper must never
    # do is invent a verdict out of missing data.
    $null = $proc.Handle

    if (-not $proc.WaitForExit($TimeoutMinutes * 60 * 1000)) {
        $timedOut = $true
        try {
            $proc.Kill()
            # A killed process still needs to be reaped before its redirected
            # files are released, or the read below gets a sharing violation.
            [void]$proc.WaitForExit(30000)
        }
        catch {
            Write-Warning "The comparison exceeded its deadline and could not be killed: $($_.Exception.Message)"
        }
    }
    else {
        $childExit = $proc.ExitCode
    }
}
catch {
    $launchError = $_.Exception.Message
}
finally {
    if ($null -ne $proc) { $proc.Dispose() }
}

function Read-IfPresent([string]$Path) {
    if (-not (Test-Path -LiteralPath $Path)) { return @() }
    try {
        $text = Get-Content -LiteralPath $Path -Raw -ErrorAction Stop
        if ($null -eq $text) { return @() }
        return @($text -split "`r?`n")
    }
    catch {
        Write-Warning "Could not read $Path`: $($_.Exception.Message)"
        return @()
    }
}

$stdout = Read-IfPresent $outFile
$stderr = Read-IfPresent $errFile

foreach ($line in $stdout) { [void]$script:Lines.Add($line) }
if (@($stderr | Where-Object { -not [string]::IsNullOrWhiteSpace($_) }).Count -gt 0) {
    [void]$script:Lines.Add('')
    [void]$script:Lines.Add('== stderr ==')
    foreach ($line in $stderr) { [void]$script:Lines.Add($line) }
}

foreach ($temp in @($outFile, $errFile, $inFile)) {
    Remove-Item -LiteralPath $temp -Force -ErrorAction SilentlyContinue
}

# Echo the comparison's own report. It is what the exit code is about, and a
# wrapper that swallows it makes every investigation start by re-running the
# thing by hand.
foreach ($line in $stdout) { Write-Output $line }

if ($launchError) {
    Complete-Run -OutcomeName 'misconfigured' -Reason "The comparison could not be launched at all ($hostExe): $launchError" -Facts @{ hostExecutable = $hostExe }
}

if ($timedOut) {
    $failures = 0
    if ($script:StateReady) {
        $counterPath = Join-Path $StateDirectory ("counter-$(Get-SafeName $ConnectionId).json")
        if (Test-Path -LiteralPath $counterPath) {
            try { $failures = [int]((Get-Content -LiteralPath $counterPath -Raw | ConvertFrom-Json).consecutiveFailures) } catch { $failures = 0 }
        }
    }
    $name = 'transient'
    if (($failures + 1) -ge $FailuresBeforeAlert) { $name = 'stuck' }
    Complete-Run -OutcomeName $name -Reason (
        "The comparison exceeded its $TimeoutMinutes-minute deadline and was killed. " +
        "An uncapped pass costs one Graph GET per source row, so a corpus that has grown may simply need a longer " +
        "-TimeoutMinutes; a pass that never finishes at any deadline is a Graph or SQL problem, not a timeout one.") `
        -Facts @{ timedOut = $true; timeoutMinutes = $TimeoutMinutes }
}

# ---------------------------------------------------------------------------
# Reading the comparison's report
#
# The == Result == block is machine-readable by construction: the comparison
# builds it with ("  {0,-8} {1}" -f $group.Name, $group.Count) over
# Group-Object, one line per state that actually occurred. Parsing is confined
# to the run of lines immediately after the marker, so nothing elsewhere in the
# output can be mistaken for a count.
# ---------------------------------------------------------------------------

$counts = @{}
$sawResultBlock = $false
$inResultBlock = $false
$truncatedRows = 0
$inventoryOnly = 0
$inventoryAuthoritative = $false
$inventoryEmpty = $false
$inventoryUnreadable = $false
$inventoryAbsent = $false
$nothingToDo = $false

foreach ($line in $stdout) {
    if ($line -match '^== Result ==') {
        $sawResultBlock = $true
        $inResultBlock = $true
        continue
    }

    if ($inResultBlock) {
        if ($line -match '^\s{2}([A-Z][A-Z_]*)\s+(\d+)\s*$') {
            $counts[$Matches[1]] = [int]$Matches[2]
            continue
        }
        # Any other line ends the block. The comparison emits a blank line next.
        $inResultBlock = $false
    }

    if ($line -match '^\s+(\d+) row\(s\) not examined: -MaxItems is') {
        $truncatedRows = [int]$Matches[1]
    }
    elseif ($line -match '^\s+(\d+) item\(s\) (?:are )?live in the inventory and absent from the source') {
        # Emitted twice by the comparison - once as a finding before the Graph
        # pass, once as a remediation list afterwards. Take the larger rather
        # than the sum, or one orphan is counted as two.
        $n = [int]$Matches[1]
        if ($n -gt $inventoryOnly) { $inventoryOnly = $n }
    }
    elseif ($line -match 'The hard-delete gap is CLOSED for this run') {
        $inventoryAuthoritative = $true
    }
    elseif ($line -match 'holds NO items for connection') {
        $inventoryEmpty = $true
    }
    elseif ($line -match 'Could not read crawl\.vwItemInventory') {
        $inventoryUnreadable = $true
    }
    elseif ($line -match 'there is no inventory to read') {
        $inventoryAbsent = $true
    }
    elseif ($line -match 'Nothing to do: the index matches the source') {
        $nothingToDo = $true
    }
}

function Count-Of([string]$state) {
    if ($counts.ContainsKey($state)) { return [int]$counts[$state] }
    return 0
}

$ok       = Count-Of 'OK'
$orphan   = Count-Of 'ORPHAN'
$missing  = Count-Of 'MISSING'
$stale    = Count-Of 'STALE'
$errors   = Count-Of 'ERROR'
$examined = 0
foreach ($value in $counts.Values) { $examined += [int]$value }

# Any state name the comparison emits that is not one of the five known ones.
# It would be classified by `default { 'Red' }` in the comparison's own switch,
# which is to say: it is a finding, and this wrapper does not know what kind.
$unknownStates = @($counts.Keys | Where-Object { @('OK', 'ORPHAN', 'MISSING', 'STALE', 'ERROR') -notcontains $_ })

$facts = @{
    childExitCode          = $childExit
    examined               = $examined
    ok                     = $ok
    orphan                 = $orphan
    missing                = $missing
    stale                  = $stale
    errorRows              = $errors
    inventoryOnly          = $inventoryOnly
    truncatedRows          = $truncatedRows
    inventoryAuthoritative = $inventoryAuthoritative
    maxItems               = $effectiveMaxItems
    unknownStates          = ($unknownStates -join ',')
}

Say ''
Say '== Wrapper reading =='
Say ("  child exit {0}; examined {1}; OK {2}; ORPHAN {3}; MISSING {4}; STALE {5}; ERROR {6}" -f
    $childExit, $examined, $ok, $orphan, $missing, $stale, $errors)
Say ("  inventory-only orphans {0}; rows not examined {1}; inventory authoritative: {2}" -f
    $inventoryOnly, $truncatedRows, $inventoryAuthoritative)

# ---------------------------------------------------------------------------
# The verdict
# ---------------------------------------------------------------------------

# 1. Could the comparison run at all? The comparison exits 1 for four unrelated
#    reasons; three of them produce no Result block, and those three are the
#    ones that mean "could not run".
if (-not $sawResultBlock) {
    if ($childExit -eq 0) {
        # Exited cleanly and produced no report. The contract between these two
        # files is broken and nothing here should guess which way.
        Complete-Run -OutcomeName 'misconfigured' -Reason (
            "The comparison exited 0 but produced no '== Result ==' block, so there is nothing to read a verdict " +
            "from. Reporting clean here would be a reassurance manufactured out of missing data. Check the " +
            "transcript: either the comparison's output format changed and this wrapper needs updating, or it " +
            "stopped early without saying so.") -Facts $facts
    }

    $failures = 0
    if ($script:StateReady) {
        $counterPath = Join-Path $StateDirectory ("counter-$(Get-SafeName $ConnectionId).json")
        if (Test-Path -LiteralPath $counterPath) {
            try { $failures = [int]((Get-Content -LiteralPath $counterPath -Raw | ConvertFrom-Json).consecutiveFailures) } catch { $failures = 0 }
        }
    }

    $name = 'transient'
    $tail = "This is failure $($failures + 1); the threshold for exit 4 is $FailuresBeforeAlert."
    if (($failures + 1) -ge $FailuresBeforeAlert) {
        $name = 'stuck'
        $tail = "That is $($failures + 1) consecutive could-not-run outcomes, at or past the threshold of $FailuresBeforeAlert. This check has stopped working; it is not weather."
    }

    Complete-Run -OutcomeName $name -Reason (
        "The comparison exited $childExit before producing a report - a SQL read that failed, a token it could " +
        "not acquire, or a 403 from Graph. Nothing was reconciled. $tail") -Facts $facts
}

# 2. It ran. Is the coverage complete enough to draw a verdict from?
$blindReasons = New-Object System.Collections.ArrayList

if ($errors -gt 0) {
    [void]$blindReasons.Add("$errors row(s) finished in state ERROR, so nothing is known about them")
}
if ($unknownStates.Count -gt 0) {
    [void]$blindReasons.Add("the comparison reported state(s) this wrapper does not classify: $($unknownStates -join ', ')")
}
if ($truncatedRows -gt 0 -and -not $AllowTruncated) {
    [void]$blindReasons.Add("$truncatedRows row(s) were not examined because -MaxItems cut the pass short")
}
if ($RequireInventory -and -not $inventoryAuthoritative) {
    $why = 'the inventory was not read'
    if ($inventoryEmpty) { $why = 'the inventory read successfully and held nothing for this connection' }
    elseif ($inventoryUnreadable) { $why = 'crawl.vwItemInventory could not be read' }
    elseif ($inventoryAbsent) { $why = 'no state connection string was available' }
    [void]$blindReasons.Add("-RequireInventory is set and $why, so a hard-deleted row's item could not have been found")
}

# 3. What drift did it find?
$driftTotal = $orphan + $missing + $stale + $inventoryOnly
$facts['driftTotal'] = $driftTotal

if ($driftTotal -gt $DriftTolerance) {
    $detailLines = New-Object System.Collections.ArrayList
    if ($orphan -gt 0)        { [void]$detailLines.Add("$orphan orphan(s) - deleted at the source, still indexed and still citeable") }
    if ($inventoryOnly -gt 0) { [void]$detailLines.Add("$inventoryOnly item(s) live in the inventory and absent from the source - hard-deleted rows, found only because the inventory remembered them") }
    if ($missing -gt 0)       { [void]$detailLines.Add("$missing missing item(s) - a live row the push never reached") }
    if ($stale -gt 0)         { [void]$detailLines.Add("$stale stale item(s) - indexed, but the source row has changed since") }

    $reason = "$driftTotal drifted item(s) against a tolerance of $DriftTolerance. " + ($detailLines -join '; ') + '.'
    if ($blindReasons.Count -gt 0) {
        $reason += " The comparison was ALSO incomplete (" + ($blindReasons -join '; ') + "), so the real figure is at least this and possibly larger."
    }
    $reason += ' The transcript carries the remediation commands the comparison printed; they are printed, never run.'

    Complete-Run -OutcomeName 'drift' -Reason $reason -Facts $facts
}

if ($blindReasons.Count -gt 0) {
    Complete-Run -OutcomeName 'blind' -Reason (
        "The comparison found no drift in what it looked at, but the verdict is withheld: " +
        ($blindReasons -join '; ') + ". A check that could not see everything must not report clean; " +
        "that is how a reconciliation stops meaning anything.") -Facts $facts
}

# 4. Clean. One last sanity check before saying so.
if ($examined -eq 0) {
    Complete-Run -OutcomeName 'misconfigured' -Reason (
        "The comparison produced a '== Result ==' block containing no rows at all, so it examined nothing. " +
        "Either the source returned no rows or -MaxItems is zero. Zero rows examined is not zero drift.") -Facts $facts
}

$reason = "$examined item(s) examined, all OK. No orphans, nothing missing, nothing stale."
if (-not $nothingToDo) {
    # The comparison prints "Nothing to do" only when errors, orphans and
    # missing are all zero. Reaching here without it means the two readings
    # disagree, which is worth saying out loud even though it is not fatal.
    $reason += " (Note: the comparison did not print its own 'Nothing to do' line, which it normally does on a clean pass. Check the transcript.)"
}
if ($truncatedRows -gt 0) {
    $reason += " $truncatedRows row(s) were not examined and -AllowTruncated was set, so this is clean in the part that was looked at."
}
if (-not $inventoryAuthoritative) {
    $reason += " The inventory was not authoritative on this run, so a row HARD-deleted from the source leaves an item nothing here could have found."
}

Complete-Run -OutcomeName 'clean' -Reason $reason -Facts $facts
