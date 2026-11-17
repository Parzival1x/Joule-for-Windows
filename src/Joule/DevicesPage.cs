using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using Joule.Core;

namespace Joule;

/// <summary>Every battery in one place: this PC, Bluetooth devices, controllers, a UPS.</summary>
public sealed class DevicesPage : Page
{
    private readonly ContentControl _body = Host(0);
    private readonly ScrollViewer _root;
    private string _signature = "";
    public override FrameworkElement Root => _root;

    public DevicesPage()
    {
        var stack = Stack();
        stack.Children.Add(_body);
        _root = PageScroll(stack);
        _root.SizeChanged += (_, e) => { if (Math.Abs(e.NewSize.Width - e.PreviousSize.Width) > 1 && Columns(e.NewSize.Width) != Columns(e.PreviousSize.Width)) Render(); };
    }

    public override void Reload()
    {
        if (!M.DevicesRead || !M.Demo) M.ReadDevices();
        Render();
    }

    public override void LiveUpdate()
    {
        if (Signature() != _signature) Render();
    }

    private string Signature() =>
        string.Join("|", M.Devices.Select(d => $"{d.Id}:{d.Percent}:{d.Connected}:{d.Charging}")) + $"|away:{M.NotConnectedCount}|pc:{Math.Round(M.Power?.Percent ?? -1)}:{M.Power?.Source}|{M.Settings.HiddenDevices.Count}|{M.DevicesRead}";

    private static int Columns(double width) => width >= 1000 ? 3 : 2;

    private void Render()
    {
        _signature = Signature();
        var items = new List<UIElement?>
        {
            UI.PageHeader("Devices", M.Demo ? "Sample data" : null,
                UI.Button("Refresh", () => { M.ReadDevices(); }).Tip("Read all devices again")),
            UI.Secondary("Battery levels of this PC and every device connected to it – mice, keyboards, headphones, controllers, phones and a UPS.", 14),
        };

        var withLevel = M.Devices.Where(d => d.Percent != null).ToList();
        var withoutLevel = M.Devices.Where(d => d.Percent == null).ToList();
        var cards = new List<UIElement>();
        if (M.Power is { } power) cards.Add(PcCard(power));
        cards.AddRange(withLevel.Select(DeviceCard));
        var grid = new UniformGrid { Columns = Columns(_root.ActualWidth > 0 ? _root.ActualWidth : 1100) };
        foreach (var card in cards) grid.Children.Add(card is FrameworkElement fe ? fe.Margin(0, 0, 16, 16) : card);
        items.Add(new Border { Child = grid, Margin = new Thickness(0, 0, -16, -16) });

        if (!M.DevicesRead) items.Add(UI.EmptyNote("Looking for devices …"));
        else if (withLevel.Count == 0)
            items.Add(UI.Card(UI.HStretch(14, 1, UI.SymbolTile(Glyph.Bluetooth, Theme.Apps, 34),
                UI.V(2, UI.Text(M.Devices.Count == 0 ? "No device connected right now" : "No connected device reports a battery level", 14, FontWeights.SemiBold, wrap: true),
                    UI.Secondary("Turn on a paired mouse, keyboard, headset or controller, or connect one in Windows Settings. It appears here as soon as it’s connected.", 12.5))), 18));

        if (withoutLevel.Count > 0)
        {
            items.Add(UI.V(8,
                UI.SectionTitle("Connected without a battery level", "Windows doesn’t get a level from these devices"),
                UI.GroupedList(withoutLevel.Select(d => (UIElement)NoLevelRow(d)), 62)));
        }
        if (M.NotConnectedCount > 0)
        {
            var away = M.NotConnectedCount;
            items.Add(UI.H(8, UI.Icon(Glyph.Bluetooth, 12, Theme.Tertiary),
                UI.Secondary(away == 1 ? "1 paired device isn’t connected and isn’t shown. It appears when you turn it on."
                    : $"{away} paired devices aren’t connected and aren’t shown. They appear when you turn them on.", 12, true)));
        }
        if (M.Settings.HiddenDevices.Count > 0)
        {
            var n = M.Settings.HiddenDevices.Count;
            items.Add(UI.Link(n == 1 ? "Show 1 hidden device" : $"Show {n} hidden devices", () =>
            {
                foreach (var id in M.Settings.HiddenDevices.ToList()) M.HideDevice(id, false);
            }, 12.5));
        }
        items.Add(AboutCard());
        Set(_body, UI.V(18, items));
    }

