using System.Windows;
using System.Windows.Threading;
using Joule.Core;
using Microsoft.Win32;

namespace Joule;

/// <summary>An app's draw right now, from the short live interval.</summary>
public sealed record LiveApp(AppIdentity App, double Watts, double GpuShare, bool Background, bool Estimated);

public sealed record Blocker(AppIdentity App, string Reason, DateTime? Since);

/// <summary>Measured process power right now; the rest of the system draw is display & hardware.</summary>
public sealed record LiveSplit(double Apps, double Services)
{
    public double Hardware(double total) => Math.Max(0, total - Apps - Services);
}

/// <summary>Records continuously and publishes live state for the UI.</summary>
public sealed class Monitor
{
    public static Monitor Shared { get; private set; } = null!;

    public PowerReading? Power { get; private set; }
    public List<LiveApp> LiveApps { get; private set; } = new();
    public LiveSplit? LiveSplit { get; private set; }
    /// <summary>Set once a read failed: this PC has no battery.</summary>
    public bool NoBattery { get; private set; }
    public List<Blocker> Blockers { get; private set; } = new();
    /// <summary>Windows only reveals power requests to administrators.</summary>
    public bool BlockersUnavailable => !Demo && !PowerRequestProbe.Elevated;
    public List<(DateTime date, double watts)> RecentPower { get; private set; } = new();
    public Report? Today { get; private set; }
    public SessionBreakdown? CurrentSession { get; private set; }
    public SessionTimeline? CurrentTimeline { get; private set; }
    public int UnreadAlerts { get; private set; }
    public double? Brightness { get; private set; }
    public string? ErrorMessage { get; private set; }
    public int Revision { get; private set; }
    public bool HasEnergyMeter { get; } = EnergyProbe.Available;
    /// <summary>Battery-powered devices: Bluetooth, controllers, UPS. Hidden ones are left out.</summary>
    public List<DeviceReading> Devices { get; private set; } = new();
    public bool DevicesRead { get; private set; }
    /// <summary>Paired devices that are away right now (not shown).</summary>
    public int NotConnectedCount { get; private set; }

    public AppSettings Settings { get; }
    public WarningSettings Warnings => Settings.Warnings;
    public Store? Store { get; }
    public bool Demo { get; }

    /// <summary>Live values changed (every few seconds while someone is looking).</summary>
    public event Action? LiveChanged;
    /// <summary>Stored data changed: pages reload.</summary>
    public event Action? Changed;
    /// <summary>Tray text or badge may have changed.</summary>
    public event Action? TrayChanged;
    public Action<string, string>? Notify;
    /// <summary>Whether a Joule window or the tray panel is on screen.</summary>
    public Func<bool> IsLooking = () => false;
    public bool SessionLocked { get; private set; }

    private PowerReading? _latest;
    private readonly List<(DateTime date, double watts)> _powerBuffer = new();
    private int _unreadCount;
    private bool _stale;
    private bool _liveCaptureRunning, _commitRunning, _blockerReadRunning;
    private readonly AppResolver _resolver = new();
    private readonly Accountant _recordAccountant, _liveAccountant;
    private readonly WarningEngine _warnings;
    private readonly PowerModel _model;
    private readonly List<PowerReading> _readings = new();
    private DateTime _intervalStart = DateTime.Now;
    private (double continuous, double awake) _intervalClock = (Clock.Continuous(), Clock.Awake());
    private (DateTime date, double percent, bool battery)? _sleepStart;
    private readonly List<DispatcherTimer> _timers = new();
    private DispatcherTimer? _liveTimer;
    private PowerSource? _lastSource;
    private (EnergyReading reading, DateTime date)? _lastEnergy;
    private double? _packageWatts;
    private double _lastMeasuredWatts;
    private List<ProcessReading> _lastProcesses = new();
    private Dictionary<int, ProcessReading> _lastPidMap = new();
    private HashSet<string>? _runningKeys;
    private readonly Dictionary<string, DateTime> _blockerSince = new();
    private DateTime _lastBlockerRead = DateTime.MinValue;
    private DateTime _lastDeviceRead = DateTime.MinValue;
    private bool _deviceReadRunning, _deviceReadAgain;
    private List<DeviceReading> _allDevices = new();
    private readonly DeviceWatch _deviceWatch = new();

    public const double RecordInterval = 60, PowerInterval = 10, LiveInterval = 3, DeviceInterval = 20;

