<#
.SYNOPSIS
    Proves stage 1 on its own: that the source table is readable, shaped as the
    connector expects, and that its LastModified values can carry a watermark.

.DESCRIPTION
    Runs the connector's own queries against dbo.Tickets with the identity you
    run it as, so a failure here is a database problem and not a connector one.
    Read-only throughout: SELECT and catalogue views, nothing else.

    Run it AS THE SERVICE ACCOUNT where you can (psexec, or a scheduled task —
    see docs/RUNBOOK.md §2a for both). Run as yourself and a pass proves the
    table is fine while saying nothing about whether the connector can read it.

    Three checks here are not obvious and each one explains a real symptom:

    1. FUTURE TIMESTAMPS POISON THE WATERMARK. The crawl resumes from
       (LastModified, TicketId) and only ever moves forward. One row written
       with a timestamp in the future — a local time in a UTC column, a clock
       skew, a backdated import — drags the watermark past every later edit, and
       those edits are then never crawled. The symptom is "some tickets stopped
       updating" with a clean log and no errors at all.

    2. LOCAL TIME IN A UTC COLUMN. The connector compares LastModified against
       UTC. A column populated with GETDATE() rather than SYSUTCDATETIME() is
       wrong by the UTC offset, which is invisible in winter in a UTC tenant and
       one hour of missing changes in summer. Detected by comparing the newest
       row against both clocks.

    3. OVERSIZE BODIES. Items over the platform cap are truncated with a warning
       and, if still too large, skipped entirely. Counting them here tells you
       whether "missing from Copilot" is a size problem before you go looking in
       Graph.

.PARAMETER TicketId
    Inspect one ticket: its item ID, timestamp, delete flag and body size, and
    whether the current watermark would include it.

.PARAMETER Watermark
    A watermark string copied from a crawl log line ("v2|2026-08-13T09:00:00.0000000Z|1002"),
    or just the timestamp. Reports how many rows a crawl resuming there would see.

.PARAMETER Credential
    For DataSource:SqlAuthMode = SqlLogin. Omit for WindowsIntegrated, which is
    the shipped configuration. The password is passed to the driver as a
    SecureString and is never written to the console, a variable or a log.

.EXAMPLE
    .\Test-SqlSource.ps1

.EXAMPLE
    .\Test-SqlSource.ps1 -TicketId 1001 -Watermark 'v2|2026-08-13T09:00:00.0000000Z|1002'

.EXAMPLE
    .\Test-SqlSource.ps1 -Server sql01.contoso.local -Database Ops -Credential (Get-Credential)
#>

[CmdletBinding()]
param(
    [string]$ConfigPath = 'C:\Connectors\SqlTickets\appsettings.json',
    [string]$Server,
    [string]$Database,
    [string]$Table = 'dbo.Tickets',
    [int]$TicketId = 0,
    [string]$Watermark,
    [int]$MaxContentBytes = 0,
    [System.Management.Automation.PSCredential]$Credential,
    [int]$TimeoutSeconds = 15
)

$ErrorActionPreference = 'Stop'
$script:failures = 0

function Step([string]$name) { Write-Host "`n== $name ==" -ForegroundColor Cyan }
function Pass([string]$msg) { Write-Host "  PASS  $msg" -ForegroundColor Green }
function Fail([string]$msg) { Write-Host "  FAIL  $msg" -ForegroundColor Red; $script:failures++ }
function Warn([string]$msg) { Write-Host "  WARN  $msg" -ForegroundColor Yellow }
function Note([string]$msg) { Write-Host "  note  $msg" -ForegroundColor DarkGray }

# Defaults come from the deployed configuration so the two cannot disagree.
if (Test-Path $ConfigPath) {
    $config = Get-Content $ConfigPath -Raw | ConvertFrom-Json
    if (-not $Server) { $Server = $config.DataSource.Server }
    if (-not $Database) { $Database = $config.DataSource.Database }
    if ($MaxContentBytes -le 0 -and $config.DataSource.MaxContentBytes) {
        $MaxContentBytes = [int]$config.DataSource.MaxContentBytes
    }
    Write-Host "Defaults from $ConfigPath"
}
if ($MaxContentBytes -le 0) { $MaxContentBytes = 3670016 }

if (-not $Server -or -not $Database) {
    throw 'Server and Database are required. Pass them, or point -ConfigPath at a deployed appsettings.json.'
}

Write-Host "SQL source diagnostics — $Server / $Database / $Table"
Write-Host "Running as $([Security.Principal.WindowsIdentity]::GetCurrent().Name)"

