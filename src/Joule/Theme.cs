using System.Windows;
using System.Windows.Media;
using Microsoft.Win32;

namespace Joule;

/// <summary>Colours for light and dark mode, following the Windows app theme.</summary>
public static class Theme
{
    public static bool Dark { get; private set; }

    public static Brush Accent = null!, AccentSoft = null!, AccentFaint = null!;
    public static Brush Window = null!, Card = null!, CardEdge = null!, Track = null!, Hover = null!, Selected = null!, NavBar = null!;
    public static Brush Primary = null!, Secondary = null!, Tertiary = null!;
    public static Brush Apps = null!, Services = null!, Hardware = null!, Charging = null!, ChargingSoft = null!;
    public static Brush Indigo = null!, IndigoSoft = null!, Red = null!, Brown = null!, Purple = null!, Locked = null!;
    public static Brush[] Lanes = Array.Empty<Brush>();
    public static Color AccentColor;

    public static readonly FontFamily Font = new("Segoe UI Variable Text, Segoe UI");
    public static readonly FontFamily DisplayFont = new("Segoe UI Variable Display, Segoe UI");
    public static readonly FontFamily Icons = new("Segoe Fluent Icons, Segoe MDL2 Assets");

    public static event Action? Changed;

    static Theme() => Apply(ReadDarkMode());

    public static bool ReadDarkMode()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            return key?.GetValue("AppsUseLightTheme") is int v && v == 0;
        }
        catch { return false; }
    }

    public static bool TaskbarDark()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            return key?.GetValue("SystemUsesLightTheme") is not int v || v == 0;
        }
        catch { return true; }
    }

    public static void Refresh()
    {
        var dark = ReadDarkMode();
        if (dark == Dark && Accent != null) return;
        Apply(dark);
        Changed?.Invoke();
    }

    private static SolidColorBrush B(byte r, byte g, byte b, byte a = 255)
    {
        var brush = new SolidColorBrush(Color.FromArgb(a, r, g, b));
        brush.Freeze();
        return brush;
    }

    public static SolidColorBrush WithOpacity(Brush brush, double opacity)
    {
        var c = ((SolidColorBrush)brush).Color;
        var result = new SolidColorBrush(Color.FromArgb((byte)(c.A * opacity), c.R, c.G, c.B));
        result.Freeze();
        return result;
    }

    private static void Apply(bool dark)
    {
        Dark = dark;
        // Brand: warm amber, the colour of a glowing filament. Darker in light mode for contrast.
        AccentColor = dark ? Color.FromRgb(255, 158, 46) : Color.FromRgb(199, 105, 10);
        Accent = B(AccentColor.R, AccentColor.G, AccentColor.B);
        AccentSoft = WithOpacity(Accent, dark ? 0.16 : 0.10);
        AccentFaint = WithOpacity(Accent, 0.16);
        Window = dark ? B(32, 32, 32) : B(243, 243, 243);
        Card = dark ? B(43, 43, 43) : B(251, 251, 251);
        CardEdge = dark ? B(255, 255, 255, 18) : B(0, 0, 0, 15);
        NavBar = dark ? B(255, 255, 255, 14) : B(0, 0, 0, 14);
        Track = dark ? B(255, 255, 255, 26) : B(0, 0, 0, 18);
        Hover = dark ? B(255, 255, 255, 14) : B(0, 0, 0, 10);
        Selected = dark ? B(60, 60, 60) : B(255, 255, 255);
        Primary = dark ? B(242, 242, 242) : B(27, 27, 27);
        Secondary = dark ? B(170, 170, 170) : B(96, 96, 96);
        Tertiary = dark ? B(120, 120, 120) : B(150, 150, 150);
        Apps = B(92, 107, 245);
        Services = B(33, 173, 168);
        Hardware = dark ? B(110, 110, 110) : B(180, 180, 180);
        Charging = B(51, 199, 102);
        ChargingSoft = WithOpacity(Charging, 0.3);
        Indigo = dark ? B(94, 92, 230) : B(88, 86, 214);
        IndigoSoft = WithOpacity(Indigo, 0.3);
        Red = B(240, 92, 56);
        Brown = dark ? B(196, 150, 110) : B(150, 100, 60);
        Purple = B(175, 82, 222);
        Locked = Tertiary;
        Lanes = new Brush[] { B(46, 125, 245), dark ? B(173, 173, 184) : B(92, 92, 102), B(217, 120, 87) };
    }

    public static Brush Battery(double percent, bool charging = false)
    {
        if (charging) return Charging;
        if (percent <= 15) return B(245, 77, 66);
        if (percent <= 35) return B(250, 184, 26);
        return Charging;
    }
}

/// <summary>Segoe Fluent Icons code points.</summary>
public static class Glyph
{
    public const string Mouse = "", Keyboard = "", Headphones = "", Speaker = "", Controller = "",
        Phone = "", Pen = "", Watch = "", Bluetooth = "", Devices = "", Refresh = "", Laptop = "";

    public static string For(Joule.Core.DeviceKind kind) => kind switch
    {
        Joule.Core.DeviceKind.Mouse => Mouse,
        Joule.Core.DeviceKind.Keyboard => Keyboard,
        Joule.Core.DeviceKind.Headphones => Headphones,
        Joule.Core.DeviceKind.Speaker => Speaker,
        Joule.Core.DeviceKind.Controller => Controller,
        Joule.Core.DeviceKind.Phone => Phone,
        Joule.Core.DeviceKind.Pen => Pen,
        Joule.Core.DeviceKind.Watch => Watch,
        Joule.Core.DeviceKind.Ups => Battery,
        _ => Bluetooth,
    };

    public const string Settings = "", Moon = "", Sun = "", Bell = "", Display = "", Stack = "",
        Bolt = "", Hide = "", Battery = "", Heart = "", Message = "", Chevron = "", Power = "",
        Folder = "", Close = "", Ruler = "", Lock = "", Grid = "", Info = "", Shield = "",
        Warning = "", Check = "", Circle = "", Fire = "", Admin = "";
}
