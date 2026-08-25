// ---------------------------------------------------------------------------
// ICertificateResolver.cs
// ---------------------------------------------------------------------------

namespace SqlConnector.Security.Certificates
{
    using System.Collections.Generic;

    /// <summary>
    /// Supplies the client certificates used to authenticate to Entra ID, in the
    /// order they should be tried. A list rather than a single certificate is
    /// what makes a zero downtime rotation possible: install the new certificate,
    /// restart, confirm from the log which thumbprint authenticated, then remove
    /// the old one.
    /// </summary>
    public interface ICertificateResolver
    {
        /// <summary>
        /// Returns every usable certificate, best first.
        /// Implementations throw <see cref="CertificateResolutionException"/> when
        /// none is usable, with a message naming each rejected thumbprint, the
        /// reason, and the process identity that did the looking.
        /// </summary>
        IReadOnlyList<CertificateCandidate> ResolveCandidates();
    }
}
