// ---------------------------------------------------------------------------
// PushOptions.cs
// The configuration every push connector shares, and a bag for the settings it
// does not.
//
// Same rules as everywhere else in this solution: nothing sensitive in the
// file, and every problem reported in one pass rather than one per run.
//
// The Settings bag is what keeps this file still. A new connector that needs a
// value of its own puts it under Settings and reads it in ValidateOptions and
// MapRow. It does not add a property here, which would mean editing the core
// and rebuilding every other connector to carry a field none of them use.
// ---------------------------------------------------------------------------

namespace SqlPushCore;

using System.Globalization;
using System.Text.Json;
using SqlTicketsConnector.Security.Configuration;

/// <summary>Root of a push connector's appsettings file.</summary>
public sealed class PushOptions
{
    /// <summary>The file read when a connector declares no key-specific one.</summary>
    public const string DefaultFileName = "appsettings.json";

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
    public GraphSection Graph { get; set; } = new GraphSection();

    /// <summary>Gets or sets settings describing where the rows come from.</summary>
    public SourceSection Source { get; set; } = new SourceSection();

    /// <summary>
    /// Gets or sets connector-specific values, so a new connector never has to
    /// add a property to this class. Keys are matched case insensitively.
    /// </summary>
    public Dictionary<string, string> Settings { get; set; } =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

    /// <summary>Gets the path the configuration was read from.</summary>
    public string SourcePath { get; private set; } = "(not loaded from a file)";

    /// <summary>Reads and deserializes the file.</summary>
    /// <param name="path">Full path to the appsettings file.</param>
    /// <returns>The configuration, not yet validated.</returns>
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
            // A type mismatch (a quoted number, a bare number where a string is
            // expected) surfaces as JsonException too, and calling that "not
            // valid JSON" sends the operator hunting for a syntax error that is
            // not there. Path present = the shape parsed but a value did not.
            bool wrongType = ex.Path is not null &&
                ex.Message.Contains("could not be converted", StringComparison.OrdinalIgnoreCase);

            string message = wrongType
                ? $"Configuration file {path} has a value of the wrong type at {ex.Path}: {ex.Message}"
                : $"Configuration file {path} is not valid JSON: {ex.Message}";

            throw new InvalidOperationException(message, ex);
        }

        if (options is null)
        {
            throw new InvalidOperationException($"Configuration file {path} is empty.");
        }

        options.SourcePath = path;

        // Deserialization replaces the dictionary, and the replacement does not
        // inherit the comparer. Without this, Settings lookups become case
        // sensitive as soon as the file actually contains a Settings section.
        if (options.Settings is not null && !ReferenceEquals(options.Settings.Comparer, StringComparer.OrdinalIgnoreCase))
        {
            options.Settings = new Dictionary<string, string>(options.Settings, StringComparer.OrdinalIgnoreCase);
        }

        return options;
    }

    /// <summary>
    /// Resolves the configuration file for a connector: appsettings.{key}.json
    /// when it exists, appsettings.json otherwise.
    /// </summary>
    /// <param name="directory">Where to look, normally beside the executable.</param>
    /// <param name="key">The connector key.</param>
    /// <returns>The path to read.</returns>
    public static string ResolveFile(string directory, string key)
    {
        string specific = Path.Combine(directory, $"appsettings.{key}.json");

        return File.Exists(specific) ? specific : Path.Combine(directory, DefaultFileName);
    }

    /// <summary>Reads a connector-specific setting, or a fallback when it is absent.</summary>
    /// <param name="name">The key under Settings.</param>
    /// <param name="fallback">Returned when the key is absent or empty.</param>
    /// <returns>The configured value or the fallback.</returns>
    public string Setting(string name, string fallback = "")
    {
        if (this.Settings is not null &&
            this.Settings.TryGetValue(name, out string? value) &&
            !string.IsNullOrWhiteSpace(value))
        {
            return value;
        }

        return fallback;
    }

    /// <summary>Reads a connector-specific setting as an integer.</summary>
    /// <param name="name">The key under Settings.</param>
    /// <param name="fallback">Returned when the key is absent or not a number.</param>
    /// <returns>The configured value or the fallback.</returns>
    public int Setting(string name, int fallback)
    {
        return int.TryParse(this.Setting(name), NumberStyles.Integer, CultureInfo.InvariantCulture, out int value)
            ? value
            : fallback;
    }

    /// <summary>Reads a connector-specific setting as a flag.</summary>
    /// <param name="name">The key under Settings.</param>
    /// <param name="fallback">Returned when the key is absent or not a boolean.</param>
    /// <returns>The configured value or the fallback.</returns>
    public bool Setting(string name, bool fallback)
    {
        return bool.TryParse(this.Setting(name), out bool value) ? value : fallback;
    }

    /// <summary>Validates every shared section and returns all problems at once.</summary>
    /// <returns>The accumulated errors, empty when the configuration is usable.</returns>
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

