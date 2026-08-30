// ---------------------------------------------------------------------------
// DisplayZone.cs
// Which time zone this page is rendering in, and how it says so.
//
// The store holds UTC and only UTC - DATETIME2(3), SYSUTCDATETIME(), no offsets
// anywhere - and nothing in this file changes what is stored or queried. This is
// presentation: the same instant, printed in a zone, with the zone named beside
// it so nobody has to guess which one they are reading.
//
// THE ORIGINAL ARGUMENT AGAINST DOING THIS WAS RIGHT, AND IS ANSWERED RATHER
// THAN IGNORED. Format.cs used to say that rendering in the browser's zone would
// mean a run in a ticket and the same run on this page showing different times,
// which is how a wrong conclusion gets written down. That danger is real and it
// is a danger of UNLABELLED local time. "14:32" is ambiguous and dangerous on a
// page about run timing; "14:32 (Europe/London)" is not, and neither is a column
// header that names the zone, a masthead that names it, a colophon that names
// it, and a one-click switch back to UTC that survives having no JavaScript at
// all. The answer to an ambiguous timestamp is to remove the ambiguity, not to
// make everybody do the arithmetic themselves.
//
// TWO THINGS ARE DELIBERATELY NOT CONVERTED.
//
//   The DATE AXIS of the trend chart, and Format.Date with it. Those come from
//   vwDailyActivity, which groups runs BY UTC DATE in SQL. Shifting the label
//   into a zone would put a bar dated the 14th over a bucket the database
//   computed for the 15th, and for a viewer west of Greenwich every bucket would
//   be mislabelled by a day. The grouping is the database's; so is the date.
//
//   The FROM and TO FILTERS on the run list. Those are bounds that go to
//   uspListRuns as UTC. Reinterpreting a typed date in the viewer's zone would
//   silently move the filter by hours, and would make a URL somebody pasted into
//   a ticket mean different things for the two people reading it. They stay UTC
//   and their labels say UTC, whatever the display zone is.
//
// THE COOKIE IS UNTRUSTED INPUT. It is written by the probe script, and it is
// also whatever anybody with access to this browser profile, or to a cookie for
// this domain, chooses to put there. FindSystemTimeZoneById THROWS
// TimeZoneNotFoundException on an id the platform does not know - measured, not
// assumed - so a cookie reading "Not/AZone" would be a 500 on every page at
// once, from a value the server never validated. Resolution below is
// TryFindSystemTimeZoneById behind a length and character guard, it cannot
// throw, and the failure renders as UTC with a line on the page saying why.
// Falling back silently would have been worse than the crash: a viewer who
// believes they are reading local time and is not is exactly the reader the
// labelling exists to protect.
//
// EVERY TIMESTAMP IS CONVERTED INDIVIDUALLY. Never one offset computed once and
// added to a page full of values: this dashboard shows historical runs, and a
// list that spans a DST boundary would then be wrong on one side of it.
// Measured on the build machine: 12:00Z in Europe/London is 12:00 on 15 January
// 2026 and 13:00 on 15 July 2026. A run list covering both is a normal thing to
// look at after a quiet weekend.
// ---------------------------------------------------------------------------

namespace ConnectorState.Dashboard.Presentation;

/// <summary>The time zone one page render is printing its timestamps in.</summary>
public sealed class DisplayZone
{
    /// <summary>The cookie the probe script writes, holding the browser's IANA zone id.</summary>
    public const string ZoneCookieName = "dash.tz";

    /// <summary>The cookie holding the viewer's explicit choice of zone or UTC.</summary>
    public const string ModeCookieName = "dash.tzmode";

    /// <summary>The query-string parameter the switch links use.</summary>
    public const string ModeParameter = "tz";

    /// <summary>The mode value that forces UTC.</summary>
    public const string UtcMode = "utc";

    /// <summary>The mode value that follows the browser's detected zone.</summary>
    public const string DeviceMode = "device";

    /// <summary>What UTC is called on the page.</summary>
    public const string UtcLabel = "UTC";

