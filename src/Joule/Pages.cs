using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Joule.Core;

namespace Joule;

public abstract class Page
{
    protected Monitor M => Monitor.Shared;
    public abstract FrameworkElement Root { get; }
    /// <summary>Stored data changed or the page became visible.</summary>
    public virtual void Reload() { }
    /// <summary>Live values changed.</summary>
    public virtual void LiveUpdate() { }
    public virtual void Hidden() { }

    /// <summary>Scrolls, centred, comfortable reading width.</summary>
    protected static ScrollViewer PageScroll(StackPanel content, double maxWidth = 1080)
    {
        content.Margin = new Thickness(36, 28, 36, 40);
        content.MaxWidth = maxWidth;
        return new ScrollViewer { Content = content, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, Focusable = false };
    }

    protected static StackPanel Stack() => new() { Orientation = Orientation.Vertical };

    protected static void Set(ContentControl host, UIElement? content) => host.Content = content;

    protected static ContentControl Host(double topMargin = 22) => new() { Margin = new Thickness(0, topMargin, 0, 0), Focusable = false, IsTabStop = false };
}

// MARK: Now

public sealed class NowPage : Page
{
    private readonly ContentControl _error = Host(0), _header = Host(0), _session = Host(), _live = Host();
    private readonly ScrollViewer _root;
    public override FrameworkElement Root => _root;
    public Action<string>? Navigate;

    public NowPage()
    {
        var stack = Stack();
        foreach (var host in new[] { _error, _header, _session, _live }) stack.Children.Add(host);
        _root = PageScroll(stack);
    }

    public override void Reload()
    {
        var session = M.CurrentSession;
        if (session != null)
        {
            Set(_session, UI.Card(Views.SessionBreakdown(session, M.CurrentTimeline, M.Power?.FullWh ?? 0,
                M.Blockers.Where(b => b.App.Category == AppCategory.App).Select(b => b.App.Name).ToList(), () => Navigate?.Invoke("battery")), 24));
        }
        else
        {
            Set(_session, UI.Card(UI.V(6, UI.Title(M.Power?.Source == PowerSource.Battery ? "On battery" : "On power adapter", 22),
                UI.EmptyNote(M.Power?.Source == PowerSource.Battery ? "The breakdown appears after the first percent." : "As soon as you unplug, Joule shows here where every percent goes.")), 24));
        }
        LiveUpdate();
    }

    public override void LiveUpdate()
    {
        Set(_error, M.ErrorMessage == null ? null : UI.ErrorBanner(M.ErrorMessage).Margin(0, 0, 0, 16));
        Set(_header, Header());
        Set(_live, UI.Columns(22, LiveAppsCard(), BlockersCard()));
    }

    private FrameworkElement Header()
    {
        if (M.Power is not { } power) return UI.EmptyNote(M.NoBattery ? "This PC reports no battery." : "Reading battery …");
        var glyph = new BatteryGlyph { Percent = power.Percent, Charging = power.Source == PowerSource.Charging, Plugged = power.Source == PowerSource.Adapter, Width = 66, Height = 32 };
        var percent = UI.H(10, UI.Title(Format.Percent(power.Percent), 34), M.Demo ? UI.Pill("Sample data") : null);
        var state = UI.Secondary($"{power.StateText} · Health {Format.Percent(power.HealthPercent)}", 13, false);
        var wattsCard = new Border
        {
            Width = 340, Padding = new Thickness(16, 12, 16, 12), CornerRadius = new CornerRadius(12), Background = Theme.Card,
            BorderBrush = Theme.CardEdge, BorderThickness = new Thickness(1),
            Child = UI.V(8,
                UI.H(8, UI.Text((power.Estimated ? "≈ " : "") + Format.Watts(power.SystemWatts), 24, FontWeights.SemiBold, Theme.Accent), UI.Fill,
                    UI.Secondary(power.Estimated ? "estimated, whole PC" : "now, whole PC", 11, false)).Tip(power.Estimated
                        ? "On the adapter the battery can’t see the load. Joule adds the baseline it learned on battery to the measured processor power."
                        : "Straight from the battery’s discharge rate."),
                Views.PowerSplit(power.SystemWatts, M.LiveSplit, true)),
        };
        return UI.H(20, glyph, UI.V(2, percent, state), UI.Fill, wattsCard);
    }

    private FrameworkElement LiveAppsCard()
    {
        var rows = new List<UIElement?>
        {
            UI.H(6, UI.Text("Active now", 15, FontWeights.SemiBold), UI.Fill, UI.Icon("", 8, Theme.Charging), UI.Secondary("live, every 3 s", 11, false)),
        };
        if (M.LiveApps.Count == 0) rows.Add(UI.EmptyNote("Measuring …").Also(t => t.MinHeight = 120));
        else
        {
            var maximum = Math.Max(0.5, M.LiveApps.Max(a => a.Watts));
            rows.Add(UI.V(2, M.LiveApps.Take(6).Select(a => (UIElement)Views.LiveAppRow(a, maximum, false))));
        }
        rows.Add(UI.Secondary(M.HasEnergyMeter || M.Demo ? "Processor energy. Display and graphics are part of “Display & hardware”." : "Estimated from CPU time – this PC has no readable energy meter.", 11));
        return UI.Card(UI.V(12, rows));
    }

