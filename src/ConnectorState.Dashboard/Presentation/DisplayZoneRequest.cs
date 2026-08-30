// ---------------------------------------------------------------------------
// DisplayZoneRequest.cs
// How a request turns into a DisplayZone, and how the switch links are built.
//
// Split from DisplayZone.cs so that the rule - which zone, from which value,
// and what to do when it is nonsense - is a function of two strings and can be
// asked directly. Everything HTTP is here.
//
// THE SWITCH WORKS WITHOUT JAVASCRIPT, AND THAT IS THE REQUIREMENT RATHER THAN
// A BONUS. It is an ordinary link to the current URL with ?tz= on the end. The
// middleware below sees the parameter, writes the preference as a cookie so the
// next page keeps it, and the render for THIS request uses the query rather than
// the cookie it has just written - a cookie appended to a response is not in
// Request.Cookies, and a page that ignored that would need a second click to do
// anything. Script is required for detecting the zone and for nothing else: with
// it blocked the site still renders, still says UTC, and still lets a viewer
// move between the zone and UTC if a zone was ever detected.
//
// THE LINK REBUILDS THE WHOLE QUERY STRING rather than appending. The run list
// and the inventory carry filters and a page number; a switch link that dropped
// them would silently reset the filter, which on a paged list looks like the
// data changing. Any existing tz is dropped first so that clicking twice cannot
// produce ?tz=device&tz=utc, which binds to "device,utc" and matches no mode at
// all - the page would then have quietly stopped responding to its own control.
//
// THE COOKIE IS A PREFERENCE, NOT STATE. Nothing on the server reads it except
// this file, nothing is stored against the user, and losing it costs a viewer
// one click. It is HttpOnly because nothing in the browser needs to read it -
// unlike the zone cookie next to it, which the probe script writes.
// ---------------------------------------------------------------------------

namespace ConnectorState.Dashboard.Presentation;

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Primitives;

/// <summary>Reads the display zone off a request, and writes the viewer's choice back.</summary>
public static class DisplayZoneRequest
{
    /// <summary>How long the preference lasts. A year; it is a preference, not a session.</summary>
    public static readonly TimeSpan PreferenceLifetime = TimeSpan.FromDays(365);

    private const string ItemsKey = "ConnectorState.Dashboard.DisplayZone";

    /// <summary>Records an explicit zone choice from the query string, before anything renders.</summary>
    /// <param name="app">The pipeline.</param>
    /// <returns>The same pipeline, for chaining.</returns>
    /// <remarks>
    /// Middleware rather than page code because the cookie has to be on the
    /// response before the body starts. A Razor Page writing Set-Cookie from its
    /// view would be racing the first flush of the buffer, and the failure -
    /// "the choice does not stick, sometimes" - is the kind nobody manages to
    /// reproduce.
    /// </remarks>
    public static IApplicationBuilder UseDisplayZone(this IApplicationBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);

        return app.Use(async (context, next) =>
        {
            string? mode = Mode(context.Request.Query[DisplayZone.ModeParameter]);

            if (mode is not null)
            {
                context.Response.Cookies.Append(
                    DisplayZone.ModeCookieName,
                    mode,
                    new CookieOptions
                    {
                        Path = "/",
                        MaxAge = PreferenceLifetime,
                        SameSite = SameSiteMode.Strict,

                        // Matches the probe cookie. The supported deployment
                        // redirects to HTTPS, so this costs nothing there; on a
                        // plain-HTTP host the preference simply will not stick,
                        // and the page still says which zone it is showing.
                        Secure = true,

                        // Nothing in the browser reads this one.
                        HttpOnly = true,
                        IsEssential = true,
                    });
            }

            await next();
        });
    }

    /// <summary>Gets the zone this request renders in, resolving once per request.</summary>
    /// <param name="context">The request.</param>
    /// <returns>The zone. Never null.</returns>
    /// <remarks>
    /// Cached in HttpContext.Items because a page asks for it once for its
    /// column headers and then once per timestamp, and TryFindSystemTimeZoneById
    /// is not free. It resolves on demand rather than requiring the middleware,
    /// so a page cannot render unlabelled times because a pipeline line was
    /// missed - the middleware only exists to persist an explicit choice.
    /// </remarks>
    public static DisplayZone For(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        if (context.Items.TryGetValue(ItemsKey, out object? existing) && existing is DisplayZone cached)
        {
            return cached;
        }

        // The query wins for this request: it is the click that has just
        // happened, and the cookie recording it is on the response rather than
        // the request.
        string? mode = Mode(context.Request.Query[DisplayZone.ModeParameter])
            ?? Mode(context.Request.Cookies[DisplayZone.ModeCookieName]);

        DisplayZone zone = DisplayZone.Resolve(context.Request.Cookies[DisplayZone.ZoneCookieName], mode);

        context.Items[ItemsKey] = zone;

        return zone;
    }

    /// <summary>Builds the link that re-renders the current page in another zone.</summary>
    /// <param name="request">The current request.</param>
    /// <param name="mode">"utc" or "device".</param>
    /// <returns>A relative URL: this path, this query, with tz replaced.</returns>
    public static string SwitchUrl(HttpRequest request, string mode)
    {
        ArgumentNullException.ThrowIfNull(request);

        QueryString query = QueryString.Empty;

        foreach (KeyValuePair<string, StringValues> parameter in request.Query)
        {
            if (string.Equals(parameter.Key, DisplayZone.ModeParameter, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            foreach (string? value in parameter.Value)
            {
                // QueryString.Add percent-encodes both halves, so a filter value
                // carrying an ampersand cannot split into a parameter of its own.
                query = query.Add(parameter.Key, value ?? string.Empty);
            }
        }

        query = query.Add(DisplayZone.ModeParameter, mode);

        return request.PathBase.Add(request.Path).Add(query);
    }

    /// <summary>Canonicalises a mode value, or returns null if it is not one.</summary>
    /// <param name="value">The raw value from a query string or cookie.</param>
    /// <returns>"utc", "device", or null.</returns>
    /// <remarks>
    /// Null for anything unrecognised, rather than a guess. A repeated parameter
    /// arrives here as "device,utc" and is refused, which leaves the previous
    /// preference standing instead of picking one of them.
    /// </remarks>
    private static string? Mode(string? value)
    {
        if (string.Equals(value, DisplayZone.UtcMode, StringComparison.OrdinalIgnoreCase))
        {
            return DisplayZone.UtcMode;
        }

        if (string.Equals(value, DisplayZone.DeviceMode, StringComparison.OrdinalIgnoreCase))
        {
            return DisplayZone.DeviceMode;
        }

        return null;
    }
}
