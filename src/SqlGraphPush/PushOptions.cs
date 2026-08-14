// ---------------------------------------------------------------------------
// PushOptions.cs
// Configuration for the direct push tool. Same shape and same rules as the
// connector: nothing sensitive in the file, everything validated in one pass.
// ---------------------------------------------------------------------------

namespace SqlGraphPush;

using System.Text.Json;
using SqlTicketsConnector.Security.Configuration;

/// <summary>Root of the push tool's appsettings.json.</summary>
public sealed class PushOptions
{
    /// <summary>The file this configuration is read from.</summary>
    public const string FileName = "appsettings.json";

    /// <summary>Gets or sets the deployment environment, for example Production.</summary>
    public string Environment { get; set; } = "Production";

    /// <summary>Gets or sets the Entra credential settings. Certificate only; no client secret exists.</summary>
    public AuthOptions Auth { get; set; } = new AuthOptions();

    /// <summary>Gets or sets the Key Vault settings.</summary>
    public KeyVaultOptions KeyVault { get; set; } = new KeyVaultOptions();

    /// <summary>Gets or sets the SQL data source settings.</summary>
    public DataSourceOptions DataSource { get; set; } = new DataSourceOptions();

    /// <summary>Gets or sets the access control settings.</summary>
    public AclOptions Acl { get; set; } = new AclOptions();

    /// <summary>Gets or sets the Microsoft Graph external connection settings.</summary>
    public GraphSection Graph { get; set; } = new GraphSection();

    /// <summary>Gets the path the configuration was read from.</summary>
    public string SourcePath { get; private set; } = "(not loaded from a file)";

    /// <summary>Reads appsettings.json from beside the executable.</summary>
    public static PushOptions Load()
    {
        return Load(Path.Combine(AppContext.BaseDirectory, FileName));
    }

    /// <summary>Reads and deserializes the file.</summary>
    public static PushOptions Load(string path)
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

        PushOptions? options;

        try
        {
            options = JsonSerializer.Deserialize<PushOptions>(File.ReadAllText(path), serializerOptions);
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
public sealed class GraphSection
{
    /// <summary>Gets or sets the external connection ID: 3 to 32 alphanumeric characters.</summary>
    public string ConnectionId { get; set; } = "sqltickets";

    /// <summary>Gets or sets the display name shown in the admin centre.</summary>
    public string ConnectionName { get; set; } = "SQL Support Tickets";

    /// <summary>Gets or sets the connection description.</summary>
    public string Description { get; set; } = "Support tickets ingested from SQL Server";

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
    }
}