    // MARK: Cards

    private FrameworkElement PcCard(PowerReading power)
    {
        var levels = M.Store?.PcLevels(DateTime.Now.AddDays(-7)) ?? new();
        var charging = power.Source == PowerSource.Charging;
        var status = power.Source switch
        {
            PowerSource.Battery => power.MinutesToEmpty is int m ? $"On battery · about {Format.Duration(m * 60, true)} left" : "On battery",
            PowerSource.Charging => power.MinutesToFull is int f ? $"Charging · full in {Format.Duration(f * 60, true)}" : "Charging",
            _ => "Plugged in · not charging",
        };
        return Card(Glyph.Laptop, Theme.Accent, "This PC", $"Laptop battery · health {Format.Percent(power.HealthPercent)}",
            Format.Percent(power.Percent), power.Percent, charging, status, Theme.Secondary, levels,
            $"{Format.Number(power.RemainingWh, 1)} of {Format.Number(power.FullWh, 1)} Wh", null);
    }

    private FrameworkElement DeviceCard(DeviceReading device)
    {
        var history = M.Store?.DeviceLevels(device.Id, DateTime.Now.AddDays(-30)) ?? new();
        var percent = device.Percent ?? 0;
        string status;
        Brush statusColor = Theme.Secondary;
        if (device.Charging) { status = "Charging"; statusColor = Theme.Charging; }
        else if (device.Connected) { status = "Connected"; statusColor = Theme.Charging; }
        else status = device.Updated is DateTime u ? $"Not connected · level from {Ago(u)}" : "Not connected";

        string forecast;
        if (device.Charging) forecast = "Charging now";
        else if (device.Coarse) forecast = "This controller only reports full, medium, low or empty";
        else if (DeviceForecast.PercentPerDay(history) is double rate && rate > 0)
        {
            var days = percent / rate;
            forecast = days >= 1.5 ? $"≈ {Format.Number(days, 0)} days left · about {Format.Percent(rate)} a day"
                : days * 24 >= 1 ? $"≈ {Format.Number(days * 24, 0)} hours left · about {Format.Percent(rate)} a day"
                : $"Less than an hour left · about {Format.Percent(rate)} a day";
        }
        else forecast = "A runtime estimate appears after a day of use";

        var low = !device.Charging && percent <= M.Warnings.DevicePercent;
        return Card(Glyph.For(device.Kind), low ? Theme.Red : Theme.Apps, device.Name, $"{device.Kind.Label()} · {device.Via}",
            device.LevelText, percent, device.Charging, status, statusColor, history.Where(l => l.Date >= DateTime.Now.AddDays(-7)).ToList(), forecast,
            () => M.HideDevice(device.Id, true));
    }

