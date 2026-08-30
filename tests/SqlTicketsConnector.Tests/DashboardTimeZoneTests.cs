// ---------------------------------------------------------------------------
// DashboardTimeZoneTests.cs
// The dashboard renders times in the viewer's zone. These are the ways that
// goes wrong quietly.
//
// A wrong TIME is not like a wrong number. A wrong count looks wrong; a run that
// says it started at 09:14 when it started at 14:14 looks completely normal, and
// it stays normal all the way into the incident write-up. Every test here is
// about one of the four ways this feature can produce a plausible wrong answer:
//
//   IT CONVERTS WHEN IT SHOULD NOT, or does not convert when it should. The
//   store holds UTC and only UTC; the display zone is presentation. The
//   from/to filters on the run list and the date axis of the trend chart are
//   NOT presentation - one is a query bound, the other is a bucket the database
//   grouped by - and neither is converted.
//
//   IT USES ONE OFFSET FOR A PAGE. This dashboard shows historical runs, so a
//   list routinely spans a daylight-saving change. Measured on the build
//   machine: 12:00Z in Europe/London is 12:00 in January and 13:00 in July.
//   Every timestamp is converted on its own, and there is a test that would
//   fail if that were ever optimised into one cached offset.
//
//   IT TRUSTS THE COOKIE. The zone arrives in a cookie a script wrote, which
//   means it also arrives in a cookie anybody with this browser profile can
//   write. FindSystemTimeZoneById THROWS TimeZoneNotFoundException on an id the
//   platform does not know - measured, not assumed - so an unvalidated cookie
//   would be a 500 on every page at once. And a rejected value gets printed
//   back to explain the fallback, so the value that is printed has to be one it
//   is safe to print.
//
//   IT FAILS SILENTLY. The whole feature rests on a Content-Security-Policy
//   hash matching a script byte for byte. When that stops matching, nothing
//   errors: the browser refuses the script, no cookie is written, and the page
//   shows UTC - correct, labelled, and not what anybody asked for. The last two
//   tests are the tripwire.
//
// None of this needs a browser, a server or a database. The resolution rule is a
// function of two strings and the plumbing is a DefaultHttpContext.
// ---------------------------------------------------------------------------

namespace SqlTicketsConnector.Tests
{
    using System;
    using System.Security.Cryptography;
    using System.Text;
    using System.Threading.Tasks;
    using global::ConnectorState.Dashboard.Presentation;
    using Microsoft.AspNetCore.Builder;
    using Microsoft.AspNetCore.Http;
    using Microsoft.Extensions.DependencyInjection;
    using Xunit;

    public class DashboardTimeZoneTests
    {
        /// <summary>Midsummer, when Europe/London is an hour ahead of the stored value.</summary>
        private static readonly DateTime SummerUtc =
            new DateTime(2026, 7, 15, 12, 0, 0, DateTimeKind.Unspecified);

        /// <summary>Midwinter, when it is not.</summary>
        private static readonly DateTime WinterUtc =
            new DateTime(2026, 1, 15, 12, 0, 0, DateTimeKind.Unspecified);

        /* ===================================================================
           1. Which zone, from which value.
           =================================================================== */

        [Fact]
        public void A_zone_reported_by_the_browser_is_used_without_being_asked_for()
        {
            // "Based on the device settings" means this: cookie present, no
            // preference expressed, and the page is already in the viewer's
            // zone. Anything that needed a click first would be a zone picker
            // rather than a detected zone.
            DisplayZone zone = DisplayZone.Resolve("Europe/London", mode: null);

            Assert.False(zone.IsUtc);
            Assert.Equal("Europe/London", zone.Label);
            Assert.Equal("Europe/London", zone.DeviceZoneLabel);
            Assert.False(zone.DeviceZoneRejected);
        }