    public Monitor(AppSettings settings, bool demo)
    {
        Shared = this;
        Settings = settings;
        Demo = demo;
        _warnings = new WarningEngine(settings.Warnings);
        _recordAccountant = new Accountant(_resolver);
        _liveAccountant = new Accountant(_resolver);
        _model = new PowerModel { Baseline = settings.Baseline, Samples = settings.BaselineSamples };
        try
        {
            if (demo)
            {
                var path = Path.Combine(Path.GetTempPath(), $"joule-demo-{Environment.ProcessId}.sqlite");
                Store = new Store(path);
                DemoData.Fill(Store);
            }
            else Store = new Store();
        }
        catch (Exception e)
        {
            Store = null;
            ErrorMessage = $"History can’t be saved: {e.Message}";
        }
    }

    public void SettingsChanged()
    {
        _warnings.Settings = Settings.Warnings;
        Settings.Save();
        TrayChanged?.Invoke();
        Changed?.Invoke();
    }

    public void Start()
    {
        try { Store?.Prune(); } catch { }
        // The sparkline starts filled after a restart instead of empty.
        try { _powerBuffer.AddRange(Store?.PowerSeries(DateTime.Now.AddMinutes(-30)) ?? new()); } catch { }
        ReadPower();
        if (!Demo)
        {
            _intervalStart = DateTime.Now;
            _intervalClock = (Clock.Continuous(), Clock.Awake());
            Task.Run(Snapshot.Capture).ContinueWith(t => Dispatch(() => { _recordAccountant.Ingest(t.Result); RememberProcesses(t.Result); ReadBlockers(); }));
        }
        RefreshDerived();
        ReadDevices();
        if (!Demo)
        {
            try { WatchDeviceChanges(); } catch { }
            LogitechProbe.LinkChanged += () => Dispatch(() => { _changeSource = "receiver"; DevicesChangedNow(); });
            _ = Task.Run(DeviceProbe.ListenForLinks);
        }
        Schedule(PowerInterval, ReadPower);
        // Every 20 s: catches devices that leave without Windows or a receiver announcing it.
        Schedule(DeviceInterval, ReadDevices);
        if (!Demo)
        {
            Schedule(RecordInterval, () => Commit());
            Schedule(3600, () => { try { Store?.Prune(); } catch { } });
        }
        ObserveSystem();
    }

    private static void Dispatch(Action action) => Application.Current?.Dispatcher.BeginInvoke(action);

