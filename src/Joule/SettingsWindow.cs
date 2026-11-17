using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Imaging;
using Joule.Core;

namespace Joule;

public sealed class SettingsWindow : Window
{
    private readonly TabControl _tabs = new() { Margin = new Thickness(12) };
    private Monitor M => Monitor.Shared;
    private WarningSettings W => M.Warnings;

    public SettingsWindow()
    {
        Title = "Joule Settings";
        UiScale.Size(this, 560, 640, 480, 400);
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        Icon = BitmapFrame.Create(new Uri("pack://application:,,,/Assets/Joule.ico"));
        Background = Theme.Window;
        UseLayoutRounding = true;
        _tabs.LayoutTransform = UiScale.Transform();
        Content = _tabs;
        void Rescale() { _tabs.LayoutTransform = UiScale.Transform(); UiScale.Size(this, 560, 640, 480, 400); }
        UiScale.Changed += Rescale;
        Closed += (_, _) => UiScale.Changed -= Rescale;
        Build();
    }

    public void Select(int tab)
    {
        Build();
        _tabs.SelectedIndex = Math.Clamp(tab, 0, _tabs.Items.Count - 1);
    }

    private void Build()
    {
        var selected = Math.Max(0, _tabs.SelectedIndex);
        Background = Theme.Window;
        _tabs.Items.Clear();
        _tabs.Items.Add(new TabItem { Header = "General", Content = Scroll(General()) });
        _tabs.Items.Add(new TabItem { Header = "Alerts", Content = Scroll(Alerts()) });
        _tabs.Items.Add(new TabItem { Header = "How it measures", Content = Scroll(Method()) });
        _tabs.SelectedIndex = selected;
    }

    private static ScrollViewer Scroll(UIElement content) => new() { Content = content, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Padding = new Thickness(8, 12, 8, 12) };

    private static FrameworkElement Section(string? title, params UIElement?[] rows)
    {
        var items = new List<UIElement?>();
        if (title != null) items.Add(UI.Text(title, 13, FontWeights.SemiBold).Margin(2, 0, 0, 0));
        items.Add(UI.Card(UI.V(12, rows), 16).Also(c => c.Effect = null));
        return UI.V(8, items).Margin(0, 0, 0, 18);
    }

    private void Changed() => M.SettingsChanged();

    private FrameworkElement General()
    {
        var trayOptions = new[] { (TrayStyle.Battery, "Battery (all devices)"), (TrayStyle.Watts, "Power (W)"), (TrayStyle.Percent, "PC battery level (%)"), (TrayStyle.Icon, "Joule icon") };
        var combo = new ComboBox { Width = 220, ItemsSource = trayOptions.Select(o => o.Item2).ToList(), SelectedIndex = Array.FindIndex(trayOptions, o => o.Item1 == M.Settings.Tray) };
        combo.SelectionChanged += (_, _) => { M.Settings.Tray = trayOptions[Math.Max(0, combo.SelectedIndex)].Item1; Changed(); };

        var elevatedTask = LoginItem.ElevatedTaskExists();
        var login = UI.Check("Open at login", LoginItem.Enabled || elevatedTask, v =>
        {
            if (!v && elevatedTask) { LoginItem.SetElevatedTask(false); }
            LoginItem.Enabled = v;
            Build();
        });
        FrameworkElement admin;
        if (PowerRequestProbe.Elevated)
        {
            admin = UI.V(6, UI.H(8, UI.Icon(Glyph.Check, 13, Theme.Charging), UI.Text("Running with administrator rights", 12.5)),
                UI.Secondary(elevatedTask ? "Joule starts with these rights at login, so it can see what keeps the PC awake." : "Joule can see what keeps the PC awake right now.", 11.5),
                elevatedTask ? UI.Link("Start at login without administrator rights", () => { if (LoginItem.SetElevatedTask(false)) LoginItem.Enabled = true; Build(); }, 12) : null);
        }
        else
        {
            admin = UI.V(6, UI.Text("Detect what keeps the PC awake", 12.5, FontWeights.SemiBold),
                UI.Secondary("Windows only shows power requests to administrators. Joule can start with administrator rights at login (a scheduled task; Windows asks once).", 11.5),
                UI.Button(elevatedTask ? "Restart with administrator rights" : "Enable …", () => App.Current.EnableElevated()));
        }
        return UI.V(0,
            Section("General",
                login,
                UI.Secondary("Joule only records while it’s running. Started at login, you won’t miss a minute.", 11.5),
                admin,
                UI.H(12, UI.Text("Tray icon shows", 12.5), UI.Fill, combo)),
            Section("Data",
                UI.H(12, UI.Text("Location", 12.5), UI.Fill, UI.Button("Show in Explorer", () => Process.Start(new ProcessStartInfo("explorer.exe", $"\"{Store.DefaultDirectory}\"") { UseShellExecute = true }))),
                UI.H(12, UI.Text("Retention", 12.5), UI.Fill, UI.Secondary("90 days, per-minute data 7 days", 12.5, false)),
                UI.Button("Delete entire history …", () =>
                {
                    if (MessageBox.Show(this, "All measurements and alerts will be removed. This can’t be undone.", "Delete entire history?",
                            MessageBoxButton.OKCancel, MessageBoxImage.Warning, MessageBoxResult.Cancel) == MessageBoxResult.OK)
                        M.EraseAll();
                })));
    }