        [Fact]
        public void An_IANA_id_and_its_Windows_spelling_land_on_the_same_offset()
        {
            // Browsers report IANA - "Europe/London" - and .NET on Windows
            // historically wanted "GMT Standard Time". Measured on the build
            // machine (Windows 11, .NET 10.0.11, ICU, 141 system zones):
            // TryFindSystemTimeZoneById resolves BOTH directly, and the IANA one
            // keeps its own id, which is why the label below reads as it does.
            // A host in NLS or invariant-globalization mode would take
            // DisplayZone's IANA-to-Windows fallback instead and label it "GMT
            // Standard Time" - so the assertion that has to hold everywhere is
            // the offset, and it is asserted separately from the label.
            DisplayZone iana = DisplayZone.Resolve("Europe/London", null);
            DisplayZone windows = DisplayZone.Resolve("GMT Standard Time", null);

            Assert.False(iana.IsUtc);
            Assert.False(windows.IsUtc);

            Assert.Equal(
                iana.ConvertFromUtc(SummerUtc),
                windows.ConvertFromUtc(SummerUtc));

            Assert.Equal("Europe/London", iana.Label);
        }

        [Fact]
        public void Each_timestamp_is_converted_on_its_own_so_a_list_can_span_a_clock_change()
        {
            // The failure this prevents: one offset computed for the page and
            // added to everything on it. The run list is ordered by start time
            // and routinely covers both sides of a transition - a Monday morning
            // look back over a quiet weekend in late March does it every year -
            // and half the rows would be an hour out, consistently and
            // plausibly.
            DisplayZone london = DisplayZone.Resolve("Europe/London", null);

            Assert.Equal(new DateTime(2026, 1, 15, 12, 0, 0), london.ConvertFromUtc(WinterUtc));
            Assert.Equal(new DateTime(2026, 7, 15, 13, 0, 0), london.ConvertFromUtc(SummerUtc));
        }

        [Fact]
        public void Asking_for_UTC_overrides_a_perfectly_good_device_zone()
        {
            // The option the request asked for. It has to beat a detected zone,
            // and it has to leave the detected zone visible so the masthead can
            // offer it back by name rather than as "local".
            DisplayZone zone = DisplayZone.Resolve("Europe/London", DisplayZone.UtcMode);

            Assert.True(zone.IsUtc);
            Assert.Equal("UTC", zone.Label);
            Assert.Equal("Europe/London", zone.DeviceZoneLabel);
            Assert.Equal(SummerUtc, zone.ConvertFromUtc(SummerUtc));
        }

        [Fact]
        public void With_no_cookie_at_all_the_page_is_UTC_and_nothing_was_rejected()
        {
            // What every viewer sees on a first visit, and what every viewer
            // with script blocked sees for ever. It has to be the honest answer
            // - the store's own zone - and it must not claim anything was wrong,
            // because nothing was.
            DisplayZone zone = DisplayZone.Resolve(null, null);

            Assert.True(zone.IsUtc);
            Assert.Equal("UTC", zone.Label);
            Assert.Null(zone.DeviceZoneLabel);
            Assert.False(zone.DeviceZoneRejected);
        }

        [Fact]
        public void An_id_the_platform_does_not_know_falls_back_to_UTC_instead_of_throwing()
        {
            // FindSystemTimeZoneById would throw TimeZoneNotFoundException here -
            // measured on the build machine - and the cookie is not the server's
            // value to trust. Unhandled, that is a 500 on every page of the site
            // at once, from a string somebody typed into a browser console.
            //
            // It is reported rather than swallowed: a viewer who believes they
            // are reading local time and is not is exactly the reader the whole
            // labelling exists to protect, so the page says the value was
            // refused and names it.
            DisplayZone zone = DisplayZone.Resolve("Not/AZone", null);

            Assert.True(zone.IsUtc);
            Assert.Equal("UTC", zone.Label);
            Assert.Null(zone.DeviceZoneLabel);
            Assert.True(zone.DeviceZoneRejected);
            Assert.Equal("Not/AZone", zone.RejectedZoneId);
        }

