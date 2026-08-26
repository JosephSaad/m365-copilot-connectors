// ---------------------------------------------------------------------------
// CertificateResolutionException.cs
// ---------------------------------------------------------------------------

namespace Connector.Security.Certificates
{
    using System;
    using System.Collections.Generic;

    /// <summary>
    /// Raised when no configured certificate can be used. Carries the full
    /// rejection list so the log line names every thumbprint that was tried.
    /// </summary>
    public sealed class CertificateResolutionException : Exception
    {
        /// <summary>Initializes a new instance.</summary>
        public CertificateResolutionException(string message, IReadOnlyList<CertificateRejection> rejections)
            : base(message)
        {
            this.Rejections = rejections ?? new List<CertificateRejection>();
        }

        /// <summary>Initializes a new instance with an inner exception.</summary>
        public CertificateResolutionException(string message, Exception innerException)
            : base(message, innerException)
        {
            this.Rejections = new List<CertificateRejection>();
        }

        /// <summary>Gets every certificate that was considered and skipped.</summary>
        public IReadOnlyList<CertificateRejection> Rejections { get; }
    }
}
