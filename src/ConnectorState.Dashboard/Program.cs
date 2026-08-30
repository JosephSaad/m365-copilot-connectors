// ---------------------------------------------------------------------------
// Program.cs
// The composition root: who may look, what talks to SQL, and what headers go
// back. Everything security-relevant about this application is decided here or
// in CrawlStateQueries.cs, and both files are short on purpose.
//
// THIS PROCESS IS READ-ONLY, AND THAT IS ENFORCED IN THREE PLACES, NONE OF
// WHICH IS A CODE REVIEW:
//
//   1. sql/25 grants the IIS application pool identity - the crawl_reader role -
//      EXECUTE on the seven reporting procedures in sql/24 and SELECT on the six
//      views in sql/22. It has no permission on any table, and an explicit DENY
//      on INSERT, UPDATE, DELETE, ALTER and REFERENCES for the whole crawl
//      schema. A defect in this web tier cannot write crawl state, because the
//      connection it holds does not have the rights to.
//   2. sql/24 contains no write statement of any kind - not a hit counter, not a
//      last-viewed timestamp. So "can the dashboard corrupt crawl state" is
//      answered by the absence of an UPDATE in one SQL file.
//   3. CrawlStateQueries.cs is the only file in this project that names a SQL
//      object. It names the seven procedures and one view, and nothing else.
//
// AUTHENTICATION IS WINDOWS INTEGRATED, END TO END.
//
//   To SQL: Integrated Security, always, set in code by CrawlStateOptions and
//   not configurable. The app pool identity IS the database principal, so there
//   is no SQL password in this repository, in appsettings.json, or on the
//   server - nothing to rotate and nothing to leak. The connection string is
//   built from a server name and a database name.
//
//   To the browser: the IIS authentication scheme, with a fallback authorization
//   policy that requires an authenticated user for every endpoint. There is no
//   anonymous page. Configure the site in IIS with Windows Authentication
//   enabled and Anonymous Authentication disabled; the fallback policy below is
//   what makes a misconfiguration there fail closed rather than publish the
//   estate's crawl state to the intranet.
//
//   The handler behind that scheme is supplied by the ASP.NET Core Module, so
//   under plain Kestrel there is no handler to challenge with and every page
//   returns 500 with "No authenticationScheme was specified". Running this with
//   `dotnet run` on a developer workstation therefore serves nothing but the
//   error page. That is the correct failure and not a bug to work around by
//   adding an anonymous fallback: the only supported host is IIS with the
//   ASP.NET Core Module, which is the deployment this estate has, and an
//   anonymous fallback added for local convenience is an anonymous fallback
//   that ships.
//
// WHAT AN OPERATOR CAN SEE HERE IS METADATA, NOT CONTENT. crawl.Item holds an
// item ID, a type, two hashes and a byte count - see the header of sql/22 - so
// no page here can show a document body, a field value or a title, however the
// query is written. The pages say so where somebody would otherwise expect it.
//
// THERE IS ONE ENDPOINT THAT IS NOT A PAGE: GET /health, in Monitoring/. It is
// JSON for a monitoring system rather than HTML for a person, and it is gated by
// the same policy as everything else - it asks for that policy BY NAME, which is
// why the policy is registered under a name below as well as installed as the
// fallback. The reasoning for gating it at all, rather than leaving a monitoring
// endpoint anonymous, is in the header of HealthEndpoint.cs; the short version
// is that its body names customer connections, so an anonymous /health is an
// anonymous page with the connection inventory on it.
//
// THERE IS NOW ONE SCRIPT ON THIS SITE, AND THE POLICY BELOW PERMITS EXACTLY IT.
// It reads the browser's time zone into a cookie so the pages can render times
// in the viewer's zone instead of making everybody convert from UTC in their
// head. It is allowed by a SHA-256 of its own bytes rather than by script-src
// 'self', so adding a second script - a file in wwwroot, another inline block,
// an event-handler attribute - is still refused by the browser. See
// TimeZoneProbeScript.cs for why a script was the only way to answer the
// question at all, and DisplayZone.cs for what the pages do when it never runs.
// ---------------------------------------------------------------------------

using ConnectorState.Dashboard;
using ConnectorState.Dashboard.Data;
using ConnectorState.Dashboard.Monitoring;
using ConnectorState.Dashboard.Presentation;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Server.IISIntegration;

WebApplicationBuilder builder = WebApplication.CreateBuilder(args);

// Windows authentication, supplied by the ASP.NET Core Module. This names the
// scheme; IIS performs the handshake and hands the request an identity.
//
// IISDefaults.AuthenticationScheme and IISServerDefaults.AuthenticationScheme
// are both "Windows" - the first is the out-of-process constant and the second
// the in-process one - so this line is correct for either hosting model and does
// not have to be revisited if the site is moved between them.
builder.Services.AddAuthentication(IISDefaults.AuthenticationScheme);

// Who may read crawl state, over and above being authenticated. Left empty the
// site behaves as it always has: any authenticated user. Set to one or more
// group names and membership is required as well.
//
// It is a list of names rather than a single one because a reader group and an
// operations group are the normal shape, and because the alternative - one group
// with everybody nested inside it - is a directory change rather than a
// configuration change, and needs somebody else's approval on the day.
string[] readerGroups = builder.Configuration
    .GetSection(ReaderPolicy.ConfigurationPath)
    .Get<string[]>() ?? Array.Empty<string>();

