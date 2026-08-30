#Requires -Version 5.1
<#
.SYNOPSIS
    Turns several connectors on one host into one serial queue behind one
    scheduled task, so their crawls cannot stack in the same window and
    collectively exceed the tenant's Graph budget. Previews by default;
    registers nothing without -Apply.

.DESCRIPTION
    WHAT THIS EXISTS FOR. Section 7 of docs/GO-LIVE-READINESS.md, Tier 2 item
    12: "Several connectors on one host need a queue so full crawls do not stack
    on the same window, and per-connection throttle budgets so one greedy crawl
    cannot eat the tenant's Graph quota."

    The deployment this repository documents is N scheduled tasks, one per
    connector, each with its own clock trigger. docs/CDP-DEPLOYMENT.md step 9
    says to stagger them by hand - "three tasks, three hours" - and explains
    why: "they share a host, a service account and a tenant, and a full HDFS
    crawl overlapping a catalogue run buys nothing but throttling."

    That advice is correct and it does not survive contact with a growing
    estate. A hand-staggered schedule is a set of independent assumptions about
    how long each crawl takes, written down once, in separate places, and never
    revisited. The first crawl that outgrows its gap overlaps the next one and
    nothing says so. Adding a fourth connector means re-deriving all four
    offsets in somebody's head. And the arithmetic that matters - peak
    concurrent operations against Graph - is nowhere written at all.

    THE UNIT OF CONTENTION IS NOT THE HOST. This is the part worth being
    precise about, because it decides what this script can and cannot promise.

    Three candidates, and only one is right:

      The connection. Wrong: two runs against ONE connection are refused by the
      run lease (sql/43), which returns exit 5 - "skipped, not failed". That
      problem is solved elsewhere and this script must not duplicate it.

      The host. Convenient, and wrong. The measured 100-fold run on this rig was
      Graph-round-trip-bound with the writer pool 98% busy while the machine
      itself was largely idle, so the host is not the scarce resource. Two hosts
      each running four connectors against one tenant collide exactly as badly
      as one host running eight.

      The (tenant, application) pair. Right. Graph counts throttling against the
      application making the calls, and the connectors API states that "an
      application is limited to 25 concurrent operations on a connection" -
      which is why PushEngine clamps Settings:Writers to 16 and says so in the
      warning it logs. Everything that shares the app registration shares the
      budget.

    So the honest statement of what this script does: IT SERIALISES ONE HOST,
    WHICH IS A COMPLETE ANSWER ONLY WHEN ONE HOST RUNS EVERY CONNECTOR FOR ONE
    APPLICATION. That is the deployment this repository describes, and it is not
    one you may assume. Two hosts sharing an app registration need a shared
    lease, and the only shared thing this deployment already has is
    ConnectorState - the same place sql/43 puts the per-connection lease. That
    is the shape of the fix and it is deliberately not attempted here.

    HOW IT SERIALISES: ONE TASK, ONE PROCESS, A QUEUE INSIDE IT.

    The obvious design - keep N tasks and compute N start times - cannot handle
    the case the item is actually about, which is a crawl that overruns. Task
    Scheduler has no native "on completion of task X" trigger; the nearest thing
    keys off events in the Task Scheduler operational log, which has to be
    enabled and is fragile. A computed offset is a promise about durations that
    nothing enforces.

    So: ONE scheduled task, which runs this script with -Run, which executes the
    queue sequentially in its own process. At most one crawl is ever in flight
    from this host, by construction rather than by arithmetic. Peak concurrency
    against Graph becomes max(Writers) instead of sum(Writers), and the preview
    prints both numbers next to the published ceiling of 25 so the operator can
    see what serialising bought.

    Adding a connector is one object in the manifest and a re-run of this
    script. Nothing is re-derived by hand, because nothing was derived by hand.

    WHAT HAPPENS WHEN A CRAWL OVERRUNS ITS SLOT. Three things, in order:

      1. It is allowed to finish. Killing a crawl mid-write is worse than
         overrunning: the run row is left open for the store to reap as
         abandoned, the checkpoint stops where it stopped, and the next run
         redoes the work. Nothing is corrupted - the store records a hash only
         after Graph confirms the write - but nothing is gained either.
         "overrunPolicy": "kill" is available per entry for operators who
         prefer a hard stop, and the cost above is what it buys.

      2. The overrun is recorded, named, and reported in the summary. An
         entry that overran is the single most useful capacity signal this
         estate produces, and it is exactly the number docs/CAPACITY-PLANNING.md
         asks you to keep.

      3. The rest of the queue keeps its window, not its clock. Entries that
         have not started when the window closes are SKIPPED and reported -
         never started late into the morning. A crawl that runs into business
         hours is how a connector becomes the thing that made the tenant slow.

    PER-CONNECTION THROTTLE BUDGETS, AND THE LIMIT ON ENFORCING THEM.

    PushOptions.Load reads its JSON file directly with System.Text.Json. There
    is no IConfiguration, no environment-variable provider and no command-line
    provider - PushHost accepts only --connector, --dry-run and --help. So
    Settings:Writers cannot be injected at launch. A scheduler that claimed to
    set a per-connection budget at run time would be claiming something the
    engine cannot receive.

    What this script does instead is honest and, in practice, better: it READS
    each entry's appsettings, reports the Writers value the engine will actually
    use (clamped to 16 exactly as PushEngine.ResolveWriterCount clamps it),
    computes the peak concurrency the queue implies, and REFUSES TO INSTALL a
    manifest whose numbers breach the declared budget - naming the file to edit.
    The budget is enforced at the moment somebody schedules it, which is the
    moment a person is present to fix it.

    It never writes to an appsettings.json. Those are deployment artefacts owned
    by whoever deployed them, and a scheduler that edits configuration is a
    scheduler that will one day edit the wrong one.

    THE MANIFEST. One JSON file, described in docs/SCHEDULING.md, of which the
    load-bearing parts are:

      window                  start and end, local time, HH:mm
      concurrentOperationBudget  25 by default - Graph's published per-app,
                                 per-connection ceiling
      safetyFactor            multiplier on expectedMinutes to get a slot
      slotGranularityMinutes  slots round up to this
      taskPrincipal           who the single task runs as
      queue[]                 the entries, in the order they run

    Write it once, add to it, re-run this script. The schedule is derived, so
    there is nothing to keep consistent.

