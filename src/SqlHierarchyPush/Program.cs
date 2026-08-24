// ---------------------------------------------------------------------------
// SqlHierarchyPush
//
// The three level test case: Customer -> Engagement -> TimeEntry, pushed
// straight to /external/connections/{id}/items/{itemId}, bypassing the Graph
// connector agent.
//
// WHAT THIS DEMONSTRATES, AND WHY IT NEEDS A TOOL OF ITS OWN
//
// A Graph external item has a flat property list. There is no parent property,
// no child collection and no join at retrieval time. Copilot fetches individual
// items; it does not traverse anything. So a hierarchy cannot be indexed as a
// hierarchy — it has to be flattened, with every descendant physically carrying
// its ancestors' text, or a search for the customer will never reach the time
// entries.
//
// That flattening lives in sql/12-timesheet-views.sql, deliberately: a DBA can
// read exactly what leaves the database, and this program holds one query
// against one view with no join logic at all.
//
// Everything security-related is the shared engine: SqlTicketsConnector.Security
// resolves the certificate, builds the credential, constructs the SQL connection
// and scrubs the logs. Only the schema and the item shape are new here.
//
// Coexists with SqlGraphPush rather than replacing it. Different tables,
// different connection ID, different schema; run both against one tenant.
//
// Exit codes: 0 success, 2 configuration invalid, 3 credential, 4 ingestion.
// ---------------------------------------------------------------------------

using Azure.Core;
using Microsoft.Data.SqlClient;
using Microsoft.Graph;
using Microsoft.Graph.Models.ExternalConnectors;
using Microsoft.Graph.Models.ODataErrors;
using Serilog;
using Serilog.Events;
using SqlHierarchyPush;
using SqlTicketsConnector.Security.Certificates;
using SqlTicketsConnector.Security.Configuration;
using SqlTicketsConnector.Security.Content;
using SqlTicketsConnector.Security.Credentials;
using SqlTicketsConnector.Security.Logging;
using SqlTicketsConnector.Security.Secrets;
using SqlTicketsConnector.Security.Sql;

const string GraphScope = "https://graph.microsoft.com/.default";

// Reads the source and reports what would be written, without a tenant. The
// flattening is the part worth testing and it can be proven without Graph.
bool dryRun = args.Any(a => string.Equals(a, "--dry-run", StringComparison.OrdinalIgnoreCase));

HierarchyOptions options;

