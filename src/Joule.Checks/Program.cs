using System.Runtime.CompilerServices;
using Joule.Core;

if (args.Length > 0 && args[0] == "probe") { Probe.Run(args.Skip(1).ToArray()); return 0; }
if (args.Length > 0 && args[0] == "devices") { Probe.Devices(); return 0; }
if (args.Length > 0 && args[0] == "hidpp-flags") { DeviceProbe.DiagnoseLogitechNotifications(Console.WriteLine); return 0; }
if (args.Length > 0 && args[0] == "hidpp") { DeviceProbe.DiagnoseLogitech(Console.WriteLine); return 0; }
return Checks.RunAll();

static class Probe
{
    public static void Devices()
    {
        var watch = System.Diagnostics.Stopwatch.StartNew();
        var devices = DeviceProbe.ReadAll();
        Console.WriteLine($"{devices.Count} devices in {watch.ElapsedMilliseconds} ms");
        watch.Restart();
        DeviceProbe.ReadAll();
        Console.WriteLine($"second read: {watch.ElapsedMilliseconds} ms");
        foreach (var d in devices)
            Console.WriteLine($"{d.LevelText,6}  {(d.Connected ? "connected" : "away"),-9} {(d.Charging ? "charging" : ""),-8} {d.Kind,-10} {d.Via,-13} {d.Name}  [{d.Id}] updated {d.Updated?.ToString("g") ?? "-"}");
    }

    public static void Run(string[] args)
    {
        var seconds = args.Length > 0 && int.TryParse(args[0], out var s) ? s : 5;
        Console.WriteLine($"Energy meter available: {EnergyProbe.Available}");
        foreach (var (name, value) in EnergyProbe.ReadRaw()) Console.WriteLine($"  raw {name} = {value}");
        var accountant = new Accountant();
        var e0 = EnergyProbe.Read();
        var t0 = DateTime.Now;
        accountant.Ingest(Snapshot.Capture());
        var power = PowerProbe.Read();
        Thread.Sleep(seconds * 1000);
        var snapshot = Snapshot.Capture();
        var e1 = EnergyProbe.Read();
        var dt = (DateTime.Now - t0).TotalSeconds;
        if (e0 != null && e1 != null)
            Console.WriteLine($"Package {Format.Watts((e1.PackageJoules - e0.PackageJoules) / dt)}, cores {Format.Watts(((e1.CoreJoules ?? 0) - (e0.CoreJoules ?? 0)) / dt)}");
        if (power != null)
            Console.WriteLine($"Battery {Format.Percent(power.Percent, 1)} · {power.Source} · battery flow {Format.Watts(power.BatteryWatts)} · system {Format.Watts(power.SystemWatts)} · full {Format.Number(power.FullWh)} Wh of {Format.Number(power.DesignWh)} Wh · health {Format.Percent(power.HealthPercent)} · cycles {power.CycleCount} · {Format.Number(power.Voltage, 2)} V · temp {power.TemperatureC?.ToString("0.0") ?? "n/a"}");
        else Console.WriteLine("No battery");
        Console.WriteLine($"Brightness: {DisplayProbe.Brightness(power?.Source == PowerSource.Battery)?.ToString("P0") ?? "unknown"}");
        Console.WriteLine($"Processes: {snapshot.Processes.Count}, with path: {snapshot.Processes.Count(p => p.Path.Length > 0)}, GPU pids: {snapshot.Gpu.Count}");
        var interval = accountant.Ingest(snapshot);
        if (interval == null) { Console.WriteLine("No interval"); return; }
        Console.WriteLine($"Measured over processes: {Format.Watts(interval.MeasuredJoules / interval.Duration)} in {Format.Number(interval.Duration)} s (package {Format.Watts((interval.PackageJoules ?? 0) / interval.Duration)})");
        foreach (var u in interval.Usages.Take(20))
            Console.WriteLine($"{u.Joules / interval.Duration,7:0.00} W  cpu {u.CpuSeconds / interval.Duration,5:0.00}  gpu {u.GpuSeconds / interval.Duration,4:0.00}  csw/s {u.IdleWakeups / interval.Duration,6:0}  {u.App.Name} [{u.App.Category}] {u.App.Key}");
        var fg = Foreground.Pid();
        Console.WriteLine($"Foreground pid: {fg}");
        Console.WriteLine($"Elevated: {PowerRequestProbe.Elevated}");
        var blockers = PowerRequestProbe.ReadAll();
        if (blockers == null) Console.WriteLine("Sleep blockers: needs administrator rights");
        else foreach (var b in blockers) Console.WriteLine($"Keeps awake: {b.ProcessName} – {b.Reason} [{b.Type}] {b.Path}");
    }
}

static class Checks
{
    static int _failures, _count;

