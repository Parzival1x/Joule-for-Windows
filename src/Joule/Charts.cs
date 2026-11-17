using System.Globalization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using Joule.Core;

namespace Joule;

/// <summary>Shared drawing helpers for the charts.</summary>
public abstract class Chart : FrameworkElement
{
    protected static FormattedText Ft(string text, double size, Brush? brush = null, FontWeight? weight = null) =>
        new(text, CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
            new Typeface(Theme.Font, FontStyles.Normal, weight ?? FontWeights.Normal, FontStretches.Normal), size, brush ?? Theme.Secondary, 1.25);

    protected static void Label(DrawingContext dc, string text, double x, double y, double size = 11, Brush? brush = null, TextAlignment align = TextAlignment.Left)
    {
        var ft = Ft(text, size, brush);
        var dx = align switch { TextAlignment.Center => -ft.Width / 2, TextAlignment.Right => -ft.Width, _ => 0 };
        dc.DrawText(ft, new Point(x + dx, y));
    }

    protected static Pen Grid => new(Theme.Track, 1);

    /// <summary>Nice round steps for an axis from 0 to max.</summary>
    protected static List<double> Ticks(double max, int count)
    {
        if (max <= 0) return new() { 0 };
        var raw = max / count;
        var magnitude = Math.Pow(10, Math.Floor(Math.Log10(raw)));
        var step = new[] { 1, 2, 2.5, 5, 10 }.Select(m => m * magnitude).First(s => s >= raw);
        var ticks = new List<double>();
        for (var v = 0.0; v <= max + step * 0.01; v += step) ticks.Add(v);
        return ticks;
    }

    /// <summary>Label positions along a time axis.</summary>
    public static List<(DateTime date, string text)> TimeTicks(DateRange range, Period period)
    {
        var result = new List<(DateTime, string)>();
        switch (period)
        {
            case Period.Hour:
                for (var t = new DateTime(range.Start.Year, range.Start.Month, range.Start.Day, range.Start.Hour, range.Start.Minute / 10 * 10, 0).AddMinutes(10); t < range.End; t = t.AddMinutes(10))
                    result.Add((t, Format.Time(t)));
                break;
            case Period.Today:
            case Period.Day:
                var step = period == Period.Today ? 3 : 4;
                for (var t = range.Start.Date.AddHours(Math.Ceiling(range.Start.TimeOfDay.TotalHours / step) * step); t < range.End; t = t.AddHours(step))
                    result.Add((t, t.ToString("t", CultureInfo.CurrentCulture)));
                break;
            case Period.Week:
                for (var t = range.Start.Date.AddDays(range.Start.TimeOfDay > TimeSpan.Zero ? 1 : 0); t < range.End; t = t.AddDays(1))
                    result.Add((t, Format.Day(t)));
                break;
            default:
                for (var t = range.Start.Date.AddDays(1); t < range.End; t = t.AddDays(4))
                    result.Add((t, t.ToString("MMM d", CultureInfo.CurrentCulture)));
                break;
        }
        return result;
    }

    protected void DrawTimeAxis(DrawingContext dc, Rect plot, DateRange range, Period period)
    {
        var span = Math.Max(1, range.Duration);
        foreach (var (date, text) in TimeTicks(range, period))
        {
            var x = plot.Left + (date - range.Start).TotalSeconds / span * plot.Width;
            dc.DrawLine(Grid, new Point(x, plot.Top), new Point(x, plot.Bottom));
            var ft = Ft(text, 10.5);
            var lx = Math.Clamp(x - ft.Width / 2, plot.Left, plot.Right - ft.Width);
            dc.DrawText(ft, new Point(lx, plot.Bottom + 4));
        }
    }
}

/// <summary>Recent system power, scaled to the data actually there.</summary>
public sealed class Sparkline : Chart
{
    public List<(DateTime date, double watts)> Points = new();

