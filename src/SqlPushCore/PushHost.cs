// ---------------------------------------------------------------------------
// PushHost.cs
// The whole of a push tool's Main.
//
// A push executable is one line: return await PushHost.RunAsync(args). The
// connector classes compiled into it are discovered, one is selected, its
// configuration file is read, and the engine runs it.
//
// Exit codes are part of the interface and are the same for every connector:
//   0  success
//   2  configuration invalid, or no connector could be selected
//   3  credential could not be built, or was rejected
//   4  ingestion failed partway
//
// The log file is named after the executable, not the connector, so an existing
// deployment's log path does not move when a second connector is added to it.
// ---------------------------------------------------------------------------

namespace SqlPushCore;

using System.Reflection;
using Azure.Core;
using Microsoft.Graph;
using Serilog;
using Serilog.Events;
using SqlTicketsConnector.Security.Certificates;
using SqlTicketsConnector.Security.Configuration;
using SqlTicketsConnector.Security.Credentials;
using SqlTicketsConnector.Security.Logging;
using SqlTicketsConnector.Security.Secrets;
using SqlTicketsConnector.Security.Sql;

/// <summary>Startup for every direct push executable.</summary>
public static class PushHost
{
    private const string GraphScope = "https://graph.microsoft.com/.default";

    /// <summary>Runs whichever connector the arguments select.</summary>
    /// <param name="args">Command line: --connector, --dry-run, --help.</param>
    /// <returns>The process exit code.</returns>
    public static Task<int> RunAsync(string[] args)
    {
        return RunAsync(PushConnectorRegistry.Discover(Assembly.GetEntryAssembly()!), args);
    }

    /// <summary>Runs whichever of the supplied connectors the arguments select.</summary>
    /// <param name="connectors">The connectors this executable hosts.</param>
    /// <param name="args">Command line: --connector, --dry-run, --help.</param>
    /// <returns>The process exit code.</returns>
    public static async Task<int> RunAsync(IReadOnlyList<IPushConnector> connectors, string[] args)
    {
        bool dryRun = HasFlag(args, "--dry-run");
        string? key = ValueOf(args, "--connector");

        if (HasFlag(args, "--help") || HasFlag(args, "-h"))
        {
            WriteHelp(connectors);
            return 0;
        }

        IPushConnector? connector = PushConnectorRegistry.Select(connectors, key, out string problem);

        if (connector is null)
        {
            Console.Error.WriteLine($"FATAL: {problem}");
            return 2;
        }

        PushOptions options;
        string configurationPath = PushOptions.ResolveFile(AppContext.BaseDirectory, connector.Key);

        try
        {
            options = PushOptions.Load(configurationPath);
        }
        catch (Exception ex)
        {
            // Before the logger exists, so this goes to stderr.
            Console.Error.WriteLine($"FATAL: {ex.Message}");
            return 2;
        }

        string executable = Assembly.GetEntryAssembly()?.GetName().Name ?? "SqlPush";

        using var logger = new LoggerConfiguration()
            .MinimumLevel.Information()
            .Enrich.With(new ScrubbingEnricher())
            .WriteTo.Console(outputTemplate: "{Timestamp:HH:mm:ss} [{Level:u3}] {Message:lj}{NewLine}{Exception}")
            .WriteTo.File(
                Path.Combine(AppContext.BaseDirectory, "Logs", executable + ".log"),
                fileSizeLimitBytes: 10L * 1024 * 1024,
                rollOnFileSizeLimit: true,
                retainedFileCountLimit: 30,
                restrictedToMinimumLevel: LogEventLevel.Information)
            .CreateLogger();

        Log.Logger = logger;

        ApplyDefaults(options, connector);

        ValidationErrors errors = options.Validate();
        connector.ValidateOptions(options, errors);
        RejectNeighboursConnection(options, connector, connectors, errors);

        if (errors.HasErrors)
        {
            Log.Fatal(
                "Configuration in {ConfigurationPath} is invalid. {ErrorCount} problem(s):{NewLine}{Errors}",
                options.SourcePath,
                errors.Errors.Count,
                Environment.NewLine,
                errors.ToMessage());

            Log.CloseAndFlush();
            return 2;
        }

        Log.Information(
            "{Executable} starting connector {Key} ({DisplayName}) against connection {ConnectionId}, " +
            "configuration {ConfigurationPath}.",
            executable,
            connector.Key,
            connector.DisplayName,
            options.Graph.ConnectionId,
            options.SourcePath);

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
            Log.CloseAndFlush();
            return 3;
        }