    private FrameworkElement BlockersCard()
    {
        var items = new List<UIElement?> { UI.Text("Keeping awake", 15, FontWeights.SemiBold) };
        if (M.BlockersUnavailable)
        {
            items.Add(UI.H(12, UI.SymbolTile(Glyph.Shield, Theme.Secondary, 34),
                UI.V(1, UI.Text("Needs administrator rights", 14, FontWeights.SemiBold), UI.Secondary("Windows keeps this list for administrators.", 12, false))));
            items.Add(UI.Secondary("Windows only tells administrators which apps keep the PC from sleeping. Let Joule start with administrator rights at login to see them here and get alerts.", 13));
            items.Add(UI.Button("Enable …", () => App.Current.EnableElevated(), primary: true));
            return UI.Card(UI.V(14, items));
        }
        var blocker = M.Blockers.FirstOrDefault(b => b.App.Category == AppCategory.App) ?? M.Blockers.FirstOrDefault();
        if (blocker == null)
        {
            items.Add(UI.H(12, UI.SymbolTile(Glyph.Moon, Theme.Indigo, 34),
                UI.V(1, UI.Text("Nothing is keeping your PC awake", 14, FontWeights.SemiBold), UI.Secondary("It can sleep as soon as you stop using it.", 12, false))));
            return UI.Card(UI.V(14, items));
        }
        var detail = string.Join(" · ", new[]
        {
            blocker.Since is DateTime since ? $"for {Format.Duration((DateTime.Now - since).TotalSeconds)}" : null,
            string.IsNullOrEmpty(blocker.Reason) ? null : $"“{blocker.Reason}”",
        }.Where(s => s != null));
        items.Add(UI.H(12, UI.SymbolTile(Glyph.Sun, Theme.Brown, 34),
            UI.V(1, UI.Text($"{blocker.App.Name} is preventing sleep", 14, FontWeights.SemiBold), UI.Secondary(detail, 12, false))));
        items.Add(UI.Secondary($"As long as {blocker.App.Name} does that, your PC won’t go to sleep on its own – not even with the screen locked.", 13));
        var buttons = new StackPanel { Orientation = Orientation.Horizontal };
        if (blocker.App.Category == AppCategory.App && M.IsRunning(blocker.App)) buttons.Children.Add(UI.Button($"Quit {blocker.App.Name}", () => M.Quit(blocker.App), true).Margin(0, 0, 8, 0));
        if (!M.Warnings.Muted.Contains(blocker.App.Key)) buttons.Children.Add(UI.Button("Don’t report again", () => M.Mute(blocker.App.Key)));
        items.Add(buttons);
        if (M.Blockers.Count > 1) items.Add(UI.Secondary($"Also: {Format.List(M.Blockers.Where(b => b != blocker).Select(b => b.App.Name).ToList())}", 11.5));
        return UI.Card(UI.V(14, items));
    }
}

// MARK: History

public sealed class HistoryPage : Page
{
    private readonly ContentControl _header = Host(0), _body = Host();
    private readonly ScrollViewer _root;
    public override FrameworkElement Root => _root;
    public Action<string>? Navigate;

    public HistoryPage()
    {
        var stack = Stack();
        stack.Children.Add(_header);
        stack.Children.Add(_body);
        _root = PageScroll(stack);
    }

    private Period Period => M.Settings.HistoryPeriod;

    public override void Reload()
    {
        var periods = PeriodExtensions.All;
        Set(_header, UI.PageHeader("History", M.Demo ? "Sample data" : null,
            UI.Segmented(periods.Select(p => p.Title()).ToList(), Array.IndexOf(periods, Period), i => { M.Settings.HistoryPeriod = periods[i]; M.Settings.Save(); Reload(); }),
            UI.Check("Battery only", M.Settings.BatteryOnly, v => { M.Settings.BatteryOnly = v; M.Settings.Save(); Reload(); })));
        if (M.Store == null) { Set(_body, null); return; }
        var range = Period.Range();
        Report report;
        List<DayStrip> days;
        try
        {
            report = M.Store.Report(range, Period.Bucket(), M.Settings.BatteryOnly);
            days = M.Store.DayStrips(range);
        }
        catch (Exception e) { Set(_body, UI.ErrorBanner(e.Message)); return; }
        if (report.IsEmpty)
        {
            Set(_body, UI.Card(UI.EmptyNote(M.Settings.BatteryOnly ? "The PC didn’t run on battery in this period." : "Nothing recorded for this period yet.")));
            return;
        }
        var charges = report.BatteryPercent(report.TotalJoules);
        var tiles = UI.Columns(14,
            UI.StatTile("Energy", Format.Energy(report.TotalJoules), charges < 100 ? $"{Format.Percent(charges)} of a battery charge" : $"≈ {Format.Number(charges / 100)} full battery charges"),
            UI.StatTile("Battery used", Format.Percent(report.PercentUsed), "on battery, excluding sleep"),
            UI.StatTile("Avg. power", Format.Watts(report.AverageWatts), "while the PC was awake"),
            UI.StatTile("Recorded", Format.Duration(report.RecordedSeconds, true), $"{Format.Duration(report.BatterySeconds, true)} of it on battery"));
        var items = new List<UIElement?> { tiles };
        if (Period is Period.Week or Period.Month) items.Add(DayStrips(days, Period == Period.Week ? "The week" : "The last 30 days"));
        else
        {
            items.Add(BatteryCard(report));
            items.Add(PowerCard(report));
        }
        items.Add(UI.Columns(22, EnergySplit(report), TopApps(report)));
        Set(_body, UI.V(22, items));
    }

