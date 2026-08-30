<#
.SYNOPSIS
    Restores a ConnectorState backup to a throwaway database, verifies it the
    way a deployment would, and drops it again.

.DESCRIPTION
    An untested restore is a hypothesis. This script is how it becomes a plan.

    It restores a backup produced by deploy\Backup-ConnectorState.ps1 to a
    DIFFERENTLY NAMED database, proves the restored copy is complete and
    structurally sound, and then removes it. Nothing it does touches the live
    database, and it is written so that it CANNOT touch the live database even
    if you ask it to - see the safety section below.

    WHAT "VERIFIED" MEANS HERE

    RESTORE VERIFYONLY, which Backup-ConnectorState.ps1 already runs, proves the
    media is readable. It does not prove the database inside it is usable. This
    script asks the four further questions that a deployment asks:

      1. Does it restore at all, onto this instance, with the files relocated?
         A backup taken on a host with D:\Data restores onto a host with only
         C:\ only if somebody worked out the MOVE clauses. Doing that for the
         first time during an incident is how a twenty-minute restore becomes a
         two-hour one.

      2. Is every row there? Row counts per table are compared against the
         manifest written beside the backup. This is the check that catches a
         backup of the wrong database, or of the right database before a load.

      3. Is the schema intact? Table, procedure, view and table-type counts are
         compared against the manifest fingerprint, and DBCC CHECKDB is run
         against the restored copy. CHECKDB is run HERE rather than against the
         live database on purpose: the drill copy is disposable, so the
         expensive check is free of consequence.

      4. Would the deployment's own verification pass? sql\30-verify-set-options
         and sql\42-verify-least-privilege are executed against the restored
         copy. sql\30 is the one that matters most: QUOTED_IDENTIFIER is stored
         per module and a restored database carries whatever the original had,
         so a restore is exactly the moment to re-ask. sql\42 exercises the
         least-privilege model, and it is run against the drill copy precisely
         because it creates and drops probe users - which is a write, and
         therefore something you do not want aimed at production.

    SAFETY - WHY THIS CANNOT EAT THE LIVE DATABASE

    Three independent guards, because one is not enough for a script whose whole
    job is DROP DATABASE:

      * The target name is compared against the source database name recorded in
        the backup header itself (RESTORE HEADERONLY), not against a parameter
        default. Restoring a ConnectorState backup to a database called
        ConnectorState is refused outright.

      * Every database this script creates is stamped with an extended property,
        CS_DrillDatabase. The script will not DROP, and will not restore over,
        any database that does not carry that stamp. A pre-existing database
        that happens to share the drill name is therefore an error, not a
        casualty.

      * The restore always relocates the data and log files with MOVE, to
        filenames derived from the drill database name. It cannot overwrite the
        live database's files even by accident.

    There is no -Force. If a guard fires, the answer is to pick a different name.

.PARAMETER ServerInstance
    Instance to restore onto. Default 'localhost'. This does not have to be the
    instance the backup came from, and running the drill somewhere else is the
    better test - it is the one that proves the MOVE clauses.

.PARAMETER BackupPath
    The .bak to restore. If omitted, the newest ConnectorState-*.bak in
    -BackupDirectory is used.

.PARAMETER BackupDirectory
    Where to look for the newest backup when -BackupPath is not given.

.PARAMETER DrillDatabase
    Name for the throwaway database. Default 'ConnectorState_DrillRestore'.

.PARAMETER ManifestPath
    The .json manifest written beside the backup. Defaults to the backup path
    with a .json extension. Without it the row counts have nothing to be
    compared against, and the drill says so rather than quietly skipping the
    check.

.PARAMETER DataDirectory
    Where the restored .mdf and .ldf go. Defaults to the instance default data
    and log paths.

.PARAMETER SqlScriptRoot
    The repository sql\ directory, for sql\30 and sql\42. Defaults to ..\sql
    relative to this script.

.PARAMETER KeepDatabase
    Leave the restored copy in place instead of dropping it. For when the drill
    found something and you want to look at it. Remember to drop it afterwards;
    the script tells you the exact statement.

.PARAMETER SkipIntegrityCheck
    Skip DBCC CHECKDB. It is the slowest step by far on a large database.