    protected override void OnRender(DrawingContext dc)
    {
        var w = ActualWidth; var h = ActualHeight;
        dc.DrawRectangle(Brushes.Transparent, null, new Rect(0, 0, w, h));
        if (Points.Count < 2)
        {
            Label(dc, "History is filling up …", w / 2, h / 2 - 8, 11, Theme.Tertiary, TextAlignment.Center);
            return;
        }
        var start = Points[0].date;
        var end = Time.Max(Points[^1].date, start.AddSeconds(60));
        var span = (end - start).TotalSeconds;
        var max = Math.Max(1, Points.Max(p => p.watts) * 1.15);
        Point P((DateTime date, double watts) p) => new((p.date - start).TotalSeconds / span * w, h - p.watts / max * h);
        var area = new StreamGeometry();
        using (var ctx = area.Open())
        {
            ctx.BeginFigure(new Point(P(Points[0]).X, h), true, true);
            foreach (var p in Points) ctx.LineTo(P(p), true, true);
            ctx.LineTo(new Point(P(Points[^1]).X, h), true, true);
        }
        var fill = new LinearGradientBrush(Color.FromArgb(90, Theme.AccentColor.R, Theme.AccentColor.G, Theme.AccentColor.B),
            Color.FromArgb(5, Theme.AccentColor.R, Theme.AccentColor.G, Theme.AccentColor.B), 90);
        dc.DrawGeometry(fill, null, area);
        var line = new StreamGeometry();
        using (var ctx = line.Open())
        {
            ctx.BeginFigure(P(Points[0]), false, false);
            foreach (var p in Points.Skip(1)) ctx.LineTo(P(p), true, true);
        }
        dc.DrawGeometry(null, new Pen(Theme.Accent, 1.5) { LineJoin = PenLineJoin.Round }, line);
    }
}

/// <summary>Parts of a whole as one capsule.</summary>
public sealed class SplitBar : Chart
{
    public List<(double value, Brush brush)> Parts = new();

    protected override void OnRender(DrawingContext dc)
    {
        var w = ActualWidth; var h = ActualHeight;
        var clip = new RectangleGeometry(new Rect(0, 0, w, h), h / 2, h / 2);
        dc.PushClip(clip);
        var sum = Parts.Sum(p => Math.Max(0, p.value));
        if (sum <= 0.0001) dc.DrawRectangle(Theme.Track, null, new Rect(0, 0, w, h));
        else
        {
            var gaps = Math.Max(0, Parts.Count(p => p.value > 0) - 1) * 2;
            var x = 0.0;
            foreach (var (value, brush) in Parts)
            {
                if (value <= 0) continue;
                var width = Math.Max(value > 0.01 ? 3 : 0, (w - gaps) * value / sum);
                dc.DrawRectangle(brush, null, new Rect(x, 0, width, h));
                x += width + 2;
            }
        }
        dc.Pop();
    }
}

/// <summary>Battery line and power over a session, with sleep shaded.</summary>
public sealed class SessionChart : Chart
{
    public SessionTimeline Timeline = null!;

