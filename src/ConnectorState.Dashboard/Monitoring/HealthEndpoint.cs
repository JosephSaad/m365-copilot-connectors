// ---------------------------------------------------------------------------
// HealthEndpoint.cs
// GET /health - connection health as JSON, for a monitoring system rather than
// for a person.
//
// The seven pages in Pages/ answer "what is wrong, and where", in HTML somebody
// reads. Nothing here answered "is anything wrong" in a form a scheduled check
// can parse, so the only automated view of this estate was whatever an operator
// remembered to look at. This endpoint is a projection of the same view those
// pages read - crawl.vwConnectionHealth - whose own header in sql/22 says: "This
// is the view a monitoring system polls, and the one whose shape should stay
// stable." This is that sentence implemented, and it is why the endpoint reads
// the view directly rather than calling uspDashboardSummary: that procedure
// exists to give the FRONT PAGE four result sets in one round trip, and three of
// them - a thirty-day trend series, ten recent runs, seven inventory counts -
// are work nothing polling this every sixty seconds would ever look at.
//
// WHO MAY CALL IT: THE READER POLICY. THE SAME RULE, THE SAME OBJECT, AS THE
// PAGES.
//
// The case for anonymous is real and was weighed rather than waved away: a
// monitoring system frequently cannot present Windows credentials, and an
// endpoint the monitor cannot call is an endpoint nobody deploys. It loses on
// what the payload contains. This body names customer connections - identifier,
// display name, how many items each holds, what kind of error the last run
// raised - which is the same information every page on this site is gated on.
// An anonymous /health is an anonymous page with the connection inventory on it,
// and the header of Program.cs says in as many words that there is no anonymous
// page here, and that an anonymous fallback added for local convenience is an
// anonymous fallback that ships.
//
// The objection also does not hold on THIS deployment. The site is IIS with
// Windows authentication on an intranet; a check running as a domain service
// account negotiates like any other client, and `Invoke-WebRequest
// https://host/health -UseDefaultCredentials` under a scheduled task is the
// smallest working version of it. The configuration cost is one more entry in
// CrawlState:ReaderGroups - which is a LIST of names precisely because a reader
// group and an operations group are the normal shape, and adding a service
// account to a group is a change somebody can actually get approved.
//
// If a monitor genuinely cannot authenticate at all, the answer is a SECOND
// endpoint that discloses nothing - no names, no counts, no error kinds, just
// whether this process can reach its database - and that is a different feature
// with its own review. It is not this one widened, because widening this one
// publishes the estate.
//
// AND IT NAMES ITS POLICY RATHER THAN INHERITING IT, BECAUSE THE OBVIOUS
// SHORTHAND WOULD HAVE QUIETLY WIDENED IT. An endpoint carrying no authorization
// metadata falls to options.FallbackPolicy, which Program.cs sets to the reader
// policy - so writing nothing here would also have been correct today. The trap
// is what happens when somebody later makes the requirement explicit the easy
// way: `.RequireAuthorization()` with no argument gives the endpoint metadata,
// the fallback stops applying to it, and the DEFAULT policy takes over. That
// policy is RequireAuthenticatedUser and nothing else. /health would become
// readable by every authenticated user in the domain while
// CrawlState:ReaderGroups still said otherwise and every page still enforced it
// - no error, no log line, and a configuration file that still reads correctly.
// Naming the policy means the endpoint asks for the same object the fallback
// uses, and HealthEndpointTests holds the test that shows those two policies are
// not the same rule.
//
// IT ANSWERS 200 WITH AN UNHEALTHY BODY, AND 503 ONLY WHEN IT CANNOT ANSWER.
//
// A 503 for an unhealthy connection is genuinely easier to alert on - any HTTP
// check goes red with nobody parsing anything - and it was the tempting option.
// It spends the endpoint's only out-of-band signal on a distinction the body
// already makes, and then cannot make the one the body cannot: "a connection is
// failing" and "the dashboard is down" arrive as the same red, and the first
// thing anybody does about the second is open the site, which is also down. It
// also fires on conditions that are not faults - a connection disabled for
// planned maintenance would hold the endpoint at 503 for the whole window, and
// an operator who suppresses a check to do planned work is an operator who
// forgets to unsuppress it.
//
// So: 200 whenever this process read crawl state, whatever it found there, and
// 503 with "status": "unavailable" when it could not. That is the literal
// reading of 503 - this service cannot currently answer - and it is the reading
// a monitor needs, because the one mistake it must never make is treating "no
// answer" as "healthy".
//
// THE SECURITY HEADERS IN Program.cs NEED NO EXCEPTION FOR THIS, AND GET NONE.
// Content-Security-Policy governs what a DOCUMENT may load; a JSON body loads
// nothing, so default-src 'none' costs this endpoint exactly nothing, and
// relaxing it for a response that has no scripts, styles or images in it would
// have been a weakening bought for no benefit at all. The other headers earn
// their place here as much as on the pages: X-Content-Type-Options stops a
// browser deciding an application/json body is HTML, and Cache-Control:
// no-store stops an intermediary answering a monitor with a verdict from ten
// minutes ago - which is the same failure as reporting healthy while the
// database is unreachable, arriving by a different route.
//
// A MONITOR MUST CALL IT OVER HTTPS. Program.cs installs UseHttpsRedirection, so
// an http:// probe receives a 307, and a check configured not to follow
// redirects records that as a failure indistinguishable from the site being
// down. GET only, and deliberately: this endpoint's answer IS its body, and a
// HEAD that returned 200 with nothing in it would tell a monitor only that IIS
// is running, which IIS can already be asked directly.
// ---------------------------------------------------------------------------

