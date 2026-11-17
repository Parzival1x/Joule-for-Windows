using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace Joule.Core;

/// <summary>One process reading. Counters are cumulative since process start.</summary>
public sealed class ProcessReading
{
    public int Pid;
    public int ParentPid;
    /// <summary>0 for services and other session-0 processes.</summary>
    public int SessionId = 1;
    /// <summary>pid + create time identifies a process; pids get reused.</summary>
    public long StartTime;
    public string Path = "";
    public string Name = "";
    public ulong CpuNanos;
    public ulong Cycles;
    public ulong ContextSwitches;
}

public static class ProcessProbe
{
    private static readonly object Gate = new();
    private static IntPtr _buffer = IntPtr.Zero;
    private static int _size = 0;
    private static readonly Dictionary<(int, long), string> PathCache = new();

    /// <summary>
    /// Every process on the machine, including other users' and services'. CPU time and cycles come
    /// from the kernel's process list, which needs no privileges; only the image path of protected
    /// processes stays hidden.
    /// </summary>
    public static List<ProcessReading> ReadAll()
    {
        lock (Gate)
        {
            var result = new List<ProcessReading>(512);
            for (var attempt = 0; attempt < 5; attempt++)
            {
                if (_buffer == IntPtr.Zero) { _size = 2 << 20; _buffer = Marshal.AllocHGlobal(_size); }
                var status = Native.NtQuerySystemInformation(Native.SystemProcessInformation, _buffer, _size, out var needed);
                if (status == unchecked((int)0xC0000004))
                {
                    Marshal.FreeHGlobal(_buffer);
                    _size = Math.Max(needed, _size) + (256 << 10);
                    _buffer = Marshal.AllocHGlobal(_size);
                    continue;
                }
                if (status != 0) return result;
                Parse(result);
                break;
            }
            // Forget paths of processes that are gone.
            if (PathCache.Count > result.Count * 2 + 200)
            {
                var alive = result.Select(r => (r.Pid, r.StartTime)).ToHashSet();
                foreach (var key in PathCache.Keys.Where(k => !alive.Contains(k)).ToList()) PathCache.Remove(key);
            }
            return result;
        }
    }

    private static unsafe void Parse(List<ProcessReading> result)
    {
        var p = (byte*)_buffer;
        while (true)
        {
            var next = *(uint*)p;
            var threads = *(uint*)(p + 4);
            var cycles = *(ulong*)(p + 24);
            var create = *(long*)(p + 32);
            var user = *(long*)(p + 40);
            var kernel = *(long*)(p + 48);
            var nameLength = *(ushort*)(p + 56);
            var nameBuffer = *(IntPtr*)(p + 64);
            var pid = (int)*(IntPtr*)(p + 80);
            var parent = (int)*(IntPtr*)(p + 88);
            var session = (int)*(uint*)(p + 100);
            if (pid > 0)
            {
                ulong switches = 0;
                var thread = p + 256;
                for (var i = 0; i < threads; i++) switches += *(uint*)(thread + i * 80 + 64);
                var name = nameBuffer != IntPtr.Zero && nameLength > 0 ? Marshal.PtrToStringUni(nameBuffer, nameLength / 2) : (pid == 4 ? "System" : $"pid {pid}");
                var path = PathOf(pid, create);
                result.Add(new ProcessReading
                {
                    Pid = pid,
                    ParentPid = parent,
                    SessionId = session,
                    StartTime = create,
                    Path = path,
                    Name = name,
                    CpuNanos = (ulong)Math.Max(0, user + kernel) * 100,
                    Cycles = cycles,
                    ContextSwitches = switches,
                });
            }
            if (next == 0) break;
            p += next;
        }
    }