.PARAMETER TrustServerCertificate
    Skip TLS certificate validation, for an instance with a self-signed cert.

.PARAMETER TimeoutSeconds
    Command timeout for the restore and CHECKDB. Default 1800.

.EXAMPLE
    .\Invoke-RestoreDrill.ps1 -BackupDirectory D:\Backup\ConnectorState

    Restores the newest backup, verifies it, drops it, and prints a verdict.

.EXAMPLE
    .\Invoke-RestoreDrill.ps1 -BackupPath D:\Backup\ConnectorState-20260830-020000.bak -KeepDatabase

    Restores a specific backup and leaves it for inspection.

.NOTES
    Windows PowerShell 5.1 and PowerShell 7 both supported and both tested.
    Nothing here is interactive. Exit code 0 means every check passed.
#>

[CmdletBinding()]
param(
    [string]$ServerInstance = 'localhost',
    [string]$BackupPath,
    [string]$BackupDirectory,
    [string]$DrillDatabase = 'ConnectorState_DrillRestore',
    [string]$ManifestPath,
    [string]$DataDirectory,
    [string]$SqlScriptRoot,
    [switch]$KeepDatabase,
    [switch]$SkipIntegrityCheck,
    [switch]$TrustServerCertificate,
    [int]$TimeoutSeconds = 1800
)

$ErrorActionPreference = 'Stop'

$script:failures = 0
function Step([string]$name) { Write-Host "`n== $name ==" -ForegroundColor Cyan }
function Pass([string]$msg) { Write-Host "  PASS  $msg" -ForegroundColor Green }
function Fail([string]$msg) { Write-Host "  FAIL  $msg" -ForegroundColor Red; $script:failures++ }
function Warn([string]$msg) { Write-Host "  WARN  $msg" -ForegroundColor Yellow }
function Note([string]$msg) { Write-Host "  note  $msg" -ForegroundColor DarkGray }

$builder = New-Object System.Data.SqlClient.SqlConnectionStringBuilder
$builder['Data Source'] = $ServerInstance
$builder['Initial Catalog'] = 'master'
$builder['Integrated Security'] = $true
$builder['Application Name'] = 'Invoke-RestoreDrill'
$builder['Connect Timeout'] = 15
if ($TrustServerCertificate) { $builder['TrustServerCertificate'] = $true }

$connection = New-Object System.Data.SqlClient.SqlConnection $builder.ConnectionString

# PRINT and RAISERROR(...,0,...) output arrives as InfoMessage rather than in a
# result set, and sql\30 and sql\42 say most of what they have to say that way.
# Collected here and flushed after each batch: writing to the host from the
# provider's callback thread behaves differently on 5.1 and 7, and this is the
# version-stable way to do it.
$script:infoMessages = New-Object System.Collections.ArrayList
try {
    $connection.add_InfoMessage({
        param($sender, $e)
        $null = $script:infoMessages.Add($e.Message)
    })
    $connection.FireInfoMessageEventOnUserErrors = $false
}
catch {
    Warn "could not hook InfoMessage; PRINT output from sql\30 and sql\42 will not be shown: $($_.Exception.Message)"
}

function Flush-Info {
    foreach ($message in $script:infoMessages) {
        foreach ($line in ($message -split "`r?`n")) {
            if ($line.Trim().Length -gt 0) { Note $line.Trim() }
        }
    }
    $script:infoMessages.Clear()
}

function Invoke-Table([string]$sql, [hashtable]$parameters, [int]$timeout) {
    $command = $connection.CreateCommand()
    $command.CommandText = $sql
    if ($timeout) { $command.CommandTimeout = $timeout } else { $command.CommandTimeout = 60 }
    if ($parameters) {
        foreach ($key in $parameters.Keys) { $null = $command.Parameters.AddWithValue($key, $parameters[$key]) }
    }
    $adapter = New-Object System.Data.SqlClient.SqlDataAdapter $command
    $table = New-Object System.Data.DataTable
    $null = $adapter.Fill($table)

    # See the identical comment in Backup-ConnectorState.ps1: without the comma
    # PowerShell enumerates the DataTable on the way out and the caller gets
    # DataRows, whose absent .Rows.Count silently reads as 0.
    return , $table
}