    private FrameworkElement DayStrips(List<DayStrip> days, string title)
    {
        var rows = new List<UIElement?>
        {
            UI.H(12, UI.Text(title, 17, FontWeights.SemiBold), UI.Fill,
                UI.Legend(("Battery", Theme.Accent), ("Charging", Theme.Charging), ("Adapter", Theme.ChargingSoft), ("Sleep", Theme.IndigoSoft))),
        };
        var list = new List<UIElement>();
        foreach (var day in days)
        {
            var today = day.Day.Date == DateTime.Today;
            list.Add(UI.HStretch(14, 1,
                UI.Text(Format.Day(day.Day), 13, today ? FontWeights.SemiBold : FontWeights.Normal).Width(70),
                new DayStripBar { Day = day, Height = 22 },
                UI.Text(day.PercentUsed >= 0.5 ? $"−{Format.Number(day.PercentUsed, 0)} %" : "–", 13, color: Theme.Secondary).Right().Width(56)));
        }
        rows.Add(UI.V(9, list));
        rows.Add(UI.HStretch(14, 1, new Border { Width = 70 }, new SpreadLabels { Items = new() { "0:00", "6", "12", "18", "24" } }, UI.Secondary("Battery", 11, false).Right().Width(56)));
        return UI.Card(UI.V(14, rows), 22);
    }

    private FrameworkElement BatteryCard(Report report) => UI.Card(UI.V(12,
        UI.H(12, UI.SectionTitle("Battery level", "Shaded: sleep"), UI.Fill,
            UI.Legend(("Battery", Theme.Accent), ("Adapter", Theme.Charging), ("Sleep", Theme.WithOpacity(Theme.Secondary, 0.3)))),
        new BatteryChart { Report = report, Period = Period, Height = 190 }));

    private FrameworkElement PowerCard(Report report)
    {
        var chart = new PowerChart { Report = report, Period = Period, Height = 200 };
        var trailing = new ContentControl { Focusable = false };
        FrameworkElement LegendOrReadout()
        {
            if (chart.Hovered is not { } b)
                return UI.Legend(("Apps", Theme.Apps), ("Windows services", Theme.Services), ("Display & hardware", Theme.Hardware));
            static FrameworkElement Readout(string label, double value, Brush color) =>
                UI.H(4, new Border { Width = 6, Height = 6, CornerRadius = new CornerRadius(3), Background = color }, UI.Text($"{label} {Format.Watts(value)}", 11.5));
            return UI.H(14, UI.Secondary(b.Start.ToString("ddd t"), 11.5, false), Readout("Apps", b.AppsWatts, Theme.Apps), Readout("Services", b.ServicesWatts, Theme.Services),
                Readout("Hardware", b.HardwareWatts, Theme.Hardware), UI.Text(Format.Watts(b.Watts), 13, FontWeights.SemiBold));
        }
        trailing.Content = LegendOrReadout();
        chart.HoverChanged += () => trailing.Content = LegendOrReadout();
        return UI.Card(UI.V(12, UI.H(12, UI.SectionTitle("Power", $"Average per {Period.BucketName()}, split by source"), UI.Fill, trailing), chart));
    }

    private static FrameworkElement EnergySplit(Report report)
    {
        var parts = new (string name, double joules, Brush color)[]
        {
            ("Apps", report.Breakdown.Apps, Theme.Apps), ("Windows services", report.Breakdown.Services, Theme.Services), ("Display & hardware", report.Breakdown.Hardware, Theme.Hardware),
        };
        var bar = new SplitBar { Height = 14, Parts = parts.Select(p => (p.joules, p.color)).ToList() };
        var rows = parts.Select(p => (UIElement)UI.H(8, new Border { Width = 9, Height = 9, CornerRadius = new CornerRadius(2), Background = p.color }, UI.Text(p.name, 13), UI.Fill,
            UI.Text(Format.Energy(p.joules), 13, color: Theme.Secondary), UI.Text(Format.Share(report.Share(p.joules)), 13, FontWeights.SemiBold).Right().Width(44)));
        var hardware = report.Share(report.Breakdown.Hardware);
        var top = report.Apps.FirstOrDefault(a => a.App.Category == AppCategory.App);
        var sentence = hardware >= 0.5 ? "More than half went to the PC itself – mostly display and graphics." : $"Your apps and services made up {Format.Share(1 - hardware)} together.";
        if (top != null) sentence += $" Biggest app: {top.App.Name} with {Format.Energy(top.Joules)}.";
        return UI.Card(UI.V(14, UI.Text("Where the energy went", 15, FontWeights.SemiBold), bar, UI.V(8, rows), UI.Secondary(sentence, 13)));
    }

