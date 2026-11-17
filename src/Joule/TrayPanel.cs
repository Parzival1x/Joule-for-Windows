using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Effects;
using Joule.Core;

namespace Joule;

/// <summary>The compact panel that opens from the tray icon.</summary>
public sealed class TrayPanel : Window
{
    private const int Rows = 5;
    private const double RowHeight = 26;
    private readonly Border _frame;
    private Monitor M => Monitor.Shared;
    public DateTime LastHidden { get; private set; }

    public TrayPanel()
    {
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ShowInTaskbar = false;
        Topmost = true;
        ResizeMode = ResizeMode.NoResize;
        SizeToContent = SizeToContent.Height;
        UseLayoutRounding = true;
        _frame = new Border { Margin = new Thickness(12), LayoutTransform = UiScale.Transform() };
        Width = 360 * UiScale.Current;
        Content = _frame;
        UiScale.Changed += () => { _frame.LayoutTransform = UiScale.Transform(); Width = 360 * UiScale.Current; };
        Deactivated += (_, _) => HidePanel();
        IsVisibleChanged += (_, _) => M.UpdateLive();
        M.LiveChanged += () => { if (IsVisible) Render(); };
        M.Changed += () => { if (IsVisible) Render(); };
        PreviewKeyDown += (_, e) => { if (e.Key == System.Windows.Input.Key.Escape) HidePanel(); };
    }

    public void HidePanel()
    {
        if (!IsVisible) return;
        LastHidden = DateTime.Now;
        Hide();
    }

    public void Toggle()
    {
        if (IsVisible) { HidePanel(); return; }
        // A click on the tray icon first deactivates (hides) the panel; don't reopen it right away.
        if ((DateTime.Now - LastHidden).TotalMilliseconds < 300) return;
        Render();
        Show();
        UpdateLayout();
        Place();
        Activate();
    }

    /// <summary>
    /// Next to the taskbar on the primary screen. Measured in physical pixels and converted with this window's own DPI,
    /// so it lands correctly at any scaling, also when the panel's screen has a different DPI than the system.
    /// </summary>
    private void Place()
    {
        var screen = System.Windows.Forms.Screen.PrimaryScreen!;
        var dpi = VisualTreeHelper.GetDpi(this);
        Rect Dip(System.Drawing.Rectangle r) => new(r.X / dpi.DpiScaleX, r.Y / dpi.DpiScaleY, r.Width / dpi.DpiScaleX, r.Height / dpi.DpiScaleY);
        var area = Dip(screen.WorkingArea);
        var bounds = Dip(screen.Bounds);
        var h = ActualHeight;
        if (area.Top > bounds.Top) { Top = area.Top; Left = area.Right - Width; }           // taskbar at the top
        else if (area.Left > bounds.Left) { Left = area.Left; Top = area.Bottom - h; }      // left
        else { Left = area.Right - Width; Top = area.Bottom - h; }                           // bottom or right
        Top = Math.Max(area.Top, Top);
    }

    public void Render()
    {
        var items = new List<UIElement?> { Header() };
        if (M.Power is { } power) items.Add(Views.PowerSplit(power.SystemWatts, M.LiveSplit, true));
        items.Add(new Sparkline { Points = M.RecentPower, Height = 40 });
        items.Add(SessionBox());
        items.Add(Apps());
        items.Add(SleepRow());
        items.Add(DevicesRow());
        items.Add(AlertsRow());
        items.Add(UI.Divider());
        items.Add(UI.H(8,
            UI.Link("Open Joule", () => { HidePanel(); App.Current.ShowMain("now"); }, 12.5, Theme.Primary),
            UI.Fill,
            UI.Clickable(UI.Icon(Glyph.Settings, 13).Margin(6, 4, 6, 4), () => { HidePanel(); App.Current.ShowSettings(0); }, new CornerRadius(5)).Tip("Settings"),
            UI.Clickable(UI.Icon(Glyph.Power, 13).Margin(6, 4, 6, 4), () => App.Current.Quit(), new CornerRadius(5)).Tip("Quit Joule")));
        _frame.Child = new Border
        {
            Background = Theme.Card, CornerRadius = new CornerRadius(10), BorderBrush = Theme.CardEdge, BorderThickness = new Thickness(1),
            Padding = new Thickness(16),
            Effect = new DropShadowEffect { BlurRadius = 16, ShadowDepth = 3, Opacity = 0.28, Direction = 270 },
            // Taller than the screen (many devices on a small display): scroll inside instead of running off the edge.
            Child = new ScrollViewer { Content = UI.V(12, items), VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, Focusable = false },
        };
        MaxHeight = SystemParameters.WorkArea.Height - 4;
    }