        [Theory]
        [InlineData("<script>alert(1)</script>")]
        [InlineData("Europe/London\r\nSet-Cookie: x=y")]
        [InlineData("../../etc/passwd ")]
        public void A_value_that_is_not_the_shape_of_a_zone_id_is_never_echoed_back(string hostile)
        {
            // Razor encodes everything, so none of these could have executed.
            // The point is narrower and still worth having: the page REPEATS a
            // refused id to explain why it is showing UTC, and repeating
            // arbitrary text on an operations dashboard is how somebody else's
            // sentence ends up in a screenshot in a ticket. Refused values that
            // do not look like zone names are reported without being quoted.
            DisplayZone zone = DisplayZone.Resolve(hostile, null);

            Assert.True(zone.IsUtc);
            Assert.True(zone.DeviceZoneRejected);
            Assert.Null(zone.RejectedZoneId);
        }

        [Fact]
        public void A_cookie_far_longer_than_any_real_zone_id_is_refused()
        {
            // The longest real ones are around thirty characters. A cookie can
            // hold four kilobytes.
            DisplayZone zone = DisplayZone.Resolve(new string('x', DisplayZone.MaxZoneIdLength + 1), null);

            Assert.True(zone.IsUtc);
            Assert.True(zone.DeviceZoneRejected);
            Assert.Null(zone.RejectedZoneId);
        }

        [Fact]
        public void A_rejected_zone_is_still_reported_when_UTC_was_the_choice_anyway()
        {
            // Otherwise the explanation disappears the moment somebody clicks
            // UTC, and the next person to click back gets no clue why their zone
            // never worked.
            DisplayZone zone = DisplayZone.Resolve("Not/AZone", DisplayZone.UtcMode);

            Assert.True(zone.DeviceZoneRejected);
            Assert.Equal("Not/AZone", zone.RejectedZoneId);
        }

        [Fact]
        public void A_value_whose_kind_says_local_is_converted_rather_than_throwing()
        {
            // ConvertTimeFromUtc throws ArgumentException on a DateTimeKind.Local
            // input - "the supplied DateTime did not have the Kind property set
            // correctly" - measured on the build machine. Nothing in this
            // dashboard produces one today, and a 500 on a page is a poor way to
            // find out that something started to.
            DisplayZone london = DisplayZone.Resolve("Europe/London", null);

            DateTime asLocalKind = DateTime.SpecifyKind(SummerUtc, DateTimeKind.Local);

            Assert.Equal(new DateTime(2026, 7, 15, 13, 0, 0), london.ConvertFromUtc(asLocalKind));
        }

        /* ===================================================================
           2. What the pages print.
           =================================================================== */

        [Fact]
        public void A_timestamp_is_rendered_in_the_page_zone_and_UTC_leaves_it_alone()
        {
            DisplayZone london = DisplayZone.Resolve("Europe/London", null);
            DisplayZone utc = DisplayZone.Resolve("Europe/London", DisplayZone.UtcMode);

            Assert.Equal("2026-07-15 13:00:00", Format.Timestamp(SummerUtc, london));
            Assert.Equal("2026-07-15 13:00", Format.TimestampShort(SummerUtc, london));

            Assert.Equal("2026-07-15 12:00:00", Format.Timestamp(SummerUtc, utc));
            Assert.Equal("2026-07-15 12:00", Format.TimestampShort(SummerUtc, utc));
        }

        [Fact]
        public void A_null_timestamp_is_still_an_em_dash_in_every_zone()
        {
            // Null means there is no value - a run that has not completed, a
            // connection that has never succeeded. Converting it into an epoch
            // and rendering that would be the same class of mistake as reading a
            // null staleness as zero.
            DisplayZone london = DisplayZone.Resolve("Europe/London", null);

            Assert.Equal(Format.None, Format.Timestamp(null, london));
            Assert.Equal(Format.None, Format.TimestampShort(null, london));
        }

        [Fact]
        public void A_date_from_the_trend_view_is_not_shifted_into_the_viewers_zone()
        {
            // vwDailyActivity groups runs BY UTC DATE in SQL. The label belongs
            // to the bucket, not to an instant, and shifting it would put a bar
            // dated the 14th over a day the database computed for the 15th - for
            // every viewer west of Greenwich, on every bucket. Format.Date
            // therefore takes no zone at all, which is why this test can only be
            // written one way.
            Assert.Equal("2026-07-15", Format.Date(new DateTime(2026, 7, 15)));
        }

        /* ===================================================================
           3. Getting the choice off the request, and back onto it.
           =================================================================== */

