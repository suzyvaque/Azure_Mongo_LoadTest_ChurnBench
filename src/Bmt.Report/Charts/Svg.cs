using System.Globalization;
using System.Text;

namespace Bmt.Report.Charts;

/// <summary>A single named line series for <see cref="Svg.LineChart"/>.</summary>
public sealed record LineSeries(string Name, string Color, IReadOnlyList<(double X, double Y)> Points);

/// <summary>A single named value for <see cref="Svg.GroupedBarChart"/> (one bar within a group).</summary>
public sealed record BarValue(string Series, string Color, double Value);

/// <summary>A labeled group of bars (e.g. one operation, with p50/p95/p99/p99.9 bars).</summary>
public sealed record BarGroup(string Label, IReadOnlyList<BarValue> Values);

/// <summary>
/// Minimal dependency-free inline-SVG chart generator (line + grouped bar) so the HTML report is fully
/// self-contained (§8.1: no external dependencies, openable locally). Renders axes, gridlines, a legend
/// and the data; all sizing is in user units that scale with the SVG viewBox.
/// </summary>
public static class Svg
{
    private const int Width = 860;
    private const int Height = 320;
    private const int Left = 60;
    private const int Right = 20;
    private const int Top = 20;
    private const int Bottom = 50;

    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    public static string LineChart(string title, string xLabel, string yLabel, IReadOnlyList<LineSeries> series)
    {
        var plotW = Width - Left - Right;
        var plotH = Height - Top - Bottom;

        var allPts = series.SelectMany(s => s.Points).ToList();
        var maxX = allPts.Count > 0 ? allPts.Max(p => p.X) : 1;
        var maxY = allPts.Count > 0 ? allPts.Max(p => p.Y) : 1;
        if (maxX <= 0)
        {
            maxX = 1;
        }

        if (maxY <= 0)
        {
            maxY = 1;
        }

        var sb = new StringBuilder();
        sb.Append(CultureInfo.InvariantCulture, $"<svg class=\"chart\" viewBox=\"0 0 {Width} {Height}\" role=\"img\" aria-label=\"{Esc(title)}\">");
        sb.Append(CultureInfo.InvariantCulture, $"<text x=\"{Width / 2}\" y=\"14\" class=\"ct\" text-anchor=\"middle\">{Esc(title)}</text>");

        // Axes.
        sb.Append(CultureInfo.InvariantCulture, $"<line x1=\"{Left}\" y1=\"{Top}\" x2=\"{Left}\" y2=\"{Top + plotH}\" class=\"axis\"/>");
        sb.Append(CultureInfo.InvariantCulture, $"<line x1=\"{Left}\" y1=\"{Top + plotH}\" x2=\"{Left + plotW}\" y2=\"{Top + plotH}\" class=\"axis\"/>");

        // Y gridlines + ticks (5 divisions).
        for (var i = 0; i <= 5; i++)
        {
            var y = Top + plotH - (plotH * i / 5.0);
            var val = maxY * i / 5.0;
            sb.Append(CultureInfo.InvariantCulture, $"<line x1=\"{Left}\" y1=\"{F(y)}\" x2=\"{Left + plotW}\" y2=\"{F(y)}\" class=\"grid\"/>");
            sb.Append(CultureInfo.InvariantCulture, $"<text x=\"{Left - 6}\" y=\"{F(y + 3)}\" class=\"tick\" text-anchor=\"end\">{FmtNum(val)}</text>");
        }

        // X ticks (6 divisions).
        for (var i = 0; i <= 6; i++)
        {
            var x = Left + (plotW * i / 6.0);
            var val = maxX * i / 6.0;
            sb.Append(CultureInfo.InvariantCulture, $"<text x=\"{F(x)}\" y=\"{Top + plotH + 16}\" class=\"tick\" text-anchor=\"middle\">{FmtNum(val)}</text>");
        }

        sb.Append(CultureInfo.InvariantCulture, $"<text x=\"{Left + plotW / 2}\" y=\"{Height - 6}\" class=\"alabel\" text-anchor=\"middle\">{Esc(xLabel)}</text>");
        sb.Append(CultureInfo.InvariantCulture, $"<text x=\"14\" y=\"{Top + plotH / 2}\" class=\"alabel\" text-anchor=\"middle\" transform=\"rotate(-90 14 {Top + plotH / 2})\">{Esc(yLabel)}</text>");

        foreach (var s in series)
        {
            if (s.Points.Count == 0)
            {
                continue;
            }

            var path = new StringBuilder();
            for (var i = 0; i < s.Points.Count; i++)
            {
                var (px, py) = s.Points[i];
                var x = Left + plotW * (px / maxX);
                var y = Top + plotH - plotH * (py / maxY);
                path.Append(i == 0 ? 'M' : 'L').Append(F(x)).Append(' ').Append(F(y)).Append(' ');
            }

            sb.Append(CultureInfo.InvariantCulture, $"<path d=\"{path.ToString().Trim()}\" fill=\"none\" stroke=\"{s.Color}\" stroke-width=\"1.6\"/>");
        }

        AppendLegend(sb, series.Select(s => (s.Name, s.Color)).ToList(), plotW);
        sb.Append("</svg>");
        return sb.ToString();
    }

