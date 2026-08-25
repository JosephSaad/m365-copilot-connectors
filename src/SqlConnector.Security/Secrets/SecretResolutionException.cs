// ---------------------------------------------------------------------------
// SecretResolutionException.cs
// ---------------------------------------------------------------------------

namespace SqlConnector.Security.Secrets
{
    using System;

    /// <summary>
    /// Raised when a secret cannot be resolved. The message names the secret and
    /// the source, never the value.
    /// </summary>
    public sealed class SecretResolutionException : Exception
    {
        /// <summary>Initializes a new instance with a message.</summary>
        public SecretResolutionException(string message)
            : base(message)
        {
        }

        /// <summary>Initializes a new instance with a message and inner exception.</summary>
        public SecretResolutionException(string message, Exception innerException)
            : base(message, innerException)
        {
        }
    }
}
