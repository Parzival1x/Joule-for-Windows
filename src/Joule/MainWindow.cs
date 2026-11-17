using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Imaging;

namespace Joule;

public sealed class MainWindow : Window
{
    public static readonly string[] PageIds = { "now", "history", "apps", "battery", "devices", "alerts" };
    private static readonly string[] PageTitles = { "Now", "History", "Apps", "Battery", "Devices", "Alerts" };

    private readonly Dictionary<string, Page> _pages = new();
    private readonly ContentControl _nav = new() { Focusable = false, HorizontalAlignment = HorizontalAlignment.Center };
    private readonly ContentControl _host = new() { Focusable = false };
    private readonly DockPanel _root = new();
    private readonly Border _scaled = new();
    private string _current = "now";
    public bool AllowClose;
    private Monitor M => Monitor.Shared;

    public string CurrentPage => _current;

    public MainWindow()
    {
        Title = "Joule";
        UiScale.Size(this, 1220, 820, 960, 640);
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        Icon = BitmapFrame.Create(new Uri("pack://application:,,,/Assets/Joule.ico"));
        UseLayoutRounding = true;
        Build();
        _scaled.Child = _root;
        _scaled.LayoutTransform = UiScale.Transform();
        Content = _scaled;
        UiScale.Changed += () =>
        {
            _scaled.LayoutTransform = UiScale.Transform();
            var wasNormal = WindowState == WindowState.Normal;
            UiScale.Size(this, 1220, 820, 960, 640);
            if (wasNormal && IsVisible)
            {
                var area = SystemParameters.WorkArea;
                Left = area.Left + (area.Width - Width) / 2;
                Top = area.Top + (area.Height - Height) / 2;
            }
        };
        Closing += (_, e) =>
        {
            if (AllowClose) return;
            e.Cancel = true;
            Hide();
        };
        IsVisibleChanged += (_, _) => { if (!IsVisible) _pages[_current].Hidden(); M.UpdateLive(); };
        StateChanged += (_, _) => M.UpdateLive();
        Activated += (_, _) => M.UpdateLive();
        M.Changed += () => { if (IsVisible) { _pages[_current].Reload(); RenderNav(); } };
        M.LiveChanged += () => { if (IsVisible && WindowState != WindowState.Minimized) _pages[_current].LiveUpdate(); RenderNav(); };
    }

    public bool IsShowing => IsVisible && WindowState != WindowState.Minimized;

    private void Build()
    {
        _pages.Clear();
        var now = new NowPage { Navigate = Show };
        var history = new HistoryPage { Navigate = Show };
        var alerts = new AlertsPage { OpenRules = () => App.Current.ShowSettings(1) };
        _pages["now"] = now;
        _pages["history"] = history;
        _pages["apps"] = new AppsPage();
        _pages["battery"] = new BatteryPage();
        _pages["devices"] = new DevicesPage();
        _pages["alerts"] = alerts;

        _root.Children.Clear();
        Background = Theme.Window;
        var gear = UI.Clickable(UI.Icon(Glyph.Settings, 15, Theme.Secondary).Margin(8, 6, 8, 6), () => App.Current.ShowSettings(0), new System.Windows.CornerRadius(6)).Tip("Settings");
        var bar = new Grid { Height = 52, Background = Theme.Window };
        var center = UI.H(10, _nav, gear);
        center.HorizontalAlignment = HorizontalAlignment.Center;
        bar.Children.Add(center);
        var line = new Border { Height = 1, Background = Theme.CardEdge };
        var top = new DockPanel();
        DockPanel.SetDock(bar, Dock.Top);
        top.Children.Add(bar);
        top.Children.Add(line);
        DockPanel.SetDock(top, Dock.Top);
        _root.Children.Add(top);
        _root.Children.Add(_host);
        RenderNav();
    }

    /// <summary>Colours changed (light/dark): rebuild everything.</summary>
    public void Rebuild()
    {
        Build();
        Show(_current);
    }

    private void RenderNav()
    {
        var index = Array.IndexOf(PageIds, _current);
        _nav.Content = UI.Segmented(PageTitles, index, i => Show(PageIds[i]), 13, Array.IndexOf(PageIds, "alerts"), M.UnreadAlerts);
    }

    public void Show(string page)
    {
        if (!_pages.ContainsKey(page)) page = "now";
        if (_current != page && _pages.TryGetValue(_current, out var old)) old.Hidden();
        _current = page;
        M.Settings.Page = page;
        _host.Content = _pages[page].Root;
        _pages[page].Reload();
        RenderNav();
    }

    public void ShowAndActivate(string? page = null)
    {
        Show(page ?? _current);
        if (!IsVisible) Show();
        if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
        Activate();
        Topmost = true; Topmost = false;
        Focus();
    }
}
