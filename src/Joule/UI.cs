using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;
using Joule.Core;

namespace Joule;

/// <summary>Small builders so pages read like a description of the layout.</summary>
public static class UI
{
    public static readonly object Fill = new();

    public static TextBlock Text(string text, double size = 13, FontWeight? weight = null, Brush? color = null, bool wrap = false)
    {
        var block = new TextBlock
        {
            Text = text, FontSize = size, FontWeight = weight ?? FontWeights.Normal, Foreground = color ?? Theme.Primary,
            TextWrapping = wrap ? TextWrapping.Wrap : TextWrapping.NoWrap, TextTrimming = wrap ? TextTrimming.None : TextTrimming.CharacterEllipsis,
            FontFamily = Theme.Font, VerticalAlignment = VerticalAlignment.Center,
        };
        System.Windows.Documents.Typography.SetNumeralAlignment(block, FontNumeralAlignment.Tabular);
        return block;
    }

    public static TextBlock Title(string text, double size) => Text(text, size, FontWeights.Bold).Also(t => t.FontFamily = Theme.DisplayFont);
    public static TextBlock Secondary(string text, double size = 12, bool wrap = true) => Text(text, size, color: Theme.Secondary, wrap: wrap);

    public static T Also<T>(this T value, Action<T> configure) { configure(value); return value; }
    public static T Margin<T>(this T element, double left, double top, double right, double bottom) where T : FrameworkElement { element.Margin = new Thickness(left, top, right, bottom); return element; }
    public static T Width<T>(this T element, double width) where T : FrameworkElement { element.Width = width; return element; }
    public static T Tip<T>(this T element, string tip) where T : FrameworkElement { element.ToolTip = tip; return element; }
    public static T Right<T>(this T element) where T : FrameworkElement { element.HorizontalAlignment = HorizontalAlignment.Right; if (element is TextBlock t) t.TextAlignment = TextAlignment.Right; return element; }

    public static StackPanel V(double spacing, params UIElement?[] children)
    {
        var panel = new StackPanel { Orientation = Orientation.Vertical };
        var first = true;
        foreach (var child in children)
        {
            if (child == null) continue;
            if (!first && child is FrameworkElement fe) fe.Margin = new Thickness(fe.Margin.Left, fe.Margin.Top + spacing, fe.Margin.Right, fe.Margin.Bottom);
            panel.Children.Add(child);
            first = false;
        }
        return panel;
    }

    public static StackPanel V(double spacing, IEnumerable<UIElement?> children) => V(spacing, children.ToArray());

    /// <summary>A row: elements at their natural width, UI.Fill takes the remaining space.</summary>
    public static Grid H(double spacing, params object?[] items)
    {
        var grid = new Grid();
        var column = 0;
        var first = true;
        foreach (var item in items)
        {
            if (item == null) continue;
            if (ReferenceEquals(item, Fill))
            {
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                column++;
                continue;
            }
            var element = (UIElement)item;
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            if (element is FrameworkElement fe)
            {
                if (!first) fe.Margin = new Thickness(fe.Margin.Left + spacing, fe.Margin.Top, fe.Margin.Right, fe.Margin.Bottom);
                if (fe.ReadLocalValue(FrameworkElement.VerticalAlignmentProperty) == DependencyProperty.UnsetValue) fe.VerticalAlignment = VerticalAlignment.Center;
            }
            Grid.SetColumn(element, column++);
            grid.Children.Add(element);
            first = false;
        }
        return grid;
    }

    /// <summary>Like H, but the element at <paramref name="stretchIndex"/> takes the remaining width.</summary>
    public static Grid HStretch(double spacing, int stretchIndex, params UIElement[] items)
    {
        var grid = H(spacing, items);
        grid.ColumnDefinitions[stretchIndex].Width = new GridLength(1, GridUnitType.Star);
        return grid;
    }

    /// <summary>Equal-width columns, e.g. two cards side by side.</summary>
    public static Grid Columns(double spacing, params UIElement[] items)
    {
        var grid = new Grid();
        for (var i = 0; i < items.Length; i++)
        {
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            if (i > 0 && items[i] is FrameworkElement fe) fe.Margin = new Thickness(spacing, 0, 0, 0);
            Grid.SetColumn(items[i], i);
            grid.Children.Add(items[i]);
        }
        return grid;
    }

    public static Border Card(UIElement content, double padding = 20) => new()
    {
        Child = content,
        Padding = new Thickness(padding),
        CornerRadius = new CornerRadius(12),
        Background = Theme.Card,
        BorderBrush = Theme.CardEdge,
        BorderThickness = new Thickness(1),
        Effect = new DropShadowEffect { BlurRadius = 6, ShadowDepth = 1, Opacity = Theme.Dark ? 0.25 : 0.06, Direction = 270 },
    };