try
{
    options = HierarchyOptions.Load();
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
        Path.Combine(AppContext.BaseDirectory, "Logs", "SqlHierarchyPush.log"),
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

// Only constructed when it will be used: a dry run must not need a tenant.
GraphServiceClient? graph = dryRun ? null : new GraphServiceClient(credential, new[] { GraphScope });

try
{
    if (dryRun)
    {
        Log.Information("Dry run: reading {View} and reporting what would be written. No Graph call is made.",
            options.Source.ItemView);
    }
    else
    {
        await EnsureConnectionAsync();
        await EnsureSchemaAsync();
    }

    PushSummary summary = await PushItemsAsync();

    Log.Information(
        "{Mode} complete. {Total} item(s): {Customers} customer(s), {Engagements} engagement(s), {TimeEntries} time entr(y/ies). " +
        "{Truncated} truncated, {Throttled} throttling wait(s).",
        dryRun ? "Dry run" : "Ingestion",
        summary.Total,
        summary.Customers,
        summary.Engagements,
        summary.TimeEntries,
        summary.Truncated,
        summary.ThrottleWaits);

    if (!dryRun)
    {
        // Stated every run, because it is the one property of this path people
        // forget: nothing here ever deletes.
        Log.Information(
            "Rows soft deleted since a previous run are excluded from this push, not removed from the index. " +
            "Run deploy/Compare-SourceToIndex.ps1 to find the orphans that leaves.");
    }

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
        ExternalConnection? existing = await graph!.External.Connections[options.Graph.ConnectionId].GetAsync();
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

    await graph!.External.Connections.PostAsync(new ExternalConnection
    {
        Id = options.Graph.ConnectionId,
        Name = options.Graph.ConnectionName,
        Description = options.Graph.Description,
    });

    Log.Information("Connection {ConnectionId} created.", options.Graph.ConnectionId);
}

// ---------------------------------------------------------------------------
// 2. Register the schema (async server side operation, poll until Ready)
//
// One flat schema serves all three levels. A time entry leaves the engagement
// and customer columns populated; a customer leaves the descendant columns
// unset. That is what "flat" costs, and it is cheaper than three connections,
// which could not be searched as one thing.
//
// Two platform rules shape every line below:
//   * isSearchable and isRefinable are mutually exclusive. Anything a person
//     types goes in the searchable column; anything they filter or facet by
//     goes in the refinable one.
//   * property names are 32 alphanumeric characters at most.
// ---------------------------------------------------------------------------
async Task EnsureSchemaAsync()
{
    ExternalConnection? connection = await graph!.External.Connections[options.Graph.ConnectionId].GetAsync();

    if (connection?.State == ConnectionState.Ready)
    {
        Log.Information("Schema already registered.");
        return;
    }

    var schema = new Schema
    {
        BaseType = "microsoft.graph.externalItem",
        Properties = new List<Property>
        {
            // --- which level this item is, and where it sits ----------------
            // Refinable, not searchable: you facet by it, you do not type it.
            Prop("itemType", PropertyType.String, queryable: true, retrievable: true, refinable: true),

            Prop("title", PropertyType.String, searchable: true, queryable: true, retrievable: true,
                label: Label.Title),
            Prop("url", PropertyType.String, retrievable: true, label: Label.Url),
            Prop("lastModified", PropertyType.DateTime, queryable: true, retrievable: true,
                label: Label.LastModifiedDateTime),

            // containerName and containerUrl are how the platform expresses
            // "this item sits inside that one" — an engagement's container is
            // its customer, a time entry's is its engagement. It is the closest
            // a flat index gets to the hierarchy, and result surfaces show it.
            Prop("containerName", PropertyType.String, searchable: true, queryable: true, retrievable: true,
                label: Label.ContainerName),
            Prop("containerUrl", PropertyType.String, retrievable: true, label: Label.ContainerUrl),

            // The breadcrumb as one searchable string: "Contoso > Data Platform
            // Migration > 2026-08-14 Priya Raman". Matches a query that names
            // two levels at once, which neither level alone would.
            Prop("hierarchyPath", PropertyType.String, searchable: true, queryable: true, retrievable: true),

            // --- level 1, present on ALL THREE levels -----------------------
            // This block is the requirement. customerName is searchable on the
            // time entry as well as on the customer, which is the only reason a
            // search for the customer reaches the time entry at all.
            Prop("customerName", PropertyType.String, searchable: true, queryable: true, retrievable: true),
            Prop("customerCode", PropertyType.String, searchable: true, queryable: true, retrievable: true),
            Prop("accountManager", PropertyType.String, searchable: true, queryable: true, retrievable: true),
            Prop("industry", PropertyType.String, queryable: true, retrievable: true, refinable: true),
            Prop("region", PropertyType.String, queryable: true, retrievable: true, refinable: true),

            // --- level 2, present on engagements and time entries -----------
            Prop("engagementName", PropertyType.String, searchable: true, queryable: true, retrievable: true),
            Prop("engagementCode", PropertyType.String, searchable: true, queryable: true, retrievable: true),
            Prop("projectManager", PropertyType.String, searchable: true, queryable: true, retrievable: true),
            Prop("practice", PropertyType.String, queryable: true, retrievable: true, refinable: true),
            Prop("status", PropertyType.String, queryable: true, retrievable: true, refinable: true),

            // --- level 3 ----------------------------------------------------
            Prop("consultantName", PropertyType.String, searchable: true, queryable: true, retrievable: true),
            Prop("consultantEmail", PropertyType.String, queryable: true, retrievable: true),
            Prop("workType", PropertyType.String, queryable: true, retrievable: true, refinable: true),
            Prop("workDate", PropertyType.DateTime, queryable: true, retrievable: true),
            Prop("hours", PropertyType.Double, queryable: true, retrievable: true),
            Prop("billable", PropertyType.Boolean, queryable: true, retrievable: true),

            // --- roll ups, so an answer can cite a number without arithmetic -
            Prop("contractValue", PropertyType.Double, queryable: true, retrievable: true),
            Prop("totalHours", PropertyType.Double, queryable: true, retrievable: true),
            Prop("childCount", PropertyType.Int64, queryable: true, retrievable: true),
        },
    };

    await graph.External.Connections[options.Graph.ConnectionId].Schema.PatchAsync(schema);

    Log.Information(
        "Schema registration submitted: {Count} properties. This runs server side and typically takes 5 to 15 minutes.",
        schema.Properties.Count);
    Log.Information(
        "It cannot be changed afterwards except by adding properties. Run deploy/Watch-SchemaRegistration.ps1 " +
        "to watch, and read the schema it prints before pushing anything.");

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
                $"Schema registration did not reach Ready within {options.Graph.SchemaReadyTimeoutMinutes} minute(s). " +
                "The operation continues server side; re-run deploy/Watch-SchemaRegistration.ps1 rather than recreating the connection.");
        }
    }
}

