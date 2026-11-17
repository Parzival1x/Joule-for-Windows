using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Win32.SafeHandles;

namespace Joule.Core;

public enum DeviceKind { Mouse, Keyboard, Headphones, Speaker, Controller, Phone, Pen, Watch, Ups, Other }

public static class DeviceKindExtensions
{
    public static string Raw(this DeviceKind kind) => kind.ToString().ToLowerInvariant();
    public static DeviceKind ParseDeviceKind(string? raw) => Enum.TryParse<DeviceKind>(raw, true, out var kind) ? kind : DeviceKind.Other;

    public static string Label(this DeviceKind kind) => kind switch
    {
        DeviceKind.Mouse => "Mouse",
        DeviceKind.Keyboard => "Keyboard",
        DeviceKind.Headphones => "Headphones",
        DeviceKind.Speaker => "Speaker",
        DeviceKind.Controller => "Game controller",
        DeviceKind.Phone => "Phone",
        DeviceKind.Pen => "Pen",
        DeviceKind.Watch => "Watch",
        DeviceKind.Ups => "Uninterruptible power supply",
        _ => "Bluetooth device",
    };

    /// <summary>From the Windows device-container category (e.g. "Input.Mouse"), falling back to the name.</summary>
    public static DeviceKind Classify(IEnumerable<string> categories, string name)
    {
        foreach (var category in categories)
        {
            var c = category.ToLowerInvariant();
            if (c.Contains("mouse") || c.Contains("touchpad")) return DeviceKind.Mouse;
            if (c.Contains("keyboard")) return DeviceKind.Keyboard;
            if (c.Contains("gaming") || c.Contains("gamepad") || c.Contains("joystick")) return DeviceKind.Controller;
            if (c.Contains("headphone") || c.Contains("headset") || c.Contains("earbud") || c.Contains("handsfree")) return DeviceKind.Headphones;
            if (c.Contains("speaker")) return DeviceKind.Speaker;
            if (c.Contains("phone")) return DeviceKind.Phone;
            if (c.Contains("pen")) return DeviceKind.Pen;
            if (c.Contains("watch")) return DeviceKind.Watch;
        }
        var n = name.ToLowerInvariant();
        if (n.Contains("mouse") || n.Contains("trackball") || n.Contains("mx master") || n.Contains("trackpad")) return DeviceKind.Mouse;
        if (n.Contains("keyboard") || n.Contains("keys")) return DeviceKind.Keyboard;
        if (n.Contains("controller") || n.Contains("gamepad") || n.Contains("dualsense") || n.Contains("dualshock") || n.Contains("joy-con")) return DeviceKind.Controller;
        if (n.Contains("buds") || n.Contains("pods") || n.Contains("headphone") || n.Contains("headset") || n.Contains("wh-") || n.Contains("wf-") || n.Contains("earbud")) return DeviceKind.Headphones;
        if (n.Contains("speaker") || n.Contains("soundlink") || n.Contains("jbl")) return DeviceKind.Speaker;
        if (n.Contains("phone") || n.Contains("galaxy s") || n.Contains("pixel")) return DeviceKind.Phone;
        if (n.Contains("pen") || n.Contains("stylus")) return DeviceKind.Pen;
        if (n.Contains("watch")) return DeviceKind.Watch;
        return DeviceKind.Other;
    }
}

/// <summary>One battery-powered device as Windows (or the device itself) reports it right now.</summary>
public sealed record DeviceReading
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public DeviceKind Kind { get; init; }
    /// <summary>Null when the device does not report its battery to Windows.</summary>
    public double? Percent { get; init; }
    public bool Connected { get; init; }
    public bool Charging { get; init; }
    /// <summary>When the level was last updated (Bluetooth keeps the last value after disconnecting).</summary>
    public DateTime? Updated { get; init; }
    /// <summary>"Bluetooth", "USB", "Xbox wireless", "UPS".</summary>
    public string Via { get; init; } = "Bluetooth";
    /// <summary>The device only reports steps (full / medium / low / empty).</summary>
    public bool Coarse { get; init; }
    internal Guid? Container { get; init; }

    public string LevelText => Percent is not double p ? "–"
        : Coarse ? p switch { >= 90 => "Full", >= 50 => "Medium", >= 10 => "Low", _ => "Empty" }
        : Format.Percent(p);
}

/// <summary>A stored battery level of a device.</summary>
public readonly record struct DeviceLevel(DateTime Date, double Percent, bool Charging);