    private FrameworkElement TopApps(Report report)
    {
        var top = report.Apps.Take(6).ToList();
        var maximum = Math.Max(0.001, top.FirstOrDefault()?.Joules ?? 1);
        var rows = new List<UIElement?> { UI.H(8, UI.Text("Top consumers", 15, FontWeights.SemiBold), UI.Fill, UI.Link("All apps", () => Navigate?.Invoke("apps"), 13)) };
        foreach (var total in top)
        {
            rows.Add(UI.H(10, UI.AppIcon(total.App, 24), UI.Text(total.App.Name, 13), total.IsEstimated ? UI.EstimatedPill() : null, UI.Fill,
                UI.Bar(total.Joules / maximum, 110, total.App.Category == AppCategory.App ? Theme.Apps : Theme.Services),
                UI.Text((total.IsEstimated ? "≈ " : "") + Format.Energy(total.Joules), 13).Right().Width(74)));
        }
        return UI.Card(UI.V(10, rows));
    }
}

// MARK: Apps

public sealed class AppsPage : Page
{
    private enum Filter { All, Apps, System }
    private enum Column { Name, Energy, Battery, Share, Power, Cpu, Gpu, Background, Wakeups }

    private readonly Grid _root = new();
    private readonly ContentControl _header = Host(0), _filters = Host(16), _table = Host(16), _detail = new() { Focusable = false };
    public override FrameworkElement Root => _root;
    private Report? _report;
    private Filter _filter = Filter.All;
    private string _searchText = "";
    private readonly Grid _search;
    private Column _sort = Column.Energy;
    private bool _descending = true;
    private string? _selection;
    private readonly TextBox _searchBox;

    private static readonly (Column column, string title, double width)[] Columns =
    {
        (Column.Name, "App", 0), (Column.Energy, "Energy", 80), (Column.Battery, "Battery", 64), (Column.Share, "Share", 136), (Column.Power, "Avg. power", 80),
        (Column.Cpu, "CPU time", 76), (Column.Gpu, "GPU time", 72), (Column.Background, "Background", 84), (Column.Wakeups, "Switches/s", 80),
    };

    public AppsPage()
    {
        _searchBox = new TextBox { Width = 170, FontSize = 12.5, VerticalAlignment = VerticalAlignment.Center };
        var placeholder = UI.Secondary("Search apps", 12.5, false).Also(t => { t.IsHitTestVisible = false; t.Margin = new Thickness(11, 0, 0, 0); });
        _searchBox.TextChanged += (_, _) =>
        {
            _searchText = _searchBox.Text;
            placeholder.Visibility = _searchText.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
            RenderTable();
            RenderFilters();
        };
        _search = new Grid { Children = { _searchBox, placeholder } };
        var main = new DockPanel { Margin = new Thickness(28, 24, 28, 24), LastChildFill = true };
        DockPanel.SetDock(_header, Dock.Top);
        DockPanel.SetDock(_filters, Dock.Top);
        main.Children.Add(_header);
        main.Children.Add(_filters);
        main.Children.Add(_table);
        _root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        _root.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        _root.Children.Add(main);
        Grid.SetColumn(_detail, 1);
        _root.Children.Add(_detail);
    }

    private Period Period => M.Settings.AppsPeriod;

    public override void Reload()
    {
        var periods = PeriodExtensions.All;
        if (_search.Parent is Panel old) old.Children.Remove(_search);
        Set(_header, UI.PageHeader("Apps", M.Demo ? "Sample data" : null,
            UI.Segmented(periods.Select(p => p.Title()).ToList(), Array.IndexOf(periods, Period), i => { M.Settings.AppsPeriod = periods[i]; M.Settings.Save(); Reload(); }),
            _search));
        try { _report = M.Store?.Report(Period.Range(), Period.Bucket(), M.Settings.BatteryOnly); }
        catch { _report = null; }
        RenderFilters();
        RenderTable();
        RenderDetail();
    }

    private List<AppTotal> Rows()
    {
        if (_report == null) return new();
        IEnumerable<AppTotal> rows = _report.Apps
            .Where(a => _filter == Filter.All || (_filter == Filter.Apps) == (a.App.Category == AppCategory.App))
            .Where(a => _searchText.Length == 0 || a.App.Name.Contains(_searchText, StringComparison.CurrentCultureIgnoreCase));
        Func<AppTotal, IComparable> key = _sort switch
        {
            Column.Name => a => a.App.Name,
            Column.Power => a => a.AverageWatts,
            Column.Cpu => a => a.CpuSeconds,
            Column.Gpu => a => a.GpuSeconds,
            Column.Background => a => a.BackgroundShare,
            Column.Wakeups => a => a.WakeupsPerSecond,
            _ => a => a.Joules,
        };
        rows = _descending ? rows.OrderByDescending(key) : rows.OrderBy(key);
        return rows.ToList();
    }

