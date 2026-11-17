namespace Joule.Core;

public sealed class WarningSettings
{
    public bool AppHog { get; set; } = true;
    /// <summary>Average watts over AppWindow for an app in use.</summary>
    public double AppWatts { get; set; } = 6.0;
    public double AppWindow { get; set; } = 5.0 * 60;
    public bool BackgroundHog { get; set; } = true;
    public double BackgroundWatts { get; set; } = 1.5;
    public double BackgroundWindow { get; set; } = 10.0 * 60;
    public bool HighDrain { get; set; } = true;
    public double SystemWatts { get; set; } = 30.0;
    public double SystemWindow { get; set; } = 10.0 * 60;
    public bool SleepBlocker { get; set; } = true;
    public double BlockerMinutes { get; set; } = 30.0;
    public bool SleepDrain { get; set; } = true;
    public double SleepPercentPerHour { get; set; } = 1.0;
    public bool LowBattery { get; set; } = false;
    public double LowBatteryPercent { get; set; } = 15.0;
    /// <summary>Mouse, headphones, controllers … running low.</summary>
    public bool DeviceLow { get; set; } = true;
    public double DevicePercent { get; set; } = 20.0;
    /// <summary>Same warning for the same app at most once per this many seconds.</summary>
    public double Cooldown { get; set; } = 60.0 * 60;
    /// <summary>Only warn while running on battery.</summary>
    public bool BatteryOnly { get; set; } = true;
    public HashSet<string> Muted { get; set; } = new();

    public WarningSettings Clone()
    {
        var copy = (WarningSettings)MemberwiseClone();
        copy.Muted = new HashSet<string>(Muted);
        return copy;
    }
}

public sealed record Finding(EventKind Kind, string? AppKey, string Title, string Body, double Value);

/// <summary>What the recorder saw in one interval, input for the rules.</summary>
public sealed class Observation
{
    public DateTime Date;
    public double Duration;
    public PowerSource Source;
    public double Percent;
    public double SystemWatts;
    public double RemainingWh;
    public double FullWh;
    public List<AppUsage> Usages = new();
    public string? Frontmost;
    /// <summary>App identity → blocker description, for apps currently preventing sleep.</summary>
    public Dictionary<AppIdentity, string> Blockers = new();
}

/// <summary>Pure rule engine; the recorder feeds observations and posts the findings.</summary>
public sealed class WarningEngine
{
    public WarningSettings Settings;
    private readonly List<Observation> _history = new();
    private readonly Dictionary<string, DateTime> _lastFired = new();
    private Dictionary<string, DateTime> _blockedSince = new();
    private bool _lowBatteryArmed = true;

    public WarningEngine(WarningSettings? settings = null) { Settings = settings ?? new WarningSettings(); }

    public void Reset()
    {
        _history.Clear();
        _blockedSince.Clear();
    }

    private static string D(double s) => Format.Duration(s);

    public List<Finding> Evaluate(Observation observation)
    {
        _history.Add(observation);
        var keep = Math.Max(Math.Max(Settings.AppWindow, Settings.BackgroundWindow), Settings.SystemWindow) + 120;
        _history.RemoveAll(o => (observation.Date - o.Date).TotalSeconds > keep);

        if (observation.Source != PowerSource.Battery) _lowBatteryArmed = true;
        var findings = new List<Finding>();
        var now = observation.Date;
        var allowed = !Settings.BatteryOnly || observation.Source == PowerSource.Battery;
        double HourlyPercent(double watts) => observation.FullWh > 0 ? watts / observation.FullWh * 100 : 0;

        if (allowed && (Settings.AppHog || Settings.BackgroundHog))
        {
            foreach (var (app, stats) in AppStats(Math.Max(Settings.AppWindow, Settings.BackgroundWindow), now))
            {
                if (Settings.Muted.Contains(app.Key) || app.Category != AppCategory.App) continue;
                var fg = stats(Settings.AppWindow);
                if (Settings.AppHog && fg.Covered && fg.Steadiness >= 0.6 && fg.Watts >= Settings.AppWatts)
                {
                    Fire(findings, $"app:{app.Key}", now, new Finding(EventKind.AppHog, app.Key, $"{app.Name} is using a lot of energy",
                        $"Averaging {Format.Watts(fg.Watts)} for {D(Settings.AppWindow)} – about {Format.Percent(HourlyPercent(fg.Watts))} battery per hour.", fg.Watts));
                    continue;
                }
                var bg = stats(Settings.BackgroundWindow);
                if (Settings.BackgroundHog && bg.Covered && bg.Steadiness >= 0.6 && !bg.EverFront && bg.Watts >= Settings.BackgroundWatts)
                {
                    Fire(findings, $"bg:{app.Key}", now, new Finding(EventKind.BackgroundHog, app.Key, $"{app.Name} is working in the background",
                        $"You’re not using the app right now, but it has been drawing {Format.Watts(bg.Watts)} on average for {D(Settings.BackgroundWindow)}.", bg.Watts));
                }
            }
        }

        if (allowed && Settings.HighDrain && SystemAverage(Settings.SystemWindow, now) is double system && system >= Settings.SystemWatts)
        {
            var hours = observation.RemainingWh / system;
            var top = _history.SelectMany(o => o.Usages).Where(u => u.App.Category == AppCategory.App)
                .GroupBy(u => u.App.Key).Select(g => (name: g.First().App.Name, joules: g.Sum(u => u.Joules)))
                .OrderByDescending(x => x.joules).FirstOrDefault();
            var body = observation.Source == PowerSource.Battery
                ? $"Your PC is drawing {Format.Watts(system)} on average – at this rate the battery lasts about {D(hours * 3600)}."
                : $"Your PC is drawing {Format.Watts(system)} on average.";
            if (top.name != null) body += $" Biggest consumer: {top.name}.";
            Fire(findings, "system", now, new Finding(EventKind.HighDrain, null, "High power draw", body.Replace("..", "."), system));
        }

        if (Settings.SleepBlocker)
        {
            var current = observation.Blockers.Keys.Select(k => k.Key).ToHashSet();
            _blockedSince = _blockedSince.Where(kv => current.Contains(kv.Key)).ToDictionary(kv => kv.Key, kv => kv.Value);
            foreach (var (app, reason) in observation.Blockers)
            {
                if (Settings.Muted.Contains(app.Key)) continue;
                var since = _blockedSince.TryGetValue(app.Key, out var s) ? s : now;
                _blockedSince[app.Key] = since;
                var minutes = (now - since).TotalMinutes;
                if (allowed && minutes >= Settings.BlockerMinutes)
                {
                    var detail = string.IsNullOrEmpty(reason) ? "" : $" (“{reason}”)";
                    Fire(findings, $"block:{app.Key}", now, new Finding(EventKind.SleepBlocker, app.Key, $"{app.Name} is keeping your PC awake",
                        $"For {D(minutes * 60)}, {app.Name} has been preventing sleep{detail}.", minutes));
                }
            }
        }

        if (Settings.LowBattery && observation.Source == PowerSource.Battery && _lowBatteryArmed && observation.Percent <= Settings.LowBatteryPercent)
        {
            _lowBatteryArmed = false;
            var watts = SystemAverage(600, now) ?? observation.SystemWatts;
            var rest = watts > 0 ? $" At the current rate, about {D(observation.RemainingWh / watts * 3600)} left." : "";
            findings.Add(new Finding(EventKind.LowBattery, null, $"Battery at {Format.Percent(observation.Percent)}",
                ("Time to get the charger." + rest).Replace("..", "."), observation.Percent));
        }
        return findings;
    }