.PARAMETER ManifestPath
    The schedule manifest. Defaults to connector-schedule.json beside this
    script. Resolved after the param block, not in it: $PSScriptRoot is EMPTY
    inside a param() default under Windows PowerShell 5.1, which would silently
    turn the default into a path at the drive root.

.PARAMETER Run
    Execute the queue now. This is what the single scheduled task invokes.

.PARAMETER Install
    Register (or re-register) the single scheduled task. Needs -Apply.

.PARAMETER Uninstall
    Remove the scheduled task. Needs -Apply.

.PARAMETER Apply
    Actually write to Task Scheduler. Without it, -Install and -Uninstall
    describe what they would do and change nothing.

    A switch rather than SupportsShouldProcess, because -WhatIf's sibling
    -Confirm can prompt, and nothing in a deployment script for scheduled tasks
    should be able to prompt. Preview is the default and -Apply is typed.

.PARAMETER TaskName
    The scheduled task's name. Default 'ConnectorQueue'.

.PARAMETER TaskPath
    The Task Scheduler folder. Default '\', the root, matching the CDP
    deployment's existing tasks.

.PARAMETER PowerShellPath
    The host used for the registered task's action and for reconciliation
    entries. Defaults to the host running this script.

.PARAMETER StateDirectory
    Where the run summaries go. Defaults to a `schedule` folder beside this
    script.

.PARAMETER Force
    Install despite budget or configuration findings. Every finding is still
    printed. There is no switch that hides one.

.EXAMPLE
    # What an operator runs first, and after every manifest edit. Registers
    # nothing; prints the derived timetable and the budget arithmetic.
    .\Schedule-Connectors.ps1

.EXAMPLE
    # Register the single task.
    .\Schedule-Connectors.ps1 -Install -Apply

.EXAMPLE
    # What the scheduled task itself runs.
    powershell -NoProfile -ExecutionPolicy Bypass -File C:\Connectors\deploy\Schedule-Connectors.ps1 -Run -ManifestPath C:\Connectors\deploy\connector-schedule.json

.LINK
    docs/SCHEDULING.md

.LINK
    docs/CAPACITY-PLANNING.md
#>
[CmdletBinding()]
param(
    [string]$ManifestPath,
    [switch]$Run,
    [switch]$Install,
    [switch]$Uninstall,
    [switch]$Apply,
    [string]$TaskName = 'ConnectorQueue',
    [string]$TaskPath = '\',
    [string]$PowerShellPath,
    [string]$StateDirectory,
    [switch]$Force
)

# Deliberately not 'Stop'. The -Run path has to survive a connector failing and
# carry on to the next entry; a terminating error would abandon the queue
# halfway and exit with whatever code the host picked.
$ErrorActionPreference = 'Continue'

# ---------------------------------------------------------------------------
# Paths that cannot be defaulted in the param block
#
# $PSScriptRoot is EMPTY inside a param() default under Windows PowerShell 5.1
# - not null, empty - so `Join-Path $PSScriptRoot 'x'` there yields '\x' and
# resolves against the current drive root. Every path default is resolved here.
# ---------------------------------------------------------------------------

$here = $PSScriptRoot
if ([string]::IsNullOrWhiteSpace($here)) {
    $here = Split-Path -Parent $MyInvocation.MyCommand.Path
}

if ([string]::IsNullOrWhiteSpace($ManifestPath)) {
    $ManifestPath = Join-Path $here 'connector-schedule.json'
}
if ([string]::IsNullOrWhiteSpace($StateDirectory)) {
    $StateDirectory = Join-Path $here 'schedule'
}
if ([string]::IsNullOrWhiteSpace($PowerShellPath)) {
    try {
        $PowerShellPath = [System.Diagnostics.Process]::GetCurrentProcess().MainModule.FileName
    }
    catch {
        $PowerShellPath = $null
    }
    if ([string]::IsNullOrWhiteSpace($PowerShellPath)) {
        if ($PSVersionTable.PSEdition -eq 'Core') { $PowerShellPath = 'pwsh.exe' } else { $PowerShellPath = 'powershell.exe' }
    }
}

# Graph's published per-application, per-connection ceiling on concurrent
# operations. PUBLISHED BY MICROSOFT, not measured here - it is the same number
# PushEngine.ResolveWriterCount cites when it clamps Settings:Writers to 16.
$PublishedConcurrentOperations = 25

# PushEngine's own clamp. Anything above this in a configuration file is
# reported by the engine and then ignored, so this script reports the number
# the engine will use rather than the number somebody typed.
$EngineMaxWriters = 16
$EngineDefaultWriters = 4

function Get-Utf8NoBom { return (New-Object System.Text.UTF8Encoding($false)) }