    private void RenderFilters()
    {
        var rows = Rows();
        var filters = new[] { Filter.All, Filter.Apps, Filter.System };
        Set(_filters, UI.H(14,
            UI.Segmented(new[] { "All", "Apps", "Services & tools" }, Array.IndexOf(filters, _filter), i => { _filter = filters[i]; RenderTable(); RenderFilters(); }),
            UI.Check("Battery only", M.Settings.BatteryOnly, v => { M.Settings.BatteryOnly = v; M.Settings.Save(); Reload(); }),
            UI.Fill,
            _report == null ? null : UI.Secondary($"{rows.Count} items · together {Format.Energy(rows.Sum(r => r.Joules))} of {Format.Energy(_report.TotalJoules)} total", 12, false)));
    }

    private Grid RowGrid()
    {
        var grid = new Grid();
        foreach (var (_, _, width) in Columns)
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = width == 0 ? new GridLength(1, GridUnitType.Star) : new GridLength(width), MinWidth = width == 0 ? 180 : 0 });
        return grid;
    }

    private static void Put(Grid grid, int column, UIElement element)
    {
        Grid.SetColumn(element, column);
        grid.Children.Add(element);
    }

    private void RenderTable()
    {
        var header = RowGrid();
        header.Margin = new Thickness(14, 10, 14, 8);
        for (var i = 0; i < Columns.Length; i++)
        {
            var (column, title, _) = Columns[i];
            var active = column == _sort;
            var label = UI.Text(title + (active ? (_descending ? "  ˅" : "  ˄") : ""), 12, active ? FontWeights.SemiBold : FontWeights.Normal, active ? Theme.Primary : Theme.Secondary);
            Put(header, i, UI.Link("", () =>
            {
                if (_sort == column) _descending = !_descending;
                else { _sort = column; _descending = column != Column.Name; }
                RenderTable();
            }).Also(b => b.Content = label).Also(b => b.HorizontalAlignment = HorizontalAlignment.Left));
        }
        var list = new StackPanel();
        var rows = Rows();
        var index = 0;
        foreach (var total in rows)
        {
            var grid = RowGrid();
            var name = UI.H(8, UI.AppIcon(total.App, 20), UI.Text(total.App.Name, 13),
                total.App.Category != AppCategory.App ? UI.Pill(total.App.Category == AppCategory.System ? "Windows" : "Tool") : null,
                total.IsEstimated ? UI.EstimatedPill() : null);
            Put(grid, 0, name);
            Put(grid, 1, UI.Text((total.IsEstimated ? "≈ " : "") + Format.Energy(total.Joules), 13));
            Put(grid, 2, UI.Text(Format.BatteryShare(_report!.BatteryPercent(total.Joules)), 13));
            var share = _report.Share(total.Joules);
            Put(grid, 3, UI.H(6, UI.Bar(Math.Min(1, share * 2), 64, Theme.Apps), UI.Text(Format.Share(share), 12.5).Right().Width(44).Margin(0, 0, 18, 0)));
            Put(grid, 4, UI.Text(Format.Watts(total.AverageWatts), 13));
            Put(grid, 5, UI.Text(Format.Duration(total.CpuSeconds, true), 13));
            Put(grid, 6, UI.Text(total.GpuSeconds >= 1 ? Format.Duration(total.GpuSeconds, true) : "–", 13));
            Put(grid, 7, UI.Text(total.App.Category == AppCategory.App ? Format.Share(total.BackgroundShare) : "–", 13));
            Put(grid, 8, UI.Text(Format.Number(total.WakeupsPerSecond, 0), 13));
            grid.Margin = new Thickness(14, 7, 14, 7);
            var selected = total.Id == _selection;
            var background = selected ? Theme.WithOpacity(Theme.Accent, 0.16) : (index++ % 2 == 1 ? Theme.WithOpacity(Theme.Track, 0.5) : Brushes.Transparent);
            var key = total.Id;
            list.Children.Add(UI.Clickable(grid, () => { _selection = _selection == key ? null : key; RenderTable(); RenderDetail(); }, new CornerRadius(6), background));
        }
        UIElement content = list;
        if (_report != null && rows.Count == 0)
            content = UI.V(6, UI.Text("No items", 15, FontWeights.SemiBold).Also(t => t.HorizontalAlignment = HorizontalAlignment.Center),
                UI.Secondary("No measurements for this period and filter.", 13, false).Also(t => t.HorizontalAlignment = HorizontalAlignment.Center)).Margin(0, 80, 0, 0);
        var scroll = new ScrollViewer { Content = content, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Padding = new Thickness(6, 0, 6, 6) };
        var dock = new DockPanel();
        var headerBox = new Border { Child = header, BorderBrush = Theme.CardEdge, BorderThickness = new Thickness(0, 0, 0, 1), Margin = new Thickness(6, 0, 6, 0) };
        DockPanel.SetDock(headerBox, Dock.Top);
        dock.Children.Add(headerBox);
        dock.Children.Add(scroll);
        Set(_table, new Border { Child = dock, Background = Theme.Card, CornerRadius = new CornerRadius(12), BorderBrush = Theme.CardEdge, BorderThickness = new Thickness(1) });
    }

    private void RenderDetail()
    {
        var total = _report?.Apps.FirstOrDefault(a => a.Id == _selection);
        if (total == null || _report == null || M.Store == null) { _detail.Content = null; return; }
        var report = _report;
        var series = M.Store.Series(total.App.Key, report.Range, Period.Bucket(), M.Settings.BatteryOnly);
        var members = M.Store.Members(total.App.Key, report.Range);
        var items = new List<UIElement?>
        {
            UI.H(12, UI.AppIcon(total.App, 48), UI.V(2, UI.Text(total.App.Name, 18, FontWeights.SemiBold),
                UI.H(6, UI.Secondary(total.App.Category.Label(), 12, false), total.IsEstimated ? UI.EstimatedPill() : null)), UI.Fill,
                UI.Link("", () => { _selection = null; RenderTable(); RenderDetail(); }).Also(b => b.Content = UI.Icon(Glyph.Close, 12)).Also(b => b.VerticalAlignment = VerticalAlignment.Top)),
        };
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        var metrics = new (string, string)[]
        {
            ("Energy", Format.Energy(total.Joules)), ("Battery", Format.BatteryShare(report.BatteryPercent(total.Joules))),
            ("Avg. power", Format.Watts(total.AverageWatts)), ("Share of total", Format.Share(report.Share(total.Joules))),
            ("CPU time", Format.Duration(total.CpuSeconds, true)), ("GPU time", total.GpuSeconds >= 1 ? Format.Duration(total.GpuSeconds, true) : "–"),
            ("In background", total.App.Category == AppCategory.App ? Format.Share(total.BackgroundShare) : "–"), ("Context switches/s", Format.Number(total.WakeupsPerSecond, 0)),
        };
        for (var i = 0; i < metrics.Length; i++)
        {
            if (i % 2 == 0) grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var m = UI.Metric(metrics[i].Item1, metrics[i].Item2).Margin(0, 0, 0, 12);
            Grid.SetRow(m, i / 2); Grid.SetColumn(m, i % 2);
            grid.Children.Add(m);
        }
        items.Add(grid);
        items.Add(UI.V(8, UI.SectionTitle("History", Period.Title()), new SeriesChart { Series = series, Range = report.Range, Period = Period, Height = 130 }));
        if (members.Count > 0)
        {
            var sum = Math.Max(0.001, members.Sum(m => m.Joules));
            items.Add(UI.V(6, new UIElement[] { UI.SectionTitle("Processes", "What uses energy inside the app") }
                .Concat(members.Select(m => (UIElement)UI.H(8, UI.Text(m.Name, 12), UI.Fill, UI.Text(Format.Share(m.Joules / sum), 12, color: Theme.Secondary))))));
        }
        var explanation = total.IsEstimated
            ? "This PC has no readable energy meter, so Joule estimates the energy from the app’s CPU time."
            : "This is the processor energy spent on this app – measured by the CPU’s energy meter and shared out by the CPU cycles each process used, including helper processes.";
        if (total.GpuSeconds >= 1) explanation += " Graphics work (GPU time) is part of “Display & hardware”.";
        if (total.WakeupsPerSecond > 1000) explanation += " Many context switches keep the processor out of its power-saving states – that costs battery even when idle.";
        items.Add(UI.Secondary(explanation, 11.5));
        var actions = new List<UIElement?>();
        if (M.IsRunning(total.App)) actions.Add(UI.Link("Quit app", () => M.Quit(total.App)));
        if (total.App.BundlePath is { } path && File.Exists(path))
            actions.Add(UI.Link("Show in Explorer", () => Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{path}\"") { UseShellExecute = true })));
        if (M.Warnings.Muted.Contains(total.App.Key)) actions.Add(UI.Link("Alert again", () => { M.Unmute(total.App.Key); RenderDetail(); }));
        else actions.Add(UI.Link("No alerts for this app", () => { M.Mute(total.App.Key); RenderDetail(); }));
        items.Add(UI.V(8, actions));
        _detail.Content = new Border
        {
            Width = 360, Background = Theme.Card, BorderBrush = Theme.CardEdge, BorderThickness = new Thickness(1, 0, 0, 0),
            Child = new ScrollViewer { Content = UI.V(18, items).Margin(20, 20, 20, 20), VerticalScrollBarVisibility = ScrollBarVisibility.Auto },
        };
    }
}