    private FrameworkElement Header()
    {
        if (M.Power is not { } power) return UI.Text(M.NoBattery ? "No battery found" : "Reading battery …", 13).Also(t => t.Height = 40);
        var glyph = new BatteryGlyph { Percent = power.Percent, Charging = power.Source == PowerSource.Charging, Plugged = power.Source == PowerSource.Adapter, Width = 42, Height = 20 };
        return UI.H(12, glyph,
            UI.V(1, UI.Text(Format.Percent(power.Percent), 20, FontWeights.SemiBold), UI.Secondary(power.ShortStateText, 11, false)),
            UI.Fill,
            UI.V(1, UI.Text((power.Estimated ? "≈ " : "") + Format.Watts(power.SystemWatts), 20, FontWeights.SemiBold, Theme.Accent).Right(),
                UI.Secondary("whole PC", 11, false).Right())).Also(g => g.Height = 40);
    }

    private FrameworkElement SessionBox()
    {
        string title, text;
        if (M.CurrentSession is { } session && session.Session.Ongoing)
        {
            title = $"Since unplugging −{Format.Number(session.PercentUsed, 0)} %";
            text = Narrative.Headline(session);
        }
        else if (M.Power?.Source == PowerSource.Battery)
        {
            title = "On battery";
            text = "The breakdown appears after the first percent.";
        }
        else
        {
            title = "On power adapter";
            text = "Once you unplug, Joule shows where the battery goes.";
        }
        var content = UI.V(3, UI.Text(title, 12, FontWeights.SemiBold), UI.Secondary(text, 11.5).Also(t => t.MaxHeight = 32));
        content.Height = 50;
        return UI.Clickable(new Border { Child = content, Padding = new Thickness(12, 8, 12, 8) }, () => { HidePanel(); App.Current.ShowMain("now"); },
            new CornerRadius(10), Theme.AccentSoft);
    }

    private FrameworkElement Apps()
    {
        var list = new StackPanel { Height = RowHeight * Rows };
        if (M.LiveApps.Count == 0) list.Children.Add(UI.Secondary("Measuring …", 12, false).Also(t => t.Height = RowHeight));
        else
        {
            var maximum = Math.Max(1, M.LiveApps.Max(a => a.Watts));
            foreach (var app in M.LiveApps.Take(Rows)) list.Children.Add(Views.LiveAppRow(app, maximum, true).Also(r => r.Height = RowHeight));
        }
        return UI.V(4, UI.H(8, UI.Text("Apps", 11, FontWeights.SemiBold, Theme.Secondary), UI.Fill, UI.Text("Processor energy", 10.5, color: Theme.Tertiary)), list);
    }

    private FrameworkElement SleepRow()
    {
        if (M.BlockersUnavailable)
            return UI.H(8, UI.Icon(Glyph.Shield, 12, Theme.Tertiary), UI.Secondary("Sleep blockers need administrator rights", 12, false)).Also(g => g.Height = 20);
        var blocker = M.Blockers.FirstOrDefault();
        if (blocker == null) return UI.H(8, UI.Icon(Glyph.Moon, 12), UI.Secondary("Nothing is keeping the PC awake", 12, false)).Also(g => g.Height = 20);
        return UI.H(8, UI.Icon(Glyph.Sun, 12, Theme.Brown), UI.Text($"{blocker.App.Name} keeps the PC awake", 12), UI.Fill,
            blocker.Since is DateTime since ? UI.Secondary($"for {Format.Duration((DateTime.Now - since).TotalSeconds)}", 12, false) : null).Also(g => g.Height = 20);
    }

