// ---------------------------------------------------------------------------
// SqlGraphPush
// Pushes dbo.Tickets straight to /external/connections/{id}/items/{itemId},
// bypassing the Graph connector agent. Used to seed or repair a connection.
//
// Unlike the agent-hosted connector, this tool does call Microsoft Graph, so
// certificate authentication here is for Graph. Application permissions remain
// ExternalConnection.ReadWrite.OwnedBy and ExternalItem.ReadWrite.OwnedBy,
// granted with admin consent, with the public certificate uploaded to the app
// registration. A client secret is supported as an alternative (Auth:Mode), read
// from Windows Credential Manager; no secret value appears in configuration.
// ---------------------------------------------------------------------------

using Azure.Core;
using Microsoft.Data.SqlClient;
using Microsoft.Graph;
using Microsoft.Graph.Models.ExternalConnectors;
using Microsoft.Graph.Models.ODataErrors;
using Serilog;
using Serilog.Events;
using SqlGraphPush;
using SqlTicketsConnector.Security.Certificates;
using SqlTicketsConnector.Security.Configuration;
using SqlTicketsConnector.Security.Content;
using SqlTicketsConnector.Security.Credentials;
using SqlTicketsConnector.Security.Logging;
using SqlTicketsConnector.Security.Secrets;
using SqlTicketsConnector.Security.Sql;

const string GraphScope = "https://graph.microsoft.com/.default";

PushOptions options;

try
{
    options = PushOptions.Load();
}
catch (Exception ex)
{
    Console.Error.WriteLine($"FATAL: {ex.Message}");
    return 2;
}

using var logger = new LoggerConfiguration()
    .MinimumLevel.Information()
    .Enrich.With(new ScrubbingEnricher())
    .WriteTo.Console(outputTemplate: "{Timestamp:HH:mm:ss} [{Level:u3}] {Message:lj}{NewLine}{Exception}")
    .WriteTo.File(
        Path.Combine(AppContext.BaseDirectory, "Logs", "SqlGraphPush.log"),
        fileSizeLimitBytes: 10L * 1024 * 1024,
        rollOnFileSizeLimit: true,
        retainedFileCountLimit: 30,
        restrictedToMinimumLevel: LogEventLevel.Information)
    .CreateLogger();

Log.Logger = logger;

ValidationErrors errors = options.Validate();

if (errors.HasErrors)
{
    Log.Fatal(
        "Configuration in {ConfigurationPath} is invalid. {ErrorCount} problem(s):{NewLine}{Errors}",
        options.SourcePath,
        errors.Errors.Count,
        Environment.NewLine,
        errors.ToMessage());

    return 2;
}

ICertificateResolver? certificateResolver = options.Auth.ParsedMode == AuthMode.Certificate
    ? new StoreCertificateResolver(options.Auth, Log.Logger)
    : null;

TokenCredential credential;
CachingSecretProvider? secretCache = null;

try
{
    credential = TokenCredentialFactory.Create(options.Auth, certificateResolver, Log.Logger);
}
catch (Exception ex)
{
    Log.Fatal(RedactedException.Wrap(ex), "Could not build the Entra credential.");
    return 3;
}

ISecretProvider? secrets = null;

if (!string.IsNullOrWhiteSpace(options.KeyVault.Uri))
{
    secretCache = new CachingSecretProvider(
        new KeyVaultSecretProvider(new Uri(options.KeyVault.Uri), credential, Log.Logger),
        TimeSpan.FromMinutes(options.KeyVault.SecretCacheTtlMinutes),
        Log.Logger);

    secrets = secretCache;
}

var connections = new SqlConnectionFactory(
    options.DataSource,
    options.Environment,
    secrets,
    options.KeyVault.SecretName(KeyVaultOptions.SqlPasswordKey),
    credential,
    Log.Logger);

// The same certificate credential authenticates to Graph. Scope is unchanged.
var graph = new GraphServiceClient(credential, new[] { GraphScope });

try
{
    await EnsureConnectionAsync();
    await EnsureSchemaAsync();
    int pushed = await PushItemsAsync();

    Log.Information("Ingestion complete. {Items} item(s) written to connection {ConnectionId}.",
        pushed,
        options.Graph.ConnectionId);

    return 0;
}
catch (Exception ex)
{
    Log.Fatal(RedactedException.Wrap(ex), "Ingestion failed.");
    return 4;
}
finally
{
    secretCache?.Dispose();
    Log.CloseAndFlush();
}

// ---------------------------------------------------------------------------
// 1. Create the external connection (idempotent)
// ---------------------------------------------------------------------------
async Task EnsureConnectionAsync()
{
    try
    {
        ExternalConnection? existing = await graph.External.Connections[options.Graph.ConnectionId].GetAsync();
        Log.Information(
            "Connection {ConnectionId} already exists. State {State}.",
            options.Graph.ConnectionId,
            existing?.State);
        return;
    }
    catch (ODataError ex) when (ex.ResponseStatusCode == 404)
    {
        // Not found, fall through to create.
    }

    await graph.External.Connections.PostAsync(new ExternalConnection
    {
        Id = options.Graph.ConnectionId,
        Name = options.Graph.ConnectionName,
        Description = options.Graph.Description,
    });

    Log.Information("Connection {ConnectionId} created.", options.Graph.ConnectionId);
}