# The builder is never printed and never logged: with SqlLogin it holds the
# password. Everything user-visible below names the server, not the string.
$builder = New-Object System.Data.SqlClient.SqlConnectionStringBuilder
$builder['Data Source'] = $Server
$builder['Initial Catalog'] = $Database
$builder['Connect Timeout'] = $TimeoutSeconds
$builder['Application Name'] = 'Test-SqlSource'
$builder['Encrypt'] = $true

if ($Credential) {
    # Integrated Security off and no User ID in the string: SqlCredential holds
    # the password as a SecureString, so it never exists as plain text in this
    # process, in a variable you could print, or in a connection string anything
    # might log.
    $builder['Integrated Security'] = $false
    Note "SQL login authentication as $($Credential.UserName)"
}
else {
    $builder['Integrated Security'] = $true
    Note 'Windows integrated authentication (the shipped configuration)'
}

# Encrypt=true matches SqlConnectionStringFactory exactly. TrustServerCertificate
# is deliberately absent: the connector rejects it outright in Production, so a
# probe that set it would pass where the connector fails.

$connection = New-Object System.Data.SqlClient.SqlConnection $builder.ConnectionString
if ($Credential) {
    $password = $Credential.Password.Copy()
    $password.MakeReadOnly()
    $connection.Credential = New-Object System.Data.SqlClient.SqlCredential($Credential.UserName, $password)
}

function Invoke-Sql([string]$sql, [hashtable]$parameters) {
    $command = $connection.CreateCommand()
    $command.CommandText = $sql
    $command.CommandTimeout = $TimeoutSeconds
    if ($parameters) {
        foreach ($key in $parameters.Keys) {
            $null = $command.Parameters.AddWithValue($key, $parameters[$key])
        }
    }
    $adapter = New-Object System.Data.SqlClient.SqlDataAdapter $command
    $result = New-Object System.Data.DataTable
    $null = $adapter.Fill($result)
    return $result
}

# ---------------------------------------------------------------------------

Step 'Connect'
try {
    $connection.Open()
    Pass "connected to $($connection.DataSource), version $($connection.ServerVersion)"
}
catch [System.Data.SqlClient.SqlException] {
    $number = $_.Exception.Number
    Fail "connection failed (SQL error $number): $($_.Exception.Message)"
    switch ($number) {
        18456 { Note 'Login failed: the identity is not a login on this instance, or the password is wrong.' }
        4060  { Note "Cannot open '$Database': the login exists but has no user in that database. Run sql\01-least-privilege.sql." }
        18452 { Note 'Untrusted domain / no trust relationship — usually a service account outside the domain.' }
        -2    { Note 'Timeout. This is exactly what the wizard shows as "the data source did not respond within 20 seconds".' }
        default { Note 'See docs/RUNBOOK.md §4.4 for the authentication category the connector reports for this.' }
    }
    exit 1
}
catch {
    Fail "connection failed: $($_.Exception.Message)"
    exit 1
}