    private const int DeviceRows = 6;

    /// <summary>
    /// Every connected device the way Windows' battery flyout shows them: icon, name, level, a bar, and a line with
    /// the connection and charging state or the time left. Names and details wrap instead of being cut off.
    /// </summary>
    private FrameworkElement? DevicesRow()
    {
        if (!M.DevicesRead) return null;
        var devices = M.Devices;
        var header = UI.H(8, UI.Text("Devices", 11, FontWeights.SemiBold, Theme.Secondary), UI.Fill,
            M.AverageLevel is double average ? UI.Text($"average {Format.Percent(average)}", 10.5, color: Theme.Tertiary) : null);
        if (devices.Count == 0)
            return UI.V(4, header, UI.Secondary("No device connected right now.", 12, false).Also(t => t.Margin = new Thickness(0, 2, 0, 0)));
        var rows = new StackPanel();
        foreach (var device in devices.Take(DeviceRows)) rows.Children.Add(DeviceRow(device));
        var more = devices.Count - DeviceRows;
        var footer = UI.H(8, UI.Link(more > 0 ? $"{more} more on the Devices page" : "Devices page", () => { HidePanel(); App.Current.ShowMain("devices"); }, 11.5), UI.Fill,
            M.NotConnectedCount > 0 ? UI.Text($"{M.NotConnectedCount} paired, not connected", 10.5, color: Theme.Tertiary) : null);
        return UI.V(4, header, rows, footer);
    }

    private FrameworkElement DeviceRow(DeviceReading device)
    {
        var low = device.Percent is double p && !device.Charging && p <= M.Warnings.DevicePercent;
        var tint = low ? Theme.Red : Theme.Apps;
        string detail;
        if (device.Percent == null) detail = $"{device.Via} · doesn’t report a battery level";
        else if (device.Charging) detail = $"{device.Via} · charging";
        else if (low) detail = $"{device.Via} · low – charge it soon";
        else detail = M.TimeLeft(device) is { } left ? $"{device.Via} · {left}" : device.Via;

        var level = device.Percent == null ? null
            : UI.H(4, device.Charging ? UI.Icon(Glyph.Bolt, 11, Theme.Charging) : null,
                UI.Text(device.LevelText, 13, FontWeights.SemiBold, low ? Theme.Red : Theme.Primary));
        var text = UI.V(1,
            UI.HStretch(8, 0, UI.Text(device.Name, 12.5, FontWeights.Medium, wrap: true), level ?? (UIElement)new Border()),
            UI.Text(detail, 11, color: low ? Theme.Red : Theme.Secondary, wrap: true),
            device.Percent is double percent ? new LevelBar { Percent = percent, Charging = device.Charging, Height = 4, Margin = new Thickness(0, 4, 0, 0) } : null);
        var row = UI.HStretch(10, 1, UI.SymbolTile(Glyph.For(device.Kind), tint, 28).Also(t => t.VerticalAlignment = VerticalAlignment.Top), text);
        row.Margin = new Thickness(0, 5, 0, 5);
        row.ToolTip = $"{device.Name} · {device.Kind.Label()} · {(device.Connected ? "connected" : "not connected")}";
        return row;
    }

    private FrameworkElement AlertsRow()
    {
        var unread = M.UnreadAlerts;
        var text = unread == 0 ? "No new alerts" : unread == 1 ? "1 new alert" : $"{unread} new alerts";
        var row = UI.H(8, UI.Icon(Glyph.Bell, 13, unread > 0 ? Theme.Accent : Theme.Secondary),
            UI.Text(text, 12, unread > 0 ? FontWeights.Medium : FontWeights.Normal, unread > 0 ? Theme.Primary : Theme.Secondary),
            UI.Fill, UI.Icon(Glyph.Chevron, 10, Theme.Tertiary));
        return UI.Clickable(new Border { Child = row, Padding = new Thickness(10, 0, 10, 0), Height = 30 }, () => { HidePanel(); App.Current.ShowMain("alerts"); },
            new CornerRadius(8), unread > 0 ? Theme.WithOpacity(Theme.Accent, 0.12) : Theme.Hover);
    }
}
