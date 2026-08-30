#Requires -Version 5.1
<#
.SYNOPSIS
    Runs dbo.uspCheckEffectiveTriggers against the timesheet source and exits
    non-zero on a finding. The scheduler for estates that have no SQL Agent.

.DESCRIPTION
    WHAT IT IS FOR. sql/32 schedules the same check as a SQL Agent job, which is
    the right answer wherever Agent exists. SQL Server Express has no Agent -
    not stopped, absent, with no setting that brings it back - so on Express
    there is nothing inside SQL Server that can run anything on a clock. This
    script is the outside clock: point Windows Task Scheduler at it daily.

    WHAT IT IS NOT FOR. It is not a substitute for somebody watching. It exits
    1 on a finding so that Task Scheduler records a non-zero Last Run Result,
    and a Last Run Result nobody reads is exactly as useful as the silence it
    replaced. Give the task an action, an alert, or an operator.

    WHY IT EXITS RATHER THAN JUST PRINTING. The failure this detects has no
    symptom: a disabled cascading trigger leaves the source accepting writes and
    the crawls succeeding while EffectiveLastModified stops moving, so every
    incremental crawl afterwards silently misses rows. A run of this script that
    prints red text into a window nobody has open has detected nothing.

    QUOTED_IDENTIFIER IS SET ON EXPLICITLY BEFORE THE EXEC. .NET's SqlClient
    connects with it ON and sqlcmd connects with it OFF, and the probe inside
    the procedure issues an UPDATE - which is refused outright against a table
    carrying a filtered index when the session has it OFF. Setting it here means
    this script behaves the same whichever of the two ever runs it.

.PARAMETER SqlInstance
    The instance hosting the timesheet source. Default localhost.

.PARAMETER Database
    The source database. Default Ops.

.PARAMETER NoProbe
    Skip the live write probe, leaving only the catalogue checks. Use where
    change control will not accept a rolled-back write against a production
    table, and understand what is given up: a trigger that is present, enabled
    and altered to do nothing passes every remaining check.

.PARAMETER TimeoutSeconds
    Command timeout. The consistency counts scan the hierarchy, so this needs
    room on a large source. Default 120.

.PARAMETER AllowUntrustedServerCertificate
    Skip validation of the server's TLS certificate. Off, and it stays off
    unless somebody types it: an instance with a self-signed certificate is a
    development instance, and a scheduled task that silently accepts any
    certificate is a scheduled task that will accept the wrong one. Same stance
    as Test-SqlSource.ps1, which refuses the equivalent outright in Production.
    Encryption itself is not negotiable and is set either way.

.EXAMPLE
    .\Test-TriggerHealth.ps1

.EXAMPLE
    # A development instance with a self-signed certificate. The connection is
    # refused without the switch, and the refusal exits 1 rather than reading as
    # a pass - "the check could not run" and "the check found nothing" must not
    # produce the same Last Run Result.
    .\Test-TriggerHealth.ps1 -AllowUntrustedServerCertificate

.EXAMPLE
    # The Windows Scheduled Task action, for an estate with no SQL Agent.
    powershell -NoProfile -ExecutionPolicy Bypass -File C:\connectors\deploy\Test-TriggerHealth.ps1 -SqlInstance localhost -Database Ops
#>
[CmdletBinding()]
param(
    [string]$SqlInstance    = 'localhost',
    [string]$Database       = 'Ops',
    [switch]$NoProbe,
    [int]$TimeoutSeconds    = 120,
    [switch]$AllowUntrustedServerCertificate
)

$ErrorActionPreference = 'Stop'

# Built rather than concatenated, and Integrated Security only. The procedure
# writes - the probe rolls back, but the permission it needs is real - so this
# runs as a Windows identity that is db_owner on the source. There is no
# password to put in a scheduled task's arguments, which is the point.
#
# Encrypt is set and is not a parameter: "encryption is not negotiable" is the
# rule SqlConnectionStringFactory and Test-SqlSource.ps1 both hold, and this is
# that line here. Certificate VALIDATION is separable from encryption and is the
# only part a switch can turn off.
$builder = New-Object System.Data.SqlClient.SqlConnectionStringBuilder
$builder['Data Source']        = $SqlInstance
$builder['Initial Catalog']    = $Database
$builder['Integrated Security'] = $true
$builder['Application Name']   = 'Test-TriggerHealth'
$builder['Connect Timeout']    = 15
$builder['Encrypt']            = $true

