// ---------------------------------------------------------------------------
// HealthProjection.cs
// Rows out of crawl.vwConnectionHealth in, one HealthReport out. No SQL, no
// HttpContext, no clock - which is what makes every decision in it testable
// without a database or a web server.
//
// THIS FILE DECIDES NOTHING ABOUT WHETHER A CONNECTION IS HEALTHY, and that is
// the constraint the whole endpoint hangs on. Health is one CASE expression in
// sql/22, deliberately a single computed word rather than five columns a
// consumer has to combine, because the combining is where every estate invents
// its own slightly different rule. A second rule written here would be a rule
// that can disagree with the pages - and it would disagree quietly, on the one
// afternoon somebody is comparing the alert to the dashboard and cannot work out
// which is lying. Every word below is carried through unchanged.
//
// THE ONE JUDGEMENT IT DOES MAKE IS RANKING, AND IT BORROWS IT RATHER THAN
// INVENTING IT. A roll-up needs to know that "failing" is worse than "late", and
// the view does not say so. StateCodes.Tone already answers exactly that
// question for the pages - it is what decides whether a pill is red, amber or
// grey - so the roll-up asks the same function. The endpoint and the page
// therefore cannot rank a word differently: there is one function, and both call
// it. Writing a private switch here instead would have produced a second list of
// health words in C#, and this repository already has evidence for how that
// ends. When sql/29 added the 'partial' run status and the 'items refused'
// health word, StateCodes.Tone was updated, the ORDER BY in uspDashboardSummary
// was not - 'items refused' falls through its CASE to ELSE 7 and sorts BELOW
// 'healthy' on the front page - and the doc comment on ConnectionHealthRow.Health
// still lists seven words without it. Two of the three copies drifted within one
// change.
//
// A WORD THIS BUILD HAS NEVER SEEN IS NOT AN ERROR AND IS NOT DROPPED. It is
// published verbatim in ByHealth and on its connection, and it does not move
// Status, because inventing a severity for a word nobody has ranked is precisely
// the second source of truth this file exists to avoid. That is survivable only
// because ByHealth exists: a monitor keyed on it sees the new word on the first
// poll after the deployment, without a build of this application. A monitor
// keyed only on Status would not - which is the reason the header of
// HealthReport.ByHealth tells consumers to use it.
//
// AN ESTATE WITH NO CONNECTIONS IN IT IS A WARNING, NOT AN "OK". Zero rows is
// what an empty crawl.Connection looks like, and it is also what a dashboard
// pointed at a freshly created, never-populated database looks like. Reporting
// ok would mean the healthiest possible answer is the one returned when nothing
// at all is being watched, and a monitor would sit green over a database that
// had lost its registrations. This is the same failure mode the delete-sweep
// evidence in GO-LIVE-READINESS ran into from the other side: zero rows means
// "measured nothing" at least as often as it means "nothing wrong", and the two
// must not render identically.
// ---------------------------------------------------------------------------

namespace ConnectorState.Dashboard.Monitoring;

using System.Text.Json;
using ConnectorState.Dashboard.Data;
using ConnectorState.Dashboard.Presentation;

/// <summary>Builds the JSON payload of GET /health from the health view's rows.</summary>
public static class HealthProjection
{
    // Ranks, used only to take the worst. Not published: a consumer gets words.
    private const int RankOk = 0;
    private const int RankWarning = 1;
    private const int RankCritical = 2;

    private static readonly JsonSerializerOptions Options = new()
    {
        // Indented, for a payload of a handful of connections. The first person
        // to read this body is not the monitor - it is whoever curls the URL at
        // 03:00 to find out what the alert meant, and a wall of one-line JSON in
        // a console is the worst possible answer to that question. It also makes
        // two polls diff line by line, which is how a change gets spotted.
        WriteIndented = true,

        // Nulls are written, never skipped. See HealthReport: a missing
        // minutesSinceLastSuccess read as zero turns "has never succeeded" into
        // "succeeded moments ago", and that is the one direction this payload
        // must not be wrong in.
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.Never,

        // The DEFAULT encoder, deliberately. UnsafeRelaxedJsonEscaping would
        // produce prettier display names and would also stop escaping the
        // characters that matter if this body is ever pasted into a page, a
        // ticket template or a chat card - all of which happen to monitoring
        // payloads. Display names come from a customer's connection
        // registration; they are not this application's text to trust.
        // Property names here are ASCII and unaffected either way.
    };