function Invoke-NonQuery([string]$sql, [hashtable]$parameters, [int]$timeout) {
    $command = $connection.CreateCommand()
    $command.CommandText = $sql
    if ($timeout) { $command.CommandTimeout = $timeout } else { $command.CommandTimeout = 60 }
    if ($parameters) {
        foreach ($key in $parameters.Keys) { $null = $command.Parameters.AddWithValue($key, $parameters[$key]) }
    }
    return $command.ExecuteNonQuery()
}

# ---------------------------------------------------------------------------
# GO is a sqlcmd batch separator, not T-SQL, and SqlClient has never heard of
# it. Sending sql\30 or sql\42 whole produces a syntax error on the first GO, so
# the file is split into batches here. The pattern matches a GO alone on its
# line, optionally with a repeat count, which is the only form these files use.
# ---------------------------------------------------------------------------
function Split-SqlBatches([string]$text) {
    $batches = New-Object System.Collections.ArrayList
    $current = New-Object System.Text.StringBuilder
    foreach ($line in ($text -split "`r?`n")) {
        if ($line -match '^\s*GO\s*(\d+)?\s*$') {
            $batch = $current.ToString()
            if ($batch.Trim().Length -gt 0) { $null = $batches.Add($batch) }
            $null = $current.Clear()
        }
        else {
            $null = $current.AppendLine($line)
        }
    }
    $tail = $current.ToString()
    if ($tail.Trim().Length -gt 0) { $null = $batches.Add($tail) }
    return , $batches
}

$restoreWatch = $null
$drillCreated = $false