    /// <summary>
    /// The longest zone id accepted from the cookie. The longest real ones are
    /// well under this - "America/Argentina/ComodRivadavia" is 32 characters and
    /// "Eastern Standard Time (Mexico)" is 30 - and the cap is here so that a
    /// cookie stuffed with kilobytes of text is refused before anything tries to
    /// look it up or, worse, print it back.
    /// </summary>
    public const int MaxZoneIdLength = 64;

    private DisplayZone(
        TimeZoneInfo zone,
        string label,
        bool isUtc,
        string? deviceZoneLabel,
        bool deviceZoneRejected,
        string? rejectedZoneId)
    {
        this.Zone = zone;
        this.Label = label;
        this.IsUtc = isUtc;
        this.DeviceZoneLabel = deviceZoneLabel;
        this.DeviceZoneRejected = deviceZoneRejected;
        this.RejectedZoneId = rejectedZoneId;
    }

    /// <summary>Gets the zone timestamps are converted into.</summary>
    public TimeZoneInfo Zone { get; }

    /// <summary>Gets the name to print: "UTC", or the zone id such as "Europe/London".</summary>
    public string Label { get; }

    /// <summary>Gets a value indicating whether this render is in UTC.</summary>
    public bool IsUtc { get; }

    /// <summary>
    /// Gets the browser's detected zone id when one is known and usable, whether
    /// or not it is the zone in use. The page needs it while showing UTC, to
    /// offer the switch back by name rather than as "local".
    /// </summary>
    public string? DeviceZoneLabel { get; }

    /// <summary>Gets a value indicating whether a detected zone was supplied and refused.</summary>
    public bool DeviceZoneRejected { get; }

    /// <summary>
    /// Gets the refused zone id when it was safe to repeat, and null when the
    /// value did not even look like a zone name. Nothing that failed the
    /// character guard is ever echoed, however encoded the page would render it.
    /// </summary>
    public string? RejectedZoneId { get; }

    /// <summary>Resolves the zone for one render.</summary>
    /// <param name="deviceZoneId">The zone id from the probe cookie, or null.</param>
    /// <param name="mode">"utc", "device", or null for the default.</param>
    /// <returns>The zone to render in. Never null, and never throws.</returns>
    /// <remarks>
    /// THE DEFAULT IS THE DEVICE, WHICH IS WHAT WAS ASKED FOR. With no mode
    /// cookie and no query parameter, a detected zone is used; with no detected
    /// zone, UTC. So a viewer whose browser ran the probe sees local time
    /// without touching anything, and a viewer whose browser did not sees UTC
    /// rather than something invented for them.
    /// </remarks>
    public static DisplayZone Resolve(string? deviceZoneId, string? mode)
    {
        TimeZoneInfo? device = null;
        bool rejected = false;
        string? rejectedId = null;

        // Resolved even when UTC is being displayed. The switch link has to name
        // the zone it would switch to, and "show local time" is the ambiguous
        // wording this whole change exists to remove.
        if (!string.IsNullOrWhiteSpace(deviceZoneId))
        {
            string candidate = deviceZoneId.Trim();

            if (!LooksLikeZoneId(candidate))
            {
                rejected = true;
            }
            else if (TryResolve(candidate, out TimeZoneInfo found))
            {
                device = found;
            }
            else
            {
                rejected = true;
                rejectedId = candidate;
            }
        }

        bool utcRequested = string.Equals(mode, UtcMode, StringComparison.OrdinalIgnoreCase);

        if (device is null || utcRequested)
        {
            return new DisplayZone(TimeZoneInfo.Utc, UtcLabel, true, device?.Id, rejected, rejectedId);
        }

        return new DisplayZone(device, device.Id, false, device.Id, rejected, rejectedId);
    }