$script:Findings = New-Object System.Collections.ArrayList

function Add-Finding {
    param([ValidateSet('error', 'warning')][string]$Severity, [string]$Where, [string]$Text)
    [void]$script:Findings.Add([pscustomobject]@{ Severity = $Severity; Where = $Where; Text = $Text })
}

# ---------------------------------------------------------------------------
# The manifest
# ---------------------------------------------------------------------------

if (-not (Test-Path -LiteralPath $ManifestPath)) {
    Write-Output ''
    Write-Output "No manifest at $ManifestPath."
    Write-Output ''
    Write-Output 'Create one. The smallest useful example, which docs/SCHEDULING.md explains field by field:'
    Write-Output ''
    Write-Output '  {'
    Write-Output '    "window": { "start": "01:00", "end": "06:00" },'
    Write-Output '    "concurrentOperationBudget": 25,'
    Write-Output '    "safetyFactor": 2.0,'
    Write-Output '    "slotGranularityMinutes": 15,'
    Write-Output '    "taskPrincipal": { "userId": "CORP\\svc-push$", "logonType": "Password", "runLevel": "Limited" },'
    Write-Output '    "queue": ['
    Write-Output '      { "name": "consultingwork", "kind": "crawl",'
    Write-Output '        "executable": "C:\\Connectors\\Hierarchy\\SqlHierarchyPush.exe",'
    Write-Output '        "expectedMinutes": 5, "runOn": "always" }'
    Write-Output '    ]'
    Write-Output '  }'
    Write-Output ''
    exit 3
}

$manifest = $null
try {
    $manifest = Get-Content -LiteralPath $ManifestPath -Raw -ErrorAction Stop | ConvertFrom-Json
}
catch {
    Write-Output "Manifest $ManifestPath is not valid JSON: $($_.Exception.Message)"
    exit 3
}

function Get-Field($object, [string]$name, $fallback) {
    if ($null -eq $object) { return $fallback }
    $property = $object.PSObject.Properties | Where-Object { $_.Name -eq $name } | Select-Object -First 1
    if ($null -eq $property) { return $fallback }
    if ($null -eq $property.Value) { return $fallback }
    if ($property.Value -is [string] -and [string]::IsNullOrWhiteSpace($property.Value)) { return $fallback }
    return $property.Value
}

$windowStartText = Get-Field (Get-Field $manifest 'window' $null) 'start' '01:00'
$windowEndText   = Get-Field (Get-Field $manifest 'window' $null) 'end'   '06:00'
$budget          = [int](Get-Field $manifest 'concurrentOperationBudget' $PublishedConcurrentOperations)
$safetyFactor    = [double](Get-Field $manifest 'safetyFactor' 2.0)
$granularity     = [int](Get-Field $manifest 'slotGranularityMinutes' 15)
$queue           = @(Get-Field $manifest 'queue' @())

if ($granularity -lt 1) { $granularity = 1 }
if ($safetyFactor -lt 1) {
    Add-Finding -Severity 'warning' -Where 'manifest' -Text "safetyFactor is $safetyFactor, below 1. A slot smaller than the expected duration guarantees an overrun on every cycle."
}

function ConvertTo-TimeOfDay([string]$text, [string]$field) {
    $parsed = [datetime]::MinValue
    $ok = [datetime]::TryParseExact(
        $text, 'HH\:mm', [System.Globalization.CultureInfo]::InvariantCulture,
        [System.Globalization.DateTimeStyles]::None, [ref]$parsed)
    if (-not $ok) {
        Add-Finding -Severity 'error' -Where 'manifest' -Text "window.$field is '$text', which is not HH:mm."
        return $null
    }
    return $parsed.TimeOfDay
}

$windowStart = ConvertTo-TimeOfDay $windowStartText 'start'
$windowEnd   = ConvertTo-TimeOfDay $windowEndText   'end'

$windowMinutes = 0
if ($null -ne $windowStart -and $null -ne $windowEnd) {
    $windowMinutes = [int]($windowEnd - $windowStart).TotalMinutes
    if ($windowMinutes -le 0) {
        # A window crossing midnight is the normal case for a nightly crawl.
        $windowMinutes = $windowMinutes + (24 * 60)
    }
}

if ($queue.Count -eq 0) {
    Add-Finding -Severity 'error' -Where 'manifest' -Text 'queue is empty. There is nothing to schedule.'
}

# ---------------------------------------------------------------------------
# Reading what each entry will ACTUALLY do
#
# The manifest says what an operator intends. appsettings.json says what the
# engine will do. Where they disagree the engine wins, so the preview reports
# the engine's numbers and the manifest's intent is only used for the timetable.
# ---------------------------------------------------------------------------

function Resolve-ConfigFile([string]$directory, [string]$key) {
    # Mirrors PushOptions.ResolveFile: appsettings.{key}.json when it exists,
    # appsettings.json otherwise, beside the executable.
    if (-not [string]::IsNullOrWhiteSpace($key)) {
        $specific = Join-Path $directory "appsettings.$key.json"
        if (Test-Path -LiteralPath $specific) { return $specific }
    }
    return (Join-Path $directory 'appsettings.json')
}

$entries = New-Object System.Collections.ArrayList
$ordinal = 0

