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
// ---------------------------------------------------------------------------

using ConnectorState.Dashboard;
using ConnectorState.Dashboard.Data;
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
builder.Services.AddAuthorization(options =>
{
    options.FallbackPolicy = ReaderPolicy.Build(readerGroups);
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

app.Use(async (context, next) =>
{
    IHeaderDictionary headers = context.Response.Headers;

    // The page loads one stylesheet from its own origin and nothing else. There
    // is no script on any page here, so 'none' is a statement of fact rather
    // than a restriction, and it is the control that keeps it that way: the day
    // somebody adds an inline handler, it stops working immediately instead of
    // quietly widening what this page can do.
    headers["Content-Security-Policy"] =
        "default-src 'none'; style-src 'self'; img-src 'self' data:; " +
        "form-action 'self'; base-uri 'none'; frame-ancestors 'none'";
    headers["X-Content-Type-Options"] = "nosniff";
    headers["Referrer-Policy"] = "same-origin";

    // Crawl state is not something to leave in an intermediary's cache.
    headers["Cache-Control"] = "no-store";

    await next();
});

app.UseHttpsRedirection();
app.UseStaticFiles();

app.UseRouting();
app.UseAuthentication();
app.UseAuthorization();

app.MapRazorPages();

app.Run();