    protected override void OnRender(DrawingContext dc)
    {
        var w = ActualWidth; var h = ActualHeight;
        var points = Timeline.Points;
        if (points.Count < 2) return;
        var span = Math.Max(60, (Timeline.End - Timeline.Start).TotalSeconds);
        double X(DateTime d) => (d - Timeline.Start).TotalSeconds / span * w;
        double low = points.Min(p => p.Percent) - 1, high = points.Max(p => p.Percent) + 1;
        var maxWatts = Math.Max(1, points.Max(p => p.Watts));
        double PY(double percent) => 8 + (high - percent) / Math.Max(1, high - low) * h * 0.5;
        double WY(double watts) => h - watts / maxWatts * h * 0.42;

        // Stretches without samples longer than a few minutes: the PC slept (or Joule was off).
        for (var i = 1; i < points.Count; i++)
        {
            if ((points[i].Date - points[i - 1].Date).TotalSeconds <= 300) continue;
            double x0 = X(points[i - 1].Date), x1 = X(points[i].Date.AddSeconds(-60));
            dc.DrawRoundedRectangle(Theme.WithOpacity(Theme.Indigo, 0.08), null, new Rect(x0, 0, Math.Max(2, x1 - x0), h), 6, 6);
            if (x1 - x0 > 90) Label(dc, "☾ " + Format.Duration((points[i].Date - points[i - 1].Date).TotalSeconds - 60), (x0 + x1) / 2, h - 22, 11, Theme.Indigo, TextAlignment.Center);
        }
        // Power area, split at gaps.
        var segment = new List<SessionTimeline.Point>();
        void FlushArea()
        {
            if (segment.Count == 0) return;
            var g = new StreamGeometry();
            using (var ctx = g.Open())
            {
                ctx.BeginFigure(new Point(X(segment[0].Date), h), true, true);
                foreach (var p in segment) ctx.LineTo(new Point(X(p.Date), WY(p.Watts)), true, false);
                ctx.LineTo(new Point(X(segment[^1].Date), h), true, false);
            }
            dc.DrawGeometry(Theme.WithOpacity(Theme.Accent, 0.16), null, g);
            segment.Clear();
        }
        foreach (var p in points)
        {
            if (segment.Count > 0 && (p.Date - segment[^1].Date).TotalSeconds > 300) FlushArea();
            segment.Add(p);
        }
        FlushArea();
        var line = new StreamGeometry();
        using (var ctx = line.Open())
        {
            ctx.BeginFigure(new Point(X(points[0].Date), PY(points[0].Percent)), false, false);
            foreach (var p in points.Skip(1)) ctx.LineTo(new Point(X(p.Date), PY(p.Percent)), true, true);
        }
        dc.DrawGeometry(null, new Pen(Theme.Charging, 3) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round, LineJoin = PenLineJoin.Round }, line);
        Label(dc, Format.Percent(points[0].Percent), X(points[0].Date), PY(points[0].Percent) - 18);
        Label(dc, Format.Percent(points[^1].Percent), Math.Min(w - 36, X(points[^1].Date) - 30), PY(points[^1].Percent) + 6);
    }
}

/// <summary>Blocks on a track, e.g. when an app was in front.</summary>
public sealed class LaneBar : Chart
{
    public DateTime Start, End;
    public List<DateRange> Blocks = new();
    public Brush Color = Brushes.Gray;

    protected override void OnRender(DrawingContext dc)
    {
        var w = ActualWidth; var h = ActualHeight;
        dc.DrawRoundedRectangle(Theme.Track, null, new Rect(0, 0, w, h), 5, 5);
        var span = Math.Max(60, (End - Start).TotalSeconds);
        foreach (var block in Blocks)
        {
            var x0 = (block.Start - Start).TotalSeconds / span * w;
            var x1 = (block.End - Start).TotalSeconds / span * w;
            dc.DrawRoundedRectangle(Color, null, new Rect(x0, 2, Math.Max(2, x1 - x0), h - 4), 4, 4);
        }
    }
}

/// <summary>Evenly spread labels: first left-aligned, last right-aligned.</summary>
public sealed class SpreadLabels : Chart
{
    public List<string> Items = new();

    protected override Size MeasureOverride(Size availableSize) => new(0, 16);

    protected override void OnRender(DrawingContext dc)
    {
        var w = ActualWidth;
        for (var i = 0; i < Items.Count; i++)
        {
            var f = Items.Count == 1 ? 0 : (double)i / (Items.Count - 1);
            var align = i == 0 ? TextAlignment.Left : (i == Items.Count - 1 ? TextAlignment.Right : TextAlignment.Center);
            Label(dc, Items[i], f * w, 0, 11, Theme.Secondary, align);
        }
    }
}

/// <summary>Battery level, broken into segments by power source, with sleep shaded.</summary>
public sealed class BatteryChart : Chart
{
    public Report Report = null!;
    public Period Period;

