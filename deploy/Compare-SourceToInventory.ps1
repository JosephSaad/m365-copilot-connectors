<#
.SYNOPSIS
    Finds source records the connector has never written — the one direction
    Compare-InventoryToIndex.ps1 cannot see.

.DESCRIPTION
    The two reconcilers are complements, and neither is sufficient alone.

      Compare-InventoryToIndex   inventory -> Graph.  Finds items the connector
                                 recorded writing that the index does not have
                                 (LOST), and items it recorded deleting that the
                                 index still serves (ORPHAN). Needs no source,
                                 so it works for every connector including CDP.

      THIS SCRIPT               source -> inventory.  Finds records that exist
                                 in the source and have NO inventory row, which
                                 means the connector never wrote them: a crawl
                                 that never reached them, a filter that excluded
                                 them, or a run that stopped short and was never
                                 re-run.

    That second direction is invisible to the first by construction. An item the
    connector never wrote leaves no record, and Graph will not enumerate, so
    nothing but the source can report it.

    WHY THIS IS THREE SOURCES AND NOT FIVE. It reads Oracle, Teradata and
    MongoDB, whose connectors share a documented key contract. It does not read
    SQL Server, because Compare-SourceToIndex.ps1 already covers that path and
    goes further - it compares against Graph directly rather than against the
    inventory. And it does not read CDP: Hive needs a Kerberos ODBC connection,
    HDFS a WebHDFS client and Atlas a REST call, which is three integrations
    rather than a query, and the honest answer is that CDP's source direction
    stays open.

    Strictly read-only, and it never touches Graph at all.

.PARAMETER Source
    Oracle, Teradata or MongoDB. Decides which driver is loaded and which
    identifier prefix the connector composes.

.PARAMETER BinPath
    The connector's build output, where the provider assembly is found. Defaults
    to the sibling of ConfigPath.