/// <summary>Reads battery levels of Bluetooth devices, PlayStation and Xbox controllers and UPS units.</summary>
public static class DeviceProbe
{
    private static readonly object Gate = new();

    public static List<DeviceReading> ReadAll()
    {
        lock (Gate)
        {
            var devices = new List<DeviceReading>();
            try { devices.AddRange(Bluetooth()); } catch { }
            try { MergeControllers(devices, SonyControllers()); } catch { }
            try { MergeXbox(devices, XboxControllers()); } catch { }
            try { devices.AddRange(LogitechProbe.Read(DeviceSet.Interfaces(HidInterface))); } catch { }
            try { devices.AddRange(PowerProbe.OtherBatteries()); } catch { }
            return devices;
        }
    }

    /// <summary>Starts (or extends) listening for Logitech receivers' connect / disconnect announcements.</summary>
    public static void ListenForLinks()
    {
        try { LogitechProbe.Listen(DeviceSet.Interfaces(HidInterface).Select(i => i.path)); } catch { }
    }

    public static void DiagnoseLogitechNotifications(Action<string> log) => LogitechProbe.DiagnoseNotifications(DeviceSet.Interfaces(HidInterface).Select(i => i.path), log);

    public static void DiagnoseLogitech(Action<string> log) => LogitechProbe.Diagnose(DeviceSet.Interfaces(HidInterface).Select(i => i.path), log);

    // MARK: Bluetooth: Windows keeps the level the device reports through its Battery Service (LE) or hands-free profile (classic).

    private static readonly DevKey FriendlyName = new("a45c254e-df1c-4efd-8020-67d146a850e0", 14);
    private static readonly DevKey DeviceName = new("b725f130-47ef-101a-a5f1-02608c9ebac0", 10);
    private static readonly DevKey InstanceId = new("78c34fc8-104a-4aca-9ea4-524d52996e57", 256);
    private static readonly DevKey ContainerId = new("8c7ed206-3f8a-4827-b3ab-ae9e1faefc6c", 2);
    private static readonly DevKey ContainerCategory = new("78c34fc8-104a-4aca-9ea4-524d52996e57", 90);
    private static readonly DevKey BatteryLevel = new("104ea319-6ee2-4701-bd47-8ddbf425bbe5", 2);
    private static readonly DevKey BatteryUpdated = new("104ea319-6ee2-4701-bd47-8ddbf425bbe5", 7);
    private static readonly DevKey IsConnected = new("83da6326-97a6-4088-9453-a1923f573b29", 15);

    internal static List<DeviceReading> Bluetooth()
    {
        var byAddress = new Dictionary<string, DeviceReading>();
        foreach (var enumerator in new[] { "BTHLE", "BTHENUM" })
        {
            DeviceSet.Each(enumerator, (set, info) =>
            {
                var instance = DeviceSet.String(set, info, InstanceId) ?? "";
                var address = BluetoothAddress(instance);
                if (address == null) return;
                var name = DeviceSet.String(set, info, FriendlyName) ?? DeviceSet.String(set, info, DeviceName) ?? "Bluetooth device";
                var level = DeviceSet.Byte(set, info, BatteryLevel);
                var categories = DeviceSet.StringList(set, info, ContainerCategory);
                var reading = new DeviceReading
                {
                    Id = $"bt:{address}",
                    Name = name.Trim(),
                    Kind = DeviceKindExtensions.Classify(categories, name),
                    Percent = level is byte b && b <= 100 ? b : null,
                    Updated = DeviceSet.FileTime(set, info, BatteryUpdated),
                    Connected = DeviceSet.Bool(set, info, IsConnected) ?? false,
                    Via = "Bluetooth",
                    Container = DeviceSet.Guid(set, info, ContainerId),
                };
                // Dual-mode devices appear twice (classic and LE): keep the one that knows the level.
                if (byAddress.TryGetValue(address, out var existing))
                {
                    if (existing.Percent != null && reading.Percent == null) reading = reading with { Percent = existing.Percent, Updated = existing.Updated };
                    reading = reading with { Connected = reading.Connected || existing.Connected };
                }
                byAddress[address] = reading;
            });
        }
        return byAddress.Values.ToList();
    }

    /// <summary>"BTHLE\DEV_DE600B5716B1\7&amp;..." → "DE600B5716B1". Service nodes ("BTHENUM\{...}") are skipped.</summary>
    public static string? BluetoothAddress(string instance)
    {
        var match = Regex.Match(instance, @"^BTH(?:LE|ENUM)\\DEV_([0-9A-F]{12})\\", RegexOptions.IgnoreCase);
        return match.Success ? match.Groups[1].Value.ToUpperInvariant() : null;
    }

