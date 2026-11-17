using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Joule.Core;

namespace Joule;

/// <summary>Pieces shared by the window and the tray panel.</summary>
public static class Views
{
    /// <summary>Splits the whole-machine draw so the parts add up to the number on top.</summary>
    public static FrameworkElement PowerSplit(double total, LiveSplit? split, bool compact)
    {
        var bar = new SplitBar { Height = compact ? 6 : 8 };
        var legend = new WrapPanel();
        if (split == null)
        {
            legend.Children.Add(UI.Secondary("Measuring breakdown …", compact ? 10.5 : 11.5, false));
        }
        else
        {
            var parts = new (string name, string shortName, double watts, Brush color)[]
            {
                ("Apps", "Apps", split.Apps, Theme.Apps),
                ("Windows services", "Services", split.Services, Theme.Services),
                ("Display & hardware", "Display & co.", split.Hardware(total), Theme.Hardware),
            };
            bar.Parts = parts.Select(p => (p.watts, p.color)).ToList();
            foreach (var p in parts)
            {
                legend.Children.Add(UI.H(4,
                    new Border { Width = 6, Height = 6, CornerRadius = new CornerRadius(3), Background = p.color },
                    UI.Text(compact ? p.shortName : p.name, compact ? 10.5 : 11.5, color: Theme.Secondary),
                    UI.Text(Format.Watts(p.watts), compact ? 10.5 : 11.5)).Margin(0, 0, compact ? 10 : 16, 0));
            }
        }
        return UI.V(6, bar, legend);
    }

    public static FrameworkElement LiveAppRow(LiveApp item, double maximum, bool compact)
    {
        var name = new StackPanel { Orientation = Orientation.Horizontal };
        name.Children.Add(UI.Text(item.App.Name, compact ? 12.5 : 13));
        if (!compact && item.Background && item.App.Category == AppCategory.App) name.Children.Add(UI.Pill("background").Margin(6, 0, 0, 0));
        if (!compact && item.GpuShare >= 0.05) name.Children.Add(UI.Pill($"GPU {Format.Share(item.GpuShare)}", Theme.Purple).Margin(6, 0, 0, 0));
        if (!compact && item.Estimated) name.Children.Add(UI.EstimatedPill().Margin(6, 0, 0, 0));
        var watts = UI.Text((item.Estimated ? "≈ " : "") + Format.SmallWatts(item.Watts), compact ? 12 : 13, color: Theme.Secondary).Right();
        if (!compact) watts.Width = 64;
        return UI.H(10,
            UI.AppIcon(item.App, compact ? 20 : 24),
            name,
            UI.Fill,
            compact ? null : UI.Bar(item.Watts / maximum, 110, Theme.Accent),
            watts).Margin(0, compact ? 0 : 4, 0, compact ? 0 : 4);
    }

    // MARK: Session breakdown