.EXAMPLE
    .\Compare-SourceToInventory.ps1 -Source Oracle `
        -ConfigPath ..\src\OracleGraphPush\appsettings.json
#>

param(
    [Parameter(Mandatory)][ValidateSet('Oracle', 'Teradata', 'MongoDB')][string]$Source,
    [string]$ConfigPath = '.\appsettings.json',
    [string]$ConnectionId,
    [string]$StateConnectionString,
    [string]$BinPath,
    [int]$MaxRows = 200000,
    [switch]$Detail,
    [System.Security.SecureString]$SourcePassword,
    [int]$TimeoutSeconds = 120
)

$ErrorActionPreference = 'Stop'

. (Join-Path $PSScriptRoot 'GraphPushAuth.ps1')

function Note([string]$msg) { Write-Host "  note  $msg" -ForegroundColor DarkGray }

$config = Get-PushConfig -Path $ConfigPath
if (-not $ConnectionId) { $ConnectionId = $config.Graph.ConnectionId }
if (-not $StateConnectionString -and $config.Settings) {
    $StateConnectionString = $config.Settings.StateConnectionString
}

if (-not $StateConnectionString) {
    Write-Host 'No state connection string, so there is no inventory to compare against.' -ForegroundColor Red
    exit 2
}

# The prefixes the connectors compose. Kept here rather than derived, so that a
# connector changing its identifier scheme breaks this loudly instead of
# reporting every item as missing.
$prefix = @{ Oracle = 'oraclerecord'; Teradata = 'teradatarecord'; MongoDB = 'mongorecord' }[$Source]

if (-not $BinPath) { $BinPath = Split-Path -Parent (Resolve-Path $ConfigPath) }

function Import-Provider([string]$pattern) {
    $dll = Get-ChildItem -Path $BinPath -Recurse -Filter $pattern -ErrorAction SilentlyContinue |
        Select-Object -First 1

    if (-not $dll) {
        Write-Host "Could not find $pattern under $BinPath." -ForegroundColor Red
        Note 'Build the connector first, or pass -BinPath pointing at its output.'
        exit 3
    }

    Add-Type -Path $dll.FullName
    return $dll.FullName
}

# ---------------------------------------------------------------------------
# The source. Every branch yields the same shape: Key, IsDeleted.
# ---------------------------------------------------------------------------

$plain = if ($SourcePassword) {
    [System.Net.NetworkCredential]::new('', $SourcePassword).Password
} else { $null }

$sourceKeys = New-Object System.Collections.Generic.HashSet[string]
$sourceDeleted = New-Object System.Collections.Generic.HashSet[string]
$softDelete = [bool]$config.DataSource.SoftDeleteEnabled

Write-Host "Reading $Source source"

if ($Source -eq 'MongoDB') {
    $null = Import-Provider 'MongoDB.Driver.dll'
    $null = Import-Provider 'MongoDB.Bson.dll'

    $url = [MongoDB.Driver.MongoUrl]::Create($config.DataSource.Server)
    $settings = [MongoDB.Driver.MongoClientSettings]::FromUrl($url)

    if ($config.DataSource.SqlUserId -and $plain) {
        $settings.Credential = [MongoDB.Driver.MongoCredential]::CreateCredential(
            $config.DataSource.Database, $config.DataSource.SqlUserId, $plain)
    }

    $client = [MongoDB.Driver.MongoClient]::new($settings)
    $database = $client.GetDatabase($config.DataSource.Database)
    $collection = $database.GetCollection[MongoDB.Bson.BsonDocument]($config.Source.ItemView)

    $filter = [MongoDB.Driver.Builders[MongoDB.Bson.BsonDocument]]::Filter.Empty
    $cursor = $collection.Find($filter).Limit($MaxRows).ToEnumerable()

    foreach ($doc in $cursor) {
        $id = $doc['_id'].ToString()
        $safe = -join ($id.ToCharArray() | Where-Object { [char]::IsLetterOrDigit($_) })
        if ($safe.Length -eq 0) { continue }
        if ($safe.Length -gt 100) { $safe = $safe.Substring(0, 100) }

        $deleted = $softDelete -and $doc.Contains('isDeleted') -and $doc['isDeleted'].ToBoolean()
        if ($deleted) { $null = $sourceDeleted.Add($prefix + $safe) }
        else { $null = $sourceKeys.Add($prefix + $safe) }
    }
}
else {
    $isDeletedColumn = if ($softDelete) { ', IS_DELETED' } else { '' }
    $query = "SELECT RECORD_ID$isDeletedColumn FROM $($config.Source.ItemView)"

    if ($Source -eq 'Oracle') {
        $null = Import-Provider 'Oracle.ManagedDataAccess.dll'
        $builder = [Oracle.ManagedDataAccess.Client.OracleConnectionStringBuilder]::new()
        $builder.DataSource = $config.DataSource.Server
        if ($plain) { $builder.UserID = $config.DataSource.SqlUserId; $builder.Password = $plain }
        else { $builder.UserID = '/' }
        $connection = [Oracle.ManagedDataAccess.Client.OracleConnection]::new($builder.ConnectionString)
    }
    else {
        $null = Import-Provider 'Teradata.Client.Provider.dll'
        $builder = [Teradata.Client.Provider.TdConnectionStringBuilder]::new()
        $builder.DataSource = $config.DataSource.Server
        if ($config.DataSource.Database) { $builder.Database = $config.DataSource.Database }
        if ($plain) {
            $builder.UserId = $config.DataSource.SqlUserId
            $builder.Password = $plain
            $builder.AuthenticationMechanism = 'TD2'
        }
        else { $builder.AuthenticationMechanism = 'KRB5' }
        $connection = [Teradata.Client.Provider.TdConnection]::new($builder.ConnectionString)
    }

    try {
        $connection.Open()
        $command = $connection.CreateCommand()
        $command.CommandText = $query
        $command.CommandTimeout = $TimeoutSeconds
        $reader = $command.ExecuteReader()

        $read = 0
        while ($reader.Read() -and $read -lt $MaxRows) {
            $read++
            if ($reader.IsDBNull(0)) { continue }

            $key = $prefix + [string]([long]$reader.GetValue(0))
            $deleted = $softDelete -and -not $reader.IsDBNull(1) -and
                       ([double]$reader.GetValue(1)) -ne 0

            if ($deleted) { $null = $sourceDeleted.Add($key) } else { $null = $sourceKeys.Add($key) }
        }

        $reader.Close()
    }
    finally {
        $connection.Close()
        $connection.Dispose()
    }
}

Write-Host "Source: $($sourceKeys.Count) live, $($sourceDeleted.Count) soft-deleted"

# ---------------------------------------------------------------------------
# The inventory
# ---------------------------------------------------------------------------

$inventory = @{}
$stateConnection = New-Object System.Data.SqlClient.SqlConnection $StateConnectionString

try {
    $stateConnection.Open()
    $stateCommand = $stateConnection.CreateCommand()
    $stateCommand.CommandText = 'SELECT ItemId, State FROM crawl.vwItemInventory WHERE ConnectionId = @c;'
    $stateCommand.CommandTimeout = $TimeoutSeconds
    $null = $stateCommand.Parameters.Add(
        (New-Object System.Data.SqlClient.SqlParameter('@c', $ConnectionId)))

    $rows = New-Object System.Data.DataTable
    $null = (New-Object System.Data.SqlClient.SqlDataAdapter $stateCommand).Fill($rows)

    foreach ($r in $rows.Rows) { $inventory[[string]$r.ItemId] = [int]$r.State }
}
catch {
    Write-Host "Could not read crawl.vwItemInventory: $($_.Exception.Message)" -ForegroundColor Red
    Note 'The connector''s own login is DENYed SELECT on the crawl views by sql/25; use crawl_reader.'
    exit 3
}
finally {
    $stateConnection.Close()
    $stateConnection.Dispose()
}

Write-Host "Inventory: $($inventory.Count) item(s) on record"
Write-Host ''

# ---------------------------------------------------------------------------
# The comparison
# ---------------------------------------------------------------------------

$missing = @($sourceKeys | Where-Object { -not $inventory.ContainsKey($_) })
$resurrected = @($sourceKeys | Where-Object { $inventory.ContainsKey($_) -and $inventory[$_] -ne 1 })

if ($missing.Count -gt 0) {
    Write-Host "$($missing.Count) source record(s) the connector has NEVER written" -ForegroundColor Red
    Note 'A crawl that never reached them, a filter that excluded them, or a run that stopped short.'
    Note 'A full crawl writes them: every write is an upsert, so re-reading what is there costs only time.'
    if ($Detail) { $missing | Select-Object -First 50 | ForEach-Object { Write-Host "    $_" } }
}

if ($resurrected.Count -gt 0) {
    Write-Host ''
    Write-Host "$($resurrected.Count) record(s) live at the source but tombstoned in the inventory" -ForegroundColor Yellow
    Note 'The sweep recorded a delete and the record came back. The next full crawl re-writes them.'
    if ($Detail) { $resurrected | Select-Object -First 50 | ForEach-Object { Write-Host "    $_" } }
}

Write-Host ''
Write-Host 'What this run did NOT check' -ForegroundColor Cyan
Note 'It never called Graph. An item recorded as written may still be absent from the index, and one'
Note 'recorded as deleted may still be served - both are Compare-InventoryToIndex.ps1, and the two'
Note 'scripts together are what full coverage means. Run both.'

if ($missing.Count -eq 0 -and $resurrected.Count -eq 0) {
    Write-Host ''
    Write-Host 'Every live source record has an inventory row.' -ForegroundColor Green
    exit 0
}

exit 4