    // MARK: PlayStation controllers: the level is part of every input report.

    private static readonly Guid HidInterface = new("4d1e55b2-f16f-11cf-88cb-001111000030");
    private const ushort Sony = 0x054C;
    private static readonly Dictionary<ushort, (string name, bool dualSense)> SonyModels = new()
    {
        [0x0CE6] = ("DualSense Wireless Controller", true),
        [0x0DF2] = ("DualSense Edge Wireless Controller", true),
        [0x05C4] = ("DUALSHOCK 4 Wireless Controller", false),
        [0x09CC] = ("DUALSHOCK 4 Wireless Controller", false),
        [0x0BA0] = ("DUALSHOCK 4 USB Wireless Adapter", false),
    };

    /// <summary>Vendor and product from a HID interface path, USB ("vid_054c&amp;pid_0ce6") or Bluetooth ("_vid&amp;0002054c_pid&amp;0ce6").</summary>
    public static (ushort vid, ushort pid)? HidIds(string path)
    {
        var match = Regex.Match(path, @"vid[_&](?:000[12])?([0-9a-f]{4})[_&]pid[_&]([0-9a-f]{4})", RegexOptions.IgnoreCase);
        if (!match.Success) return null;
        return (Convert.ToUInt16(match.Groups[1].Value, 16), Convert.ToUInt16(match.Groups[2].Value, 16));
    }

    /// <summary>
    /// Battery from a PlayStation input report. DualSense: USB report 0x01 and Bluetooth report 0x31 carry it at
    /// byte 53 / 54 (low nibble 0–10, high nibble 0 discharging, 1 charging, 2 full). DualShock 4: USB report 0x01 at byte 30,
    /// Bluetooth report 0x11 at byte 32 (low nibble level, bit 4 cable). The short Bluetooth report 0x01 has no battery.
    /// </summary>
    public static (double percent, bool charging)? ParseSony(ReadOnlySpan<byte> report, bool dualSense, bool bluetooth)
    {
        if (report.Length == 0) return null;
        if (dualSense)
        {
            int index = bluetooth ? (report[0] == 0x31 ? 54 : -1) : (report[0] == 0x01 ? 53 : -1);
            if (index < 0 || report.Length <= index) return null;
            var status = report[index];
            var level = status & 0x0F;
            var state = status >> 4;
            return state switch
            {
                0 => (Math.Min(100, level * 10 + 5), false),
                1 => (Math.Min(100, level * 10 + 5), true),
                2 => (100, false),
                _ => null,
            };
        }
        else
        {
            int index = bluetooth ? (report[0] == 0x11 ? 32 : -1) : (report[0] == 0x01 ? 30 : -1);
            if (index < 0 || report.Length <= index) return null;
            var status = report[index];
            var level = status & 0x0F;
            var cable = (status & 0x10) != 0;
            if (!cable) return (Math.Min(100, level * 10 + 5), false);
            return level >= 11 ? (100, false) : (Math.Min(100, level * 10 + 5), true);
        }
    }

    private static List<DeviceReading> SonyControllers()
    {
        var result = new List<DeviceReading>();
        foreach (var (path, container) in DeviceSet.Interfaces(HidInterface))
        {
            if (HidIds(path) is not { } ids || ids.vid != Sony || !SonyModels.TryGetValue(ids.pid, out var model)) continue;
            var bluetooth = path.Contains("{00001124-", StringComparison.OrdinalIgnoreCase);
            // A controller has several HID interfaces on USB; only the gamepad one sends the full report.
            if (result.Any(r => r.Container == container && container != null && r.Percent != null)) continue;
            var level = ReadSony(path, model.dualSense, bluetooth);
            if (level == null && result.Any(r => r.Container == container && container != null)) continue;
            result.RemoveAll(r => r.Container == container && container != null);
            result.Add(new DeviceReading
            {
                Id = $"hid:{(container ?? Guid.Empty):N}",
                Name = model.name,
                Kind = DeviceKind.Controller,
                Percent = level?.percent,
                Charging = level?.charging ?? false,
                Connected = true,
                Updated = level != null ? DateTime.Now : null,
                Via = bluetooth ? "Bluetooth" : "USB",
                Container = container,
            });
        }
        return result;
    }

