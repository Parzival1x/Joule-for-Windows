namespace Joule.Core;

/// <summary>Local history in %LOCALAPPDATA%\Joule.</summary>
public sealed partial class Store : IDisposable
{
    private readonly SQLiteDatabase _db;
    private readonly object _lock = new();

    public static string DefaultDirectory => System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Joule");
    public static string DefaultPath => System.IO.Path.Combine(DefaultDirectory, "history.sqlite");

    public string Path { get; }

    public Store(string? path = null)
    {
        Path = path ?? DefaultPath;
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path)!);
        _db = new SQLiteDatabase(Path);
        Migrate();
    }

    public void Dispose() => _db.Dispose();

    internal T Locked<T>(Func<T> body) { lock (_lock) return body(); }
    internal void Locked(Action body) { lock (_lock) body(); }
    internal SQLiteDatabase Database => _db;

    private void Migrate()
    {
        _db.Execute("""
            CREATE TABLE IF NOT EXISTS apps(key TEXT PRIMARY KEY, name TEXT NOT NULL, bundle TEXT, category TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS power(
                t REAL PRIMARY KEY, dt REAL NOT NULL, source TEXT NOT NULL, percent REAL NOT NULL,
                watts REAL NOT NULL, remaining_wh REAL, full_wh REAL, brightness REAL,
                measured_j REAL, front TEXT);
            CREATE TABLE IF NOT EXISTS usage(
                t REAL NOT NULL, app TEXT NOT NULL, source TEXT NOT NULL, joules REAL NOT NULL,
                cpu REAL NOT NULL, gpu REAL NOT NULL, wakeups REAL NOT NULL, fg INTEGER NOT NULL, dt REAL NOT NULL,
                estimated REAL NOT NULL DEFAULT 0,
                PRIMARY KEY(t, app)) WITHOUT ROWID;
            CREATE INDEX IF NOT EXISTS usage_app ON usage(app, t);
            CREATE TABLE IF NOT EXISTS usage_hourly(
                hour INTEGER NOT NULL, app TEXT NOT NULL, source TEXT NOT NULL, joules REAL NOT NULL,
                cpu REAL NOT NULL, gpu REAL NOT NULL, wakeups REAL NOT NULL, fg_seconds REAL NOT NULL, seconds REAL NOT NULL,
                estimated REAL NOT NULL DEFAULT 0,
                PRIMARY KEY(hour, app, source)) WITHOUT ROWID;
            CREATE TABLE IF NOT EXISTS members(
                hour INTEGER NOT NULL, app TEXT NOT NULL, member TEXT NOT NULL, joules REAL NOT NULL,
                PRIMARY KEY(hour, app, member)) WITHOUT ROWID;
            CREATE TABLE IF NOT EXISTS sleeps(start REAL PRIMARY KEY, end REAL NOT NULL, before REAL NOT NULL, after REAL NOT NULL, battery INTEGER NOT NULL);
            CREATE TABLE IF NOT EXISTS alerts(
                id INTEGER PRIMARY KEY AUTOINCREMENT, t REAL NOT NULL, kind TEXT NOT NULL, app TEXT,
                title TEXT NOT NULL, body TEXT NOT NULL, value REAL NOT NULL, read INTEGER NOT NULL DEFAULT 0);
            CREATE TABLE IF NOT EXISTS meta(key TEXT PRIMARY KEY, value REAL NOT NULL);
            CREATE TABLE IF NOT EXISTS devices(id TEXT PRIMARY KEY, name TEXT NOT NULL, kind TEXT NOT NULL, via TEXT NOT NULL, seen REAL NOT NULL);
            CREATE TABLE IF NOT EXISTS device_levels(id TEXT NOT NULL, t REAL NOT NULL, percent REAL NOT NULL, charging INTEGER NOT NULL,
                PRIMARY KEY(id, t)) WITHOUT ROWID;
            """);
        RemoveRowsSpanningSleep();
    }

    /// <summary>A power row overlapping a recorded sleep by more than a minute is not awake time.</summary>
    private void RemoveRowsSpanningSleep()
    {
        _db.Execute("""
            DELETE FROM power WHERE EXISTS (
                SELECT 1 FROM sleeps s WHERE s.start < power.t - 60 AND s.end > power.t - power.dt + 60)
            """);
        _db.Execute("""
            DELETE FROM alerts WHERE kind IN ('appHog', 'backgroundHog', 'sleepBlocker', 'highDrain') AND EXISTS (
                SELECT 1 FROM sleeps s WHERE s.start < alerts.t - 60 AND s.end > alerts.t + 60)
            """);
    }

    // MARK: Writing

    /// <summary>Interval without app breakdown (first sample after start or wake) is allowed.</summary>
    public void Record(PowerSample power, Interval? interval)
    {
        Locked(() => _db.Transaction(() =>
        {
            var t = Time.Unix(power.End);
            var source = power.Source.Raw();
            _db.Run("INSERT OR REPLACE INTO power VALUES(?,?,?,?,?,?,?,?,?,?)",
                t, power.Duration, source, power.Percent, power.Watts, power.RemainingWh, power.FullWh,
                power.Brightness, interval?.MeasuredJoules, power.Frontmost);
            if (interval == null) return;
            var hour = (long)Math.Floor(t / 3600);
            var dt = interval.Duration;
            foreach (var usage in interval.Usages)
            {
                _db.Run("INSERT INTO apps VALUES(?,?,?,?) ON CONFLICT(key) DO UPDATE SET name=excluded.name, bundle=excluded.bundle",
                    usage.App.Key, usage.App.Name, usage.App.BundlePath, usage.App.Category.Raw());
                // Tiny contributions only bloat the minute table; they still count in the hourly one.
                var fg = usage.App.Key == power.Frontmost;
                if (usage.Joules >= 0.05 || usage.GpuSeconds > 0.01 || fg)
                {
                    _db.Run("INSERT OR REPLACE INTO usage(t, app, source, joules, cpu, gpu, wakeups, fg, dt, estimated) VALUES(?,?,?,?,?,?,?,?,?,?)",
                        t, usage.App.Key, source, usage.Joules, usage.CpuSeconds, usage.GpuSeconds, usage.IdleWakeups, fg, dt, usage.EstimatedJoules);
                }
                _db.Run("""
                    INSERT INTO usage_hourly(hour, app, source, joules, cpu, gpu, wakeups, fg_seconds, seconds, estimated) VALUES(?,?,?,?,?,?,?,?,?,?)
                    ON CONFLICT(hour, app, source) DO UPDATE SET joules=joules+excluded.joules, cpu=cpu+excluded.cpu,
                    gpu=gpu+excluded.gpu, wakeups=wakeups+excluded.wakeups, fg_seconds=fg_seconds+excluded.fg_seconds, seconds=seconds+excluded.seconds,
                    estimated=estimated+excluded.estimated
                    """, hour, usage.App.Key, source, usage.Joules, usage.CpuSeconds, usage.GpuSeconds, usage.IdleWakeups, fg ? dt : 0.0, dt, usage.EstimatedJoules);
                foreach (var (member, joules) in usage.Members)
                {
                    if (joules < 0.01) continue;
                    _db.Run("""
                        INSERT INTO members VALUES(?,?,?,?)
                        ON CONFLICT(hour, app, member) DO UPDATE SET joules=joules+excluded.joules
                        """, hour, usage.App.Key, member, joules);
                }
            }
        }));
    }

    public void RecordSleep(SleepRecord sleep) =>
        Locked(() => _db.Run("INSERT OR REPLACE INTO sleeps VALUES(?,?,?,?,?)", sleep.Start, sleep.End, sleep.PercentBefore, sleep.PercentAfter, sleep.OnBattery));

    public long AddAlert(EventKind kind, string? app, string title, string body, double value, DateTime? date = null) => Locked(() =>
    {
        _db.Run("INSERT INTO alerts(t, kind, app, title, body, value) VALUES(?,?,?,?,?,?)", date ?? DateTime.Now, kind.Raw(), app, title, body, value);
        return _db.LastInsertId;
    });

    public void MarkAlertsRead() => Locked(() => _db.Run("UPDATE alerts SET read=1 WHERE read=0"));
    public void DeleteAlert(long id) => Locked(() => _db.Run("DELETE FROM alerts WHERE id=?", id));

    /// <summary>Minute rows are kept a week, aggregates 90 days.</summary>
    public void Prune(DateTime? now = null) => Locked(() =>
    {
        var n = now ?? DateTime.Now;
        var minuteCutoff = Time.Unix(n.AddDays(-7));
        var cutoff = Time.Unix(n.AddDays(-90));
        _db.Run("DELETE FROM usage WHERE t < ?", minuteCutoff);
        _db.Run("DELETE FROM usage_hourly WHERE hour < ?", (long)(cutoff / 3600));
        _db.Run("DELETE FROM members WHERE hour < ?", (long)(cutoff / 3600));
        _db.Run("DELETE FROM power WHERE t < ?", cutoff);
        _db.Run("DELETE FROM sleeps WHERE start < ?", cutoff);
        _db.Run("DELETE FROM alerts WHERE t < ?", cutoff);
        _db.Run("DELETE FROM device_levels WHERE t < ?", cutoff);
    });

    public void EraseAll() => Locked(() =>
        _db.Execute("DELETE FROM usage; DELETE FROM usage_hourly; DELETE FROM members; DELETE FROM power; DELETE FROM sleeps; DELETE FROM alerts; DELETE FROM apps; DELETE FROM device_levels; DELETE FROM devices;"));

    public double? Meta(string key) => Locked(() =>
    {
        double? value = null;
        _db.Query("SELECT value FROM meta WHERE key=?", r => value = r.Double(0), key);
        return value;
    });

    public void SetMeta(string key, double value) => Locked(() => _db.Run("INSERT OR REPLACE INTO meta VALUES(?,?)", key, value));

    // MARK: Reading helpers

    internal Dictionary<string, AppIdentity> Apps()
    {
        var result = new Dictionary<string, AppIdentity>();
        _db.Query("SELECT key, name, bundle, category FROM apps", row =>
        {
            var key = row.String(0);
            result[key] = new AppIdentity(key, row.String(1), row.OptionalString(2), AppCategoryExtensions.ParseCategory(row.String(3)));
        });
        return result;
    }

    /// <summary>App names for keys found in the history.</summary>
    public Dictionary<string, AppIdentity> AppIdentities() => Locked(Apps);

    /// <summary>Newest stored sample.</summary>
    public (double percent, double watts, PowerSource source)? LatestPower() => Locked(() =>
    {
        (double, double, PowerSource)? result = null;
        _db.Query("SELECT percent, watts, source FROM power ORDER BY t DESC LIMIT 1",
            row => result = (row.Double(0), row.Double(1), PowerSourceExtensions.ParseSource(row.String(2))));
        return result;
    });
}