    public static string GroupedBarChart(string title, string yLabel, IReadOnlyList<BarGroup> groups, IReadOnlyList<(string Name, string Color)> legend)
    {
        var plotW = Width - Left - Right;
        var plotH = Height - Top - Bottom;

        var maxY = groups.SelectMany(g => g.Values).Select(v => v.Value).DefaultIfEmpty(1).Max();
        if (maxY <= 0)
        {
            maxY = 1;
        }

        var sb = new StringBuilder();
        sb.Append(CultureInfo.InvariantCulture, $"<svg class=\"chart\" viewBox=\"0 0 {Width} {Height}\" role=\"img\" aria-label=\"{Esc(title)}\">");
        sb.Append(CultureInfo.InvariantCulture, $"<text x=\"{Width / 2}\" y=\"14\" class=\"ct\" text-anchor=\"middle\">{Esc(title)}</text>");

        sb.Append(CultureInfo.InvariantCulture, $"<line x1=\"{Left}\" y1=\"{Top}\" x2=\"{Left}\" y2=\"{Top + plotH}\" class=\"axis\"/>");
        sb.Append(CultureInfo.InvariantCulture, $"<line x1=\"{Left}\" y1=\"{Top + plotH}\" x2=\"{Left + plotW}\" y2=\"{Top + plotH}\" class=\"axis\"/>");

        for (var i = 0; i <= 5; i++)
        {
            var y = Top + plotH - (plotH * i / 5.0);
            var val = maxY * i / 5.0;
            sb.Append(CultureInfo.InvariantCulture, $"<line x1=\"{Left}\" y1=\"{F(y)}\" x2=\"{Left + plotW}\" y2=\"{F(y)}\" class=\"grid\"/>");
            sb.Append(CultureInfo.InvariantCulture, $"<text x=\"{Left - 6}\" y=\"{F(y + 3)}\" class=\"tick\" text-anchor=\"end\">{FmtNum(val)}</text>");
        }

        sb.Append(CultureInfo.InvariantCulture, $"<text x=\"14\" y=\"{Top + plotH / 2}\" class=\"alabel\" text-anchor=\"middle\" transform=\"rotate(-90 14 {Top + plotH / 2})\">{Esc(yLabel)}</text>");

        var groupCount = Math.Max(1, groups.Count);
        var groupW = plotW / (double)groupCount;
        for (var gi = 0; gi < groups.Count; gi++)
        {
            var g = groups[gi];
            var gx = Left + groupW * gi;
            var inner = groupW * 0.8;
            var pad = groupW * 0.1;
            var barW = g.Values.Count > 0 ? inner / g.Values.Count : inner;
            for (var bi = 0; bi < g.Values.Count; bi++)
            {
                var v = g.Values[bi];
                var h = plotH * (v.Value / maxY);
                var bx = gx + pad + barW * bi;
                var by = Top + plotH - h;
                sb.Append(CultureInfo.InvariantCulture,
                    $"<rect x=\"{F(bx)}\" y=\"{F(by)}\" width=\"{F(barW * 0.92)}\" height=\"{F(h)}\" fill=\"{v.Color}\"><title>{Esc(g.Label)} {Esc(v.Series)}: {FmtNum(v.Value)}</title></rect>");
            }

            sb.Append(CultureInfo.InvariantCulture, $"<text x=\"{F(gx + groupW / 2)}\" y=\"{Top + plotH + 16}\" class=\"tick\" text-anchor=\"middle\">{Esc(g.Label)}</text>");
        }

        AppendLegend(sb, legend, plotW);
        sb.Append("</svg>");
        return sb.ToString();
    }

    private static void AppendLegend(StringBuilder sb, IReadOnlyList<(string Name, string Color)> legend, int plotW)
    {
        var lx = Left + 8;
        var ly = Top + 6;
        foreach (var (name, color) in legend)
        {
            sb.Append(CultureInfo.InvariantCulture, $"<rect x=\"{lx}\" y=\"{ly - 8}\" width=\"10\" height=\"10\" fill=\"{color}\"/>");
            sb.Append(CultureInfo.InvariantCulture, $"<text x=\"{lx + 14}\" y=\"{ly + 1}\" class=\"leg\">{Esc(name)}</text>");
            lx += 14 + 8 + Math.Max(40, name.Length * 7);
            if (lx > Left + plotW - 80)
            {
                lx = Left + 8;
                ly += 14;
            }
        }
    }

    private static string F(double v) => v.ToString("0.##", Inv);

    private static string FmtNum(double v)
    {
        if (Math.Abs(v) >= 1000)
        {
            return v.ToString("N0", Inv);
        }

        return Math.Abs(v - Math.Round(v)) < 0.05 ? v.ToString("0", Inv) : v.ToString("0.##", Inv);
    }

    public static string Esc(string? s) => string.IsNullOrEmpty(s)
        ? string.Empty
        : s.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;").Replace("\"", "&quot;");
}
