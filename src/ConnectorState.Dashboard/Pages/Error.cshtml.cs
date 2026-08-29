// ---------------------------------------------------------------------------
// Error.cshtml.cs
// The page an unhandled exception lands on, and what it deliberately does not
// say.
//
// A stack trace from this application names the SQL Server, the database, the
// crawl schema and the procedure that failed. In a regulated estate that is
// reconnaissance handed to whoever provoked the error, so the page shows a
// request identifier and nothing else. The detail goes to the application log,
// where it is behind the same access control as the server.
//
// AllowAnonymous, because a failure in authentication itself must still render
// a page rather than recursing into another authorization failure. Nothing on
// this page is worth protecting.
// ---------------------------------------------------------------------------

namespace ConnectorState.Dashboard.Pages;

using System.Diagnostics;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc.RazorPages;

/// <summary>The generic failure page.</summary>
[AllowAnonymous]
public sealed class ErrorModel : PageModel
{
    /// <summary>Gets the identifier that ties this page to a line in the application log.</summary>
    public string RequestId { get; private set; } = string.Empty;

    /// <summary>Captures the request identifier.</summary>
    public void OnGet()
    {
        this.RequestId = Activity.Current?.Id ?? this.HttpContext.TraceIdentifier;
    }
}
