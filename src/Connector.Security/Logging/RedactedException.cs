// ---------------------------------------------------------------------------
// RedactedException.cs
// Serilog writes exceptions by calling ToString(), which is outside the reach of
// an enricher. Exceptions are therefore wrapped before they are logged, so a
// SqlException that quotes a connection string cannot leak through the one path
// that skips property scrubbing.
// ---------------------------------------------------------------------------

namespace Connector.Security.Logging
{
    using System;
    using System.Text;

    /// <summary>
    /// A stand-in for an exception whose message may contain credential shaped
    /// text. Keeps the original type name and stack trace so diagnostics survive.
    /// </summary>
    public sealed class RedactedException : Exception
    {
        private readonly string originalTypeName;
        private readonly string originalStackTrace;

        private RedactedException(Exception source, Exception redactedInner)
            : base(LogScrubber.Scrub(source.Message), redactedInner)
        {
            this.originalTypeName = source.GetType().FullName;
            this.originalStackTrace = LogScrubber.Scrub(source.StackTrace);
        }

        /// <summary>Gets the type name of the exception this stands in for.</summary>
        public string OriginalTypeName
        {
            get { return this.originalTypeName; }
        }

        /// <inheritdoc />
        public override string StackTrace
        {
            get { return this.originalStackTrace; }
        }

        /// <summary>
        /// Returns an exception safe to hand to a log sink. The original instance
        /// is returned unchanged when neither it nor any inner exception contains
        /// anything worth scrubbing, which is the common case.
        /// </summary>
        public static Exception Wrap(Exception exception)
        {
            if (exception == null)
            {
                return null;
            }

            if (!NeedsRedaction(exception))
            {
                return exception;
            }

            Exception inner = exception.InnerException == null ? null : Wrap(exception.InnerException);
            return new RedactedException(exception, inner);
        }

        /// <inheritdoc />
        public override string ToString()
        {
            var builder = new StringBuilder();
            builder.Append(this.originalTypeName).Append(": ").Append(this.Message);

            if (this.InnerException != null)
            {
                builder.Append(" ---> ").Append(this.InnerException).AppendLine();
                builder.Append("   --- End of inner exception stack trace ---");
            }

            if (!string.IsNullOrEmpty(this.originalStackTrace))
            {
                builder.AppendLine().Append(this.originalStackTrace);
            }

            return builder.ToString();
        }

        private static bool NeedsRedaction(Exception exception)
        {
            Exception current = exception;

            while (current != null)
            {
                if (LogScrubber.NeedsScrubbing(current.Message) || LogScrubber.NeedsScrubbing(current.StackTrace))
                {
                    return true;
                }

                current = current.InnerException;
            }

            return false;
        }
    }
}