    /// <summary>Projects the health view's rows into the monitoring payload.</summary>
    /// <param name="rows">Every row of crawl.vwConnectionHealth, in any order.</param>
    /// <param name="generatedUtc">The time to stamp the payload with, in UTC.</param>
    /// <returns>The report, ready to serialize.</returns>
    public static HealthReport Build(IReadOnlyList<ConnectionHealthRow> rows, DateTime generatedUtc)
    {
        ArgumentNullException.ThrowIfNull(rows);

        var byHealth = new SortedDictionary<string, int>(StringComparer.Ordinal);
        var connections = new List<HealthConnection>(rows.Count);

        int worst = RankOk;
        int enabled = 0;
        int maxConsecutiveFailures = 0;

        foreach (ConnectionHealthRow row in rows)
        {
            string health = row.Health;

            byHealth[health] = byHealth.TryGetValue(health, out int seen) ? seen + 1 : 1;

            if (row.IsEnabled)
            {
                enabled++;
            }

            if (row.ConsecutiveFailures > maxConsecutiveFailures)
            {
                maxConsecutiveFailures = row.ConsecutiveFailures;
            }

            int rank = Rank(health);

            if (rank > worst)
            {
                worst = rank;
            }

            connections.Add(new HealthConnection
            {
                ConnectionId = row.ConnectionId,
                DisplayName = row.DisplayName,
                ConnectorKey = row.ConnectorKey,
                Enabled = row.IsEnabled,
                Health = health,
                LastRunId = row.LastRunId,
                LastRunStatus = row.LastRunStatus,
                LastRunStartedUtc = AsUtc(row.LastRunStartedUtc),
                LastSuccessUtc = AsUtc(row.LastSuccessUtc),
                MinutesSinceLastSuccess = row.MinutesSinceLastSuccess,
                ExpectedIntervalMinutes = row.ExpectedIntervalMinutes,
                ConsecutiveFailures = row.ConsecutiveFailures,
                LiveItems = row.LiveItemCount,
                PendingDeletes = row.PendingDeleteCount,
                ErrorKind = row.ErrorKind,
            });
        }

        // By identifier, not by severity. sql/24 hands the FRONT PAGE its
        // connections worst-first, which is right for somebody scanning a screen
        // and wrong for something diffing two payloads: a connection that starts
        // failing would move up the array and make every row below it look
        // changed. The identifier is stable, ordinal and unique, so consecutive
        // polls line up. Ordinal rather than culture-aware for the reason
        // Format.cs gives about invariant culture: the order must not depend on
        // the locale of whatever happens to be running the app pool.
        connections.Sort(static (left, right) =>
            string.CompareOrdinal(left.ConnectionId, right.ConnectionId));

        return new HealthReport
        {
            // See the file header: no rows is not the healthiest answer, it is
            // the answer that means nothing is being watched.
            Status = rows.Count == 0 ? HealthReport.Warning : Word(worst),
            GeneratedUtc = DateTime.SpecifyKind(generatedUtc, DateTimeKind.Utc),
            ConnectionCount = rows.Count,
            EnabledCount = enabled,
            MaxConsecutiveFailures = maxConsecutiveFailures,
            ByHealth = byHealth,
            Connections = connections,
        };
    }

