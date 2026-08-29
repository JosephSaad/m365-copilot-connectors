// ---------------------------------------------------------------------------
// TrendChart.cs
// The thirty-day trend, as SVG built from the data on the server.
//
// There is no charting library here and there must not be one. The network this
// dashboard runs on blocks outbound HTTP, so a page that loads a chart script
// from a CDN renders a broken box on the only machine it will ever run on, and
// vendoring one means a minified third-party file in a regulated estate's web
// tier for the sake of eighty bars. SVG is markup: the server already knows the
// numbers and already renders markup.
//
// It is also the accessible option almost by accident. The chart carries a
// role and a label, and the same figures appear in the table beside it, so an
// operator reading with a screen reader is not being shown a picture of data
// they cannot otherwise get.
//
// COLOUR ENCODES ONE THING: whether the day had a failure. Written and unchanged
// are separated by weight, not hue, because "which of these two bars is bigger"
// is the question and a second colour would compete with the only signal on the
// page that means something is wrong.
//
// The geometry is in a viewBox with no width attribute, so the chart scales to
// whatever column it is placed in without a media query or a resize handler.
//
// EVERY VALUE INTERPOLATED INTO THE MARKUP IS A NUMBER FORMATTED HERE, except
// the aria-label, which is HTML-encoded. Nothing from the database reaches the
// SVG as raw text.
// ---------------------------------------------------------------------------

namespace ConnectorState.Dashboard.Presentation;

using System.Globalization;
using System.Net;
using System.Text;
using ConnectorState.Dashboard.Data;
using Microsoft.AspNetCore.Html;

/// <summary>Builds the daily-activity chart as inline SVG.</summary>
public static class TrendChart
{
    private const int Width = 960;
    private const int Height = 190;
    private const int PadLeft = 52;
    private const int PadRight = 8;
    private const int PadTop = 10;
    private const int PadBottom = 26;

