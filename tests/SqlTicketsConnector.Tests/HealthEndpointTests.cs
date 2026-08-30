// ---------------------------------------------------------------------------
// HealthEndpointTests.cs
// What GET /health promises to whatever is polling it.
//
// WHY THESE EXIST AS UNIT TESTS. The same reason DashboardAuthorizationTests
// does: the honest live test is to publish to IIS, point a check at the URL as a
// service account and again as a non-member, and read both answers. That needs
// elevation and a second identity, and it has not been run. So the tests below
// are evidence about the decisions this application makes, not about what
// negotiate puts on a token or what IIS does with a 307.
//
// A monitoring endpoint fails in a direction ordinary code does not. Almost
// every mistake available here - a null read as a zero, a timestamp without a
// zone, a health word this build has never seen, a database that cannot be
// reached - produces a payload that parses, contains plausible numbers, and
// says nothing is wrong. Nobody investigates a green check. So most of what
// follows is not "does it work"; it is "when it is wrong, does it say so".
//
// The tests fall into four groups:
//
//   1. The words are the database's. Every health word is carried through
//      verbatim, including one this build has never heard of, because the rule
//      that produces them is one CASE expression in sql/22 and a second opinion
//      in C# would be a second answer for somebody to find during an incident.
//
//   2. The roll-up is ranked by the function that colours the pages. The table
//      in Roll_up_ranks_each_word_the_way_the_page_colours_it pins the pill
//      tone and the status word together on purpose: change StateCodes.Tone and
//      the test fails until somebody has decided, deliberately, what the alert
//      should now do.
//
//   3. Nulls, zones and shapes - the four ways a payload lies quietly.
//
//   4. The authorization decision, including the specific mistake that would
//      have published this to every authenticated user in the domain without
//      anybody noticing.
// ---------------------------------------------------------------------------

namespace SqlTicketsConnector.Tests
{
    using System;
    using System.Collections.Generic;
    using System.Linq;
    using System.Security.Claims;
    using System.Threading.Tasks;
    using global::ConnectorState.Dashboard;
    using global::ConnectorState.Dashboard.Data;
    using global::ConnectorState.Dashboard.Monitoring;
    using global::ConnectorState.Dashboard.Presentation;
    using Microsoft.AspNetCore.Authorization;
    using Microsoft.AspNetCore.Builder;
    using Microsoft.AspNetCore.Http;
    using Microsoft.AspNetCore.Routing;
    using Microsoft.Extensions.DependencyInjection;
    using Xunit;

    public class HealthEndpointTests
    {
        /// <summary>A fixed instant, so a serialized payload can be asserted character for character.</summary>
        private static readonly DateTime When = new DateTime(2026, 8, 30, 9, 14, 2, DateTimeKind.Utc);

        /// <summary>
        /// Every word the Health CASE in crawl.vwConnectionHealth can produce, in
        /// the order its arms are written. Copied from sql/22 rather than from
        /// any C# file, because the point of the tests using it is that no C#
        /// file holds this list.
        /// </summary>
        private static readonly string[] ViewWords =
        {
            "disabled", "never run", "running", "failing",
            "items refused", "late", "deletes pending", "healthy",
        };

        /* ===================================================================
           1. The words belong to the database.
           =================================================================== */

        [Fact]
        public void Every_word_the_view_can_return_is_published_verbatim()
        {
            // Not title-cased, not hyphenated into an identifier, not mapped to
            // a code. A consumer that has to reverse a transformation to get
            // back to what the page shows is a consumer that will get it wrong
            // once and then disagree with the dashboard forever.
            HealthReport report = HealthProjection.Build(RowPerWord(), When);

            Assert.Equal(
                ViewWords.OrderBy(w => w, StringComparer.Ordinal).ToArray(),
                report.ByHealth.Keys.ToArray());

            foreach (string word in ViewWords)
            {
                Assert.Equal(1, report.ByHealth[word]);
                Assert.Contains(report.Connections, c => c.Health == word);
            }
        }