    private static string PathOf(int pid, long create)
    {
        if (PathCache.TryGetValue((pid, create), out var cached)) return cached;
        var path = "";
        var handle = Native.OpenProcess(Native.PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
        if (handle != IntPtr.Zero)
        {
            var builder = new StringBuilder(1024);
            var size = builder.Capacity;
            if (Native.QueryFullProcessImageNameW(handle, 0, builder, ref size)) path = builder.ToString();
            Native.CloseHandle(handle);
        }
        PathCache[(pid, create)] = path;
        return path;
    }

    public static string? PathOf(int pid)
    {
        var handle = Native.OpenProcess(Native.PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
        if (handle == IntPtr.Zero) return null;
        try
        {
            var builder = new StringBuilder(1024);
            var size = builder.Capacity;
            return Native.QueryFullProcessImageNameW(handle, 0, builder, ref size) ? builder.ToString() : null;
        }
        finally { Native.CloseHandle(handle); }
    }
}

/// <summary>Cumulative CPU energy from the Energy Metering Interface (Intel RAPL and similar), in joules.</summary>
public sealed class EnergyReading
{
    public double PackageJoules;
    /// <summary>Energy of the processor cores alone (pp0), when the meter splits it out.</summary>
    public double? CoreJoules;
    public double? GraphicsJoules;
}

public static class EnergyProbe
{
    private static readonly object Gate = new();
    private static IntPtr _query, _counter;
    private static bool _initialized, _available;
    /// <summary>The Energy counter counts picowatt-hours.</summary>
    public static double JoulesPerUnit = 3.6e-9;

    public static bool Available { get { lock (Gate) { Init(); return _available; } } }

    private static void Init()
    {
        if (_initialized) return;
        _initialized = true;
        if (Native.PdhOpenQueryW(null, IntPtr.Zero, out _query) != 0) return;
        if (Native.PdhAddEnglishCounterW(_query, @"\Energy Meter(*)\Energy", IntPtr.Zero, out _counter) != 0) return;
        _available = Native.PdhCollectQueryData(_query) == 0 && ReadRaw().Count > 0;
    }

    public static Dictionary<string, long> ReadRaw()
    {
        lock (Gate)
        {
            if (_query == IntPtr.Zero) Init();
            if (_counter == IntPtr.Zero) return new();
            Native.PdhCollectQueryData(_query);
            return Pdh.RawArray(_counter);
        }
    }

    public static EnergyReading? Read()
    {
        lock (Gate)
        {
            Init();
            if (!_available) return null;
        }
        var raw = ReadRaw();
        if (raw.Count == 0) return null;
        double package = 0, cores = 0, graphics = 0, other = 0;
        bool hasPackage = false, hasCores = false, hasGraphics = false;
        foreach (var (name, value) in raw)
        {
            var n = name.ToLowerInvariant();
            if (n == "_total") continue;
            var joules = value * JoulesPerUnit;
            if (n.EndsWith("_pkg") || n.Contains("package") && !n.Contains("_pp") && !n.Contains("dram")) { package += joules; hasPackage = true; }
            else if (n.EndsWith("_pp0") || n.Contains("core")) { cores += joules; hasCores = true; }
            else if (n.EndsWith("_pp1") || n.Contains("gpu") || n.Contains("graphics")) { graphics += joules; hasGraphics = true; }
            else if (!n.Contains("dram")) other += joules;
        }
        if (!hasPackage) package = cores + graphics + other;
        return new EnergyReading
        {
            PackageJoules = package,
            CoreJoules = hasCores && cores > 0 ? cores : null,
            GraphicsJoules = hasGraphics ? graphics : null,
        };
    }
}

/// <summary>Cumulative GPU running time per process from the "GPU Engine" performance counters.</summary>
public static class GPUProbe
{
    private static readonly object Gate = new();
    private static IntPtr _query, _counter;
    private static bool _initialized;

    /// <summary>pid → nanoseconds of engine time.</summary>
    public static Dictionary<int, ulong> ReadAll()
    {
        lock (Gate)
        {
            if (!_initialized)
            {
                _initialized = true;
                if (Native.PdhOpenQueryW(null, IntPtr.Zero, out _query) == 0)
                    Native.PdhAddEnglishCounterW(_query, @"\GPU Engine(*)\Running Time", IntPtr.Zero, out _counter);
            }
            var result = new Dictionary<int, ulong>();
            if (_counter == IntPtr.Zero) return result;
            if (Native.PdhCollectQueryData(_query) != 0) return result;
            foreach (var (name, value) in Pdh.RawArray(_counter))
            {
                if (ParsePID(name) is not int pid || value <= 0) continue;
                // Copy engines run alongside the others; counting them would double the time.
                if (name.Contains("engtype_Copy", StringComparison.OrdinalIgnoreCase)) continue;
                result[pid] = result.GetValueOrDefault(pid) + (ulong)value * 100;
            }
            return result;
        }
    }

    /// <summary>"pid_412_luid_0x…_engtype_3D" → 412</summary>
    public static int? ParsePID(string instance)
    {
        if (!instance.StartsWith("pid_", StringComparison.Ordinal)) return null;
        var end = instance.IndexOf('_', 4);
        return int.TryParse(end > 4 ? instance[4..end] : instance[4..], out var pid) ? pid : null;
    }
}

internal static class Pdh
{
    public static Dictionary<string, long> RawArray(IntPtr counter)
    {
        var result = new Dictionary<string, long>();
        var size = 0;
        var status = Native.PdhGetRawCounterArrayW(counter, ref size, out _, IntPtr.Zero);
        if (status != Native.PDH_MORE_DATA || size <= 0) return result;
        var buffer = Marshal.AllocHGlobal(size);
        try
        {
            if (Native.PdhGetRawCounterArrayW(counter, ref size, out var count, buffer) != 0) return result;
            var itemSize = Marshal.SizeOf<Native.PDH_RAW_COUNTER_ITEM>();
            for (var i = 0; i < count; i++)
            {
                var item = Marshal.PtrToStructure<Native.PDH_RAW_COUNTER_ITEM>(buffer + i * itemSize);
                var name = Marshal.PtrToStringUni(item.szName) ?? "";
                // Duplicate instance names (e.g. several engines) add up.
                result[name] = result.GetValueOrDefault(name) + item.RawValue.FirstValue;
            }
        }
        finally { Marshal.FreeHGlobal(buffer); }
        return result;
    }
}

public static class PowerProbe
{
    private static readonly Guid BatteryClass = new("72631e54-78a4-11d0-bcf7-00aa00b7b32a");
    private const uint IOCTL_BATTERY_QUERY_TAG = 0x294040, IOCTL_BATTERY_QUERY_INFORMATION = 0x294044, IOCTL_BATTERY_QUERY_STATUS = 0x29404C;
    private const uint POWER_ON_LINE = 1, DISCHARGING = 2, CHARGING = 4;
    private const uint CAPACITY_RELATIVE = 0x40000000;
    private static readonly object Gate = new();
    private static List<string>? _devices;

    private static List<string> Devices()
    {
        if (_devices != null) return _devices;
        var list = new List<string>();
        var guid = BatteryClass;
        var set = Native.SetupDiGetClassDevsW(ref guid, IntPtr.Zero, IntPtr.Zero, Native.DIGCF_PRESENT | Native.DIGCF_DEVICEINTERFACE);
        if (set == new IntPtr(-1)) return _devices = list;
        try
        {
            for (var i = 0; i < 8; i++)
            {
                var data = new Native.SP_DEVICE_INTERFACE_DATA { cbSize = Marshal.SizeOf<Native.SP_DEVICE_INTERFACE_DATA>() };
                if (!Native.SetupDiEnumDeviceInterfaces(set, IntPtr.Zero, ref guid, i, ref data)) break;
                Native.SetupDiGetDeviceInterfaceDetailW(set, ref data, IntPtr.Zero, 0, out var required, IntPtr.Zero);
                if (required <= 0) continue;
                var detail = Marshal.AllocHGlobal(required);
                try
                {
                    Marshal.WriteInt32(detail, IntPtr.Size == 8 ? 8 : 6);
                    if (Native.SetupDiGetDeviceInterfaceDetailW(set, ref data, detail, required, out _, IntPtr.Zero))
                        list.Add(Marshal.PtrToStringUni(detail + 4) ?? "");
                }
                finally { Marshal.FreeHGlobal(detail); }
            }
        }
        finally { Native.SetupDiDestroyDeviceInfoList(set); }
        return _devices = list.Where(d => d.Length > 0).ToList();
    }

    private sealed class Battery
    {
        public uint PowerState, Capacity, Voltage, Capabilities, Designed, Full, Cycles;
        public int Rate;
        public double? TemperatureC;
        public uint? EstimatedSeconds;
        public string Name = "";
        public bool System => (Capabilities & SYSTEM_BATTERY) != 0;
    }

    private const uint SYSTEM_BATTERY = 0x80000000;

    private static unsafe Battery? ReadDevice(string path)
    {
        var handle = Native.CreateFileW(path, 0xC0000000, 3, IntPtr.Zero, 3, 0x80, IntPtr.Zero);
        if (handle == new IntPtr(-1)) return null;
        try
        {
            uint wait = 0, tag = 0;
            if (!Native.DeviceIoControl(handle, IOCTL_BATTERY_QUERY_TAG, (IntPtr)(&wait), 4, (IntPtr)(&tag), 4, out _, IntPtr.Zero) || tag == 0) return null;
            var battery = new Battery();

            var query = stackalloc uint[3];
            query[0] = tag; query[1] = 0; query[2] = 0;
            var info = stackalloc byte[64];
            if (Native.DeviceIoControl(handle, IOCTL_BATTERY_QUERY_INFORMATION, (IntPtr)query, 12, (IntPtr)info, 36, out _, IntPtr.Zero))
            {
                battery.Capabilities = *(uint*)info;
                battery.Designed = *(uint*)(info + 12);
                battery.Full = *(uint*)(info + 16);
                battery.Cycles = *(uint*)(info + 32);
            }
            query[1] = 2; // temperature, tenths of a kelvin
            uint value = 0;
            if (Native.DeviceIoControl(handle, IOCTL_BATTERY_QUERY_INFORMATION, (IntPtr)query, 12, (IntPtr)(&value), 4, out _, IntPtr.Zero) && value > 2000 && value < 4000)
                battery.TemperatureC = value / 10.0 - 273.15;
            query[1] = 3; // estimated time, seconds
            if (Native.DeviceIoControl(handle, IOCTL_BATTERY_QUERY_INFORMATION, (IntPtr)query, 12, (IntPtr)(&value), 4, out _, IntPtr.Zero) && value != uint.MaxValue && value > 0)
                battery.EstimatedSeconds = value;
            query[1] = 4; // device name
            var name = stackalloc char[128];
            if (Native.DeviceIoControl(handle, IOCTL_BATTERY_QUERY_INFORMATION, (IntPtr)query, 12, (IntPtr)name, 256, out var nameBytes, IntPtr.Zero) && nameBytes > 2)
                battery.Name = new string(name, 0, Math.Min(127, nameBytes / 2)).TrimEnd('\0').Trim();

            var waitStatus = stackalloc uint[5];
            waitStatus[0] = tag;
            var status = stackalloc uint[4];
            if (!Native.DeviceIoControl(handle, IOCTL_BATTERY_QUERY_STATUS, (IntPtr)waitStatus, 20, (IntPtr)status, 16, out _, IntPtr.Zero)) return null;
            battery.PowerState = status[0];
            battery.Capacity = status[1];
            battery.Voltage = status[2];
            battery.Rate = (int)status[3];
            return battery;
        }
        finally { Native.CloseHandle(handle); }
    }

    private static List<Battery> ReadBatteries()
    {
        lock (Gate)
        {
            var batteries = Devices().Select(ReadDevice).Where(b => b != null).Select(b => b!).ToList();
            if (batteries.Count == 0 && _devices is { Count: > 0 }) { _devices = null; batteries = Devices().Select(ReadDevice).Where(b => b != null).Select(b => b!).ToList(); }
            return batteries;
        }
    }

    public static PowerReading? Read(DateTime? now = null)
    {
        var all = ReadBatteries();
        // A UPS also shows up as a battery; the PC's own batteries carry the system flag.
        var batteries = all.Any(b => b.System) ? all.Where(b => b.System).ToList() : all;
        if (batteries.Count == 0) return null;
        Native.GetSystemPowerStatus(out var sps);
        return Combine(batteries.Select(b => (b.PowerState, b.Capacity, b.Voltage, b.Rate, b.Capabilities, b.Designed, b.Full, b.Cycles, b.TemperatureC, b.EstimatedSeconds)).ToList(),
                       sps.ACLineStatus == 1, sps.BatteryLifeTime, now ?? DateTime.Now);
    }

    /// <summary>Batteries that don't power this PC (a UPS on USB), listed with the devices.</summary>
    public static List<DeviceReading> OtherBatteries()
    {
        var all = ReadBatteries();
        if (!all.Any(b => b.System)) return new();
        return all.Where(b => !b.System && b.Full > 0 && b.Capacity != uint.MaxValue).Select((b, i) => new DeviceReading
        {
            Id = $"ups:{(b.Name.Length > 0 ? b.Name : i.ToString())}",
            Name = b.Name.Length > 0 ? b.Name : "UPS",
            Kind = DeviceKind.Ups,
            Percent = Math.Clamp(b.Capacity * 100.0 / b.Full, 0, 100),
            Charging = (b.PowerState & CHARGING) != 0,
            Connected = true,
            Updated = DateTime.Now,
            Via = "USB",
        }).ToList();
    }

    /// <summary>Pure part, testable: combines one or more batteries into one reading.</summary>
    public static PowerReading Combine(List<(uint state, uint capacity, uint voltage, int rate, uint capabilities, uint designed, uint full, uint cycles, double? temperature, uint? estimate)> batteries,
                                       bool acOnline, int systemLifeSeconds, DateTime now)
    {
        double remainingMwh = 0, fullMwh = 0, designMwh = 0, batteryMw = 0, volts = 0;
        int cycles = 0;
        bool online = acOnline, charging = false, relative = false;
        double? temperature = null;
        foreach (var b in batteries)
        {
            relative |= (b.capabilities & CAPACITY_RELATIVE) != 0;
            remainingMwh += b.capacity == uint.MaxValue ? 0 : b.capacity;
            fullMwh += b.full;
            designMwh += b.designed;
            online |= (b.state & POWER_ON_LINE) != 0;
            charging |= (b.state & CHARGING) != 0;
            if (b.rate != int.MinValue)
            {
                var mw = Math.Abs((double)b.rate);
                // Drivers disagree on the sign; the state flags do not.
                if ((b.state & DISCHARGING) != 0) batteryMw -= mw;
                else if ((b.state & CHARGING) != 0) batteryMw += mw;
                else batteryMw += b.rate;
            }
            volts = Math.Max(volts, b.voltage / 1000.0);
            cycles = Math.Max(cycles, (int)b.cycles);
            temperature ??= b.temperature;
        }
        var percent = fullMwh > 0 ? Math.Clamp(remainingMwh / fullMwh * 100, 0, 100) : 0;
        var source = !online ? PowerSource.Battery : (charging ? PowerSource.Charging : PowerSource.Adapter);
        var batteryWatts = batteryMw / 1000;
        var reading = new PowerReading
        {
            Date = now,
            Percent = percent,
            Source = source,
            BatteryWatts = batteryWatts,
            SystemWatts = source == PowerSource.Battery ? Math.Max(0, -batteryWatts) : 0,
            Estimated = source != PowerSource.Battery,
            RemainingWh = relative ? 0 : remainingMwh / 1000,
            FullWh = relative ? 0 : fullMwh / 1000,
            DesignWh = relative ? 0 : designMwh / 1000,
            CycleCount = cycles,
            Voltage = volts,
            TemperatureC = temperature,
        };
        if (source == PowerSource.Battery)
        {
            if (systemLifeSeconds > 0) reading.MinutesToEmpty = systemLifeSeconds / 60;
            else if (reading.SystemWatts > 0.5 && reading.RemainingWh > 0) reading.MinutesToEmpty = (int)(reading.RemainingWh / reading.SystemWatts * 60);
        }
        else if (source == PowerSource.Charging && batteryWatts > 0.5 && reading.FullWh > reading.RemainingWh)
        {
            reading.MinutesToFull = (int)((reading.FullWh - reading.RemainingWh) / batteryWatts * 60);
        }
        return reading;
    }
}

/// <summary>Built-in display brightness 0…1 from the active power plan.</summary>
public static class DisplayProbe
{
    private static Guid _subgroup = new("7516b95f-f776-4464-8c53-06167f40cc99");
    private static Guid _brightness = new("aded5e82-b909-4619-9949-f5d71dac0bcb");

    public static double? Brightness(bool onBattery)
    {
        try
        {
            if (Native.PowerGetActiveScheme(IntPtr.Zero, out var schemePtr) != 0) return null;
            try
            {
                var scheme = Marshal.PtrToStructure<Guid>(schemePtr);
                uint value;
                var rc = onBattery
                    ? Native.PowerReadDCValueIndex(IntPtr.Zero, ref scheme, ref _subgroup, ref _brightness, out value)
                    : Native.PowerReadACValueIndex(IntPtr.Zero, ref scheme, ref _subgroup, ref _brightness, out value);
                return rc == 0 && value <= 100 ? value / 100.0 : null;
            }
            finally { Native.LocalFree(schemePtr); }
        }
        catch { return null; }
    }
}

public static class Clock
{
    /// <summary>Includes time asleep.</summary>
    public static double Continuous() => Native.GetTickCount64() / 1000.0;
    /// <summary>Stops while asleep.</summary>
    public static double Awake() { Native.QueryUnbiasedInterruptTime(out var t); return t / 1e7; }
}

/// <summary>
/// What keeps the PC from sleeping, from <c>powercfg /requests</c>. Windows only answers that
/// for administrators, so this returns null when Joule runs without elevation.
/// </summary>
public static class PowerRequestProbe
{
    public static readonly bool Elevated = Native.IsElevated();
    private static Dictionary<string, string>? _devices;

    public static List<SleepBlocker>? ReadAll()
    {
        if (!Elevated) return null;
        try
        {
            var info = new ProcessStartInfo(Environment.ExpandEnvironmentVariables(@"%SystemRoot%\System32\powercfg.exe"), "/requests")
            {
                RedirectStandardOutput = true, UseShellExecute = false, CreateNoWindow = true,
                StandardOutputEncoding = Encoding.Default,
            };
            using var process = Process.Start(info);
            if (process == null) return null;
            var output = process.StandardOutput.ReadToEnd();
            process.WaitForExit(5000);
            return Parse(output, DosPath);
        }
        catch { return null; }
    }

    /// <summary>Sections that keep the machine (or the display) on.</summary>
    static readonly HashSet<string> Blocking = new(StringComparer.OrdinalIgnoreCase) { "DISPLAY", "SYSTEM", "AWAYMODE" };

    public static List<SleepBlocker> Parse(string output, Func<string, string> dosPath)
    {
        var result = new List<SleepBlocker>();
        string? section = null;
        var lines = output.Replace("\r", "").Split('\n');
        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i].Trim();
            if (line.Length == 0) continue;
            if (line.EndsWith(':') && !line.StartsWith('[') && line.ToUpperInvariant() == line) { section = line.TrimEnd(':'); continue; }
            if (section == null || !Blocking.Contains(section) || !line.StartsWith('[')) continue;
            var close = line.IndexOf(']');
            if (close < 0) continue;
            var type = line[1..close];
            var subject = line[(close + 1)..].Trim();
            var reason = i + 1 < lines.Length && !lines[i + 1].TrimStart().StartsWith('[') ? lines[i + 1].Trim() : "";
            string path = "", name = subject;
            if (type.Equals("PROCESS", StringComparison.OrdinalIgnoreCase) || type.Equals("SERVICE", StringComparison.OrdinalIgnoreCase))
            {
                var exe = subject;
                var paren = subject.IndexOf(" (", StringComparison.Ordinal);
                var service = paren > 0 ? subject[(paren + 2)..].TrimEnd(')') : null;
                if (paren > 0) exe = subject[..paren];
                path = dosPath(exe);
                name = service ?? System.IO.Path.GetFileNameWithoutExtension(path);
            }
            result.Add(new SleepBlocker(0, name, path, reason, $"{section}/{type}"));
        }
        return result;
    }

