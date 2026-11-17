using System.Globalization;

namespace Joule.Core;

public static class Format
{
    public const string Nbsp = " ";
    public static CultureInfo Culture = CultureInfo.CurrentCulture;

    public static string Number(double value, int digits = 1) => value.ToString("N" + digits, Culture);

    public static string Watts(double value) =>
        value < 10 ? $"{Number(value, 1)}{Nbsp}W" : $"{Number(value, 0)}{Nbsp}W";

    /// <summary>For live per-app values, where "0.0 W" would look like a broken measurement.</summary>
    public static string SmallWatts(double value) => value < 0.05 ? $"< {Number(0.1)}{Nbsp}W" : Watts(value);

    /// <summary>Chooses Wh or mWh so small numbers stay readable.</summary>
    public static string Energy(double joules)
    {
        var wh = joules / 3600;
        if (wh >= 10) return $"{Number(wh, 0)}{Nbsp}Wh";
        if (wh >= 0.1) return $"{Number(wh, 1)}{Nbsp}Wh";
        if (wh >= 0.001) return $"{Number(wh * 1000, 0)}{Nbsp}mWh";
        return $"< 1{Nbsp}mWh";
    }

    public static string Percent(double value, int digits = 0)
    {
        if (value > 0 && value < 1 && digits == 0) return $"< 1{Nbsp}%";
        return $"{Number(value, digits)}{Nbsp}%";
    }

    /// <summary>Percent of a battery charge with one decimal; tiny values read "&lt; 0.1 %".</summary>
    public static string BatteryShare(double value) =>
        value > 0 && value < 0.05 ? $"< {Number(0.1)}{Nbsp}%" : Percent(value, 1);

    public static string Share(double fraction)
    {
        if (fraction > 0 && fraction < 0.01) return $"< 1{Nbsp}%";
        return $"{Number(fraction * 100, 0)}{Nbsp}%";
    }

    /// <summary>"2 hr, 10 min"; short gives "2:10 h".</summary>
    public static string Duration(double seconds, bool @short = false)
    {
        var total = (long)Math.Max(0, Math.Round(seconds));
        long minutes = total / 60 % 60, hours = total / 3600;
        if (@short && total >= 3600 && hours < 48) return $"{hours}:{minutes:00}{Nbsp}h";
        if (total < 60) return $"{total}{Nbsp}sec";
        if (hours >= 48)
        {
            var days = (long)Math.Round(total / 86400.0);
            return $"{days}{Nbsp}days";
        }
        if (hours == 0) return $"{minutes}{Nbsp}min";
        if (minutes == 0) return $"{hours}{Nbsp}hr";
        return $"{hours}{Nbsp}hr, {minutes}{Nbsp}min";
    }

    public static string Time(DateTime date) => date.ToString("t", Culture);

    public static string DayTime(DateTime date)
    {
        var today = DateTime.Today;
        if (date.Date == today) return $"Today, {Time(date)}";
        if (date.Date == today.AddDays(-1)) return $"Yesterday, {Time(date)}";
        return $"{date.ToString("ddd, MMM d", Culture)} at {Time(date)}";
    }

    /// <summary>Weekday and day of month, e.g. "Mon 19".</summary>
    public static string Day(DateTime date) => date.ToString("ddd d", Culture);

    /// <summary>"A and B", "A, B and C".</summary>
    public static string List(IReadOnlyList<string> items) => items.Count switch
    {
        0 => "",
        1 => items[0],
        _ => string.Join(", ", items.Take(items.Count - 1)) + " and " + items[^1],
    };
}
