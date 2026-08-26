// ---------------------------------------------------------------------------
// PrincipalResolver.cs
// A cluster group name in, an Entra group object ID out - or nothing.
//
// "Or nothing" is the whole design. A group this cannot resolve produces no
// grant, which narrows who can see the item. The alternative - carrying on with
// a guess, or falling back to a configured group - would widen the audience of
// exactly the item whose permissions could not be established, which is the one
// item where widening is least defensible.
//
// Two ways to resolve, in order:
//
//   The explicit map. Settings:EntraGroupMap pairs a cluster group name with an
//   Entra group object ID. It needs no Graph permission, it is reviewable in a
//   configuration file, and it is what a regulated deployment should prefer:
//   somebody wrote down that "hadoop-analysts" means this Entra group, and a
//   change to that statement is a change to a file under review.
//
//   The directory lookup, off unless asked for. Where the cluster's Kerberos is
//   AD-integrated, the group names ARE AD group names, and Entra carries them
//   with onPremisesSamAccountName set. That needs an application permission the
//   rest of this connector does not use - GroupMember.Read.All - so it is
//   opt-in rather than a silent new requirement on the app registration.
//
// Results are cached for the run. A crawl of a million files under a hundred
// directories asks about the same dozen groups a million times.
// ---------------------------------------------------------------------------

namespace CdpConnector.Source.Acl;

using Microsoft.Graph;
using PushCore;
using Serilog;

/// <summary>Turns cluster group names into grants.</summary>
public sealed class PrincipalResolver
{
    private readonly Dictionary<string, string> explicitMap;
    private readonly GraphServiceClient? graph;
    private readonly ILogger log;
    private readonly Dictionary<string, string?> cache = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> reportedMisses = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Initializes a new instance of the <see cref="PrincipalResolver"/> class.</summary>
    /// <param name="explicitMap">Cluster group name to Entra group object ID.</param>
    /// <param name="graph">A Graph client for directory lookups, or null when they are off.</param>
    /// <param name="log">Where to report unresolved groups.</param>
    public PrincipalResolver(
        IReadOnlyDictionary<string, string> explicitMap, GraphServiceClient? graph, ILogger log)
    {
        this.explicitMap = new Dictionary<string, string>(explicitMap, StringComparer.OrdinalIgnoreCase);
        this.graph = graph;
        this.log = log;
    }

    /// <summary>Gets the cluster groups that could not be resolved this run.</summary>
    public IReadOnlyCollection<string> Unresolved => this.reportedMisses;

    /// <summary>Parses the Settings:EntraGroupMap value.</summary>
    /// <param name="value">Semicolon-separated name=objectId pairs.</param>
    /// <returns>The map.</returns>
    public static Dictionary<string, string> ParseMap(string value)
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        if (string.IsNullOrWhiteSpace(value))
        {
            return map;
        }

        foreach (string pair in value.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            int equals = pair.IndexOf('=', StringComparison.Ordinal);

            if (equals <= 0 || equals == pair.Length - 1)
            {
                continue;
            }

            map[pair[..equals].Trim()] = pair[(equals + 1)..].Trim();
        }

        return map;
    }

    /// <summary>Resolves a set of cluster group names to grants, dropping the ones it cannot.</summary>
    /// <param name="groupNames">The cluster group names.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>One grant per resolved group, deduplicated.</returns>
    public async Task<List<PushAclEntry>> ResolveAsync(
        IEnumerable<string> groupNames, CancellationToken cancellationToken)
    {
        var grants = new List<PushAclEntry>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (string name in groupNames)
        {
            if (string.IsNullOrWhiteSpace(name) || !seen.Add(name))
            {
                continue;
            }

            string? objectId = await this.ResolveOneAsync(name, cancellationToken);

            if (objectId is null)
            {
                continue;
            }

            grants.Add(new PushAclEntry(PushAclType.Group, objectId));
        }

        return grants;
    }

    private async Task<string?> ResolveOneAsync(string name, CancellationToken cancellationToken)
    {
        if (this.cache.TryGetValue(name, out string? cached))
        {
            return cached;
        }

        string? resolved = null;

        if (this.explicitMap.TryGetValue(name, out string? mapped) && Guid.TryParse(mapped, out Guid parsed))
        {
            resolved = parsed.ToString("D");
        }
        else if (this.graph is not null)
        {
            resolved = await this.LookUpAsync(name, cancellationToken);
        }

        this.cache[name] = resolved;

        if (resolved is null && this.reportedMisses.Add(name))
        {
            // Once per group per run, not once per file. At a million files this
            // is the difference between a line and a log nobody can read.
            this.log.Warning(
                "Cluster group {GroupName} does not resolve to an Entra group, so it grants nothing. " +
                "Items readable only by it will be skipped. Add it to Settings:EntraGroupMap, or enable " +
                "Settings:ResolveGroupsFromDirectory if its name matches an AD group synchronised to Entra.",
                name);
        }

        return resolved;
    }

    private async Task<string?> LookUpAsync(string name, CancellationToken cancellationToken)
    {
        try
        {
            var page = await this.graph!.Groups.GetAsync(
                request =>
                {
                    // The on-premises name is the one Hadoop knows. Matching on
                    // displayName instead would match a different group that
                    // merely reads the same, which is the kind of near-miss that
                    // grants the wrong people access.
                    request.QueryParameters.Filter = $"onPremisesSamAccountName eq '{Escape(name)}'";
                    request.QueryParameters.Select = ["id", "displayName"];
                    request.QueryParameters.Top = 2;
                },
                cancellationToken);

            List<Microsoft.Graph.Models.Group> matches = page?.Value ?? [];

            if (matches.Count == 1)
            {
                return matches[0].Id;
            }

            if (matches.Count > 1)
            {
                // Two groups claiming the same on-premises name is a directory
                // problem, and picking one would be picking an audience.
                this.log.Warning(
                    "Cluster group {GroupName} matches more than one Entra group by onPremisesSamAccountName. " +
                    "It grants nothing until Settings:EntraGroupMap says which one is meant.",
                    name);
            }

            return null;
        }
        catch (Microsoft.Graph.Models.ODataErrors.ODataError ex) when (ex.ResponseStatusCode is 401 or 403)
        {
            throw new PushSourceAuthenticationException(
                "Graph refused a group lookup. Settings:ResolveGroupsFromDirectory needs the " +
                "GroupMember.Read.All application permission, which the rest of this connector does not use - " +
                "grant it deliberately, or map the groups in Settings:EntraGroupMap instead.",
                ex);
        }
    }

    /// <summary>Escapes a value for an OData string literal.</summary>
    /// <param name="value">The value.</param>
    /// <returns>The escaped value.</returns>
    private static string Escape(string value)
    {
        return value.Replace("'", "''", StringComparison.Ordinal);
    }
}
