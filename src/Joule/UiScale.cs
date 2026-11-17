using System.Windows;
using System.Windows.Media;

namespace Joule;

/// <summary>
/// Joule follows Windows' display scaling exactly (per-monitor DPI aware): 100 % zoom is true 100 %, 150 % is 150 %.
/// The factor here stays 1 and only changes for layout checks (--scale); the helpers keep windows within the screen at any DPI.
/// </summary>
public static class UiScale
{
    public static double Current { get; private set; } = 1;

    public static event Action? Changed;

    public static void Apply(double factor)
    {
        if (factor <= 0 || Math.Abs(factor - Current) < 0.001) return;
        Current = factor;
        Changed?.Invoke();
    }

    public static Transform Transform() => Math.Abs(Current - 1) < 0.001 ? System.Windows.Media.Transform.Identity : new ScaleTransform(Current, Current);

    /// <summary>Sizes a window, never larger than the screen's work area (e.g. a 4K screen at 300 % has about 1280 × 700 to work with).</summary>
    public static void Size(Window window, double width, double height, double minWidth, double minHeight)
    {
        var area = SystemParameters.WorkArea;
        var maxW = area.Width * 0.96; var maxH = area.Height * 0.94;
        window.MinWidth = Math.Min(minWidth * Current, maxW);
        window.MinHeight = Math.Min(minHeight * Current, maxH);
        window.Width = Math.Clamp(width * Current, window.MinWidth, maxW);
        window.Height = Math.Clamp(height * Current, window.MinHeight, maxH);
    }
}