    protected override void OnRender(DrawingContext dc)
    {
        var plot = new Rect(0, 4, Math.Max(10, ActualWidth - 44), Math.Max(10, ActualHeight - 24));
        var range = Report.Range;
        var span = Math.Max(1, range.Duration);
        double X(DateTime d) => plot.Left + (d - range.Start).TotalSeconds / span * plot.Width;
        double Y(double p) => plot.Bottom - p / 100 * plot.Height;
        foreach (var v in new[] { 0, 25, 50, 75, 100 })
        {
            dc.DrawLine(Grid, new Point(plot.Left, Y(v)), new Point(plot.Right, Y(v)));
            Label(dc, $"{v} %", plot.Right + 6, Y(v) - 8, 10.5);
        }
        DrawTimeAxis(dc, plot, range, Period);
        foreach (var sleep in Report.Sleeps)
        {
            double x0 = X(Time.Max(sleep.Start, range.Start)), x1 = X(Time.Min(sleep.End, range.End));
            if (x1 > x0) dc.DrawRectangle(Theme.WithOpacity(Theme.Secondary, 0.10), null, new Rect(x0, plot.Top, x1 - x0, plot.Height));
        }
        // Segments: a new one at gaps over five minutes and when the source changes.
        var segments = new List<(bool battery, List<BatteryPoint> points)>();
        BatteryPoint? last = null;
        foreach (var point in Report.Battery)
        {
            var gap = last == null || (point.Date - last.Date).TotalMinutes > 5;
            if (gap || last!.Source.OnBattery() != point.Source.OnBattery())
            {
                var bridge = !gap && last != null ? new List<BatteryPoint> { last } : new List<BatteryPoint>();
                bridge.Add(point);
                segments.Add((point.Source.OnBattery(), bridge));
            }
            else segments[^1].points.Add(point);
            last = point;
        }
        foreach (var (battery, points) in segments)
        {
            var color = battery ? Theme.Accent : Theme.Charging;
            if (points.Count == 1) { dc.DrawEllipse(color, null, new Point(X(points[0].Date), Y(points[0].Percent)), 1.5, 1.5); continue; }
            var area = new StreamGeometry();
            using (var ctx = area.Open())
            {
                ctx.BeginFigure(new Point(X(points[0].Date), plot.Bottom), true, true);
                foreach (var p in points) ctx.LineTo(new Point(X(p.Date), Y(p.Percent)), true, false);
                ctx.LineTo(new Point(X(points[^1].Date), plot.Bottom), true, false);
            }
            dc.DrawGeometry(Theme.WithOpacity(color, 0.10), null, area);
            var line = new StreamGeometry();
            using (var ctx = line.Open())
            {
                ctx.BeginFigure(new Point(X(points[0].Date), Y(points[0].Percent)), false, false);
                foreach (var p in points.Skip(1)) ctx.LineTo(new Point(X(p.Date), Y(p.Percent)), true, true);
            }
            dc.DrawGeometry(null, new Pen(color, 2) { LineJoin = PenLineJoin.Round, StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round }, line);
        }
    }
}

/// <summary>Stacked average watts per bucket, with hover readout.</summary>
public sealed class PowerChart : Chart
{
    public Report Report = null!;
    public Period Period;
    public PowerBucket? Hovered { get; private set; }
    public event Action? HoverChanged;
    private Rect _plot;

    protected override void OnRender(DrawingContext dc)
    {
        _plot = new Rect(0, 4, Math.Max(10, ActualWidth - 50), Math.Max(10, ActualHeight - 24));
        var plot = _plot;
        dc.DrawRectangle(Brushes.Transparent, null, new Rect(0, 0, ActualWidth, ActualHeight));
        var range = Report.Range;
        var span = Math.Max(1, range.Duration);
        double X(DateTime d) => plot.Left + (d - range.Start).TotalSeconds / span * plot.Width;
        var max = Math.Max(1, Report.Buckets.Select(b => b.Watts).DefaultIfEmpty(1).Max() * 1.1);
        var ticks = Ticks(max, 4);
        max = Math.Max(max, ticks[^1]);
        double Y(double w) => plot.Bottom - w / max * plot.Height;
        foreach (var t in ticks)
        {
            dc.DrawLine(Grid, new Point(plot.Left, Y(t)), new Point(plot.Right, Y(t)));
            Label(dc, Format.Watts(t), plot.Right + 6, Y(t) - 8, 10.5);
        }
        DrawTimeAxis(dc, plot, range, Period);
        var bucket = Period.Bucket();
        foreach (var b in Report.Buckets)
        {
            var x0 = X(b.Start);
            var width = Math.Max(1, X(b.Start.AddSeconds(bucket * 0.82)) - x0);
            dc.PushOpacity(Hovered == null || Hovered == b ? 1 : 0.45);
            double apps = b.AppsWatts, services = b.ServicesWatts, total = b.Watts;
            dc.DrawRectangle(Theme.Apps, null, new Rect(x0, Y(apps), width, Math.Max(0, Y(0) - Y(apps))));
            dc.DrawRectangle(Theme.Services, null, new Rect(x0, Y(apps + services), width, Math.Max(0, Y(apps) - Y(apps + services))));
            dc.DrawRectangle(Theme.Hardware, null, new Rect(x0, Y(total), width, Math.Max(0, Y(apps + services) - Y(total))));
            dc.Pop();
        }
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        var x = e.GetPosition(this).X;
        var span = Math.Max(1, Report.Range.Duration);
        var date = Report.Range.Start.AddSeconds((x - _plot.Left) / Math.Max(1, _plot.Width) * span);
        var nearest = Report.Buckets.OrderBy(b => Math.Abs((b.Start.AddSeconds(Period.Bucket() / 2) - date).TotalSeconds)).FirstOrDefault();
        if (nearest != Hovered) { Hovered = nearest; InvalidateVisual(); HoverChanged?.Invoke(); }
    }

