// ---------------------------------------------------------------------------
// ValidationErrors.cs
// Collects every configuration problem so startup can report them all at once.
// Failing on the first bad field costs an operator one restart per mistake.
// ---------------------------------------------------------------------------

namespace SqlTicketsConnector.Security.Configuration
{
    using System;
    using System.Collections.Generic;
    using System.Globalization;

    /// <summary>
    /// Accumulator for configuration validation messages.
    /// </summary>
    public sealed class ValidationErrors
    {
        private readonly List<string> errors = new List<string>();

        /// <summary>Gets a value indicating whether any field failed validation.</summary>
        public bool HasErrors
        {
            get { return this.errors.Count > 0; }
        }

        /// <summary>Gets every message collected so far, in the order added.</summary>
        public IReadOnlyList<string> Errors
        {
            get { return this.errors; }
        }

        /// <summary>Records a problem against a configuration path such as "Auth:TenantId".</summary>
        public void Add(string path, string message)
        {
            this.errors.Add(path + ": " + message);
        }

        /// <summary>Records a problem when the value is null, empty or whitespace.</summary>
        public void RequireNonEmpty(string path, string value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                this.Add(path, "is required but was empty.");
            }
        }

        /// <summary>Records a problem when the value is not a GUID.</summary>
        public void RequireGuid(string path, string value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                this.Add(path, "is required but was empty.");
                return;
            }

            Guid parsed;
            if (!Guid.TryParse(value, out parsed))
            {
                this.Add(path, "must be a GUID.");
                return;
            }

            if (parsed == Guid.Empty)
            {
                // The all zero GUID is what a half finished deployment looks like.
                this.Add(path, "must be a real GUID, not the empty GUID.");
            }
        }

        /// <summary>Records a problem when the value falls outside an inclusive range.</summary>
        public void RequireRange(string path, int value, int minimum, int maximum)
        {
            if (value < minimum || value > maximum)
            {
                this.Add(
                    path,
                    string.Format(
                        CultureInfo.InvariantCulture,
                        "must be between {0} and {1}; found {2}.",
                        minimum,
                        maximum,
                        value));
            }
        }

        /// <summary>Records a problem when the value is not one of the allowed values.</summary>
        public void RequireOneOf(string path, string value, params string[] allowed)
        {
            foreach (string candidate in allowed)
            {
                if (string.Equals(candidate, value, StringComparison.OrdinalIgnoreCase))
                {
                    return;
                }
            }

            this.Add(
                path,
                string.Format(
                    CultureInfo.InvariantCulture,
                    "must be one of [{0}]; found '{1}'.",
                    string.Join(", ", allowed),
                    value ?? "(null)"));
        }

        /// <summary>Renders every message as one multi-line block for a single log entry.</summary>
        public string ToMessage()
        {
            return string.Join(Environment.NewLine, this.errors);
        }
    }
}
