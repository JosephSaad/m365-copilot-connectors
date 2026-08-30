<#
.SYNOPSIS
    Takes a verified, self-describing backup of the ConnectorState database.

.DESCRIPTION
    This script exists because of one asymmetry, and everything it does follows
    from it.

    Most of ConnectorState is a CACHE. The item inventory, the checkpoint, the
    connection registration and the principal map are all rebuilt by the
    connector doing its ordinary job — see section 9 of
    docs/CRAWL-STATE-DEPLOYMENT.md, which works through why an empty store
    cannot produce a wrong index. If those tables were all this database held,
    the honest backup policy would be "do not bother".

    But four tables are not a cache. crawl.Run, crawl.RunItemType,
    crawl.RunPhaseTiming and crawl.ThrottleEvent are EVIDENCE. Nothing rebuilds
    them, and "prove the connector ran nightly for the last quarter" is a
    question a regulated estate eventually asks. The recovery point objective
    for this database is set entirely by those four tables and by nothing else:
    lose a day of them and you have lost a day of audit trail that cannot be
    reconstructed from any other source.

    That is why this script defaults to being run daily. Not because a day of
    inventory is expensive — it is worth exactly one full crawl — but because a
    day of evidence is gone for good.

    WHAT THIS SCRIPT DOES THAT A MAINTENANCE PLAN DOES NOT

    1. COPY_ONLY, always. If the estate ever adds differential backups of this
       database, an ordinary full backup taken by this script would silently
       become their new differential base — and the estate's own differentials
       would then restore against a full backup this script may have already
       pruned. COPY_ONLY makes this script unable to damage a backup chain it
       does not own. There is no case in which you want the other behaviour
       here; if this database is the estate's to back up, let the estate back it
       up and do not run this at all.

    2. CHECKSUM on the way out and RESTORE VERIFYONLY on the way back. A backup
       file that exists is not a backup that restores. VERIFYONLY reads the
       media back and validates every page checksum, which is the cheapest
       available answer to "is this file actually restorable" short of
       restoring it. Use deploy/Invoke-RestoreDrill.ps1 for the real answer.

    3. A sidecar manifest. The .bak alone does not tell you which connector
       binaries wrote the state inside it, or how many rows you should expect
       back. The manifest records both: per-table row counts, the distinct
       crawl.Run.ToolVersion values present, and a schema fingerprint (table,
       procedure and table-type counts). Invoke-RestoreDrill.ps1 verifies a
       restore against it, and the upgrade runbook uses the ToolVersion list to
       answer "which binaries does this backup pair with" — see
       docs/UPGRADE-RUNBOOK.md.

    WARNING — THE BACKUP IS WRITTEN BY THE SQL SERVER SERVICE ACCOUNT, NOT BY
    YOU. BACKUP DATABASE is executed inside the database engine, so the identity
    that must be able to write to -BackupDirectory is the SQL Server service
    account (typically NT Service\MSSQLSERVER), not the account running this
    script. Pointing -BackupDirectory at your own profile, a mapped drive or a
    scratch folder produces:

        Cannot open backup device '...'. Operating system error 5(Access is
        denied.). BACKUP DATABASE is terminating abnormally.

    The reverse is also true and catches people out just as often: the instance
    default backup directory lives under Program Files and the service account
    can write there while YOU frequently cannot READ there. That is why this
    script takes the backup size from msdb.dbo.backupset rather than from
    Get-Item, and why a manifest that cannot be written is a warning rather than
    a failure. The backup is the artefact; the manifest is an aid.

    This script never writes to the database it backs up.

.PARAMETER ServerInstance
    The SQL Server instance holding ConnectorState. Default 'localhost'.

.PARAMETER Database
    The database to back up. Default 'ConnectorState'. Parameterised only so the
    restore drill can point this at a copy; there is no reason to change it in
    production.