foreach ($raw in $queue) {
    $ordinal = $ordinal + 1

    $name    = [string](Get-Field $raw 'name' "entry$ordinal")
    $kind    = [string](Get-Field $raw 'kind' 'crawl')
    $enabled = [bool](Get-Field $raw 'enabled' $true)
    $runOn   = Get-Field $raw 'runOn' 'always'
    $expected = [double](Get-Field $raw 'expectedMinutes' 15)
    $overrun = [string](Get-Field $raw 'overrunPolicy' 'finish')
    $connectorKey = [string](Get-Field $raw 'connectorKey' '')
    $executable = [string](Get-Field $raw 'executable' '')
    $workingDirectory = [string](Get-Field $raw 'workingDirectory' '')
    $arguments = @(Get-Field $raw 'arguments' @())

    $entry = [ordered]@{
        Ordinal          = $ordinal
        Name             = $name
        Kind             = $kind
        Enabled          = $enabled
        RunOn            = @($runOn)
        ExpectedMinutes  = $expected
        OverrunPolicy    = $overrun
        Executable       = $executable
        Arguments        = $arguments
        WorkingDirectory = $workingDirectory
        ConnectorKey     = $connectorKey
        ConfigPath       = ''
        ConnectionId     = ''
        Writers          = 0
        Incremental      = $false
        FullEveryHours   = 0
        ItemView         = ''
        SlotMinutes      = 0
    }

    if ($overrun -ne 'finish' -and $overrun -ne 'kill') {
        Add-Finding -Severity 'error' -Where $name -Text "overrunPolicy is '$overrun'; it must be 'finish' or 'kill'."
    }
    if ($expected -le 0) {
        Add-Finding -Severity 'error' -Where $name -Text "expectedMinutes is $expected. A slot cannot be derived from it."
    }

    # The slot: the expected duration times the safety factor, rounded up to the
    # granularity. It is what the timetable is built from and what an overrun is
    # measured against. It is NOT a timeout unless overrunPolicy is 'kill'.
    $slot = [Math]::Ceiling(($expected * $safetyFactor) / $granularity) * $granularity
    if ($slot -lt $granularity) { $slot = $granularity }
    $entry.SlotMinutes = [int]$slot

    if ($kind -eq 'crawl') {
        if ([string]::IsNullOrWhiteSpace($executable)) {
            Add-Finding -Severity 'error' -Where $name -Text 'kind is crawl and executable is empty.'
        }
        elseif (-not (Test-Path -LiteralPath $executable)) {
            Add-Finding -Severity 'error' -Where $name -Text "executable $executable does not exist on this host."
        }
        else {
            if ([string]::IsNullOrWhiteSpace($workingDirectory)) {
                $entry.WorkingDirectory = Split-Path -Parent $executable
            }

            $configPath = Resolve-ConfigFile $entry.WorkingDirectory $connectorKey
            $entry.ConfigPath = $configPath

            if (-not (Test-Path -LiteralPath $configPath)) {
                Add-Finding -Severity 'error' -Where $name -Text "no configuration at $configPath. PushOptions.ResolveFile looks for appsettings.<connectorKey>.json then appsettings.json, beside the executable."
            }
            else {
                $cfg = $null
                try { $cfg = Get-Content -LiteralPath $configPath -Raw -ErrorAction Stop | ConvertFrom-Json }
                catch { Add-Finding -Severity 'error' -Where $name -Text "configuration $configPath is not valid JSON: $($_.Exception.Message)" }

                if ($null -ne $cfg) {
                    $settings = Get-Field $cfg 'Settings' $null
                    $source   = Get-Field $cfg 'Source' $null
                    $graph    = Get-Field $cfg 'Graph' $null

                    $entry.ConnectionId = [string](Get-Field $graph 'ConnectionId' '')

                    $configuredWriters = [int](Get-Field $settings 'Writers' $EngineDefaultWriters)
                    $effectiveWriters = $configuredWriters
                    if ($effectiveWriters -gt $EngineMaxWriters) { $effectiveWriters = $EngineMaxWriters }
                    if ($effectiveWriters -lt 1) { $effectiveWriters = 1 }
                    $entry.Writers = $effectiveWriters

                    if ($configuredWriters -ne $effectiveWriters) {
                        Add-Finding -Severity 'warning' -Where $name -Text "Settings:Writers is $configuredWriters; PushEngine will clamp it to $effectiveWriters and log that it did. The timetable below uses $effectiveWriters."
                    }

                    $entry.Incremental = [bool](Get-Field $settings 'Incremental' $false)
                    $entry.FullEveryHours = [int](Get-Field $settings 'FullEveryHours' 168)
                    $entry.ItemView = [string](Get-Field $source 'ItemView' '')

                    # The one incremental misconfiguration everybody makes once.
                    # HierarchyPushConnector.Validate refuses it by name and the
                    # tool exits 2 - at run time, in the middle of the night, in
                    # a log nobody is reading. Catching it here costs nothing.
                    if ($entry.Incremental -and $entry.ItemView -eq 'dbo.vwExternalItems') {
                        Add-Finding -Severity 'error' -Where $name -Text "Settings:Incremental is on and Source:ItemView is dbo.vwExternalItems, which carries no EffectiveLastModified column. The connector refuses this at startup and exits 2. Point ItemView at dbo.vwExternalItemsIncremental (sql/26), or turn Incremental off."
                    }

                    if ($entry.Incremental) {
                        Add-Finding -Severity 'warning' -Where $name -Text "Settings:Incremental is on, so the delete sweep runs only when the run escalates to full - every Settings:FullEveryHours ($($entry.FullEveryHours)) hours, or on a hash-version change. Deletions and ACL revocations propagate at THAT cadence, not at this task's. See docs/SCHEDULING.md."
                    }
                }
            }
        }
    }
    elseif ($kind -eq 'reconciliation') {
        # Runs Invoke-Reconciliation.ps1 under the same host. The entry supplies
        # the arguments; this script supplies the interpreter and the script
        # path, so a manifest cannot point 'reconciliation' at something else.
        $entry.Executable = $PowerShellPath
        if ([string]::IsNullOrWhiteSpace($workingDirectory)) { $entry.WorkingDirectory = $here }
        $wrapper = Join-Path $here 'Invoke-Reconciliation.ps1'
        if (-not (Test-Path -LiteralPath $wrapper)) {
            Add-Finding -Severity 'error' -Where $name -Text "kind is reconciliation and Invoke-Reconciliation.ps1 is not beside this script."
        }
        $entry.Arguments = @('-NoProfile', '-NonInteractive', '-ExecutionPolicy', 'Bypass', '-File', $wrapper) + $arguments
    }
    else {
        Add-Finding -Severity 'error' -Where $name -Text "kind is '$kind'; it must be 'crawl' or 'reconciliation'."
    }

    [void]$entries.Add([pscustomobject]$entry)
}