    /// <summary>Builds the payload served when crawl state could not be read at all.</summary>
    /// <param name="generatedUtc">The time to stamp the payload with, in UTC.</param>
    /// <returns>A report whose status is "unavailable" and whose StatusCode is 503.</returns>
    /// <remarks>
    /// The shape is identical to a successful report - same fields, all present,
    /// counts at zero - because a consumer that has to branch on which fields
    /// exist will get that branch wrong. It distinguishes this from an empty
    /// estate by the status word and by the 503, and never by a missing key.
    /// </remarks>
    public static HealthReport Unavailable(DateTime generatedUtc) => new()
    {
        Status = HealthReport.Unavailable,
        GeneratedUtc = DateTime.SpecifyKind(generatedUtc, DateTimeKind.Utc),
    };

    /// <summary>Serializes a report to the exact bytes the endpoint writes.</summary>
    /// <param name="report">The report.</param>
    /// <returns>The JSON body.</returns>
    public static string Serialize(HealthReport report) => JsonSerializer.Serialize(report, Options);

    /// <summary>
    /// Ranks a health word by severity, using the same function that colours the
    /// pill beside it on the pages.
    /// </summary>
    /// <param name="health">The word, verbatim from crawl.vwConnectionHealth.</param>
    /// <returns>The internal rank. Anything unranked is <see cref="RankOk"/>.</returns>
    /// <remarks>
    /// The tones are the page's vocabulary: ok, warn, bad, busy, idle. Mapped
    /// here as bad -> critical and warn -> warning, and everything else
    /// contributes nothing to the roll-up.
    ///
    /// "busy" is a run in progress, which must not raise anything - a monitor
    /// that pages while the connector is working is a monitor somebody turns
    /// off. "idle" covers disabled and never run: a connection disabled on
    /// purpose is not a fault, and holding the roll-up amber through planned
    /// maintenance is how a check ends up suppressed and then left suppressed.
    /// It is also what an unrecognised word maps to, which is discussed in the
    /// file header.
    ///
    /// This is a rank, not a threshold, and it does not agree with everything on
    /// the front page. 'deletes pending' is amber on its pill and IS counted
    /// here, while ConnectionsNeedingAttention in sql/24 counts only failing,
    /// late and items refused. Following the pill was the deliberate choice -
    /// one C# function ranks words, and it is the one the pages already use -
    /// but a pending delete is normal for a few seconds of every run, so a
    /// monitor that must not flap should page on byHealth.failing or on
    /// maxConsecutiveFailures rather than on status alone, and read
    /// connections[].pendingDeletes to tell a stuck delete from a running one.
    /// </remarks>
    private static int Rank(string? health)
    {
        return StateCodes.Tone(health) switch
        {
            "bad" => RankCritical,
            "warn" => RankWarning,
            _ => RankOk,
        };
    }

    private static string Word(int rank)
    {
        return rank switch
        {
            RankCritical => HealthReport.Critical,
            RankWarning => HealthReport.Warning,
            _ => HealthReport.Ok,
        };
    }

    /// <summary>Stamps a timestamp from the database as UTC.</summary>
    /// <param name="value">The value, or null.</param>
    /// <returns>The same instant, with its kind stated, or null.</returns>
    /// <remarks>
    /// SpecifyKind, NOT ToUniversalTime, and the difference is a wrong answer
    /// rather than a style choice. SqlDataReader returns DATETIME2 with
    /// DateTimeKind.Unspecified; the schema stores UTC and nothing else, so the
    /// instant is already right and only its label is missing. ToUniversalTime
    /// would read an Unspecified value as LOCAL time and shift it by the app
    /// pool machine's offset - an hour out every summer on a UK server, and
    /// silently, because the result is still a plausible timestamp.
    ///
    /// Stating the kind is what makes System.Text.Json emit the trailing Z. An
    /// Unspecified DateTime serializes as "2026-08-30T09:14:02" with no zone at
    /// all, and every consumer that parses that will apply its own local zone to
    /// it. On a page this problem does not exist, because Format.cs renders the
    /// text and the column header says UTC; a machine has no column header.
    /// </remarks>
    private static DateTime? AsUtc(DateTime? value)
    {
        return value.HasValue ? DateTime.SpecifyKind(value.Value, DateTimeKind.Utc) : null;
    }
}
