// ---------------------------------------------------------------------------
// SqlSourceRules.cs
// The configuration a SQL connector cannot start without.
//
// These checks used to live in PushOptions.Validate, where they ran for every
// connector whether or not it opened a database. They are here now because they
// are properties of one source family: a connector reading a filesystem has no
// Server, no Database and no SqlAuthMode, and requiring them of it would mean
// inventing values that nothing reads - which is how a configuration file stops
// describing the deployment.
//
// The messages are unchanged, deliberately. They are quoted in the runbook and
// pinned by tests, and an operator searching a log for one of them after this
// refactor should still find it.
// ---------------------------------------------------------------------------

namespace PushCore.Sql;

using Connector.Security.Configuration;

/// <summary>The SQL half of startup validation, run before any connector's own.</summary>
public static class SqlSourceRules
{
    /// <summary>Adds a message for every SQL-side configuration problem at once.</summary>
    /// <param name="options">The configuration as loaded.</param>
    /// <param name="errors">Accumulator.</param>
    public static void Validate(PushOptions options, ValidationErrors errors)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(errors);

        options.DataSource.Validate(errors, "DataSource", options.Environment);

        // The shape of a view name is checked by the shared Source section
        // whenever one is present; that it must be present at all is this
        // family's rule, because this family concatenates it into a query.
        options.Source.RequireItemView(errors, "Source");

        if (!options.DataSource.RequiresVaultSecret)
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(options.KeyVault.Uri))
        {
            errors.Add("KeyVault:Uri", "is required because the current configuration resolves a secret.");
        }

        if (options.KeyVault.SecretName(KeyVaultOptions.SqlPasswordKey) is null)
        {
            errors.Add("KeyVault:Secrets:SqlPassword", "is required because DataSource:SqlAuthMode is SqlLogin.");
        }
    }
}