    /// <summary>Checked once after waking up.</summary>
    public Finding? Evaluate(SleepRecord sleep)
    {
        if (!(Settings.SleepDrain && sleep.OnBattery && sleep.Hours >= 1 && sleep.PercentLost >= 3 && sleep.PercentPerHour >= Settings.SleepPercentPerHour)) return null;
        return new Finding(EventKind.SleepDrain, null, "Battery drained during sleep",
            $"In {D(sleep.Hours * 3600)} of sleep, {Format.Percent(sleep.PercentLost)} was lost ({Format.Percent(sleep.PercentPerHour, 1)} per hour). Usually it’s well under {Format.Percent(0.5, 1)} per hour.",
            sleep.PercentPerHour);
    }

    private void Fire(List<Finding> findings, string key, DateTime now, Finding finding)
    {
        if (_lastFired.TryGetValue(key, out var last) && (now - last).TotalSeconds < Settings.Cooldown) return;
        _lastFired[key] = now;
        findings.Add(finding);
    }

    private readonly record struct Window(double Watts, bool Covered, bool EverFront, double Steadiness);

    /// <summary>Per app: a function returning average watts over the trailing window.</summary>
    private List<(AppIdentity, Func<double, Window>)> AppStats(double window, DateTime now)
    {
        var apps = new Dictionary<string, AppIdentity>();
        foreach (var o in _history.Where(o => (now - o.Date).TotalSeconds <= window))
            foreach (var u in o.Usages.Where(u => u.App.Category == AppCategory.App)) apps[u.App.Key] = u.App;
        return apps.Values.Select(app => (app, (Func<double, Window>)(length =>
        {
            var recent = _history.Where(o => (now - o.Date).TotalSeconds < length + 1).ToList();
            var seconds = recent.Sum(o => o.Duration);
            var onBattery = !Settings.BatteryOnly || recent.All(o => o.Source == PowerSource.Battery);
            double JoulesIn(Observation o) => o.Usages.Where(u => u.App.Key == app.Key).Sum(u => u.Joules);
            var joules = recent.Sum(JoulesIn);
            var front = recent.Any(o => o.Frontmost == app.Key);
            var average = seconds > 0 ? joules / seconds : 0;
            var steady = recent.Count(o => o.Duration > 0 && JoulesIn(o) / o.Duration >= average / 2);
            // The window must be mostly observed and the draw sustained, so one burst does not trigger.
            return new Window(average, onBattery && seconds >= length * 0.8, front, recent.Count == 0 ? 0 : (double)steady / recent.Count);
        }))).ToList();
    }

    private double? SystemAverage(double window, DateTime now)
    {
        var recent = _history.Where(o => (now - o.Date).TotalSeconds < window + 1).ToList();
        var seconds = recent.Sum(o => o.Duration);
        if (seconds < window * 0.8) return null;
        if (Settings.BatteryOnly && !recent.All(o => o.Source == PowerSource.Battery)) return null;
        return recent.Sum(o => o.SystemWatts * o.Duration) / seconds;
    }
}