        try
        {
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

            // The same credential authenticates to Graph. Scope is unchanged.
            var graph = new GraphServiceClient(credential, new[] { GraphScope });

            var engine = new PushEngine(connector, options, graph, connections, Log.Logger, dryRun);

            PushSummary summary = await engine.RunAsync();

            Log.Information(
                "{Verb} complete. {Total} item(s) ({Breakdown}) for connection {ConnectionId}. " +
                "truncated={Truncated} skipped={Skipped} throttleWaits={ThrottleWaits}",
                dryRun ? "Dry run" : "Ingestion",
                summary.Total,
                summary.Describe(),
                options.Graph.ConnectionId,
                summary.Truncated,
                summary.Skipped,
                summary.ThrottleWaits);

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
    }

    /// <summary>
    /// Fills anything the configuration file left out from the connector's own
    /// declarations, so adding a section to the core does not invalidate every
    /// appsettings.json already deployed.
    /// </summary>
    /// <param name="options">The configuration as loaded.</param>
    /// <param name="connector">The connector about to run.</param>
    public static void ApplyDefaults(PushOptions options, IPushConnector connector)
    {
        if (string.IsNullOrWhiteSpace(options.Graph.ConnectionId))
        {
            options.Graph.ConnectionId = connector.DefaultConnectionId;
        }

        if (string.IsNullOrWhiteSpace(options.Graph.ConnectionName))
        {
            options.Graph.ConnectionName = connector.DefaultConnectionName;
        }

        if (string.IsNullOrWhiteSpace(options.Graph.Description))
        {
            options.Graph.Description = connector.DefaultDescription;
        }

        if (string.IsNullOrWhiteSpace(options.Source.ItemView))
        {
            options.Source.ItemView = connector.DefaultItemView;
        }
    }

    /// <summary>
    /// Refuses a connection ID that belongs to another connector in the same
    /// executable.
    ///
    /// Two connectors on one connection means one of them cannot register its
    /// schema - a registered schema is fixed - and whichever created the
    /// connection is the only one that can manage it. The check is generic, so a
    /// connector added later is covered without anything being told about it.
    /// </summary>
    /// <param name="options">The configuration as loaded.</param>
    /// <param name="connector">The connector about to run.</param>
    /// <param name="connectors">Every connector this executable hosts.</param>
    /// <param name="errors">Accumulator.</param>
    public static void RejectNeighboursConnection(
        PushOptions options,
        IPushConnector connector,
        IReadOnlyList<IPushConnector> connectors,
        ValidationErrors errors)
    {
        foreach (IPushConnector other in connectors)
        {
            if (string.Equals(other.Key, connector.Key, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (string.Equals(options.Graph.ConnectionId, other.DefaultConnectionId, StringComparison.OrdinalIgnoreCase))
            {
                errors.Add(
                    "Graph:ConnectionId",
                    "is the connection belonging to the '" + other.Key + "' connector. Two connectors cannot " +
                    "share one connection: they register different schemas, and a registered schema cannot be " +
                    "changed.");
            }
        }
    }

    private static bool HasFlag(string[] args, string name)
    {
        return args.Any(a => string.Equals(a, name, StringComparison.OrdinalIgnoreCase));
    }

    private static string? ValueOf(string[] args, string name)
    {
        for (int i = 0; i < args.Length; i++)
        {
            if (string.Equals(args[i], name, StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
            {
                return args[i + 1];
            }

            if (args[i].StartsWith(name + "=", StringComparison.OrdinalIgnoreCase))
            {
                return args[i][(name.Length + 1)..];
            }
        }

        return null;
    }

    private static void WriteHelp(IReadOnlyList<IPushConnector> connectors)
    {
        Console.WriteLine("Options:");
        Console.WriteLine("  --connector <key>  Which connector to run. Optional when only one is hosted.");
        Console.WriteLine("  --dry-run          Read and map, report what would be written, write nothing.");
        Console.WriteLine("  --help             This.");
        Console.WriteLine();
        Console.WriteLine("Connectors in this executable:");

        foreach (IPushConnector connector in connectors)
        {
            Console.WriteLine($"  {connector.Key,-20} {connector.DisplayName}");
            Console.WriteLine($"  {string.Empty,-20} configuration: appsettings.{connector.Key}.json, " +
                              "or appsettings.json when that does not exist");
        }
    }
}
