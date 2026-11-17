using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Runtime.InteropServices;
using Joule.Core;
using Forms = System.Windows.Forms;

namespace Joule;

/// <summary>The notification-area icon: shows watts or percent, opens the panel, posts notifications.</summary>
public sealed class Tray : IDisposable
{
    private readonly Forms.NotifyIcon _icon;
    private IntPtr _handle;
    private string _rendered = "";
    private Monitor M => Monitor.Shared;
    public Action? Clicked;
    public Action? BalloonClicked;

    [DllImport("user32.dll")] private static extern bool DestroyIcon(IntPtr handle);

    public Tray()
    {
        _icon = new Forms.NotifyIcon { Text = "Joule", Visible = true };
        var menu = new Forms.ContextMenuStrip();
        menu.Items.Add("Open Joule", null, (_, _) => App.Current.ShowMain(null));
        menu.Items.Add("Settings", null, (_, _) => App.Current.ShowSettings(0));
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add("Quit Joule", null, (_, _) => App.Current.Quit());
        _icon.ContextMenuStrip = menu;
        _icon.MouseClick += (_, e) => { if (e.Button == Forms.MouseButtons.Left) Clicked?.Invoke(); };
        _icon.BalloonTipClicked += (_, _) => BalloonClicked?.Invoke();
        Update();
    }

    public void Notify(string title, string body) => _icon.ShowBalloonTip(10000, title, body, Forms.ToolTipIcon.None);

    public void Update()
    {
        var power = M.Power;
        var unread = M.UnreadAlerts;
        var tip = power == null ? "Joule" : $"Joule – {(power.Estimated ? "≈ " : "")}{Format.Watts(power.SystemWatts)} · {Format.Percent(power.Percent)} · {power.ShortStateText}";
        if (unread > 0) tip += $" · {unread} new alert{(unread == 1 ? "" : "s")}";
        _icon.Text = tip.Length > 127 ? tip[..127] : tip;

        var text = M.Settings.Tray switch
        {
            TrayStyle.Watts when power != null => Math.Round(power.SystemWatts).ToString("0"),
            TrayStyle.Percent when power != null => Math.Round(power.Percent).ToString("0"),
            _ => "",
        };
        // Battery style: the average of this PC and the connected devices; red low-battery icon while any of them is low.
        var lowNames = M.LowDeviceNames;
        if (M.PcLow) lowNames.Insert(0, "This PC");
        var average = M.AverageLevel;
        if (M.Settings.Tray == TrayStyle.Battery)
        {
            var parts = new List<string> { average is double a ? $"Joule – average battery {Format.Percent(a)}" : "Joule" };
            if (lowNames.Count > 0) parts.Add($"Low: {string.Join(", ", lowNames)}");
            if (power != null) parts.Add($"PC {Format.Percent(power.Percent)}");
            parts.AddRange(M.Devices.Where(d => d.Percent != null).Select(d => $"{d.Name} {d.LevelText}"));
            if (unread > 0) parts.Add($"{unread} new alert{(unread == 1 ? "" : "s")}");
            tip = string.Join(" · ", parts);
            _icon.Text = tip.Length > 127 ? tip[..126] + "…" : tip;
        }
        var level = Math.Round(average ?? 0);
        var key = $"{text}|{unread > 0}|{Theme.TaskbarDark()}|{M.Settings.Tray}|{IconSize()}|{level}|{lowNames.Count > 0}|{power?.Source}";
        if (key == _rendered) return;
        _rendered = key;
        var size = IconSize();
        using var bitmap = M.Settings.Tray == TrayStyle.Battery
            ? RenderBattery(average, lowNames.Count > 0, power?.Source == PowerSource.Charging, unread > 0, size, Theme.TaskbarDark())
            : Render(text, unread > 0, size, Theme.TaskbarDark());
        var handle = bitmap.GetHicon();
        _icon.Icon = Icon.FromHandle(handle);
        if (_handle != IntPtr.Zero) DestroyIcon(_handle);
        _handle = handle;
    }

    [DllImport("user32.dll")] private static extern int GetSystemMetricsForDpi(int index, uint dpi);
    [DllImport("user32.dll")] private static extern uint GetDpiForSystem();

