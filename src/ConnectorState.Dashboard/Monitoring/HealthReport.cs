// ---------------------------------------------------------------------------
// HealthReport.cs
// The wire shape of GET /health. This file IS the contract.
//
// Every property carries an explicit [JsonPropertyName]. That is not decoration
// and it is not a style preference: a naming policy set on the serializer would
// mean the names on the wire are decided somewhere else, and somebody changing
// that one option - or adding a second serializer with a different one - would
// silently rename every field for every consumer at once. Written here, a field
// can only be renamed by editing the line that names it, next to the comment
// saying who reads it.
//
// The dictionary keys are the exception that proves it. ByHealth is keyed by the
// database's own words, spaces and all - "deletes pending", "items refused" -
// and a camel-case naming policy would have mangled those into "deletesPending"
// on the way out, which is a word no page shows, no view returns and nothing in
// sql/22 could ever produce. HealthEndpointTests asserts the raw text of the
// payload for exactly that reason.
//
// NULL IS A VALUE HERE, NOT AN OMISSION. MinutesSinceLastSuccess is null for a
// connection that has never succeeded, which is not "0 minutes ago" - the header
// of CrawlStateModels.cs makes the same point about the same column. The
// serializer is therefore configured to write nulls rather than skip them: a
// consumer that reads a missing key as zero would report a connection that has
// never once worked as the freshest thing in the estate.
//
// ADDING A FIELD IS SAFE; RENAMING OR REMOVING ONE IS NOT. Whatever polls this
// is a scheduled check somebody wrote once and will not revisit, and it breaks
// silently - a check that can no longer find its field usually evaluates to "not
// alerting" rather than to an error. Treat the names below the way sql/22 treats
// the shape of vwConnectionHealth.
// ---------------------------------------------------------------------------

namespace ConnectorState.Dashboard.Monitoring;

using System.Text.Json.Serialization;

/// <summary>The whole payload of GET /health.</summary>
public sealed record HealthReport
{
    /// <summary>The roll-up when nothing is failing or degraded.</summary>
    public const string Ok = "ok";

    /// <summary>The roll-up when something wants looking at but is not down.</summary>
    public const string Warning = "warning";

    /// <summary>The roll-up when at least one connection is failing.</summary>
    public const string Critical = "critical";

    /// <summary>
    /// The roll-up when this process could not read crawl state at all. NOT a
    /// verdict about any connection: it means the question was not answered.
    /// </summary>
    public const string Unavailable = "unavailable";

    /// <summary>
    /// Gets the one field a monitor that can only threshold one field should
    /// threshold: ok, warning, critical or unavailable.
    /// </summary>
    /// <remarks>
    /// DELIBERATELY NOT ONE OF THE DATABASE'S WORDS. The view's vocabulary is
    /// healthy / failing / late / items refused / deletes pending / never run /
    /// running / disabled, and this field's is the four above. They are disjoint
    /// so that a consumer, a log line or a person reading a payload can never
    /// mistake the estate-wide roll-up for one connection's health - which is
    /// the confusion a shared word list would invite the first time somebody
    /// grepped for "healthy".
    /// </remarks>
    [JsonPropertyName("status")]
    public string Status { get; init; } = Ok;

    /// <summary>
    /// Gets when this payload was built, in UTC, with an explicit Z.
    /// </summary>
    /// <remarks>
    /// A monitor cannot otherwise tell a live answer from one an intermediary
    /// cached. Program.cs sends Cache-Control: no-store, which is the control
    /// that should make that impossible - this is the evidence that it did.
    /// </remarks>
    [JsonPropertyName("generatedUtc")]
    public DateTime GeneratedUtc { get; init; }

    /// <summary>Gets the number of registered connections, enabled or not.</summary>
    [JsonPropertyName("connectionCount")]
    public int ConnectionCount { get; init; }

    /// <summary>Gets how many of them are enabled.</summary>
    [JsonPropertyName("enabledCount")]
    public int EnabledCount { get; init; }

    /// <summary>
    /// Gets the largest ConsecutiveFailures across every connection, so an alert
    /// can be written as a threshold on one number without parsing the array.
    /// </summary>
    /// <remarks>
    /// A MAX over a column the payload already publishes, and nothing more. It
    /// is not a second opinion about health: the view counts consecutive
    /// failures back to the last success, which is what "consecutive" has to
    /// mean for an alert rule to be worth writing, and that counting happens in
    /// sql/22 rather than here.
    /// </remarks>
    [JsonPropertyName("maxConsecutiveFailures")]
    public int MaxConsecutiveFailures { get; init; }

    /// <summary>
    /// Gets a count per health word, keyed by the word the database returned.
    /// </summary>
    /// <remarks>
    /// THE FIELD A CAREFUL MONITOR SHOULD ACTUALLY USE. Status is a convenience
    /// and it compresses; this does not. The keys are whatever
    /// crawl.vwConnectionHealth said, so a word added to that view in a future
    /// sql/ script appears here under its own name on the next poll, with no
    /// build of this application involved - which is the one property a roll-up
    /// computed in C# can never have.
    ///
    /// Sorted ordinally so two consecutive polls differ only where the estate
    /// did. An unordered dictionary would make every poll look like a change to
    /// anything diffing the body.
    /// </remarks>
    [JsonPropertyName("byHealth")]
    public IReadOnlyDictionary<string, int> ByHealth { get; init; } =
        new SortedDictionary<string, int>(StringComparer.Ordinal);

