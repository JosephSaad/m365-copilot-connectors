<#
.SYNOPSIS
    Reads the connector log and reconstructs what every crawl did: counts,
    duration, errors, and whether the watermark chain is intact.

.DESCRIPTION
    The log already holds everything needed to answer "did the crawl run, what
    did it send, and where did it stop" — but spread over three lines per crawl
    across up to thirty rolled files. This assembles them.

    Run it on the connector host. It reads log files and nothing else.

    The check worth running it for is WATERMARK CONTINUITY. Each crawl logs the
    watermark it started from and the one it finished with. The platform stores
    that value and hands it back next time, so in a healthy connector every
    crawl starts exactly where the previous one ended:

        crawl 1  in: …|1002   out: …|1187
        crawl 2  in: …|1187   out: …|1240      <- intact

    A break in that chain means the checkpoint is not being persisted, and the
    connector is re-reading the same window forever (wasted work, and deletions
    detected late) or skipping one (items silently missing). Neither logs an
    error, because from the connector's side nothing failed — it used the
    marker it was given. Nothing else surfaces this.

.PARAMETER LogPath
    A log file, or a directory of them. Default C:\Connectors\SqlTickets\Logs.

.PARAMETER Last
    How many crawls to report, newest last. Default 20.

.PARAMETER CrawlId
    Print every line for one crawl instead of the summary table. A prefix is
    enough.

.PARAMETER ItemId
    Find the lines that mention one item, e.g. ticket1001. Per-item lines are
    written at Debug, so this finds nothing at the default Information level
    unless the item was truncated, skipped or errored — which are the cases
    logged at Warning and above.

.EXAMPLE
    .\Get-CrawlHistory.ps1

.EXAMPLE
    .\Get-CrawlHistory.ps1 -Last 50 -IncrementalMinutes 15

.EXAMPLE
    .\Get-CrawlHistory.ps1 -ItemId ticket1001
#>

[CmdletBinding()]
param(
    [string]$LogPath = 'C:\Connectors\SqlTickets\Logs',
    [int]$Last = 20,
    [string]$CrawlId,
    [string]$ItemId,
    [int]$IncrementalMinutes = 15,
    [int]$FullHours = 24
)

$ErrorActionPreference = 'Stop'

function Head([string]$name) { Write-Host "`n== $name ==" -ForegroundColor Cyan }
function Pass([string]$msg) { Write-Host "  PASS  $msg" -ForegroundColor Green }
$script:failures = 0
function Fail([string]$msg) { $script:failures++; Write-Host "  FAIL  $msg" -ForegroundColor Red }
function Warn([string]$msg) { Write-Host "  WARN  $msg" -ForegroundColor Yellow }
function Note([string]$msg) { Write-Host "  note  $msg" -ForegroundColor DarkGray }

if (-not (Test-Path $LogPath)) {
    throw "No log at $LogPath. Check Logging:Directory in appsettings.json."
}

$files = if ((Get-Item $LogPath).PSIsContainer) {
    Get-ChildItem (Join-Path $LogPath '*.log') | Sort-Object LastWriteTime
}
else {
    @(Get-Item $LogPath)
}

if ($files.Count -eq 0) { throw "No .log files under $LogPath." }

Write-Host "Reading $($files.Count) file(s) from $LogPath"
Write-Host "Oldest: $($files[0].LastWriteTime); newest: $($files[-1].LastWriteTime)"

# {Timestamp:yyyy-MM-dd HH:mm:ss.fffzzz} [{Level:u3}] [{ConnectorId}] [{CrawlId}] {Message}
$linePattern = '^(?<ts>\d{4}-\d{2}-\d{2} \d{2}:\d{2}:\d{2}\.\d{3}[+-]\d{2}:\d{2}) \[(?<lvl>\w{3})\] \[(?<cid>[^\]]*)\] \[(?<crawl>[^\]]*)\] (?<msg>.*)$'

$script:unmatched = 0
$script:firstUnmatchedFile = $null

$lines = foreach ($f in $files) {
    foreach ($raw in [System.IO.File]::ReadLines($f.FullName)) {
        $m = [regex]::Match($raw, $linePattern)

        # Exception stack traces continue on lines without a timestamp; those are
        # expected. A timestamped line that fails the pattern means template
        # drift, and dropping it silently would under-count crawls and failures.
        if (-not $m.Success -and $raw -match '^\d{4}-\d{2}-\d{2} ') {
            $script:unmatched++
            if (-not $script:firstUnmatchedFile) { $script:firstUnmatchedFile = $f.Name }
        }

        if ($m.Success) {
            [pscustomobject]@{
                Time    = [datetimeoffset]::Parse($m.Groups['ts'].Value)
                Level   = $m.Groups['lvl'].Value
                CrawlId = $m.Groups['crawl'].Value
                Message = $m.Groups['msg'].Value
                Raw     = $raw
            }
        }
    }
}