namespace ConnectorState.Dashboard.Monitoring;

using ConnectorState.Dashboard.Data;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Logging;

/// <summary>Registers GET /health, the machine-readable projection of connection health.</summary>
public static class HealthEndpoint
{
    /// <summary>The route. A constant so the tests and the log message cannot drift from it.</summary>
    public const string Route = "/health";

    /// <summary>The media type served, including the charset a strict client will look for.</summary>
    public const string ContentType = "application/json; charset=utf-8";

    // A category rather than ILogger<HealthEndpoint>: a static class cannot be a
    // generic type argument, and an arbitrary stand-in type would put a
    // misleading name in the log. This string is what an operator greps for.
    private const string LogCategory = "ConnectorState.Dashboard.Monitoring.HealthEndpoint";

    /// <summary>Maps the health endpoint onto the application's route table.</summary>
    /// <param name="endpoints">The route builder, from Program.cs.</param>
    /// <returns>The same builder, for chaining.</returns>
    /// <remarks>
    /// The policy is required BY NAME. Program.cs registers the object it uses as
    /// the fallback policy under <see cref="ReaderPolicy.PolicyName"/> as well,
    /// so this is the same rule the pages get and not a copy of it - see the file
    /// header for what the no-argument overload would have done instead.
    /// </remarks>
    public static IEndpointRouteBuilder MapConnectionHealth(this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);

        endpoints
            .MapGet(Route, HandleAsync)
            .RequireAuthorization(ReaderPolicy.PolicyName);

        return endpoints;
    }

    /// <summary>Reads the health view, projects it and writes the payload.</summary>
    /// <param name="context">The request.</param>
    /// <param name="queries">The read surface.</param>
    /// <param name="loggerFactory">Logging, for the case where crawl state cannot be read.</param>
    /// <returns>A task that completes when the response has been written.</returns>
    private static async Task HandleAsync(
        HttpContext context,
        CrawlStateQueries queries,
        ILoggerFactory loggerFactory)
    {
        // One timestamp for the whole payload, taken before the query rather than
        // after it: on a slow database the difference is the query's duration,
        // and a monitor computing staleness from this should be told when the
        // answer was asked for.
        DateTime generatedUtc = DateTime.UtcNow;

        HealthReport report;

        try
        {
            IReadOnlyList<ConnectionHealthRow> rows =
                await queries.ListConnectionHealthAsync(context.RequestAborted);

            report = HealthProjection.Build(rows, generatedUtc);
        }
        catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested)
        {
            // The caller hung up. That is not this service failing, and dressing
            // it up as a 503 would put a database fault in the record for
            // something that was a monitor's own timeout. Every page here
            // behaves the same way for the same event.
            throw;
        }
        catch (Exception ex)
        {
            // Broad on purpose. The contract of this endpoint is that it always
            // answers in JSON with a status a monitor can read; an exception
            // escaping here would instead be caught by UseExceptionHandler and
            // re-executed as /Error, which returns an HTML page to something
            // that can only parse JSON - and the monitor would record a parse
            // failure rather than "unavailable".
            //
            // The detail goes to the log and NOT to the caller. Program.cs makes
            // the same call for the error page and gives the reason: a stack
            // trace from a monitoring dashboard names the database, the schema
            // and the procedure. The operator needs that; whatever is polling
            // does not, and cannot act on it.
            loggerFactory
                .CreateLogger(LogCategory)
                .LogError(ex, "GET {Route} could not read crawl state; answering 503.", Route);

            report = HealthProjection.Unavailable(generatedUtc);
        }

        // Serialized in full before anything is written, so the status code is
        // never already on the wire when serialization fails. A partially
        // written body with a 200 in front of it is the one outcome a consumer
        // cannot recover from.
        string body = HealthProjection.Serialize(report);

        context.Response.StatusCode = report.StatusCode;
        context.Response.ContentType = ContentType;

        await context.Response.WriteAsync(body, context.RequestAborted);
    }
}