    /// <summary>"\Device\HarddiskVolume3\Program Files\x.exe" → "C:\Program Files\x.exe"</summary>
    public static string DosPath(string devicePath)
    {
        if (_devices == null)
        {
            _devices = new(StringComparer.OrdinalIgnoreCase);
            var target = new StringBuilder(1024);
            for (var c = 'A'; c <= 'Z'; c++)
            {
                if (Native.QueryDosDeviceW($"{c}:", target, target.Capacity) > 0) _devices[target.ToString()] = $"{c}:";
            }
        }
        foreach (var (device, drive) in _devices)
        {
            if (devicePath.StartsWith(device + "\\", StringComparison.OrdinalIgnoreCase)) return drive + devicePath[device.Length..];
        }
        return devicePath;
    }
}

public static class Foreground
{
    /// <summary>pid of the app in front; UWP apps behind ApplicationFrameHost resolve to their own process.</summary>
    public static int? Pid()
    {
        var hwnd = Native.GetForegroundWindow();
        if (hwnd == IntPtr.Zero) return null;
        Native.GetWindowThreadProcessId(hwnd, out var pid);
        if (pid <= 0) return null;
        var path = ProcessProbe.PathOf(pid) ?? "";
        if (path.EndsWith("ApplicationFrameHost.exe", StringComparison.OrdinalIgnoreCase))
        {
            var inner = pid;
            Native.EnumChildWindows(hwnd, (child, _) =>
            {
                Native.GetWindowThreadProcessId(child, out var childPid);
                if (childPid != pid && childPid > 0) { inner = childPid; return false; }
                return true;
            }, IntPtr.Zero);
            pid = inner;
        }
        return pid;
    }
}