$lines = @($lines)
if ($lines.Count -eq 0) {
    throw 'No lines matched the expected log format. Was the output template changed in LoggingSetup.cs?'
}

if ($script:unmatched -gt 0) {
    Warn ("$($script:unmatched) timestamped line(s) did not match the expected format (first in " +
        "$($script:firstUnmatchedFile)). Counts below may be low; check the output template in LoggingSetup.cs.")
}

# --- one crawl in full -----------------------------------------------------

if ($CrawlId) {
    Head "Crawl $CrawlId"
    $hits = @($lines | Where-Object { $_.CrawlId -like "$CrawlId*" })
    if ($hits.Count -eq 0) {
        Fail "no lines with a CrawlId starting '$CrawlId'"
        exit 1
    }
    $hits | ForEach-Object { Write-Host $_.Raw }
    Write-Host "`n$($hits.Count) line(s)."
    exit 0
}

# --- one item --------------------------------------------------------------

if ($ItemId) {
    Head "Item $ItemId"
    $hits = @($lines | Where-Object { $_.Message -match [regex]::Escape($ItemId) })
    if ($hits.Count -eq 0) {
        Note "no mention of $ItemId."
        Note 'Per-item lines are written at Debug. At the default Information level only truncated, skipped and'
        Note 'errored items appear — so silence here means the item was processed normally, or not seen at all.'
        Note 'To tell those apart, set Logging:MinimumLevel to Debug, restart, force a crawl, and put it back.'
    }
    else {
        $hits | ForEach-Object { Write-Host $_.Raw }
        Write-Host "`n$($hits.Count) line(s)."
    }
    exit 0
}

# --- crawl summaries -------------------------------------------------------

$summaryPattern = '^(?<op>.+?) summary: items=(?<items>\d+) deleted=(?<deleted>\d+) skipped=(?<skipped>\d+) ' +
                  'truncated=(?<truncated>\d+) contentBytes=(?<bytes>\d+) sqlRoundTrips=(?<trips>\d+) ' +
                  'durationMs=(?<ms>\d+) errors=(?<errors>.*?) watermarkIn=(?<in>.*?) watermarkOut=(?<out>.*)$'

$crawls = foreach ($l in $lines) {
    $m = [regex]::Match($l.Message, $summaryPattern)
    if ($m.Success) {
        [pscustomobject]@{
            Time       = $l.Time
            CrawlId    = $l.CrawlId
            Operation  = $m.Groups['op'].Value
            Items      = [int]$m.Groups['items'].Value
            Deleted    = [int]$m.Groups['deleted'].Value
            Skipped    = [int]$m.Groups['skipped'].Value
            Truncated  = [int]$m.Groups['truncated'].Value
            DurationMs = [int]$m.Groups['ms'].Value
            Errors     = $m.Groups['errors'].Value.Trim()
            In         = $m.Groups['in'].Value.Trim()
            Out        = $m.Groups['out'].Value.Trim()
        }
    }
}

$crawls = @($crawls | Sort-Object Time)

Head 'Crawls'
if ($crawls.Count -eq 0) {
    Fail 'no completed crawl in these logs'
    Note 'A crawl that starts and never summarises either failed or was cancelled — look for "failed after" below.'
}
else {
    $shown = if ($crawls.Count -gt $Last) { $crawls[-$Last..-1] } else { $crawls }
    $shown | Format-Table -AutoSize @(
        @{ Label = 'When'; Expression = { $_.Time.ToString('MM-dd HH:mm:ss') } }
        @{ Label = 'Operation'; Expression = { $_.Operation } }
        @{ Label = 'Crawl'; Expression = { if ($_.CrawlId.Length -ge 8) { $_.CrawlId.Substring(0, 8) } else { $_.CrawlId } } }
        @{ Label = 'Items'; Expression = { $_.Items } }
        @{ Label = 'Del'; Expression = { $_.Deleted } }
        @{ Label = 'Skip'; Expression = { $_.Skipped } }
        @{ Label = 'Trunc'; Expression = { $_.Truncated } }
        @{ Label = 'ms'; Expression = { $_.DurationMs } }
        @{ Label = 'Errors'; Expression = { if ($_.Errors -eq '{}' -or $_.Errors -eq '[]') { '' } else { $_.Errors } } }
    ) | Out-String | Write-Host

    Pass "$($crawls.Count) completed crawl(s); showing the last $($shown.Count)"
}

# --- watermark continuity --------------------------------------------------

