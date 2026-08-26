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

namespace PushCore;

using System.Reflection;
using Azure.Core;
using Azure.Identity;
using Microsoft.Graph;
using Microsoft.Graph.Models.ODataErrors;
using Serilog;
using Serilog.Events;
using Connector.Security.Certificates;
using Connector.Security.Configuration;
using Connector.Security.Credentials;
using Connector.Security.Logging;
using Connector.Security.Secrets;

/// <summary>Startup for every direct push executable.</summary>
public static class PushHost
{
    private const string GraphScope = "https://graph.microsoft.com/.default";

    /// <summary>Runs whichever connector the arguments select.</summary>
    /// <param name="args">Command line: --connector, --dry-run, --help.</param>
    /// <returns>The process exit code.</returns>
    public static async Task<int> RunAsync(string[] args)
    {
        IReadOnlyList<IPushConnector> connectors;

        try
        {
            connectors = PushConnectorRegistry.Discover(Assembly.GetEntryAssembly()!);
        }
        catch (Exception ex)
        {
            // Discovery loads every type in the executable, so a stale or
            // mismatched DLL beside it throws here - before any logger exists.
            // Without this guard that is a bare CLR crash dump and an exit code
            // outside the documented contract; a broken deployment is a
            // configuration fault, so it reports as one.
            Console.Error.WriteLine($"FATAL: {Flatten(ex)}");
            return 2;
        }

        return await RunAsync(connectors, args);
    }

