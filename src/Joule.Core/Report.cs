namespace Joule.Core;

public enum Period { Hour, Today, Day, Week, Month }

public static class PeriodExtensions
{
    public static readonly Period[] All = { Period.Hour, Period.Today, Period.Day, Period.Week, Period.Month };

    public static string Title(this Period p) => p switch
    {
        Period.Hour => "1 hr",
        Period.Today => "Today",
        Period.Day => "24 hrs",
        Period.Week => "7 days",
        _ => "30 days",
    };

    public static DateRange Range(this Period p, DateTime? now = null)
    {
        var n = now ?? DateTime.Now;
        return p switch
        {
            Period.Hour => new(n.AddHours(-1), n),
            Period.Today => new(n.Date, n),
            Period.Day => new(n.AddDays(-1), n),
            Period.Week => new(n.AddDays(-6).Date, n),
            _ => new(n.AddDays(-29).Date, n),
        };
    }

    /// <summary>Chart bucket size in seconds.</summary>
    public static double Bucket(this Period p) => p switch
    {
        Period.Hour => 120,
        Period.Today or Period.Day => 900,
        Period.Week => 3 * 3600,
        _ => 86400,
    };

    public static string BucketName(this Period p) => p switch
    {
        Period.Hour => "2 minutes",
        Period.Today or Period.Day => "quarter hour",
        Period.Week => "3 hours",
        _ => "day",
    };
}

public struct Breakdown
{
    /// <summary>Measured CPU energy of regular apps.</summary>
    public double Apps;
    /// <summary>CPU energy of Windows services and command-line tools.</summary>
    public double Services;
    /// <summary>System draw not explained by processes: display, GPU, memory, radios, the rest of the chip.</summary>
    public double Hardware;
    public readonly double Total => Apps + Services + Hardware;
}

public sealed class AppTotal
{
    public required AppIdentity App;
    public double Joules, CpuSeconds, GpuSeconds, IdleWakeups, ForegroundSeconds, ActiveSeconds, EstimatedJoules;
    public string Id => App.Key;
    public bool IsEstimated => EstimatedJoules > Joules * 0.5;
    public double WattHours => Joules / 3600;
    public double AverageWatts => ActiveSeconds > 0 ? Joules / ActiveSeconds : 0;
    public double BackgroundShare => ActiveSeconds > 0 ? Math.Max(0, 1 - ForegroundSeconds / ActiveSeconds) : 1;
    public double WakeupsPerSecond => ActiveSeconds > 0 ? IdleWakeups / ActiveSeconds : 0;
}

public sealed class PowerBucket
{
    public DateTime Start;
    public double Seconds;
    public Breakdown Breakdown;
    public double BatterySeconds;
    public double Watts => Seconds > 0 ? Breakdown.Total / Seconds : 0;
    public double AppsWatts => Seconds > 0 ? Breakdown.Apps / Seconds : 0;
    public double ServicesWatts => Seconds > 0 ? Breakdown.Services / Seconds : 0;
    public double HardwareWatts => Seconds > 0 ? Breakdown.Hardware / Seconds : 0;
}

public sealed record BatteryPoint(DateTime Date, double Percent, PowerSource Source, double Watts);

public sealed class Report
{
    public DateRange Range;
    public bool BatteryOnly;
    public Breakdown Breakdown;
    public List<AppTotal> Apps = new();
    public List<PowerBucket> Buckets = new();
    public List<BatteryPoint> Battery = new();
    public List<SleepRecord> Sleeps = new();
    public double RecordedSeconds;
    public double BatterySeconds;
    /// <summary>Sum of all percentage drops while on battery and awake.</summary>
    public double PercentUsed;
    public double FullWh;

    public double TotalJoules => Breakdown.Total;
    public double AverageWatts => RecordedSeconds > 0 ? TotalJoules / RecordedSeconds : 0;
    public bool IsEmpty => RecordedSeconds < 1;
    /// <summary>Joules expressed as percent of a full battery.</summary>
    public double BatteryPercent(double joules) => FullWh > 0 ? joules / (FullWh * 3600) * 100 : 0;
    public double Share(double joules) => TotalJoules > 0 ? joules / TotalJoules : 0;
}