    public static Border Pill(string text, Brush? color = null)
    {
        var tint = color ?? Theme.Secondary;
        return new Border
        {
            Child = Text(text, 10.5, FontWeights.SemiBold, color != null ? tint : Theme.Secondary),
            Padding = new Thickness(6, 1, 6, 2),
            CornerRadius = new CornerRadius(8),
            Background = Theme.WithOpacity(tint, 0.14),
            VerticalAlignment = VerticalAlignment.Center,
        };
    }

    public static Border EstimatedPill() => Pill("estimated", Theme.Accent).Tip("Windows exposes no energy meter on this PC. The energy is estimated from CPU time.");

    /// <summary>A capsule track with a filled part.</summary>
    public static FrameworkElement Bar(double fraction, double width, Brush color, double height = 5)
    {
        var grid = new Grid { Width = width, Height = height, VerticalAlignment = VerticalAlignment.Center };
        grid.Children.Add(new Border { Background = Theme.Track, CornerRadius = new CornerRadius(height / 2) });
        grid.Children.Add(new Border
        {
            Background = color, CornerRadius = new CornerRadius(height / 2), HorizontalAlignment = HorizontalAlignment.Left,
            Width = Math.Max(3, width * Math.Clamp(double.IsFinite(fraction) ? fraction : 0, 0, 1)),
        });
        return grid;
    }

    public static TextBlock Icon(string glyph, double size, Brush? color = null) => new()
    {
        Text = glyph, FontFamily = Theme.Icons, FontSize = size, Foreground = color ?? Theme.Secondary, VerticalAlignment = VerticalAlignment.Center,
        HorizontalAlignment = HorizontalAlignment.Center,
    };

    public static Border SymbolTile(string glyph, Brush tint, double size = 26) => new()
    {
        Width = size, Height = size, CornerRadius = new CornerRadius(size * 0.24),
        Background = Theme.WithOpacity(tint, 0.15),
        Child = Icon(glyph, size * 0.52, tint),
        VerticalAlignment = VerticalAlignment.Center,
    };

    public static FrameworkElement AppIcon(AppIdentity? app, double size)
    {
        if (app == null) return SymbolTile(Glyph.Stack, Theme.Services, size);
        if (app.Category == AppCategory.System && app.BundlePath == null) return SymbolTile(Glyph.Settings, Theme.Services, size);
        if (IconCache.Get(app.BundlePath) is { } image)
            return new Image { Source = image, Width = size, Height = size, VerticalAlignment = VerticalAlignment.Center }.Also(i => RenderOptions.SetBitmapScalingMode(i, BitmapScalingMode.HighQuality));
        if (app.Category == AppCategory.Tool) return SymbolTile("", Theme.Secondary, size);
        // A tile with the initial, coloured by name.
        var palette = new[] { Theme.Apps, Theme.Services, Theme.Accent, Theme.Lanes[0], Theme.Purple, Theme.Red };
        var color = palette[Math.Abs(app.Name.GetHashCode()) % palette.Length];
        return new Border
        {
            Width = size, Height = size, CornerRadius = new CornerRadius(size * 0.24), Background = color, VerticalAlignment = VerticalAlignment.Center,
            Child = Text(app.Name.Length > 0 ? app.Name[..1].ToUpperInvariant() : "?", size * 0.5, FontWeights.SemiBold, Brushes.White).Also(t => t.HorizontalAlignment = HorizontalAlignment.Center),
        };
    }

    public static Button Link(string text, Action click, double size = 12.5, Brush? color = null)
    {
        var button = new Button
        {
            Content = Text(text, size, color: color ?? Theme.Accent), Cursor = Cursors.Hand, Background = Brushes.Transparent, BorderThickness = new Thickness(0),
            Padding = new Thickness(0), VerticalAlignment = VerticalAlignment.Center, FocusVisualStyle = null, MinHeight = 0, MinWidth = 0,
            Template = PlainTemplate(),
        };
        button.Click += (_, _) => click();
        return button;
    }

    public static Button Button(string text, Action click, bool primary = false)
    {
        var button = new Button
        {
            Content = text, Padding = new Thickness(12, 4, 12, 5), FontSize = 12.5, VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Left, MinHeight = 0,
        };
        if (primary) button.Style = Application.Current.TryFindResource("AccentButtonStyle") as Style ?? button.Style;
        button.Click += (_, _) => click();
        return button;
    }

    /// <summary>A button with no chrome: just its content, with a hover background.</summary>
    public static Button Clickable(UIElement content, Action click, CornerRadius? radius = null, Brush? background = null)
    {
        var button = new Button
        {
            Content = content, Cursor = Cursors.Hand, Background = background ?? Brushes.Transparent, BorderThickness = new Thickness(0),
            Padding = new Thickness(0), HorizontalContentAlignment = HorizontalAlignment.Stretch, HorizontalAlignment = HorizontalAlignment.Stretch,
            FocusVisualStyle = null, MinHeight = 0, MinWidth = 0,
            Template = PlainTemplate(radius ?? new CornerRadius(8), hover: true),
        };
        button.Click += (_, _) => click();
        return button;
    }