# Two entries crawling one connection is a schedule that has already decided to
# collide. The sql/43 lease will refuse the second at run time and return 5 -
# correct behaviour, and a nightly exit 5 nobody understands. Refuse to
# schedule it instead.
$duplicateConnections = @($entries |
    Where-Object { $_.Kind -eq 'crawl' -and -not [string]::IsNullOrWhiteSpace($_.ConnectionId) } |
    Group-Object ConnectionId | Where-Object { $_.Count -gt 1 })
foreach ($group in $duplicateConnections) {
    Add-Finding -Severity 'error' -Where 'manifest' -Text "connection '$($group.Name)' is crawled by $($group.Count) queue entries ($(($group.Group | ForEach-Object { $_.Name }) -join ', ')). One connection, one entry: the sql/43 run lease refuses a second concurrent run against a connection and returns exit 5, so this schedules a nightly no-op."
}

# ---------------------------------------------------------------------------
# The timetable, and the budget
# ---------------------------------------------------------------------------

$due = @($entries | Where-Object { $_.Enabled })
$totalSlot = 0
foreach ($entry in $due) { $totalSlot += $entry.SlotMinutes }

$peakSerial = 0
$peakParallel = 0
foreach ($entry in $due) {
    if ($entry.Writers -gt $peakSerial) { $peakSerial = $entry.Writers }
    $peakParallel += $entry.Writers
}

if ($windowMinutes -gt 0 -and $totalSlot -gt $windowMinutes) {
    Add-Finding -Severity 'error' -Where 'manifest' -Text "the queue needs $totalSlot minute(s) of slots and the window $windowStartText-$windowEndText is $windowMinutes minute(s). It does not fit. Widen the window, lower safetyFactor, move an entry to a weekly runOn, or accept that the tail will be skipped every cycle."
}

if ($peakSerial -gt $budget) {
    Add-Finding -Severity 'error' -Where 'manifest' -Text "one entry alone asks for $peakSerial concurrent writers against a declared budget of $budget."
}

Write-Output ''
Write-Output "Connector queue - $ManifestPath"
Write-Output ''
Write-Output ("  window            {0} to {1} local ({2} minutes)" -f $windowStartText, $windowEndText, $windowMinutes)
Write-Output ("  slots             {0} minute(s) total, granularity {1}, safety factor {2}" -f $totalSlot, $granularity, $safetyFactor)
Write-Output ("  host              {0}" -f $env:COMPUTERNAME)
Write-Output ("  PowerShell        {0}" -f $PSVersionTable.PSVersion)
Write-Output ''

$clock = $windowStart
$timetable = New-Object System.Collections.ArrayList
foreach ($entry in $entries) {
    $startText = '-'
    if ($entry.Enabled -and $null -ne $clock) {
        $startText = ([datetime]::Today.Add($clock)).ToString('HH:mm')
        $clock = $clock.Add([TimeSpan]::FromMinutes($entry.SlotMinutes))
    }

    $mode = ''
    if ($entry.Kind -eq 'crawl') {
        if ($entry.Incremental) { $mode = "incremental (full every $($entry.FullEveryHours)h)" } else { $mode = 'full' }
    }

    [void]$timetable.Add([pscustomobject]@{
        Start    = $startText
        Slot     = "$($entry.SlotMinutes)m"
        Name     = $entry.Name
        Kind     = $entry.Kind
        RunOn    = ($entry.RunOn -join ',')
        # A string, not an int. Format-Table types a column from its first row,
        # so an int column meets '-' on the reconciliation row and renders it as
        # blank - a writer count that looks like zero rather than like "not
        # applicable".
        Writers  = $(if ($entry.Kind -eq 'crawl') { [string]$entry.Writers } else { '-' })
        Mode     = $mode
        Enabled  = $entry.Enabled
    })
}

$timetable | Format-Table -AutoSize | Out-String | Write-Output

Write-Output '== Graph concurrency =='
Write-Output ''
Write-Output ("  serialised (this queue)   peak {0} concurrent operation(s)" -f $peakSerial)
Write-Output ("  if these ran in parallel  peak {0} concurrent operation(s)" -f $peakParallel)
Write-Output ("  declared budget           {0}" -f $budget)
Write-Output ''
Write-Output "  The budget is Graph's published per-application, per-connection ceiling on concurrent"
Write-Output "  operations - PUBLISHED BY MICROSOFT, not measured on this rig. PushEngine clamps"
Write-Output "  Settings:Writers to $EngineMaxWriters against it, leaving room for the schema polls and ownership"
Write-Output "  checks a run makes on the same connection."
Write-Output ''
if ($peakParallel -gt $budget -and $peakSerial -le $budget) {
    Write-Output "  This is what serialising bought: $peakParallel would have exceeded $budget; $peakSerial does not."
    Write-Output ''
}