.PARAMETER BackupDirectory
    Where the engine writes the .bak. Defaults to the instance's own default
    backup path, which is the one directory the service account is guaranteed to
    be able to write. Must be reachable BY THE SERVICE ACCOUNT — see the warning
    above.

.PARAMETER RetentionDays
    Delete backups produced by this script older than this many days. Default
    35, which keeps a month plus a margin. The newest backup is never deleted,
    whatever its age. Pruning is a filesystem operation performed by YOUR
    account: where you cannot enumerate the backup directory it is skipped with
    a warning, and retention becomes the estate's housekeeping job.

.PARAMETER TrustServerCertificate
    Skip validation of the server's TLS certificate. Needed against an instance
    using a self-signed certificate. Not defaulted on: an unvalidated encrypted
    connection is a decision, not a convenience.

.PARAMETER NoCompression
    Take the backup uncompressed. The script uses COMPRESSION by default and
    falls back automatically on an edition that does not support it, so this
    switch is only for an estate that mandates uncompressed media.

.PARAMETER SkipVerify
    Skip RESTORE VERIFYONLY. Only sensible when a downstream tool verifies the
    media instead. Verification roughly doubles the runtime, and against this
    database that is measured in seconds.

.PARAMETER RunIntegrityCheck
    Run DBCC CHECKDB WITH PHYSICAL_ONLY before the backup. Off by default
    because it is the slowest thing here, and because CHECKSUM already catches
    a damaged page on the way out. Worth turning on for the backup you intend to
    keep: WITH CHECKSUM proves the backup matches the database, and only CHECKDB
    proves the database is worth matching.

.PARAMETER TimeoutSeconds
    Command timeout for the backup and verify. Default 900.

.EXAMPLE
    .\Backup-ConnectorState.ps1

    Backs up ConnectorState on localhost to the instance default backup path,
    verifies it, writes the manifest and prunes anything older than 35 days.

.EXAMPLE
    .\Backup-ConnectorState.ps1 -ServerInstance sql01 -BackupDirectory D:\Backup\ConnectorState -RetentionDays 90

    The shape to schedule. Register it as a daily scheduled task running as an
    account with BACKUP DATABASE rights; see docs/DISASTER-RECOVERY.md.

.NOTES
    Windows PowerShell 5.1 and PowerShell 7 both supported and both tested.
    Nothing here is interactive.
#>

[CmdletBinding()]
param(
    [string]$ServerInstance = 'localhost',
    [string]$Database = 'ConnectorState',
    [string]$BackupDirectory,
    [int]$RetentionDays = 35,
    [switch]$TrustServerCertificate,
    [switch]$NoCompression,
    [switch]$SkipVerify,
    [switch]$RunIntegrityCheck,
    [int]$TimeoutSeconds = 900
)

$ErrorActionPreference = 'Stop'

function Step([string]$name) { Write-Host "`n== $name ==" -ForegroundColor Cyan }
function Pass([string]$msg) { Write-Host "  PASS  $msg" -ForegroundColor Green }
function Fail([string]$msg) { Write-Host "  FAIL  $msg" -ForegroundColor Red }
function Warn([string]$msg) { Write-Host "  WARN  $msg" -ForegroundColor Yellow }
function Note([string]$msg) { Write-Host "  note  $msg" -ForegroundColor DarkGray }

# ---------------------------------------------------------------------------
# Connection. System.Data.SqlClient rather than the SqlServer module: it is in
# the framework on 5.1 and in the runtime on 7, so this script has no install
# step on either. deploy/Test-SqlSource.ps1 makes the same choice for the same
# reason.
# ---------------------------------------------------------------------------

$builder = New-Object System.Data.SqlClient.SqlConnectionStringBuilder
$builder['Data Source'] = $ServerInstance
$builder['Initial Catalog'] = 'master'
$builder['Integrated Security'] = $true
$builder['Application Name'] = 'Backup-ConnectorState'
$builder['Connect Timeout'] = 15
if ($TrustServerCertificate) { $builder['TrustServerCertificate'] = $true }