    private static ControlTemplate PlainTemplate(CornerRadius? radius = null, bool hover = false)
    {
        var template = new ControlTemplate(typeof(Button));
        var border = new FrameworkElementFactory(typeof(Border)) { Name = "Bd" };
        border.SetValue(Border.BackgroundProperty, new TemplateBindingExtension(Control.BackgroundProperty));
        border.SetValue(Border.CornerRadiusProperty, radius ?? new CornerRadius(0));
        border.SetValue(Border.PaddingProperty, new TemplateBindingExtension(Control.PaddingProperty));
        var presenter = new FrameworkElementFactory(typeof(ContentPresenter));
        presenter.SetValue(ContentPresenter.HorizontalAlignmentProperty, new TemplateBindingExtension(Control.HorizontalContentAlignmentProperty));
        presenter.SetValue(ContentPresenter.VerticalAlignmentProperty, VerticalAlignment.Center);
        border.AppendChild(presenter);
        template.VisualTree = border;
        var over = new Trigger { Property = UIElement.IsMouseOverProperty, Value = true };
        if (hover) over.Setters.Add(new Setter(Border.BackgroundProperty, Theme.Hover, "Bd"));
        else over.Setters.Add(new Setter(UIElement.OpacityProperty, 0.75, "Bd"));
        template.Triggers.Add(over);
        return template;
    }

    public static FrameworkElement Legend(params (string label, Brush color)[] items)
    {
        var panel = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        foreach (var (label, color) in items)
        {
            panel.Children.Add(new Border { Width = 9, Height = 9, CornerRadius = new CornerRadius(2), Background = color, Margin = new Thickness(panel.Children.Count == 0 ? 0 : 12, 0, 5, 0), VerticalAlignment = VerticalAlignment.Center });
            panel.Children.Add(Text(label, 11, color: Theme.Secondary));
        }
        return panel;
    }

    public static FrameworkElement Segmented(IList<string> labels, int selected, Action<int> onSelect, double fontSize = 12.5, int? badgeIndex = null, int badge = 0)
    {
        var panel = new StackPanel { Orientation = Orientation.Horizontal };
        for (var i = 0; i < labels.Count; i++)
        {
            var index = i;
            var isSelected = i == selected;
            var content = new StackPanel { Orientation = Orientation.Horizontal };
            content.Children.Add(Text(labels[i], fontSize, isSelected ? FontWeights.SemiBold : FontWeights.Normal, isSelected ? Theme.Primary : Theme.Secondary));
            if (badgeIndex == i && badge > 0)
            {
                content.Children.Add(new Border
                {
                    Background = Theme.Accent, CornerRadius = new CornerRadius(8), Padding = new Thickness(5, 0, 5, 1), Margin = new Thickness(6, 0, 0, 0),
                    Child = Text(badge.ToString(), 10.5, FontWeights.SemiBold, Brushes.White), VerticalAlignment = VerticalAlignment.Center,
                });
            }
            var item = new Border
            {
                Child = content, Padding = new Thickness(fontSize > 12.6 ? 16 : 12, 4, fontSize > 12.6 ? 16 : 12, 5), CornerRadius = new CornerRadius(6),
                Background = isSelected ? Theme.Selected : Brushes.Transparent,
                Effect = isSelected ? new DropShadowEffect { BlurRadius = 3, ShadowDepth = 1, Opacity = 0.14, Direction = 270 } : null,
                Cursor = Cursors.Hand,
            };
            item.MouseLeftButtonUp += (_, _) => onSelect(index);
            panel.Children.Add(item);
        }
        return new Border { Child = panel, Background = Theme.NavBar, CornerRadius = new CornerRadius(8), Padding = new Thickness(3), VerticalAlignment = VerticalAlignment.Center };
    }

    public static CheckBox Check(string label, bool value, Action<bool> changed, double size = 12.5)
    {
        var box = new CheckBox { Content = label, IsChecked = value, FontSize = size, VerticalAlignment = VerticalAlignment.Center, MinWidth = 0 };
        box.Checked += (_, _) => changed(true);
        box.Unchecked += (_, _) => changed(false);
        return box;
    }

    public static FrameworkElement Divider(double inset = 0) => new Border { Height = 1, Background = Theme.CardEdge, Margin = new Thickness(inset, 0, 0, 0) };

    public static TextBlock EmptyNote(string text) => Secondary(text, 13);

