using System.Globalization;
using System.Text;
using Microsoft.AspNetCore.Html;

namespace GmmPanel.Pages;

// Player-count graphs as plain SVG drawn on the server (no chart library, nothing for the CSP to refuse).
// The 5-minute samples are averaged into buckets (10 minutes for a day, an hour for a week) so the line reads as a trend,
// not as noise; the height is scaled to the busiest bucket, not the server's slots, so the shape fills the box. A missing
// bucket (the server was offline) breaks the line. The day and hour marks under it are added by panel.js, in each
// viewer's own time zone and language, from data-from / data-to / data-marks.
public static class Graph
{
    public static IHtmlContent Players(IReadOnlyList<(DateTimeOffset At, int Players, int Max)> samples, DateTimeOffset from, DateTimeOffset to,
        int width, int height, string css, string label)
    {
        double total = Math.Max(1, (to - from).TotalSeconds);
        bool week = total > 2 * 86400;
        TimeSpan bucket = week ? TimeSpan.FromHours(1) : TimeSpan.FromMinutes(10);
        int count = (int)Math.Ceiling(total / bucket.TotalSeconds);

        // Average per bucket; null where the server sent nothing (offline)
        var sum = new double[count];
        var n = new int[count];
        foreach (var s in samples)
        {
            if (s.At < from || s.At > to)
                continue;
            int i = Math.Min(count - 1, (int)((s.At - from).TotalSeconds / bucket.TotalSeconds));
            sum[i] += s.Players;
            n[i]++;
        }
        var value = new double?[count];
        for (int i = 0; i < count; i++)
            value[i] = n[i] > 0 ? sum[i] / n[i] : null;

        double peak = value.Max(v => v ?? 0);
        double top = Math.Max(1, peak * 1.15);   // A little headroom over the busiest bucket
        string X(int i) => ((i + 0.5) * bucket.TotalSeconds / total * width).ToString("0.#", CultureInfo.InvariantCulture);
        string Y(double players) => (height - 1 - players / top * (height - 6)).ToString("0.#", CultureInfo.InvariantCulture);

        var line = new StringBuilder();
        var area = new StringBuilder();
        int? runStart = null, runEnd = null;
        for (int i = 0; i < count; i++)
        {
            if (value[i] is not double v)
            {
                if (runStart != null)
                    area.Append($"L{X(runEnd!.Value)},{height} L{X(runStart.Value)},{height} Z ");
                runStart = null;
                continue;
            }
            if (runStart == null)
            {
                runStart = i;
                line.Append($"M{X(i)},{Y(v)} ");
                area.Append($"M{X(i)},{height} L{X(i)},{Y(v)} ");
            }
            else
            {
                line.Append($"L{X(i)},{Y(v)} ");
                area.Append($"L{X(i)},{Y(v)} ");
            }
            runEnd = i;
        }
        if (runStart != null)
            area.Append($"L{X(runEnd!.Value)},{height} L{X(runStart.Value)},{height} Z");

        return new HtmlString(
            $"<div class=\"graph-wrap\" data-from=\"{from.ToUnixTimeMilliseconds()}\" data-to=\"{to.ToUnixTimeMilliseconds()}\" data-marks=\"{(week ? "days" : "hours")}\">" +
            $"<svg class=\"{css}\" viewBox=\"0 0 {width} {height}\" preserveAspectRatio=\"none\" role=\"img\" aria-label=\"{System.Net.WebUtility.HtmlEncode(label)}\">" +
            (line.Length == 0 ? "" : $"<path class=\"area\" d=\"{area}\"/><path class=\"line\" d=\"{line}\"/>") +
            "</svg><div class=\"graph-axis\" aria-hidden=\"true\"></div></div>");
    }
}