// ---------------------------------------------------------------------------
// 2. Register the schema (async server-side operation, poll until Ready)
// ---------------------------------------------------------------------------
async Task EnsureSchemaAsync()
{
    ExternalConnection? connection = await graph.External.Connections[options.Graph.ConnectionId].GetAsync();

    if (connection?.State == ConnectionState.Ready)
    {
        Log.Information("Schema already registered.");
        return;
    }

    Schema schema = TicketSchema.Build();

    await graph.External.Connections[options.Graph.ConnectionId].Schema.PatchAsync(schema);

    Log.Information(
        "Schema registration submitted. This runs server side and typically takes 5 to 15 minutes.");

    DateTimeOffset deadline = DateTimeOffset.UtcNow.AddMinutes(options.Graph.SchemaReadyTimeoutMinutes);

    while (true)
    {
        await Task.Delay(TimeSpan.FromSeconds(30));

        ConnectionState? state = (await graph.External.Connections[options.Graph.ConnectionId].GetAsync())?.State;
        Log.Information("Connection state {State}.", state);

        if (state == ConnectionState.Ready)
        {
            return;
        }

        if (state == ConnectionState.LimitExceeded)
        {
            throw new InvalidOperationException("Item quota exceeded for this tenant.");
        }

        if (DateTimeOffset.UtcNow > deadline)
        {
            throw new TimeoutException(
                $"Schema registration did not reach Ready within {options.Graph.SchemaReadyTimeoutMinutes} minute(s).");
        }
    }
}

// ---------------------------------------------------------------------------
// 3. Read SQL rows and PUT one external item per row
// ---------------------------------------------------------------------------
async Task<int> PushItemsAsync()
{
    // This tool always pushes the whole table, so it needs no watermark predicate.
    // Soft deleted rows are excluded rather than pushed and then removed.
    const string SelectColumns = "SELECT TicketId, Title, Status, AssignedTo, Body, LastModified FROM dbo.Tickets";

    string query = options.DataSource.SoftDeleteEnabled
        ? SelectColumns + " WHERE IsDeleted = 0 ORDER BY TicketId;"
        : SelectColumns + " ORDER BY TicketId;";

    // Entra group principals, never Everyone: the ticket body is customer data.
    List<Acl> acl = options.Acl.GrantGroupObjectIds
        .Select(id => new Acl
        {
            Type = AclType.Group,
            Value = id.Trim(),
            AccessType = AccessType.Grant,
        })
        .ToList();

    int pushed = 0;

    await using SqlConnection connection = await connections.OpenAsync(CancellationToken.None);
    await using var command = new SqlCommand(query, connection);
    command.CommandTimeout = options.DataSource.ConnectTimeoutSeconds;

    await using SqlDataReader reader = await command.ExecuteReaderAsync();

    int ticketIdOrdinal = reader.GetOrdinal("TicketId");
    int titleOrdinal = reader.GetOrdinal("Title");
    int statusOrdinal = reader.GetOrdinal("Status");
    int assignedToOrdinal = reader.GetOrdinal("AssignedTo");
    int bodyOrdinal = reader.GetOrdinal("Body");
    int lastModifiedOrdinal = reader.GetOrdinal("LastModified");

    while (await reader.ReadAsync())
    {
        int ticketId = reader.GetInt32(ticketIdOrdinal);
        string itemId = $"ticket{ticketId}";   // alphanumeric, 128 character maximum

        string body = reader.IsDBNull(bodyOrdinal) ? string.Empty : reader.GetString(bodyOrdinal);
        TruncationResult content = ContentTruncator.Truncate(body, options.DataSource.MaxContentBytes);

        if (content.Truncated)
        {
            Log.Warning(
                "Item {ItemId} content truncated from {OriginalBytes} to {FinalBytes} bytes.",
                itemId,
                content.OriginalBytes,
                content.FinalBytes);
        }

        var item = new ExternalItem
        {
            Id = itemId,
            Acl = acl,
            Properties = new Properties
            {
                AdditionalData = new Dictionary<string, object>
                {
                    ["ticketId"] = ticketId.ToString(),
                    ["title"] = Text(reader, titleOrdinal),
                    ["status"] = Text(reader, statusOrdinal),
                    ["assignedTo"] = Text(reader, assignedToOrdinal),
                    ["lastModified"] = DateTime
                        .SpecifyKind(reader.GetDateTime(lastModifiedOrdinal), DateTimeKind.Utc)
                        .ToString("o"),
                    ["url"] = string.Format(options.DataSource.ItemUrlTemplate, ticketId),
                },
            },
            Content = new ExternalItemContent
            {
                Type = ExternalItemContentType.Text,
                Value = content.Content,
            },
        };

        await graph.External.Connections[options.Graph.ConnectionId].Items[itemId].PutAsync(item);

        // Item ID only. Property values and content are customer data.
        Log.Information("Indexed {ItemId} ({ContentBytes} bytes).", itemId, content.FinalBytes);
        pushed++;
    }

    return pushed;
}

static string Text(SqlDataReader reader, int ordinal)
{
    return reader.IsDBNull(ordinal) ? string.Empty : reader.GetString(ordinal);
}