    public static int RunAll()
    {
        Run("helpers join their app", HelpersJoinTheirApp);
        Run("install roots group processes", InstallRoots);
        Run("reused pid does not produce a delta", ReusedPid);
        Run("sleep between snapshots discards the interval", SleepDiscards);
        Run("energy is shared out by cycles", EnergySharedByCycles);
        Run("no meter falls back to an estimate", NoMeterFallback);
        Run("gpu time is differenced", GpuDifferenced);
        Run("gpu instance pid parses", GpuPid);
        Run("battery status combines and signs", BatteryCombine);
        Run("sustained foreground drain warns once within cooldown", SustainedWarnsOnce);
        Run("short spike does not warn", ShortSpike);
        Run("background app warns at lower threshold", BackgroundWarns);
        Run("no warnings on adapter by default", NoWarningsOnAdapter);
        Run("muted apps stay silent", Muted);
        Run("sleep blocker after threshold", BlockerAfterThreshold);
        Run("sleep drain", SleepDrain);
        Run("report splits apps, services and hardware", ReportSplits);
        Run("notices round trip", NoticesRoundTrip);
        Run("demo data produces a plausible fortnight", DemoPlausible);
        Run("shares add up to the lost percentage", SharesAddUp);
        Run("explains a screen-dominated locked session", ExplainsLocked);
        Run("names the app when one dominates", NamesDominantApp);
        Run("lanes and day strips", LanesAndStrips);
        Run("rows spanning sleep are removed", RowsSpanningSleep);
        Run("charging while asleep starts a new session", ChargingAsleep);
        Run("powercfg output parses", PowercfgParses);
        Run("power model learns the baseline", PowerModelLearns);
        Run("formatting", Formatting);
        Run("bluetooth instance ids and hid paths parse", DeviceIds);
        Run("playstation battery reports parse", SonyReports);
        Run("device kinds classify", DeviceKinds);
        Run("device forecast uses the latest discharge", DeviceForecastRun);
        Run("device low alert fires once per discharge", DeviceLowOnce);
        Run("device levels store only changes", DeviceLevelsStore);
        Run("logitech hid++ battery replies parse", LogitechReplies);
        Console.WriteLine();
        Console.WriteLine(_failures == 0 ? $"All {_count} checks passed." : $"{_failures} of {_count} checks FAILED.");
        return _failures == 0 ? 0 : 1;
    }

    static void Run(string name, Action body)
    {
        _count++;
        try { body(); Console.WriteLine($"  ok    {name}"); }
        catch (Exception e) { _failures++; Console.WriteLine($"  FAIL  {name}: {e.Message}"); }
    }

    static void Expect(bool condition, [CallerArgumentExpression(nameof(condition))] string? text = null, [CallerLineNumber] int line = 0)
    {
        if (!condition) throw new Exception($"line {line}: {text}");
    }

    // MARK: Fixtures

    static readonly Func<string, ExeInfo> FakeInfo = path => new ExeInfo(
        System.IO.Path.GetFileNameWithoutExtension(path) switch
        {
            "chrome" => "Google Chrome", "steam" => "Steam", "steamwebhelper" => "Steam Client WebHelper", "Code" => "Visual Studio Code",
            "OUTLOOK" => "Microsoft Outlook", "WINWORD" => "Microsoft Word", var other => other,
        }, null, path.EndsWith("node.exe", StringComparison.OrdinalIgnoreCase) || path.EndsWith("python.exe", StringComparison.OrdinalIgnoreCase));

    static AppResolver Resolver() => new(FakeInfo, @"C:\Windows", @"C:\Program Files", @"C:\Program Files (x86)",
        @"C:\Users\me\AppData\Local", @"C:\Users\me\AppData\Roaming", @"C:\ProgramData");

    static ProcessReading P(int pid, string path, ulong cpuMs = 0, ulong cycles = 0, int parent = 0, long start = 1, int session = 1, string? name = null) => new()
    {
        Pid = pid, Path = path, Name = name ?? (path.Length > 0 ? System.IO.Path.GetFileName(path) : $"pid {pid}"),
        CpuNanos = cpuMs * 1_000_000, Cycles = cycles, ParentPid = parent, StartTime = start, SessionId = session,
    };

    static Snapshot Snap(double t, List<ProcessReading> processes, double? package = null, double? cores = null, Dictionary<int, ulong>? gpu = null, double asleep = 0) => new()
    {
        Date = new DateTime(2026, 1, 1, 12, 0, 0).AddSeconds(t + asleep),
        Continuous = 1000 + t + asleep,
        Awake = 1000 + t,
        Processes = processes,
        Gpu = gpu ?? new(),
        Energy = package is double p ? new EnergyReading { PackageJoules = p, CoreJoules = cores } : null,
    };

    static Dictionary<int, ProcessReading> Map(params ProcessReading[] ps) => ps.ToDictionary(p => p.Pid);

    // MARK: Resolver & accountant

    static void HelpersJoinTheirApp()
    {
        var r = Resolver();
        var app = P(100, @"C:\Users\me\AppData\Local\Programs\MyApp\MyApp.exe", parent: 1);
        var webview = P(101, @"C:\Program Files (x86)\Microsoft\EdgeWebView\Application\120.0\msedgewebview2.exe", parent: 100, start: 2);
        var nested = P(102, webview.Path, parent: 101, start: 3);
        var map = Map(app, webview, nested);
        Expect(r.Identity(webview, map).Key == r.Identity(app, map).Key);
        Expect(r.Identity(nested, map).Key == r.Identity(app, map).Key);
        Expect(r.Identity(app, map).Category == AppCategory.App);
        // A webview whose parent started later (pid reuse) stays on its own.
        var orphan = P(103, webview.Path, parent: 104, start: 2);
        var late = P(104, app.Path, start: 9);
        Expect(r.Identity(orphan, Map(orphan, late)).Key != r.Identity(late, Map(orphan, late)).Key);
    }

