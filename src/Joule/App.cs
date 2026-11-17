using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Joule.Core;
using Microsoft.Win32;

namespace Joule;

public static class Program
{
    private const string MutexName = @"Local\Joule-Windows-SingleInstance";
    private static Mutex? _mutex;

    /// <summary>Lets another copy (the elevated one) take over before this one has exited.</summary>
    public static void ReleaseSingleInstance()
    {
        try { _mutex?.ReleaseMutex(); } catch { }
        _mutex?.Dispose();
        _mutex = null;
    }

    /// <summary>Takes the single-instance lock back (a hand-over that didn't happen). False if another copy has it.</summary>
    public static bool TakeSingleInstance()
    {
        if (_mutex != null) return true;
        try
        {
            var mutex = new Mutex(true, MutexName, out var created);
            if (created) { _mutex = mutex; return true; }
            mutex.Dispose();
        }
        catch { }
        return false;
    }

    /// <summary>Another copy holds the lock (also when it runs elevated and won't let this one open it).</summary>
    public static bool OtherInstanceRunning()
    {
        try
        {
            if (Mutex.TryOpenExisting(MutexName, out var other)) { other.Dispose(); return true; }
            return false;
        }
        catch (UnauthorizedAccessException) { return true; }
        catch { return false; }
    }
    private const string ShowEventName = @"Local\Joule-Windows-Show";

    [STAThread]
    public static int Main(string[] args)
    {
        var demo = args.Contains("--demo");
        var snapshot = args.Contains("--snapshot");
        // The tray menu is WinForms: let it follow each monitor's DPI like the WPF windows (manifest: PerMonitorV2).
        System.Windows.Forms.Application.SetHighDpiMode(System.Windows.Forms.HighDpiMode.PerMonitorV2);
        Mutex? mutex = null;
        EventWaitHandle? showEvent = null;
        if (!demo && !snapshot)
        {
            bool created;
            try { mutex = new Mutex(true, MutexName, out created); }
            catch (UnauthorizedAccessException) { created = false; }
            if (!created)
            {
                // Already running: bring its window up instead (unless this is the login start).
                if (!args.Contains("--background") && !args.Contains("--takeover"))
                {
                    try { EventWaitHandle.OpenExisting(ShowEventName).Set(); } catch { }
                }
                if (!args.Contains("--takeover")) return 0;
                // An elevated copy taking over: wait for the old one to quit.
                for (var i = 0; i < 50 && !created; i++)
                {
                    Thread.Sleep(200);
                    try { mutex?.Dispose(); mutex = new Mutex(true, MutexName, out created); } catch { created = false; }
                }
            }
            try { showEvent = new EventWaitHandle(false, EventResetMode.AutoReset, ShowEventName); } catch { }
        }
        _mutex = mutex;
        var app = new App(args, showEvent);
        return app.Run();
    }
}

public sealed class App : Application
{
    public static new App Current => (App)Application.Current;

    private readonly string[] _args;
    private readonly EventWaitHandle? _showEvent;
    private Monitor _monitor = null!;
    private Tray? _tray;
    private TrayPanel _panel = null!;
    private MainWindow _main = null!;
    private SettingsWindow? _settings;
    private bool _quitting;

    public App(string[] args, EventWaitHandle? showEvent)
    {
        _args = args;
        _showEvent = showEvent;
        ShutdownMode = ShutdownMode.OnExplicitShutdown;
#pragma warning disable WPF0001
        ThemeMode = ThemeMode.System;
#pragma warning restore WPF0001
        DispatcherUnhandledException += (_, e) =>
        {
            Log(e.Exception);
            e.Handled = true;
        };
    }