    protected override void OnMouseLeave(MouseEventArgs e)
    {
        Hovered = null;
        InvalidateVisual();
        HoverChanged?.Invoke();
    }
}

/// <summary>One day as a strip of battery / charging / adapter / sleep blocks.</summary>
public sealed class DayStripBar : Chart
{
    public DayStrip Day = null!;

    public static Brush Color(StripKind kind) => kind switch
    {
        StripKind.Battery => Theme.Accent,
        StripKind.Charging => Theme.Charging,
        StripKind.Adapter => Theme.ChargingSoft,
        _ => Theme.IndigoSoft,
    };

    protected override void OnRender(DrawingContext dc)
    {
        var w = ActualWidth; var h = ActualHeight;
        dc.PushClip(new RectangleGeometry(new Rect(0, 0, w, h), 6, 6));
        dc.DrawRectangle(Theme.Track, null, new Rect(0, 0, w, h));
        foreach (var block in Day.Blocks)
        {
            var x0 = (block.Start - Day.Day).TotalSeconds / 86400 * w;
            var x1 = (block.End - Day.Day).TotalSeconds / 86400 * w;
            dc.DrawRectangle(Color(block.Kind), null, new Rect(x0, 0, Math.Max(1, x1 - x0), h));
        }
        dc.Pop();
    }
}

/// <summary>Drain per hour for each longer sleep, with the alert threshold.</summary>
public sealed class SleepChart : Chart
{
    public List<SleepRecord> Sleeps = new();
    public double Threshold;

    protected override void OnRender(DrawingContext dc)
    {
        var plot = new Rect(0, 8, Math.Max(10, ActualWidth - 46), Math.Max(10, ActualHeight - 28));
        if (Sleeps.Count == 0) return;
        var max = Math.Max(Threshold * 1.4, Sleeps.Max(s => s.PercentPerHour) * 1.15);
        var ticks = Ticks(max, 2);
        max = Math.Max(max, ticks[^1]);
        double Y(double v) => plot.Bottom - v / max * plot.Height;
        foreach (var t in ticks)
        {
            dc.DrawLine(Grid, new Point(plot.Left, Y(t)), new Point(plot.Right, Y(t)));
            Label(dc, Format.Percent(t, 1), plot.Right + 6, Y(t) - 8, 10.5);
        }
        var start = Sleeps.Min(s => s.Start).Date;
        var end = Time.Max(Sleeps.Max(s => s.Start).Date.AddDays(1), start.AddDays(7));
        var span = (end - start).TotalSeconds;
        double X(DateTime d) => plot.Left + 8 + (d - start).TotalSeconds / span * (plot.Width - 16);
        for (var d = start; d < end; d = d.AddDays(2)) Label(dc, d.ToString("MMM d", CultureInfo.CurrentCulture), X(d), plot.Bottom + 6, 10.5, Theme.Secondary, TextAlignment.Left);
        foreach (var s in Sleeps)
        {
            var x = X(s.Start);
            var color = s.PercentPerHour >= Threshold ? Theme.Accent : Theme.WithOpacity(Theme.Indigo, 0.7);
            var top = Y(s.PercentPerHour);
            dc.DrawRoundedRectangle(color, null, new Rect(x - 7, top, 14, Math.Max(2, plot.Bottom - top)), 3, 3);
        }
        var pen = new Pen(Theme.WithOpacity(Theme.Accent, 0.6), 1) { DashStyle = new DashStyle(new double[] { 4, 3 }, 0) };
        dc.DrawLine(pen, new Point(plot.Left, Y(Threshold)), new Point(plot.Right, Y(Threshold)));
    }
}