try {
    Step "Connecting to $ServerInstance"
    $connection.Open()
    Note "server version $($connection.ServerVersion)"

    # -----------------------------------------------------------------------
    # 1. Find the backup.
    # -----------------------------------------------------------------------

    Step 'Locating the backup'

    if ([string]::IsNullOrWhiteSpace($BackupPath)) {
        if ([string]::IsNullOrWhiteSpace($BackupDirectory)) {
            Fail 'give either -BackupPath or -BackupDirectory.'
            exit 2
        }
        $newest = Get-ChildItem -LiteralPath $BackupDirectory -Filter '*.bak' -File |
            Sort-Object LastWriteTime -Descending | Select-Object -First 1
        if ($null -eq $newest) {
            Fail "no .bak files in $BackupDirectory."
            exit 2
        }
        $BackupPath = $newest.FullName
        Note 'no -BackupPath given; using the newest backup in the directory'
    }

    Pass "backup $BackupPath"

    if ([string]::IsNullOrWhiteSpace($ManifestPath)) {
        $ManifestPath = [System.IO.Path]::ChangeExtension($BackupPath, '.json')
    }

    $manifest = $null
    if (Test-Path -LiteralPath $ManifestPath) {
        $manifest = Get-Content -LiteralPath $ManifestPath -Raw | ConvertFrom-Json
        Pass "manifest $ManifestPath"
        # ConvertFrom-Json parses an ISO-8601 string into a [datetime] on
        # PowerShell 7 and leaves it a [string] on 5.1, so the same manifest
        # prints "08/30/2026 15:34:08" on one host and the round-trip form on
        # the other. Normalised here so the two transcripts can be diffed.
        $takenUtc = if ($manifest.backupUtc -is [datetime]) {
            ([datetime]$manifest.backupUtc).ToUniversalTime().ToString('o')
        } else { [string]$manifest.backupUtc }

        Note "taken $takenUtc from [$($manifest.database)] on $($manifest.serverInstance), $($manifest.totalRows) row(s)"
    }
    else {
        Warn "no manifest at $ManifestPath. Row counts and the schema fingerprint have nothing to be compared against."
        Note 'the restore and the structural checks still run; only the completeness comparison is lost'
    }

    # -----------------------------------------------------------------------
    # 2. Read the header. This is guard one: the source database name comes
    #    from the backup media itself.
    # -----------------------------------------------------------------------

    Step 'Reading the backup header'

    $header = Invoke-Table 'RESTORE HEADERONLY FROM DISK = @p' @{ '@p' = $BackupPath } 300
    if ($header.Rows.Count -eq 0) {
        Fail 'the backup media contains no backup sets.'
        exit 2
    }

    $sourceDatabase = [string]$header.Rows[0]['DatabaseName']
    $backupTypeCode = [int]$header.Rows[0]['BackupType']
    $backupTypeName = switch ($backupTypeCode) { 1 { 'full' } 2 { 'transaction log' } 5 { 'differential' } default { "type $backupTypeCode" } }

    Pass "media holds a $backupTypeName backup of [$sourceDatabase]"

    if ($backupTypeCode -ne 1) {
        Fail 'this drill restores full backups only. A differential or log backup needs its base restored first.'
        exit 2
    }

    # GUARD ONE.
    if ($DrillDatabase -eq $sourceDatabase -or
        [string]::Equals($DrillDatabase, $sourceDatabase, [StringComparison]::OrdinalIgnoreCase)) {
        Write-Host ''
        Fail "refusing to restore over the source database."
        Write-Host ''
        Write-Host "  -DrillDatabase is '$DrillDatabase' and the backup header says this is a" -ForegroundColor Red
        Write-Host "  backup of '$sourceDatabase'. Restoring one onto the other is not a drill," -ForegroundColor Red
        Write-Host '  it is an outage: the live database would be overwritten with whatever' -ForegroundColor Red
        Write-Host '  state this file happens to hold, and every crawl since the backup would' -ForegroundColor Red
        Write-Host '  be lost along with the run history that proves they happened.' -ForegroundColor Red
        Write-Host ''
        Write-Host '  Pick a different -DrillDatabase. There is no override.' -ForegroundColor Red
        exit 4
    }

    Pass "drill target [$DrillDatabase] differs from the source [$sourceDatabase]"

    # -----------------------------------------------------------------------
    # 3. Guard two: if the drill database already exists, it must be ours.
    # -----------------------------------------------------------------------

    Step 'Checking the drill target'

    $existing = Invoke-Table 'SELECT database_id FROM sys.databases WHERE name = @db' @{ '@db' = $DrillDatabase } 30

    if ($existing.Rows.Count -gt 0) {
        Note "[$DrillDatabase] already exists; checking whether a previous drill created it"

        $escapedDrill = $DrillDatabase.Replace(']', ']]')
        $stamp = Invoke-Table "SELECT value FROM [$escapedDrill].sys.extended_properties WHERE class = 0 AND name = N'CS_DrillDatabase'" $null 30

        if ($stamp.Rows.Count -eq 0) {
            Write-Host ''
            Fail "[$DrillDatabase] exists but was not created by this script."
            Write-Host ''
            Write-Host '  It does not carry the CS_DrillDatabase extended property, which every' -ForegroundColor Red
            Write-Host '  database this script creates is stamped with. Something else owns it.' -ForegroundColor Red
            Write-Host '  This script will not drop a database it cannot prove is disposable.' -ForegroundColor Red
            Write-Host ''
            Write-Host '  Choose a different -DrillDatabase, or remove that database yourself if' -ForegroundColor Red
            Write-Host '  you are certain it is spare.' -ForegroundColor Red
            exit 4
        }

        Note "stamped by a previous drill at $($stamp.Rows[0]['value']); dropping it"
        $null = Invoke-NonQuery "ALTER DATABASE [$escapedDrill] SET SINGLE_USER WITH ROLLBACK IMMEDIATE" $null 120
        $null = Invoke-NonQuery "DROP DATABASE [$escapedDrill]" $null 120
        Pass 'previous drill database removed'
    }
    else {
        Pass "[$DrillDatabase] does not exist"
    }

    # -----------------------------------------------------------------------
    # 4. Guard three: work out the MOVE clauses. Relocating the files is what
    #    makes it impossible to land on the live database's own files, and it
    #    is also the step nobody has rehearsed when they need it most.
    # -----------------------------------------------------------------------

    Step 'Planning the file relocation'

    if ([string]::IsNullOrWhiteSpace($DataDirectory)) {
        $paths = Invoke-Table @'
SELECT CAST(SERVERPROPERTY('InstanceDefaultDataPath') AS NVARCHAR(4000)) AS DataPath,
       CAST(SERVERPROPERTY('InstanceDefaultLogPath')  AS NVARCHAR(4000)) AS LogPath
'@ $null 30
        $dataPath = ([string]$paths.Rows[0]['DataPath']).TrimEnd('\')
        $logPath = ([string]$paths.Rows[0]['LogPath']).TrimEnd('\')
        Note 'no -DataDirectory given; using the instance default data and log paths'
    }
    else {
        $dataPath = $DataDirectory.TrimEnd('\')
        $logPath = $dataPath
    }

    $fileList = Invoke-Table 'RESTORE FILELISTONLY FROM DISK = @p' @{ '@p' = $BackupPath } 300

    $moveClauses = @()
    foreach ($row in $fileList.Rows) {
        $logical = [string]$row['LogicalName']
        $type = [string]$row['Type']
        $extension = if ($type -eq 'L') { 'ldf' } else { 'mdf' }
        $root = if ($type -eq 'L') { $logPath } else { $dataPath }
        $target = "$root\$DrillDatabase" + '_' + $logical + '.' + $extension
        # Escape single quotes: a logical file name may legally contain one, and
        # these values are identifiers inside a string literal, not parameters.
        $moveClauses += "MOVE N'" + $logical.Replace("'", "''") + "' TO N'" + $target.Replace("'", "''") + "'"
        Note "$logical ($type) -> $target"
    }

    Pass "$($fileList.Rows.Count) file(s) relocated under the drill name"

    # -----------------------------------------------------------------------
    # 5. Restore.
    # -----------------------------------------------------------------------

    Step 'Restoring'

    $escapedDrill = $DrillDatabase.Replace(']', ']]')
    $restoreSql = "RESTORE DATABASE [$escapedDrill] FROM DISK = @p WITH " +
        ($moveClauses -join ', ') + ', CHECKSUM, RECOVERY, STATS = 25'

    $restoreWatch = [System.Diagnostics.Stopwatch]::StartNew()
    $null = Invoke-NonQuery $restoreSql @{ '@p' = $BackupPath } $TimeoutSeconds
    $restoreWatch.Stop()
    $script:infoMessages.Clear()

    $drillCreated = $true
    Pass ("restored in {0:N1}s" -f $restoreWatch.Elapsed.TotalSeconds)

    # Stamp it immediately, so that even an aborted run leaves a database this
    # script is willing to clean up next time.
    $null = Invoke-NonQuery @"
EXEC [$escapedDrill].sys.sp_addextendedproperty
     @name = N'CS_DrillDatabase',
     @value = N'$([DateTime]::UtcNow.ToString('o'))'
"@ $null 60
    Note 'stamped with CS_DrillDatabase so a later drill can safely reclaim it'

    $connection.ChangeDatabase($DrillDatabase)

    # -----------------------------------------------------------------------
    # 6. Completeness: row counts against the manifest.
    # -----------------------------------------------------------------------

    Step 'Comparing row counts'

    $countsTable = Invoke-Table @'
SELECT  s.name AS SchemaName,
        t.name AS TableName,
        SUM(CASE WHEN p.index_id IN (0, 1) THEN p.rows ELSE 0 END) AS RowCountValue
FROM    sys.tables AS t
INNER JOIN sys.schemas AS s ON s.schema_id = t.schema_id
INNER JOIN sys.partitions AS p ON p.object_id = t.object_id
GROUP BY s.name, t.name
ORDER BY s.name, t.name
'@ $null 120

    $restored = [ordered]@{}
    $restoredTotal = [long]0
    foreach ($row in $countsTable.Rows) {
        $key = "$($row['SchemaName']).$($row['TableName'])"
        $restored[$key] = [long]$row['RowCountValue']
        $restoredTotal += [long]$row['RowCountValue']
    }

    if ($null -ne $manifest) {
        $expected = $manifest.tableCounts
        $mismatches = 0
        foreach ($name in $expected.PSObject.Properties.Name) {
            $want = [long]$expected.$name
            if (-not $restored.Contains($name)) {
                Fail ("{0,-28} missing from the restored copy (expected {1:N0} rows)" -f $name, $want)
                $mismatches++
                continue
            }
            $got = [long]$restored[$name]
            if ($got -eq $want) {
                Pass ("{0,-28} {1,12:N0} rows" -f $name, $got)
            }
            else {
                Fail ("{0,-28} expected {1:N0}, restored {2:N0}" -f $name, $want, $got)
                $mismatches++
            }
        }
        foreach ($name in $restored.Keys) {
            if (-not $expected.PSObject.Properties.Name.Contains($name)) {
                Warn ("{0,-28} present in the restore but not in the manifest" -f $name)
            }
        }
        if ($mismatches -eq 0) {
            Pass ("every table matches the manifest: {0:N0} row(s) total" -f $restoredTotal)
        }
    }
    else {
        foreach ($name in $restored.Keys) { Note ("{0,-28} {1,12:N0} rows" -f $name, $restored[$name]) }
        Warn 'no manifest, so these counts are reported but not verified'
    }

    # -----------------------------------------------------------------------
    # 7. Structure: the schema fingerprint.
    # -----------------------------------------------------------------------

    Step 'Comparing the schema fingerprint'

    $fingerprint = Invoke-Table @'
SELECT  (SELECT COUNT(*) FROM sys.tables)                                AS TableCount,
        (SELECT COUNT(*) FROM sys.procedures)                            AS ProcedureCount,
        (SELECT COUNT(*) FROM sys.views WHERE is_ms_shipped = 0)         AS ViewCount,
        (SELECT COUNT(*) FROM sys.table_types WHERE is_user_defined = 1) AS TableTypeCount
'@ $null 60

    $actual = [ordered]@{
        tables     = [int]$fingerprint.Rows[0]['TableCount']
        procedures = [int]$fingerprint.Rows[0]['ProcedureCount']
        views      = [int]$fingerprint.Rows[0]['ViewCount']
        tableTypes = [int]$fingerprint.Rows[0]['TableTypeCount']
    }

    if ($null -ne $manifest) {
        foreach ($key in $actual.Keys) {
            $want = [int]$manifest.fingerprint.$key
            $got = [int]$actual[$key]
            if ($want -eq $got) { Pass ("{0,-12} {1}" -f $key, $got) }
            else { Fail ("{0,-12} expected {1}, restored {2}" -f $key, $want, $got) }
        }
    }
    else {
        foreach ($key in $actual.Keys) { Note ("{0,-12} {1}" -f $key, $actual[$key]) }
    }

    # -----------------------------------------------------------------------
    # 8. Integrity, on the disposable copy.
    # -----------------------------------------------------------------------

    if (-not $SkipIntegrityCheck) {
        Step 'Checking integrity'
        Note 'DBCC CHECKDB against the restored copy - free of consequence here, which is why it belongs in the drill'
        $checkWatch = [System.Diagnostics.Stopwatch]::StartNew()
        $null = Invoke-NonQuery "DBCC CHECKDB ([$escapedDrill]) WITH NO_INFOMSGS" $null $TimeoutSeconds
        $checkWatch.Stop()
        $script:infoMessages.Clear()
        Pass ("DBCC CHECKDB reported no allocation or consistency errors in {0:N1}s" -f $checkWatch.Elapsed.TotalSeconds)
    }
    else {
        Warn 'integrity check skipped by -SkipIntegrityCheck'
    }

    # -----------------------------------------------------------------------
    # 9. The deployment's own verification.
    # -----------------------------------------------------------------------

    Step 'Running the deployment verification scripts'

    if ([string]::IsNullOrWhiteSpace($SqlScriptRoot)) {
        $SqlScriptRoot = Join-Path $PSScriptRoot '..\sql'
    }

    $verificationScripts = @('30-verify-set-options.sql', '42-verify-least-privilege.sql')

    foreach ($name in $verificationScripts) {
        $path = Join-Path $SqlScriptRoot $name
        if (-not (Test-Path -LiteralPath $path)) {
            Warn "$name not found at $path; skipped"
            continue
        }

        $text = Get-Content -LiteralPath $path -Raw
        $batches = Split-SqlBatches $text

        try {
            foreach ($batch in $batches) {
                $null = Invoke-NonQuery $batch $null $TimeoutSeconds
            }
            Flush-Info
            Pass "$name passed against the restored copy"
        }
        catch {
            Flush-Info
            Fail "$name failed: $($_.Exception.Message)"
        }
    }

    # -----------------------------------------------------------------------
    # 10. The views execute. Section 5 of docs\CRAWL-STATE-DEPLOYMENT.md makes
    #     this point about the deployment and it applies just as well to a
    #     restore: a view that compiled is not a view that runs.
    # -----------------------------------------------------------------------

    Step 'Executing the views'

    $views = Invoke-Table "SELECT s.name AS SchemaName, v.name AS ViewName FROM sys.views AS v INNER JOIN sys.schemas AS s ON s.schema_id = v.schema_id WHERE v.is_ms_shipped = 0 ORDER BY v.name" $null 60

    foreach ($row in $views.Rows) {
        $viewName = "[$([string]$row['SchemaName'])].[$([string]$row['ViewName'])]"
        try {
            $null = Invoke-Table "SELECT TOP 1 * FROM $viewName" $null 120
            Pass "$viewName executes"
        }
        catch {
            Fail "$viewName does not execute: $($_.Exception.Message)"
        }
    }
}
finally {
    # -----------------------------------------------------------------------
    # 11. Clean up. In finally, so a failure part-way through still removes the
    #     copy rather than leaving a full-size database behind on a volume that
    #     may not have room for two.
    # -----------------------------------------------------------------------

    if ($connection.State -ne 'Closed') {
        if ($drillCreated -and -not $KeepDatabase) {
            Write-Host "`n== Dropping the drill database ==" -ForegroundColor Cyan
            try {
                $connection.ChangeDatabase('master')
                $escapedDrill = $DrillDatabase.Replace(']', ']]')

                # The stamp is re-checked here rather than trusted from earlier.
                # By this point the script has been running for a while and the
                # only thing that makes a DROP safe is the property, so it is
                # read again immediately before the DROP.
                $stamp = Invoke-Table "SELECT value FROM [$escapedDrill].sys.extended_properties WHERE class = 0 AND name = N'CS_DrillDatabase'" $null 30
                if ($stamp.Rows.Count -eq 0) {
                    Fail "[$DrillDatabase] lost its CS_DrillDatabase stamp; refusing to drop it. Remove it by hand."
                }
                else {
                    $null = Invoke-NonQuery "ALTER DATABASE [$escapedDrill] SET SINGLE_USER WITH ROLLBACK IMMEDIATE" $null 120
                    $null = Invoke-NonQuery "DROP DATABASE [$escapedDrill]" $null 120
                    Pass "[$DrillDatabase] dropped; the instance is back as it was"
                }
            }
            catch {
                Fail "could not drop [$DrillDatabase]: $($_.Exception.Message)"
                Write-Host "  Remove it by hand:  DROP DATABASE [$DrillDatabase];" -ForegroundColor Yellow
            }
        }
        elseif ($drillCreated -and $KeepDatabase) {
            Write-Host "`n== Keeping the drill database ==" -ForegroundColor Cyan
            Warn "[$DrillDatabase] left in place by -KeepDatabase."
            Write-Host "  Drop it when you are done:  DROP DATABASE [$DrillDatabase];" -ForegroundColor Yellow
        }

        $connection.Close()
    }
    $connection.Dispose()

    Write-Host ''
    if ($script:failures -eq 0) {
        Write-Host 'Restore drill PASSED.' -ForegroundColor Green
        if ($null -ne $restoreWatch) {
            Write-Host ("  The backup restores, in {0:N1}s, complete and structurally sound." -f $restoreWatch.Elapsed.TotalSeconds)
        }
        Write-Host '  Record the date in docs\DISASTER-RECOVERY.md. A drill nobody wrote down' -ForegroundColor DarkGray
        Write-Host '  is a drill nobody can prove happened.' -ForegroundColor DarkGray
    }
    else {
        Write-Host "Restore drill FAILED: $($script:failures) check(s) did not pass." -ForegroundColor Red
        Write-Host '  Do not record this backup as restorable. Fix the cause and drill again.' -ForegroundColor Red
    }
}

if ($script:failures -gt 0) { exit 1 }
