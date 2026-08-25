// ---------------------------------------------------------------------------
// LoggingSetup.cs
// Builds the Serilog pipeline: rolling file, Windows event log, console in
// development, and an optional OTLP exporter.
//
// The default log directory sits under the install directory rather than the
// service account's LocalAppData, because nobody looks there, least of all at
// 3am during an incident.
// ---------------------------------------------------------------------------

namespace SqlTicketsConnector.Logging
{
    using System;
    using System.IO;
    using Serilog;
    using Serilog.Core;
    using Serilog.Events;
    using SqlConnector.Security.Logging;
    using SqlTicketsConnector.Server;

    /// <summary>
    /// Creates the configured logger.
    /// </summary>
    public static class LoggingSetup
    {
        /// <summary>Name of the log file, before Serilog appends its roll suffix.</summary>
        public const string LogFileName = "ConnectorLog.log";

        private const string OutputTemplate =
            "{Timestamp:yyyy-MM-dd HH:mm:ss.fffzzz} [{Level:u3}] [{ConnectorId}] [{CrawlId}] {Message:lj}{NewLine}{Exception}";

        /// <summary>Builds the logger from the Logging section.</summary>
        public static Logger Create(ConnectorOptions options)
        {
            if (options == null)
            {
                throw new ArgumentNullException(nameof(options));
            }

            LoggingOptions logging = options.Logging ?? new LoggingOptions();
            string directory = ResolveDirectory(logging.Directory);
            Directory.CreateDirectory(directory);

            var configuration = ApplyRedaction(new LoggerConfiguration())
                .MinimumLevel.Is(ParseLevel(logging.MinimumLevel))
                .Enrich.FromLogContext()
                .Enrich.WithProperty("ConnectorId", options.Connector == null ? string.Empty : options.Connector.Id)
                .Enrich.WithProperty("MachineName", System.Environment.MachineName)
                .Enrich.WithProperty("ProcessId", System.Environment.ProcessId)

                .WriteTo.File(
                    Path.Combine(directory, LogFileName),
                    fileSizeLimitBytes: logging.FileSizeLimitBytes,
                    rollOnFileSizeLimit: true,
                    retainedFileCountLimit: logging.RetainedFileCountLimit,
                    outputTemplate: OutputTemplate);

            if (!options.IsProduction)
            {
                // Console is for interactive development only. A Windows service has
                // no console, and writing to one that is not there costs time in the
                // hot path of a crawl.
                configuration = configuration.WriteTo.Console(outputTemplate: OutputTemplate);
            }

            if (logging.EventLogEnabled && OperatingSystem.IsWindows())
            {
                // manageEventSource stays false: creating the source needs
                // administrative rights, and the service account must not have them.
                // Install-Connector.ps1 creates it at deployment time.
                configuration = configuration.WriteTo.EventLog(
                    source: logging.EventLogSource,
                    manageEventSource: false,
                    restrictedToMinimumLevel: LogEventLevel.Warning);
            }

            Logger logger = ConfigureOptionalExporters(configuration, logging).CreateLogger();

            if (logging.EventLogEnabled && !OperatingSystem.IsWindows())
            {
                logger.Warning(
                    "Logging:EventLogEnabled is true but this host is not Windows. The event log sink is inactive.");
            }

            return logger;
        }

        /// <summary>
        /// Attaches the redaction controls. Kept separate so the test suite builds
        /// its logger through exactly the same code the service uses.
        /// </summary>
        public static LoggerConfiguration ApplyRedaction(LoggerConfiguration configuration)
        {
            if (configuration == null)
            {
                throw new ArgumentNullException(nameof(configuration));
            }

            foreach (Type type in RedactionDestructuringPolicy.NeverStringify)
            {
                // Keeps the object in the event instead of its ToString(), so the
                // enricher below can redact it even for a plain {Value} hole.
                configuration = configuration.Destructure.AsScalar(type);
            }

            // The policy shapes properties logged with {@X}; the enricher applies the
            // same policy to objects logged as plain {X} and scrubs any text that
            // survived either route.
            return configuration
                .Destructure.With(new RedactionDestructuringPolicy())
                .Enrich.With(new ScrubbingEnricher(new RedactionDestructuringPolicy()));
        }

        /// <summary>Resolves the log directory, defaulting to Logs beside the executable.</summary>
        public static string ResolveDirectory(string configured)
        {
            if (!string.IsNullOrWhiteSpace(configured))
            {
                return configured;
            }

            return Path.Combine(AppContext.BaseDirectory, "Logs");
        }

        /// <summary>Parses a level name, falling back to Information.</summary>
        public static LogEventLevel ParseLevel(string level)
        {
            LogEventLevel parsed;
            return Enum.TryParse(level, true, out parsed) ? parsed : LogEventLevel.Information;
        }

        private static LoggerConfiguration ConfigureOptionalExporters(
            LoggerConfiguration configuration,
            LoggingOptions logging)
        {
            OtlpOptions otlp = logging.Otlp ?? new OtlpOptions();

            if (!otlp.Enabled)
            {
                return configuration;
            }

#if OTLP_EXPORTER
            return configuration.WriteTo.OpenTelemetry(exporter =>
            {
                exporter.Endpoint = otlp.Endpoint;
                exporter.ResourceAttributes = new System.Collections.Generic.Dictionary<string, object>
                {
                    { "service.name", "SqlTicketsConnector" },
                };
            });
#else
            // The exporter package drags in a second gRPC stack and a newer
            // Google.Protobuf, so it is not part of the default dependency graph.
            // Build with -p:EnableOtlpExporter=true to include it.
            Console.Error.WriteLine(
                "Logging:Otlp:Enabled is true but this build was produced without the OTLP exporter. " +
                "Rebuild with -p:EnableOtlpExporter=true. See docs/SECURITY.md.");
            return configuration;
#endif
        }
    }
}