    public static FrameworkElement SessionBreakdown(SessionBreakdown breakdown, SessionTimeline? timeline, double fullWh, IReadOnlyList<string> blockers,
                                                    Action? showAll, int maxRows = 8)
    {
        var session = breakdown.Session;
        var used = Format.Number(breakdown.PercentUsed, 0);
        var heading = session.Ongoing ? $"Where did the {used} % go?" : $"{used} % this session";
        var start = Format.DayTime(session.Start);
        var range = $"{Format.Number(session.PercentStart, 0)} % → {Format.Number(session.PercentEnd, 0)} %";
        var since = breakdown.StartsAtRecording ? $"Since recording began · {start} · {range}"
            : !session.Ongoing ? $"{start} to {Format.Time(session.End)} · {range}"
            : session.ChargedUnseen ? $"Since the last charge · {start} · {range}"
            : $"Since unplugging · {start} · {range}";

        var children = new List<UIElement?>
        {
            UI.H(12, UI.V(3, UI.Title(heading, 22), UI.Secondary(since, 13, false)), UI.Fill,
                showAll == null ? null : UI.Link("All sessions", showAll, 13).Also(l => l.VerticalAlignment = VerticalAlignment.Top)),
        };
        if (breakdown.PercentUsed < 1)
        {
            children.Add(UI.EmptyNote("Not a full percent used since unplugging. As soon as the battery drops, you’ll see here what used it."));
            return UI.V(18, children);
        }
        var sentences = Narrative.Explain(breakdown, fullWh, blockers);
        if (sentences.Count > 0)
        {
            var text = UI.Text(string.Join(" ", sentences), 15, wrap: true);
            text.LineHeight = 22;
            children.Add(new Border
            {
                Background = Theme.AccentSoft, CornerRadius = new CornerRadius(10), Padding = new Thickness(16, 14, 16, 14),
                Child = UI.HStretch(12, 1, UI.Icon(Glyph.Message, 16, Theme.Accent).Also(i => i.VerticalAlignment = VerticalAlignment.Top).Margin(0, 3, 0, 0), text),
            });
        }
        if (timeline != null && timeline.Points.Count >= 2) children.Add(Timeline(timeline));
        var maximum = Math.Max(0.01, breakdown.Entries.Select(e => e.Percent).DefaultIfEmpty(1).Max());
        children.Add(UI.GroupedList(breakdown.Entries.Take(maxRows).Select(e => ShareRow(e, maximum))));
        var parts = new List<string> { "Shared out by measured energy on battery." };
        if (breakdown.SleepHours > 0.05) parts.Add($"Sleep: {Format.Duration(breakdown.SleepHours * 3600)}.");
        if (breakdown.StartsAtRecording && session.PercentStart < 99.5) parts.Add($"Joule didn’t see what happened before {Format.Number(session.PercentStart, 0)} %.");
        if (breakdown.UnrecordedSeconds > 300) parts.Add($"{Format.Duration(breakdown.UnrecordedSeconds)} without recording are shared out proportionally.");
        children.Add(UI.Secondary(string.Join(" ", parts).Replace("..", "."), 11));
        return UI.V(18, children);
    }

    private static FrameworkElement ShareRow(ShareEntry entry, double maximum)
    {
        FrameworkElement icon = entry.Kind switch
        {
            ShareKind.App or ShareKind.Service => UI.AppIcon(entry.App, 26),
            ShareKind.Others => UI.SymbolTile(Glyph.Stack, Theme.Services),
            ShareKind.Hardware => UI.SymbolTile(Glyph.Display, Theme.Secondary),
            _ => UI.SymbolTile(Glyph.Moon, Theme.Indigo),
        };
        var color = entry.Kind switch
        {
            ShareKind.App => Theme.Apps,
            ShareKind.Service or ShareKind.Others => Theme.Services,
            ShareKind.Hardware => Theme.Hardware,
            _ => Theme.WithOpacity(Theme.Indigo, 0.75),
        };
        return UI.H(12,
            icon,
            UI.Text(entry.Name, 14),
            entry.Estimated ? UI.EstimatedPill() : null,
            UI.Fill,
            UI.Bar(entry.Percent / maximum, 140, color),
            UI.Text((entry.Estimated ? "≈ " : "") + Format.Percent(entry.Percent, entry.Percent < 10 ? 1 : 0), 14, FontWeights.SemiBold).Right().Width(64))
            .Margin(14, 9, 14, 9);
    }

    /// <summary>Battery line and power over the session, with which app was in front underneath.</summary>
    public static FrameworkElement Timeline(SessionTimeline timeline)
    {
        const double labelWidth = 170;
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(labelWidth) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(14) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        void Add(UIElement label, UIElement content, double top)
        {
            var row = grid.RowDefinitions.Count;
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            if (label is FrameworkElement fl) fl.Margin = new Thickness(0, top, 0, 0);
            if (content is FrameworkElement fc) fc.Margin = new Thickness(0, top, 0, 0);
            Grid.SetRow(label, row); Grid.SetRow(content, row); Grid.SetColumn(content, 2);
            grid.Children.Add(label); grid.Children.Add(content);
        }
        Add(UI.Secondary("Battery", 12, false), new SessionChart { Timeline = timeline, Height = 92 }, 0);
        var span = Math.Max(60, (timeline.End - timeline.Start).TotalSeconds);
        for (var i = 0; i < timeline.Lanes.Count; i++)
        {
            var lane = timeline.Lanes[i];
            var color = lane.Kind == SessionTimeline.LaneKind.Locked ? Theme.Locked : Theme.Lanes[i % Theme.Lanes.Length];
            var label = UI.HStretch(6, 1, new Border { Width = 8, Height = 8, CornerRadius = new CornerRadius(2), Background = color },
                UI.Text(lane.Name, 12), UI.Secondary(Format.Duration(lane.Seconds, true), 10.5, false).Margin(0, 0, 4, 0));
            var bar = new LaneBar { Start = timeline.Start, End = timeline.End, Blocks = lane.Blocks, Color = color, Height = 16 };
            bar.ToolTip = lane.Kind == SessionTimeline.LaneKind.Locked ? $"{lane.Name}: {Format.Duration(lane.Seconds)} locked but awake" : $"{lane.Name}: {Format.Duration(lane.Seconds)} in front";
            Add(label, bar, 8);
        }
        Add(new Border(), new SpreadLabels { Items = Enumerable.Range(0, 4).Select(i => Format.Time(timeline.Start.AddSeconds(span * i / 3))).ToList() }, 8);
        return grid;
    }