    private void Schedule(double seconds, Action body)
    {
        var timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromSeconds(seconds) };
        timer.Tick += (_, _) => body();
        timer.Start();
        _timers.Add(timer);
    }

    // MARK: Live mode while a window or the tray panel is visible

    /// <summary>Starts the 3-second sampling while a Joule window or the panel is on screen, stops it otherwise.</summary>
    public void UpdateLive()
    {
        var looking = IsLooking() && !SessionLocked;
        if (looking && _liveTimer == null)
        {
            if (_latest == null) ReadPower();
            PublishLive();
            RefreshDerived(force: true);
            Task.Run(Snapshot.Capture).ContinueWith(t => Dispatch(() => _liveAccountant.Ingest(t.Result)));
            _liveTimer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromSeconds(LiveInterval) };
            _liveTimer.Tick += (_, _) => LiveTick();
            _liveTimer.Start();
            // A first list right away instead of after three seconds.
            var once = new DispatcherTimer { Interval = TimeSpan.FromSeconds(0.9) };
            once.Tick += (_, _) => { once.Stop(); LiveTick(); };
            once.Start();
        }
        else if (!looking && _liveTimer != null)
        {
            _liveTimer.Stop();
            _liveTimer = null;
            _liveAccountant.Reset();
        }
        else if (looking && _stale)
        {
            RefreshDerived(force: true);
        }
    }

    private void LiveTick()
    {
        if (!IsLooking() || SessionLocked) { UpdateLive(); return; }
        if (_stale) RefreshDerived(force: true);
        ReadPower();
        if (Demo)
        {
            var live = DemoData.Live();
            LiveApps = live.Select((x, i) => new LiveApp(x.app, x.watts, x.gpu, i > 0, false)).ToList();
            LiveSplit = new LiveSplit(LiveApps.Sum(a => a.Watts), 0.7);
            var claude = LiveApps.FirstOrDefault(a => a.App.Name == "Claude");
            if (claude != null) Blockers = new() { new Blocker(claude.App, "Electron", DateTime.Now.AddMinutes(-40)) };
            LiveChanged?.Invoke();
            return;
        }
        if ((DateTime.Now - _lastBlockerRead).TotalSeconds > 30) ReadBlockers();
        if ((DateTime.Now - _lastDeviceRead).TotalSeconds > 8) ReadDevices();
        if (_liveCaptureRunning) return;
        _liveCaptureRunning = true;
        Task.Run(Snapshot.Capture).ContinueWith(t => Dispatch(() =>
        {
            _liveCaptureRunning = false;
            if (_liveTimer != null && t.IsCompletedSuccessfully) ApplyLive(t.Result);
        }));
    }

    private void ApplyLive(Snapshot snapshot)
    {
        RememberProcesses(snapshot);
        var interval = _liveAccountant.Ingest(snapshot);
        if (interval == null) return;
        var front = FrontmostKey();
        var seconds = interval.Duration;
        LiveSplit = new LiveSplit(
            interval.Usages.Where(u => u.App.Category == AppCategory.App).Sum(u => u.Joules) / seconds,
            interval.Usages.Where(u => u.App.Category != AppCategory.App).Sum(u => u.Joules) / seconds);
        LiveApps = interval.Usages
            .Where(u => u.App.Category == AppCategory.App || u.Joules / seconds > 0.3)
            .Take(8)
            .Select(u => new LiveApp(u.App, u.Joules / seconds, Math.Min(1, u.GpuSeconds / seconds), u.App.Key != front, u.EstimatedJoules > u.Joules * 0.5))
            .ToList();
        LiveChanged?.Invoke();
    }

    private void PublishLive()
    {
        Brightness = Demo ? 0.6 : DisplayProbe.Brightness(_latest?.Source == PowerSource.Battery);
        Power = _latest;
        RecentPower = _powerBuffer.ToList();
    }

    // MARK: Recording

    private void ReadPower()
    {
        PowerReading? reading;
        if (Demo) reading = Store != null ? DemoData.Power(Store) : null;
        else
        {
            reading = PowerProbe.Read();
            if (reading != null) ApplyPowerModel(reading);
        }
        if (reading == null) { NoBattery = true; LiveChanged?.Invoke(); return; }
        NoBattery = false;
        _latest = reading;
        _powerBuffer.Add((reading.Date, reading.SystemWatts));
        _powerBuffer.RemoveAll(p => (reading.Date - p.date).TotalMinutes > 30);
        TrayChanged?.Invoke();
        if (IsLooking()) { PublishLive(); LiveChanged?.Invoke(); } else _stale = true;
        if (!Demo)
        {
            _readings.Add(reading);
            if (_lastSource != null && _lastSource != reading.Source) Commit();
        }
        _lastSource = reading.Source;
    }

    /// <summary>On battery the gauge tells the truth and teaches the model; on the adapter the model fills in.</summary>
    private void ApplyPowerModel(PowerReading reading)
    {
        var now = DateTime.Now;
        if (EnergyProbe.Read() is { } energy)
        {
            if (_lastEnergy is { } last)
            {
                var dt = (now - last.date).TotalSeconds;
                var delta = energy.PackageJoules - last.reading.PackageJoules;
                if (dt >= 1 && delta >= 0) _packageWatts = delta / dt;
            }
            _lastEnergy = (energy, now);
        }
        var package = _packageWatts ?? _lastMeasuredWatts;
        if (reading.Source == PowerSource.Battery)
        {
            if (reading.SystemWatts > 0.5 && package > 0)
            {
                _model.Learn(reading.SystemWatts, package);
                if (_model.Samples % 20 == 0)
                {
                    Settings.Baseline = _model.Baseline;
                    Settings.BaselineSamples = _model.Samples;
                    Settings.Save();
                }
            }
            reading.Estimated = false;
        }
        else
        {
            reading.SystemWatts = _model.Estimate(package);
            reading.Estimated = true;
        }
    }

    /// <summary>Closes the running interval and stores it. sync is for going to sleep, where there is no later.</summary>
    private void Commit(bool sync = false)
    {
        if (Store == null || _commitRunning) return;
        var last = _readings.LastOrDefault() ?? _latest;
        if (last == null) return;
        var now = DateTime.Now;
        var taken = _readings.ToList();
        var start = _intervalStart;
        double continuous = Clock.Continuous(), awake = Clock.Awake();
        var asleep = (continuous - _intervalClock.continuous) - (awake - _intervalClock.awake);
        _readings.Clear();
        _intervalStart = now;
        _intervalClock = (continuous, awake);
        // The interval since the last sample was mostly sleep: storing it would count sleep as awake time.
        if (asleep > 20)
        {
            RecordMissedSleep(asleep, taken.FirstOrDefault() ?? last);
            _warnings.Reset();
            _recordAccountant.Reset();
            Task.Run(Snapshot.Capture).ContinueWith(t => Dispatch(() => _recordAccountant.Ingest(t.Result)));
            return;
        }
        if (sync)
        {
            FinishCommit(Snapshot.Capture(), taken, start, now, last);
            return;
        }
        _commitRunning = true;
        Task.Run(Snapshot.Capture).ContinueWith(t => Dispatch(() =>
        {
            _commitRunning = false;
            if (t.IsCompletedSuccessfully) FinishCommit(t.Result, taken, start, now, last);
        }));
    }

    /// <summary>Sleep that came without a suspend notification (modern standby): reconstructed from the clocks.</summary>
    private void RecordMissedSleep(double seconds, PowerReading before)
    {
        if (_sleepStart != null || seconds < 180) return;
        var after = PowerProbe.Read();
        if (after == null) return;
        var end = DateTime.Now;
        var record = new SleepRecord(end.AddSeconds(-seconds), end, before.Percent, after.Percent, before.Source == PowerSource.Battery && after.Source == PowerSource.Battery);
        try { Store?.RecordSleep(record); } catch { }
        if (_warnings.Evaluate(record) is { } finding) Post(finding);
    }

    private void FinishCommit(Snapshot snapshot, List<PowerReading> readings, DateTime start, DateTime now, PowerReading last)
    {
        if (Store == null) return;
        RememberProcesses(snapshot);
        var interval = _recordAccountant.Ingest(snapshot);
        if (interval != null && interval.Duration > 0) _lastMeasuredWatts = interval.MeasuredJoules / interval.Duration;
        var watts = TimeWeightedWatts(readings, start) ?? last.SystemWatts;
        var front = FrontmostKey();
        var sample = new PowerSample(start, now, last.Source, last.Percent, watts, last.RemainingWh, last.FullWh,
            Brightness ?? DisplayProbe.Brightness(last.Source == PowerSource.Battery), front);
        if (sample.Duration <= 5) return;
        try
        {
            Store.Record(sample, interval);
            ErrorMessage = null;
        }
        catch (Exception e)
        {
            ErrorMessage = $"Saving failed: {e.Message}";
        }
        if ((DateTime.Now - _lastBlockerRead).TotalSeconds > 50) ReadBlockers();
        var observation = new Observation
        {
            Date = now, Duration = sample.Duration, Source = sample.Source, Percent = sample.Percent, SystemWatts = watts,
            RemainingWh = last.RemainingWh, FullWh = last.FullWh, Usages = interval?.Usages ?? new(), Frontmost = front,
            Blockers = Blockers.GroupBy(b => b.App).ToDictionary(g => g.Key, g => g.First().Reason),
        };
        foreach (var finding in _warnings.Evaluate(observation)) Post(finding);
        RefreshDerived();
    }

    /// <summary>Live mode reads more often; weight each reading by the time it stood for.</summary>
    public static double? TimeWeightedWatts(List<PowerReading> readings, DateTime start)
    {
        var previous = start;
        double energy = 0, seconds = 0;
        foreach (var reading in readings)
        {
            var dt = Math.Min(PowerInterval * 1.5, Math.Max(0, (reading.Date - previous).TotalSeconds));
            energy += reading.SystemWatts * dt;
            seconds += dt;
            previous = reading.Date;
        }
        return seconds > 0 ? energy / seconds : null;
    }

    private void RememberProcesses(Snapshot snapshot)
    {
        _lastProcesses = snapshot.Processes;
        _lastPidMap = new Dictionary<int, ProcessReading>();
        foreach (var p in snapshot.Processes) _lastPidMap.TryAdd(p.Pid, p);
        _runningKeys = null;
    }

    private void ReadBlockers()
    {
        if (Demo || !PowerRequestProbe.Elevated || _blockerReadRunning) return;
        _blockerReadRunning = true;
        _lastBlockerRead = DateTime.Now;
        Task.Run(PowerRequestProbe.ReadAll).ContinueWith(t => Dispatch(() =>
        {
            _blockerReadRunning = false;
            if (!t.IsCompletedSuccessfully || t.Result == null) return;
            var now = DateTime.Now;
            var list = new List<Blocker>();
            foreach (var request in t.Result)
            {
                AppIdentity app;
                if (request.Type.EndsWith("PROCESS") && request.Path.Length > 0)
                {
                    var process = _lastProcesses.FirstOrDefault(p => string.Equals(p.Path, request.Path, StringComparison.OrdinalIgnoreCase))
                                  ?? new ProcessReading { Path = request.Path, Name = Path.GetFileName(request.Path), SessionId = 1 };
                    app = _resolver.Identity(process, _lastPidMap);
                }
                else app = new AppIdentity($"system:{request.ProcessName}", request.ProcessName, null, AppCategory.System);
                if (list.Any(b => b.App.Key == app.Key)) continue;
                if (!_blockerSince.ContainsKey(app.Key)) _blockerSince[app.Key] = now;
                list.Add(new Blocker(app, request.Reason, _blockerSince[app.Key]));
            }
            foreach (var key in _blockerSince.Keys.Where(k => list.All(b => b.App.Key != k)).ToList()) _blockerSince.Remove(key);
            Blockers = list.OrderBy(b => b.App.Category == AppCategory.App ? 0 : 1).ThenBy(b => b.Since).ToList();
            LiveChanged?.Invoke();
        }));
    }

    // MARK: Devices

    private System.Windows.Interop.HwndSource? _deviceWindow;
    private DispatcherTimer? _deviceSettle;
    private int _deviceSettleStep;

    /// <summary>
    /// Windows broadcasts WM_DEVICECHANGE when a device appears or disappears (a Bluetooth mouse connecting adds its HID
    /// devices, a USB receiver or controller arriving adds its own). A hidden top-level window receives it; the devices are
    /// then read right away and once more a few seconds later, because Bluetooth's "connected" flag can trail the event.
    /// </summary>
    private void WatchDeviceChanges()
    {
        var parameters = new System.Windows.Interop.HwndSourceParameters("Joule device watch") { Width = 0, Height = 0, WindowStyle = 0 };
        _deviceWindow = new System.Windows.Interop.HwndSource(parameters);
        _deviceWindow.AddHook((IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled) =>
        {
            const int WM_DEVICECHANGE = 0x0219, DBT_DEVNODES_CHANGED = 0x0007, DBT_DEVICEARRIVAL = 0x8000, DBT_DEVICEREMOVECOMPLETE = 0x8004;
            if (msg == WM_DEVICECHANGE && (int)wParam is DBT_DEVNODES_CHANGED or DBT_DEVICEARRIVAL or DBT_DEVICEREMOVECOMPLETE) { _changeSource = "windows"; DevicesChangedNow(); }
            return IntPtr.Zero;
        });
    }

    /// <summary>
    /// A device came or went: read at once (a fraction of a second, so the burst of entries one device adds is in),
    /// then again after 2 and 6 seconds, because Bluetooth's "connected" flag can trail the event.
    /// </summary>
    private void DevicesChangedNow()
    {
        LogitechProbe.DevicesChanged();
        _ = Task.Run(DeviceProbe.ListenForLinks);   // a receiver may just have been plugged in
        _deviceSettleStep = 0;
        if (_deviceSettle == null)
        {
            _deviceSettle = new DispatcherTimer(DispatcherPriority.Normal);
            _deviceSettle.Tick += (_, _) =>
            {
                ReadDevices();
                _deviceSettleStep++;
                _deviceSettle!.Interval = TimeSpan.FromSeconds(_deviceSettleStep == 1 ? 2 : 4);
                if (_deviceSettleStep >= 3) _deviceSettle.Stop();
            };
        }
        _deviceSettle.Stop();
        _deviceSettle.Interval = TimeSpan.FromSeconds(0.3);
        _deviceSettle.Start();
    }

    /// <summary>Reads the devices in the background, stores their levels and checks for low batteries.</summary>
    public void ReadDevices()
    {
        _lastDeviceRead = DateTime.Now;
        if (Demo)
        {
            ApplyDevices(DemoData.Devices());
            return;
        }
        // A read already running may have started before the change: read once more as soon as it ends.
        if (_deviceReadRunning) { _deviceReadAgain = true; return; }
        _deviceReadRunning = true;
        Task.Run(DeviceProbe.ReadAll).ContinueWith(t => Dispatch(() =>
        {
            _deviceReadRunning = false;
            if (_deviceReadAgain) { _deviceReadAgain = false; Dispatch(ReadDevices); }
            if (!t.IsCompletedSuccessfully) return;
            try { Store?.RecordDevices(t.Result); } catch { }
            _deviceWatch.Enabled = Warnings.DeviceLow;
            _deviceWatch.Threshold = Warnings.DevicePercent;
            _deviceWatch.Muted = Warnings.Muted;
            _deviceWatch.Fired = Settings.DeviceLowFired;
            var before = string.Join(",", Settings.DeviceLowFired.Order());
            var visible = t.Result.Where(d => !Settings.HiddenDevices.Contains(d.Id));
            foreach (var finding in _deviceWatch.Evaluate(visible, DateTime.Now)) Post(finding);
            if (before != string.Join(",", Settings.DeviceLowFired.Order())) Settings.Save();
            ApplyDevices(t.Result);
        }));
    }

    private void ApplyDevices(List<DeviceReading> devices)
    {
        _allDevices = devices;
        DevicesRead = true;
        // Every device is read and its levels stored, but only the ones connected right now are shown.
        NotConnectedCount = devices.Count(d => !d.Connected && !Settings.HiddenDevices.Contains(d.Id));
        Devices = devices.Where(d => d.Connected && !Settings.HiddenDevices.Contains(d.Id))
            .OrderBy(d => d.Percent == null ? 2 : d.Connected ? 0 : 1)
            .ThenBy(d => d.Kind)
            .ThenBy(d => d.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
        UpdateLowDevices(devices);
        if (!Demo) AnnounceConnections();
        TrayChanged?.Invoke();
        if (IsLooking()) LiveChanged?.Invoke();
    }

    // MARK: Connect / disconnect notifications

    private Dictionary<string, DeviceReading>? _connected;
    private readonly Dictionary<string, (DeviceReading device, DispatcherTimer timer)> _leaving = new();
    /// <summary>
    /// A disconnect is announced once the device has stayed away this long. Just enough to merge the burst of changes
    /// one device makes; a mouse waking up is already covered by the probe keeping its last reading through one missed reply.
    /// </summary>
    private const double DisconnectGrace = 3;
    /// <summary>What triggered the read that is being applied: "windows", "receiver", or a regular poll.</summary>
    private string _changeSource = "poll";

    private void AnnounceConnections()
    {
        var now = Devices.ToDictionary(d => d.Id);
        var before = _connected;
        _connected = now;
        var source = _changeSource;
        _changeSource = "poll";
        if (before == null) return;   // the first read after starting is not news
        foreach (var (id, device) in now)
        {
            if (before.ContainsKey(id)) continue;
            // Back within the grace period: it never really left.
            if (_leaving.Remove(id, out var pending)) { pending.timer.Stop(); LogDevice("back (no notification)", device, source); continue; }
            LogDevice("connected", device, source);
            var level = device.Percent != null ? $"Battery {device.LevelText}{(device.Charging ? ", charging" : "")}." : "It doesn’t report a battery level.";
            NotifyDevice($"{device.Name} connected", level);
        }
        foreach (var (id, device) in before)
        {
            if (now.ContainsKey(id) || _leaving.ContainsKey(id)) continue;
            LogDevice("gone, waiting " + DisconnectGrace + " s", device, source);
            var timer = new DispatcherTimer(DispatcherPriority.Normal) { Interval = TimeSpan.FromSeconds(DisconnectGrace) };
            timer.Tick += (_, _) =>
            {
                timer.Stop();
                if (!_leaving.Remove(id)) return;
                // Still away? (a read may be in flight; the latest list is the truth)
                if (Devices.Any(d => d.Id == id)) { LogDevice("back (no notification)", device, "check"); return; }
                LogDevice("disconnected", device, source);
                var level = device.Percent != null ? $"Battery was {device.LevelText}." : "";
                NotifyDevice($"{device.Name} disconnected", level);
            };
            _leaving[id] = (device, timer);
            timer.Start();
        }
    }

    private void NotifyDevice(string title, string body)
    {
        if (Settings.Notifications && Settings.DeviceNotifications) Notify?.Invoke(title, body.Length > 0 ? body : " ");
    }

    /// <summary>devices.log next to the history: when each device came and went, and what noticed it.</summary>
    private static void LogDevice(string what, DeviceReading device, string source)
    {
        try
        {
            var path = Path.Combine(Store.DefaultDirectory, "devices.log");
            if (File.Exists(path) && new FileInfo(path).Length > 256 * 1024) File.Delete(path);
            File.AppendAllText(path, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}  {what,-28} {device.Name} [{device.Via}, {device.LevelText}]  via {source}\n");
        }
        catch { }
    }

    // MARK: Low devices for the tray icon

    /// <summary>Devices whose battery ran low and that haven't been charged since (kept across restarts).</summary>
    public List<string> LowDeviceNames => Settings.LowDevices.Select(DeviceName).ToList();

    private void UpdateLowDevices(List<DeviceReading> devices)
    {
        var low = Settings.LowDevices;
        var before = string.Join(",", low.Order());
        foreach (var device in devices)
        {
            if (Settings.HiddenDevices.Contains(device.Id) || device.Percent is not double percent) { low.Remove(device.Id); continue; }
            // Low once it reaches the alert level; back to normal only after charging (or clearly above it again).
            if (device.Charging || percent > Warnings.DevicePercent + 5) low.Remove(device.Id);
            else if (percent <= Warnings.DevicePercent) low.Add(device.Id);
        }
        // Unpaired since: no way to see it charged, so let it go.
        low.RemoveWhere(id => devices.All(d => d.Id != id));
        if (before != string.Join(",", low.Order())) Settings.Save();
    }

    /// <summary>Average level of this PC and every connected device with a level, for the tray icon.</summary>
    public double? AverageLevel
    {
        get
        {
            var levels = Devices.Where(d => d.Percent != null).Select(d => d.Percent!.Value).ToList();
            if (Power is { } p && !NoBattery) levels.Add(p.Percent);
            else if (_latest is { } l && !NoBattery) levels.Add(l.Percent);
            return levels.Count > 0 ? levels.Average() : null;
        }
    }

    private readonly Dictionary<string, (DateTime at, double? rate)> _drain = new();

    /// <summary>Percent per day over the device's latest discharge, from its stored levels (cached five minutes).</summary>
    public double? DrainPerDay(string id)
    {
        if (_drain.TryGetValue(id, out var cached) && (DateTime.Now - cached.at).TotalMinutes < 5) return cached.rate;
        double? rate = null;
        try { rate = Store == null ? null : DeviceForecast.PercentPerDay(Store.DeviceLevels(id, DateTime.Now.AddDays(-30))); } catch { }
        _drain[id] = (DateTime.Now, rate);
        return rate;
    }

    /// <summary>"≈ 17 days left", "≈ 20 hours left" or null when there's too little history.</summary>
    public string? TimeLeft(DeviceReading device)
    {
        if (device.Charging || device.Coarse || device.Percent is not double percent || DrainPerDay(device.Id) is not double rate || rate <= 0) return null;
        var days = percent / rate;
        if (days >= 1.5) return $"≈ {Format.Number(Math.Min(days, 365), 0)} days left";
        var hours = days * 24;
        return hours >= 1 ? $"≈ {Format.Number(hours, 0)} hours left" : "less than an hour left";
    }

    /// <summary>This PC on battery below the low-battery level counts as low too.</summary>
    public bool PcLow => _latest is { } l && l.Source == PowerSource.Battery && l.Percent <= Warnings.LowBatteryPercent;

    public void HideDevice(string id, bool hidden)
    {
        if (hidden) Settings.HiddenDevices.Add(id); else Settings.HiddenDevices.Remove(id);
        Settings.Save();
        ApplyDevices(_allDevices);
        Changed?.Invoke();
    }

    /// <summary>Name of a device for alerts and settings, also when it is no longer around.</summary>
    public string DeviceName(string id) =>
        _allDevices.FirstOrDefault(d => d.Id == id)?.Name
        ?? (Store?.KnownDevices().FirstOrDefault(d => d.Id == id)?.Name)
        ?? (Demo ? DemoData.Devices().FirstOrDefault(d => d.Id == id)?.Name : null)
        ?? id;

    private string? FrontmostKey()
    {
        if (SessionLocked) return AppResolver.LockedKey;
        var pid = Foreground.Pid();
        if (pid is not int id) return null;
        if (!_lastPidMap.TryGetValue(id, out var process))
        {
            var path = ProcessProbe.PathOf(id) ?? "";
            process = new ProcessReading { Pid = id, Path = path, Name = Path.GetFileName(path), SessionId = 1 };
        }
        if (process.Name.Equals("LockApp.exe", StringComparison.OrdinalIgnoreCase)) return AppResolver.LockedKey;
        if (process.Pid == Environment.ProcessId) return _resolver.Identity(process, _lastPidMap).Key;
        return _resolver.Identity(process, _lastPidMap).Key;
    }

    /// <summary>Reloads summaries; skipped (and marked stale) while no window is visible.</summary>
    public void RefreshDerived(bool force = false)
    {
        if (Store == null) return;
        try { _unreadCount = Store.UnreadAlertCount(); } catch { }
        UnreadAlerts = _unreadCount;
        TrayChanged?.Invoke();
        if (!force && !IsLooking()) { _stale = true; return; }
        _stale = false;
        PublishLive();
        try
        {
            Today = Store.Report(Period.Today.Range(), Period.Today.Bucket(), false);
            var session = Store.Sessions(DateTime.Now.AddDays(-30)).FirstOrDefault();
            if (session != null)
            {
                CurrentSession = Store.Breakdown(session);
                CurrentTimeline = Store.Timeline(session);
            }
            else
            {
                CurrentSession = null;
                CurrentTimeline = null;
            }
        }
        catch (Exception e) { ErrorMessage = $"Reading history failed: {e.Message}"; }
        Revision++;
        Changed?.Invoke();
        LiveChanged?.Invoke();
    }

    // MARK: Alerts

    private void Post(Finding finding)
    {
        try { Store?.AddAlert(finding.Kind, finding.AppKey, finding.Title, finding.Body, finding.Value); } catch { }
        _unreadCount++;
        UnreadAlerts = _unreadCount;
        TrayChanged?.Invoke();
        if (Settings.Notifications) Notify?.Invoke(finding.Title, finding.Body);
    }

    public void MarkAlertsRead()
    {
        try { Store?.MarkAlertsRead(); } catch { }
        UnreadAlerts = 0;
        _unreadCount = 0;
        TrayChanged?.Invoke();
    }

    private HashSet<string> RunningKeys()
    {
        if (_runningKeys != null) return _runningKeys;
        if (_lastProcesses.Count == 0) RememberProcesses(new Snapshot { Processes = ProcessProbe.ReadAll() });
        _runningKeys = _lastProcesses.Select(p => _resolver.Identity(p, _lastPidMap).Key).ToHashSet();
        return _runningKeys;
    }

    public bool IsRunning(AppIdentity app) => !Demo && app.Category == AppCategory.App && RunningKeys().Contains(app.Key);

    /// <summary>Asks the app to close its windows normally (it may still ask to save documents).</summary>
    public void Quit(AppIdentity app)
    {
        var pids = _lastProcesses.Where(p => _resolver.Identity(p, _lastPidMap).Key == app.Key).Select(p => p.Pid).ToHashSet();
        if (pids.Count == 0) return;
        WindowCloser.CloseWindowsOf(pids);
        _runningKeys = null;
    }

    public void Mute(string key)
    {
        Warnings.Muted.Add(key);
        SettingsChanged();
    }

    public void Unmute(string key)
    {
        Warnings.Muted.Remove(key);
        SettingsChanged();
    }

    public void EraseAll()
    {
        try { Store?.EraseAll(); } catch { }
        _powerBuffer.Clear();
        RefreshDerived(force: true);
    }

    // MARK: Sleep, wake, power source, lock

    private void ObserveSystem()
    {
        SystemEvents.PowerModeChanged += (_, e) => Application.Current?.Dispatcher.Invoke(() =>
        {
            switch (e.Mode)
            {
                case PowerModes.Suspend: WillSleep(); break;
                case PowerModes.Resume: DidWake(); break;
                case PowerModes.StatusChange: ReadPower(); break;
            }
        });
        SystemEvents.SessionSwitch += (_, e) => Application.Current?.Dispatcher.BeginInvoke(() =>
        {
            if (e.Reason is SessionSwitchReason.SessionLock or SessionSwitchReason.ConsoleDisconnect or SessionSwitchReason.RemoteDisconnect) SessionLocked = true;
            if (e.Reason is SessionSwitchReason.SessionUnlock or SessionSwitchReason.ConsoleConnect or SessionSwitchReason.RemoteConnect) SessionLocked = false;
            UpdateLive();
        });
    }

    private void WillSleep()
    {
        if (Demo) return;
        Commit(sync: true);
        if (_latest != null) _sleepStart = (DateTime.Now, _latest.Percent, _latest.Source == PowerSource.Battery);
    }

    private void DidWake()
    {
        if (Demo) return;
        _recordAccountant.Reset();
        _liveAccountant.Reset();
        _warnings.Reset();
        _readings.Clear();
        _lastEnergy = null;
        _packageWatts = null;
        _intervalStart = DateTime.Now;
        _intervalClock = (Clock.Continuous(), Clock.Awake());
        Task.Run(Snapshot.Capture).ContinueWith(t => Dispatch(() =>
        {
            _recordAccountant.Ingest(t.Result);
            if (_liveTimer != null) _liveAccountant.Ingest(t.Result);
        }));
        var start = _sleepStart;
        _sleepStart = null;
        var now = PowerProbe.Read();
        if (start is { } s && now != null)
        {
            var record = new SleepRecord(s.date, DateTime.Now, s.percent, now.Percent, s.battery && now.Source == PowerSource.Battery);
            if (record.Hours > 0.05)
            {
                try { Store?.RecordSleep(record); } catch { }
                if (_warnings.Evaluate(record) is { } finding) Post(finding);
            }
        }
        ReadPower();
        RefreshDerived();
    }
}