        [Fact]
        public void A_word_this_build_has_never_seen_is_published_rather_than_dropped()
        {
            // The one thing that must not happen when sql/31 adds a health word:
            // the endpoint silently omitting it, or mapping it to something it
            // recognises. Either way a monitor stays green over a state nobody
            // has ever looked at.
            //
            // It does not move the roll-up, and that is deliberate rather than
            // an oversight - inventing a severity for a word nobody has ranked
            // is exactly the second source of truth this endpoint avoids. It is
            // survivable only because byHealth carries the word itself, which is
            // why the payload has that field and why consumers are told to use
            // it.
            HealthReport report = HealthProjection.Build(
                new[] { Row("a", health: "quarantined") },
                When);

            Assert.Equal(1, report.ByHealth["quarantined"]);
            Assert.Equal("quarantined", report.Connections[0].Health);
            Assert.Equal(HealthReport.Ok, report.Status);

            Assert.Contains("\"quarantined\": 1", HealthProjection.Serialize(report), StringComparison.Ordinal);
        }

        [Fact]
        public void A_health_word_with_a_space_in_it_keeps_the_space_as_a_json_key()
        {
            // A camel-case naming policy on the serializer would rewrite these
            // keys as "deletesPending" and "itemsRefused" - words no view
            // returns, no page shows and nothing in sql/22 could produce. The
            // payload uses explicit property names and sets no policy; this is
            // the test that notices if that ever changes.
            string json = HealthProjection.Serialize(HealthProjection.Build(RowPerWord(), When));

            Assert.Contains("\"deletes pending\": 1", json, StringComparison.Ordinal);
            Assert.Contains("\"items refused\": 1", json, StringComparison.Ordinal);
            Assert.Contains("\"never run\": 1", json, StringComparison.Ordinal);
            Assert.DoesNotContain("deletesPending", json, StringComparison.Ordinal);
            Assert.DoesNotContain("itemsRefused", json, StringComparison.Ordinal);
        }

        /* ===================================================================
           2. The roll-up borrows the page's ranking.
           =================================================================== */

        [Theory]
        [InlineData("healthy", "ok", HealthReport.Ok)]
        [InlineData("running", "busy", HealthReport.Ok)]
        [InlineData("never run", "idle", HealthReport.Ok)]
        [InlineData("disabled", "idle", HealthReport.Ok)]
        [InlineData("late", "warn", HealthReport.Warning)]
        [InlineData("deletes pending", "warn", HealthReport.Warning)]
        [InlineData("items refused", "warn", HealthReport.Warning)]
        [InlineData("failing", "bad", HealthReport.Critical)]
        public void Roll_up_ranks_each_word_the_way_the_page_colours_it(
            string word,
            string expectedTone,
            string expectedStatus)
        {
            // BOTH columns are asserted on purpose. The endpoint ranks words by
            // calling StateCodes.Tone - the same function that decides whether a
            // pill is red, amber or grey - so that an alert and the dashboard it
            // sends somebody to cannot disagree. Pinning the tone here as well
            // as the status means a change to Tone fails this test rather than
            // quietly re-tuning every alert in the estate: whoever makes it has
            // to say what the monitor should now do.
            Assert.Equal(expectedTone, StateCodes.Tone(word));

            HealthReport report = HealthProjection.Build(new[] { Row("a", health: word) }, When);

            Assert.Equal(expectedStatus, report.Status);
        }

        [Fact]
        public void The_worst_connection_decides_the_roll_up_and_not_the_last_one()
        {
            // Ordered so the failing connection sorts FIRST by identifier and
            // the healthy ones after it, which is the arrangement that catches a
            // roll-up written as "whatever the final row said".
            HealthReport report = HealthProjection.Build(
                new[]
                {
                    Row("a-broken", health: "failing", consecutiveFailures: 3),
                    Row("b-late", health: "late"),
                    Row("c-fine", health: "healthy"),
                },
                When);

            Assert.Equal(HealthReport.Critical, report.Status);
            Assert.Equal(3, report.MaxConsecutiveFailures);
        }

