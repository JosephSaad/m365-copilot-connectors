// ---------------------------------------------------------------------------
// HierarchyOptions.cs
// Configuration for the three level push tool. Same shape and same rules as the
// connector and SqlGraphPush: nothing sensitive in the file, and every problem
// reported in one pass rather than one per run.
// ---------------------------------------------------------------------------

namespace SqlHierarchyPush;

using System.Text.Json;
using SqlTicketsConnector.Security.Configuration;

/// <summary>Root of the hierarchy push tool's appsettings.json.</summary>
public sealed class HierarchyOptions
{
    /// <summary>The file this configuration is read from.</summary>
    public const string FileName = "appsettings.json";

    /// <summary>Gets or sets the deployment environment, for example Production.</summary>
    public string Environment { get; set; } = "Production";

    /// <summary>Gets or sets the Entra credential settings. Certificate, or a Credential Manager client secret.</summary>
    public AuthOptions Auth { get; set; } = new AuthOptions();

    /// <summary>Gets or sets the Key Vault settings.</summary>
    public KeyVaultOptions KeyVault { get; set; } = new KeyVaultOptions();

    /// <summary>Gets or sets the SQL data source settings.</summary>
    public DataSourceOptions DataSource { get; set; } = new DataSourceOptions();

    /// <summary>Gets or sets the access control settings.</summary>
    public AclOptions Acl { get; set; } = new AclOptions();

    /// <summary>Gets or sets the Microsoft Graph external connection settings.</summary>
    public HierarchyGraphSection Graph { get; set; } = new HierarchyGraphSection();

    /// <summary>Gets or sets settings describing where the flattened items come from.</summary>
    public SourceSection Source { get; set; } = new SourceSection();

    /// <summary>Gets the path the configuration was read from.</summary>
    public string SourcePath { get; private set; } = "(not loaded from a file)";

    /// <summary>Reads appsettings.json from beside the executable.</summary>
    public static HierarchyOptions Load()
    {
        return Load(Path.Combine(AppContext.BaseDirectory, FileName));
    }

    /// <summary>Reads and deserializes the file.</summary>
    public static HierarchyOptions Load(string path)
    {
        if (!File.Exists(path))
        {
            throw new InvalidOperationException(
                $"Configuration file not found at {path}. The push tool cannot run without it.");
        }

        var serializerOptions = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
            ReadCommentHandling = JsonCommentHandling.Skip,
            AllowTrailingCommas = true,
        };

        HierarchyOptions? options;

        try
        {
            options = JsonSerializer.Deserialize<HierarchyOptions>(File.ReadAllText(path), serializerOptions);
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException($"Configuration file {path} is not valid JSON: {ex.Message}", ex);
        }

        if (options is null)
        {
            throw new InvalidOperationException($"Configuration file {path} is empty.");
        }

        options.SourcePath = path;
        return options;
    }

    /// <summary>Validates every section and returns all problems at once.</summary>
    public ValidationErrors Validate()
    {
        var errors = new ValidationErrors();

        errors.RequireOneOf("Environment", this.Environment, "Production", "Staging", "Development");

        this.Auth.Validate(errors, "Auth");
        this.DataSource.Validate(errors, "DataSource", this.Environment);
        this.Acl.Validate(errors, "Acl");
        this.Graph.Validate(errors, "Graph");
        this.Source.Validate(errors, "Source");
        this.KeyVault.Validate(errors, "KeyVault", this.DataSource.RequiresVaultSecret);

        if (this.DataSource.RequiresVaultSecret &&
            this.KeyVault.SecretName(KeyVaultOptions.SqlPasswordKey) is null)
        {
            errors.Add("KeyVault:Secrets:SqlPassword", "is required because DataSource:SqlAuthMode is SqlLogin.");
        }

        return errors;
    }
}