// The rule itself is in ReaderPolicy, so it can be tested. Its negative case -
// somebody outside every configured group being refused - is the half that
// matters and the half a running site cannot show you without a second person.
AuthorizationPolicy readerPolicy = ReaderPolicy.Build(readerGroups);

builder.Services.AddAuthorization(options =>
{
    // The fallback covers every endpoint that does not state a requirement of
    // its own, which is all seven pages: adding a page cannot accidentally add
    // an anonymous one.
    options.FallbackPolicy = readerPolicy;

    // The SAME OBJECT, under a name, for the one endpoint that does state its
    // own requirement - GET /health. Registered from the variable rather than
    // by calling ReaderPolicy.Build a second time, because two policies built
    // separately from the same configuration are two things somebody can later
    // edit separately, and the one that gets missed is whichever is not on the
    // screen. This way the endpoint and the pages are not merely consistent;
    // they are the same rule.
    options.AddPolicy(ReaderPolicy.PolicyName, readerPolicy);
});

builder.Services.AddRazorPages();

builder.Services
    .AddOptions<CrawlStateOptions>()
    .Bind(builder.Configuration.GetSection(CrawlStateOptions.SectionName))
    .Validate(
        options => !string.IsNullOrWhiteSpace(options.Server),
        "CrawlState:Server is not set. It is the SQL Server hosting the ConnectorState database.")
    .Validate(
        options => !string.IsNullOrWhiteSpace(options.Database),
        "CrawlState:Database is not set.")
    .Validate(
        options => options.CommandTimeoutSeconds > 0 && options.ConnectTimeoutSeconds > 0,
        "CrawlState timeouts must be greater than zero.")

    // Zero is off; anything else has to be long enough to read the page. A
    // negative or one-second value renders a meta refresh the browser obeys
    // immediately, which is a site nobody can use and an unbounded query load
    // on the state database.
    .Validate(
        options => options.AutoRefreshSeconds == 0 || options.AutoRefreshSeconds >= 10,
        "CrawlState:AutoRefreshSeconds must be 0 to disable, or at least 10 seconds.")

    // Fail at startup, not on the first page load. A dashboard that starts and
    // then 500s is a dashboard somebody has to open to discover is misconfigured.
    .ValidateOnStart();

// Singleton: it holds a connection string and no connection. SqlConnection is
// opened and disposed per query, and pooling does the rest.
builder.Services.AddSingleton<CrawlStateQueries>();

WebApplication app = builder.Build();

if (!app.Environment.IsDevelopment())
{
    // The error page shows no exception detail. A stack trace from a monitoring
    // dashboard names the database, the schema and the procedure.
    app.UseExceptionHandler("/Error");
}

// Built once, outside the request, because the script-src source is a SHA-256
// of the probe script's bytes and there is no reason to hash it per request.
//
// WHAT THIS POLICY NOW PERMITS THAT IT DID NOT. It gained exactly one source:
// script-src 'sha256-...', which allows an inline script whose content hashes to
// that value and nothing else. It did NOT gain 'self' - a second script file
// dropped into wwwroot is still refused - and it did not gain 'unsafe-inline',
// so an onclick attribute or a second inline block is still refused too. The
// property this file used to claim, that there is no script on any page here, is
// now narrower and still enforced by the browser rather than by review: there is
// ONE script here, its text is in TimeZoneProbeScript.cs, and anything else is
// blocked. The reason it exists at all is in that file's header; the short
// version is that a browser's time zone reaches the server no other way, and the
// alternative was to render the SERVER's zone and label it as the viewer's.
//
// Everything else is unchanged: default-src stays 'none', so a page here still
// cannot fetch, connect, frame, or load a font or a worker from anywhere.
string contentSecurityPolicy =
    "default-src 'none'; " +
    "script-src " + TimeZoneProbeScript.ContentSecurityPolicySource + "; " +
    "style-src 'self'; img-src 'self' data:; " +
    "form-action 'self'; base-uri 'none'; frame-ancestors 'none'";

app.Use(async (context, next) =>
{
    IHeaderDictionary headers = context.Response.Headers;

    headers["Content-Security-Policy"] = contentSecurityPolicy;
    headers["X-Content-Type-Options"] = "nosniff";
    headers["Referrer-Policy"] = "same-origin";

    // Crawl state is not something to leave in an intermediary's cache.
    headers["Cache-Control"] = "no-store";

    await next();
});

// Records an explicit "show me UTC" or "show me my zone" from the query string
// as a cookie, before anything renders. The reading of that preference is
// DisplayZoneRequest.For, which the pages call; this only has to run early
// enough to get a Set-Cookie onto the response.
app.UseDisplayZone();

app.UseHttpsRedirection();
app.UseStaticFiles();

app.UseRouting();
app.UseAuthentication();
app.UseAuthorization();

app.MapRazorPages();

// Mapped after the pages and outside them. It is not a Razor Page with a JSON
// content type bolted on: a page carries a view, a layout and an antiforgery
// pipeline it would have no use for, and every one of those is a thing that can
// start returning HTML to something that only parses JSON.
app.MapConnectionHealth();

app.Run();