if ($script:Findings.Count -gt 0) {
    Write-Output '== Findings =='
    Write-Output ''
    foreach ($finding in $script:Findings) {
        Write-Output ("  {0,-8} {1,-24} {2}" -f $finding.Severity.ToUpper(), $finding.Where, $finding.Text)
    }
    Write-Output ''
}

$errorCount = @($script:Findings | Where-Object { $_.Severity -eq 'error' }).Count

# ---------------------------------------------------------------------------
# -Run: the queue itself
# ---------------------------------------------------------------------------

if ($Run) {
    if ($errorCount -gt 0 -and -not $Force) {
        Write-Output "Refusing to run: $errorCount error finding(s) above. Fix them, or pass -Force."
        exit 3
    }

    try {
        if (-not (Test-Path -LiteralPath $StateDirectory)) {
            [void](New-Item -ItemType Directory -Path $StateDirectory -Force -ErrorAction Stop)
        }
    }
    catch {
        Write-Warning "State directory $StateDirectory is not usable: $($_.Exception.Message). The cycle will run; its summary will not be written."
    }

    $today = (Get-Date).DayOfWeek.ToString()
    $cycleStart = Get-Date
    $windowClose = [datetime]::Today.Add($windowEnd)
    if ($windowClose -le [datetime]::Today.Add($windowStart)) {
        # The window crosses midnight.
        $windowClose = $windowClose.AddDays(1)
    }
    # A cycle started late - a delayed task, a manual run at noon - must not
    # decide it has no window at all and skip everything. If the close is
    # already behind us, give the cycle the window's full length from now and
    # say so, because a silently zero-length window looks exactly like a
    # scheduler that ran and did nothing.
    if ($windowClose -le $cycleStart) {
        $windowClose = $cycleStart.AddMinutes($windowMinutes)
        Write-Warning "This cycle started at $($cycleStart.ToString('HH:mm')), outside the $windowStartText-$windowEndText window. Using a $windowMinutes-minute window from now instead of skipping everything."
    }

    Write-Output "== Cycle =="
    Write-Output ''
    Write-Output ("  started {0}, window closes {1}, today is {2}" -f
        $cycleStart.ToString('u'), $windowClose.ToString('u'), $today)
    Write-Output ''

    $results = New-Object System.Collections.ArrayList
    $windowExhausted = $false

    foreach ($entry in $entries) {
        if (-not $entry.Enabled) {
            [void]$results.Add([pscustomobject]@{ Name = $entry.Name; Outcome = 'disabled'; ExitCode = $null; Minutes = 0; Overran = $false })
            continue
        }

        $runsToday = $false
        foreach ($day in $entry.RunOn) {
            if ([string]::Equals([string]$day, 'always', [StringComparison]::OrdinalIgnoreCase) -or
                [string]::Equals([string]$day, $today, [StringComparison]::OrdinalIgnoreCase)) {
                $runsToday = $true
            }
        }
        if (-not $runsToday) {
            [void]$results.Add([pscustomobject]@{ Name = $entry.Name; Outcome = 'not due'; ExitCode = $null; Minutes = 0; Overran = $false })
            continue
        }

        if ((Get-Date) -ge $windowClose) {
            $windowExhausted = $true
            [void]$results.Add([pscustomobject]@{ Name = $entry.Name; Outcome = 'skipped (window closed)'; ExitCode = $null; Minutes = 0; Overran = $false })
            Write-Output "  $($entry.Name): SKIPPED - the window closed before it could start."
            continue
        }

        Write-Output "  $($entry.Name): starting (slot $($entry.SlotMinutes)m, overrun policy $($entry.OverrunPolicy))"

        $entryStart = Get-Date
        $exitCode = $null
        $launchError = $null
        $killed = $false
        $proc = $null

        try {
            $startArgs = @{
                FilePath    = $entry.Executable
                NoNewWindow = $true
                PassThru    = $true
                ErrorAction = 'Stop'
            }
            if (@($entry.Arguments).Count -gt 0) { $startArgs['ArgumentList'] = @($entry.Arguments) }
            if (-not [string]::IsNullOrWhiteSpace($entry.WorkingDirectory)) { $startArgs['WorkingDirectory'] = $entry.WorkingDirectory }

            $proc = Start-Process @startArgs

            # Load-bearing under Windows PowerShell 5.1. Start-Process -PassThru
            # returns a Process whose native handle is not cached, so once the
            # child exits .ExitCode reads back as $null - empty, not zero - while
            # .HasExited is $true. Reading .Handle once while the child is alive
            # keeps a SafeProcessHandle open and the exit code survives.
            # Measured here on 5.1.26100.9168 against a child exiting 7:
            # without it "ExitCode = []", with it "ExitCode = [7]". PowerShell
            # 7.6.5 returns 7 either way.
            #
            # Everything below depends on it. A null exit code lands in the
            # `default` arm of the switch and reports a successful crawl as
            # "failed: exit ", and - worse - would report the sql/43 lease's
            # exit 5 as a failure, which is the one code this queue must read as
            # success.
            $null = $proc.Handle

            if ($entry.OverrunPolicy -eq 'kill') {
                $deadlineMs = [int]($entry.SlotMinutes * 60 * 1000)
                if (-not $proc.WaitForExit($deadlineMs)) {
                    Write-Warning "$($entry.Name) exceeded its $($entry.SlotMinutes)-minute slot and overrunPolicy is 'kill'. Killing it. The run row is left open for the store to reap as abandoned; nothing is corrupted, because the store records a hash only after Graph confirms the write, but the work since the last checkpoint is redone next time."
                    try { $proc.Kill(); [void]$proc.WaitForExit(30000) } catch { }
                    $killed = $true
                    $exitCode = -1
                }
                else {
                    $exitCode = $proc.ExitCode
                }
            }
            else {
                $proc.WaitForExit()
                $exitCode = $proc.ExitCode
            }
        }
        catch {
            $launchError = $_.Exception.Message
        }
        finally {
            if ($null -ne $proc) { $proc.Dispose() }
        }

        $minutes = [Math]::Round(((Get-Date) - $entryStart).TotalMinutes, 1)
        # A killed entry is an overrun by definition - it was stopped BECAUSE it
        # passed the deadline - and rounding to one decimal is not going to
        # agree with that on its own.
        $overran = ($minutes -gt $entry.SlotMinutes) -or $killed

        $outcome = 'failed'
        if ($launchError) {
            $outcome = "could not start: $launchError"
        }
        elseif ($killed) {
            # Not "failed: exit -1". It did not fail; it was stopped by policy,
            # and the operator who set overrunPolicy to kill should read back
            # the decision they made rather than a made-up exit code.
            $outcome = "killed at the $($entry.SlotMinutes)-minute slot deadline (overrunPolicy kill)"
        }
        elseif ($entry.Kind -eq 'crawl') {
            # THE EXIT TABLE THE QUEUE ACTS ON.
            #   0 success
            #   2 configuration invalid
            #   3 credential rejected
            #   4 ingestion failed
            #   5 skipped: another instance holds the sql/43 run lease
            #
            # FIVE IS A SUCCESS. It means the connection was already being
            # crawled and this run stood down, which is exactly what the lease
            # is for. A scheduler that treated it as a failure would page
            # somebody, nightly, for correct behaviour.
            switch ($exitCode) {
                0 { $outcome = 'succeeded' }
                5 { $outcome = 'skipped (lease held elsewhere)' }
                2 { $outcome = 'failed: configuration invalid (exit 2)' }
                3 { $outcome = 'failed: credential rejected (exit 3)' }
                4 { $outcome = 'failed: ingestion (exit 4)' }
                default { $outcome = "failed: exit $exitCode" }
            }
        }
        else {
            # Invoke-Reconciliation's own codes: 0 clean, 1 transient, and 2 and
            # above alert-worthy.
            if ($exitCode -eq 0) { $outcome = 'clean' }
            elseif ($exitCode -eq 1) { $outcome = 'transient (not alerting)' }
            else { $outcome = "reconciliation exit $exitCode - alert-worthy" }
        }

        if ($overran) {
            Write-Warning "$($entry.Name) took $minutes minute(s) against a $($entry.SlotMinutes)-minute slot. Raise expectedMinutes in the manifest, or the tail of this queue will start being skipped."
        }

        Write-Output "  $($entry.Name): $outcome in $minutes minute(s)"

        [void]$results.Add([pscustomobject]@{
            Name = $entry.Name; Outcome = $outcome; ExitCode = $exitCode; Minutes = $minutes; Overran = $overran
        })
    }

    Write-Output ''
    Write-Output '== Cycle summary =='
    Write-Output ''
    $results | Format-Table -AutoSize | Out-String | Write-Output

    $failed = @($results | Where-Object {
        $_.Outcome -like 'failed*' -or $_.Outcome -like 'could not start*' -or
        $_.Outcome -like 'killed at*' -or $_.Outcome -like '*alert-worthy*' })
    $skipped = @($results | Where-Object { $_.Outcome -like 'skipped (window closed)*' })
    $overrun = @($results | Where-Object { $_.Overran })

    try {
        $summary = [ordered]@{
            startedUtc  = $cycleStart.ToUniversalTime().ToString('o')
            finishedUtc = [DateTime]::UtcNow.ToString('o')
            host        = $env:COMPUTERNAME
            manifest    = $ManifestPath
            windowMinutes = $windowMinutes
            results     = @($results)
        }
        [System.IO.File]::WriteAllText(
            (Join-Path $StateDirectory ("cycle-" + $cycleStart.ToString('yyyyMMdd-HHmmss') + '.json')),
            ($summary | ConvertTo-Json -Depth 6), (Get-Utf8NoBom))
        [System.IO.File]::WriteAllText(
            (Join-Path $StateDirectory 'cycle-latest.json'),
            ($summary | ConvertTo-Json -Depth 6), (Get-Utf8NoBom))
    }
    catch {
        Write-Warning "Could not write the cycle summary: $($_.Exception.Message)"
    }

    if ($overrun.Count -gt 0) {
        Write-Output "$($overrun.Count) entr(y/ies) overran their slot. That is the capacity signal docs/CAPACITY-PLANNING.md asks you to keep - record it before raising expectedMinutes to make it go away."
    }

    if ($failed.Count -gt 0) {
        Write-Output "$($failed.Count) entr(y/ies) failed. Exit 2."
        exit 2
    }
    if ($windowExhausted -or $skipped.Count -gt 0) {
        Write-Output "$($skipped.Count) entr(y/ies) did not run because the window closed. Exit 1 - not a connector failure, a capacity finding."
        exit 1
    }

    Write-Output 'Every due entry ran and none failed.'
    exit 0
}