        [Fact]
        public void The_query_string_wins_over_the_cookie_on_the_request_that_carries_it()
        {
            // The click that has just happened. The cookie recording it is on
            // the RESPONSE, not the request, so a render that consulted only
            // cookies would need a second click to do anything - which reads as
            // the control being broken.
            HttpContext context = Request("/Runs", "?tz=utc", zoneCookie: "Europe/London", modeCookie: "device");

            Assert.True(DisplayZoneRequest.For(context).IsUtc);
        }

        [Fact]
        public void The_stored_preference_is_used_when_the_query_says_nothing()
        {
            HttpContext context = Request("/Runs", string.Empty, "Europe/London", DisplayZone.UtcMode);

            Assert.True(DisplayZoneRequest.For(context).IsUtc);
            Assert.Equal("Europe/London", DisplayZoneRequest.For(context).DeviceZoneLabel);
        }

        [Fact]
        public void The_zone_is_resolved_once_and_reused_for_the_rest_of_the_request()
        {
            // Every page asks for it for its column headers and then once per
            // timestamp, and a paged list is five hundred timestamps.
            HttpContext context = Request("/Runs", string.Empty, "Europe/London", null);

            Assert.Same(DisplayZoneRequest.For(context), DisplayZoneRequest.For(context));
        }

        [Fact]
        public void A_repeated_tz_parameter_is_refused_rather_than_guessed_at()
        {
            // ?tz=device&tz=utc binds to "device,utc". Picking one of them would
            // mean a link somebody double-clicked, or a URL edited by hand,
            // silently deciding the zone. Unrecognised leaves the stored
            // preference standing.
            HttpContext context = Request("/Runs", "?tz=device&tz=utc", "Europe/London", DisplayZone.UtcMode);

            Assert.True(DisplayZoneRequest.For(context).IsUtc);
        }