    private static string Flatten(Exception ex)
    {
        // ReflectionTypeLoadException buries the useful message in
        // LoaderExceptions; everything else is fine as-is.
        if (ex is ReflectionTypeLoadException loadEx && loadEx.LoaderExceptions is { Length: > 0 })
        {
            return ex.Message + " " + string.Join(
                " | ",
                loadEx.LoaderExceptions.Where(e => e is not null).Select(e => e!.Message).Distinct());
        }

        return ex.ToString();
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

        string executable = Assembly.GetEntryAssembly()?.GetName().Name ?? "Push";

        // The file sink opens its file lazily and swallows open failures into
        // Serilog's SelfLog, which is off by default - so an unwritable Logs
        // directory would silently produce no log file at all. Route SelfLog to
        // stderr, and probe the directory up front so the operator is told at
        // startup rather than discovering an empty directory during an incident.
        Serilog.Debugging.SelfLog.Enable(Console.Error);

        string logsDirectory = Path.Combine(AppContext.BaseDirectory, "Logs");

        try
        {
            Directory.CreateDirectory(logsDirectory);

            // Per-process probe name: two connectors sharing an install directory
            // must not race on one file and manufacture a spurious warning.
            string probe = Path.Combine(logsDirectory, $".writable-{Environment.ProcessId}");
            File.WriteAllText(probe, string.Empty);
            File.Delete(probe);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(
                $"WARNING: the log directory {logsDirectory} is not writable ({ex.Message}). " +
                "File logging may be unavailable for this run; the console output is authoritative.");
        }

        using var logger = CreateLogger(executable);

        Log.Logger = logger;

        ValidationErrors errors;

        try
        {
            ApplyDefaults(options, connector);

            errors = options.Validate(requireSharedAcl: !connector.ItemsCarryTheirOwnAcl);

            // Validate, not ValidateOptions: the source family's rules run first
            // and then the connector's own. A connector that defines
            // ValidateOptions is adding to that, never replacing it.
            connector.Validate(options, errors);
            RejectNeighboursConnection(options, connector, connectors, errors);
        }
        catch (Exception ex)
        {
            // ValidateOptions is connector-authored code; a throw there is a
            // configuration-stage fault and must not escape Main unhandled.
            Log.Fatal(RedactedException.Wrap(ex), "Configuration validation threw.");
            Log.CloseAndFlush();
            return 2;
        }

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

        // Declared ahead of the try so the cancellation catch below can tell a
        // genuine Ctrl+C from an HttpClient timeout wearing the same exception.
        using var cancellation = new CancellationTokenSource();

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

            // The same credential authenticates to Graph. Scope is unchanged.
            var graph = new GraphServiceClient(credential, new[] { GraphScope });

            var context = new PushSourceContext(options, credential, secrets, Log.Logger);
            var engine = new PushEngine(connector, options, graph, Log.Logger, dryRun);

            // Ctrl+C cancels cleanly: the token reaches every Graph call and
            // every poll delay, so a two-hour schema wait does not have to be
            // killed from Task Manager.
            Console.CancelKeyPress += (_, eventArgs) =>
            {
                eventArgs.Cancel = true;
                Log.Warning("Ctrl+C received. Stopping after the current item.");
                cancellation.Cancel();
            };

            PushSummary summary = await engine.RunAsync(context, cancellation.Token);

            Log.Information(
                "{Verb} complete. {Total} row(s) processed ({Breakdown}) for connection {ConnectionId}; " +
                "{Distinct} distinct item(s). " +
                "truncated={Truncated} skipped={Skipped} duplicates={Duplicates} throttleWaits={ThrottleWaits}",
                dryRun ? "Dry run" : "Ingestion",
                summary.Total,
                summary.Describe(),
                options.Graph.ConnectionId,
                summary.Total - summary.Duplicates,
                summary.Truncated,
                summary.Skipped,
                summary.Duplicates,
                summary.ThrottleWaits);

            return 0;
        }
        catch (AuthenticationFailedException ex)
        {
            // TokenCredentialFactory.Create only CONSTRUCTS the credential; Entra
            // is first contacted on the first Graph call, inside this try. An
            // expired secret or revoked certificate lands here, and the contract
            // says that is exit 3, not 4 - a monitoring rule keyed to 3 must fire
            // for credential rotation, not send the operator into the data path.
            Log.Fatal(RedactedException.Wrap(ex), "The credential was rejected by Entra ID.");
            return 3;
        }
        catch (ODataError ex) when (ex.ResponseStatusCode is 401 or 403)
        {
            Log.Fatal(
                RedactedException.Wrap(ex),
                "Graph rejected the caller ({Status}). Check admin consent for the application " +
                "permissions and that this app registration owns connection {ConnectionId}.",
                ex.ResponseStatusCode,
                options.Graph.ConnectionId);
            return 3;
        }
        catch (PushSourceAuthenticationException ex)
        {
            // The other half of exit 3. Graph rejecting us and the source
            // rejecting us are the same class of fault - this identity is no
            // longer accepted - and a monitoring rule keyed to 3 has to fire for
            // both, or a Kerberos ticket that stopped renewing looks like a bug
            // in the data path.
            Log.Fatal(RedactedException.Wrap(ex), "The source rejected this identity.");
            return 3;
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            // Filtered: the Graph HttpClient's request timeout also surfaces as
            // an OperationCanceledException, and a network hang must report as
            // an ingestion failure, not as "you cancelled" when nobody did.
            Log.Warning("Cancelled. The index holds what was written before the stop; re-run to complete.");
            return 4;
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
    /// Builds the logger every push executable uses. Public so a test can drive
    /// the exact production pipeline: the redaction canaries prove content never
    /// reaches a sink against THIS configuration, not a lookalike.
    /// </summary>
    /// <param name="executable">Names the log file, so an existing deployment's log path never moves.</param>
    /// <returns>The configured logger.</returns>
    public static Serilog.Core.Logger CreateLogger(string executable)
    {
        return ConfigurePushPipeline(new LoggerConfiguration())
            .WriteTo.Console(outputTemplate: "{Timestamp:HH:mm:ss} [{Level:u3}] {Message:lj}{NewLine}{Exception}")
            .WriteTo.File(
                Path.Combine(AppContext.BaseDirectory, "Logs", executable + ".log"),
                fileSizeLimitBytes: 10L * 1024 * 1024,
                rollOnFileSizeLimit: true,
                retainedFileCountLimit: 30,
                restrictedToMinimumLevel: LogEventLevel.Information)
            .CreateLogger();
    }

    /// <summary>
    /// The redaction half of the pipeline, separated from the sinks so the
    /// canary tests can attach a collecting sink to the exact configuration the
    /// executables run - not a lookalike.
    /// </summary>
    /// <param name="configuration">The configuration to extend.</param>
    /// <returns>The same configuration, for chaining.</returns>
    public static LoggerConfiguration ConfigurePushPipeline(LoggerConfiguration configuration)
    {
        return configuration
            .MinimumLevel.Information()
            .Enrich.With(new ScrubbingEnricher())

            // The engine logs item IDs and counts, never objects - but that is a
            // convention, and conventions drift. These registrations make the
            // risky Graph types render as their type name if one is ever logged,
            // instead of being destructured into full JSON including content.
            .Destructure.AsScalar<Microsoft.Graph.Models.ExternalConnectors.ExternalItem>()
            .Destructure.AsScalar<Microsoft.Graph.Models.ExternalConnectors.Properties>()
            .Destructure.AsScalar<Microsoft.Graph.Models.ExternalConnectors.ExternalItemContent>();
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

        // Anything past the connection itself belongs to the source family: a
        // SQL connector defaults Source:ItemView here, a filesystem connector
        // has no view to default. The core does not know which is which.
        connector.ApplyDefaults(options);
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