    /// <summary>Rows in one rounded box, with hairlines between them.</summary>
    public static Border GroupedList(IEnumerable<UIElement> rows, double inset = 52)
    {
        var panel = new StackPanel();
        foreach (var row in rows)
        {
            if (panel.Children.Count > 0) panel.Children.Add(Divider(inset));
            panel.Children.Add(row);
        }
        return new Border
        {
            Child = panel, CornerRadius = new CornerRadius(10), Background = Theme.Dark ? Theme.WithOpacity(Brushes.White, 0.03) : Theme.WithOpacity(Brushes.White, 0.7),
            BorderBrush = Theme.CardEdge, BorderThickness = new Thickness(1),
        };
    }

    public static FrameworkElement SectionTitle(string title, string? subtitle = null) =>
        V(2, Text(title, 15, FontWeights.SemiBold), subtitle == null ? null : Secondary(subtitle, 12, false));

    public static FrameworkElement Metric(string label, string value) =>
        V(2, Secondary(label, 11, false), Text(value, 16, FontWeights.SemiBold));

    public static FrameworkElement StatTile(string label, string value, string detail) =>
        Card(V(4, Secondary(label, 12, false), Title(value, 26), Secondary(detail, 12, false)), 16);

    public static FrameworkElement PageHeader(string title, string? tag, params object?[] trailing)
    {
        var items = new List<object?> { Title(title, 30), tag == null ? null : Pill(tag), Fill };
        items.AddRange(trailing);
        return H(12, items.ToArray());
    }

    public static FrameworkElement ErrorBanner(string message) => new Border
    {
        Background = Theme.WithOpacity(Theme.Red, 0.1), CornerRadius = new CornerRadius(8), Padding = new Thickness(12),
        Child = H(8, Icon(Glyph.Warning, 14, Theme.Red), Text(message, 12.5, color: Theme.Red, wrap: true)),
    };
}

/// <summary>App icons from the executables, cached.</summary>
public static class IconCache
{
    private static readonly Dictionary<string, ImageSource?> Cache = new(StringComparer.OrdinalIgnoreCase);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern uint PrivateExtractIcons(string file, int index, int cx, int cy, IntPtr[] icons, uint[] ids, uint count, uint flags);
    [DllImport("user32.dll")] private static extern bool DestroyIcon(IntPtr icon);

    public static ImageSource? Get(string? path)
    {
        if (string.IsNullOrEmpty(path)) return null;
        if (Cache.TryGetValue(path, out var cached)) return cached;
        ImageSource? image = null;
        try
        {
            if (File.Exists(path))
            {
                var icons = new IntPtr[1];
                var ids = new uint[1];
                // 128 px: sharp at 32 px on screen up to 400 % scaling (WPF scales it down smoothly).
                if (PrivateExtractIcons(path, 0, 128, 128, icons, ids, 1, 0) == 1 && icons[0] != IntPtr.Zero)
                {
                    image = Imaging.CreateBitmapSourceFromHIcon(icons[0], Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
                    image.Freeze();
                    DestroyIcon(icons[0]);
                }
            }
        }
        catch { image = null; }
        Cache[path] = image;
        return image;
    }
}

/// <summary>The battery outline with its fill level.</summary>
public sealed class BatteryGlyph : FrameworkElement
{
    public double Percent;
    public bool Charging, Plugged;

    protected override void OnRender(DrawingContext dc)
    {
        var w = ActualWidth; var h = ActualHeight;
        var capW = Math.Max(2, w * 0.06);
        var bodyW = w - capW - 1.5;
        var stroke = new Pen(Theme.Tertiary, Math.Max(1.5, h * 0.07));
        var body = new Rect(stroke.Thickness / 2, stroke.Thickness / 2, bodyW - stroke.Thickness, h - stroke.Thickness);
        dc.DrawRoundedRectangle(null, stroke, body, h * 0.22, h * 0.22);
        dc.DrawRoundedRectangle(Theme.Tertiary, null, new Rect(bodyW + 1, h * 0.32, capW, h * 0.36), capW / 2, capW / 2);
        var inset = Math.Max(2.5, h * 0.12);
        var inner = new Rect(body.X + inset, body.Y + inset, Math.Max(0, (body.Width - inset * 2) * Math.Clamp(Percent / 100, 0.04, 1)), body.Height - inset * 2);
        dc.DrawRoundedRectangle(Theme.Battery(Percent, Charging), null, inner, h * 0.12, h * 0.12);
        if (Charging || Plugged)
        {
            var text = new FormattedText(Glyph.Bolt, System.Globalization.CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
                new Typeface(Theme.Icons, FontStyles.Normal, FontWeights.Normal, FontStretches.Normal), h * 0.6, Theme.Dark ? Brushes.White : Brushes.Black, 1.0);
            dc.DrawText(text, new Point(body.X + body.Width / 2 - text.Width / 2, body.Y + body.Height / 2 - text.Height / 2));
        }
    }
}