// ---------------------------------------------------------------------------
// 3. Read the flattened view and PUT one external item per row
// ---------------------------------------------------------------------------
async Task<PushSummary> PushItemsAsync()
{
    // Parents first. A run interrupted halfway then leaves customers and
    // engagements present with some time entries missing, which is a coherent
    // index; the reverse would leave orphaned children whose ancestors are not
    // there to be found. The view name is validated as an identifier in
    // SourceSection.Validate, which is what makes concatenating it safe.
    string top = options.Source.MaxItems > 0 ? $"TOP ({options.Source.MaxItems}) " : string.Empty;

    string query =
        $"SELECT {top}ItemId, ItemType, Title, Url, LastModified, HierarchyPath, ContainerName, ContainerUrl, " +
        "CustomerId, CustomerName, CustomerCode, Industry, Region, AccountManager, AccountManagerEmail, " +
        "EngagementId, EngagementName, EngagementCode, Practice, Status, ProjectManager, " +
        "ConsultantName, ConsultantEmail, WorkDate, Hours, Billable, WorkType, " +
        "ContractValue, TotalHours, ChildCount, Content " +
        $"FROM {options.Source.ItemView} " +
        "ORDER BY CASE ItemType WHEN 'Customer' THEN 0 WHEN 'Engagement' THEN 1 ELSE 2 END, ItemId;";

    // Entra group principals, never Everyone. Every level gets the same ACL:
    // a time entry narrative is at least as sensitive as the engagement it
    // belongs to, so there is no argument for trimming them differently here.
    List<Acl> acl = options.Acl.GrantGroupObjectIds
        .Select(id => new Acl
        {
            Type = AclType.Group,
            Value = id.Trim(),
            AccessType = AccessType.Grant,
        })
        .ToList();

    var summary = new PushSummary();

    await using SqlConnection connection = await connections.OpenAsync(CancellationToken.None);
    await using var command = new SqlCommand(query, connection);
    command.CommandTimeout = options.DataSource.ConnectTimeoutSeconds;

    await using SqlDataReader reader = await command.ExecuteReaderAsync();

    while (await reader.ReadAsync())
    {
        string itemId = reader.GetString(reader.GetOrdinal("ItemId"));
        string itemType = reader.GetString(reader.GetOrdinal("ItemType"));

        string body = Text(reader, "Content");
        TruncationResult content = ContentTruncator.Truncate(body, options.DataSource.MaxContentBytes);

        if (content.Truncated)
        {
            summary.Truncated++;
            Log.Warning(
                "Item {ItemId} content truncated from {OriginalBytes} to {FinalBytes} bytes.",
                itemId,
                content.OriginalBytes,
                content.FinalBytes);
        }

        // Null properties are omitted rather than sent as null: a customer has
        // no consultant, and Graph rejects a null value rather than ignoring it.
        var properties = new Dictionary<string, object>
        {
            ["itemType"] = itemType,
            ["title"] = Text(reader, "Title"),
            ["url"] = Text(reader, "Url"),
            ["lastModified"] = Utc(reader, "LastModified"),
            ["hierarchyPath"] = Text(reader, "HierarchyPath"),
            ["customerName"] = Text(reader, "CustomerName"),
            ["customerCode"] = Text(reader, "CustomerCode"),
            ["accountManager"] = Text(reader, "AccountManager"),
            ["industry"] = Text(reader, "Industry"),
            ["region"] = Text(reader, "Region"),
        };

        AddIfPresent(properties, "containerName", Text(reader, "ContainerName"));
        AddIfPresent(properties, "containerUrl", Text(reader, "ContainerUrl"));
        AddIfPresent(properties, "engagementName", Text(reader, "EngagementName"));
        AddIfPresent(properties, "engagementCode", Text(reader, "EngagementCode"));
        AddIfPresent(properties, "projectManager", Text(reader, "ProjectManager"));
        AddIfPresent(properties, "practice", Text(reader, "Practice"));
        AddIfPresent(properties, "status", Text(reader, "Status"));
        AddIfPresent(properties, "consultantName", Text(reader, "ConsultantName"));
        AddIfPresent(properties, "consultantEmail", Text(reader, "ConsultantEmail"));
        AddIfPresent(properties, "workType", Text(reader, "WorkType"));

        string? workDate = NullableUtc(reader, "WorkDate");
        if (workDate is not null)
        {
            properties["workDate"] = workDate;
        }

        double? hours = Number(reader, "Hours");
        if (hours.HasValue)
        {
            properties["hours"] = hours.Value;
        }

        bool? billable = Flag(reader, "Billable");
        if (billable.HasValue)
        {
            properties["billable"] = billable.Value;
        }

        double? contractValue = Number(reader, "ContractValue");
        if (contractValue.HasValue)
        {
            properties["contractValue"] = contractValue.Value;
        }

        double? totalHours = Number(reader, "TotalHours");
        if (totalHours.HasValue)
        {
            properties["totalHours"] = totalHours.Value;
        }

        double? childCount = Number(reader, "ChildCount");
        if (childCount.HasValue)
        {
            properties["childCount"] = (long)childCount.Value;
        }

        summary.Count(itemType);

        if (dryRun)
        {
            // Item ID, level and sizes only. The content is customer data and
            // does not go to the console any more than it goes to the log.
            Log.Information(
                "Would write {ItemId} ({ItemType}): {PropertyCount} properties, {ContentBytes} content bytes, {AclCount} ACL entr(y/ies).",
                itemId,
                itemType,
                properties.Count,
                content.FinalBytes,
                acl.Count);
            continue;
        }

        var item = new ExternalItem
        {
            Id = itemId,
            Acl = acl,
            Properties = new Properties { AdditionalData = properties },
            Content = new ExternalItemContent
            {
                Type = ExternalItemContentType.Text,
                Value = content.Content,
            },
        };

        await WriteWithRetryAsync(itemId, item, summary);

        Log.Information("Indexed {ItemId} ({ItemType}, {ContentBytes} bytes).", itemId, itemType, content.FinalBytes);
    }

    return summary;
}

