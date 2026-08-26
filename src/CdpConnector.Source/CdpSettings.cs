// ---------------------------------------------------------------------------
// CdpSettings.cs
// The connector-specific configuration, read out of the Settings bag once and
// checked before anything opens a socket.
//
// It lives in the bag rather than as properties on PushOptions because that is
// the repository's rule for anything one connector needs and the others do not:
// a cluster host name has no business in a file that a SQL connector also
// reads. Reading it into a typed object here, once, is what keeps the rest of
// the code from doing string lookups with defaults scattered through it.
//
// There is no credential in this class and there is no place to put one. Hive
// and Impala authenticate through the ODBC driver's SSPI plugin and HDFS
// through HTTP Negotiate, both as the identity the service already runs as, so
// the connection string this builds has no UID and no PWD in it - and
// ValidateHive refuses one that does, rather than trusting that nobody will.
// ---------------------------------------------------------------------------

namespace CdpConnector.Source;

using Connector.Security.Configuration;
using PushCore;

/// <summary>How a cluster group name becomes a principal on an item.</summary>
public enum GroupMappingMode
{
    /// <summary>
    /// The cluster's groups are AD groups synchronised to Entra, so each one is
    /// looked up by name and becomes an Entra group grant. The normal case for
    /// a cluster whose Kerberos is AD-integrated.
    /// </summary>
    EntraByName = 0,

    /// <summary>
    /// The cluster's groups are local to it, so each one is mirrored as an
    /// external group on this connection and referenced by ID.
    /// </summary>
    ExternalGroups = 1,
}

/// <summary>How the process obtains its Kerberos identity.</summary>
public enum KerberosMode
{
    /// <summary>
    /// Windows SSPI, from the logon session of the account the service runs as -
    /// a gMSA for preference. Nothing is held by this process, and there is no
    /// secret anywhere for an operator to leak. The default, and the one to use.
    /// </summary>
    Sspi = 0,

    /// <summary>
    /// The driver's own MIT GSSAPI path against an operator-provisioned keytab,
    /// for a cluster whose realm has no trust to Active Directory. A keytab is a
    /// secret at rest, so this mode is opt-in, is refused unless the file is
    /// reachable, and never travels in the repository or the package.
    ///
    /// Note it is the DRIVER that reads the keytab, not this process, and note
    /// that SSPI cannot consume an MIT ticket cache - the two modes are
    /// alternatives, not layers.
    /// </summary>
    MitKeytab = 1,
}

/// <summary>The CDP half of a connector's configuration.</summary>
public sealed class CdpSettings
{
    private CdpSettings()
    {
    }

    /// <summary>Gets the HttpFS or WebHDFS base URL, ending in /webhdfs/v1.</summary>
    public string HdfsBaseUrl { get; private init; } = string.Empty;

    /// <summary>Gets the absolute HDFS paths to crawl.</summary>
    public IReadOnlyList<string> HdfsRoots { get; private init; } = Array.Empty<string>();

    /// <summary>Gets the file extensions to index, lower case and without dots. Empty means every supported one.</summary>
    public IReadOnlyList<string> IncludeExtensions { get; private init; } = Array.Empty<string>();

    /// <summary>Gets the size above which a file's text is not extracted. Its metadata still is.</summary>
    public long MaxRawFileBytes { get; private init; }

    /// <summary>
    /// Gets the number of seconds subtracted from the stored watermark before a
    /// crawl resumes, to absorb clock skew between this host and the NameNode.
    ///
    /// It does NOT solve a file renamed into scope with an older timestamp -
    /// nothing bounded does, which is what FullRecrawlEveryRuns is for.
    /// </summary>
    public int ScanSlackSeconds { get; private init; }

    /// <summary>Gets the Ranger Admin base URL, or empty when routing is not enforced.</summary>
    public string RangerBaseUrl { get; private init; } = string.Empty;

    /// <summary>Gets the Ranger service name for HDFS policies.</summary>
    public string RangerHdfsService { get; private init; } = string.Empty;

    /// <summary>Gets the Ranger service name for Hive and Impala policies. One definition covers both.</summary>
    public string RangerSqlService { get; private init; } = string.Empty;

    /// <summary>Gets how a cluster group name becomes a principal.</summary>
    public GroupMappingMode GroupMapping { get; private init; }

    /// <summary>
    /// Gets the Entra group that world-readable files are granted to, or empty.
    ///
    /// Empty is the default and means the other-read bit contributes no grant at
    /// all. "Everyone on the cluster" and "everyone in the tenant" are not the
    /// same set of people, and mapping one to the other silently is how a lake
    /// ends up searchable by the whole company.
    /// </summary>
    public string OtherReadableGroupId { get; private init; } = string.Empty;