        [Fact]
        public void A_connection_disabled_on_purpose_does_not_raise_the_roll_up()
        {
            // Planned maintenance is not an incident. An endpoint that goes
            // amber for the duration of it is an endpoint whose check gets
            // suppressed, and a suppressed check is one nobody re-enables.
            HealthReport report = HealthProjection.Build(
                new[] { Row("a", health: "disabled", enabled: false), Row("b", health: "healthy") },
                When);

            Assert.Equal(HealthReport.Ok, report.Status);
            Assert.Equal(2, report.ConnectionCount);
            Assert.Equal(1, report.EnabledCount);
        }

        [Fact]
        public void A_run_in_progress_does_not_raise_the_roll_up()
        {
            // The connector working is not the connector failing. A monitor that
            // pages every time a crawl starts is one somebody turns off, and
            // then it is not a monitor.
            HealthReport report = HealthProjection.Build(
                new[] { Row("a", health: "running", lastRunStatus: "running") },
                When);

            Assert.Equal(HealthReport.Ok, report.Status);
        }

        [Fact]
        public void An_estate_with_no_connections_in_it_is_not_reported_as_ok()
        {
            // Zero rows is what a dashboard pointed at a fresh, never-populated
            // database looks like, and what a database that has lost its
            // registrations looks like. Reporting ok would make the healthiest
            // answer available the one returned when nothing at all is being
            // watched - which is the same "measured nothing, called it clean"
            // failure this repository has already been bitten by from the
            // delete-sweep side.
            HealthReport report = HealthProjection.Build(Array.Empty<ConnectionHealthRow>(), When);

            Assert.Equal(HealthReport.Warning, report.Status);
            Assert.Equal(0, report.ConnectionCount);
            Assert.Empty(report.Connections);

            // And still a 200: nothing failed, there is simply nothing there,
            // and the body says which.
            Assert.Equal(200, report.StatusCode);
        }

        /* ===================================================================
           3. Nulls, zones, order and shape.
           =================================================================== */

        [Fact]
        public void Timestamps_are_published_as_UTC_with_an_explicit_Z()
        {
            // THE TRAP THIS TEST EXISTS FOR. SqlDataReader hands back DATETIME2
            // as DateTimeKind.Unspecified, and System.Text.Json serializes an
            // Unspecified DateTime with no zone at all - "2026-08-29T22:15:00".
            // Every consumer then applies its own local zone to a value the
            // schema only ever stores in UTC, and the error is an hour or eight,
            // silently, on a timestamp that still looks completely reasonable.
            //
            // The pages do not have this problem: Format.cs renders the text and
            // the column header says UTC. A machine has no column header.
            HealthReport report = HealthProjection.Build(
                new[]
                {
                    Row(
                        "a",
                        lastRunStartedUtc: Unspecified(2026, 8, 30, 8, 45, 0),
                        lastSuccessUtc: Unspecified(2026, 8, 29, 22, 15, 0)),
                },
                When);

            string json = HealthProjection.Serialize(report);

            Assert.Contains("\"lastRunStartedUtc\": \"2026-08-30T08:45:00Z\"", json, StringComparison.Ordinal);
            Assert.Contains("\"lastSuccessUtc\": \"2026-08-29T22:15:00Z\"", json, StringComparison.Ordinal);
            Assert.Contains("\"generatedUtc\": \"2026-08-30T09:14:02Z\"", json, StringComparison.Ordinal);

            // The instant is unchanged, only its label added. ToUniversalTime
            // would have shifted an already-UTC value by the app pool machine's
            // offset - an hour out every summer on a UK server.
            Assert.Equal(new DateTime(2026, 8, 30, 8, 45, 0), report.Connections[0].LastRunStartedUtc.Value);
        }

        [Fact]
        public void Minutes_since_last_success_is_published_as_null_and_never_as_zero()
        {
            // Null means the connection has never once succeeded. Zero means it
            // succeeded this minute. A payload that omits the field, or writes
            // 0, tells a monitor thresholding on staleness that the connection
            // that has never worked is the freshest thing in the estate.
            HealthReport report = HealthProjection.Build(
                new[] { Row("a", health: "never run", minutesSinceLastSuccess: null) },
                When);

            Assert.Null(report.Connections[0].MinutesSinceLastSuccess);

            string json = HealthProjection.Serialize(report);

            Assert.Contains("\"minutesSinceLastSuccess\": null", json, StringComparison.Ordinal);
            Assert.Contains("\"lastSuccessUtc\": null", json, StringComparison.Ordinal);
            Assert.Contains("\"lastRunId\": null", json, StringComparison.Ordinal);
        }

