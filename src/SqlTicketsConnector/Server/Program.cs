// ---------------------------------------------------------------------------
// Program.cs
// Process entry point: load configuration, build the logger, validate every
// field at once, start the server, wait for shutdown.
//
// Exit codes are distinct so the service recovery action and the installer can
// tell a configuration fault from a runtime one:
//   0 clean shutdown
//   2 configuration could not be read or failed validation
//   3 the server could not start
// ---------------------------------------------------------------------------

namespace SqlTicketsConnector
{
    using System;
    using System.Threading;
    using Serilog;
    using Serilog.Core;
    using SqlTicketsConnector.Logging;
    using SqlTicketsConnector.Security.Configuration;
    using SqlTicketsConnector.Security.Logging;
    using SqlTicketsConnector.Server;

    /// <summary>Process entry point.</summary>
    public class Program
    {
        private static readonly ManualResetEventSlim ShutdownSignal = new ManualResetEventSlim(false);

        /// <summary>Starts the gRPC server and blocks until shutdown.</summary>
        public static int Main()
        {
            ConnectorOptions options;

            try
            {
                options = ConnectorOptions.Load();
            }
            catch (Exception ex)
            {
                // The logger needs configuration, so this one failure has nowhere to
                // go but the console and the process exit code.
                Console.Error.WriteLine("FATAL: " + ex.Message);
                return 2;
            }

            Logger logger;

            try
            {
                logger = LoggingSetup.Create(options);
            }
            catch (Exception ex)
            {
                // A bad Logging section (unwritable directory, invalid event log
                // source) is a configuration fault. Without this guard it would
                // escape Main as an unhandled exception: CLR crash dump, no FATAL
                // line, and an exit code outside the documented contract.
                Console.Error.WriteLine("FATAL: could not initialise logging: " + ex);
                return 2;
            }

            using (logger)
            {
                Log.Logger = logger;

                try
                {
                    Log.Information(
                        "SqlTicketsConnector starting. Configuration {ConfigurationPath}. Environment {Environment}.",
                        options.SourcePath,
                        options.Environment);

                    ValidationErrors errors = options.Validate();

                    if (errors.HasErrors)
                    {
                        // Every problem at once. Fixing five typos should cost one
                        // restart, not five.
                        Log.Fatal(
                            "Configuration in {ConfigurationPath} is invalid. {ErrorCount} problem(s):{NewLine}{Errors}",
                            options.SourcePath,
                            errors.Errors.Count,
                            Environment.NewLine,
                            errors.ToMessage());

                        return 2;
                    }

                    using (var server = new ConnectorServer(options, Log.Logger))
                    {
                        try
                        {
                            server.Start();
                        }
                        catch (Exception ex)
                        {
                            Log.Fatal(RedactedException.Wrap(ex), "Server failed to start.");
                            return 3;
                        }

                        Console.CancelKeyPress += (sender, args) =>
                        {
                            args.Cancel = true;
                            Log.Information("Ctrl+C received. Shutting down.");
                            ShutdownSignal.Set();
                        };

                        AppDomain.CurrentDomain.ProcessExit += (sender, args) => ShutdownSignal.Set();

                        ShutdownSignal.Wait();
                        server.Stop();
                    }

                    return 0;
                }
                finally
                {
                    Log.CloseAndFlush();
                }
            }
        }
    }
}