    /// <summary>Gets where the watermark file is kept, relative to the executable unless rooted.</summary>
    public string CheckpointDirectory { get; private init; } = string.Empty;

    /// <summary>
    /// Gets how often a run ignores the watermark and re-reads everything.
    ///
    /// This is the connector's answer to two things an incremental crawl cannot
    /// see, and it is a security control as much as a completeness one:
    ///
    ///   * A permission change does not alter a file's modification time, so an
    ///     item whose group grant was revoked at the source is never revisited
    ///     by an incremental pass. This setting is therefore the documented
    ///     upper bound on ACL staleness - at a daily schedule, seven runs is
    ///     seven days - and it belongs in the deployment's risk record.
    ///   * A file moved into a crawled directory keeps its old timestamp and
    ///     would otherwise be skipped for ever.
    ///
    /// Zero disables it, which is a deliberate choice to be made in writing.
    /// </summary>
    public int FullRecrawlEveryRuns { get; private init; }

    /// <summary>Gets the most items one run will push. Zero means no cap.</summary>
    public int MaxItemsPerRun { get; private init; }

    /// <summary>
    /// Gets the number of items this connection is budgeted for. Zero means
    /// unset. A preflight estimate above it fails startup rather than
    /// discovering the connection's real ceiling halfway through a crawl.
    /// </summary>
    public int ItemBudget { get; private init; }

    /// <summary>
    /// Gets the percentage of examined items that may fail before the run
    /// aborts. It stops a systemically broken extractor or a sick DataNode from
    /// being laundered into a successful crawl of skips.
    /// </summary>
    public int MaxErrorRatePercent { get; private init; }

    /// <summary>Gets how the process obtains its Kerberos identity.</summary>
    public KerberosMode Kerberos { get; private init; }

    /// <summary>Gets the Hive or Impala host.</summary>
    public string HiveHost { get; private init; } = string.Empty;

    /// <summary>Gets the HiveServer2 or Impala port.</summary>
    public int HivePort { get; private init; }

    /// <summary>Gets the installed ODBC driver name.</summary>
    public string HiveDriver { get; private init; } = string.Empty;

    /// <summary>Gets the Thrift transport: http or sasl. Kerberos does not support binary.</summary>
    public string HiveTransport { get; private init; } = string.Empty;

    /// <summary>Gets the HTTP endpoint path, used only by the http transport.</summary>
    public string HiveHttpPath { get; private init; } = string.Empty;

    /// <summary>Gets a value indicating whether the ODBC connection uses TLS.</summary>
    public bool HiveUseSsl { get; private init; }

    /// <summary>Gets the Kerberos realm the HiveServer2 principal belongs to.</summary>
    public string HiveRealm { get; private init; } = string.Empty;

    /// <summary>Gets the service name of the HiveServer2 principal, normally hive or impala.</summary>
    public string HiveServiceName { get; private init; } = string.Empty;

    /// <summary>Gets extra ODBC keywords, inspected for credentials and downgrades before use.</summary>
    public string HiveExtraOptions { get; private init; } = string.Empty;

    /// <summary>Gets the column a Hive crawl watermarks on, or empty for a full read each run.</summary>
    public string HiveWatermarkColumn { get; private init; } = string.Empty;

    /// <summary>Gets the column that breaks watermark ties, normally the table's key.</summary>
    public string HiveKeyColumn { get; private init; } = string.Empty;