public sealed class DischargeSession
{
    public DateTime Start, End;
    public double PercentStart, PercentEnd, Joules, AwakeSeconds;
    public bool Ongoing;
    /// <summary>The battery was charged without Joule seeing the adapter (charged while asleep).</summary>
    public bool ChargedUnseen;
    public double PercentUsed => Math.Max(0, PercentStart - PercentEnd);
    public double AverageWatts => AwakeSeconds > 0 ? Joules / AwakeSeconds : 0;
}

public sealed record MemberShare(string Name, double Joules);

public enum ShareKind { App, Service, Others, Hardware, Sleep }

/// <summary>One line of "where did the X % go".</summary>
public sealed class ShareEntry
{
    public ShareKind Kind;
    public string Name = "";
    public AppIdentity? App;
    public double Joules;
    /// <summary>Percentage points of battery this entry accounts for.</summary>
    public double Percent;
    public bool Estimated;
    public string Id => App?.Key ?? Name;
}

/// <summary>Splits the battery percentage lost in one session over what used it.</summary>
public sealed class SessionBreakdown
{
    public required DischargeSession Session;
    public List<ShareEntry> Entries = new();
    public double SleepHours;
    /// <summary>Joule started recording during this session, so its true start is unknown.</summary>
    public bool StartsAtRecording;
    /// <summary>Awake time on battery for which no measurement exists (Joule was not running).</summary>
    public double UnrecordedSeconds;
    /// <summary>Awake on battery with the screen locked.</summary>
    public double LockedSeconds;
    public double LockedPercent;
    public double? AverageBrightness;
    public double PercentUsed => Session.PercentUsed;
}

public sealed partial class Store
{
    private static bool UseMinutes(DateTime start) => start > DateTime.Now.AddSeconds(-7 * 86400 + 3600);