    private FrameworkElement SliderRow(string label, double value, double min, double max, double step, string unit, Action<double> set)
    {
        var digits = step < 1 ? 1 : 0;
        var text = UI.Text($"{Format.Number(value, digits)} {unit}", 12.5).Right().Width(70);
        var slider = new Slider { Minimum = min, Maximum = max, Value = value, TickFrequency = step, IsSnapToTickEnabled = true, Width = 220, VerticalAlignment = VerticalAlignment.Center };
        slider.ValueChanged += (_, e) => text.Text = $"{Format.Number(e.NewValue, digits)} {unit}";
        slider.PreviewMouseUp += (_, _) => { set(slider.Value); Changed(); };
        slider.LostKeyboardFocus += (_, _) => { set(slider.Value); Changed(); };
        return UI.H(12, UI.Text(label, 12.5), UI.Fill, slider, text);
    }

    private FrameworkElement Alerts()
    {
        var sections = new List<UIElement?>
        {
            Section(null,
                UI.Check("Show notifications", M.Settings.Notifications, v => { M.Settings.Notifications = v; Changed(); }),
                UI.Check("Only alert on battery", W.BatteryOnly, v => { W.BatteryOnly = v; Changed(); })),
            Section("App using a lot of energy",
                UI.Check("On", W.AppHog, v => { W.AppHog = v; Changed(); }),
                SliderRow("From", W.AppWatts, 2, 20, 0.5, "W", v => W.AppWatts = v)),
            Section("App working in the background",
                UI.Check("On", W.BackgroundHog, v => { W.BackgroundHog = v; Changed(); }),
                SliderRow("From", W.BackgroundWatts, 0.5, 8, 0.5, "W", v => W.BackgroundWatts = v)),
            Section("High total drain",
                UI.Check("On", W.HighDrain, v => { W.HighDrain = v; Changed(); }),
                SliderRow("From", W.SystemWatts, 10, 120, 1, "W", v => W.SystemWatts = v)),
            Section("Sleep",
                UI.Check("Alert when something keeps the PC awake", W.SleepBlocker, v => { W.SleepBlocker = v; Changed(); }),
                SliderRow("After", W.BlockerMinutes, 10, 180, 5, "min", v => W.BlockerMinutes = v),
                UI.Check("Alert on battery drain during sleep", W.SleepDrain, v => { W.SleepDrain = v; Changed(); }),
                SliderRow("From", W.SleepPercentPerHour, 0.3, 5, 0.1, "%/h", v => W.SleepPercentPerHour = v)),
            Section("Low battery",
                UI.Check("Alert in addition to Windows", W.LowBattery, v => { W.LowBattery = v; Changed(); }),
                SliderRow("At", W.LowBatteryPercent, 5, 50, 1, "%", v => W.LowBatteryPercent = v)),
            Section("Devices",
                UI.Check("Alert when a mouse, headset, controller … runs low", W.DeviceLow, v => { W.DeviceLow = v; Changed(); }),
                SliderRow("At", W.DevicePercent, 5, 50, 1, "%", v => W.DevicePercent = v),
                UI.Secondary("Once per discharge, even on the adapter. Charging the device re-arms it. The battery tray icon turns red until it’s charged.", 11.5),
                UI.Check("Notify when a device connects or disconnects", M.Settings.DeviceNotifications, v => { M.Settings.DeviceNotifications = v; Changed(); })),
        };
        if (W.Muted.Count > 0)
        {
            var names = M.Store?.AppIdentities() ?? new();
            string Name(string key) => key.StartsWith("device:") ? M.DeviceName(key["device:".Length..])
                : names.TryGetValue(key, out var app) ? app.Name : key.Split(':').Last().Split('\\').Last();
            sections.Add(Section("Muted apps and devices", W.Muted.OrderBy(k => k).Select(key =>
                (UIElement)UI.HStretch(12, 0, UI.Text(Name(key), 12.5, wrap: true),
                    UI.Button("Alert again", () => { M.Unmute(key); Build(); _tabs.SelectedIndex = 1; }))).ToArray()));
        }
        return UI.V(0, sections);
    }

