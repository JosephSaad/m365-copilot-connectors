// ---------------------------------------------------------------------------
// PushLoggingRedactionTests.cs
// Redaction canaries for the PUSH pipeline, and a source-level tripwire for the
// one redaction rule Serilog cannot enforce.
//
// The connector service's canaries run against LoggingSetup.ApplyRedaction; the
// push executables build a different pipeline (PushHost.ConfigurePushPipeline),
// and until these tests it had no canary of its own - the header claim that
// content never reaches a sink was evidence for one pipeline, silently assumed
// for the other.
// ---------------------------------------------------------------------------

namespace SqlTicketsConnector.Tests
{
    using System;
    using System.Collections.Generic;
    using System.IO;
    using System.Linq;
    using System.Text.RegularExpressions;
    using Microsoft.Graph.Models.ExternalConnectors;
    using Serilog;
    using Serilog.Core;
    using Serilog.Events;
    using PushCore;
    using SqlTicketsConnector.Tests.TestSupport;
    using Xunit;

    public class PushLoggingRedactionTests
    {
        private const string Canary = "CANARY-9f1c-the-customer-narrative";

        private static Logger PushLogger(CollectingSink sink)
        {
            return PushHost.ConfigurePushPipeline(new LoggerConfiguration())
                .WriteTo.Sink(sink, LogEventLevel.Verbose)
                .CreateLogger();
        }

        [Fact]
        public void An_external_item_logged_by_either_spelling_never_leaks_its_content()
        {
            var sink = new CollectingSink();

            var item = new ExternalItem
            {
                Id = "cust1",
                Content = new ExternalItemContent { Value = Canary },
                Properties = new Properties
                {
                    AdditionalData = new Dictionary<string, object> { ["customerName"] = Canary },
                },
            };

            using (Logger logger = PushLogger(sink))
            {
                // Both spellings: destructured and plain. The destructuring
                // registrations catch the first, the enricher the second.
                logger.Information("Writing {@Item}.", item);
                logger.Information("Writing {Item}.", item);
            }

            string rendered = string.Join(Environment.NewLine, sink.Events.Select(Render));

            Assert.DoesNotContain(Canary, rendered, StringComparison.Ordinal);
        }

        [Fact]
        public void A_connection_string_shaped_message_is_scrubbed_by_the_push_pipeline()
        {
            var sink = new CollectingSink();

            using (Logger logger = PushLogger(sink))
            {
                logger.Warning("Could not connect with {Details}.", "Server=x;Password=hunter2;Database=Ops");
            }

            string rendered = string.Join(Environment.NewLine, sink.Events.Select(Render));

            Assert.DoesNotContain("hunter2", rendered, StringComparison.Ordinal);
        }

        [Fact]
        public void No_source_file_logs_an_exception_without_wrapping_it()
        {
            // Serilog enrichers cannot rewrite LogEvent.Exception, so exception
            // redaction rests on every call site remembering RedactedException
            // .Wrap. A convention nothing checks is a convention that drifts;
            // this scan is the check. Allowed shapes: Wrap(...) as the first
            // argument, or no exception argument at all.
            var offenders = new List<string>();

            // Covers every Serilog level plus the Write(level, ex, ...) overload,
            // and dotted receivers (this.lastException) as well as bare locals.
            var pattern = new Regex(
                @"\.(Fatal|Error|Warning|Information|Debug|Verbose|Write)\(\s*(?:LogEventLevel\.\w+\s*,\s*)?(?!(?:Logging\.)?RedactedException\.Wrap)(?<arg>[A-Za-z_][A-Za-z0-9_.]*)\s*,",
                RegexOptions.Compiled);

            foreach (string file in Directory.EnumerateFiles(
                Path.Combine(RepositoryRoot(), "src"), "*.cs", SearchOption.AllDirectories))
            {
                string text = File.ReadAllText(file);

                foreach (Match match in pattern.Matches(text))
                {
                    string arg = match.Groups["arg"].Value;
                    string leaf = arg[(arg.LastIndexOf('.') + 1)..];

                    if (leaf.EndsWith("Exception", StringComparison.Ordinal) ||
                        leaf is "ex" or "exception" or "e" or "error" or "failure")
                    {
                        int line = text.Take(match.Index).Count(c => c == '\n') + 1;
                        offenders.Add($"{Path.GetFileName(file)}:{line} logs '{arg}' unwrapped");
                    }
                }
            }

            Assert.True(
                offenders.Count == 0,
                "Exceptions must be logged through RedactedException.Wrap:\n  " + string.Join("\n  ", offenders));
        }

        private static string Render(LogEvent logEvent)
        {
            using var writer = new StringWriter();
            logEvent.RenderMessage(writer);

            foreach (KeyValuePair<string, LogEventPropertyValue> property in logEvent.Properties)
            {
                writer.Write(' ');
                property.Value.Render(writer);
            }

            if (logEvent.Exception is not null)
            {
                writer.Write(logEvent.Exception.ToString());
            }

            return writer.ToString();
        }

        private static string RepositoryRoot()
        {
            var directory = new DirectoryInfo(AppContext.BaseDirectory);

            while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "SqlTicketsConnector.sln")))
            {
                directory = directory.Parent;
            }

            Assert.True(directory is not null, "could not locate the repository root from " + AppContext.BaseDirectory);
            return directory!.FullName;
        }
    }
}