/// <summary>Bars of one app's energy per bucket.</summary>
public sealed class SeriesChart : Chart
{
    public List<(DateTime date, double joules)> Series = new();
    public DateRange Range;
    public Period Period;

    protected override void OnRender(DrawingContext dc)
    {
        var plot = new Rect(0, 4, Math.Max(10, ActualWidth - 56), Math.Max(10, ActualHeight - 24));
        var span = Math.Max(1, Range.Duration);
        double X(DateTime d) => plot.Left + (d - Range.Start).TotalSeconds / span * plot.Width;
        var max = Math.Max(0.001, Series.Select(s => s.joules / 3600).DefaultIfEmpty(0).Max() * 1.1);
        var ticks = Ticks(max, 3);
        max = Math.Max(max, ticks[^1]);
        double Y(double wh) => plot.Bottom - wh / max * plot.Height;
        foreach (var t in ticks)
        {
            dc.DrawLine(Grid, new Point(plot.Left, Y(t)), new Point(plot.Right, Y(t)));
            Label(dc, $"{Format.Number(t, t < 1 ? 2 : 0)} Wh", plot.Right + 6, Y(t) - 8, 10.5);
        }
        DrawTimeAxis(dc, plot, Range, Period);
        var bucket = Period.Bucket();
        foreach (var (date, joules) in Series)
        {
            if (joules <= 0) continue;
            var x0 = X(date);
            var width = Math.Max(1, X(date.AddSeconds(bucket * 0.8)) - x0);
            var top = Y(joules / 3600);
            dc.DrawRoundedRectangle(Theme.Apps, null, new Rect(x0, top, width, Math.Max(1, plot.Bottom - top)), Math.Min(2, width / 2), Math.Min(2, width / 2));
        }
    }
}

/// <summary>Battery health as a ring.</summary>
public sealed class HealthRing : Chart
{
    public double Percent;

    protected override void OnRender(DrawingContext dc)
    {
        var size = Math.Min(ActualWidth, ActualHeight);
        var thickness = 14.0;
        var r = size / 2 - thickness / 2;
        var c = new Point(ActualWidth / 2, ActualHeight / 2);
        dc.DrawEllipse(null, new Pen(Theme.Track, thickness), c, r, r);
        var fraction = Math.Clamp(Percent / 100, 0, 0.9999);
        var angle = fraction * 2 * Math.PI;
        var start = new Point(c.X, c.Y - r);
        var end = new Point(c.X + r * Math.Sin(angle), c.Y - r * Math.Cos(angle));
        var g = new StreamGeometry();
        using (var ctx = g.Open())
        {
            ctx.BeginFigure(start, false, false);
            ctx.ArcTo(end, new Size(r, r), 0, angle > Math.PI, SweepDirection.Clockwise, true, false);
        }
        dc.DrawGeometry(null, new Pen(Percent >= 80 ? Theme.Charging : Theme.Accent, thickness) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round }, g);
    }
}

/// <summary>A 0–100 track with the used range highlighted.</summary>
public sealed class RangeBar : Chart
{
    public double From, To;

    protected override void OnRender(DrawingContext dc)
    {
        var w = ActualWidth; var h = ActualHeight;
        dc.DrawRoundedRectangle(Theme.Track, null, new Rect(0, 0, w, h), h / 2, h / 2);
        var x = w * From / 100;
        var width = Math.Max(4, w * (To - From) / 100);
        var brush = new LinearGradientBrush(Theme.WithOpacity(Theme.Accent, 0.8).Color, Theme.AccentColor, 0);
        dc.DrawRoundedRectangle(brush, null, new Rect(x, 0, width, h), h / 2, h / 2);
    }
}
