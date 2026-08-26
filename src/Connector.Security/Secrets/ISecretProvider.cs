// ---------------------------------------------------------------------------
// ISecretProvider.cs
// The only way any project in this solution obtains a secret value.
// ---------------------------------------------------------------------------

namespace Connector.Security.Secrets
{
    using System.Threading;
    using System.Threading.Tasks;

    /// <summary>
    /// Resolves secrets by name at runtime. Implementations must never write a
    /// resolved value to disk, to a temporary file, or to a log.
    /// </summary>
    /// <remarks>
    /// Values are returned as ordinary strings rather than SecureString. On .NET
    /// 8 SecureString provides no memory protection outside Windows CryptProtect
    /// paths that .NET no longer uses, and the value has to be marshalled back to
    /// a managed string to build a connection string anyway. Using it here would
    /// suggest a protection to a reviewer that does not exist. See docs/SECURITY.md.
    /// </remarks>
    public interface ISecretProvider
    {
        /// <summary>Resolves a secret by name.</summary>
        Task<string> GetSecretAsync(string name, CancellationToken ct);

        /// <summary>
        /// Drops any cached copy of the named secret so the next resolution goes
        /// back to the source. Called after an authentication failure so a
        /// rotated credential is picked up without restarting the service.
        /// </summary>
        Task InvalidateAsync(string name, CancellationToken ct);
    }
}
