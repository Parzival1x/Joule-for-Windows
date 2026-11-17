namespace Joule.Core;

/// <summary>
/// Whole-machine power on the adapter. The battery gauge only sees the load while discharging;
/// plugged in, Joule adds a baseline it learned on battery (display, memory, radios …) to the CPU
/// package power it can still measure.
/// </summary>
public sealed class PowerModel
{
    public double Baseline = 6.0;
    public int Samples;

    /// <summary>On battery: learns what the machine needs beyond the CPU package.</summary>
    public void Learn(double systemWatts, double packageWatts)
    {
        var residual = systemWatts - packageWatts;
        if (residual < 0.3 || residual > 80 || systemWatts <= 0) return;
        var alpha = Math.Max(0.02, 1.0 / (Samples + 2));
        Baseline = Baseline * (1 - alpha) + residual * alpha;
        Samples++;
    }

    public double Estimate(double packageWatts) => Math.Max(0, packageWatts) + Baseline;
}

/// <summary>Synthetic but plausible two weeks for --demo, so the UI can be judged without days of recording.</summary>
public static class DemoData
{
    private sealed record DemoApp(AppIdentity Identity, double Watts, double Gpu, int From, int To, string[] Members);

    private static DemoApp App(string name, string key, string? path, AppCategory category, double watts, double gpu = 0, int from = 8, int to = 18, params string[] members) =>
        new(new AppIdentity(key, name, path, category), watts, gpu, from, to, members.Length == 0 ? new[] { name } : members);