try {
    Step 'The validation query the wizard runs'
    # Byte for byte what ValidateAuthentication executes. Selecting IsDeleted is
    # deliberate: it makes a missing soft-delete column a wizard error rather
    # than a first-crawl failure.
    try {
        $probe = Invoke-Sql "SELECT TOP 1 TicketId, IsDeleted FROM $Table;"
        Pass "SELECT TOP 1 TicketId, IsDeleted FROM $Table succeeded ($($probe.Rows.Count) row)"
    }
    catch {
        Fail "the wizard's validation query fails: $($_.Exception.Message)"
        Note 'If the error names IsDeleted, run sql\02-soft-delete.sql, or set DataSource:SoftDeleteEnabled to false'
        Note 'and accept that deletions then only leave the index at the next full crawl.'
    }

    Step 'Permissions'
    $perms = Invoke-Sql "SELECT permission_name, state_desc FROM sys.fn_my_permissions(@t, 'OBJECT');" @{ '@t' = $Table }
    $granted = @($perms.Rows | Where-Object { $_.state_desc -like 'GRANT*' } | ForEach-Object { $_.permission_name })
    $write = @($granted | Where-Object { $_ -in @('INSERT', 'UPDATE', 'DELETE', 'ALTER', 'CONTROL') })

    if ($granted -contains 'SELECT') {
        Pass "SELECT granted on $Table"
    }
    else {
        Fail "no SELECT on $Table for this identity. Run sql\01-least-privilege.sql against $Database."
    }
    if ($write.Count -gt 0) {
        Warn "this identity also holds $($write -join ', '). The connector needs SELECT and nothing else."
        Note 'sql\01-least-privilege.sql DENYs the write surface explicitly so a future role change cannot widen it.'
    }
    else {
        Note 'read-only, as intended'
    }

    Step 'Table shape'
    $columns = Invoke-Sql @"
SELECT c.name AS column_name, t.name AS data_type, c.is_nullable, c.max_length
FROM   sys.columns AS c
JOIN   sys.types   AS t ON t.user_type_id = c.user_type_id
WHERE  c.object_id = OBJECT_ID(@t);
"@ @{ '@t' = $Table }

    $have = @($columns.Rows | ForEach-Object { $_.column_name })
    foreach ($required in @('TicketId', 'Title', 'Status', 'AssignedTo', 'Body', 'LastModified')) {
        if ($have -contains $required) { Pass "$required present" }
        else { Fail "$required is missing — the crawl cannot build an item without it" }
    }
    if ($have -contains 'IsDeleted') {
        Pass 'IsDeleted present (incremental deletes are possible)'
    }
    else {
        Warn 'IsDeleted is missing. Deletions can only reach the index by a full crawl.'
        Note 'Run sql\02-soft-delete.sql, or set DataSource:SoftDeleteEnabled to false to stop the connector asking for it.'
    }

    $lastModified = $columns.Rows | Where-Object { $_.column_name -eq 'LastModified' }
    if ($lastModified -and $lastModified.is_nullable) {
        Warn 'LastModified is nullable. A NULL never satisfies the watermark predicate, so those rows are invisible'
        Warn 'to every incremental crawl and only ever appear via a full crawl.'
    }

    $indexes = Invoke-Sql "SELECT name, type_desc FROM sys.indexes WHERE object_id = OBJECT_ID(@t) AND name IS NOT NULL;" @{ '@t' = $Table }
    $names = @($indexes.Rows | ForEach-Object { $_.name })
    if ($names -contains 'IX_Tickets_LastModified_TicketId') {
        Pass 'IX_Tickets_LastModified_TicketId present — incremental crawls seek rather than scan'
    }
    else {
        Warn "the composite watermark index is missing. Present: $($names -join ', ')"
        Note 'Not a correctness problem; every incremental crawl becomes a table scan. sql\02-soft-delete.sql creates it.'
    }

    Step 'Volume'
    $hasDeleted = $have -contains 'IsDeleted'
    $countSql = if ($hasDeleted) {
        "SELECT COUNT(*) AS total, SUM(CASE WHEN IsDeleted = 0 THEN 1 ELSE 0 END) AS live, SUM(CASE WHEN IsDeleted = 1 THEN 1 ELSE 0 END) AS tombstones FROM $Table;"
    }
    else {
        "SELECT COUNT(*) AS total, COUNT(*) AS live, 0 AS tombstones FROM $Table;"
    }
    $counts = (Invoke-Sql $countSql).Rows[0]
    Pass "$($counts.total) row(s): $($counts.live) live, $($counts.tombstones) soft-deleted"
    Note 'Live rows are what a full crawl indexes, and each one consumes tenant item quota.'

    Step 'Timestamps and the watermark'
    $clock = (Invoke-Sql @"
SELECT SYSUTCDATETIME() AS utc_now,
       SYSDATETIME()    AS local_now,
       MIN(LastModified) AS oldest,
       MAX(LastModified) AS newest,
       SUM(CASE WHEN LastModified > SYSUTCDATETIME() THEN 1 ELSE 0 END) AS in_future
FROM   $Table;
"@).Rows[0]

    if ([int]$counts.total -eq 0 -or $clock.in_future -eq [DBNull]::Value) {
        # Aggregates over an empty table return NULL, and [int]DBNull is a
        # terminating cast error that would kill the diagnostic mid-run - before
        # the watermark, item-size and summary sections it exists to print.
        Warn 'the table is empty; skipping the timestamp checks and the -Watermark pending-rows check'
    }
    else {

    Pass "oldest $($clock.oldest), newest $($clock.newest)"
    Note "server clock: $($clock.utc_now) UTC / $($clock.local_now) local"

    if ([int]$clock.in_future -gt 0) {
        Fail "$($clock.in_future) row(s) have LastModified in the future"
        Note 'The watermark only moves forward. Every one of these drags it past edits that have not been crawled yet,'
        Note 'and those edits will never be picked up incrementally. Correct the timestamps, then force a full crawl.'
    }
    else {
        Pass 'no rows timestamped in the future'
    }

    # Local time in a UTC column: the newest row lands between the two clocks
    # and close to the local one. Only meaningful where the offset is non-zero.
    $offsetHours = [Math]::Round(([datetime]$clock.local_now - [datetime]$clock.utc_now).TotalHours, 2)
    if ([Math]::Abs($offsetHours) -ge 0.5 -and $clock.newest -ne [DBNull]::Value) {
        $toUtc = ([datetime]$clock.utc_now - [datetime]$clock.newest).TotalHours
        if ($toUtc -lt -0.25 -and $toUtc -gt (-1 * [Math]::Abs($offsetHours) - 0.5)) {
            Warn "the newest row is $([Math]::Round(-$toUtc, 2))h ahead of UTC, and this server is $offsetHours h from UTC."
            Warn 'That pattern says LastModified is being written in LOCAL time. The connector compares against UTC.'
            Note 'Fix the writer to use SYSUTCDATETIME(). Until then the watermark runs ahead by the UTC offset.'
        }
    }

    if ($Watermark) {
        $mark = $Watermark
        $markId = 0
        if ($Watermark -match '^v\d+\|([^|]+)\|(\d+)$') {
            $mark = $Matches[1]
            $markId = [int]$Matches[2]
        }
        try {
            $ahead = (Invoke-Sql @"
SELECT COUNT(*) AS pending
FROM   $Table
WHERE  (LastModified > @w OR (LastModified = @w AND TicketId > @id));
"@ @{ '@w' = [datetime]$mark; '@id' = $markId }).Rows[0].pending

            Pass "a crawl resuming at $mark / $markId would see $ahead row(s)"
            if ([int]$ahead -eq 0) {
                Note 'Zero pending rows and missing items together means the watermark is already past them —'
                Note 'the future-timestamp case above, or the row was edited without touching LastModified.'
            }
        }
        catch {
            Fail "could not parse -Watermark '$Watermark': $($_.Exception.Message)"
            Note 'Copy it from a log line: watermarkOut=v2|2026-08-13T09:35:12.4410000Z|1187'
        }
    }
    }

    Step 'Item size'
    # DATALENGTH on NVARCHAR is bytes at 2 per character; the connector measures
    # UTF-8, which is smaller for Latin text. This over-reports rather than
    # under-reports, which is the safe direction for a warning.
    $oversize = (Invoke-Sql "SELECT COUNT(*) AS n FROM $Table WHERE DATALENGTH(Body) > @max;" @{ '@max' = $MaxContentBytes }).Rows[0].n
    if ([int]$oversize -eq 0) {
        Pass "no row's Body exceeds $MaxContentBytes bytes"
    }
    else {
        Warn "$oversize row(s) have a Body over $MaxContentBytes bytes (UTF-16 measure; UTF-8 will be smaller)"
        Note 'These are truncated with a Warning naming the item, and skipped entirely if still over the 4 MB platform cap.'
    }

    if ($TicketId -gt 0) {
        Step "Ticket $TicketId"
        $cols = 'TicketId, Title, Status, AssignedTo, LastModified, DATALENGTH(Body) AS body_bytes'
        if ($hasDeleted) { $cols += ', IsDeleted' }
        $row = Invoke-Sql "SELECT $cols FROM $Table WHERE TicketId = @id;" @{ '@id' = $TicketId }

        if ($row.Rows.Count -eq 0) {
            Fail "no row with TicketId $TicketId. It cannot be in the index because it is not in the source."
        }
        else {
            $r = $row.Rows[0]
            Pass "item ID 'ticket$TicketId' — '$($r.Title)', $($r.Status), modified $($r.LastModified), body $($r.body_bytes) bytes"
            if ($hasDeleted -and [bool]$r.IsDeleted) {
                Note 'IsDeleted = 1: the next incremental crawl reports this as a deletion and it leaves the index.'
                Note 'If it is still findable in Copilot, no crawl has run since it was marked.'
            }
            if ([int]$r.body_bytes -gt $MaxContentBytes) {
                Warn 'this body is over the configured cap and will be truncated or skipped'
            }
            Note "Verify the other end with: .\Verify-GraphConnection.ps1 -ItemId ticket$TicketId"
        }
    }
}
finally {
    $connection.Close()
    $connection.Dispose()
}

Write-Host ''
if ($script:failures -eq 0) {
    Write-Host 'Stage 1 is healthy: the source is readable and correctly shaped.' -ForegroundColor Green
}
else {
    Write-Host "$($script:failures) check(s) failed. See docs/TROUBLESHOOTING.md stage 1." -ForegroundColor Red
    exit 1
}