    public Report Report(DateRange range, double bucket, bool batteryOnly) => Locked(() =>
    {
        var report = new Report { Range = range, BatteryOnly = batteryOnly };
        var apps = Apps();
        double from = Time.Unix(range.Start), to = Time.Unix(range.End);
        var sourceFilter = batteryOnly ? "AND source='battery'" : "";

        var buckets = new Dictionary<long, PowerBucket>();
        long BucketIndex(double t) => (long)Math.Floor((t - from) / bucket);
        PowerBucket Touch(long i)
        {
            if (!buckets.TryGetValue(i, out var b)) buckets[i] = b = new PowerBucket { Start = Time.FromUnix(from + i * bucket) };
            return b;
        }

        double? lastPercent = null;
        PowerSource? lastSource = null;
        _db.Query("SELECT t, dt, source, percent, watts, full_wh, measured_j FROM power WHERE t > ? AND t <= ? ORDER BY t", row =>
        {
            double t = row.Double(0), dt = row.Double(1);
            var source = PowerSourceExtensions.ParseSource(row.String(2));
            double percent = row.Double(3), watts = row.Double(4);
            report.Battery.Add(new BatteryPoint(Time.FromUnix(t), percent, source, watts));
            if (row.OptionalDouble(5) is double full && full > 0) report.FullWh = full;
            if (source == PowerSource.Battery && lastSource == PowerSource.Battery && lastPercent is double lp && lp > percent)
                report.PercentUsed += lp - percent;
            lastPercent = percent;
            lastSource = source;
            if (batteryOnly && source != PowerSource.Battery) return;
            var joules = watts * dt;
            var hardware = Math.Max(0, joules - (row.OptionalDouble(6) ?? 0));
            report.RecordedSeconds += dt;
            if (source == PowerSource.Battery) report.BatterySeconds += dt;
            report.Breakdown.Hardware += hardware;
            var b = Touch(BucketIndex(t - dt / 2));
            b.Seconds += dt;
            b.Breakdown.Hardware += hardware;
            if (source == PowerSource.Battery) b.BatterySeconds += dt;
        }, from, to);

        var totals = new Dictionary<string, AppTotal>();
        void Add(string key, double t, double joules, double cpu, double gpu, double wakeups, double fg, double seconds, double estimated)
        {
            if (!apps.TryGetValue(key, out var app))
            {
                var prefix = key.Split(':')[0];
                app = new AppIdentity(key, key, null, AppCategoryExtensions.ParseCategory(prefix));
            }
            if (!totals.TryGetValue(key, out var total)) totals[key] = total = new AppTotal { App = app };
            total.Joules += joules;
            total.CpuSeconds += cpu;
            total.GpuSeconds += gpu;
            total.IdleWakeups += wakeups;
            total.ForegroundSeconds += fg;
            total.ActiveSeconds += seconds;
            total.EstimatedJoules += estimated;
            var b = Touch(BucketIndex(t));
            if (app.Category == AppCategory.App) { b.Breakdown.Apps += joules; report.Breakdown.Apps += joules; }
            else { b.Breakdown.Services += joules; report.Breakdown.Services += joules; }
        }
        if (UseMinutes(range.Start))
        {
            _db.Query($"SELECT t, app, joules, cpu, gpu, wakeups, fg, dt, estimated FROM usage WHERE t > ? AND t <= ? {sourceFilter}", row =>
            {
                var dt = row.Double(7);
                Add(row.String(1), row.Double(0) - dt / 2, row.Double(2), row.Double(3), row.Double(4), row.Double(5), row.Int(6) == 1 ? dt : 0, dt, row.Double(8));
            }, from, to);
        }
        else
        {
            _db.Query($"SELECT hour, app, joules, cpu, gpu, wakeups, fg_seconds, seconds, estimated FROM usage_hourly WHERE hour >= ? AND hour < ? {sourceFilter}", row =>
            {
                Add(row.String(1), row.Int(0) * 3600.0 + 1800, row.Double(2), row.Double(3), row.Double(4), row.Double(5), row.Double(6), row.Double(7), row.Double(8));
            }, (long)(from / 3600), (long)(to / 3600) + 1);
        }
        report.Apps = totals.Values.Where(t => t.Joules > 0.5 || t.GpuSeconds > 1).OrderByDescending(t => t.Joules).ToList();
        report.Buckets = buckets.Values.OrderBy(b => b.Start).ToList();

        _db.Query("SELECT start, end, before, after, battery FROM sleeps WHERE end > ? AND start < ? ORDER BY start", row =>
            report.Sleeps.Add(new SleepRecord(row.Date(0), row.Date(1), row.Double(2), row.Double(3), row.Int(4) == 1)), from, to);
        if (report.FullWh == 0)
            _db.Query("SELECT full_wh FROM power WHERE full_wh > 0 ORDER BY t DESC LIMIT 1", row => report.FullWh = row.Double(0));
        return report;
    });

    /// <summary>Joules per bucket for a single app.</summary>
    public List<(DateTime date, double joules)> Series(string key, DateRange range, double bucket, bool batteryOnly) => Locked(() =>
    {
        double from = Time.Unix(range.Start), to = Time.Unix(range.End);
        var filter = batteryOnly ? "AND source='battery'" : "";
        var sums = new Dictionary<long, double>();
        if (UseMinutes(range.Start))
        {
            _db.Query($"SELECT t, joules FROM usage WHERE app=? AND t > ? AND t <= ? {filter}", row =>
            {
                var i = (long)((row.Double(0) - from) / bucket);
                sums[i] = sums.GetValueOrDefault(i) + row.Double(1);
            }, key, from, to);
        }
        else
        {
            _db.Query($"SELECT hour, joules FROM usage_hourly WHERE app=? AND hour >= ? AND hour < ? {filter}", row =>
            {
                var i = (long)((row.Int(0) * 3600.0 + 1800 - from) / bucket);
                sums[i] = sums.GetValueOrDefault(i) + row.Double(1);
            }, key, (long)(from / 3600), (long)(to / 3600) + 1);
        }
        var count = (int)((to - from) / bucket) + 1;
        return Enumerable.Range(0, count).Select(i => (Time.FromUnix(from + i * bucket), sums.GetValueOrDefault(i))).ToList();
    });