    /// <summary>The notification area's icon size in pixels at the taskbar's DPI (16 at 100 %, 20 at 125 %, 24 at 150 % …).</summary>
    public static int IconSize()
    {
        try
        {
            var size = GetSystemMetricsForDpi(49 /* SM_CXSMICON */, GetDpiForSystem());
            if (size >= 16) return size;
        }
        catch { }
        return Math.Max(16, Forms.SystemInformation.SmallIconSize.Width);
    }

    /// <summary>
    /// A battery like the one on the Now page: outline, a fill as wide as the level, green / amber / red by level.
    /// Low (any device below its alert level, until charged): red outline, red sliver and an exclamation mark.
    /// It sits in the lower part of the square so the unread-alerts dot (top right) never touches it.
    /// </summary>
    public static Bitmap RenderBattery(double? percent, bool low, bool charging, bool badge, int size, bool darkTaskbar)
    {
        var bitmap = new Bitmap(size, size, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        using var g = Graphics.FromImage(bitmap);
        g.Clear(Color.Transparent);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.PixelOffsetMode = PixelOffsetMode.HighQuality;
        var ink = darkTaskbar ? Color.White : Color.FromArgb(28, 28, 28);
        var red = Color.FromArgb(240, 72, 56);
        var amber = Color.FromArgb(250, 184, 26);
        var green = Color.FromArgb(52, 199, 89);
        var level = Math.Clamp(percent ?? 0, 0, 100);

        // Whole pixels so the outline stays crisp at 16 px.
        var stroke = Math.Max(1, (int)Math.Round(size / 14.0));
        var nubW = Math.Max(1, (int)Math.Round(size / 12.0));
        var bodyW = size - nubW - 1;
        var bodyH = (int)Math.Round(size * 0.56);
        var top = size - bodyH - (int)Math.Round(size * 0.1);
        var body = new RectangleF(stroke / 2f, top + stroke / 2f, bodyW - stroke, bodyH - stroke);
        var radius = Math.Max(1.5f, size / 9f);
        using (var path = Rounded(body, radius))
        using (var pen = new Pen(low ? red : ink, stroke))
            g.DrawPath(pen, path);
        var nubH = (int)Math.Round(bodyH * 0.42);
        using (var nub = new SolidBrush(low ? red : ink))
            g.FillRectangle(nub, bodyW, top + (bodyH - nubH) / 2, nubW, nubH);

        var gap = Math.Max(1, (int)Math.Round(size / 16.0));
        var inner = new RectangleF(stroke + gap, top + stroke + gap, bodyW - 2 * (stroke + gap), bodyH - 2 * (stroke + gap));
        if (percent != null && inner.Width > 0 && inner.Height > 0)
        {
            var fill = low ? red : charging ? green : level <= 15 ? red : level <= 35 ? amber : green;
            var width = low ? Math.Max(1.5f, inner.Width * 0.2f) : Math.Max(1.5f, inner.Width * (float)(level / 100));
            using var brush = new SolidBrush(fill);
            using var path = Rounded(new RectangleF(inner.X, inner.Y, width, inner.Height), Math.Max(0.5f, radius - stroke - gap));
            g.FillPath(brush, path);
        }
        // Below 24 px the mark and the sliver blur into two bars; there the red outline and sliver say it alone.
        if (low && size >= 24 && inner.Height >= 3)
        {
            // "!" in the empty part of the body, clear of the red sliver.
            var x = inner.X + inner.Width * 0.62f;
            var w = Math.Max(1.5f, size / 11f);
            var dotH = Math.Max(1.5f, inner.Height * 0.22f);
            using var mark = new SolidBrush(red);
            g.FillRectangle(mark, x - w / 2, inner.Y, w, inner.Height - dotH - Math.Max(1, inner.Height * 0.12f));
            g.FillRectangle(mark, x - w / 2, inner.Bottom - dotH, w, dotH);
        }
        if (badge)
        {
            var d = (float)Math.Round(size * 0.3);
            using var dot = new SolidBrush(Color.FromArgb(240, 92, 56));
            g.FillEllipse(dot, size - d, 0, d, d);
        }
        return bitmap;
    }

    private static GraphicsPath Rounded(RectangleF r, float radius)
    {
        var path = new GraphicsPath();
        var d = Math.Min(radius * 2, Math.Min(r.Width, r.Height));
        if (d <= 0.5f) { path.AddRectangle(r); return path; }
        path.AddArc(r.X, r.Y, d, d, 180, 90);
        path.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        path.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        path.CloseFigure();
        return path;
    }

    /// <summary>
    /// Digits as large as the square allows, above a thin amber underline that marks the icon as Joule's. The glyphs are
    /// laid out from their actual outline, so they never touch the underline or the edge. Unread alerts turn the underline red
    /// (a dot would cover the digits); in icon-only mode a dot with a cut-out ring marks them.
    /// </summary>
    public static Bitmap Render(string text, bool badge, int size, bool darkTaskbar)
    {
        var bitmap = new Bitmap(size, size, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        using var g = Graphics.FromImage(bitmap);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.PixelOffsetMode = PixelOffsetMode.HighQuality;
        g.InterpolationMode = InterpolationMode.HighQualityBicubic;
        g.Clear(Color.Transparent);
        var red = Color.FromArgb(240, 92, 56);
        if (text.Length == 0)
        {
            using (var stream = System.Windows.Application.GetResourceStream(new Uri("pack://application:,,,/Assets/Joule.ico"))?.Stream)
            {
                if (stream != null)
                {
                    // The .ico has hand-sized frames (16, 20, 24, 32 …): use the exact one instead of scaling the large image.
                    using var icon = new Icon(stream, new Size(size, size));
                    using var image = icon.ToBitmap();
                    g.DrawImage(image, new Rectangle(0, 0, size, size));
                }
            }
            if (badge)
            {
                var d = (float)Math.Round(size * 0.4);
                g.CompositingMode = CompositingMode.SourceCopy;
                using (var clear = new SolidBrush(Color.Transparent)) g.FillEllipse(clear, size - d - 1.5f, -1.5f, d + 3, d + 3);
                g.CompositingMode = CompositingMode.SourceOver;
                using var dot = new SolidBrush(red);
                g.FillEllipse(dot, size - d, 0, d, d);
            }
            return bitmap;
        }

        var underline = Math.Max(1, (int)Math.Round(size / 12.0));
        var gap = Math.Max(1, (int)Math.Round(size / 14.0));
        var box = new RectangleF(0, 0, size, size - underline - gap);
        using (var path = new GraphicsPath())
        using (var family = new FontFamily("Segoe UI"))
        {
            path.AddString(text, family, (int)FontStyle.Bold, 100, PointF.Empty, StringFormat.GenericTypographic);
            var bounds = path.GetBounds();
            if (bounds.Width > 0 && bounds.Height > 0)
            {
                // Same scale both ways (no squashed digits); centred horizontally, sitting on the box's bottom.
                var scale = Math.Min(box.Width / bounds.Width, box.Height / bounds.Height);
                var width = bounds.Width * scale;
                var height = bounds.Height * scale;
                using var matrix = new Matrix();
                matrix.Translate(box.Left + (box.Width - width) / 2, box.Bottom - height);
                matrix.Scale(scale, scale);
                matrix.Translate(-bounds.Left, -bounds.Top);
                path.Transform(matrix);
                using var brush = new SolidBrush(darkTaskbar ? Color.White : Color.FromArgb(28, 28, 28));
                g.FillPath(brush, path);
            }
        }
        g.SmoothingMode = SmoothingMode.None;
        g.PixelOffsetMode = PixelOffsetMode.None;
        using var line = new SolidBrush(badge ? red : Color.FromArgb(255, 158, 46));
        var lineWidth = badge ? size : (int)Math.Round(size * 0.625);
        g.FillRectangle(line, (size - lineWidth) / 2, size - underline, lineWidth, underline);
        return bitmap;
    }

    public void Dispose()
    {
        _icon.Visible = false;
        _icon.Dispose();
        if (_handle != IntPtr.Zero) DestroyIcon(_handle);
    }
}