        [Fact]
        public void The_payload_carries_the_error_kind_and_not_the_error_message()
        {
            // The kind is a short stable token a check can route on. The message
            // is operator-facing prose - unbounded, and rewritten whenever
            // somebody improves the wording, which is not a change anybody
            // thinks of as breaking a monitoring contract. It stays on the
            // connection page, where a person is reading it.
            HealthReport report = HealthProjection.Build(
                new[]
                {
                    Row(
                        "a",
                        health: "failing",
                        errorKind: "graph-throttled",
                        errorMessage: "SENTINEL-the-prose-message-that-must-not-ship"),
                },
                When);

            string json = HealthProjection.Serialize(report);

            Assert.Contains("\"errorKind\": \"graph-throttled\"", json, StringComparison.Ordinal);
            Assert.DoesNotContain("SENTINEL", json, StringComparison.Ordinal);
        }

        [Fact]
        public void Connections_are_ordered_by_identifier_so_two_polls_can_be_diffed()
        {
            // sql/24 hands the front page its connections worst-first, which is
            // right for somebody scanning a screen and wrong for anything
            // comparing two payloads: a connection that starts failing would
            // move to the top and make every row below it look changed.
            HealthReport report = HealthProjection.Build(
                new[]
                {
                    Row("zeta", health: "healthy"),
                    Row("alpha", health: "failing"),
                    Row("mike", health: "late"),
                },
                When);

            Assert.Equal(
                new[] { "alpha", "mike", "zeta" },
                report.Connections.Select(c => c.ConnectionId).ToArray());
        }

        [Fact]
        public void An_unhealthy_estate_is_still_served_with_200()
        {
            // The decision, as a test. A 503 here would be easier to alert on
            // and would spend the endpoint's only out-of-band signal on a
            // distinction the body already makes - leaving "a connection is
            // failing" and "the dashboard is down" arriving as the same red.
            HealthReport report = HealthProjection.Build(
                new[] { Row("a", health: "failing", consecutiveFailures: 9) },
                When);

            Assert.Equal(HealthReport.Critical, report.Status);
            Assert.Equal(200, report.StatusCode);
        }

        [Fact]
        public void Only_an_unreadable_database_produces_a_503()
        {
            // 503 in its literal sense: this service cannot answer the question.
            // The failure a monitor must never make is reading "no answer" as
            // "healthy", so the body says unavailable in the same field the
            // roll-up uses, and the status code says it again out of band.
            HealthReport report = HealthProjection.Unavailable(When);

            Assert.Equal(HealthReport.Unavailable, report.Status);
            Assert.Equal(503, report.StatusCode);
        }

        [Fact]
        public void The_unavailable_body_has_the_same_shape_as_any_other()
        {
            // A consumer that has to branch on which fields exist will get that
            // branch wrong. Every field is present with a zero or an empty
            // collection; only the status word and the code differ - and they
            // are what separates this from an estate that really has no
            // connections in it, which answers 200 with "warning".
            string unavailable = HealthProjection.Serialize(HealthProjection.Unavailable(When));
            string empty = HealthProjection.Serialize(
                HealthProjection.Build(Array.Empty<ConnectionHealthRow>(), When));

            foreach (string field in new[]
            {
                "\"status\"", "\"generatedUtc\"", "\"connectionCount\"", "\"enabledCount\"",
                "\"maxConsecutiveFailures\"", "\"byHealth\"", "\"connections\"",
            })
            {
                Assert.Contains(field, unavailable, StringComparison.Ordinal);
                Assert.Contains(field, empty, StringComparison.Ordinal);
            }

            Assert.Contains("\"status\": \"unavailable\"", unavailable, StringComparison.Ordinal);
            Assert.Contains("\"status\": \"warning\"", empty, StringComparison.Ordinal);
        }

