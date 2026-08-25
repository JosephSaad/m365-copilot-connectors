// ---------------------------------------------------------------------------
// PushConnectorRegistry.cs
// Finding the connectors compiled into an executable.
//
// Reflection over one assembly, deliberately - not a scan of a directory for
// DLLs to load. A plugin folder would mean the set of things this tool can do
// is decided by whatever is sitting next to it on a server, which is not a
// property a reviewer should have to accept. Here, adding a connector means
// adding source and rebuilding, and the package assertion in CI can see it.
// ---------------------------------------------------------------------------

namespace SqlPushCore;

using System.Reflection;

/// <summary>The connectors an executable hosts.</summary>
public static class PushConnectorRegistry
{
    /// <summary>Instantiates every public IPushConnector in an assembly.</summary>
    /// <param name="assembly">Normally the entry assembly.</param>
    /// <returns>The connectors, ordered by key.</returns>
    public static IReadOnlyList<IPushConnector> Discover(Assembly assembly)
    {
        ArgumentNullException.ThrowIfNull(assembly);

        List<IPushConnector> found = assembly.GetTypes()
            .Where(t => typeof(IPushConnector).IsAssignableFrom(t) && !t.IsAbstract && !t.IsInterface)
            .Where(t => t.GetConstructor(Type.EmptyTypes) is not null)
            .Select(t => (IPushConnector)Activator.CreateInstance(t)!)
            .OrderBy(c => c.Key, StringComparer.OrdinalIgnoreCase)
            .ToList();

        List<string> duplicates = found
            .GroupBy(c => c.Key, StringComparer.OrdinalIgnoreCase)
            .Where(g => g.Count() > 1)
            .Select(g => g.Key)
            .ToList();

        if (duplicates.Count > 0)
        {
            // Two connectors sharing a key would share a configuration file and
            // race for the same connection. Caught at startup rather than by
            // whichever one the reflection order happened to return first.
            throw new InvalidOperationException(
                "Two connectors in " + assembly.GetName().Name + " share the key(s) " +
                string.Join(", ", duplicates) + ". Keys select the configuration file and must be unique.");
        }

        return found;
    }

    /// <summary>
    /// Picks the connector to run: the named one, or the only one when a single
    /// connector is hosted and no name was given.
    /// </summary>
    /// <param name="connectors">What the assembly offers.</param>
    /// <param name="key">The requested key, or null.</param>
    /// <param name="problem">Set when no single connector could be chosen.</param>
    /// <returns>The connector, or null when <paramref name="problem"/> is set.</returns>
    public static IPushConnector? Select(
        IReadOnlyList<IPushConnector> connectors, string? key, out string problem)
    {
        problem = string.Empty;

        if (connectors.Count == 0)
        {
            problem = "This executable hosts no push connector. Add a class implementing IPushConnector.";
            return null;
        }

        if (string.IsNullOrWhiteSpace(key))
        {
            if (connectors.Count == 1)
            {
                return connectors[0];
            }

            problem =
                "This executable hosts more than one connector, so --connector is required. Available: " +
                string.Join(", ", connectors.Select(c => c.Key)) + ".";
            return null;
        }

        IPushConnector? match = connectors
            .FirstOrDefault(c => string.Equals(c.Key, key, StringComparison.OrdinalIgnoreCase));

        if (match is null)
        {
            problem =
                "No connector named '" + key + "'. Available: " +
                string.Join(", ", connectors.Select(c => c.Key)) + ".";
        }

        return match;
    }
}