    /// <summary>Converts one UTC instant into this zone.</summary>
    /// <param name="value">The instant, as the store holds it.</param>
    /// <returns>The same instant, in this zone.</returns>
    /// <remarks>
    /// The kind is stated before converting, and that is not defensive tidying.
    /// SqlDataReader returns DATETIME2 as DateTimeKind.Unspecified, which
    /// ConvertTimeFromUtc accepts - but a caller that passed a
    /// DateTimeKind.Local value would get an ArgumentException reading "the
    /// supplied DateTime did not have the Kind property set correctly", which is
    /// a 500 on a page rather than a wrong time. Measured on the build machine.
    /// Every value this dashboard renders is UTC by construction, so saying so
    /// is free and removes the one input that throws.
    /// </remarks>
    public DateTime ConvertFromUtc(DateTime value) =>
        TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(value, DateTimeKind.Utc), this.Zone);

    /// <summary>Converts an optional UTC instant into this zone.</summary>
    /// <param name="value">The instant, or null.</param>
    /// <returns>The converted instant, or null.</returns>
    public DateTime? ConvertFromUtc(DateTime? value) =>
        value.HasValue ? this.ConvertFromUtc(value.Value) : null;

    /// <summary>Resolves a zone id, accepting either an IANA or a Windows spelling.</summary>
    /// <param name="id">The candidate id.</param>
    /// <param name="zone">The resolved zone, or UTC when this returns false.</param>
    /// <returns>True when the platform knows the id.</returns>
    /// <remarks>
    /// MEASURED ON THIS BUILD MACHINE RATHER THAN ASSUMED. Windows 11, .NET
    /// 10.0.11, ICU rather than NLS, 141 system zones: TryFindSystemTimeZoneById
    /// resolves BOTH spellings directly - "Europe/London" and "GMT Standard
    /// Time" both return true, and the IANA one keeps its own id. So the second
    /// branch below did not fire once on this machine.
    ///
    /// It is here anyway, because the thing that turns it on is a deployment
    /// decision made somewhere else: a host running in NLS mode, or a publish
    /// with InvariantGlobalization set, has no IANA aliases at all, and every
    /// browser on earth reports IANA. Without the fallback the symptom would be
    /// that time zones simply never work on that one server, and the page would
    /// say the browser's zone was unrecognised while naming a zone that plainly
    /// exists.
    /// </remarks>
    private static bool TryResolve(string id, out TimeZoneInfo zone)
    {
        if (TimeZoneInfo.TryFindSystemTimeZoneById(id, out TimeZoneInfo? found))
        {
            zone = found;
            return true;
        }

        if (TimeZoneInfo.TryConvertIanaIdToWindowsId(id, out string? windowsId) &&
            TimeZoneInfo.TryFindSystemTimeZoneById(windowsId, out found))
        {
            zone = found;
            return true;
        }

        zone = TimeZoneInfo.Utc;
        return false;
    }

    /// <summary>Screens a cookie value before it is looked up or printed.</summary>
    /// <param name="value">The trimmed candidate.</param>
    /// <returns>True when it is the shape of a zone id.</returns>
    /// <remarks>
    /// Not a security boundary on its own - Razor encodes everything this class
    /// hands it, so an unscreened value could not have executed either. It is
    /// here so that the page can safely REPEAT a rejected id, which is the
    /// difference between "your browser sent Europe/Londn, which this server
    /// does not know" and a paragraph of somebody else's text on an operations
    /// dashboard. Parentheses are allowed because real Windows ids contain them:
    /// "Central Standard Time (Mexico)".
    /// </remarks>
    private static bool LooksLikeZoneId(string value)
    {
        if (value.Length == 0 || value.Length > MaxZoneIdLength)
        {
            return false;
        }

        foreach (char c in value)
        {
            bool allowed =
                (c >= 'A' && c <= 'Z') ||
                (c >= 'a' && c <= 'z') ||
                (c >= '0' && c <= '9') ||
                c == '/' || c == '_' || c == '+' || c == '-' || c == '.' ||
                c == ' ' || c == '(' || c == ')';

            if (!allowed)
            {
                return false;
            }
        }

        return true;
    }
}