# ---------------------------------------------------------------------------
# -Uninstall
# ---------------------------------------------------------------------------

if ($Uninstall) {
    $existing = Get-ScheduledTask -TaskName $TaskName -TaskPath $TaskPath -ErrorAction SilentlyContinue
    if ($null -eq $existing) {
        Write-Output "No scheduled task '$TaskName' under '$TaskPath'. Nothing to remove."
        exit 0
    }
    if (-not $Apply) {
        Write-Output "Would remove scheduled task '$TaskName' under '$TaskPath'. Pass -Apply to do it."
        exit 0
    }
    try {
        Unregister-ScheduledTask -TaskName $TaskName -TaskPath $TaskPath -Confirm:$false -ErrorAction Stop
        Write-Output "Removed scheduled task '$TaskName'."
        exit 0
    }
    catch {
        Write-Output "Could not remove '$TaskName': $($_.Exception.Message)"
        exit 3
    }
}

# ---------------------------------------------------------------------------
# -Install
# ---------------------------------------------------------------------------

if ($Install) {
    if ($errorCount -gt 0 -and -not $Force) {
        Write-Output "Refusing to install: $errorCount error finding(s) above. Every one of them is something that would misbehave at 02:00 with nobody watching. Fix them, or pass -Force having read them."
        exit 3
    }

    $principal = Get-Field $manifest 'taskPrincipal' $null
    $userId    = [string](Get-Field $principal 'userId' '')
    $logonType = [string](Get-Field $principal 'logonType' 'Password')
    $runLevel  = [string](Get-Field $principal 'runLevel' 'Limited')

    if ([string]::IsNullOrWhiteSpace($userId)) {
        Write-Output 'taskPrincipal.userId is empty. Name the identity the queue runs as - it needs to reach SQL Server, Graph, and whatever credential store the connectors use, and every one of those is per-identity.'
        exit 3
    }

    $action = New-ScheduledTaskAction `
        -Execute $PowerShellPath `
        -Argument ("-NoProfile -NonInteractive -ExecutionPolicy Bypass -File `"$($MyInvocation.MyCommand.Path)`" -Run -ManifestPath `"$ManifestPath`"") `
        -WorkingDirectory $here

    $trigger = New-ScheduledTaskTrigger -Daily -At ([datetime]::Today.Add($windowStart))

    # LogonType Password with a gMSA supplies NO password: Windows retrieves the
    # current one from Active Directory at logon. Nothing is typed here and
    # nothing is stored here - the same stance docs/CDP-DEPLOYMENT.md step 9
    # takes for the per-connector tasks this replaces.
    $taskPrincipal = New-ScheduledTaskPrincipal -UserId $userId -LogonType $logonType -RunLevel $runLevel

    # ExecutionTimeLimit is the backstop under the per-entry policy: the queue
    # manages its own window, and this is what stops a hung cycle from still
    # being resident when tomorrow's fires. Twice the window, so a legitimate
    # overrun is handled by the queue's own rules rather than by Task Scheduler
    # killing the whole process mid-crawl.
    $limit = [TimeSpan]::FromMinutes([Math]::Max(60, $windowMinutes * 2))
    $settings = New-ScheduledTaskSettingsSet `
        -MultipleInstances IgnoreNew `
        -ExecutionTimeLimit $limit `
        -StartWhenAvailable `
        -DontStopOnIdleEnd

    if (-not $Apply) {
        Write-Output '== Would register =='
        Write-Output ''
        Write-Output "  task        $TaskPath$TaskName"
        Write-Output "  runs        $PowerShellPath -File $($MyInvocation.MyCommand.Path) -Run"
        Write-Output "  trigger     daily at $windowStartText"
        Write-Output "  principal   $userId ($logonType, $runLevel)"
        Write-Output "  time limit  $($limit.TotalMinutes) minute(s)"
        Write-Output "  instances   IgnoreNew - a cycle still running when the next fires is not doubled"
        Write-Output ''
        Write-Output 'Pass -Apply to register it. Nothing was written.'
        exit 0
    }

    try {
        $existing = Get-ScheduledTask -TaskName $TaskName -TaskPath $TaskPath -ErrorAction SilentlyContinue
        if ($null -ne $existing) {
            Unregister-ScheduledTask -TaskName $TaskName -TaskPath $TaskPath -Confirm:$false -ErrorAction Stop
        }

        [void](Register-ScheduledTask -TaskName $TaskName -TaskPath $TaskPath `
            -Action $action -Trigger $trigger -Principal $taskPrincipal -Settings $settings `
            -Description 'Serial connector queue. One task, one process, one crawl in flight. See docs/SCHEDULING.md.' `
            -ErrorAction Stop)

        Write-Output "Registered '$TaskPath$TaskName', daily at $windowStartText as $userId."
        Write-Output 'Its Last Run Result is the cycle exit code: 0 clean, 1 the window closed on part of the queue, 2 an entry failed, 3 the queue refused to run.'
        exit 0
    }
    catch {
        Write-Output "Registration failed: $($_.Exception.Message)"
        exit 3
    }
}

# ---------------------------------------------------------------------------
# No mode given: the preview above IS the output.
# ---------------------------------------------------------------------------

Write-Output 'Preview only. Nothing was registered and nothing was run.'
Write-Output '  -Install -Apply   register the single scheduled task'
Write-Output '  -Run              execute the queue now'
Write-Output '  -Uninstall -Apply remove the task'
Write-Output ''

if ($errorCount -gt 0) { exit 3 }
exit 0
