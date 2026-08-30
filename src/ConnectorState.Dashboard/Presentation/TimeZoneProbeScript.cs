// ---------------------------------------------------------------------------
// TimeZoneProbeScript.cs
// The only script on this site, and the Content-Security-Policy source that
// permits exactly it and nothing else.
//
// WHY THERE IS A SCRIPT AT ALL, ON A SITE WHOSE WHOLE POINT WAS THAT THERE WAS
// NOT ONE. Program.cs sends default-src 'none' and says, in as many words, that
// the absence of script here is a property worth keeping - and the auto-refresh
// was built as a <meta http-equiv="refresh"> rather than a setInterval
// specifically so that it stayed true. That reasoning has not changed and this
// file does not overturn it.
//
// What changed is the requirement: show run times in the VIEWER'S time zone. A
// browser's time zone is not sent in any request header, there is no client hint
// for it, and Accept-Language carries a locale rather than a zone. So the server
// cannot know it. The options were to detect it with one line of script, or to
// render the SERVER'S zone and describe it as the viewer's - and the second is
// not an approximation, it is a wrong label on a page about incident timing,
// which is the exact failure the header of Format.cs was written to prevent.
//
// SO THE LOOSENING IS A HASH, NOT 'self'. script-src 'self' would permit any
// file served from this origin to execute, which quietly retires the property
// above: from then on, adding a .js file to wwwroot works. A 'sha256-...' source
// permits exactly one script body - this one, byte for byte - and nothing else.
// Another inline script, a second file, an event handler attribute: all still
// refused. The hash is computed below FROM the string that is rendered, so the
// policy and the script cannot drift apart in a way that leaves either one wrong.
//
// WHAT IT COSTS WHEN IT DOES NOT RUN: nothing that matters. No cookie is
// written, DisplayZone resolves to UTC, every timestamp is rendered in UTC, and
// the page says so in the masthead and the colophon. The site is completely
// usable with scripts blocked - which is the requirement a monitoring page has
// to meet, because a dashboard that becomes unreadable when script is disabled
// is worse than one that was never clever.
//
// IT DOES NOT RELOAD THE PAGE, AND THAT IS DELIBERATE. Writing the cookie and
// calling location.reload() would show the right zone immediately, and would
// also be an infinite reload for anybody whose browser refuses the cookie -
// third-party-cookie policies, a Secure cookie on a plain-HTTP deployment, a
// locked-down profile - because the reloaded page would find no cookie and try
// again. Instead the cookie lands and the NEXT render uses it, which on this
// site is at most one auto-refresh away. The cost is that a first-ever page view
// shows UTC, correctly labelled UTC. That is a fair trade against a page that
// can loop.
//
// THE COOKIE IS Secure, AND SO WILL NOT BE SET OVER PLAIN HTTP. The supported
// deployment is IIS with UseHttpsRedirection in front of it, so this costs
// nothing there. On a deployment reached over http:// the browser drops the
// write, no zone is ever detected, and the page reads UTC and says the zone was
// not detected - visibly wrong-looking rather than silently wrong.
// ---------------------------------------------------------------------------

namespace ConnectorState.Dashboard.Presentation;

using System.Security.Cryptography;
using System.Text;

/// <summary>The inline time-zone probe, and the CSP source that allows it.</summary>
public static class TimeZoneProbeScript
{
    /// <summary>
    /// The script, exactly as the layout emits it between its script tags.
    /// </summary>
    /// <remarks>
    /// EDIT THIS AND THE HASH MOVES WITH IT, WHICH IS THE POINT - but the layout
    /// has to keep emitting it with no whitespace of its own around it, because
    /// a CSP hash covers the element's content byte for byte. A stray newline
    /// introduced by reformatting _Layout.cshtml would leave the browser
    /// refusing the script, with no visible symptom beyond the page quietly
    /// reporting UTC for ever. _Layout.cshtml carries the same warning at the
    /// tag itself.
    ///
    /// It is written as one line and reads badly on purpose: every character is
    /// part of a hash, so this is not a file to tidy.
    /// </remarks>
    public const string Source =
        "try{var z=Intl.DateTimeFormat().resolvedOptions().timeZone;" +
        "if(z)document.cookie=\"dash.tz=\"+encodeURIComponent(z)+" +
        "\";Path=/;Max-Age=31536000;SameSite=Strict;Secure\"}catch(e){}";

    /// <summary>
    /// The CSP source expression that permits <see cref="Source"/>: a base64
    /// SHA-256 of its UTF-8 bytes, in the quoted form the header wants.
    /// </summary>
    /// <remarks>
    /// Computed rather than pasted. A literal in the header would be a second
    /// copy of a value derived from this file, and the failure of a stale copy
    /// is silent in the worst direction - the script stops running, the page
    /// falls back to UTC, and nothing anywhere reports an error except a console
    /// nobody has open.
    /// </remarks>
    public static readonly string ContentSecurityPolicySource =
        "'sha256-" + Convert.ToBase64String(SHA256.HashData(Encoding.UTF8.GetBytes(Source))) + "'";
}