    private FrameworkElement Card(string glyph, Brush tint, string title, string subtitle, string level, double percent, bool charging,
        string status, Brush statusColor, List<DeviceLevel> levels, string footer, Action? hide)
    {
        var header = UI.HStretch(12, 1, UI.SymbolTile(glyph, tint, 36).Also(t => t.VerticalAlignment = VerticalAlignment.Top),
            UI.V(2, UI.Text(title, 14, FontWeights.SemiBold, wrap: true), UI.Secondary(subtitle, 11.5)));
        var levelRow = UI.H(8, UI.Title(level, 28),
            charging ? UI.Icon(Glyph.Bolt, 16, Theme.Charging).Margin(0, 4, 0, 0) : null);
        var bar = new LevelBar { Percent = percent, Charging = charging, Height = 6 };
        var chart = new DeviceLevelChart { Levels = levels, Start = DateTime.Now.AddDays(-7), End = DateTime.Now, Height = 54, Threshold = title == "This PC" ? null : M.Warnings.DevicePercent };
        chart.ToolTip = "Battery level over the last 7 days";
        var footerRow = UI.HStretch(8, 0, UI.Secondary(footer, 11.5),
            hide == null ? new Border() : UI.Link("Hide", hide, 11.5).Tip("Don’t show this device here").Also(l => l.VerticalAlignment = VerticalAlignment.Bottom));
        return UI.Card(UI.V(10, header, UI.V(2, levelRow, UI.Text(status, 12, color: statusColor, wrap: true)), bar, chart, footerRow), 18);
    }

    private FrameworkElement NoLevelRow(DeviceReading device)
    {
        string reason;
        var playStation = device.Name.Contains("DualSense", StringComparison.OrdinalIgnoreCase) || device.Name.Contains("DUALSHOCK", StringComparison.OrdinalIgnoreCase);
        if (playStation)
            reason = device.Connected
                ? "Connected in basic Bluetooth mode. The level appears once a game or Steam has switched the controller to its full mode, or over USB."
                : "Turn the controller on (or connect it by cable) and Joule reads its level.";
        else if (device.Connected) reason = "Connected, but it doesn’t report its battery to Windows.";
        else reason = "Not connected. Windows hasn’t received a battery level from it.";
        return UI.HStretch(12, 1,
            UI.SymbolTile(Glyph.For(device.Kind), Theme.Secondary, 32),
            UI.V(2, UI.Text(device.Name, 13.5, FontWeights.SemiBold, wrap: true), UI.Secondary($"{device.Kind.Label()} · {reason}", 12)),
            UI.Link("Hide", () => M.HideDevice(device.Id, true), 12)).Margin(16, 12, 16, 12);
    }

    private static FrameworkElement AboutCard()
    {
        FrameworkElement Item(string glyph, string title, string text) => UI.HStretch(12, 1,
            UI.Icon(glyph, 15, Theme.Accent).Also(i => { i.VerticalAlignment = VerticalAlignment.Top; i.Width = 20; }).Margin(0, 2, 0, 0),
            UI.V(2, UI.Text(title, 13, FontWeights.SemiBold), UI.Secondary(text, 12)));
        return UI.Card(UI.V(14,
            UI.SectionTitle("Where the levels come from"),
            UI.Columns(24,
                UI.V(14,
                    Item(Glyph.Bluetooth, "Bluetooth", "Mice, keyboards, headsets and phones report their level to Windows. Joule reads every paired device and keeps its history, and shows the ones that are connected."),
                    Item(Glyph.Controller, "Controllers", "PlayStation controllers are read directly; Xbox controllers report full, medium, low or empty.")),
                UI.V(14,
                    Item(Glyph.Battery, "UPS", "A UPS connected by USB shows up next to the laptop’s own battery."),
                    Item(Glyph.Mouse, "Logitech receivers", "Mice and keyboards on a Logi Bolt, Unifying or Lightspeed receiver are asked directly, the way Logi Options+ does."),
                    Item(Glyph.Info, "Not listed?", "Other vendors’ receivers often only tell their own software. iPhones don’t share their level with Windows.")))), 20);
    }

    private static string Ago(DateTime date)
    {
        var seconds = Math.Max(0, (DateTime.Now - date).TotalSeconds);
        if (seconds < 90) return "just now";
        if (seconds < 3600) return $"{(int)Math.Round(seconds / 60)} min ago";
        if (seconds < 86400) return $"{(int)Math.Round(seconds / 3600)} h ago";
        var days = (int)Math.Round(seconds / 86400);
        return days == 1 ? "yesterday" : $"{days} days ago";
    }
}