if ($AllowUntrustedServerCertificate) {
    # Assigned only when asked for. Never defaulted, and never set from a
    # configuration file this script reads, so there is no path by which an
    # estate ends up with certificate validation off and nobody having decided.
    $builder['TrustServerCertificate'] = $true
    Write-Warning 'Server certificate validation is disabled for this run. Do not schedule it this way against a production instance - install a certificate that chains to a trusted root instead.'
}

$connectionString = $builder.ConnectionString

$probe = if ($NoProbe) { 0 } else { 1 }

# @Throw = 0: the findings are read here and the exit code is decided here, so
# the procedure is asked for its results rather than for an exception. The
# scheduled task's signal is this script's exit code, not SQL Server's error.
$sql = @"
SET QUOTED_IDENTIFIER ON;
SET ANSI_NULLS ON;
SET NOCOUNT ON;
EXEC dbo.uspCheckEffectiveTriggers @Probe = $probe, @Throw = 0;
"@

$connection = $null
$failed = 0
$skipped = 0

try {
    $connection = New-Object System.Data.SqlClient.SqlConnection $connectionString
    $connection.Open()

    $command = $connection.CreateCommand()
    $command.CommandText = $sql
    $command.CommandTimeout = $TimeoutSeconds

    $reader = $command.ExecuteReader()

    # The procedure returns two result sets: the findings, then the verdict.
    # Both are read. Reading only the verdict would leave a failure with no
    # detail in the one place a person looks afterwards.
    Write-Output ''
    Write-Output "Trigger health for $Database on $SqlInstance at $((Get-Date).ToString('u'))"
    Write-Output ''

    while ($reader.Read()) {
        $verdict = [string]$reader['verdict']
        $name    = [string]$reader['check_name']
        $detail  = [string]$reader['detail']

        if ($verdict -eq 'FAIL')    { $failed++ }
        if ($verdict -eq 'SKIPPED') { $skipped++ }

        $line = '{0,-8} {1}' -f $verdict, $name
        switch ($verdict) {
            'FAIL'    { Write-Output $line; Write-Output "         $detail" }
            'SKIPPED' { Write-Output $line; Write-Output "         $detail" }
            default   { Write-Output $line }
        }
    }

    [void]$reader.NextResult()
    while ($reader.Read()) {
        Write-Output ''
        Write-Output ('{0} - {1} ok, {2} failed, {3} skipped' -f
            $reader['verdict'], $reader['checks_ok'], $reader['checks_failed'], $reader['checks_skipped'])
    }

    $reader.Close()
}
catch {
    # A connection or permission failure is not a passing check. It is reported
    # as a failure and exits non-zero, because "the check could not run" and
    # "the check found nothing" must not produce the same Last Run Result.
    Write-Output ''
    Write-Output "FAIL     the check could not be run at all: $($_.Exception.Message)"
    if ($null -ne $connection) { $connection.Dispose() }
    exit 1
}
finally {
    if ($null -ne $connection) { $connection.Dispose() }
}

Write-Output ''

if ($failed -gt 0) {
    Write-Output "EffectiveLastModified is not being maintained on $Database. Every incremental crawl since is reading a delta that is missing rows, and reporting success while doing it. Repair per sql/31, then re-run sql/26 section 4 to correct the rows written meanwhile."
    exit 1
}

if ($skipped -gt 0) {
    # Not a failure and not a clean pass. A skipped check is one that did not
    # run, and calling that a pass is how a monitoring script comes to mean
    # nothing. Exit 0 so the task is not red, and say the word.
    Write-Output "Passed, with $skipped check(s) skipped - those did not run and were not assessed. See the detail lines above."
    exit 0
}

Write-Output 'All checks passed.'
exit 0
