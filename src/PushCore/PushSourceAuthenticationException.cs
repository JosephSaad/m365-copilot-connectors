// ---------------------------------------------------------------------------
// PushSourceAuthenticationException.cs
// "The source refused our identity", said in a way the exit-code contract can
// hear.
//
// Exit 3 means a credential problem and exit 4 means the ingestion failed.
// Graph's own rejections already land on 3 through AuthenticationFailedException
// and ODataError 401/403. A source rejecting the service identity - an expired
// Kerberos ticket, a revoked Ranger grant, a SQL login that lost its role - is
// the same class of fault and must reach the same exit code, or a monitoring
// rule keyed to 3 sends someone into the data path to look for a bug that is
// really a rotation.
//
// A source raises this instead of letting the driver's own exception escape,
// because only the source knows which of its driver's error codes mean
// "authentication" rather than "unavailable". Wrap the original as the inner
// exception; the host logs it through RedactedException.Wrap.
// ---------------------------------------------------------------------------

namespace PushCore;

/// <summary>The source rejected the identity this process runs as.</summary>
public sealed class PushSourceAuthenticationException : Exception
{
    /// <summary>Initializes a new instance of the <see cref="PushSourceAuthenticationException"/> class.</summary>
    /// <param name="message">What was refused, without any credential material in it.</param>
    public PushSourceAuthenticationException(string message)
        : base(message)
    {
    }

    /// <summary>Initializes a new instance of the <see cref="PushSourceAuthenticationException"/> class.</summary>
    /// <param name="message">What was refused, without any credential material in it.</param>
    /// <param name="innerException">The driver's own exception.</param>
    public PushSourceAuthenticationException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