        [Fact]
        public void The_counts_add_up_to_what_the_array_holds()
        {
            // The summary and the array are two views of the same rows, and a
            // monitor will read one of them - whichever it can reach with the
            // JSON path it supports. They must not be able to disagree.
            HealthReport report = HealthProjection.Build(RowPerWord(), When);

            Assert.Equal(ViewWords.Length, report.ConnectionCount);
            Assert.Equal(report.Connections.Count, report.ConnectionCount);
            Assert.Equal(report.ConnectionCount, report.ByHealth.Values.Sum());
        }

        /* ===================================================================
           4. Who may call it.
           =================================================================== */

        [Fact]
        public void The_endpoint_requires_the_reader_policy_by_name()
        {
            // Evidence that the route is gated at all, taken from the endpoint
            // the application actually builds rather than from reading the call.
            // It is a GET on /health carrying an authorization requirement that
            // names ReaderPolicy.PolicyName - which Program.cs registers as the
            // very object it installs as the fallback policy.
            RouteEndpoint endpoint = BuildHealthEndpoint();

            Assert.Equal(HealthEndpoint.Route, endpoint.RoutePattern.RawText);

            IReadOnlyList<IAuthorizeData> authorize = endpoint.Metadata.GetOrderedMetadata<IAuthorizeData>();

            Assert.NotEmpty(authorize);
            Assert.Contains(authorize, a => a.Policy == ReaderPolicy.PolicyName);

            // No AllowAnonymous anywhere on it. The fallback policy would have
            // covered this endpoint even with no metadata at all, so the way
            // this becomes anonymous by accident is somebody adding that
            // attribute, not somebody forgetting this one.
            Assert.Empty(endpoint.Metadata.GetOrderedMetadata<IAllowAnonymous>());

            HttpMethodMetadata methods = endpoint.Metadata.GetMetadata<HttpMethodMetadata>();

            Assert.NotNull(methods);
            Assert.Equal(new[] { "GET" }, methods.HttpMethods.ToArray());
        }

        [Fact]
        public async Task Requiring_the_default_policy_instead_would_have_published_crawl_state()
        {
            // THE TEST THIS SECTION EXISTS FOR, and the reason the policy is
            // registered under a name at all.
            //
            // `.RequireAuthorization()` with no argument is the obvious way to
            // make an endpoint's requirement explicit, and it does not mean "the
            // policy this site uses". It means the DEFAULT policy, which is
            // RequireAuthenticatedUser and nothing else. Below, one principal -
            // authenticated, member of a group that is not a reader group -
            // passes that policy and is refused by the site's own. An endpoint
            // written the obvious way would therefore have served the connection
            // inventory to every authenticated user in the domain while
            // CrawlState:ReaderGroups still said otherwise, every page still
            // enforced it, and nothing logged a thing.
            var options = new AuthorizationOptions();
            AuthorizationPolicy defaultPolicy = options.DefaultPolicy;
            AuthorizationPolicy reader = ReaderPolicy.Build(new[] { "CONTOSO\\Connector-Readers" });

            ClaimsPrincipal outsider = Authenticated("CONTOSO\\Some-Other-Group");

            Assert.True(await AllowsAsync(defaultPolicy, outsider));
            Assert.False(await AllowsAsync(reader, outsider));
        }

        [Fact]
        public async Task An_anonymous_caller_is_refused_even_with_no_reader_groups_configured()
        {
            // The shipped default is an empty ReaderGroups, and a monitoring
            // endpoint is exactly where somebody would be tempted to make an
            // exception for convenience. There is none: the policy this endpoint
            // names still requires an authenticated user, so an IIS site
            // misconfigured with anonymous authentication enabled fails closed
            // here for the same reason the pages do.
            AuthorizationPolicy policy = ReaderPolicy.Build(Array.Empty<string>());

            Assert.False(await AllowsAsync(policy, Anonymous()));
            Assert.True(await AllowsAsync(policy, Authenticated()));
        }

        /* ===================================================================
           Helpers.
           =================================================================== */