    public static void Log(Exception e)
    {
        try
        {
            Directory.CreateDirectory(Store.DefaultDirectory);
            File.AppendAllText(Path.Combine(Store.DefaultDirectory, "errors.log"), $"{DateTime.Now:O} {e}\n\n");
        }
        catch { }
    }

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        var demo = _args.Contains("--demo");
        var settings = AppSettings.Load();
        // Joule follows Windows' scaling exactly. --scale 1.5 is only for checking layouts in snapshots.
        var scaleIndex = Array.IndexOf(_args, "--scale");
        if (scaleIndex >= 0 && scaleIndex + 1 < _args.Length && double.TryParse(_args[scaleIndex + 1], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var s))
            UiScale.Apply(s);
        _monitor = new Monitor(settings, demo);
        _main = new MainWindow();
        MainWindow = _main;
        _panel = new TrayPanel();
        _monitor.IsLooking = () => _main.IsShowing || _panel.IsVisible;
        if (!_args.Contains("--snapshot"))
        {
            _tray = new Tray { Clicked = () => _panel.Toggle(), BalloonClicked = () => ShowMain("alerts") };
            _monitor.TrayChanged += () => _tray.Update();
            _monitor.Notify = (title, body) => _tray.Notify(title, body);
        }
        _monitor.Start();
        if (!demo && !_args.Contains("--snapshot")) RepairElevatedTask();
        SystemEvents.UserPreferenceChanged += (_, args) =>
        {
            if (args.Category is UserPreferenceCategory.General or UserPreferenceCategory.Color or UserPreferenceCategory.VisualStyle)
                Dispatcher.BeginInvoke(ThemeChanged);
        };
        // Resolution or scaling changed (another monitor, 4K plugged in): the tray icon is redrawn at the new size.
        SystemEvents.DisplaySettingsChanged += (_, _) => Dispatcher.BeginInvoke(() => _tray?.Update());
        if (_showEvent != null)
        {
            var thread = new Thread(() =>
            {
                while (true)
                {
                    try { _showEvent.WaitOne(); } catch { return; }
                    // A copy that is quitting must not reopen its window (its history is already closed).
                    Dispatcher.BeginInvoke(() => { if (!_quitting) ShowMain(null); });
                }
            }) { IsBackground = true };
            thread.Start();
        }
        var snapshotIndex = Array.IndexOf(_args, "--snapshot");
        if (snapshotIndex >= 0)
        {
            var folder = snapshotIndex + 1 < _args.Length ? _args[snapshotIndex + 1] : "snapshots";
            _ = Snapshots(folder);
            return;
        }
        if (settings.ReopenAfterRestart is { } reopen)
        {
            // Restarted with administrator rights from the app: come back where the user was.
            settings.ReopenAfterRestart = null;
            settings.Save();
            ShowMain(settings.Page);
            if (reopen == "settings") ShowSettings(0);
        }
        else if (!_args.Contains("--background")) ShowMain(settings.Page);
    }

    private void ThemeChanged()
    {
        var wasDark = Theme.Dark;
        Theme.Refresh();
        _tray?.Update();
        if (wasDark == Theme.Dark) return;
        _main.Rebuild();
        if (_panel.IsVisible) _panel.Render();
        _settings?.Select(0);
    }

    public void ShowMain(string? page)
    {
        _panel.HidePanel();
        _main.ShowAndActivate(page);
        _monitor.UpdateLive();
    }

    public void ShowSettings(int tab)
    {
        _panel.HidePanel();
        if (_settings == null)
        {
            _settings = new SettingsWindow();
            _settings.Closed += (_, _) => _settings = null;
        }
        _settings.Select(tab);
        _settings.Show();
        _settings.Activate();
    }

    private bool _elevating;

    /// <summary>
    /// Sets up (or repairs) the elevated logon task – Windows asks for consent once – and hands over to an elevated copy
    /// right away. This copy only quits once the new one is running; otherwise it stays and says why.
    /// </summary>
    public async void EnableElevated()
    {
        if (_monitor.Demo || _elevating) return;
        _elevating = true;
        try
        {
            if (!(LoginItem.ElevatedTaskExists() && LoginItem.ElevatedTaskIsCurrent()) && !LoginItem.SetElevatedTask(true))
            {
                MessageBox.Show("Joule couldn’t set up the task (the Windows prompt was declined or failed). Without administrator rights it keeps working, but can’t see what keeps the PC awake.",
                    "Joule", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
            // The task now starts Joule at login; the Run entry would start a second copy.
            LoginItem.Enabled = false;
            // The elevated copy reopens the window (and settings) the user was in.
            _monitor.Settings.ReopenAfterRestart = _settings?.IsVisible == true ? "settings" : "main";
            _monitor.Settings.Save();
            Program.ReleaseSingleInstance();
            var started = LoginItem.RunElevatedTask();
            for (var i = 0; started && i < 40 && !Program.OtherInstanceRunning(); i++) await Task.Delay(200);
            if (started && Program.OtherInstanceRunning())
            {
                Quit();
                return;
            }
            _monitor.Settings.ReopenAfterRestart = null;
            _monitor.Settings.Save();
            Program.TakeSingleInstance();
            MessageBox.Show("The task is set up, but Windows didn’t start it just now. Joule keeps running without administrator rights and will start with them at your next login.",
                "Joule", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        finally { _elevating = false; }
    }

    /// <summary>A copy running with administrator rights silently brings an outdated logon task up to date (no prompt needed).</summary>
    private static void RepairElevatedTask()
    {
        if (!PowerRequestProbe.Elevated) return;
        Task.Run(() =>
        {
            if (LoginItem.ElevatedTaskExists() && !LoginItem.ElevatedTaskIsCurrent()) LoginItem.SetElevatedTask(true);
        });
    }

    public void Quit()
    {
        if (_quitting) return;
        _quitting = true;
        // Let a new copy start at once, even while this one is still closing down.
        Program.ReleaseSingleInstance();
        try { _showEvent?.Dispose(); } catch { }
        _main.AllowClose = true;
        _tray?.Dispose();
        try { _monitor.Store?.Dispose(); } catch { }
        Shutdown();
    }

    // MARK: Screenshots of every page with sample data, for checking the layout.

    private async Task Snapshots(string folder)
    {
        Directory.CreateDirectory(folder);
        var ri = Array.IndexOf(_args, "--render-scale");
        if (ri >= 0 && ri + 1 < _args.Length && double.TryParse(_args[ri + 1], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var r)) RenderScale = r;
        TrayPreview(folder);
        _main.Show();
        _main.Width = 1220 * UiScale.Current; _main.Height = 900 * UiScale.Current;
        await Task.Delay(1500);
        foreach (var page in Joule.MainWindow.PageIds)
        {
            _main.Show(page);
            await Task.Delay(page == "now" ? 4500 : 1200);
            Save(_main, Path.Combine(folder, $"{page}.png"));
        }
        // The narrowest the window can get: nothing may overlap or be cut off here either.
        _main.Width = _main.MinWidth; _main.Height = 900 * UiScale.Current;
        foreach (var page in Joule.MainWindow.PageIds)
        {
            _main.Show(page);
            await Task.Delay(page == "now" ? 1500 : 900);
            Save(_main, Path.Combine(folder, $"{page}-narrow.png"));
        }
        _main.Width = 1220 * UiScale.Current;
        _main.Show("apps");
        await Task.Delay(500);
        // Select the first app to show the detail panel.
        var store = _monitor.Store!;
        _panel.Render();
        _panel.Show();
        _panel.UpdateLayout();
        await Task.Delay(1500);
        _panel.Render();
        await Task.Delay(300);
        Save(_panel, Path.Combine(folder, "panel.png"));
        _panel.Hide();
        ShowSettings(0);
        await Task.Delay(800);
        Save(_settings!, Path.Combine(folder, "settings-general.png"));
        _settings!.Select(1);
        await Task.Delay(500);
        Save(_settings!, Path.Combine(folder, "settings-alerts.png"));
        _settings!.Select(2);
        await Task.Delay(500);
        Save(_settings!, Path.Combine(folder, "settings-method.png"));
        var session = store.Sessions(DateTime.Now.AddDays(-14)).Skip(1).FirstOrDefault();
        _ = Dispatcher.BeginInvoke(DispatcherPriority.Background, async () =>
        {
            await Task.Delay(1500);
            foreach (Window w in Windows)
            {
                if (w.Title == "Battery session") { Save(w, Path.Combine(folder, "session.png")); w.Close(); }
            }
        });
        if (session != null) SessionWindow.Show(session);
        Quit();
    }

    /// <summary>The tray icon at every common size, both taskbar colours, enlarged 8× pixel by pixel, as it ends up in the HICON.</summary>
    private static void TrayPreview(string folder)
    {
        var sizes = new[] { 16, 20, 24, 32 };
        var cases = new[] { ("7", false), ("23", false), ("100", false), ("23", true), ("", false), ("", true) };
        const int zoom = 8, pad = 8;
        var cell = 32 * zoom + pad;
        using var sheet = new System.Drawing.Bitmap(cases.Length * cell + pad, sizes.Length * 2 * cell + pad);
        using var g = System.Drawing.Graphics.FromImage(sheet);
        g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.NearestNeighbor;
        g.PixelOffsetMode = System.Drawing.Drawing2D.PixelOffsetMode.Half;
        g.Clear(System.Drawing.Color.Magenta);
        for (var dark = 0; dark < 2; dark++)
            for (var s = 0; s < sizes.Length; s++)
            {
                var y = pad + (dark * sizes.Length + s) * cell;
                using var back = new System.Drawing.SolidBrush(dark == 1 ? System.Drawing.Color.FromArgb(32, 32, 32) : System.Drawing.Color.FromArgb(238, 238, 238));
                g.FillRectangle(back, 0, y - pad / 2, sheet.Width, cell);
                for (var c = 0; c < cases.Length; c++)
                {
                    using var rendered = Tray.Render(cases[c].Item1, cases[c].Item2, sizes[s], dark == 1);
                    var handle = rendered.GetHicon();
                    using (var icon = System.Drawing.Icon.FromHandle(handle))
                    using (var roundTrip = icon.ToBitmap())
                        g.DrawImage(roundTrip, new System.Drawing.Rectangle(pad + c * cell, y, sizes[s] * zoom, sizes[s] * zoom));
                    DestroyIcon(handle);
                }
            }
        sheet.Save(Path.Combine(folder, "tray-icons.png"), System.Drawing.Imaging.ImageFormat.Png);

        // The battery style: level 80 / 30 / 10, charging, low (until charged), low with unread alerts.
        var battery = new (double? level, bool low, bool charging, bool badge)[] { (80, false, false, false), (30, false, false, false), (10, false, false, false), (55, false, true, false), (70, true, false, false), (70, true, false, true) };
        using var sheet2 = new System.Drawing.Bitmap(battery.Length * cell + pad, sizes.Length * 2 * cell + pad);
        using var g2 = System.Drawing.Graphics.FromImage(sheet2);
        g2.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.NearestNeighbor;
        g2.PixelOffsetMode = System.Drawing.Drawing2D.PixelOffsetMode.Half;
        g2.Clear(System.Drawing.Color.Magenta);
        for (var dark = 0; dark < 2; dark++)
            for (var s = 0; s < sizes.Length; s++)
            {
                var y = pad + (dark * sizes.Length + s) * cell;
                using var back = new System.Drawing.SolidBrush(dark == 1 ? System.Drawing.Color.FromArgb(32, 32, 32) : System.Drawing.Color.FromArgb(238, 238, 238));
                g2.FillRectangle(back, 0, y - pad / 2, sheet2.Width, cell);
                for (var c = 0; c < battery.Length; c++)
                {
                    var b = battery[c];
                    using var rendered = Tray.RenderBattery(b.level, b.low, b.charging, b.badge, sizes[s], dark == 1);
                    var handle = rendered.GetHicon();
                    using (var icon = System.Drawing.Icon.FromHandle(handle))
                    using (var roundTrip = icon.ToBitmap())
                        g2.DrawImage(roundTrip, new System.Drawing.Rectangle(pad + c * cell, y, sizes[s] * zoom, sizes[s] * zoom));
                    DestroyIcon(handle);
                }
            }
        sheet2.Save(Path.Combine(folder, "tray-battery.png"), System.Drawing.Imaging.ImageFormat.Png);
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern bool DestroyIcon(IntPtr handle);

    private static double RenderScale = 1;

    private static void Save(Window window, string path)
    {
        if (window.Content is not FrameworkElement content) return;
        content.UpdateLayout();
        var dpi = VisualTreeHelper.GetDpi(window);
        // --render-scale 2 renders the PNG as a 200 % screen would show it (a 4K check on any monitor).
        var extra = RenderScale;
        dpi = new DpiScale(dpi.DpiScaleX * extra, dpi.DpiScaleY * extra);
        // The interface size is a LayoutTransform on the content: draw it scaled, as it appears on screen.
        var scale = content.LayoutTransform is ScaleTransform st ? st.ScaleX : 1;
        var w = content.ActualWidth * scale; var h = content.ActualHeight * scale;
        var width = (int)Math.Ceiling(w * dpi.DpiScaleX);
        var height = (int)Math.Ceiling(h * dpi.DpiScaleY);
        if (width <= 0 || height <= 0) return;
        var bitmap = new RenderTargetBitmap(width, height, 96 * dpi.DpiScaleX, 96 * dpi.DpiScaleY, PixelFormats.Pbgra32);
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            dc.DrawRectangle(window.Background is SolidColorBrush b && b.Color.A > 0 ? b : Theme.Window, null, new Rect(0, 0, w, h));
            dc.DrawRectangle(new VisualBrush(content) { Stretch = Stretch.Fill }, null, new Rect(0, 0, w, h));
        }
        bitmap.Render(visual);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = File.Create(path);
        encoder.Save(stream);
    }
}