    static void InstallRoots()
    {
        var r = Resolver();
        var steam = P(1, @"C:\Program Files (x86)\Steam\steam.exe", parent: 999);
        var helper = P(2, @"C:\Program Files (x86)\Steam\bin\cef\cef.win7x64\steamwebhelper.exe", parent: 1, start: 2);
        var map = Map(steam, helper);
        r.Prime(new[] { helper, steam }, map);
        var id = r.Identity(helper, map);
        Expect(id.Key == r.Identity(steam, map).Key);
        Expect(id.Name == "Steam");
        var chrome = P(3, @"C:\Program Files\Google\Chrome\Application\chrome.exe");
        var update = P(4, @"C:\Program Files (x86)\Google\Update\GoogleUpdate.exe");
        Expect(r.Identity(chrome, Map(chrome)).Key == @"app:c:\program files\google\chrome");
        Expect(r.Identity(update, Map(update)).Key != r.Identity(chrome, Map(chrome)).Key);
        var outlook = P(5, @"C:\Program Files\Microsoft Office\root\Office16\OUTLOOK.EXE");
        var word = P(6, @"C:\Program Files\Microsoft Office\root\Office16\WINWORD.EXE");
        Expect(r.Identity(outlook, Map(outlook)).Key != r.Identity(word, Map(word)).Key);
        Expect(r.Identity(outlook, Map(outlook)).Name == "Microsoft Outlook");
        var node = P(7, @"C:\Program Files\nodejs\node.exe");
        Expect(r.Identity(node, Map(node)) is { Category: AppCategory.Tool, Key: "tool:node" });
        var svchost = P(8, @"C:\Windows\System32\svchost.exe");
        Expect(r.Identity(svchost, Map(svchost)).Category == AppCategory.System);
        var explorer = P(9, @"C:\Windows\explorer.exe");
        Expect(r.Identity(explorer, Map(explorer)).Category == AppCategory.App);
        var service = P(10, @"C:\Program Files\Vendor\Service\agent.exe", session: 0);
        Expect(r.Identity(service, Map(service)).Category == AppCategory.System);
        var hidden = P(11, "", name: "MsMpEng.exe");
        Expect(r.Identity(hidden, Map(hidden)).Key == "system:MsMpEng");
        var discord = P(12, @"C:\Users\me\AppData\Local\Discord\app-1.0.9\Discord.exe");
        Expect(r.Identity(discord, Map(discord)).Key == @"app:c:\users\me\appdata\local\discord");
        var package = P(13, @"C:\Program Files\WindowsApps\Microsoft.WindowsCalculator_11.2405.2.0_x64__8wekyb3d8bbwe\CalculatorApp.exe");
        Expect(r.Identity(package, Map(package)).Key == @"app:c:\program files\windowsapps\microsoft.windowscalculator_11.2405.2.0_x64__8wekyb3d8bbwe");
        var portable = P(14, @"D:\Games\Foo\bin\foo.exe");
        Expect(r.Identity(portable, Map(portable)).Key == @"app:d:\games\foo\bin");
    }

    static void ReusedPid()
    {
        var a = new Accountant(Resolver());
        var path = @"C:\Program Files\A\a.exe";
        a.Ingest(Snap(0, new() { P(10, path, cpuMs: 5000, cycles: 5_000_000, start: 1) }, package: 100, cores: 50));
        var interval = a.Ingest(Snap(60, new() { P(10, path, cpuMs: 100, cycles: 100_000, start: 2) }, package: 160, cores: 80))!;
        Expect(interval.Usages.Count == 0);
    }

    static void SleepDiscards()
    {
        var a = new Accountant(Resolver());
        a.Ingest(Snap(0, new() { P(1, @"C:\Program Files\A\a.exe", 0, 0) }));
        Expect(a.Ingest(Snap(60, new() { P(1, @"C:\Program Files\A\a.exe", 1000, 10) }, asleep: 600)) == null);
    }

    static void EnergySharedByCycles()
    {
        var a = new Accountant(Resolver());
        var p1 = @"C:\Program Files\A\a.exe";
        var p2 = @"C:\Program Files\B\b.exe";
        a.Ingest(Snap(0, new() { P(1, p1, 0, 0), P(2, p2, 0, 0) }, package: 1000, cores: 500));
        var interval = a.Ingest(Snap(60, new() { P(1, p1, 3000, 3_000_000_000), P(2, p2, 3000, 1_000_000_000) }, package: 1600, cores: 740))!;
        var ua = interval.Usages.First(u => u.App.Key.EndsWith(@"\a"));
        var ub = interval.Usages.First(u => u.App.Key.EndsWith(@"\b"));
        Expect(Math.Abs(ua.Joules - 180) < 1e-6);
        Expect(Math.Abs(ub.Joules - 60) < 1e-6);
        Expect(Math.Abs(interval.MeasuredJoules - 240) < 1e-6);
        Expect(interval.PackageJoules == 600);
        Expect(ua.EstimatedJoules == 0);
        Expect(ua.Members.ContainsKey("a.exe"));
    }

    static void NoMeterFallback()
    {
        var a = new Accountant(Resolver()) { FallbackJoulesPerCpuSecond = 2 };
        var p1 = @"C:\Program Files\A\a.exe";
        a.Ingest(Snap(0, new() { P(1, p1, 0, 0) }));
        var interval = a.Ingest(Snap(60, new() { P(1, p1, 5000, 100) }))!;
        Expect(Math.Abs(interval.Usages[0].Joules - 10) < 1e-9);
        Expect(interval.Usages[0].EstimatedJoules == interval.Usages[0].Joules);
    }