    /// <summary>
    /// Renders the daily-activity series. Rows may cover several connections; they
    /// are summed per day, and days with no runs are drawn as gaps rather than
    /// dropped, so a connector that stopped shows as a hole and not as a shorter
    /// chart.
    /// </summary>
    /// <param name="rows">The vwDailyActivity rows, in any order.</param>
    /// <param name="days">How many days the axis should span.</param>
    /// <param name="label">The accessible description of the chart.</param>
    /// <returns>An SVG element, or a short paragraph when there is nothing to draw.</returns>
    public static HtmlString DailyActivity(IReadOnlyList<DailyActivityRow> rows, int days, string label)
    {
        if (days < 1)
        {
            days = 30;
        }

        // Aggregate to one bucket per day. The window ends today so an empty
        // final day reads as "nothing has run yet today", which is the question
        // somebody opening this page at 09:00 is asking.
        DateTime end = DateTime.UtcNow.Date;
        DateTime start = end.AddDays(-(days - 1));

        var written = new long[days];
        var unchanged = new long[days];
        var failed = new int[days];
        var runs = new int[days];

        foreach (DailyActivityRow row in rows)
        {
            int index = (int)(row.ActivityDate.Date - start).TotalDays;

            if (index < 0 || index >= days)
            {
                continue;
            }

            written[index] += row.ItemsWritten;
            unchanged[index] += row.ItemsUnchanged;
            failed[index] += row.Failed;
            runs[index] += row.Runs;
        }

        long max = 0;

        for (int i = 0; i < days; i++)
        {
            long total = written[i] + unchanged[i];

            if (total > max)
            {
                max = total;
            }
        }

        if (max == 0)
        {
            bool anyRuns = false;

            for (int i = 0; i < days; i++)
            {
                if (runs[i] > 0)
                {
                    anyRuns = true;
                    break;
                }
            }

            string message = anyRuns
                ? "Runs in this window, but none of them wrote or matched an item."
                : "No runs in this window.";

            return new HtmlString("<p class=\"chart-empty\">" + WebUtility.HtmlEncode(message) + "</p>");
        }

        int plotWidth = Width - PadLeft - PadRight;
        int plotHeight = Height - PadTop - PadBottom;
        double slot = (double)plotWidth / days;
        double barWidth = Math.Max(1.5, slot - 2);
        int baseline = PadTop + plotHeight;

        var svg = new StringBuilder(4096);

        svg.Append(CultureInfo.InvariantCulture, $"<svg class=\"chart\" viewBox=\"0 0 {Width} {Height}\" ");
        // No preserveAspectRatio override: the default keeps the aspect ratio, so
        // the axis text scales rather than stretching. The stylesheet gives the
        // element width:100% and height:auto and the viewBox does the rest.
        svg.Append("role=\"img\" aria-label=\"");
        svg.Append(WebUtility.HtmlEncode(label));
        svg.Append("\">");

        // Gridlines at 0, half and full scale. Three is enough to read a bar
        // against and few enough not to compete with the bars.
        for (int step = 0; step <= 2; step++)
        {
            long value = max * step / 2;
            double y = baseline - ((double)value / max * plotHeight);

            svg.Append(CultureInfo.InvariantCulture,
                $"<line class=\"grid\" x1=\"{PadLeft}\" y1=\"{F(y)}\" x2=\"{Width - PadRight}\" y2=\"{F(y)}\" />");

            svg.Append(CultureInfo.InvariantCulture,
                $"<text class=\"axis\" x=\"{PadLeft - 8}\" y=\"{F(y + 3.5)}\" text-anchor=\"end\">");
            svg.Append(WebUtility.HtmlEncode(Format.Compact(value)));
            svg.Append("</text>");
        }

        for (int i = 0; i < days; i++)
        {
            double x = PadLeft + (i * slot) + ((slot - barWidth) / 2);
            long total = written[i] + unchanged[i];

            if (total > 0)
            {
                double unchangedHeight = (double)unchanged[i] / max * plotHeight;
                double writtenHeight = (double)written[i] / max * plotHeight;

                // Written sits on the baseline because it is the figure that
                // answers "did the connector do work"; unchanged stacks above it.
                if (writtenHeight > 0)
                {
                    svg.Append(CultureInfo.InvariantCulture,
                        $"<rect class=\"bar-written\" x=\"{F(x)}\" y=\"{F(baseline - writtenHeight)}\" " +
                        $"width=\"{F(barWidth)}\" height=\"{F(writtenHeight)}\" />");
                }

                if (unchangedHeight > 0)
                {
                    svg.Append(CultureInfo.InvariantCulture,
                        $"<rect class=\"bar-unchanged\" x=\"{F(x)}\" " +
                        $"y=\"{F(baseline - writtenHeight - unchangedHeight)}\" " +
                        $"width=\"{F(barWidth)}\" height=\"{F(unchangedHeight)}\" />");
                }
            }

            // A failure marker on the baseline. Deliberately below the axis so a
            // day of failures - which contributes no items and therefore no bar -
            // is still visible instead of reading as a quiet day.
            if (failed[i] > 0)
            {
                svg.Append(CultureInfo.InvariantCulture,
                    $"<rect class=\"bar-failed\" x=\"{F(x)}\" y=\"{baseline + 2}\" " +
                    $"width=\"{F(barWidth)}\" height=\"4\" />");
            }
        }

        svg.Append(CultureInfo.InvariantCulture,
            $"<line class=\"axis-line\" x1=\"{PadLeft}\" y1=\"{baseline}\" " +
            $"x2=\"{Width - PadRight}\" y2=\"{baseline}\" />");

        // Date labels at both ends and the midpoint. Thirty labels on a chart
        // this size is a grey smear.
        AppendDateLabel(svg, PadLeft, baseline, start, "start");
        AppendDateLabel(svg, PadLeft + (plotWidth / 2), baseline, start.AddDays(days / 2), "middle");
        AppendDateLabel(svg, Width - PadRight, baseline, end, "end");

        svg.Append("</svg>");

        return new HtmlString(svg.ToString());
    }

    private static void AppendDateLabel(StringBuilder svg, double x, int baseline, DateTime date, string anchor)
    {
        string textAnchor = anchor switch
        {
            "start" => "start",
            "end" => "end",
            _ => "middle",
        };

        svg.Append(CultureInfo.InvariantCulture,
            $"<text class=\"axis\" x=\"{F(x)}\" y=\"{baseline + 18}\" text-anchor=\"{textAnchor}\">");
        svg.Append(WebUtility.HtmlEncode(Format.Date(date)));
        svg.Append("</text>");
    }

    private static string F(double value) => value.ToString("0.##", CultureInfo.InvariantCulture);
}