    /// <summary>Gets one entry per connection, ordered by connection identifier.</summary>
    [JsonPropertyName("connections")]
    public IReadOnlyList<HealthConnection> Connections { get; init; } = Array.Empty<HealthConnection>();

    /// <summary>
    /// Gets the HTTP status this report is served with: 503 when crawl state
    /// could not be read, 200 in every other case including a failing estate.
    /// </summary>
    /// <remarks>
    /// Here rather than in the endpoint delegate so the decision is a property
    /// with a test on it instead of a line inside a lambda. The reasoning is in
    /// the header of HealthEndpoint.cs; the short version is that an unhealthy
    /// connection and a dead dashboard must not arrive as the same red.
    /// </remarks>
    [JsonIgnore]
    public int StatusCode => this.Status == Unavailable ? 503 : 200;
}

/// <summary>One connection, as a monitoring system needs it.</summary>
public sealed record HealthConnection
{
    /// <summary>Gets the connection identifier, as the connector registered it.</summary>
    [JsonPropertyName("connectionId")]
    public string ConnectionId { get; init; } = string.Empty;

    /// <summary>Gets the display name.</summary>
    [JsonPropertyName("displayName")]
    public string DisplayName { get; init; } = string.Empty;

    /// <summary>Gets which connector owns this connection.</summary>
    [JsonPropertyName("connectorKey")]
    public string ConnectorKey { get; init; } = string.Empty;

    /// <summary>Gets a value indicating whether the connection is enabled.</summary>
    [JsonPropertyName("enabled")]
    public bool Enabled { get; init; }

    /// <summary>
    /// Gets the health word, verbatim from crawl.vwConnectionHealth.
    /// </summary>
    /// <remarks>
    /// Not normalised, not capitalised, not translated into a code. The view
    /// computes it in one CASE expression so that every consumer agrees on the
    /// rule, and the fastest way to break that is for one consumer to tidy the
    /// output on the way past.
    /// </remarks>
    [JsonPropertyName("health")]
    public string Health { get; init; } = string.Empty;

    /// <summary>Gets the most recent non-dry run's identifier, or null if there has never been one.</summary>
    [JsonPropertyName("lastRunId")]
    public long? LastRunId { get; init; }

    /// <summary>Gets that run's status: running, succeeded, failed, abandoned or partial.</summary>
    [JsonPropertyName("lastRunStatus")]
    public string? LastRunStatus { get; init; }

    /// <summary>Gets when that run started, in UTC.</summary>
    [JsonPropertyName("lastRunStartedUtc")]
    public DateTime? LastRunStartedUtc { get; init; }

    /// <summary>Gets when the last SUCCEEDED run completed, in UTC, or null if none ever has.</summary>
    [JsonPropertyName("lastSuccessUtc")]
    public DateTime? LastSuccessUtc { get; init; }

    /// <summary>
    /// Gets the freshness measure, in minutes, against the last success rather
    /// than the last run. Null when there has never been a success.
    /// </summary>
    /// <remarks>
    /// Null is the whole point of the column. A connection failing every fifteen
    /// minutes is punctual and broken, and a monitor thresholding this against
    /// the last RUN would call it fresh; a monitor coalescing this null to zero
    /// would call a connection that has never worked the freshest in the estate.
    /// Both mistakes are why the value arrives null and stays null.
    /// </remarks>
    [JsonPropertyName("minutesSinceLastSuccess")]
    public int? MinutesSinceLastSuccess { get; init; }

    /// <summary>
    /// Gets the interval this connection is expected to run at, or null if none
    /// is configured. Published so a monitor can read MinutesSinceLastSuccess
    /// against the schedule rather than against a number somebody guessed.
    /// </summary>
    [JsonPropertyName("expectedIntervalMinutes")]
    public int? ExpectedIntervalMinutes { get; init; }

    /// <summary>Gets the failures since the last success.</summary>
    [JsonPropertyName("consecutiveFailures")]
    public int ConsecutiveFailures { get; init; }

    /// <summary>Gets how many items the inventory believes are live.</summary>
    [JsonPropertyName("liveItems")]
    public int LiveItems { get; init; }

    /// <summary>
    /// Gets how many deletes Graph has not confirmed. Non-zero for a few seconds
    /// of every run and persistently non-zero only when a DELETE keeps being
    /// refused - so it is the number that separates the two, which the single
    /// health word cannot.
    /// </summary>
    [JsonPropertyName("pendingDeletes")]
    public int PendingDeletes { get; init; }

    /// <summary>Gets the short stable failure token from the last run, or null.</summary>
    /// <remarks>
    /// THE TOKEN, AND NOT THE MESSAGE. ErrorMessage beside it in the view is
    /// operator-facing prose: unbounded in length, and rewritten whenever
    /// somebody improves the wording. A check keyed on prose breaks on an edit
    /// nobody thought was a behaviour change, and a payload carrying prose from
    /// every connection has no size anybody has bounded. The message is on the
    /// connection page, where a person is reading it.
    /// </remarks>
    [JsonPropertyName("errorKind")]
    public string? ErrorKind { get; init; }
}