        [Fact]
        public async Task The_middleware_records_a_valid_choice_as_a_cookie()
        {
            HttpContext context = await RunAsync("/Runs", "?tz=utc");

            string setCookie = context.Response.Headers["Set-Cookie"].ToString();

            Assert.Contains(DisplayZone.ModeCookieName + "=" + DisplayZone.UtcMode, setCookie, StringComparison.Ordinal);
            Assert.Contains("path=/", setCookie, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("samesite=strict", setCookie, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("secure", setCookie, StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public async Task The_middleware_writes_nothing_for_a_value_that_is_not_a_mode()
        {
            // Including the case of no tz at all, which is every ordinary page
            // view: a Set-Cookie on every response would be a header on every
            // page for nothing.
            HttpContext junk = await RunAsync("/Runs", "?tz=Europe%2FLondon");
            HttpContext none = await RunAsync("/Runs", "?page=3");

            Assert.Empty(junk.Response.Headers["Set-Cookie"].ToString());
            Assert.Empty(none.Response.Headers["Set-Cookie"].ToString());
        }

        [Fact]
        public void The_switch_link_keeps_the_filters_and_replaces_any_existing_choice()
        {
            // A link that dropped the query would reset the filters on the run
            // list and the inventory, which on a paged view looks exactly like
            // the data having changed. And appending blindly would build
            // ?tz=device&tz=utc, which binds to a mode that matches nothing - the
            // control would appear to stop working after one click.
            HttpContext context = Request("/Runs", "?c=tickets-prod&status=failed&page=3&tz=device", null, null);

            Assert.Equal(
                "/Runs?c=tickets-prod&status=failed&page=3&tz=utc",
                DisplayZoneRequest.SwitchUrl(context.Request, DisplayZone.UtcMode));
        }

        [Fact]
        public void The_switch_link_works_on_a_page_that_has_no_query_string()
        {
            HttpContext context = Request("/", string.Empty, null, null);

            Assert.Equal("/?tz=device", DisplayZoneRequest.SwitchUrl(context.Request, DisplayZone.DeviceMode));
        }

        [Fact]
        public void A_percent_encoded_zone_cookie_reaches_the_server_decoded()
        {
            // The probe writes encodeURIComponent(zone), so "Europe/London"
            // travels as "Europe%2FLondon". Measured rather than assumed:
            // ASP.NET Core's request cookie collection percent-decodes values,
            // so the server sees the slash back. If that ever stopped being
            // true the value would fail the character guard, every viewer would
            // silently sit on UTC, and nothing would log a thing - which is why
            // the framework's behaviour is pinned here rather than trusted.
            var context = new DefaultHttpContext();
            context.Request.Headers["Cookie"] = "dash.tz=Europe%2FLondon";

            Assert.Equal("Europe/London", context.Request.Cookies[DisplayZone.ZoneCookieName]);
            Assert.Equal("Europe/London", DisplayZoneRequest.For(context).Label);
        }

        /* ===================================================================
           4. The script, and the policy that permits exactly it.
           =================================================================== */

        [Fact]
        public void The_policy_source_is_the_base64_sha256_of_the_script_a_browser_will_hash()
        {
            // A CSP hash covers the script element's content, byte for byte, as
            // UTF-8. Get the encoding, the base64 or the quoting wrong and the
            // browser refuses the script - with no server-side symptom at all,
            // because nothing on the server knows or cares.
            string expected = Convert.ToBase64String(
                SHA256.HashData(Encoding.UTF8.GetBytes(TimeZoneProbeScript.Source)));

            Assert.Equal("'sha256-" + expected + "'", TimeZoneProbeScript.ContentSecurityPolicySource);

            // Pinned as a literal as well, so that EDITING THE SCRIPT FAILS THIS
            // TEST. That is the tripwire: the recomputation above would happily
            // agree with any script at all, and the thing worth being told about
            // is that the bytes the policy permits have changed and the change
            // was deliberate.
            Assert.Equal(
                "'sha256-zHT0LJABJ7HsUy35Yduc+irQBtP6tmV5VdJ1E13INnI='",
                TimeZoneProbeScript.ContentSecurityPolicySource);
        }

        [Fact]
        public void The_script_writes_the_cookie_the_server_reads()
        {
            // Two files, one name. The script is a string literal, so the
            // compiler cannot check this and the failure would be silent in the
            // usual direction: the cookie is written, the server looks for
            // another one, every page shows UTC.
            Assert.Contains(DisplayZone.ZoneCookieName + "=", TimeZoneProbeScript.Source, StringComparison.Ordinal);

            // It writes a cookie and nothing else. No fetch, no reload - a
            // reload here would be an infinite loop for anybody whose browser
            // refuses the cookie, because the reloaded page would find none and
            // try again.
            Assert.DoesNotContain("location", TimeZoneProbeScript.Source, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("fetch", TimeZoneProbeScript.Source, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("XMLHttpRequest", TimeZoneProbeScript.Source, StringComparison.Ordinal);
        }

        /* ===================================================================
           Helpers.
           =================================================================== */

        /// <summary>A request carrying a path, a query and the two cookies this feature uses.</summary>
        private static HttpContext Request(string path, string query, string zoneCookie, string modeCookie)
        {
            var context = new DefaultHttpContext();

            context.Request.Path = path;
            context.Request.QueryString = string.IsNullOrEmpty(query) ? QueryString.Empty : new QueryString(query);

            string cookies = string.Empty;

            if (zoneCookie is not null)
            {
                cookies = DisplayZone.ZoneCookieName + "=" + Uri.EscapeDataString(zoneCookie);
            }

            if (modeCookie is not null)
            {
                cookies = (cookies.Length > 0 ? cookies + "; " : string.Empty)
                    + DisplayZone.ModeCookieName + "=" + modeCookie;
            }

            if (cookies.Length > 0)
            {
                context.Request.Headers["Cookie"] = cookies;
            }

            return context;
        }

        /// <summary>Runs the display-zone middleware over one request, as the pipeline does.</summary>
        private static async Task<HttpContext> RunAsync(string path, string query)
        {
            ServiceProvider services = new ServiceCollection().AddLogging().BuildServiceProvider();

            var builder = new ApplicationBuilder(services);

            builder.UseDisplayZone();

            RequestDelegate pipeline = builder.Build();

            HttpContext context = Request(path, query, null, null);
            context.RequestServices = services;

            await pipeline(context);

            return context;
        }
    }
}
