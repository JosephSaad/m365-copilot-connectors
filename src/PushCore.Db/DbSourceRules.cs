// ---------------------------------------------------------------------------
// DbSourceRules.cs
// The configuration a provider-agnostic relational connector cannot start
// without.
//
// This is deliberately SHORTER than SqlSourceRules, and the difference is not an
// oversight. SqlSourceRules calls DataSource.Validate, which checks Server,
// Database and SqlAuthMode - a shape that describes SQL Server and misdescribes
// Oracle, where the same three concepts are a TNS alias or an Easy Connect
// string and a wallet. Running those checks here would reject a correct Oracle
// configuration and, worse, print a message naming a setting Oracle does not
// have.
//
// So this family checks only what it genuinely owns: the view name it
// concatenates into a query, and the vault settings for a connector that said it
// needs a secret. Everything provider-specific is the connector's own
// ValidateOptions, which runs immediately after this.
// ---------------------------------------------------------------------------

namespace PushCore.Db;

using Connector.Security.Configuration;

/// <summary>The provider-agnostic half of startup validation, run before any connector's own.</summary>
public static class DbSourceRules
{
    /// <summary>Adds a message for every family-level configuration problem at once.</summary>
    /// <param name="connector">The connector being validated, for its secret requirement.</param>
    /// <param name="options">The configuration as loaded.</param>
    /// <param name="errors">Accumulator.</param>
    public static void Validate(IDbPushConnector connector, PushOptions options, ValidationErrors errors)
    {
        ArgumentNullException.ThrowIfNull(connector);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(errors);

        // The shape of a view name is checked by the shared Source section
        // whenever one is present; that it must be present at all is this
        // family's rule, because this family concatenates it into a query.
        options.Source.RequireItemView(errors, "Source");

        string? key = connector.SecretKey;

        if (string.IsNullOrWhiteSpace(key))
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(options.KeyVault.Uri))
        {
            errors.Add("KeyVault:Uri", "is required because the current configuration resolves a secret.");
        }

        if (options.KeyVault.SecretName(key) is null)
        {
            errors.Add($"KeyVault:Secrets:{key}", "is required because this connector authenticates with a password.");
        }
    }
}