/// <summary>The "Graph" section: which external connection this connector owns.</summary>
public sealed class GraphSection
{
    /// <summary>Gets or sets the external connection ID: 3 to 32 alphanumeric characters.</summary>
    public string ConnectionId { get; set; } = string.Empty;

    /// <summary>Gets or sets the display name shown in the admin centre.</summary>
    public string ConnectionName { get; set; } = string.Empty;

    /// <summary>Gets or sets the connection description.</summary>
    public string Description { get; set; } = string.Empty;

    /// <summary>Gets or sets how long to wait for server side schema registration.</summary>
    public int SchemaReadyTimeoutMinutes { get; set; } = 30;

    /// <summary>Validates the section.</summary>
    /// <param name="errors">Accumulator.</param>
    /// <param name="path">Configuration path prefix, normally "Graph".</param>
    public void Validate(ValidationErrors errors, string path)
    {
        errors.RequireNonEmpty($"{path}:ConnectionId", this.ConnectionId);
        errors.RequireNonEmpty($"{path}:ConnectionName", this.ConnectionName);
        errors.RequireRange($"{path}:SchemaReadyTimeoutMinutes", this.SchemaReadyTimeoutMinutes, 1, 120);

        if (string.IsNullOrWhiteSpace(this.ConnectionId))
        {
            return;
        }

        if (this.ConnectionId.Length is < 3 or > 32 || !this.ConnectionId.All(char.IsAsciiLetterOrDigit))
        {
            // ASCII deliberately: char.IsLetterOrDigit admits non-ASCII letters
            // that Graph rejects at creation time, and the pre-flight script
            // checks the same field with an ASCII regex.
            errors.Add($"{path}:ConnectionId", "must be 3 to 32 ASCII letters or digits.");
        }

        if (this.ConnectionId.StartsWith("Microsoft", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(this.ConnectionId, "None", StringComparison.OrdinalIgnoreCase))
        {
            errors.Add($"{path}:ConnectionId", "cannot start with 'Microsoft' and cannot be 'None'.");
        }
    }
}

/// <summary>The "Source" section: where the rows are read from.</summary>
public sealed class SourceSection
{
    /// <summary>
    /// Gets or sets the table or view that returns one row per external item.
    /// Schema qualified.
    /// </summary>
    public string ItemView { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets a cap on rows read, for a quick smoke test against a large
    /// source. Zero means no cap, which is the normal setting: a partial push
    /// leaves an index that disagrees with the source, and the tool says so.
    /// </summary>
    public int MaxItems { get; set; }

    /// <summary>Validates the section.</summary>
    /// <param name="errors">Accumulator.</param>
    /// <param name="path">Configuration path prefix, normally "Source".</param>
    public void Validate(ValidationErrors errors, string path)
    {
        errors.RequireNonEmpty($"{path}:ItemView", this.ItemView);
        errors.RequireRange($"{path}:MaxItems", this.MaxItems, 0, 1000000);

        if (string.IsNullOrWhiteSpace(this.ItemView))
        {
            return;
        }

        // The name is concatenated into a query, so it cannot be a parameter.
        // Restricting it to an identifier shape is what makes that safe: a value
        // that is not [schema.]name is rejected before use.
        foreach (string part in this.ItemView.Split('.'))
        {
            if (part.Length == 0 ||
                !(char.IsLetter(part[0]) || part[0] == '_') ||
                !part.All(c => char.IsLetterOrDigit(c) || c == '_'))
            {
                // The first character rule matters: SQL Server rejects an
                // identifier starting with a digit, and catching that here is
                // exit 2 with a named key instead of a SQL syntax error at exit 4.
                errors.Add(
                    $"{path}:ItemView",
                    "must be a plain [schema.]name identifier: letters, digits and underscores, " +
                    "not starting with a digit.");
                return;
            }
        }

        if (this.ItemView.Split('.').Length > 2)
        {
            errors.Add($"{path}:ItemView", "must be at most schema qualified, for example dbo.vwExternalItems.");
        }
    }
}
