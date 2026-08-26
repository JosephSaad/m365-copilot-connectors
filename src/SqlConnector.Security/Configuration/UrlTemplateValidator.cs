// ---------------------------------------------------------------------------
// UrlTemplateValidator.cs
// The three checks a composite URL template needs, in one place.
//
// Two consumers format an item URL from a row key: the agent-hosted connector
// and the tickets push tool. Each failure mode is nasty in its own way - a
// template without {0} silently stamps the identical URL on every item, and a
// malformed one throws on the first row of every run instead of at startup
// with the key named. Sharing the check is what stops one consumer validating
// two thirds less than the other, which is exactly what happened once.
// ---------------------------------------------------------------------------

namespace SqlConnector.Security.Configuration
{
    using System;
    using System.Globalization;

    /// <summary>Validates a composite format template that receives a row key.</summary>
    public static class UrlTemplateValidator
    {
        /// <summary>Records every problem with the template against a config path.</summary>
        /// <param name="errors">Accumulator, so every problem is reported at once.</param>
        /// <param name="path">The configuration key, for the error message.</param>
        /// <param name="template">The template as configured.</param>
        public static void Validate(ValidationErrors errors, string path, string template)
        {
            if (errors == null)
            {
                throw new ArgumentNullException(nameof(errors));
            }

            if (string.IsNullOrWhiteSpace(template))
            {
                errors.Add(path, "is required: every item carries a URL back to its source row.");
                return;
            }

            if (!template.Contains("{0}", StringComparison.Ordinal))
            {
                errors.Add(path, "must contain {0}, the placeholder the row key is formatted into. Without " +
                    "it every item silently carries the identical URL.");
                return;
            }

            try
            {
                string unused = string.Format(CultureInfo.InvariantCulture, template, 0);
            }
            catch (FormatException)
            {
                errors.Add(path, "is not a valid composite format string.");
            }
        }
    }
}