        /// <summary>Builds the endpoint exactly as Program.cs does, and returns it.</summary>
        /// <remarks>
        /// CrawlStateQueries is registered but never resolved: minimal-API
        /// parameter binding asks IServiceProviderIsService whether a parameter
        /// is a service, and without the registration it would try to bind the
        /// query type from a request body - which a GET refuses at build time.
        /// Nothing here opens a connection.
        /// </remarks>
        private static RouteEndpoint BuildHealthEndpoint()
        {
            ServiceProvider services = new ServiceCollection()
                .AddLogging()
                .AddRouting()
                .AddAuthorization()
                .AddSingleton<CrawlStateQueries>()
                .BuildServiceProvider();

            var routes = new EndpointCollector(services);

            routes.MapConnectionHealth();

            return routes.DataSources
                .SelectMany(source => source.Endpoints)
                .OfType<RouteEndpoint>()
                .Single();
        }

        /// <summary>Evaluates a policy the way the framework does at request time.</summary>
        private static async Task<bool> AllowsAsync(AuthorizationPolicy policy, ClaimsPrincipal user)
        {
            ServiceProvider services = new ServiceCollection()
                .AddAuthorization()
                .AddLogging()
                .BuildServiceProvider();

            var authorization = services.GetRequiredService<IAuthorizationService>();

            AuthorizationResult result =
                await authorization.AuthorizeAsync(user, resource: null, policy);

            return result.Succeeded;
        }

        /// <summary>A signed-in Windows user carrying one role claim per group.</summary>
        private static ClaimsPrincipal Authenticated(params string[] groups)
        {
            var claims = new List<Claim> { new Claim(ClaimTypes.Name, "CONTOSO\\someone") };

            foreach (string group in groups)
            {
                claims.Add(new Claim(ClaimTypes.Role, group));
            }

            return new ClaimsPrincipal(new ClaimsIdentity(claims, "Negotiate"));
        }

        private static ClaimsPrincipal Anonymous() => new ClaimsPrincipal(new ClaimsIdentity());

        /// <summary>One row per word the health view can return.</summary>
        private static ConnectionHealthRow[] RowPerWord()
        {
            var rows = new ConnectionHealthRow[ViewWords.Length];

            for (int i = 0; i < ViewWords.Length; i++)
            {
                rows[i] = Row("conn-" + i, health: ViewWords[i]);
            }

            return rows;
        }

        /// <summary>A row shaped like one crawl.vwConnectionHealth returns.</summary>
        private static ConnectionHealthRow Row(
            string connectionId,
            string health = "healthy",
            bool enabled = true,
            string lastRunStatus = "succeeded",
            int? minutesSinceLastSuccess = 4,
            int consecutiveFailures = 0,
            DateTime? lastRunStartedUtc = null,
            DateTime? lastSuccessUtc = null,
            string errorKind = null,
            string errorMessage = null)
        {
            return new ConnectionHealthRow
            {
                ConnectionId = connectionId,
                DisplayName = "Connection " + connectionId,
                ConnectorKey = "sql-tickets",
                IsEnabled = enabled,
                ExpectedIntervalMinutes = 60,
                LastRunId = minutesSinceLastSuccess.HasValue ? 8123L : (long?)null,
                LastRunStatus = lastRunStatus,
                LastRunStartedUtc = lastRunStartedUtc,
                LastSuccessUtc = lastSuccessUtc,
                MinutesSinceLastSuccess = minutesSinceLastSuccess,
                ConsecutiveFailures = consecutiveFailures,
                LiveItemCount = 1200,
                PendingDeleteCount = 0,
                Health = health,
                ErrorKind = errorKind,
                ErrorMessage = errorMessage,
            };
        }

        /// <summary>A timestamp with no kind, which is what SqlDataReader returns for DATETIME2.</summary>
        private static DateTime Unspecified(int year, int month, int day, int hour, int minute, int second)
        {
            return new DateTime(year, month, day, hour, minute, second, DateTimeKind.Unspecified);
        }

        /// <summary>The smallest thing MapConnectionHealth can be mapped onto.</summary>
        private sealed class EndpointCollector : IEndpointRouteBuilder
        {
            public EndpointCollector(IServiceProvider services) => this.ServiceProvider = services;

            public ICollection<EndpointDataSource> DataSources { get; } = new List<EndpointDataSource>();

            public IServiceProvider ServiceProvider { get; }

            public IApplicationBuilder CreateApplicationBuilder() =>
                new ApplicationBuilder(this.ServiceProvider);
        }
    }
}