    // MARK: Alerts

    public static string KindGlyph(EventKind kind) => kind switch
    {
        EventKind.AppHog => Glyph.Fire,
        EventKind.BackgroundHog => Glyph.Hide,
        EventKind.SleepBlocker => Glyph.Sun,
        EventKind.HighDrain => Glyph.Warning,
        EventKind.SleepDrain => Glyph.Moon,
        EventKind.LowBattery => Glyph.Battery,
        EventKind.DeviceLow => Glyph.Bluetooth,
        _ => Glyph.Heart,
    };

    public static Brush KindTint(EventKind kind) => kind switch
    {
        EventKind.AppHog or EventKind.HighDrain => Theme.Red,
        EventKind.BackgroundHog => Theme.Accent,
        EventKind.SleepBlocker => Theme.Brown,
        EventKind.SleepDrain => Theme.Indigo,
        EventKind.LowBattery or EventKind.DeviceLow => Theme.Red,
        _ => Theme.Purple,
    };

    public static FrameworkElement NoticeRow(Monitor monitor, Notice alert, Action? onDelete, IReadOnlyDictionary<string, AppIdentity> apps)
    {
        var app = alert.AppKey != null && apps.TryGetValue(alert.AppKey, out var a) ? a : null;
        FrameworkElement icon;
        if (app != null && app.Category == AppCategory.App && IconCache.Get(app.BundlePath) != null)
        {
            var badge = new Border
            {
                Width = 16, Height = 16, CornerRadius = new CornerRadius(8), Background = KindTint(alert.Kind),
                Child = UI.Icon(KindGlyph(alert.Kind), 9, Brushes.White), HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Bottom,
                Margin = new Thickness(0, 0, -4, -4),
            };
            icon = new Grid { Width = 32, Height = 32, Children = { UI.AppIcon(app, 32), badge } };
        }
        else
        {
            icon = UI.SymbolTile(KindGlyph(alert.Kind), KindTint(alert.Kind), 32);
        }
        icon.VerticalAlignment = VerticalAlignment.Top;

        var title = UI.H(6, UI.Text(alert.Title, 14, FontWeights.SemiBold),
            alert.Read ? null : new Border { Width = 6, Height = 6, CornerRadius = new CornerRadius(3), Background = Theme.Accent },
            UI.Fill, UI.Secondary(Format.Time(alert.Date), 11, false));
        var actions = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 4, 0, 0) };
        if (app != null && alert.Kind != EventKind.SleepDrain && alert.Kind != EventKind.HighDrain && monitor.IsRunning(app))
            actions.Children.Add(UI.Button($"Quit {app.Name}", () => monitor.Quit(app)).Margin(0, 0, 14, 0));
        if (alert.AppKey != null && alert.Kind != EventKind.SleepDrain)
        {
            if (monitor.Warnings.Muted.Contains(alert.AppKey)) actions.Children.Add(UI.Text("Muted", 11.5, color: Theme.Tertiary).Margin(0, 0, 14, 0));
            else actions.Children.Add(UI.Link(alert.Kind == EventKind.DeviceLow ? "Stop alerting for this device" : "Stop alerting for this app", () => monitor.Mute(alert.AppKey), 11.5).Margin(0, 0, 14, 0));
        }
        if (onDelete != null) actions.Children.Add(UI.Link("Remove", onDelete, 11.5));
        var body = UI.V(3, title, UI.Secondary(alert.Body, 13), actions);
        return UI.HStretch(12, 1, icon, body).Margin(18, 16, 18, 16);
    }
}