// ---------------------------------------------------------------------------
// A PUT with backoff. SqlGraphPush has none, which is why a large push there can
// quietly lose items to 429 without the run failing — documented in
// docs/TROUBLESHOOTING-DIRECT-PUSH.md. This source is an order of magnitude
// larger, one item per logged day per consultant, so it needs the retry.
// ---------------------------------------------------------------------------
async Task WriteWithRetryAsync(string itemId, ExternalItem item, PushSummary summary)
{
    const int MaxAttempts = 5;

    for (int attempt = 1; ; attempt++)
    {
        try
        {
            await graph!.External.Connections[options.Graph.ConnectionId].Items[itemId].PutAsync(item);
            return;
        }
        catch (ODataError ex) when (ex.ResponseStatusCode == 429 && attempt < MaxAttempts)
        {
            TimeSpan wait = RetryAfter(ex) ?? TimeSpan.FromSeconds(Math.Min(60, Math.Pow(2, attempt + 1)));
            summary.ThrottleWaits++;

            Log.Warning(
                "Throttled writing {ItemId}. Waiting {Seconds}s before attempt {Next} of {Max}.",
                itemId,
                (int)wait.TotalSeconds,
                attempt + 1,
                MaxAttempts);

            await Task.Delay(wait);
        }
    }
}

