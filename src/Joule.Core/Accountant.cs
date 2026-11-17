using System.Diagnostics;

namespace Joule.Core;

public sealed class Snapshot
{
    public DateTime Date;
    public double Continuous;
    public double Awake;
    public List<ProcessReading> Processes = new();
    /// <summary>pid → cumulative GPU engine nanoseconds.</summary>
    public Dictionary<int, ulong> Gpu = new();
    public EnergyReading? Energy;

    public static Snapshot Capture() => new()
    {
        Processes = ProcessProbe.ReadAll(),
        Gpu = GPUProbe.ReadAll(),
        Energy = EnergyProbe.Read(),
        Date = DateTime.Now,
        Continuous = Clock.Continuous(),
        Awake = Clock.Awake(),
    };
}

/// <summary>
/// Turns cumulative per-process counters into per-app deltas.
///
/// Windows has no per-process energy counter, but it has a measured CPU energy counter (RAPL)
/// and exact per-process CPU cycles. The cores' energy of each interval is shared out by cycles,
/// so a process that ran at high clocks pays more than one that idled along at the same CPU time.
/// </summary>
public sealed class Accountant
{
    private readonly record struct Key(int Pid, long Start);

    private Snapshot? _previous;
    private Dictionary<Key, ProcessReading> _previousByKey = new();
    private Dictionary<Key, ulong> _previousGpu = new();
    private readonly AppResolver _resolver;

    /// <summary>Joules per CPU-second, used only when the machine has no readable energy meter.</summary>
    public double FallbackJoulesPerCpuSecond = 1.5;

    public Accountant(AppResolver? resolver = null) { _resolver = resolver ?? new AppResolver(); }

    /// <summary>Forget the baseline, e.g. on sleep, so no delta spans the boundary.</summary>
    public void Reset()
    {
        _previous = null;
        _previousByKey = new();
        _previousGpu = new();
    }

    /// <summary>Returns null for the first snapshot and whenever the interval is not trustworthy.</summary>
    public Interval? Ingest(Snapshot snapshot)
    {
        var byKey = new Dictionary<Key, ProcessReading>(snapshot.Processes.Count);
        var gpuByKey = new Dictionary<Key, ulong>();
        foreach (var p in snapshot.Processes)
        {
            var key = new Key(p.Pid, p.StartTime);
            byKey.TryAdd(key, p);
            if (snapshot.Gpu.TryGetValue(p.Pid, out var gpu)) gpuByKey[key] = gpu;
        }
        var previous = _previous;
        var previousByKey = _previousByKey;
        var previousGpu = _previousGpu;
        _previous = snapshot;
        _previousByKey = byKey;
        _previousGpu = gpuByKey;
        if (previous == null) return null;

        var wall = (snapshot.Date - previous.Date).TotalSeconds;
        var continuous = snapshot.Continuous - previous.Continuous;
        var awake = snapshot.Awake - previous.Awake;
        // Sleep in between, a clock change or a long stall: start over.
        if (!(wall > 0.5 && continuous > 0.5 && continuous - awake < 2 && Math.Abs(wall - continuous) < 5 && continuous < 15 * 60)) return null;

        // CPU energy of this interval, if the meter can be read and did not wrap.
        double? coreJoules = null, packageJoules = null;
        if (snapshot.Energy is { } energyNow && previous.Energy is { } energyBefore)
        {
            var package = energyNow.PackageJoules - energyBefore.PackageJoules;
            if (package >= 0 && package < 500 * wall) packageJoules = package;
            if (energyNow.CoreJoules is double c1 && energyBefore.CoreJoules is double c0 && c1 >= c0 && c1 - c0 < 500 * wall) coreJoules = c1 - c0;
            else coreJoules = packageJoules;
        }

        var pidMap = new Dictionary<int, ProcessReading>(snapshot.Processes.Count);
        foreach (var p in snapshot.Processes) pidMap.TryAdd(p.Pid, p);
        _resolver.Prime(snapshot.Processes, pidMap);

        var deltas = new List<(ProcessReading process, double cpu, double cycles, double gpu, double switches)>();
        double totalCycles = 0, totalCpu = 0;
        foreach (var (key, now) in byKey)
        {
            if (!previousByKey.TryGetValue(key, out var before)) continue;
            if (now.CpuNanos < before.CpuNanos || now.Cycles < before.Cycles) continue;
            var cpu = (now.CpuNanos - before.CpuNanos) / 1e9;
            var cycles = (double)(now.Cycles - before.Cycles);
            var gpuNow = gpuByKey.GetValueOrDefault(key);
            var gpuBefore = previousGpu.TryGetValue(key, out var g) ? g : gpuNow;
            var gpu = gpuNow >= gpuBefore ? (gpuNow - gpuBefore) / 1e9 : 0;
            var switches = now.ContextSwitches >= before.ContextSwitches ? (double)(now.ContextSwitches - before.ContextSwitches) : 0;
            if (cpu <= 0 && cycles <= 0 && gpu <= 0) continue;
            deltas.Add((now, cpu, cycles, gpu, switches));
            totalCycles += cycles;
            totalCpu += cpu;
        }

        var grouped = new Dictionary<string, AppUsage>();
        foreach (var (process, cpu, cycles, gpu, switches) in deltas)
        {
            double joules, estimated = 0;
            if (coreJoules is double energy)
            {
                var share = totalCycles > 0 ? cycles / totalCycles : (totalCpu > 0 ? cpu / totalCpu : 0);
                joules = energy * share;
            }
            else
            {
                joules = cpu * FallbackJoulesPerCpuSecond;
                estimated = joules;
            }
            var app = _resolver.Identity(process, pidMap);
            if (!grouped.TryGetValue(app.Key, out var usage)) grouped[app.Key] = usage = new AppUsage { App = app };
            usage.Joules += joules;
            usage.EstimatedJoules += estimated;
            usage.CpuSeconds += cpu;
            usage.GpuSeconds += gpu;
            usage.IdleWakeups += switches;
            usage.Members[process.Name] = usage.Members.GetValueOrDefault(process.Name) + joules;
        }
        return new Interval
        {
            Start = previous.Date,
            End = snapshot.Date,
            Usages = grouped.Values.OrderByDescending(u => u.Joules).ToList(),
            PackageJoules = packageJoules,
        };
    }
}

