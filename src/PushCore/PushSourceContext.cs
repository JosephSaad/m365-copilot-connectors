// ---------------------------------------------------------------------------
// PushSourceContext.cs
// What the host hands a connector so it can open its source.
//
// Deliberately the resolved objects rather than the raw configuration: the
// credential is already built and the secret provider is already the caching
// one, so a source cannot construct a second credential, a second vault client
// or a second cache and quietly double the token traffic. It also cannot reach
// a secret except through the provider, which is the component the redaction
// and rotation tests are written against.
//
// Secrets is null when no vault is configured. That is the normal case for a
// source authenticating as the service identity - integrated Windows auth to
// SQL Server, Kerberos to a Hadoop cluster - where there is no secret to
// resolve at all, which is the arrangement to prefer.
// ---------------------------------------------------------------------------

namespace PushCore;

using Azure.Core;
using Connector.Security.Secrets;
using PushCore.State;
using Serilog;

/// <summary>Everything a source needs from the host, already resolved.</summary>
public sealed class PushSourceContext
{
    /// <summary>Initializes a new instance of the <see cref="PushSourceContext"/> class.</summary>
    /// <param name="options">Validated configuration.</param>
    /// <param name="credential">The Entra credential, already built.</param>
    /// <param name="secrets">The caching secret provider, or null when no vault is configured.</param>
    /// <param name="log">Where to report progress.</param>
    public PushSourceContext(PushOptions options, TokenCredential credential, ISecretProvider? secrets, ILogger log)
    {
        this.Options = options;
        this.Credential = credential;
        this.Secrets = secrets;
        this.Log = log;
    }

    /// <summary>Gets the validated configuration.</summary>
    public PushOptions Options { get; }

    /// <summary>
    /// Gets the Entra credential. The same one authenticates to Graph, so a
    /// source that needs an Entra token - a directory lookup, an Azure SQL
    /// access token - uses this rather than building its own.
    /// </summary>
    public TokenCredential Credential { get; }

    /// <summary>Gets the secret provider, or null when no vault is configured.</summary>
    public ISecretProvider? Secrets { get; }

    /// <summary>Gets the logger.</summary>
    public ILogger Log { get; }

    /// <summary>
    /// Gets where an incremental read should resume, or null when the whole
    /// source must be read.
    ///
    /// Set by the engine from the crawl state store before the source is
    /// opened, so a connector building a query can add its "strictly after the
    /// marker" predicate without knowing the store exists. Null means a full
    /// read and is the only value a connector without a state store will ever
    /// see - which is why ignoring this property entirely still produces correct
    /// behaviour, just not an incremental one.
    ///
    /// A connector that USES this must declare
    /// <see cref="IPushSource.ChangeDetection"/> as
    /// <see cref="SourceChangeDetection.ChangeMarker"/> and must yield rows in
    /// ascending (marker, id) order. Reading from a marker while yielding out of
    /// order loses rows on the run after an interruption.
    /// </summary>
    public CrawlMarker? ResumeFrom { get; internal set; }
}