    static void GpuDifferenced()
    {
        var a = new Accountant(Resolver());
        var p1 = @"C:\Program Files\A\a.exe";
        a.Ingest(Snap(0, new() { P(1, p1, 0, 0) }, gpu: new() { [1] = 5_000_000_000 }));
        var interval = a.Ingest(Snap(60, new() { P(1, p1, 10, 10) }, gpu: new() { [1] = 7_000_000_000 }))!;
        Expect(Math.Abs(interval.Usages[0].GpuSeconds - 2) < 1e-9);
    }

    static void GpuPid()
    {
        Expect(GPUProbe.ParsePID("pid_412_luid_0x00000000_0x0000D1F3_phys_0_eng_0_engtype_3D") == 412);
        Expect(GPUProbe.ParsePID("_Total") == null);
    }

    static void BatteryCombine()
    {
        // Discharging at 14 W, some drivers report a positive rate.
        var r = PowerProbe.Combine(new() { (2u, 50000u, 16000u, 14000, 0u, 84000u, 80000u, 87u, 30.0, null) }, false, -1, DateTime.Now);
        Expect(r.Source == PowerSource.Battery);
        Expect(Math.Abs(r.SystemWatts - 14) < 1e-9);
        Expect(Math.Abs(r.BatteryWatts + 14) < 1e-9);
        Expect(Math.Abs(r.Percent - 62.5) < 1e-9);
        Expect(Math.Abs(r.HealthPercent - 80000.0 / 84000 * 100) < 1e-9);
        Expect(r.MinutesToEmpty == (int)(50.0 / 14 * 60));
        var c = PowerProbe.Combine(new() { (1u | 4u, 50000u, 16000u, 20000, 0u, 84000u, 80000u, 87u, null, null) }, true, -1, DateTime.Now);
        Expect(c.Source == PowerSource.Charging && c.Estimated && c.MinutesToFull == 90);
        var a = PowerProbe.Combine(new() { (1u, 63850u, 16711u, 0, 0u, 84000u, 84000u, 10u, null, null) }, true, -1, DateTime.Now);
        Expect(a.Source == PowerSource.Adapter);
    }

    // MARK: Warnings

    static readonly AppIdentity Hog = new("app:c:\\program files\\hog", "Hog", null, AppCategory.App);
    static readonly DateTime T0 = new(2026, 1, 1, 12, 0, 0);

    static Observation Obs(double minute, double watts, string? front = null, PowerSource source = PowerSource.Battery, Dictionary<AppIdentity, string>? blockers = null) => new()
    {
        Date = T0.AddMinutes(minute), Duration = 60, Source = source, Percent = 60, SystemWatts = 12 + watts, RemainingWh = 40, FullWh = 80,
        Usages = new() { new AppUsage { App = Hog, Joules = watts * 60 } }, Frontmost = front, Blockers = blockers ?? new(),
    };

    static void SustainedWarnsOnce()
    {
        var e = new WarningEngine();
        var findings = new List<Finding>();
        for (var m = 0; m < 30; m++) findings.AddRange(e.Evaluate(Obs(m, 8, Hog.Key)));
        Expect(findings.Count(f => f.Kind == EventKind.AppHog) == 1);
    }

    static void ShortSpike()
    {
        var e = new WarningEngine();
        var findings = new List<Finding>();
        for (var m = 0; m < 10; m++) findings.AddRange(e.Evaluate(Obs(m, m == 5 ? 60 : 0.2, Hog.Key)));
        Expect(findings.Count == 0);
    }

    static void BackgroundWarns()
    {
        var e = new WarningEngine();
        var findings = new List<Finding>();
        for (var m = 0; m < 12; m++) findings.AddRange(e.Evaluate(Obs(m, 2)));
        Expect(findings.Any(f => f.Kind == EventKind.BackgroundHog));
    }

    static void NoWarningsOnAdapter()
    {
        var e = new WarningEngine();
        var findings = new List<Finding>();
        for (var m = 0; m < 30; m++) findings.AddRange(e.Evaluate(Obs(m, 40, Hog.Key, PowerSource.Adapter)));
        Expect(findings.Count == 0);
    }

    static void Muted()
    {
        var settings = new WarningSettings();
        settings.Muted.Add(Hog.Key);
        var e = new WarningEngine(settings);
        var findings = new List<Finding>();
        for (var m = 0; m < 30; m++) findings.AddRange(e.Evaluate(Obs(m, 8, Hog.Key)));
        Expect(findings.All(f => f.AppKey != Hog.Key));
    }

    static void BlockerAfterThreshold()
    {
        var e = new WarningEngine();
        var findings = new List<Finding>();
        for (var m = 0; m <= 31; m++) findings.AddRange(e.Evaluate(Obs(m, 0.1, Hog.Key, blockers: new() { [Hog] = "Video Wake Lock" })));
        var f = findings.Single(x => x.Kind == EventKind.SleepBlocker);
        Expect(f.Body.Contains("Video Wake Lock"));
    }

    static void SleepDrain()
    {
        var e = new WarningEngine();
        Expect(e.Evaluate(new SleepRecord(T0, T0.AddHours(8), 80, 66, true)) != null);
        Expect(e.Evaluate(new SleepRecord(T0, T0.AddHours(8), 80, 79, true)) == null);
        Expect(e.Evaluate(new SleepRecord(T0, T0.AddHours(8), 80, 60, false)) == null);
    }

    // MARK: Store