$connection = New-Object System.Data.SqlClient.SqlConnection $builder.ConnectionString

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

    # The comma is load-bearing. PowerShell ENUMERATES a DataTable on the way
    # out of a function, so a bare `return $table` hands the caller loose
    # DataRow objects instead of the table. The caller's $result.Rows.Count then
    # evaluates against a DataRow that has no .Rows property - which returns
    # $null, whose .Count is 0 in PowerShell 3.0 and later. The result is a
    # script that reports "database does not exist" for a database that plainly
    # does, on both hosts, with no error anywhere. Wrapping in a single-element
    # array suppresses the enumeration.
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

$startedUtc = [DateTime]::UtcNow
$summary = $null

try {
    Step "Connecting to $ServerInstance"
    $connection.Open()
    Note "server version $($connection.ServerVersion)"

    # -----------------------------------------------------------------------
    # 1. The database has to exist and be ONLINE. A backup of a database in
    #    RECOVERY_PENDING succeeds at the file level and is worthless, so the
    #    state is checked rather than assumed.
    # -----------------------------------------------------------------------

    Step 'Checking the database'

    $dbRow = Invoke-Table @'
SELECT  d.name,
        d.state_desc,
        d.recovery_model_desc,
        CAST((SELECT SUM(size) * 8.0 / 1024 FROM sys.master_files WHERE database_id = d.database_id) AS DECIMAL(18,1)) AS AllocatedMb
FROM    sys.databases AS d
WHERE   d.name = @db
'@ @{ '@db' = $Database }

    if ($dbRow.Rows.Count -eq 0) {
        Fail "database [$Database] does not exist on $ServerInstance."
        exit 2
    }

    $state = [string]$dbRow.Rows[0]['state_desc']
    $recovery = [string]$dbRow.Rows[0]['recovery_model_desc']
    $allocatedMb = [decimal]$dbRow.Rows[0]['AllocatedMb']

    if ($state -ne 'ONLINE') {
        Fail "database [$Database] is $state, not ONLINE. A backup taken now would not be restorable."
        exit 2
    }

    Pass "[$Database] is ONLINE, recovery model $recovery, $allocatedMb MB allocated"

    if ($recovery -eq 'FULL') {
        # Not a failure. But under FULL a full backup does not truncate the log,
        # and section 7 of docs/CRAWL-STATE-DEPLOYMENT.md explains why that fills
        # a log volume during a crawl if nobody scheduled log backups.
        Warn "recovery model is FULL. This script takes no log backups. Confirm the estate schedules them, or the log grows until the volume fills - see section 7 of docs/CRAWL-STATE-DEPLOYMENT.md."
    }

    # -----------------------------------------------------------------------
    # 2. The inventory that goes in the manifest. Read from sys.partitions
    #    rather than from a hard-coded table list, so this keeps working against
    #    a schema older or newer than the one it was written for - which is
    #    exactly the situation a DR tool is used in.
    # -----------------------------------------------------------------------

    Step 'Reading the inventory'

    # The inventory queries read sys.* in the target database, so switch
    # context rather than three-part-naming every catalogue view. ChangeDatabase
    # takes the name as a value, so there is nothing to escape here; $escaped
    # exists for the two statements further down that CANNOT be parameterised
    # (BACKUP and DBCC both take the database as an identifier). A database
    # called ]x[ is legal and would otherwise break out of the brackets.
    $escaped = $Database.Replace(']', ']]')
    $connection.ChangeDatabase($Database)

    $countsTable = Invoke-Table @'
SELECT  s.name AS SchemaName,
        t.name AS TableName,
        SUM(CASE WHEN p.index_id IN (0, 1) THEN p.rows ELSE 0 END) AS RowCountValue
FROM    sys.tables AS t
INNER JOIN sys.schemas AS s ON s.schema_id = t.schema_id
INNER JOIN sys.partitions AS p ON p.object_id = t.object_id
GROUP BY s.name, t.name
ORDER BY s.name, t.name
'@ $null 60

    $tableCounts = [ordered]@{}
    $totalRows = [long]0
    foreach ($row in $countsTable.Rows) {
        $key = "$($row['SchemaName']).$($row['TableName'])"
        $value = [long]$row['RowCountValue']
        $tableCounts[$key] = $value
        $totalRows += $value
        Note ("{0,-28} {1,12:N0} rows" -f $key, $value)
    }

    $fingerprint = Invoke-Table @'
SELECT  (SELECT COUNT(*) FROM sys.tables)                                AS TableCount,
        (SELECT COUNT(*) FROM sys.procedures)                            AS ProcedureCount,
        (SELECT COUNT(*) FROM sys.views WHERE is_ms_shipped = 0)         AS ViewCount,
        (SELECT COUNT(*) FROM sys.table_types WHERE is_user_defined = 1) AS TableTypeCount
'@ $null 60

    $toolVersions = @()
    $latestRunUtc = $null
    if ($tableCounts.Contains('crawl.Run')) {
        $versionTable = Invoke-Table 'SELECT DISTINCT ToolVersion FROM crawl.Run ORDER BY ToolVersion' $null 60
        foreach ($row in $versionTable.Rows) { $toolVersions += [string]$row['ToolVersion'] }

        $latestTable = Invoke-Table 'SELECT MAX(StartedUtc) AS LatestUtc FROM crawl.Run' $null 60
        if ($latestTable.Rows.Count -gt 0 -and $latestTable.Rows[0]['LatestUtc'] -isnot [DBNull]) {
            $latestRunUtc = ([DateTime]$latestTable.Rows[0]['LatestUtc']).ToString('o')
        }
    }

    Pass ("{0:N0} rows across {1} table(s); {2} procedure(s), {3} table type(s)" -f
        $totalRows,
        [int]$fingerprint.Rows[0]['TableCount'],
        [int]$fingerprint.Rows[0]['ProcedureCount'],
        [int]$fingerprint.Rows[0]['TableTypeCount'])

    if ($toolVersions.Count -gt 0) {
        Note "crawl.Run carries $($toolVersions.Count) distinct ToolVersion value(s); the newest binary that wrote this state is recorded in the manifest"
    }

    $connection.ChangeDatabase('master')

    # -----------------------------------------------------------------------
    # 3. Optional integrity check.
    # -----------------------------------------------------------------------

    if ($RunIntegrityCheck) {
        Step 'Checking integrity'
        Note 'DBCC CHECKDB WITH PHYSICAL_ONLY - online, read-only, uses an internal snapshot'
        $null = Invoke-NonQuery "DBCC CHECKDB ([$escaped]) WITH PHYSICAL_ONLY, NO_INFOMSGS" $null $TimeoutSeconds
        Pass 'DBCC CHECKDB reported no errors'
    }

    # -----------------------------------------------------------------------
    # 4. Resolve where the file goes.
    # -----------------------------------------------------------------------

    Step 'Resolving the backup directory'

    if ([string]::IsNullOrWhiteSpace($BackupDirectory)) {
        $defaultPath = Invoke-Table "SELECT CAST(SERVERPROPERTY('InstanceDefaultBackupPath') AS NVARCHAR(4000)) AS p" $null 30
        $BackupDirectory = [string]$defaultPath.Rows[0]['p']
        Note 'no -BackupDirectory given; using the instance default backup path'
    }

    $BackupDirectory = $BackupDirectory.TrimEnd('\', '/')
    Pass "backup directory $BackupDirectory"
    Note 'this path is resolved and written BY THE SQL SERVER SERVICE ACCOUNT, not by you'

    $stamp = $startedUtc.ToString('yyyyMMdd-HHmmss')
    $baseName = "$Database-$stamp"
    $backupPath = "$BackupDirectory\$baseName.bak"
    $manifestPath = "$BackupDirectory\$baseName.json"

    # -----------------------------------------------------------------------
    # 5. The backup itself.
    # -----------------------------------------------------------------------

    Step 'Taking the backup'

    $withClauses = @('COPY_ONLY', 'INIT', 'CHECKSUM', 'FORMAT', "NAME = N'ConnectorState DR backup'")
    if (-not $NoCompression) { $withClauses += 'COMPRESSION' }

    $backupSql = "BACKUP DATABASE [$escaped] TO DISK = @path WITH " + ($withClauses -join ', ')

    $watch = [System.Diagnostics.Stopwatch]::StartNew()
    try {
        $null = Invoke-NonQuery $backupSql @{ '@path' = $backupPath } $TimeoutSeconds
    }
    catch {
        $message = $_.Exception.Message

        # Error 1844 and its relatives: compression is not available on this
        # edition. Retrying without it is strictly better than failing, because
        # an uncompressed backup is still a backup.
        if ((-not $NoCompression) -and ($message -match 'compression' -or $message -match '1844')) {
            Warn 'backup compression is not supported on this edition; retrying uncompressed'
            $withClauses = $withClauses | Where-Object { $_ -ne 'COMPRESSION' }
            $backupSql = "BACKUP DATABASE [$escaped] TO DISK = @path WITH " + ($withClauses -join ', ')
            $null = Invoke-NonQuery $backupSql @{ '@path' = $backupPath } $TimeoutSeconds
        }
        elseif ($message -match 'Operating system error 5' -or $message -match 'Cannot open backup device') {
            $watch.Stop()
            Fail 'the engine could not write the backup file.'
            Write-Host ''
            Write-Host '  This is almost always the service account, not you.' -ForegroundColor Yellow
            Write-Host '  BACKUP DATABASE runs inside the engine, so the identity that needs write' -ForegroundColor Yellow
            Write-Host "  access to $BackupDirectory is the SQL Server service account -" -ForegroundColor Yellow
            Write-Host '  not the account running this script. A user profile path, a scratch' -ForegroundColor Yellow
            Write-Host '  folder or a mapped drive will fail here every time even though you can' -ForegroundColor Yellow
            Write-Host '  write to it perfectly well yourself.' -ForegroundColor Yellow
            Write-Host ''
            Write-Host '  Find the account with:' -ForegroundColor Yellow
            Write-Host '    SELECT servicename, service_account FROM sys.dm_server_services;' -ForegroundColor Yellow
            Write-Host '  then grant it Modify on the directory, or omit -BackupDirectory to use' -ForegroundColor Yellow
            Write-Host '  the instance default backup path.' -ForegroundColor Yellow
            Write-Host ''
            Write-Host "  Engine said: $message" -ForegroundColor DarkGray
            exit 3
        }
        else {
            throw
        }
    }
    $watch.Stop()

    Pass ("backup completed in {0:N1}s" -f $watch.Elapsed.TotalSeconds)

    # -----------------------------------------------------------------------
    # 6. Sizes come from msdb, not from the filesystem. The account running
    #    this script frequently cannot READ the directory the engine just
    #    wrote to - the instance default backup path under Program Files is
    #    the common case - and a size of zero because Get-Item was denied
    #    looks exactly like a size of zero because the backup was empty.
    # -----------------------------------------------------------------------

    Step 'Reading what the engine recorded'

    $set = Invoke-Table @'
SELECT TOP 1
        bs.backup_set_id,
        bs.backup_start_date,
        bs.backup_finish_date,
        bs.backup_size,
        bs.compressed_backup_size,
        bs.is_copy_only,
        bs.has_backup_checksums,
        bs.database_name,
        bs.server_name,
        bmf.physical_device_name
FROM    msdb.dbo.backupset AS bs
INNER JOIN msdb.dbo.backupmediafamily AS bmf ON bmf.media_set_id = bs.media_set_id
WHERE   bs.database_name = @db
  AND   bmf.physical_device_name = @path
ORDER BY bs.backup_finish_date DESC
'@ @{ '@db' = $Database; '@path' = $backupPath } 60

    $backupBytes = $null
    $compressedBytes = $null
    if ($set.Rows.Count -gt 0) {
        $backupBytes = [long]$set.Rows[0]['backup_size']
        $compressedBytes = [long]$set.Rows[0]['compressed_backup_size']
        $copyOnly = [bool]$set.Rows[0]['is_copy_only']
        $checksums = [bool]$set.Rows[0]['has_backup_checksums']

        Pass ("{0:N1} MB on disk (uncompressed {1:N1} MB, ratio {2:N1}x)" -f
            ($compressedBytes / 1MB), ($backupBytes / 1MB),
            $(if ($compressedBytes -gt 0) { $backupBytes / $compressedBytes } else { 1 }))
        Note "COPY_ONLY = $copyOnly, page checksums recorded = $checksums"
    }
    else {
        Warn 'msdb has no backupset row for this file. The backup reported success; the history lookup did not match.'
    }

    # -----------------------------------------------------------------------
    # 7. Verify the media.
    # -----------------------------------------------------------------------

    if (-not $SkipVerify) {
        Step 'Verifying the media'
        $verifyWatch = [System.Diagnostics.Stopwatch]::StartNew()
        $null = Invoke-NonQuery 'RESTORE VERIFYONLY FROM DISK = @path WITH CHECKSUM' @{ '@path' = $backupPath } $TimeoutSeconds
        $verifyWatch.Stop()
        Pass ("RESTORE VERIFYONLY passed in {0:N1}s - the media is readable and every page checksum matched" -f $verifyWatch.Elapsed.TotalSeconds)
        Note 'this proves the file restores as far as reading it goes. Only deploy\Invoke-RestoreDrill.ps1 proves the rest'
    }
    else {
        Warn 'verification skipped by -SkipVerify. An unverified backup is a file, not a backup'
    }

    # -----------------------------------------------------------------------
    # 8. The manifest.
    # -----------------------------------------------------------------------

    Step 'Writing the manifest'

    $manifest = [ordered]@{
        schemaVersion    = 1
        database         = $Database
        serverInstance   = $ServerInstance
        backupUtc        = $startedUtc.ToString('o')
        backupPath       = $backupPath
        recoveryModel    = $recovery
        allocatedMb      = [double]$allocatedMb
        backupBytes      = $backupBytes
        compressedBytes  = $compressedBytes
        durationSeconds  = [math]::Round($watch.Elapsed.TotalSeconds, 3)
        copyOnly         = $true
        checksum         = $true
        verified         = (-not $SkipVerify.IsPresent)
        integrityChecked = $RunIntegrityCheck.IsPresent
        totalRows        = $totalRows
        tableCounts      = $tableCounts
        fingerprint      = [ordered]@{
            tables     = [int]$fingerprint.Rows[0]['TableCount']
            procedures = [int]$fingerprint.Rows[0]['ProcedureCount']
            views      = [int]$fingerprint.Rows[0]['ViewCount']
            tableTypes = [int]$fingerprint.Rows[0]['TableTypeCount']
        }
        toolVersions     = $toolVersions
        latestRunUtc     = $latestRunUtc
        takenBy          = "$env:USERDOMAIN\$env:USERNAME"
        takenOn          = $env:COMPUTERNAME
        psVersion        = $PSVersionTable.PSVersion.ToString()
    }

    # -Depth matters: the nested table counts vanish silently at the default on
    # 5.1. Datetimes are pre-formatted as strings above because ConvertTo-Json
    # renders [datetime] as /Date(...)/ on 5.1 and ISO-8601 on 7, and a manifest
    # that changes shape with the host it was written on is not a manifest.
    $json = $manifest | ConvertTo-Json -Depth 6

    $manifestWritten = $false
    try {
        Set-Content -LiteralPath $manifestPath -Value $json -Encoding UTF8
        $manifestWritten = $true
        Pass "manifest $manifestPath"
    }
    catch {
        Warn "could not write the manifest beside the backup: $($_.Exception.Message)"
        Note 'this is expected where the engine can write the backup directory and you cannot read it'
        Note 'the same numbers are in this transcript, and the restore drill can be pointed at the .bak alone'
    }

    # -----------------------------------------------------------------------
    # 9. Retention.
    # -----------------------------------------------------------------------

    Step 'Applying retention'

    if ($RetentionDays -le 0) {
        Note 'retention disabled by -RetentionDays 0; nothing pruned'
    }
    else {
        try {
            $cutoff = (Get-Date).AddDays(-$RetentionDays)
            $ours = @(Get-ChildItem -LiteralPath $BackupDirectory -Filter "$Database-*.bak" -File -ErrorAction Stop |
                Sort-Object LastWriteTime -Descending)

            if ($ours.Count -eq 0) {
                Note 'no backups from this script found in the directory'
            }
            else {
                # Never prune the newest, whatever its age. A retention window
                # shorter than the backup interval would otherwise leave the
                # directory empty, which is the one state retention must not be
                # able to produce.
                $prunable = @($ours | Select-Object -Skip 1 | Where-Object { $_.LastWriteTime -lt $cutoff })
                if ($prunable.Count -eq 0) {
                    Note "nothing older than $RetentionDays day(s) to prune; $($ours.Count) backup(s) retained"
                }
                foreach ($file in $prunable) {
                    $sidecar = [System.IO.Path]::ChangeExtension($file.FullName, '.json')
                    Remove-Item -LiteralPath $file.FullName -Force
                    if (Test-Path -LiteralPath $sidecar) { Remove-Item -LiteralPath $sidecar -Force }
                    Note "pruned $($file.Name)"
                }
                if ($prunable.Count -gt 0) { Pass "pruned $($prunable.Count) backup(s) older than $RetentionDays day(s)" }
            }
        }
        catch {
            Warn "retention skipped: $($_.Exception.Message)"
            Note 'pruning is a filesystem operation performed by YOUR account. Where you cannot enumerate the'
            Note 'backup directory, retention has to be the estate housekeeping job instead - say so in the ticket'
        }
    }

    $summary = [PSCustomObject]@{
        Database        = $Database
        BackupPath      = $backupPath
        ManifestPath    = $(if ($manifestWritten) { $manifestPath } else { $null })
        SizeBytes       = $compressedBytes
        UncompressedBytes = $backupBytes
        DurationSeconds = [math]::Round($watch.Elapsed.TotalSeconds, 3)
        TotalRows       = $totalRows
        Verified        = (-not $SkipVerify.IsPresent)
    }

    Write-Host ''
    Write-Host 'Backup complete.' -ForegroundColor Green
    Write-Host "  file      $backupPath"
    if ($null -ne $compressedBytes) {
        Write-Host ("  size      {0:N1} MB compressed, {1:N1} MB uncompressed" -f ($compressedBytes / 1MB), ($backupBytes / 1MB))
    }
    Write-Host ("  duration  {0:N1}s" -f $watch.Elapsed.TotalSeconds)
    Write-Host ("  contents  {0:N0} rows across {1} table(s)" -f $totalRows, $tableCounts.Count)
    Write-Host ''
    Write-Host '  A backup nobody has restored is a hypothesis. Rehearse it with' -ForegroundColor DarkGray
    Write-Host '  deploy\Invoke-RestoreDrill.ps1 - see docs\DISASTER-RECOVERY.md.' -ForegroundColor DarkGray

    $summary
}
finally {
    if ($connection.State -ne 'Closed') { $connection.Close() }
    $connection.Dispose()
}
