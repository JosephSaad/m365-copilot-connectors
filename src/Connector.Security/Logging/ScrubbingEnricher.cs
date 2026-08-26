// ---------------------------------------------------------------------------
// ScrubbingEnricher.cs
// Rewrites property values before they reach a sink.
//
// A destructuring policy only applies to properties logged with the @ operator.
// A plain {Value} hole keeps the object in a ScalarValue and renders it through
// ToString() at write time, which for a protobuf message is full JSON including
// item content. This enricher closes that gap: strings are scrubbed for
// credential shaped text, and any other object is handed to the same
// destructuring policy the pipeline uses, so both spellings of a log call end up
// equally safe.
// ---------------------------------------------------------------------------

namespace Connector.Security.Logging
{
    using System;
    using System.Collections.Generic;
    using Serilog.Core;
    using Serilog.Events;

    /// <summary>
    /// Applies <see cref="LogScrubber"/> to strings, and a destructuring policy to
    /// everything else, for every property in a log event.
    /// </summary>
    public sealed class ScrubbingEnricher : ILogEventEnricher
    {
        private static readonly SafeValueFactory Factory = new SafeValueFactory();

        private readonly IDestructuringPolicy fallbackPolicy;

        /// <summary>Initializes the enricher with string scrubbing only.</summary>
        public ScrubbingEnricher()
            : this(null)
        {
        }

        /// <summary>
        /// Initializes the enricher, additionally applying the supplied policy to
        /// objects that were logged without the destructuring operator.
        /// </summary>
        public ScrubbingEnricher(IDestructuringPolicy fallbackPolicy)
        {
            this.fallbackPolicy = fallbackPolicy;
        }

        /// <inheritdoc />
        public void Enrich(LogEvent logEvent, ILogEventPropertyFactory propertyFactory)
        {
            if (logEvent == null)
            {
                return;
            }

            List<LogEventProperty> replacements = null;

            foreach (KeyValuePair<string, LogEventPropertyValue> property in logEvent.Properties)
            {
                LogEventPropertyValue scrubbed;

                try
                {
                    scrubbed = this.Scrub(property.Value);
                }
                catch (Exception)
                {
                    // Serilog swallows enricher exceptions and still emits the
                    // event, so a throw here would fail OPEN: the event would be
                    // written with this property unscrubbed. Fail closed instead -
                    // a property the scrubber could not process is replaced, never
                    // passed through.
                    scrubbed = new ScalarValue(LogScrubber.Replacement);
                }

                if (!ReferenceEquals(scrubbed, property.Value))
                {
                    replacements = replacements ?? new List<LogEventProperty>();
                    replacements.Add(new LogEventProperty(property.Key, scrubbed));
                }
            }

            if (replacements == null)
            {
                return;
            }

            foreach (LogEventProperty replacement in replacements)
            {
                logEvent.AddOrUpdateProperty(replacement);
            }
        }

        private static bool IsSimple(object value)
        {
            return value is bool
                || value is char
                || value is sbyte
                || value is byte
                || value is short
                || value is ushort
                || value is int
                || value is uint
                || value is long
                || value is ulong
                || value is float
                || value is double
                || value is decimal
                || value is DateTime
                || value is DateTimeOffset
                || value is TimeSpan
                || value is Guid
                || value is Uri
                || value is Enum;
        }

        private LogEventPropertyValue Scrub(LogEventPropertyValue value)
        {
            var scalar = value as ScalarValue;
            if (scalar != null)
            {
                var text = scalar.Value as string;
                if (text != null)
                {
                    return LogScrubber.NeedsScrubbing(text) ? new ScalarValue(LogScrubber.Scrub(text)) : value;
                }

                if (this.fallbackPolicy == null || scalar.Value == null || IsSimple(scalar.Value))
                {
                    return value;
                }

                LogEventPropertyValue replacement;
                if (this.fallbackPolicy.TryDestructure(scalar.Value, Factory, out replacement))
                {
                    return replacement;
                }

                return value;
            }

            var sequence = value as SequenceValue;
            if (sequence != null)
            {
                List<LogEventPropertyValue> elements = null;

                for (int i = 0; i < sequence.Elements.Count; i++)
                {
                    LogEventPropertyValue element = this.Scrub(sequence.Elements[i]);

                    if (!ReferenceEquals(element, sequence.Elements[i]) && elements == null)
                    {
                        elements = new List<LogEventPropertyValue>(sequence.Elements);
                    }

                    if (elements != null)
                    {
                        elements[i] = element;
                    }
                }

                return elements == null ? value : new SequenceValue(elements);
            }

            var structure = value as StructureValue;
            if (structure != null)
            {
                List<LogEventProperty> properties = null;

                for (int i = 0; i < structure.Properties.Count; i++)
                {
                    LogEventProperty property = structure.Properties[i];
                    LogEventPropertyValue scrubbed = this.Scrub(property.Value);

                    if (!ReferenceEquals(scrubbed, property.Value) && properties == null)
                    {
                        properties = new List<LogEventProperty>(structure.Properties);
                    }

                    if (properties != null)
                    {
                        properties[i] = new LogEventProperty(property.Name, scrubbed);
                    }
                }

                return properties == null ? value : new StructureValue(properties, structure.TypeTag);
            }

            var dictionary = value as DictionaryValue;
            if (dictionary != null)
            {
                var elements = new List<KeyValuePair<ScalarValue, LogEventPropertyValue>>(dictionary.Elements.Count);
                bool changed = false;

                foreach (KeyValuePair<ScalarValue, LogEventPropertyValue> pair in dictionary.Elements)
                {
                    LogEventPropertyValue scrubbed = this.Scrub(pair.Value);
                    changed = changed || !ReferenceEquals(scrubbed, pair.Value);
                    elements.Add(new KeyValuePair<ScalarValue, LogEventPropertyValue>(pair.Key, scrubbed));
                }

                return changed ? new DictionaryValue(elements) : value;
            }

            return value;
        }

        /// <summary>
        /// Supplied to the fallback policy so it never has to reach back into
        /// Serilog's own conversion, which would undo the redaction.
        /// </summary>
        private sealed class SafeValueFactory : ILogEventPropertyValueFactory
        {
            public LogEventPropertyValue CreatePropertyValue(object value, bool destructureObjects)
            {
                return new ScalarValue("[redacted]");
            }
        }
    }
}