/// <summary>A full-width capsule for a battery level.</summary>
public sealed class LevelBar : Chart
{
    public double Percent;
    public bool Charging;

    protected override void OnRender(DrawingContext dc)
    {
        var w = ActualWidth; var h = ActualHeight;
        dc.DrawRoundedRectangle(Theme.Track, null, new Rect(0, 0, w, h), h / 2, h / 2);
        var fill = Math.Max(h, w * Math.Clamp(Percent / 100, 0, 1));
        dc.DrawRoundedRectangle(Theme.Battery(Percent, Charging), null, new Rect(0, 0, fill, h), h / 2, h / 2);
    }
}

/// <summary>A device's level over a week: green where it charged, amber otherwise, dashed alert threshold.</summary>
public sealed class DeviceLevelChart : Chart
{
    public List<DeviceLevel> Levels = new();
    public DateTime Start, End;
    public double? Threshold;

    protected override void OnRender(DrawingContext dc)
    {
        var w = ActualWidth; var h = ActualHeight;
        dc.DrawRectangle(Brushes.Transparent, null, new Rect(0, 0, w, h));
        var top = 2.0; var bottom = h - 2;
        double Y(double p) => bottom - Math.Clamp(p, 0, 100) / 100 * (bottom - top);
        var span = Math.Max(1, (End - Start).TotalSeconds);
        double X(DateTime d) => Math.Clamp((d - Start).TotalSeconds / span, 0, 1) * w;
        var points = Levels.Where(l => l.Date >= Start.AddDays(-1) && l.Date <= End).ToList();
        // Less than half a day would be a sliver at the right edge of a week. The note stands alone, without grid lines through it.
        if (points.Count == 0 || (End - points[0].Date).TotalHours < 12)
        {
            dc.DrawRoundedRectangle(Theme.WithOpacity(Theme.Track, 0.5), null, new Rect(0, 0, w, h), 8, 8);
            Label(dc, "History is filling up …", w / 2, h / 2 - 8, 11, Theme.Tertiary, TextAlignment.Center);
            return;
        }
        foreach (var p in new[] { 0.0, 50, 100 }) dc.DrawLine(new Pen(Theme.Track, 1), new Point(0, Math.Round(Y(p)) + 0.5), new Point(w, Math.Round(Y(p)) + 0.5));
        if (Threshold is double t)
        {
            var pen = new Pen(Theme.WithOpacity(Theme.Red, 0.6), 1) { DashStyle = new DashStyle(new double[] { 3, 3 }, 0) };
            dc.DrawLine(pen, new Point(0, Y(t)), new Point(w, Y(t)));
        }
        if (points.Count == 1) points.Add(points[0] with { Date = End });
        else if (points[^1].Date < End.AddMinutes(-5)) points.Add(points[^1] with { Date = End });
        var area = new StreamGeometry();
        using (var ctx = area.Open())
        {
            ctx.BeginFigure(new Point(X(points[0].Date), bottom), true, true);
            foreach (var p in points) ctx.LineTo(new Point(X(p.Date), Y(p.Percent)), true, true);
            ctx.LineTo(new Point(X(points[^1].Date), bottom), true, true);
        }
        var c = Theme.AccentColor;
        dc.DrawGeometry(new LinearGradientBrush(Color.FromArgb(70, c.R, c.G, c.B), Color.FromArgb(4, c.R, c.G, c.B), 90), null, area);
        for (var i = 1; i < points.Count; i++)
        {
            var pen = new Pen(points[i].Charging ? Theme.Charging : Theme.Accent, 1.6) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
            dc.DrawLine(pen, new Point(X(points[i - 1].Date), Y(points[i - 1].Percent)), new Point(X(points[i].Date), Y(points[i].Percent)));
        }
    }
}