    public List<MemberShare> Members(string key, DateRange range) => Locked(() =>
    {
        var result = new List<MemberShare>();
        _db.Query("SELECT member, SUM(joules) s FROM members WHERE app=? AND hour >= ? AND hour <= ? GROUP BY member ORDER BY s DESC LIMIT 12",
            row => result.Add(new MemberShare(row.String(0), row.Double(1))),
            key, (long)(Time.Unix(range.Start) / 3600), (long)(Time.Unix(range.End) / 3600));
        return result;
    });

    /// <summary>Recorded average system watts per interval, oldest first.</summary>
    public List<(DateTime date, double watts)> PowerSeries(DateTime since) => Locked(() =>
    {
        var result = new List<(DateTime, double)>();
        _db.Query("SELECT t, watts FROM power WHERE t > ? ORDER BY t", row => result.Add((row.Date(0), row.Double(1))), Time.Unix(since));
        return result;
    });

    /// <summary>
    /// Stretches on battery between two charges, newest first. A session ends when the adapter shows
    /// up, or when the level rose between two samples (charged while asleep).
    /// </summary>
    public List<DischargeSession> Sessions(DateTime since) => Locked(() =>
    {
        var sessions = new List<DischargeSession>();
        DischargeSession? current = null;
        _db.Query("SELECT t, dt, source, percent, watts FROM power WHERE t > ? ORDER BY t", row =>
        {
            double t = row.Double(0), dt = row.Double(1), percent = row.Double(3), watts = row.Double(4);
            if (row.String(2) != "battery")
            {
                if (current != null) sessions.Add(current);
                current = null;
                return;
            }
            if (current != null && percent < current.PercentEnd + 2)
            {
                current.End = Time.FromUnix(t);
                current.PercentEnd = percent;
                current.Joules += watts * dt;
                current.AwakeSeconds += dt;
            }
            else
            {
                var chargedUnseen = current != null;
                if (current != null) sessions.Add(current);
                current = new DischargeSession
                {
                    Start = Time.FromUnix(t - dt), End = Time.FromUnix(t), PercentStart = percent, PercentEnd = percent,
                    Joules = watts * dt, AwakeSeconds = dt, ChargedUnseen = chargedUnseen,
                };
            }
        }, Time.Unix(since));
        if (current != null)
        {
            current.Ongoing = true;
            sessions.Add(current);
        }
        // Keep the running one even if it is young; older ones only with some substance.
        var kept = sessions.Where(s => s.Ongoing || s.AwakeSeconds >= 300).ToList();
        kept.Reverse();
        return kept;
    });

    public List<Notice> Alerts(int limit = 200) => Locked(() =>
    {
        var result = new List<Notice>();
        _db.Query("SELECT id, t, kind, app, title, body, value, read FROM alerts ORDER BY t DESC LIMIT ?", row =>
            result.Add(new Notice(row.Int(0), row.Date(1), EventKindExtensions.ParseKind(row.String(2)), row.OptionalString(3),
                row.String(4), row.String(5), row.Double(6), row.Int(7) == 1)), limit);
        return result;
    });

    public int UnreadAlertCount() => Locked(() =>
    {
        var count = 0;
        _db.Query("SELECT COUNT(*) FROM alerts WHERE read=0", row => count = (int)row.Int(0));
        return count;
    });

    public List<SleepRecord> LastSleeps(int limit) => Locked(() =>
    {
        var result = new List<SleepRecord>();
        _db.Query("SELECT start, end, before, after, battery FROM sleeps WHERE end - start > 1800 ORDER BY start DESC LIMIT ?", row =>
            result.Add(new SleepRecord(row.Date(0), row.Date(1), row.Double(2), row.Double(3), row.Int(4) == 1)), limit);
        return result;
    });