    /// <summary>Only reads: never switches the controller's report mode, which would break games that use the basic mode.</summary>
    private static (double percent, bool charging)? ReadSony(string path, bool dualSense, bool bluetooth)
    {
        var handle = Native.CreateFileW(path, 0x80000000, 3, IntPtr.Zero, 3, 0x40000000, IntPtr.Zero);
        if (handle == new IntPtr(-1)) return null;
        using var safe = new SafeFileHandle(handle, true);
        try
        {
            using var stream = new FileStream(safe, FileAccess.Read, 0, true);
            var buffer = new byte[1024];
            for (var attempt = 0; attempt < 4; attempt++)
            {
                using var timeout = new CancellationTokenSource(250);
                int read;
                try { read = stream.ReadAsync(buffer.AsMemory(), timeout.Token).AsTask().GetAwaiter().GetResult(); }
                catch { return null; }
                if (read <= 0) return null;
                if (ParseSony(buffer.AsSpan(0, read), dualSense, bluetooth) is { } level) return level;
            }
        }
        catch { }
        return null;
    }

    private static void MergeControllers(List<DeviceReading> devices, List<DeviceReading> controllers)
    {
        foreach (var controller in controllers)
        {
            var index = controller.Container is Guid c ? devices.FindIndex(d => d.Container == c) : -1;
            if (index >= 0)
            {
                var paired = devices[index];
                devices[index] = paired with
                {
                    Kind = DeviceKind.Controller, Connected = true,
                    Percent = controller.Percent ?? paired.Percent, Charging = controller.Charging,
                    Updated = controller.Percent != null ? controller.Updated : paired.Updated,
                };
            }
            else devices.Add(controller);
        }
    }

    // MARK: Xbox controllers: XInput reports four steps.

    [StructLayout(LayoutKind.Sequential)] private struct XInputBattery { public byte Type, Level; }
    [DllImport("xinput1_4.dll")] private static extern uint XInputGetBatteryInformation(uint user, byte deviceType, out XInputBattery info);

    /// <summary>XInput level (0 empty … 3 full) as a percent that sorts and compares sensibly.</summary>
    public static double XInputPercent(byte level) => level switch { 0 => 5, 1 => 15, 2 => 60, _ => 100 };

    private static List<DeviceReading> XboxControllers()
    {
        var result = new List<DeviceReading>();
        for (uint user = 0; user < 4; user++)
        {
            if (XInputGetBatteryInformation(user, 0, out var info) != 0) continue;
            // 0 disconnected, 1 wired (no battery), 2 alkaline, 3 NiMH, 255 unknown.
            if (info.Type is 0 or 1) continue;
            result.Add(new DeviceReading
            {
                Id = $"xinput:{user}", Name = "Xbox Wireless Controller", Kind = DeviceKind.Controller,
                Percent = info.Type == 255 ? null : XInputPercent(info.Level), Coarse = true, Connected = true,
                Updated = DateTime.Now, Via = "Xbox wireless",
            });
        }
        return result;
    }

    private static void MergeXbox(List<DeviceReading> devices, List<DeviceReading> pads)
    {
        foreach (var pad in pads)
        {
            var index = devices.FindIndex(d => d.Connected && d.Name.Contains("Xbox", StringComparison.OrdinalIgnoreCase) && !d.Id.StartsWith("xinput:"));
            if (index >= 0)
            {
                // Paired over Bluetooth: Windows' own level is finer, XInput fills in when there is none.
                var paired = devices[index];
                if (paired.Percent == null) devices[index] = paired with { Percent = pad.Percent, Coarse = true, Updated = pad.Updated, Kind = DeviceKind.Controller };
                continue;
            }
            devices.Add(pad);
        }
    }

    // MARK: Device properties through SetupAPI

    private readonly record struct DevKey(Guid Fmtid, uint Pid)
    {
        public DevKey(string fmtid, uint pid) : this(new Guid(fmtid), pid) { }
    }

    private static class DeviceSet
    {
        [StructLayout(LayoutKind.Sequential)] private struct DEVPROPKEY { public Guid fmtid; public uint pid; }
        [StructLayout(LayoutKind.Sequential)] public struct SP_DEVINFO_DATA { public int cbSize; public Guid ClassGuid; public uint DevInst; public IntPtr Reserved; }