// MARK: Battery

public sealed class BatteryPage : Page
{
    private readonly ContentControl _body = Host(0);
    private readonly ScrollViewer _root;
    public override FrameworkElement Root => _root;

    public BatteryPage()
    {
        var stack = Stack();
        stack.Children.Add(_body);
        _root = PageScroll(stack);
    }

    public override void Reload()
    {
        var sessions = M.Store?.Sessions(DateTime.Now.AddDays(-14)) ?? new();
        var sleeps = (M.Store?.LastSleeps(14) ?? new()).AsEnumerable().Reverse().ToList();
        Set(_body, UI.V(22,
            UI.PageHeader("Battery", M.Demo ? "Sample data" : null),
            UI.Columns(22, HealthCard(), SleepCard(sleeps)),
            SessionsCard(sessions)));
    }

    public override void LiveUpdate() { }

    private FrameworkElement HealthCard()
    {
        if (M.Power is not { } power) return UI.Card(UI.EmptyNote("No battery found."), 22);
        var verdict = power.HealthPercent switch { >= 90 => "Like new", >= 80 => "In good shape", >= 70 => "Noticeably aged", _ => "Consider a replacement" };
        var ring = new Grid { Width = 132, Height = 132 };
        ring.Children.Add(new HealthRing { Percent = power.HealthPercent });
        ring.Children.Add(UI.V(0, UI.Title(Format.Percent(power.HealthPercent), 24).Also(t => t.HorizontalAlignment = HorizontalAlignment.Center),
            UI.Secondary("Health", 11, false).Also(t => t.HorizontalAlignment = HorizontalAlignment.Center)).Also(s => s.VerticalAlignment = VerticalAlignment.Center));
        var flow = power.BatteryWatts > 0.2 ? $"{Format.Watts(power.BatteryWatts)} in" : (power.BatteryWatts < -0.2 ? $"{Format.Watts(-power.BatteryWatts)} out" : Format.Watts(0));
        var metrics = new StackPanel { Orientation = Orientation.Horizontal };
        void Add(string label, string value) => metrics.Children.Add(UI.Metric(label, value).Margin(0, 0, 24, 0));
        Add("Cycle count", power.CycleCount > 0 ? power.CycleCount.ToString() : "–");
        Add("Voltage", $"{Format.Number(power.Voltage, 2)} V");
        if (power.TemperatureC is double t && t > 0) Add("Temperature", $"{Format.Number(t)} °C");
        Add("Battery flow", flow);
        return UI.Card(UI.V(18,
            UI.SectionTitle("Battery health", "How much of the original capacity is left"),
            UI.HStretch(24, 1, ring, UI.V(4, UI.Text(verdict, 14, FontWeights.SemiBold),
                UI.Secondary($"A full charge holds {Format.Number(power.FullWh, 0)} Wh today; it was {Format.Number(power.DesignWh, 0)} Wh when new.", 12))),
            UI.Divider(),
            metrics), 22);
    }