    /// <summary>Reads the settings bag. Does not validate; call one of the Validate methods.</summary>
    /// <param name="options">The configuration as loaded.</param>
    /// <returns>The settings.</returns>
    public static CdpSettings From(PushOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        return new CdpSettings
        {
            HdfsBaseUrl = options.Setting("HdfsBaseUrl").TrimEnd('/'),
            HdfsRoots = SplitList(options.Setting("HdfsRoots")),
            IncludeExtensions = SplitList(options.Setting("IncludeExtensions"))
                .Select(value => value.TrimStart('.').ToLowerInvariant())
                .ToList(),
            MaxRawFileBytes = options.Setting("MaxRawFileBytes", 268435456),
            ScanSlackSeconds = options.Setting("ScanSlackSeconds", 900),

            RangerBaseUrl = options.Setting("RangerBaseUrl").TrimEnd('/'),
            RangerHdfsService = options.Setting("RangerHdfsService", "cm_hdfs"),
            RangerSqlService = options.Setting("RangerSqlService", "cm_hive"),

            GroupMapping = string.Equals(
                options.Setting("GroupMappingMode", "EntraByName"), "ExternalGroups", StringComparison.OrdinalIgnoreCase)
                ? GroupMappingMode.ExternalGroups
                : GroupMappingMode.EntraByName,
            OtherReadableGroupId = options.Setting("OtherReadableGroupId"),

            CheckpointDirectory = options.Setting("CheckpointDirectory", "state"),
            FullRecrawlEveryRuns = options.Setting("FullRecrawlEveryRuns", 7),
            MaxItemsPerRun = options.Setting("MaxItemsPerRun", 0),
            ItemBudget = options.Setting("ItemBudget", 0),
            MaxErrorRatePercent = options.Setting("MaxErrorRatePercent", 5),

            Kerberos = string.Equals(
                options.Setting("KerberosMode", "Sspi"), "MitKeytab", StringComparison.OrdinalIgnoreCase)
                ? KerberosMode.MitKeytab
                : KerberosMode.Sspi,

            HiveHost = options.Setting("HiveHost"),
            HivePort = options.Setting("HivePort", 10001),
            HiveDriver = options.Setting("HiveDriver", "Cloudera ODBC Driver for Apache Hive"),
            HiveTransport = options.Setting("HiveTransport", "http").ToLowerInvariant(),
            HiveHttpPath = options.Setting("HiveHttpPath", "cliservice"),
            HiveUseSsl = options.Setting("HiveUseSsl", true),
            HiveRealm = options.Setting("HiveRealm"),
            HiveServiceName = options.Setting("HiveServiceName", "hive"),
            HiveExtraOptions = options.Setting("HiveExtraOptions"),
            HiveWatermarkColumn = options.Setting("HiveWatermarkColumn"),
            HiveKeyColumn = options.Setting("HiveKeyColumn"),
        };
    }

    /// <summary>Adds a message for every problem in the settings every CDP connector shares.</summary>
    /// <param name="errors">Accumulator.</param>
    public void ValidateShared(ValidationErrors errors)
    {
        ArgumentNullException.ThrowIfNull(errors);

        errors.RequireRange("Settings:FullRecrawlEveryRuns", this.FullRecrawlEveryRuns, 0, 365);
        errors.RequireRange("Settings:MaxItemsPerRun", this.MaxItemsPerRun, 0, 10000000);
        errors.RequireRange("Settings:ItemBudget", this.ItemBudget, 0, 100000000);
        errors.RequireRange("Settings:MaxErrorRatePercent", this.MaxErrorRatePercent, 0, 100);

        if (this.FullRecrawlEveryRuns == 0)
        {
            // Not an error - some deployments genuinely re-create the connection
            // instead - but it disables the ACL staleness bound, and that is not
            // a thing to turn off by accident.
            errors.Add(
                "Settings:FullRecrawlEveryRuns",
                "is 0, which disables the periodic full recrawl. That is also the only thing that re-derives " +
                "item ACLs after a permission change at the source, because a permission change does not alter " +
                "a file's modification time. Set it to the number of runs you are willing to have stale ACLs " +
                "for, or record the decision in the deployment's risk register and set it to 1.");
        }

        if (this.GroupMapping == GroupMappingMode.EntraByName &&
            !string.IsNullOrWhiteSpace(this.OtherReadableGroupId))
        {
            errors.RequireGuid("Settings:OtherReadableGroupId", this.OtherReadableGroupId);
        }

        if (string.IsNullOrWhiteSpace(this.RangerBaseUrl))
        {
            errors.Add(
                "Settings:RangerBaseUrl",
                "is required. Ranger decides which tables and paths may be indexed at all - a row filter or a " +
                "column mask means the data must be queried live rather than copied into an index - and this " +
                "connector will not index a source whose policies it cannot read.");
        }
        else if (!Uri.TryCreate(this.RangerBaseUrl, UriKind.Absolute, out Uri? ranger) ||
                 ranger.Scheme != Uri.UriSchemeHttps)
        {
            errors.Add("Settings:RangerBaseUrl", "must be an absolute https URL.");
        }
    }