internal static class WindowCloser
{
    public static void CloseWindowsOf(HashSet<int> pids)
    {
        var windows = new List<IntPtr>();
        NativeUser.EnumWindows((hwnd, _) =>
        {
            NativeUser.GetWindowThreadProcessId(hwnd, out var pid);
            if (pids.Contains(pid) && NativeUser.IsWindowVisible(hwnd) && NativeUser.GetWindow(hwnd, 4) == IntPtr.Zero) windows.Add(hwnd);
            return true;
        }, IntPtr.Zero);
        foreach (var hwnd in windows) NativeUser.PostMessageW(hwnd, 0x0010, IntPtr.Zero, IntPtr.Zero);
    }
}

internal static class NativeUser
{
    public delegate bool EnumWindowsProc(IntPtr hwnd, IntPtr lParam);
    [System.Runtime.InteropServices.DllImport("user32.dll")] public static extern bool EnumWindows(EnumWindowsProc callback, IntPtr lParam);
    [System.Runtime.InteropServices.DllImport("user32.dll")] public static extern int GetWindowThreadProcessId(IntPtr hwnd, out int pid);
    [System.Runtime.InteropServices.DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr hwnd);
    [System.Runtime.InteropServices.DllImport("user32.dll")] public static extern IntPtr GetWindow(IntPtr hwnd, uint cmd);
    [System.Runtime.InteropServices.DllImport("user32.dll")] public static extern bool PostMessageW(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);
    [System.Runtime.InteropServices.DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr hwnd);
}