Head 'Watermark chain'
if ($crawls.Count -lt 2) {
    Note 'need at least two crawls to check continuity'
}
else {
    $breaks = 0
    $repeats = 0
    for ($i = 1; $i -lt $crawls.Count; $i++) {
        $prev = $crawls[$i - 1]
        $this = $crawls[$i]

        # A full crawl is entitled to restart from the beginning, so only
        # compare within the same operation type.
        if ($this.Operation -ne $prev.Operation) { continue }

        if ($this.In -ne $prev.Out) {
            $breaks++
            if ($breaks -le 5) {
                Fail "$($this.Operation) at $($this.Time.ToString('MM-dd HH:mm:ss')) resumed at '$($this.In)' but the previous one ended at '$($prev.Out)'"
            }
        }
        if ($this.In -eq $prev.In -and $this.Out -eq $prev.Out -and $this.Items -eq 0) {
            $repeats++
        }
    }

    if ($breaks -eq 0) {
        Pass 'every crawl resumed exactly where the previous one ended'
    }
    else {
        if ($breaks -gt 5) { Note "$($breaks - 5) further break(s) not shown" }
        Note 'The platform stores the checkpoint and hands it back. A break means it is not being stored, or the'
        Note 'connection was recrawled from scratch. Compare against the recrawl history in the admin centre before'
        Note 'treating it as a fault: a deliberate full recrawl looks exactly like this.'
    }

    if ($repeats -gt 3) {
        Warn "$repeats crawl(s) re-read the same window and returned nothing"
        Note 'Idle crawls over an unchanged table are normal. Idle crawls while the table IS changing means the'
        Note 'watermark is ahead of the data — run Test-SqlSource.ps1 and look at the future-timestamp check.'
    }
}

$fallbacks = @($lines | Where-Object { $_.Message -match 'is not a watermark this build understands' })
if ($fallbacks.Count -gt 0) {
    Warn "$($fallbacks.Count) crawl(s) could not read the checkpoint they were given"
    Note 'Once, straight after an upgrade, is expected and harmless. Every crawl means the agent is not storing it.'
}

# --- failures and gaps -----------------------------------------------------

Head 'Failures'
$failed = @($lines | Where-Object { $_.Message -match 'failed after \d+ item\(s\)\. Category: (?<cat>\w+)' })
if ($failed.Count -eq 0) {
    Pass 'no failed crawl in these logs'
}
else {
    $byCategory = $failed | Group-Object { [regex]::Match($_.Message, 'Category: (\w+)').Groups[1].Value }
    foreach ($g in $byCategory) {
        Fail "$($g.Count) crawl(s) failed with Category: $($g.Name)"
    }
    Note 'Authentication and Validation fail the whole connection. Transient returns RetryDetails and the platform'
    Note 're-drives the crawl — repeated Transient failures are a SQL health problem, not a connector one.'
    Note "Most recent: $($failed[-1].Raw)"
}

$fatal = @($lines | Where-Object { $_.Level -eq 'FTL' })
if ($fatal.Count -gt 0) {
    Fail "$($fatal.Count) Fatal line(s); most recent: $($fatal[-1].Message)"
}

Head 'Cadence'
if ($crawls.Count -ge 2) {
    foreach ($op in @('Incremental crawl', 'Full crawl')) {
        $ofType = @($crawls | Where-Object { $_.Operation -eq $op })
        if ($ofType.Count -lt 2) { continue }

        $gaps = for ($i = 1; $i -lt $ofType.Count; $i++) {
            ($ofType[$i].Time - $ofType[$i - 1].Time).TotalMinutes
        }
        $longest = [Math]::Round(($gaps | Measure-Object -Maximum).Maximum, 1)
        $typical = [Math]::Round(($gaps | Measure-Object -Average).Average, 1)
        $expected = if ($op -eq 'Full crawl') { $FullHours * 60 } else { $IncrementalMinutes }

        Note "$op — $($ofType.Count) run(s), typical gap $typical min, longest $longest min (schedule: $expected min)"
        if ($longest -gt ($expected * 3)) {
            Warn "a $longest minute gap is more than three times the schedule: the agent stopped calling for a while"
        }
    }

    $sinceLast = [Math]::Round(([datetimeoffset]::Now - $crawls[-1].Time).TotalMinutes, 1)
    if ($sinceLast -gt ($IncrementalMinutes * 4)) {
        Warn "last crawl was $sinceLast minutes ago"
        Note 'Nothing has been called since. Check the connection state in the admin centre and stage 3 on the host.'
    }
    else {
        Pass "last crawl $sinceLast minute(s) ago"
    }
}

Write-Host ''
Write-Host 'This covers stages 2 and 5 of docs/TROUBLESHOOTING.md: what the connector'
Write-Host 'sent, and whether it was asked. What Graph did with it is stage 5 onward —'
Write-Host 'deploy\Verify-GraphConnection.ps1.'

# Like every sibling diagnostic: red findings are a non-zero exit, so anything
# scripted around this learns the result without parsing colours.
if ($script:failures -gt 0) { exit 1 }