    /// <summary>Adds a message for every problem in the HDFS settings.</summary>
    /// <param name="errors">Accumulator.</param>
    public void ValidateHdfs(ValidationErrors errors)
    {
        ArgumentNullException.ThrowIfNull(errors);

        errors.RequireNonEmpty("Settings:HdfsBaseUrl", this.HdfsBaseUrl);
        errors.RequireRange("Settings:MaxRawFileBytes", (int)Math.Min(this.MaxRawFileBytes, int.MaxValue), 1024, int.MaxValue);
        errors.RequireRange("Settings:ScanSlackSeconds", this.ScanSlackSeconds, 0, 86400);

        if (!string.IsNullOrWhiteSpace(this.HdfsBaseUrl))
        {
            if (!Uri.TryCreate(this.HdfsBaseUrl, UriKind.Absolute, out Uri? parsed))
            {
                errors.Add("Settings:HdfsBaseUrl", "must be an absolute URL.");
            }
            else if (parsed.Scheme != Uri.UriSchemeHttps)
            {
                // A Kerberos ticket travels in the Negotiate header. Over plain
                // HTTP that header, and everything the cluster returns, is on
                // the wire in clear.
                errors.Add(
                    "Settings:HdfsBaseUrl",
                    "must be https. The Kerberos exchange and every byte of file content would otherwise cross " +
                    "the network in clear.");
            }
            else if (!this.HdfsBaseUrl.EndsWith("/webhdfs/v1", StringComparison.OrdinalIgnoreCase))
            {
                errors.Add(
                    "Settings:HdfsBaseUrl",
                    "must end with /webhdfs/v1, for example https://httpfs01.corp:14000/webhdfs/v1. HttpFS and " +
                    "WebHDFS share that path.");
            }
        }

        if (this.HdfsRoots.Count == 0)
        {
            errors.Add(
                "Settings:HdfsRoots",
                "must list at least one absolute HDFS path, separated by semicolons. There is no default: " +
                "crawling / is not a scope decision anyone made.");
        }

        foreach (string root in this.HdfsRoots)
        {
            if (!root.StartsWith('/'))
            {
                errors.Add("Settings:HdfsRoots", $"'{root}' is not an absolute path.");
            }

            if (root.Contains("..", StringComparison.Ordinal))
            {
                errors.Add("Settings:HdfsRoots", $"'{root}' contains '..', which is not a path this will follow.");
            }
        }
    }

    /// <summary>Adds a message for every problem in the Hive or Impala settings.</summary>
    /// <param name="errors">Accumulator.</param>
    public void ValidateHive(ValidationErrors errors)
    {
        ArgumentNullException.ThrowIfNull(errors);

        errors.RequireNonEmpty("Settings:HiveHost", this.HiveHost);
        errors.RequireNonEmpty("Settings:HiveDriver", this.HiveDriver);
        errors.RequireRange("Settings:HivePort", this.HivePort, 1, 65535);
        errors.RequireOneOf("Settings:HiveTransport", this.HiveTransport, "http", "sasl");

        if (!this.HiveUseSsl)
        {
            errors.Add(
                "Settings:HiveUseSsl",
                "is false. A Kerberised HiveServer2 endpoint in a regulated deployment is TLS terminated; " +
                "turning this off puts every row this connector reads on the wire in clear.");
        }

        foreach (string problem in HiveConnectionStringFactory.Inspect(this.HiveExtraOptions))
        {
            errors.Add("Settings:HiveExtraOptions", problem);
        }

        if (!string.IsNullOrWhiteSpace(this.HiveWatermarkColumn) &&
            string.IsNullOrWhiteSpace(this.HiveKeyColumn))
        {
            // Ordering on the watermark alone is not a total order: rows sharing
            // a timestamp come back in an arbitrary order, so a run interrupted
            // inside such a group loses whichever of them had not been written.
            errors.Add(
                "Settings:HiveKeyColumn",
                "is required when Settings:HiveWatermarkColumn is set. Two rows can share a timestamp, so the " +
                "key is what makes the ordering total and the resume point exact.");
        }

        RequireIdentifier(errors, "Settings:HiveWatermarkColumn", this.HiveWatermarkColumn);
        RequireIdentifier(errors, "Settings:HiveKeyColumn", this.HiveKeyColumn);
    }

    private static void RequireIdentifier(ValidationErrors errors, string path, string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return;
        }

        // Concatenated into a query, exactly as the view name is, so it gets the
        // same treatment: an identifier shape, checked before use.
        if (!(char.IsLetter(value[0]) || value[0] == '_') ||
            !value.All(c => char.IsLetterOrDigit(c) || c == '_'))
        {
            errors.Add(
                path,
                "must be a plain column identifier: letters, digits and underscores, not starting with a digit. " +
                "It is concatenated into the query, because a column name cannot be a parameter.");
        }
    }

    private static List<string> SplitList(string value)
    {
        return value
            .Split([';', ','], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToList();
    }
}
