// ---------------------------------------------------------------------------
// DashboardAuthorizationTests.cs
// Who the dashboard lets in, and — the half that matters — who it keeps out.
//
// WHY THESE EXIST AS UNIT TESTS. The live test for this feature (L4 in
// GO-LIVE-READINESS section 5) is to set CrawlState:ReaderGroups to a real
// group, then open the site as a member and as a non-member. It could not be
// run: republishing to IIS needs elevation, and the deployed build predates the
// setting. So the feature shipped with no evidence of any kind, which for an
// authorization rule is the worst place to be — it fails open, silently, and
// looks exactly like working.
//
// These are not a substitute for L4. They cannot see a Windows token, so
// nothing here proves that negotiate puts one role claim on the principal per
// group, nor that nested groups arrive flattened. What they do prove is the
// decision the application makes once it has those claims, evaluated through
// the real AuthorizationService against the app's own ReaderPolicy rather than
// a copy of it.
//
// The empty-string case is the one worth reading twice. It is not a tidiness
// check: RequireRole treats an empty role name as a requirement no principal
// can satisfy, so a stray "" left behind by a half-finished configuration edit
// would refuse EVERYONE, including whoever is trying to work out why, while the
// JSON still looked right.
// ---------------------------------------------------------------------------

namespace SqlTicketsConnector.Tests
{
    using System.Collections.Generic;
    using System.Security.Claims;
    using System.Threading.Tasks;
    using global::ConnectorState.Dashboard;
    using Microsoft.AspNetCore.Authorization;
    using Microsoft.Extensions.DependencyInjection;
    using Xunit;

    public class DashboardAuthorizationTests
    {
        private const string ReaderGroup = "CONTOSO\\Connector-Readers";
        private const string OtherGroup = "CONTOSO\\Some-Other-Group";

        [Fact]
        public async Task With_no_groups_configured_any_authenticated_user_is_allowed()
        {
            // The shipped default, and the behaviour the site had before this
            // setting existed. An empty list must not quietly become a lockout.
            AuthorizationPolicy policy = ReaderPolicy.Build(System.Array.Empty<string>());

            Assert.True(await AllowsAsync(policy, Authenticated()));
        }

        [Fact]
        public async Task An_anonymous_user_is_refused_even_with_no_groups_configured()
        {
            // RequireAuthenticatedUser is the part that must survive every future
            // edit to this policy: it is what makes an IIS misconfiguration fail
            // closed instead of publishing crawl state to the intranet.
            AuthorizationPolicy policy = ReaderPolicy.Build(System.Array.Empty<string>());

            Assert.False(await AllowsAsync(policy, Anonymous()));
        }

        [Fact]
        public async Task A_member_of_a_configured_group_is_allowed()
        {
            AuthorizationPolicy policy = ReaderPolicy.Build(new[] { ReaderGroup });

            Assert.True(await AllowsAsync(policy, Authenticated(ReaderGroup)));
        }

        [Fact]
        public async Task A_non_member_is_refused()
        {
            // THE TEST THIS FILE EXISTS FOR. Everything else here would still pass
            // if RequireRole had been left out altogether; only this one fails.
            AuthorizationPolicy policy = ReaderPolicy.Build(new[] { ReaderGroup });

            Assert.False(await AllowsAsync(policy, Authenticated(OtherGroup)));
        }

        [Fact]
        public async Task A_user_with_no_groups_at_all_is_refused_once_groups_are_configured()
        {
            AuthorizationPolicy policy = ReaderPolicy.Build(new[] { ReaderGroup });

            Assert.False(await AllowsAsync(policy, Authenticated()));
        }

        [Fact]
        public async Task Membership_of_any_one_configured_group_is_enough()
        {
            // A reader group and an operations group is the normal shape, and
            // they are alternatives rather than both being required.
            AuthorizationPolicy policy = ReaderPolicy.Build(new[] { ReaderGroup, OtherGroup });

            Assert.True(await AllowsAsync(policy, Authenticated(OtherGroup)));
        }

        [Fact]
        public async Task A_blank_entry_does_not_lock_everybody_out()
        {
            // The half-finished edit: ["CONTOSO\\Connector-Readers", ""]. Passed
            // through, the empty name is a role no principal holds, and the site
            // refuses every user while the configuration still reads correctly.
            AuthorizationPolicy policy = ReaderPolicy.Build(new[] { ReaderGroup, "   " });

            Assert.True(await AllowsAsync(policy, Authenticated(ReaderGroup)));
            Assert.False(await AllowsAsync(policy, Authenticated(OtherGroup)));
        }

        [Fact]
        public async Task A_list_of_nothing_but_blanks_is_treated_as_no_groups_at_all()
        {
            // Not a lockout either, for the same reason, and this is the shape a
            // commented-out configuration leaves behind.
            AuthorizationPolicy policy = ReaderPolicy.Build(new[] { string.Empty, "  " });

            Assert.True(await AllowsAsync(policy, Authenticated()));
            Assert.False(await AllowsAsync(policy, Anonymous()));
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
            var claims = new List<Claim> { new(ClaimTypes.Name, "CONTOSO\\someone") };

            foreach (string group in groups)
            {
                claims.Add(new Claim(ClaimTypes.Role, group));
            }

            // The authentication type is what makes IsAuthenticated true; an
            // identity built without one is anonymous however many claims it has.
            return new ClaimsPrincipal(new ClaimsIdentity(claims, "Negotiate"));
        }

        private static ClaimsPrincipal Anonymous() => new(new ClaimsIdentity());
    }
}