    private FrameworkElement SleepCard(List<SleepRecord> sleeps)
    {
        var items = new List<UIElement?> { UI.SectionTitle("Sleep", "Drain per hour while the PC was asleep") };
        if (sleeps.Count == 0) items.Add(UI.EmptyNote("As soon as your PC has slept for more than half an hour, it shows up here."));
        else
        {
            items.Add(new SleepChart { Sleeps = sleeps, Threshold = M.Warnings.SleepPercentPerHour, Height = 150 });
            var worst = sleeps.MaxBy(s => s.PercentPerHour)!;
            items.Add(UI.Secondary($"Highest drain: {Format.Percent(worst.PercentLost)} in {Format.Duration(worst.Hours * 3600)} ({Format.DayTime(worst.Start)}).", 11.5));
        }
        return UI.Card(UI.V(12, items), 22);
    }

    private FrameworkElement SessionsCard(List<DischargeSession> sessions)
    {
        var items = new List<UIElement?> { UI.SectionTitle("Battery sessions", "Every stretch off the adapter – how far a charge gets you") };
        if (sessions.Count == 0)
        {
            items.Add(UI.EmptyNote("No battery session recorded yet. Unplug the adapter to get started."));
            return UI.Card(UI.V(12, items));
        }
        var fullWh = M.Power?.FullWh ?? 0;
        var grid = new Grid();
        foreach (var w in new[] { 200.0, 80, 176, 64, 84, 150, 70 }) grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(w) });
        void Cell(int row, int column, UIElement element) { Grid.SetRow(element, row); Grid.SetColumn(element, column); grid.Children.Add(element); }
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        var headers = new[] { "Period", "Battery", "", "Awake", "Avg. power", "Projected", "" };
        for (var i = 0; i < headers.Length; i++) Cell(0, i, UI.Text(headers[i], 11, FontWeights.Medium, Theme.Secondary).Margin(0, 0, 0, 8));
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        var line = UI.Divider();
        Grid.SetColumnSpan(line, 7);
        Cell(1, 0, line);
        var row = 2;
        foreach (var session in sessions.Take(10))
        {
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var s = session;
            Cell(row, 0, UI.V(1, UI.Text(Format.DayTime(s.Start), 12.5, FontWeights.Medium), UI.Secondary(s.Ongoing ? "ongoing" : $"until {Format.Time(s.End)}", 11, false)).Margin(0, 10, 0, 0));
            Cell(row, 1, UI.Text($"{Format.Number(s.PercentStart, 0)} → {Format.Number(s.PercentEnd, 0)} %", 12.5).Margin(0, 10, 0, 0));
            Cell(row, 2, new RangeBar { From = s.PercentEnd, To = s.PercentStart, Width = 160, Height = 8, HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 10, 0, 0) });
            Cell(row, 3, UI.Text(Format.Duration(s.AwakeSeconds, true), 12.5).Margin(0, 10, 0, 0));
            Cell(row, 4, UI.Text(Format.Watts(s.AverageWatts), 12.5).Margin(0, 10, 0, 0));
            Cell(row, 5, UI.Text(s.AverageWatts > 0 && fullWh > 0 ? $"{Format.Duration(fullWh / s.AverageWatts * 3600, true)} per charge" : "–", 12.5, color: Theme.Secondary).Margin(0, 10, 0, 0));
            Cell(row, 6, UI.Link("Where?", () => SessionWindow.Show(s), 12.5).Tip("Shows what used this session’s percent").Margin(0, 10, 0, 0));
            row++;
        }
        items.Add(grid);
        return UI.Card(UI.V(12, items));
    }
}

