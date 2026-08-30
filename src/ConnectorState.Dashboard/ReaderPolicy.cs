// ---------------------------------------------------------------------------
// ReaderPolicy.cs
// Who may read crawl state, expressed once so it can be tested.
//
// This lived inline in Program.cs, where it could not be. Program.cs is
// top-level statements in a Web SDK project, and the rule it held is the kind
// that is only ever wrong in one direction: a policy that lets too many people
// in behaves identically to a correct one until somebody who should have been
// refused is not. There is no error, no log line, and nothing to notice.
//
// The half that matters is therefore the NEGATIVE case - a principal outside
// every configured group being denied - and that is the half a running site
// cannot easily show you, because demonstrating it needs a second person.
// Pulling the rule out here makes it a function that can be asked directly.
// ---------------------------------------------------------------------------

namespace ConnectorState.Dashboard;

using Microsoft.AspNetCore.Authorization;

/// <summary>Builds the authorization policy every page falls back to.</summary>
public static class ReaderPolicy
{
    /// <summary>The configuration path holding the permitted group names.</summary>
    public const string ConfigurationPath = "CrawlState:ReaderGroups";

    /// <summary>
    /// Builds the fallback policy: authenticated always, and in one of the
    /// named groups when any are configured.
    /// </summary>
    /// <param name="readerGroups">
    /// Group names from configuration. Empty or null means authentication alone,
    /// which is what this site did before the setting existed.
    /// </param>
    /// <returns>The policy to install as the fallback.</returns>
    /// <remarks>
    /// A FALLBACK policy rather than an attribute per page. An attribute is
    /// something a new page can forget; a fallback applies to every endpoint
    /// that does not opt out, so adding a page cannot accidentally add an
    /// anonymous one.
    ///
    /// RequireRole against a Windows identity tests group membership: the
    /// negotiate handshake puts one role claim on the principal per group in the
    /// user's token, so a group name is matched the same way a role is. Nested
    /// groups therefore work, and only because the token already flattened them
    /// - this is not walking the directory, and a group the token did not carry
    /// is a group this check cannot see. That is the documented reason a user
    /// added to a group has to sign in again.
    ///
    /// BLANK ENTRIES ARE DROPPED, and that is not tidying. A JSON array with a
    /// stray empty string - the shape a half-finished edit leaves behind -
    /// would otherwise reach RequireRole, which treats an empty role name as a
    /// requirement no principal can satisfy. The site would refuse everyone,
    /// including the administrator trying to work out why, and the configuration
    /// would look correct.
    /// </remarks>
    public static AuthorizationPolicy Build(IReadOnlyList<string>? readerGroups)
    {
        var builder = new AuthorizationPolicyBuilder().RequireAuthenticatedUser();

        string[] groups = (readerGroups ?? Array.Empty<string>())
            .Where(g => !string.IsNullOrWhiteSpace(g))
            .Select(g => g.Trim())
            .ToArray();

        if (groups.Length > 0)
        {
            builder = builder.RequireRole(groups);
        }

        return builder.Build();
    }
}