    static Store TempStore() => new(System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"joule-check-{Guid.NewGuid():N}.sqlite"));

    static void ReportSplits()
    {
        using var store = TempStore();
        var now = DateTime.Now;
        var app = new AppIdentity("app:x", "X", null, AppCategory.App);
        var svc = new AppIdentity("system:svc", "svc", null, AppCategory.System);
        for (var i = 0; i < 10; i++)
        {
            var end = now.AddMinutes(-10 + i);
            store.Record(new PowerSample(end.AddSeconds(-60), end, PowerSource.Battery, 80 - i, 10, 40, 80, 0.5, "app:x"),
                new Interval { Start = end.AddSeconds(-60), End = end, Usages = new() { new AppUsage { App = app, Joules = 120, CpuSeconds = 30 }, new AppUsage { App = svc, Joules = 60, CpuSeconds = 10 } } });
        }
        var report = store.Report(new DateRange(now.AddHours(-1), now.AddMinutes(1)), 900, false);
        Expect(Math.Abs(report.Breakdown.Apps - 1200) < 1e-6);
        Expect(Math.Abs(report.Breakdown.Services - 600) < 1e-6);
        Expect(Math.Abs(report.Breakdown.Hardware - (6000 - 1800)) < 1e-6);
        Expect(Math.Abs(report.PercentUsed - 9) < 1e-9);
        Expect(report.Apps[0].App.Key == "app:x" && report.Apps[0].BackgroundShare == 0);
        Expect(Math.Abs(report.TotalJoules - 6000) < 1e-6);
    }

    static void NoticesRoundTrip()
    {
        using var store = TempStore();
        var id = store.AddAlert(EventKind.SleepBlocker, "app:x", "T", "B", 3);
        Expect(store.UnreadAlertCount() == 1);
        var alert = store.Alerts().Single();
        Expect(alert.Kind == EventKind.SleepBlocker && alert.AppKey == "app:x" && alert.Title == "T" && !alert.Read);
        store.MarkAlertsRead();
        Expect(store.UnreadAlertCount() == 0);
        store.DeleteAlert(id);
        Expect(store.Alerts().Count == 0);
    }

    static void DemoPlausible()
    {
        using var store = TempStore();
        DemoData.Fill(store);
        var report = store.Report(Period.Week.Range(), Period.Week.Bucket(), false);
        Expect(!report.IsEmpty && report.Apps.Count > 5);
        Expect(report.AverageWatts > 3 && report.AverageWatts < 40);
        Expect(store.Sessions(DateTime.Now.AddDays(-14)).Count > 3);
        Expect(store.Alerts().Count == 5);
        Expect(store.DayStrips(Period.Week.Range()).Count == 7);
    }

    static void SharesAddUp()
    {
        using var store = TempStore();
        var now = DateTime.Now;
        var app = new AppIdentity("app:x", "X", null, AppCategory.App);
        store.Record(new PowerSample(now.AddMinutes(-121), now.AddMinutes(-120), PowerSource.Adapter, 100, 10, 80, 80, null, null), null);
        double percent = 100;
        for (var i = 0; i < 60; i++)
        {
            var end = now.AddMinutes(-119 + i);
            percent -= 0.25;
            store.Record(new PowerSample(end.AddSeconds(-60), end, PowerSource.Battery, percent, 12, percent * 0.8, 80, 0.8, "app:x"),
                new Interval { Start = end.AddSeconds(-60), End = end, Usages = new() { new AppUsage { App = app, Joules = 240, CpuSeconds = 20 } } });
        }
        store.RecordSleep(new SleepRecord(now.AddMinutes(-59), now.AddMinutes(-20), percent, percent - 2, true));
        percent -= 2;
        for (var i = 0; i < 10; i++)
        {
            var end = now.AddMinutes(-10 + i);
            percent -= 0.25;
            store.Record(new PowerSample(end.AddSeconds(-60), end, PowerSource.Battery, percent, 12, percent * 0.8, 80, 0.8, "app:x"),
                new Interval { Start = end.AddSeconds(-60), End = end, Usages = new() { new AppUsage { App = app, Joules = 240, CpuSeconds = 20 } } });
        }
        var session = store.Sessions(now.AddDays(-1)).First();
        Expect(session.Ongoing);
        var breakdown = store.Breakdown(session);
        var sum = breakdown.Entries.Sum(e => e.Percent);
        Expect(Math.Abs(sum - session.PercentUsed) < 1e-6);
        Expect(breakdown.Entries.Any(e => e.Kind == ShareKind.Sleep && Math.Abs(e.Percent - 2) < 1e-6));
        Expect(!breakdown.StartsAtRecording);
    }

    static SessionBreakdown Fake(double used, List<ShareEntry> entries, double locked = 0, double lockedPercent = 0, double? brightness = null) => new()
    {
        Session = new DischargeSession { Start = T0, End = T0.AddHours(2), PercentStart = 100, PercentEnd = 100 - used, Joules = 10 * 3600, AwakeSeconds = 3600 },
        Entries = entries, LockedSeconds = locked, LockedPercent = lockedPercent, AverageBrightness = brightness,
    };

    static void ExplainsLocked()
    {
        var b = Fake(20, new() { new ShareEntry { Kind = ShareKind.Hardware, Name = "Display & hardware", Percent = 14 }, new ShareEntry { Kind = ShareKind.App, Name = "Chrome", Percent = 6, App = Hog } },
            locked: 3600, lockedPercent: 5, brightness: 0.9);
        var text = string.Join(" ", Narrative.Explain(b, 80));
        Expect(text.Contains("went to the PC itself"));
        Expect(text.Contains("screen was locked"));
        Expect(text.Contains("Brightness averaged"));
        Expect(text.Contains("full charge lasts about"));
        Expect(!text.Contains(".."));
    }

    static void NamesDominantApp()
    {
        var b = Fake(10, new() { new ShareEntry { Kind = ShareKind.App, Name = "Steam", Percent = 8, App = Hog }, new ShareEntry { Kind = ShareKind.Hardware, Name = "Display & hardware", Percent = 2 } });
        var text = Narrative.Explain(b, 80)[0];
        Expect(text.StartsWith("Steam used the most") && text.Contains("most of the drain"));
        Expect(Narrative.Headline(b).Contains("Steam"));
    }

    static void LanesAndStrips()
    {
        using var store = TempStore();
        var start = DateTime.Today.AddHours(9);
        var app = new AppIdentity("app:x", "X", null, AppCategory.App);
        for (var i = 1; i <= 30; i++)
        {
            var end = start.AddMinutes(i);
            var front = i <= 10 ? "app:x" : (i <= 20 ? AppResolver.LockedKey : "app:x");
            store.Record(new PowerSample(end.AddSeconds(-60), end, PowerSource.Battery, 90 - i * 0.2, 10, 40, 80, null, front),
                new Interval { Start = end.AddSeconds(-60), End = end, Usages = new() { new AppUsage { App = app, Joules = 30 } } });
        }
        var session = store.Sessions(start.AddDays(-1)).First();
        var timeline = store.Timeline(session);
        Expect(timeline.Points.Count == 30);
        var lane = timeline.Lanes.First(l => l.Kind == SessionTimeline.LaneKind.App);
        Expect(lane.Blocks.Count == 2 && Math.Abs(lane.Seconds - 1200) < 1e-6);
        Expect(timeline.Lanes.Any(l => l.Kind == SessionTimeline.LaneKind.Locked && Math.Abs(l.Seconds - 600) < 1e-6));
        var strips = store.DayStrips(new DateRange(DateTime.Today, DateTime.Today.AddDays(1)));
        Expect(strips.Count == 1 && strips[0].Blocks.Count == 1 && strips[0].Blocks[0].Kind == StripKind.Battery);
        Expect(Math.Abs(strips[0].PercentUsed - 29 * 0.2) < 1e-6);
    }

    static void RowsSpanningSleep()
    {
        var path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"joule-check-{Guid.NewGuid():N}.sqlite");
        var t = DateTime.Today.AddHours(2);
        using (var store = new Store(path))
        {
            store.RecordSleep(new SleepRecord(t, t.AddHours(5), 80, 78, true));
            store.Record(new PowerSample(t.AddMinutes(-1), t.AddHours(3), PowerSource.Battery, 79, 2, 40, 80, null, null), null);
            store.Record(new PowerSample(t.AddHours(6), t.AddHours(6).AddMinutes(1), PowerSource.Battery, 78, 10, 40, 80, null, null), null);
        }
        using (var store = new Store(path))
        {
            Expect(store.PowerSeries(t.AddDays(-1)).Count == 1);
        }
    }

    static void ChargingAsleep()
    {
        using var store = TempStore();
        var t = DateTime.Now.AddHours(-5);
        void Minute(DateTime at, double percent) => store.Record(new PowerSample(at.AddSeconds(-60), at, PowerSource.Battery, percent, 10, 40, 80, null, null), null);
        for (var i = 0; i < 10; i++) Minute(t.AddMinutes(i), 60 - i);
        // Lid closed, charged on the adapter while asleep, woke up at 95 %.
        for (var i = 0; i < 10; i++) Minute(t.AddHours(3).AddMinutes(i), 95 - i * 0.5);
        var sessions = store.Sessions(t.AddDays(-1));
        Expect(sessions.Count == 2);
        Expect(sessions[0].ChargedUnseen && sessions[0].Ongoing);
    }

    static void PowercfgParses()
    {
        var output = """
            DISPLAY:
            [PROCESS] \Device\HarddiskVolume3\Program Files\Google\Chrome\Application\chrome.exe
            Video Wake Lock

            SYSTEM:
            [DRIVER] Realtek High Definition Audio (HDAUDIO\FUNC_01&VEN_10EC)
            An audio stream is currently in use.
            [SERVICE] \Device\HarddiskVolume3\Windows\System32\svchost.exe (wuauserv)

            AWAYMODE:
            None.

            EXECUTION:
            [PROCESS] \Device\HarddiskVolume3\Program Files\Other\other.exe
            Busy

            PERFBOOST:
            None.
            """;
        var list = PowerRequestProbe.Parse(output, p => p.Replace(@"\Device\HarddiskVolume3", "C:"));
        Expect(list.Count == 3);
        Expect(list[0].Path == @"C:\Program Files\Google\Chrome\Application\chrome.exe" && list[0].Reason == "Video Wake Lock" && list[0].ProcessName == "chrome");
        Expect(list[1].Type == "SYSTEM/DRIVER" && list[1].Reason == "An audio stream is currently in use.");
        Expect(list[2].ProcessName == "wuauserv" && list[2].Reason == "");
    }

    static void PowerModelLearns()
    {
        var m = new PowerModel();
        for (var i = 0; i < 200; i++) m.Learn(14, 5);
        Expect(Math.Abs(m.Baseline - 9) < 0.2);
        Expect(Math.Abs(m.Estimate(20) - 29) < 0.2);
    }

    static void DeviceIds()
    {
        Expect(DeviceProbe.BluetoothAddress(@"BTHLE\DEV_DE600B5716B1\7&15065E3B&0&DE600B5716B1") == "DE600B5716B1");
        Expect(DeviceProbe.BluetoothAddress(@"BTHENUM\DEV_90B68552ACF7\7&FDF2264&0&BLUETOOTHDEVICE_90B68552ACF7") == "90B68552ACF7");
        Expect(DeviceProbe.BluetoothAddress(@"BTHENUM\{00001124-0000-1000-8000-00805F9B34FB}_VID&0002054C_PID&0CE6\7&FDF2264&0&90B68552ACF7_C00000000") == null);
        Expect(DeviceProbe.HidIds(@"\\?\hid#vid_054c&pid_0ce6&mi_03#8&1a2b&0&0000#{4d1e55b2-f16f-11cf-88cb-001111000030}") == (0x054C, 0x0CE6));
        Expect(DeviceProbe.HidIds(@"\\?\hid#{00001124-0000-1000-8000-00805f9b34fb}_vid&0002054c_pid&0ce6#9&2b&0&0000#{4d1e55b2-f16f-11cf-88cb-001111000030}") == (0x054C, 0x0CE6));
        Expect(DeviceProbe.HidIds(@"\\?\hid#acpi0c50") == null);
    }

    static void SonyReports()
    {
        var usb = new byte[64]; usb[0] = 0x01; usb[53] = 0x07;          // DualSense USB, discharging, level 7
        Expect(DeviceProbe.ParseSony(usb, true, false) == (75, false));
        var bt = new byte[78]; bt[0] = 0x31; bt[54] = 0x13;             // DualSense BT, charging, level 3
        Expect(DeviceProbe.ParseSony(bt, true, true) == (35, true));
        bt[54] = 0x2A;                                                   // full
        Expect(DeviceProbe.ParseSony(bt, true, true) == (100, false));
        var simple = new byte[78]; simple[0] = 0x01; simple[54] = 0x05; // basic Bluetooth mode carries no battery
        Expect(DeviceProbe.ParseSony(simple, true, true) == null);
        bt[54] = 0xF5;                                                   // error state
        Expect(DeviceProbe.ParseSony(bt, true, true) == null);
        var ds4 = new byte[64]; ds4[0] = 0x01; ds4[30] = 0x08;          // DualShock 4 USB, on battery
        Expect(DeviceProbe.ParseSony(ds4, false, false) == (85, false));
        ds4[30] = 0x1B;                                                  // cable, full
        Expect(DeviceProbe.ParseSony(ds4, false, false) == (100, false));
        var ds4bt = new byte[78]; ds4bt[0] = 0x11; ds4bt[32] = 0x14;    // DualShock 4 BT, cable, charging
        Expect(DeviceProbe.ParseSony(ds4bt, false, true) == (45, true));
        Expect(DeviceProbe.ParseSony(Array.Empty<byte>(), true, false) == null);
        Expect(DeviceProbe.XInputPercent(3) == 100 && DeviceProbe.XInputPercent(1) <= 20);
    }

    static void DeviceKinds()
    {
        Expect(DeviceKindExtensions.Classify(new[] { "Input.Mouse" }, "Whatever") == DeviceKind.Mouse);
        Expect(DeviceKindExtensions.Classify(new[] { "Input.Gaming" }, "DualSense") == DeviceKind.Controller);
        Expect(DeviceKindExtensions.Classify(new[] { "Audio.Headphone" }, "X") == DeviceKind.Headphones);
        Expect(DeviceKindExtensions.Classify(new[] { "Communication.Phone" }, "X") == DeviceKind.Phone);
        Expect(DeviceKindExtensions.Classify(Array.Empty<string>(), "HP 350/355 Series Keyboard") == DeviceKind.Keyboard);
        Expect(DeviceKindExtensions.Classify(Array.Empty<string>(), "Galaxy Buds2 Pro") == DeviceKind.Headphones);
        Expect(DeviceKindExtensions.Classify(Array.Empty<string>(), "Thing") == DeviceKind.Other);
        Expect(DeviceKindExtensions.ParseDeviceKind(DeviceKind.Controller.Raw()) == DeviceKind.Controller);
    }

    static void DeviceForecastRun()
    {
        var t = new DateTime(2026, 11, 1);
        var levels = new List<DeviceLevel>
        {
            new(t, 40, false), new(t.AddDays(1), 30, false),              // an older discharge
            new(t.AddDays(1.2), 60, true), new(t.AddDays(1.4), 100, false), // charged
            new(t.AddDays(3.4), 90, false), new(t.AddDays(5.4), 80, false),
        };
        Expect(Math.Abs(DeviceForecast.PercentPerDay(levels)!.Value - 5) < 1e-9);
        Expect(Math.Abs(DeviceForecast.DaysLeft(levels, 80)!.Value - 16) < 1e-9);
        Expect(DeviceForecast.PercentPerDay(levels.Take(4).ToList()) == null);                         // charging / too short
        Expect(DeviceForecast.PercentPerDay(new List<DeviceLevel> { new(t, 100, false), new(t.AddHours(3), 95, false) }) == null);
        Expect(DeviceForecast.PercentPerDay(new List<DeviceLevel> { new(t, 100, false), new(t.AddDays(2), 99, false) }) == null);
    }

    static void DeviceLowOnce()
    {
        var now = new DateTime(2026, 11, 1, 12, 0, 0);
        var watch = new DeviceWatch { Threshold = 20 };
        DeviceReading Mouse(double p, bool charging = false, bool connected = true) =>
            new() { Id = "bt:1", Name = "Mouse", Kind = DeviceKind.Mouse, Percent = p, Charging = charging, Connected = connected, Updated = now };
        Expect(watch.Evaluate(new[] { Mouse(30) }, now).Count == 0);
        var first = watch.Evaluate(new[] { Mouse(18) }, now);
        Expect(first.Count == 1 && first[0].Kind == EventKind.DeviceLow && first[0].AppKey == "device:bt:1");
        Expect(watch.Evaluate(new[] { Mouse(12) }, now).Count == 0);
        Expect(watch.Evaluate(new[] { Mouse(40, charging: true) }, now).Count == 0);
        Expect(watch.Evaluate(new[] { Mouse(19) }, now).Count == 1);
        // A stale level of a device that has been away for days stays quiet.
        var away = new DeviceReading { Id = "bt:2", Name = "Old", Percent = 5, Connected = false, Updated = now.AddDays(-3) };
        Expect(watch.Evaluate(new[] { away }, now).Count == 0);
        watch.Muted.Add("device:bt:3");
        Expect(watch.Evaluate(new[] { Mouse(10) with { Id = "bt:3" } }, now).Count == 0);
    }

    static void LogitechReplies()
    {
        byte[] R(params byte[] b) { var r = new byte[16]; b.CopyTo(r, 0); return r; }
        Expect(LogitechProbe.ParseBattery(0x1004, R(72, 4, 0, 0)) == (72, false, false));    // unified: percent, discharging
        Expect(LogitechProbe.ParseBattery(0x1004, R(40, 4, 1, 1)) == (40, true, false));     // charging
        Expect(LogitechProbe.ParseBattery(0x1004, R(90, 8, 3, 1)) == (100, false, false));    // charge complete
        Expect(LogitechProbe.ParseBattery(0x1004, R(0, 2, 0, 0)) == (15, false, true));       // only level flags: low
        Expect(LogitechProbe.ParseBattery(0x1004, R(0, 0, 0, 0)) == null);
        Expect(LogitechProbe.ParseBattery(0x1000, R(50, 20, 0)) == (50, false, false));       // battery status
        Expect(LogitechProbe.ParseBattery(0x1000, R(80, 50, 1)) == (80, true, false));
        Expect(LogitechProbe.ParseBattery(0x1001, R(0x0E, 0xE3, 0x00)) == (50, false, false)); // 3811 mV
        Expect(LogitechProbe.ParseBattery(0x1001, R(0x0F, 0x1E, 0x00)) == (62, false, false)); // 3870 mV, between 60 and 70
        Expect(LogitechProbe.ParseBattery(0x1001, R(0x10, 0x5A, 0x80))!.Value.percent == 100);  // 4186 mV, charging
        Expect(LogitechProbe.VoltagePercent(3400) == 0 && LogitechProbe.VoltagePercent(3811) == 50);
        Expect(LogitechProbe.KindFromType(3) == DeviceKind.Mouse && LogitechProbe.KindFromType(0) == DeviceKind.Keyboard);
    }

    static void DeviceLevelsStore()
    {
        using var store = TempStore();
        var now = new DateTime(2026, 11, 1, 12, 0, 0);
        var mouse = new DeviceReading { Id = "bt:1", Name = "Mouse", Kind = DeviceKind.Mouse, Percent = 90, Connected = true, Updated = now.AddHours(-1) };
        store.RecordDevices(new[] { mouse }, now);
        store.RecordDevices(new[] { mouse }, now.AddMinutes(1));                     // same report again
        store.RecordDevices(new[] { mouse with { Updated = now.AddMinutes(30) } }, now.AddMinutes(31)); // same level, soon after
        store.RecordDevices(new[] { mouse with { Percent = 85, Updated = now.AddHours(2) } }, now.AddHours(2));
        store.RecordDevices(new[] { new DeviceReading { Id = "bt:2", Name = "Speaker", Connected = false } }, now);
        var levels = store.DeviceLevels("bt:1", now.AddDays(-1));
        Expect(levels.Count == 2 && levels[0].Percent == 90 && levels[1].Percent == 85);
        Expect(Math.Abs((levels[0].Date - now.AddHours(-1)).TotalSeconds) < 1);
        var known = store.KnownDevices();
        Expect(known.Count == 2 && known.Any(k => k.Id == "bt:1" && k.Kind == DeviceKind.Mouse));
        store.ForgetDevice("bt:1");
        Expect(store.DeviceLevels("bt:1", now.AddDays(-1)).Count == 0 && store.KnownDevices().Count == 1);
    }

    static void Formatting()
    {
        Format.Culture = System.Globalization.CultureInfo.GetCultureInfo("en-US");
        Expect(Format.Duration(6 * 3600 + 41 * 60) == "6\u00A0hr, 41\u00A0min");
        Expect(Format.Duration(45 * 60) == "45\u00A0min");
        Expect(Format.Duration(79 * 60, true) == "1:19\u00A0h");
        Expect(Format.Watts(12.4) == "12\u00A0W" && Format.Watts(2.94) == "2.9\u00A0W");
        Expect(Format.Percent(0.4) == "< 1\u00A0%");
        Expect(Format.Energy(3600 * 4.8) == "4.8\u00A0Wh");
        Expect(Format.List(new[] { "A", "B", "C" }) == "A, B and C");
        Format.Culture = System.Globalization.CultureInfo.CurrentCulture;
    }
}