// Honours the service's own Retry-After when it sends one; guessing is worse
// than being told, and guessing low is what turns one 429 into a run of them.
static TimeSpan? RetryAfter(ODataError error)
{
    if (error.ResponseHeaders is null)
    {
        return null;
    }

    foreach (var header in error.ResponseHeaders)
    {
        if (!string.Equals(header.Key, "Retry-After", StringComparison.OrdinalIgnoreCase))
        {
            continue;
        }

        foreach (string value in header.Value)
        {
            if (int.TryParse(value, out int seconds) && seconds > 0)
            {
                return TimeSpan.FromSeconds(Math.Min(seconds, 300));
            }
        }
    }

    return null;
}

static void AddIfPresent(Dictionary<string, object> properties, string name, string value)
{
    if (!string.IsNullOrEmpty(value))
    {
        properties[name] = value;
    }
}

static string Text(SqlDataReader reader, string column)
{
    int ordinal = reader.GetOrdinal(column);
    return reader.IsDBNull(ordinal) ? string.Empty : reader.GetString(ordinal);
}

static string Utc(SqlDataReader reader, string column)
{
    int ordinal = reader.GetOrdinal(column);
    return DateTime.SpecifyKind(reader.GetDateTime(ordinal), DateTimeKind.Utc).ToString("o");
}

static string? NullableUtc(SqlDataReader reader, string column)
{
    int ordinal = reader.GetOrdinal(column);
    return reader.IsDBNull(ordinal)
        ? null
        : DateTime.SpecifyKind(reader.GetDateTime(ordinal), DateTimeKind.Utc).ToString("o");
}

static double? Number(SqlDataReader reader, string column)
{
    int ordinal = reader.GetOrdinal(column);
    return reader.IsDBNull(ordinal) ? null : Convert.ToDouble(reader.GetValue(ordinal));
}

static bool? Flag(SqlDataReader reader, string column)
{
    int ordinal = reader.GetOrdinal(column);
    return reader.IsDBNull(ordinal) ? null : reader.GetBoolean(ordinal);
}

static Property Prop(
    string name,
    PropertyType type,
    bool searchable = false,
    bool queryable = false,
    bool retrievable = false,
    bool refinable = false,
    Label? label = null)
{
    // The platform rejects searchable + refinable together. Catching it here
    // turns a schema registration failure fifteen minutes into the wait — with
    // a connection left in draft that cannot be corrected without deleting it —
    // into an exception before the first Graph call.
    if (searchable && refinable)
    {
        throw new InvalidOperationException(
            $"Property {name} is both searchable and refinable. Microsoft Graph rejects that combination.");
    }

    if (name.Length > 32 || !name.All(char.IsLetterOrDigit))
    {
        throw new InvalidOperationException(
            $"Property name {name} must be 32 alphanumeric characters or fewer.");
    }

    var property = new Property
    {
        Name = name,
        Type = type,
        IsSearchable = searchable,
        IsQueryable = queryable,
        IsRetrievable = retrievable,
        IsRefinable = refinable,
    };

    if (label is not null)
    {
        property.Labels = new List<Label?> { label };
    }

    return property;
}

/// <summary>Counts by level, so the summary line says what was actually written.</summary>
internal sealed class PushSummary
{
    public int Customers { get; private set; }

    public int Engagements { get; private set; }

    public int TimeEntries { get; private set; }

    public int Truncated { get; set; }

    public int ThrottleWaits { get; set; }

    public int Total => this.Customers + this.Engagements + this.TimeEntries;

    public void Count(string itemType)
    {
        switch (itemType)
        {
            case "Customer": this.Customers++; break;
            case "Engagement": this.Engagements++; break;
            default: this.TimeEntries++; break;
        }
    }
}