/// <summary>What Joule needs to know about an executable file.</summary>
public sealed record ExeInfo(string? Description, string? Product, bool IsConsole);

/// <summary>Maps a process to the app it belongs to.</summary>
public sealed class AppResolver
{
    private readonly object _lock = new();
    private readonly Dictionary<string, (AppIdentity identity, bool fromMain)> _cache = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string?> _roots = new(StringComparer.OrdinalIgnoreCase);
    private readonly Func<string, ExeInfo> _exeInfo;
    private readonly string _windows, _programFiles, _programFilesX86, _localAppData, _roamingAppData, _programData;

    public AppResolver(Func<string, ExeInfo>? exeInfo = null, string? windows = null, string? programFiles = null, string? programFilesX86 = null,
                       string? localAppData = null, string? roamingAppData = null, string? programData = null)
    {
        _exeInfo = exeInfo ?? ReadExeInfo;
        _windows = Norm(windows ?? Environment.GetFolderPath(Environment.SpecialFolder.Windows));
        _programFiles = Norm(programFiles ?? Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles));
        _programFilesX86 = Norm(programFilesX86 ?? Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86));
        _localAppData = Norm(localAppData ?? Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData));
        _roamingAppData = Norm(roamingAppData ?? Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData));
        _programData = Norm(programData ?? Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData));
    }

    private static string Norm(string path) => path.TrimEnd('\\');

    /// <summary>Embedded browsers and crash reporters work for the process that started them.</summary>
    public static readonly HashSet<string> HelperNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "msedgewebview2.exe", "crashpad_handler.exe", "CefSharp.BrowserSubprocess.exe", "QtWebEngineProcess.exe", "WerFault.exe",
    };

    /// <summary>Programs in the Windows folder that people use as apps, not services.</summary>
    public static readonly HashSet<string> WindowsApps = new(StringComparer.OrdinalIgnoreCase)
    {
        "explorer.exe", "notepad.exe", "mspaint.exe", "Taskmgr.exe", "SnippingTool.exe", "mmc.exe", "regedit.exe", "write.exe",
        "SystemSettings.exe", "control.exe", "magnify.exe", "osk.exe", "mstsc.exe", "WindowsTerminal.exe",
    };

    /// <summary>Folders that hold several unrelated products: the product folder below them is the app.</summary>
    private static readonly HashSet<string> VendorFolders = new(StringComparer.OrdinalIgnoreCase)
    {
        "Microsoft", "Google", "Common Files", "Intel", "Intel Corporation", "NVIDIA Corporation", "AMD", "Adobe", "JetBrains",
        "Mozilla", "Apple", "Logitech", "Realtek", "Dell", "HP", "Lenovo", "ASUS", "Razer", "Corsair", "Programs", "Packages", "Microsoft SDKs",
    };

    /// <summary>Suites whose programs are separate apps even though they share a folder.</summary>
    private static readonly HashSet<string> PerExeFolders = new(StringComparer.OrdinalIgnoreCase) { "Microsoft Office", "Windows Kits" };

    public const string LockedKey = "lock:screen";

    public static string Strip(string name) => name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? name[..^4] : name;

    /// <summary>Learns app names from each app's main process (the one not started by its own app).</summary>
    public void Prime(IEnumerable<ProcessReading> processes, Dictionary<int, ProcessReading> pidMap)
    {
        foreach (var p in processes)
        {
            if (p.Path.Length == 0 || p.SessionId == 0) continue;
            var root = Root(p.Path);
            if (root == null) continue;
            lock (_lock)
            {
                if (_cache.TryGetValue(root, out var cached) && cached.fromMain) continue;
            }
            if (IsMain(p, root, pidMap)) Identity(p, pidMap);
        }
    }

    private bool IsMain(ProcessReading process, string root, Dictionary<int, ProcessReading> pidMap)
    {
        if (!pidMap.TryGetValue(process.ParentPid, out var parent) || parent.StartTime > process.StartTime) return true;
        return parent.Path.Length == 0 || !string.Equals(Root(parent.Path), root, StringComparison.OrdinalIgnoreCase);
    }

    public AppIdentity Identity(ProcessReading process, Dictionary<int, ProcessReading> processes)
    {
        // 1. Embedded browsers and helpers: walk up to the app that started them.
        if (HelperNames.Contains(process.Name))
        {
            var current = process;
            for (var depth = 0; depth < 8; depth++)
            {
                if (!processes.TryGetValue(current.ParentPid, out var parent) || parent.StartTime > current.StartTime || parent.Pid == current.Pid) break;
                if (!HelperNames.Contains(parent.Name))
                {
                    var owner = Identity(parent, processes);
                    if (owner.Category == AppCategory.App) return owner;
                    break;
                }
                current = parent;
            }
        }
        var path = process.Path;
        var name = Strip(process.Name);
        // 2. Services and protected processes.
        if (path.Length == 0 || process.SessionId == 0) return new AppIdentity($"system:{name}", name, null, AppCategory.System);
        // 3. The Windows folder: services, except the handful of programs people open themselves.
        if (path.StartsWith(_windows + "\\", StringComparison.OrdinalIgnoreCase))
        {
            if (!WindowsApps.Contains(process.Name)) return new AppIdentity($"system:{name}", name, null, AppCategory.System);
            return App(path.ToLowerInvariant(), path, process, processes, rootIsExe: true);
        }
        // 4. Console programs stand on their own.
        if (Info(path).IsConsole) return new AppIdentity($"tool:{name}", name, path, AppCategory.Tool);
        // 5. Everything in one install folder is one app.
        var root = Root(path) ?? path;
        return App(root, path, process, processes, rootIsExe: string.Equals(root, path, StringComparison.OrdinalIgnoreCase));
    }

    private AppIdentity App(string root, string path, ProcessReading process, Dictionary<int, ProcessReading> processes, bool rootIsExe)
    {
        lock (_lock)
        {
            var main = rootIsExe || IsMain(process, root, processes);
            if (_cache.TryGetValue(root, out var cached) && (cached.fromMain || !main)) return cached.identity;
            var identity = new AppIdentity($"app:{root.ToLowerInvariant()}", DisplayName(path, root), path, AppCategory.App);
            _cache[root] = (identity, main);
            return identity;
        }
    }

    /// <summary>The install folder an executable belongs to, or the exe itself for suites; null for the Windows folder.</summary>
    public string? Root(string path)
    {
        lock (_lock)
        {
            if (_roots.TryGetValue(path, out var cached)) return cached;
        }
        var root = ComputeRoot(path);
        lock (_lock) { _roots[path] = root; }
        return root;
    }

    private string? ComputeRoot(string path)
    {
        if (path.StartsWith(_windows + "\\", StringComparison.OrdinalIgnoreCase)) return null;
        var bases = new[] { _programFiles, _programFilesX86, _localAppData, _roamingAppData, _programData }
            .Where(b => b.Length > 0).OrderByDescending(b => b.Length);
        foreach (var @base in bases)
        {
            if (!path.StartsWith(@base + "\\", StringComparison.OrdinalIgnoreCase)) continue;
            var parts = path[(@base.Length + 1)..].Split('\\');
            if (parts.Length <= 1) return path;
            var first = parts[0];
            if (PerExeFolders.Contains(first)) return path;
            // Packaged apps: the package folder is the app.
            if (first.Equals("WindowsApps", StringComparison.OrdinalIgnoreCase))
                return parts.Length > 2 ? $"{@base}\\{first}\\{parts[1]}" : path;
            if (VendorFolders.Contains(first) && parts.Length > 2)
            {
                if (PerExeFolders.Contains(parts[1])) return path;
                return $"{@base}\\{first}\\{parts[1]}";
            }
            return $"{@base}\\{first}";
        }
        return System.IO.Path.GetDirectoryName(path) ?? path;
    }

    private string DisplayName(string path, string root)
    {
        var info = Info(path);
        static bool Usable(string? s) => !string.IsNullOrWhiteSpace(s) && s.Trim().Length <= 48 && !s.Trim().EndsWith(".exe", StringComparison.OrdinalIgnoreCase);
        if (Usable(info.Description)) return info.Description!.Trim();
        if (Usable(info.Product)) return info.Product!.Trim();
        var folder = System.IO.Path.GetFileName(root.TrimEnd('\\'));
        if (folder.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) return Strip(folder);
        // "Microsoft.WindowsCalculator_11.2405.2.0_x64__8wekyb3d8bbwe" → "WindowsCalculator"
        var underscore = folder.IndexOf('_');
        if (underscore > 0) folder = folder[..underscore];
        var dot = folder.LastIndexOf('.');
        return dot > 0 && dot < folder.Length - 1 ? folder[(dot + 1)..] : folder;
    }

    private readonly Dictionary<string, ExeInfo> _infos = new(StringComparer.OrdinalIgnoreCase);

    private ExeInfo Info(string path)
    {
        lock (_lock)
        {
            if (_infos.TryGetValue(path, out var cached)) return cached;
        }
        ExeInfo info;
        try { info = _exeInfo(path); } catch { info = new ExeInfo(null, null, false); }
        lock (_lock) { _infos[path] = info; }
        return info;
    }

    public static ExeInfo ReadExeInfo(string path)
    {
        string? description = null, product = null;
        try
        {
            var version = FileVersionInfo.GetVersionInfo(path);
            description = version.FileDescription;
            product = version.ProductName;
        }
        catch { }
        return new ExeInfo(description, product, IsConsoleExe(path));
    }

    /// <summary>Reads the PE header: subsystem 3 is the console.</summary>
    public static bool IsConsoleExe(string path)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            var header = new byte[1024];
            var read = stream.Read(header, 0, header.Length);
            if (read < 0x40 || header[0] != 'M' || header[1] != 'Z') return false;
            var pe = BitConverter.ToInt32(header, 0x3C);
            if (pe <= 0 || pe + 24 + 70 > read) return false;
            var subsystem = BitConverter.ToUInt16(header, pe + 24 + 68);
            return subsystem == 3;
        }
        catch { return false; }
    }
}