/// <summary>"Where?" for one past session.</summary>
public static class SessionWindow
{
    public static void Show(DischargeSession session)
    {
        var store = Monitor.Shared.Store;
        if (store == null) return;
        var breakdown = store.Breakdown(session);
        var timeline = store.Timeline(session);
        var window = new Window
        {
            Title = "Battery session", WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Owner = Application.Current.MainWindow, Background = Theme.Window, ShowInTaskbar = false, ResizeMode = ResizeMode.CanResizeWithGrip,
            UseLayoutRounding = true,
        };
        UiScale.Size(window, 760, 700, 520, 400);
        var done = UI.Button("Done", window.Close, true).Also(b => { b.HorizontalAlignment = HorizontalAlignment.Right; b.IsDefault = true; b.IsCancel = true; });
        window.Content = new ScrollViewer
        {
            Content = UI.V(16, Views.SessionBreakdown(breakdown, timeline, Monitor.Shared.Power?.FullWh ?? 0, Array.Empty<string>(), null, 14), done).Margin(26, 26, 26, 26),
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            LayoutTransform = UiScale.Transform(),
        };
        window.ShowDialog();
    }
}

// MARK: Alerts

public sealed class AlertsPage : Page
{
    private enum Filter { All, Apps, Sleep, Drain, Devices }
    private readonly ContentControl _body = Host(0);
    private readonly ScrollViewer _root;
    private Filter _filter = Filter.All;
    public override FrameworkElement Root => _root;
    public Action? OpenRules;

    public AlertsPage()
    {
        var stack = Stack();
        stack.Children.Add(_body);
        _root = PageScroll(stack, 880);
    }

    private static bool Matches(Filter filter, EventKind kind) => filter switch
    {
        Filter.All => true,
        Filter.Apps => kind is EventKind.AppHog or EventKind.BackgroundHog,
        Filter.Sleep => kind is EventKind.SleepBlocker or EventKind.SleepDrain,
        Filter.Devices => kind is EventKind.DeviceLow,
        _ =>kind is EventKind.HighDrain or EventKind.LowBattery or EventKind.Health,
    };

    public override void Reload()
    {
        var alerts = M.Store?.Alerts() ?? new();
        var apps = M.Store?.AppIdentities() ?? new();
        var items = new List<UIElement?>
        {
            UI.PageHeader("Alerts", null, UI.Button("Adjust rules …", () => OpenRules?.Invoke())),
            UI.Secondary("Joule lets you know when an app draws unusually much, something blocks sleep, the battery drains overnight, or a device runs low.", 14),
        };
        var chips = new StackPanel { Orientation = Orientation.Horizontal };
        foreach (var (filter, title) in new[] { (Filter.All, "All"), (Filter.Apps, "Apps"), (Filter.Sleep, "Sleep"), (Filter.Drain, "Drain"), (Filter.Devices, "Devices") })
        {
            var selected = filter == _filter;
            var chip = new Border
            {
                Child = UI.Text(title, 12, selected ? FontWeights.SemiBold : FontWeights.Normal, selected ? Theme.Window : Theme.Primary),
                Padding = new Thickness(12, 4, 12, 5), CornerRadius = new CornerRadius(12), Margin = new Thickness(0, 0, 8, 0),
                Background = selected ? Theme.Primary : Theme.Card, BorderBrush = selected ? Brushes.Transparent : Theme.CardEdge, BorderThickness = new Thickness(1), Cursor = Cursors.Hand,
            };
            var f = filter;
            chip.MouseLeftButtonUp += (_, _) => { _filter = f; Reload(); };
            chips.Children.Add(chip);
        }
        items.Add(chips);
        var groups = alerts.Where(a => Matches(_filter, a.Kind)).GroupBy(a => a.Date.Date).OrderByDescending(g => g.Key).ToList();
        if (groups.Count == 0)
        {
            items.Add(UI.V(6, UI.Icon(Glyph.Check, 28, Theme.Tertiary), UI.Text("All quiet", 17, FontWeights.SemiBold).Also(t => t.HorizontalAlignment = HorizontalAlignment.Center),
                UI.Secondary("Nothing needed your attention so far.", 13, false).Also(t => t.HorizontalAlignment = HorizontalAlignment.Center)).Margin(0, 80, 0, 80));
        }
        foreach (var group in groups)
        {
            var day = group.Key;
            var title = day == DateTime.Today ? "Today" : day == DateTime.Today.AddDays(-1) ? "Yesterday" : day.ToString("dddd, MMMM d");
            items.Add(UI.V(8, UI.Text(title, 13, FontWeights.SemiBold, Theme.Secondary).Margin(4, 0, 0, 0),
                UI.GroupedList(group.Select(alert => (UIElement)Views.NoticeRow(M, alert, () => { M.Store?.DeleteAlert(alert.Id); Reload(); }, apps)), 64)));
        }
        Set(_body, UI.V(18, items));
    }

    public override void Hidden() => M.MarkAlertsRead();
}