    /// <summary>
    /// Awake drain is shared out by measured energy; sleep drain is its own entry,
    /// so the entries add up to the percentage actually lost.
    /// </summary>
    public SessionBreakdown Breakdown(DischargeSession session, int topCount = 7)
    {
        var range = new DateRange(session.Start, Time.Max(session.End, session.Start.AddSeconds(1)));
        var report = Report(range, 3600, true);
        double sleepLost = 0, sleepSeconds = 0;
        var allSleep = report.Sleeps.Sum(s => Math.Max(0, (Time.Min(s.End, range.End) - Time.Max(s.Start, range.Start)).TotalSeconds));
        foreach (var sleep in report.Sleeps.Where(s => s.OnBattery && s.Start >= session.Start.AddSeconds(-60)))
        {
            sleepLost += sleep.PercentLost;
            sleepSeconds += (sleep.End - sleep.Start).TotalSeconds;
        }
        sleepLost = Math.Min(sleepLost, session.PercentUsed);
        var awake = session.PercentUsed - sleepLost;
        var total = Math.Max(report.TotalJoules, 0.001);
        double Percent(double joules) => awake * joules / total;

        var entries = new List<ShareEntry>();
        foreach (var item in report.Apps.OrderByDescending(a => a.Joules).Take(topCount).Where(a => a.Joules > 0))
        {
            entries.Add(new ShareEntry
            {
                Kind = item.App.Category == AppCategory.App ? ShareKind.App : ShareKind.Service, Name = item.App.Name, App = item.App,
                Joules = item.Joules, Percent = Percent(item.Joules), Estimated = item.IsEstimated,
            });
        }
        // Everything measured but not listed, including the tiny apps the report leaves out.
        var rest = report.Breakdown.Apps + report.Breakdown.Services - entries.Sum(e => e.Joules);
        if (rest > 0.5) entries.Add(new ShareEntry { Kind = ShareKind.Others, Name = "Other apps & services", Joules = rest, Percent = Percent(rest) });
        if (report.Breakdown.Hardware > 0)
            entries.Add(new ShareEntry { Kind = ShareKind.Hardware, Name = "Display & hardware", Joules = report.Breakdown.Hardware, Percent = Percent(report.Breakdown.Hardware) });
        if (sleepLost > 0) entries.Add(new ShareEntry { Kind = ShareKind.Sleep, Name = "Sleep", Percent = sleepLost });
        // Rows that would read "0.0 %" only add noise; fold them into the rest.
        var tiny = entries.Where(e => (e.Kind == ShareKind.App || e.Kind == ShareKind.Service) && e.Percent < 0.05).ToList();
        if (tiny.Count > 0)
        {
            entries.RemoveAll(tiny.Contains);
            double joules = tiny.Sum(e => e.Joules), share = tiny.Sum(e => e.Percent);
            var others = entries.FirstOrDefault(e => e.Kind == ShareKind.Others);
            if (others != null) { others.Joules += joules; others.Percent += share; }
            else entries.Add(new ShareEntry { Kind = ShareKind.Others, Name = "Other apps & services", Joules = joules, Percent = share });
        }
        entries = entries.OrderByDescending(e => e.Percent).ToList();

        double lockedSeconds = 0, lockedJoules = 0, brightnessSum = 0, brightnessSeconds = 0;
        var earlier = false;
        Locked(() =>
        {
            _db.Query("SELECT dt, watts, front, brightness FROM power WHERE source='battery' AND t > ? AND t <= ?", row =>
            {
                var dt = row.Double(0);
                if (row.OptionalString(2) == AppResolver.LockedKey)
                {
                    lockedSeconds += dt;
                    lockedJoules += row.Double(1) * dt;
                }
                if (row.OptionalDouble(3) is double brightness)
                {
                    brightnessSum += brightness * dt;
                    brightnessSeconds += dt;
                }
            }, Time.Unix(range.Start), Time.Unix(range.End));
            // Any sample before the session means Joule was already recording.
            // Half a second of slack: the start went through a double → DateTime → double round trip.
            _db.Query("SELECT 1 FROM power WHERE t <= ? LIMIT 1", _ => earlier = true, Time.Unix(session.Start) + 0.5);
        });
        var wall = (session.End - session.Start).TotalSeconds;
        return new SessionBreakdown
        {
            Session = session,
            Entries = entries,
            SleepHours = sleepSeconds / 3600,
            StartsAtRecording = !earlier,
            UnrecordedSeconds = Math.Max(0, wall - allSleep - report.RecordedSeconds),
            LockedSeconds = lockedSeconds,
            LockedPercent = Percent(lockedJoules),
            AverageBrightness = brightnessSeconds > 0 ? brightnessSum / brightnessSeconds : null,
        };
    }
}