/// <summary>The "Graph" section.</summary>
public sealed class HierarchyGraphSection
{
    /// <summary>
    /// Gets or sets the external connection ID: 3 to 32 alphanumeric characters.
    ///
    /// It must differ from the ticket test case's connection. The two are owned
    /// by whichever app created them, they register different schemas, and a
    /// schema cannot be changed once registered — so sharing an ID means one
    /// tool silently cannot manage the connection the other made.
    /// </summary>
    public string ConnectionId { get; set; } = "consultingwork";

    /// <summary>Gets or sets the display name shown in the admin centre.</summary>
    public string ConnectionName { get; set; } = "Consulting work";

    /// <summary>Gets or sets the connection description.</summary>
    public string Description { get; set; } = "Customers, engagements and logged time";

    /// <summary>Gets or sets how long to wait for server side schema registration.</summary>
    public int SchemaReadyTimeoutMinutes { get; set; } = 30;

    /// <summary>Validates the section.</summary>
    public void Validate(ValidationErrors errors, string path)
    {
        errors.RequireNonEmpty($"{path}:ConnectionId", this.ConnectionId);
        errors.RequireNonEmpty($"{path}:ConnectionName", this.ConnectionName);
        errors.RequireRange($"{path}:SchemaReadyTimeoutMinutes", this.SchemaReadyTimeoutMinutes, 1, 120);

        if (string.IsNullOrWhiteSpace(this.ConnectionId))
        {
            return;
        }

        if (this.ConnectionId.Length is < 3 or > 32 || !this.ConnectionId.All(char.IsLetterOrDigit))
        {
            errors.Add($"{path}:ConnectionId", "must be 3 to 32 alphanumeric characters.");
        }

        if (this.ConnectionId.StartsWith("Microsoft", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(this.ConnectionId, "None", StringComparison.OrdinalIgnoreCase))
        {
            errors.Add($"{path}:ConnectionId", "cannot start with 'Microsoft' and cannot be 'None'.");
        }

        if (string.Equals(this.ConnectionId, "sqltickets", StringComparison.OrdinalIgnoreCase))
        {
            errors.Add(
                $"{path}:ConnectionId",
                "is the ticket test case's connection. The two test cases register different schemas and a " +
                "registered schema cannot be changed, so they must not share a connection ID.");
        }
    }
}

/// <summary>The "Source" section: where the flattened items are read from.</summary>
public sealed class SourceSection
{
    /// <summary>
    /// Gets or sets the view that returns one row per external item, already
    /// flattened. Schema qualified. The default is created by
    /// sql/12-timesheet-views.sql.
    /// </summary>
    public string ItemView { get; set; } = "dbo.vwExternalItems";

    /// <summary>
    /// Gets or sets a cap on rows read, for a quick smoke test against a large
    /// source. Zero means no cap, which is the normal setting: a partial push
    /// leaves an index that disagrees with the source, and the tool says so.
    /// </summary>
    public int MaxItems { get; set; }

    /// <summary>Validates the section.</summary>
    public void Validate(ValidationErrors errors, string path)
    {
        errors.RequireNonEmpty($"{path}:ItemView", this.ItemView);
        errors.RequireRange($"{path}:MaxItems", this.MaxItems, 0, 1000000);

        if (string.IsNullOrWhiteSpace(this.ItemView))
        {
            return;
        }

        // The view name is concatenated into a query, so it cannot be a
        // parameter. Restricting it to an identifier shape is what makes that
        // safe: a value that is not [schema.]name is rejected before use.
        foreach (string part in this.ItemView.Split('.'))
        {
            if (part.Length == 0 || !part.All(c => char.IsLetterOrDigit(c) || c == '_'))
            {
                errors.Add(
                    $"{path}:ItemView",
                    "must be a plain [schema.]name identifier, letters, digits and underscores only.");
                return;
            }
        }

        if (this.ItemView.Split('.').Length > 2)
        {
            errors.Add($"{path}:ItemView", "must be at most schema qualified, for example dbo.vwExternalItems.");
        }
    }
}
