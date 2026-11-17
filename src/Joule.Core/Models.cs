namespace Joule.Core;

public static class Time
{
    public static double Unix(DateTime date) => (date.ToUniversalTime() - DateTime.UnixEpoch).TotalSeconds;
    public static DateTime FromUnix(double seconds) => DateTime.UnixEpoch.AddSeconds(seconds).ToLocalTime();
    public static DateTime Max(DateTime a, DateTime b) => a > b ? a : b;
    public static DateTime Min(DateTime a, DateTime b) => a < b ? a : b;
}

public readonly record struct DateRange(DateTime Start, DateTime End)
{
    public double Duration => (End - Start).TotalSeconds;
}

public enum PowerSource { Battery, Charging, Adapter }

public static class PowerSourceExtensions
{
    public static string Raw(this PowerSource source) => source switch
    {
        PowerSource.Battery => "battery",
        PowerSource.Charging => "charging",
        _ => "adapter",
    };

    public static PowerSource ParseSource(string? raw) => raw switch
    {
        "battery" => PowerSource.Battery,
        "charging" => PowerSource.Charging,
        _ => PowerSource.Adapter,
    };

    public static bool OnBattery(this PowerSource source) => source == PowerSource.Battery;
}

public enum AppCategory { App, System, Tool }

public static class AppCategoryExtensions
{
    public static string Raw(this AppCategory c) => c switch { AppCategory.App => "app", AppCategory.System => "system", _ => "tool" };
    public static AppCategory ParseCategory(string? raw) => raw switch { "app" => AppCategory.App, "system" => AppCategory.System, _ => AppCategory.Tool };
    public static string Label(this AppCategory c) => c switch { AppCategory.App => "App", AppCategory.System => "Windows service", _ => "Command-line tool" };
}

/// <summary>An app group: every process that belongs to one installed program.</summary>
public sealed record AppIdentity(string Key, string Name, string? BundlePath, AppCategory Category)
{
    public bool Equals(AppIdentity? other) => other is not null && other.Key == Key;
    public override int GetHashCode() => Key.GetHashCode();
}

/// <summary>Energy one app group used during one interval.</summary>
public sealed class AppUsage
{
    public required AppIdentity App;
    public double Joules;
    public double CpuSeconds;
    public double GpuSeconds;
    /// <summary>Context switches, the Windows stand-in for idle wakeups.</summary>
    public double IdleWakeups;
    public Dictionary<string, double> Members = new();
    /// <summary>Part of Joules estimated from CPU time (no energy meter available).</summary>
    public double EstimatedJoules;

    public AppUsage Clone() => new()
    {
        App = App, Joules = Joules, CpuSeconds = CpuSeconds, GpuSeconds = GpuSeconds, IdleWakeups = IdleWakeups,
        Members = new Dictionary<string, double>(Members), EstimatedJoules = EstimatedJoules,
    };
}

public sealed class Interval
{
    public DateTime Start;
    public DateTime End;
    public List<AppUsage> Usages = new();
    /// <summary>CPU package energy over the interval, if the energy meter is readable.</summary>
    public double? PackageJoules;
    public double Duration => (End - Start).TotalSeconds;
    public double MeasuredJoules => Usages.Sum(u => u.Joules);
}

public sealed class PowerReading
{
    public DateTime Date;
    public double Percent;
    public PowerSource Source;
    /// <summary>Whole-machine draw in watts (display, CPU, radios …).</summary>
    public double SystemWatts;
    /// <summary>True when SystemWatts is a model estimate (on the adapter the battery cannot see the load).</summary>
    public bool Estimated;
    /// <summary>Positive while charging, negative while discharging.</summary>
    public double BatteryWatts;
    public double RemainingWh;
    public double FullWh;
    public double DesignWh;
    public int CycleCount;
    public double Voltage;
    public double? TemperatureC;
    public int? MinutesToEmpty;
    public int? MinutesToFull;
    public double HealthPercent => DesignWh > 0 ? Math.Min(100, FullWh / DesignWh * 100) : 100;

    public PowerReading Clone() => (PowerReading)MemberwiseClone();

    public string StateText => Source switch
    {
        PowerSource.Battery => MinutesToEmpty is int m ? $"On battery · about {Format.Duration(m * 60)} left" : "On battery",
        PowerSource.Charging => MinutesToFull is int f ? $"Charging · full in about {Format.Duration(f * 60)}" : "Charging",
        _ => "On power adapter · not charging",
    };

    public string ShortStateText => Source switch
    {
        PowerSource.Battery => MinutesToEmpty is int m ? $"Battery · {Format.Duration(m * 60, true)} left" : "On battery",
        PowerSource.Charging => MinutesToFull is int f ? $"Charging · full in {Format.Duration(f * 60, true)}" : "Charging",
        _ => "On power adapter",
    };
}

public sealed record SleepBlocker(int Pid, string ProcessName, string Path, string Reason, string Type);

/// <summary>One recorded interval of whole-machine power.</summary>
public sealed record PowerSample(DateTime Start, DateTime End, PowerSource Source, double Percent, double Watts,
    double RemainingWh, double FullWh, double? Brightness, string? Frontmost)
{
    public double Duration => (End - Start).TotalSeconds;
}

public sealed record SleepRecord(DateTime Start, DateTime End, double PercentBefore, double PercentAfter, bool OnBattery)
{
    public double Hours => (End - Start).TotalSeconds / 3600;
    public double PercentLost => Math.Max(0, PercentBefore - PercentAfter);
    public double PercentPerHour => Hours > 0 ? PercentLost / Hours : 0;
}

public enum EventKind { AppHog, BackgroundHog, SleepBlocker, HighDrain, SleepDrain, LowBattery, Health, DeviceLow }

public static class EventKindExtensions
{
    public static string Raw(this EventKind k) => k switch
    {
        EventKind.AppHog => "appHog",
        EventKind.BackgroundHog => "backgroundHog",
        EventKind.SleepBlocker => "sleepBlocker",
        EventKind.HighDrain => "highDrain",
        EventKind.SleepDrain => "sleepDrain",
        EventKind.LowBattery => "lowBattery",
        EventKind.DeviceLow => "deviceLow",
        _ => "health",
    };

    public static EventKind ParseKind(string raw) => raw switch
    {
        "backgroundHog" => EventKind.BackgroundHog,
        "sleepBlocker" => EventKind.SleepBlocker,
        "highDrain" => EventKind.HighDrain,
        "sleepDrain" => EventKind.SleepDrain,
        "lowBattery" => EventKind.LowBattery,
        "health" => EventKind.Health,
        "deviceLow" => EventKind.DeviceLow,
        _ => EventKind.AppHog,
    };
}

public sealed record Notice(long Id, DateTime Date, EventKind Kind, string? AppKey, string Title, string Body, double Value, bool Read);