    private FrameworkElement Method()
    {
        FrameworkElement Item(string title, string glyph, string text) => UI.HStretch(12, 1,
            UI.Icon(glyph, 15, Theme.Accent).Also(i => { i.VerticalAlignment = VerticalAlignment.Top; i.Width = 20; }).Margin(0, 2, 0, 0),
            UI.V(3, UI.Text(title, 13, FontWeights.SemiBold), UI.Secondary(text, 12)));
        var meter = EnergyProbe.Available;
        return UI.V(16,
            Item("Whole PC", Glyph.Bolt, "On battery, the power comes straight from the battery’s discharge rate: everything the PC needs right now – display, processor, graphics, memory, radios. Joule reads it every 10 seconds. On the adapter the battery can’t see the load, so Joule shows an estimate (≈): the measured processor power plus the baseline it learned on battery."),
            Item("Per app", Glyph.Grid, meter
                ? "The processor’s energy meter (RAPL) measures what the CPU cores use. Windows counts the CPU cycles of every process; Joule shares the cores’ energy out by those cycles every minute (every 3 seconds while a window is open). Helper processes and embedded browsers (WebView2) count for the app that started them, and everything in one install folder is one app."
                : "This PC exposes no energy meter, so Joule estimates each process’s energy from its CPU time. Such values are marked “≈” and “estimated”."),
            Item("Services", Glyph.Settings, "Windows services, background processes and the system itself are listed as services. Command-line tools (node, python …) stand on their own."),
            Item("Display & hardware", Glyph.Display, "What the whole PC draws, minus all processes. That’s the display, graphics chip, memory, Wi-Fi and the rest of the chip."),
            Item("Keeping awake", Glyph.Sun, "Apps and drivers that prevent sleep come from Windows’ power requests (powercfg /requests), which Windows only reveals to administrators."),
            Item("Devices", Glyph.Bluetooth, "Bluetooth devices report their battery level to Windows, which keeps the last value. PlayStation controllers are read from their own input reports (Joule never switches their mode), Xbox controllers through XInput in four steps, Logitech devices on a USB receiver through Logitech’s HID++ protocol. A UPS appears as a second battery. Levels are stored when they change, for the charts and the runtime estimate."),
            Item("Battery percent", Glyph.Battery, "“3 % battery” means: this energy equals 3 % of a full charge of your battery in its current condition."),
            Item("Private", Glyph.Lock, "Everything stays on this PC. No network connection. History lives in %LOCALAPPDATA%\\Joule.")).Margin(8, 4, 8, 4);
    }
}