        [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr SetupDiGetClassDevsW(IntPtr classGuid, string? enumerator, IntPtr parent, int flags);
        [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr SetupDiGetClassDevsW(ref Guid classGuid, IntPtr enumerator, IntPtr parent, int flags);
        [DllImport("setupapi.dll", SetLastError = true)]
        private static extern bool SetupDiEnumDeviceInfo(IntPtr set, int index, ref SP_DEVINFO_DATA data);
        [DllImport("setupapi.dll", SetLastError = true)]
        private static extern bool SetupDiEnumDeviceInterfaces(IntPtr set, IntPtr devInfo, ref Guid classGuid, int index, ref Native.SP_DEVICE_INTERFACE_DATA data);
        [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool SetupDiGetDeviceInterfaceDetailW(IntPtr set, ref Native.SP_DEVICE_INTERFACE_DATA data, IntPtr detail, int size, out int required, ref SP_DEVINFO_DATA devInfo);
        [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool SetupDiGetDevicePropertyW(IntPtr set, ref SP_DEVINFO_DATA data, ref DEVPROPKEY key, out uint type, byte[]? buffer, int size, out int required, int flags);
        [DllImport("setupapi.dll", SetLastError = true)] private static extern bool SetupDiDestroyDeviceInfoList(IntPtr set);

        private const int DIGCF_PRESENT = 0x2, DIGCF_ALLCLASSES = 0x4, DIGCF_DEVICEINTERFACE = 0x10;

        public static void Each(string enumerator, Action<IntPtr, SP_DEVINFO_DATA> body)
        {
            var set = SetupDiGetClassDevsW(IntPtr.Zero, enumerator, IntPtr.Zero, DIGCF_ALLCLASSES | DIGCF_PRESENT);
            if (set == new IntPtr(-1)) return;
            try
            {
                for (var i = 0; i < 512; i++)
                {
                    var info = new SP_DEVINFO_DATA { cbSize = Marshal.SizeOf<SP_DEVINFO_DATA>() };
                    if (!SetupDiEnumDeviceInfo(set, i, ref info)) break;
                    try { body(set, info); } catch { }
                }
            }
            finally { SetupDiDestroyDeviceInfoList(set); }
        }

        /// <summary>Present interfaces of a class, with each device's container (the physical device it belongs to).</summary>
        public static List<(string path, Guid? container)> Interfaces(Guid interfaceClass)
        {
            var result = new List<(string, Guid?)>();
            var guid = interfaceClass;
            var set = SetupDiGetClassDevsW(ref guid, IntPtr.Zero, IntPtr.Zero, DIGCF_PRESENT | DIGCF_DEVICEINTERFACE);
            if (set == new IntPtr(-1)) return result;
            try
            {
                for (var i = 0; i < 512; i++)
                {
                    var data = new Native.SP_DEVICE_INTERFACE_DATA { cbSize = Marshal.SizeOf<Native.SP_DEVICE_INTERFACE_DATA>() };
                    if (!SetupDiEnumDeviceInterfaces(set, IntPtr.Zero, ref guid, i, ref data)) break;
                    var info = new SP_DEVINFO_DATA { cbSize = Marshal.SizeOf<SP_DEVINFO_DATA>() };
                    SetupDiGetDeviceInterfaceDetailW(set, ref data, IntPtr.Zero, 0, out var required, ref info);
                    if (required <= 0) continue;
                    var detail = Marshal.AllocHGlobal(required);
                    try
                    {
                        Marshal.WriteInt32(detail, IntPtr.Size == 8 ? 8 : 6);
                        if (SetupDiGetDeviceInterfaceDetailW(set, ref data, detail, required, out _, ref info))
                            result.Add((Marshal.PtrToStringUni(detail + 4) ?? "", Guid(set, info, ContainerId)));
                    }
                    finally { Marshal.FreeHGlobal(detail); }
                }
            }
            finally { SetupDiDestroyDeviceInfoList(set); }
            return result;
        }

        private static byte[]? Raw(IntPtr set, SP_DEVINFO_DATA info, DevKey key, out uint type)
        {
            var k = new DEVPROPKEY { fmtid = key.Fmtid, pid = key.Pid };
            SetupDiGetDevicePropertyW(set, ref info, ref k, out type, null, 0, out var required, 0);
            if (required <= 0 || required > 65536) return null;
            var buffer = new byte[required];
            return SetupDiGetDevicePropertyW(set, ref info, ref k, out type, buffer, buffer.Length, out _, 0) ? buffer : null;
        }

        public static string? String(IntPtr set, SP_DEVINFO_DATA info, DevKey key)
        {
            var raw = Raw(set, info, key, out var type);
            if (raw == null || type != 0x12) return null;
            var text = Encoding.Unicode.GetString(raw).TrimEnd('\0');
            return text.Length > 0 ? text : null;
        }

        public static List<string> StringList(IntPtr set, SP_DEVINFO_DATA info, DevKey key)
        {
            var raw = Raw(set, info, key, out var type);
            if (raw == null || type != 0x2012) return new();
            return Encoding.Unicode.GetString(raw).Split('\0', StringSplitOptions.RemoveEmptyEntries).ToList();
        }

        public static byte? Byte(IntPtr set, SP_DEVINFO_DATA info, DevKey key)
        {
            var raw = Raw(set, info, key, out var type);
            return raw != null && type == 0x3 && raw.Length >= 1 ? raw[0] : null;
        }

        public static bool? Bool(IntPtr set, SP_DEVINFO_DATA info, DevKey key)
        {
            var raw = Raw(set, info, key, out var type);
            return raw != null && type == 0x11 && raw.Length >= 1 ? raw[0] != 0 : null;
        }

        public static DateTime? FileTime(IntPtr set, SP_DEVINFO_DATA info, DevKey key)
        {
            var raw = Raw(set, info, key, out var type);
            if (raw == null || type != 0x10 || raw.Length < 8) return null;
            var ticks = BitConverter.ToInt64(raw, 0);
            try { return ticks > 0 ? DateTime.FromFileTime(ticks) : null; } catch { return null; }
        }

        public static Guid? Guid(IntPtr set, SP_DEVINFO_DATA info, DevKey key)
        {
            var raw = Raw(set, info, key, out var type);
            return raw != null && type == 0xD && raw.Length >= 16 ? new Guid(raw.AsSpan(0, 16)) : null;
        }
    }
}

/// <summary>How long a device's charge lasts, from its stored levels.</summary>
public static class DeviceForecast
{
    /// <summary>
    /// Drain per day over the latest discharge (since the last charge), or null with too little to go on:
    /// at least 12 hours and 3 points of drop, since devices report whole percents (some only in steps of 5 or 10).
    /// </summary>
    public static double? PercentPerDay(IReadOnlyList<DeviceLevel> levels)
    {
        if (levels.Count < 2) return null;
        var newest = levels[^1];
        if (newest.Charging) return null;
        var start = newest;
        for (var i = levels.Count - 2; i >= 0; i--)
        {
            var level = levels[i];
            // Going back in time the level only rises while discharging; a drop or a charge ends the run.
            if (level.Charging || level.Percent < start.Percent - 0.5) break;
            start = level;
        }
        var days = (newest.Date - start.Date).TotalDays;
        var drop = start.Percent - newest.Percent;
        if (days < 0.5 || drop < 3) return null;
        return drop / days;
    }

    public static double? DaysLeft(IReadOnlyList<DeviceLevel> levels, double percent) =>
        PercentPerDay(levels) is double rate && rate > 0 ? Math.Min(365, percent / rate) : null;
}

/// <summary>Low-battery rule for devices: once per discharge, re-armed after charging.</summary>
public sealed class DeviceWatch
{
    public bool Enabled = true;
    public double Threshold = 20;
    public HashSet<string> Muted = new();
    /// <summary>Devices already reported in their current discharge.</summary>
    public HashSet<string> Fired = new();

    public List<Finding> Evaluate(IEnumerable<DeviceReading> devices, DateTime now)
    {
        var findings = new List<Finding>();
        foreach (var device in devices)
        {
            if (device.Percent is not double percent) continue;
            if (device.Charging || percent > Threshold + 5) { Fired.Remove(device.Id); continue; }
            // Only devices connected right now: a stored level of a device that is away is not news.
            if (!Enabled || !device.Connected ||percent > Threshold || Muted.Contains(DeviceKey(device.Id)) || Fired.Contains(device.Id)) continue;
            Fired.Add(device.Id);
            var level = device.Coarse ? device.LevelText.ToLowerInvariant() : $"at {Format.Percent(percent)}";
            findings.Add(new Finding(EventKind.DeviceLow, DeviceKey(device.Id), $"{device.Name} is low",
                $"The {device.Kind.Label().ToLowerInvariant()}’s battery is {level}. Charge it soon.", percent));
        }
        return findings;
    }

    /// <summary>Key used for muting and in alerts.</summary>
    public static string DeviceKey(string id) => $"device:{id}";
}