    private static readonly string Local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
    private static readonly string Roaming = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);

    private static DemoApp[] Apps => new[]
    {
        App("Google Chrome", @"app:c:\program files\google\chrome", @"C:\Program Files\Google\Chrome\Application\chrome.exe", AppCategory.App, 1.6, 0.08, 8, 18, "chrome.exe", "chrome.exe (renderer)", "chrome.exe (GPU)"),
        App("Claude", $@"app:{Local.ToLowerInvariant()}\anthropicclaude", $@"{Local}\AnthropicClaude\claude.exe", AppCategory.App, 1.1, 0.03, 8, 18, "claude.exe", "claude.exe (renderer)", "node.exe"),
        App("Microsoft Outlook", @"app:c:\program files\microsoft office\root\office16\outlook.exe", @"C:\Program Files\Microsoft Office\root\Office16\OUTLOOK.EXE", AppCategory.App, 0.7, 0, 7, 19),
        App("Photos", @"app:c:\program files\windowsapps\microsoft.windows.photos", @"C:\Program Files\WindowsApps\Microsoft.Windows.Photos\Photos.exe", AppCategory.App, 0.5, 0, 8, 20),
        App("ChatGPT", @"app:c:\program files\windowsapps\openai.chatgpt-desktop", @"C:\Program Files\WindowsApps\OpenAI.ChatGPT-Desktop\ChatGPT.exe", AppCategory.App, 0.6, 0, 8, 18, "ChatGPT.exe", "msedgewebview2.exe"),
        App("Spotify", $@"app:{Roaming.ToLowerInvariant()}\spotify", $@"{Roaming}\Spotify\Spotify.exe", AppCategory.App, 0.35, 0, 9, 12),
        App("Steam", @"app:c:\program files (x86)\steam", @"C:\Program Files (x86)\Steam\steam.exe", AppCategory.App, 4.5, 0.55, 20, 22, "steam.exe", "steamwebhelper.exe", "game.exe"),
        App("Microsoft Teams", @"app:c:\program files\windowsapps\msteams", @"C:\Program Files\WindowsApps\MSTeams\ms-teams.exe", AppCategory.App, 0.15, 0, 7, 20),
        App("WhatsApp", @"app:c:\program files\windowsapps\whatsapp", @"C:\Program Files\WindowsApps\WhatsApp\WhatsApp.exe", AppCategory.App, 0.1, 0, 7, 22),
        App("SearchIndexer", "system:SearchIndexer", null, AppCategory.System, 0.25, 0, 0, 23),
        App("MsMpEng", "system:MsMpEng", null, AppCategory.System, 0.45, 0, 0, 23),
        App("node", "tool:node", @"C:\Program Files\nodejs\node.exe", AppCategory.Tool, 0.8, 0, 9, 17),
    };

    public static void Fill(Store store, DateTime? nowOrNull = null)
    {
        var now = nowOrNull ?? DateTime.Now;
        var rng = new Random(7);
        var apps = Apps;
        var t = now.AddDays(-13).Date;
        var percent = 92.0;
        var source = PowerSource.Adapter;
        const double fullWh = 84.0;
        (DateTime start, double before)? sleepFrom = null;
        while (t < now)
        {
            var hour = t.Hour;
            var weekend = t.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday;
            var day = (int)(t.Date - DateTime.MinValue).TotalDays;
            var awake = weekend ? hour >= 10 && hour <= 23 : hour >= 7 && hour <= 23 && !(hour == 12 && day % 3 == 0);
            if (!awake)
            {
                sleepFrom ??= (t, percent);
                // Sleeping: lose a little, one night something keeps it busy.
                percent -= source == PowerSource.Battery ? (day % 5 == 2 ? 1.6 : 0.12) / 60 : 0;
                t = t.AddSeconds(60);
                continue;
            }
            if (sleepFrom is { } s)
            {
                store.RecordSleep(new SleepRecord(s.start, t, s.before, percent, source == PowerSource.Battery));
                sleepFrom = null;
            }
            // Plugged in mornings at the desk and when low.
            if (percent < 22 || (hour == 7 && day % 2 == 0)) source = PowerSource.Charging;
            if (source == PowerSource.Charging && percent >= 99) source = PowerSource.Adapter;
            if (source == PowerSource.Adapter && (hour == 9 || hour == 14 || hour == 19) && rng.NextDouble() < 0.05) source = PowerSource.Battery;

            var usages = new List<AppUsage>();
            var busy = 0.6 + 0.8 * rng.NextDouble();
            foreach (var demo in apps.Where(a => hour >= a.From && hour <= a.To))
            {
                if (demo.Identity.Name == "Steam" && day % 4 != 1) continue;
                var w = demo.Watts * busy * (0.4 + 1.2 * rng.NextDouble());
                var members = new Dictionary<string, double>();
                for (var i = 0; i < demo.Members.Length; i++) members[demo.Members[i]] = w * 60 / (i + 1) / 1.8;
                usages.Add(new AppUsage
                {
                    App = demo.Identity, Joules = w * 60, CpuSeconds = w * 9, GpuSeconds = demo.Gpu * 60 * busy, IdleWakeups = w * 900, Members = members,
                });
            }
            var measured = usages.Sum(u => u.Joules);
            var @base = 3.2 + 2.5 * (hour > 19 ? 0.6 : 1.0) + rng.NextDouble() * 1.5;
            var gpuDraw = usages.Sum(u => u.GpuSeconds) / 60 * 14;
            var watts = measured / 60 + @base + gpuDraw;
            if (source == PowerSource.Battery) percent -= watts / 60 / fullWh * 100;
            if (source == PowerSource.Charging) percent += 1.1;
            percent = Math.Clamp(percent, 3, 100);
            var end = t.AddSeconds(60);
            var front = usages.Where(u => u.App.Category == AppCategory.App).OrderByDescending(u => u.Joules).FirstOrDefault()?.App.Key;
            store.Record(new PowerSample(t, end, source, percent, watts, percent / 100 * fullWh, fullWh, 0.6, front),
                new Interval { Start = t, End = end, Usages = usages });
            t = end;
        }
        var alerts = new (double offset, EventKind kind, string? app, string title, string body)[]
        {
            (-2 * 3600, EventKind.AppHog, @"app:c:\program files (x86)\steam", "Steam is using a lot of energy",
                $"Averaging {Format.Watts(9.8)} for {Format.Duration(300)} – about {Format.Percent(12)} battery per hour."),
            (-5 * 3600, EventKind.BackgroundHog, @"app:c:\program files\windowsapps\openai.chatgpt-desktop", "ChatGPT is working in the background",
                $"You’re not using the app right now, but it has been drawing {Format.Watts(2.3)} on average for {Format.Duration(600)}."),
            (-20 * 3600, EventKind.SleepDrain, null, "Battery drained during sleep",
                $"In {Format.Duration(8 * 3600 + 40 * 60)} of sleep, {Format.Percent(14)} was lost ({Format.Percent(1.6, 1)} per hour). Usually it’s well under {Format.Percent(0.5, 1)} per hour."),
            (-27 * 3600, EventKind.SleepBlocker, $@"app:{Local.ToLowerInvariant()}\anthropicclaude", "Claude is keeping your PC awake",
                $"For {Format.Duration(45 * 60)}, Claude has been preventing sleep (“Electron”)."),
            (-50 * 3600, EventKind.HighDrain, null, "High power draw",
                $"Your PC is drawing {Format.Watts(34)} on average – at this rate the battery lasts about {Format.Duration(110 * 60)}. Biggest consumer: Steam."),
        };
        foreach (var a in alerts) store.AddAlert(a.kind, a.app, a.title, a.body, 0, now.AddSeconds(a.offset));
        FillDevices(store, now);
    }

    /// <summary>Sample devices: a slowly draining mouse, headphones charged every few days, controllers, a phone.</summary>
    public static List<DeviceReading> Devices(DateTime? nowOrNull = null)
    {
        var now = nowOrNull ?? DateTime.Now;
        return new List<DeviceReading>
        {
            new() { Id = "demo:mouse", Name = "Wireless Mouse", Kind = DeviceKind.Mouse, Percent = 64, Connected = true, Updated = now.AddMinutes(-20), Via = "Bluetooth" },
            new() { Id = "demo:headphones", Name = "Noise-cancelling Headphones", Kind = DeviceKind.Headphones, Percent = 18, Connected = true, Updated = now.AddMinutes(-5), Via = "Bluetooth" },
            new() { Id = "demo:keyboard", Name = "Low-profile Keyboard", Kind = DeviceKind.Keyboard, Percent = 91, Connected = true, Updated = now.AddHours(-2), Via = "Bluetooth" },
            new() { Id = "demo:dualsense", Name = "DualSense Wireless Controller", Kind = DeviceKind.Controller, Percent = 45, Charging = true, Connected = true, Updated = now, Via = "USB" },
            new() { Id = "demo:xbox", Name = "Xbox Wireless Controller", Kind = DeviceKind.Controller, Percent = 60, Coarse = true, Connected = true, Updated = now, Via = "Xbox wireless" },
            new() { Id = "demo:phone", Name = "Android Phone", Kind = DeviceKind.Phone, Percent = 81, Connected = true, Updated = now.AddMinutes(-3), Via = "Bluetooth" },
            new() { Id = "demo:speaker", Name = "Bluetooth Speaker", Kind = DeviceKind.Speaker, Connected = true, Via = "Bluetooth" },
            new() { Id = "demo:earbuds", Name = "Wireless Earbuds", Kind = DeviceKind.Headphones, Percent = 70, Connected = false, Updated = now.AddDays(-3), Via = "Bluetooth" },
        };
    }

    private static void FillDevices(Store store, DateTime now)
    {
        var rng = new Random(7);
        foreach (var device in Devices(now))
        {
            if (device.Percent is not double current || device.Coarse) { store.RecordDevices(new[] { device }, now); continue; }
            // Walk back from today's level: steady drain per day, a charge whenever it ran low.
            var perDay = device.Kind switch { DeviceKind.Mouse => 4.0, DeviceKind.Headphones => 22, DeviceKind.Keyboard => 1.5, DeviceKind.Phone => 35, _ => 30 };
            var points = new List<(DateTime t, double p, bool charging)>();
            var level = current;
            for (var t = now.AddHours(-1); t > now.AddDays(-30); t = t.AddHours(-3 - rng.NextDouble() * 3))
            {
                level += perDay / 6 * (0.6 + rng.NextDouble() * 0.8);
                if (level > 100) { points.Add((t, 40 + rng.NextDouble() * 20, true)); level = 10 + rng.NextDouble() * 15; continue; }
                points.Add((t, Math.Round(level), false));
            }
            points.Reverse();
            foreach (var (t, p, charging) in points)
                store.RecordDevices(new[] { device with { Percent = p, Charging = charging, Updated = t, Connected = false } }, t);
            store.RecordDevices(new[] { device }, now);
        }
    }

    public static List<(AppIdentity app, double watts, double gpu)> Live()
    {
        var rng = Random.Shared;
        return Apps.Take(6).Select(a => (a.Identity, a.Watts * (0.4 + 0.5 * rng.NextDouble()), a.Gpu)).ToList();
    }

    /// <summary>A live reading that matches the sample history, so demo screenshots show no real values.</summary>
    public static PowerReading? Power(Store store, DateTime? nowOrNull = null)
    {
        if (store.LatestPower() is not { } last) return null;
        var watts = Math.Max(3, last.watts + (Random.Shared.NextDouble() * 2.4 - 1.2));
        const double fullWh = 81.5;
        var remaining = last.percent / 100 * fullWh;
        var onBattery = last.source == PowerSource.Battery;
        return new PowerReading
        {
            Date = nowOrNull ?? DateTime.Now, Percent = last.percent, Source = last.source, SystemWatts = watts, Estimated = !onBattery,
            BatteryWatts = onBattery ? -watts : 18, RemainingWh = remaining, FullWh = fullWh, DesignWh = 84, CycleCount = 87, Voltage = 16.42,
            MinutesToEmpty = onBattery ? (int)(remaining / watts * 60) : null, MinutesToFull = last.source == PowerSource.Charging ? 40 : null,
        };
    }
}
