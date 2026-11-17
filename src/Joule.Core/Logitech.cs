using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace Joule.Core;

/// <summary>
/// Logitech devices on a USB receiver (Logi Bolt, Unifying, Lightspeed) or cable never tell Windows their battery;
/// they answer Logitech's HID++ 2.0 protocol on the receiver's vendor channel. Joule asks the same way Logi Options+ does,
/// with its own software id, so both can talk to the receiver at the same time. Only reads, never changes a setting.
/// </summary>
public static class LogitechProbe
{
    private const ushort Logitech = 0x046D;
    private const byte LongReport = 0x11, SoftwareId = 0x0B;
    /// <summary>An idle Bolt mouse polls its radio slowly and can take most of a second to answer.</summary>
    private const int Timeout = 1500;

    private const ushort FeatureRoot = 0x0000, FeatureDeviceInfo = 0x0003, FeatureName = 0x0005,
        FeatureBattery = 0x1000, FeatureVoltage = 0x1001, FeatureUnifiedBattery = 0x1004;

    /// <summary>What we learned about a device once; afterwards one request per read is enough.</summary>
    private sealed record Known(string Name, DeviceKind Kind, string Unit, ushort BatteryFeature, byte BatteryIndex);
    private static readonly Dictionary<(string path, byte slot), Known> Cache = new();
    private static readonly Dictionary<(string path, byte slot), DateTime> Empty = new();

    internal static List<DeviceReading> Read(IEnumerable<(string path, Guid? container)> interfaces)
    {
        var result = new List<DeviceReading>();
        if (_forgetEmpty) { _forgetEmpty = false; Empty.Clear(); }
        foreach (var (path, _) in interfaces)
        {
            // Only USB (receiver or cable). Over Bluetooth, Windows already has the level.
            if (DeviceProbe.HidIds(path) is not { } ids || ids.vid != Logitech || path.Contains("{00001812-", StringComparison.OrdinalIgnoreCase)
                || path.Contains("{00001124-", StringComparison.OrdinalIgnoreCase)) continue;
            try { result.AddRange(ReadInterface(path, ids.pid)); } catch { }
        }
        // The same device can answer on two channels (e.g. receiver and cable): keep one.
        return result.GroupBy(r => r.Id).Select(g => g.First()).ToList();
    }

    /// <summary>Something was plugged in or removed: ask empty slots again on the next read instead of waiting.</summary>
    public static void DevicesChanged() => _forgetEmpty = true;
    private static volatile bool _forgetEmpty;

    /// <summary>Reads each receiver's notification flags (HID++ 1.0 register 0x00) on the short channel; read-only.</summary>
    public static void DiagnoseNotifications(IEnumerable<string> paths, Action<string> log)
    {
        foreach (var path in paths)
        {
            if (DeviceProbe.HidIds(path) is not { } ids || ids.vid != Logitech || !IsReceiver(ids.pid)) continue;
            var handle = Native.CreateFileW(path, 0xC0000000, 3, IntPtr.Zero, 3, 0x40000000, IntPtr.Zero);
            if (handle == new IntPtr(-1)) continue;
            using var safe = new SafeFileHandle(handle, true);
            if (!HidCaps(safe, out var page, out var input, out _) || page != 0xFF00 || input != 7) continue;
            using var stream = new FileStream(safe, FileAccess.ReadWrite, 0, true);
            stream.WriteAsync(new byte[] { 0x10, 0xFF, 0x81, 0x00, 0, 0, 0 }).AsTask().GetAwaiter().GetResult();
            var buffer = new byte[64];
            var deadline = Environment.TickCount64 + 1500;
            while (Environment.TickCount64 < deadline)
            {
                try
                {
                    using var t = new CancellationTokenSource((int)Math.Max(1, deadline - Environment.TickCount64));
                    var n = stream.ReadAsync(buffer.AsMemory(), t.Token).AsTask().GetAwaiter().GetResult();
                    log($"{ReceiverKey(path)}: {Convert.ToHexString(buffer, 0, n)}");
                    if (buffer[1] == 0xFF && buffer[2] == 0x81 && buffer[3] == 0x00) break;
                }
                catch { break; }
            }
        }
    }

    /// <summary>Raw trace for troubleshooting: every Logitech HID interface, its report sizes, and all replies to a battery query.</summary>
    public static void Diagnose(IEnumerable<string> paths, Action<string> log)
    {
        foreach (var path in paths)
        {
            if (DeviceProbe.HidIds(path) is not { } ids || ids.vid != Logitech) continue;
            var handle = Native.CreateFileW(path, 0xC0000000, 3, IntPtr.Zero, 3, 0x40000000, IntPtr.Zero);
            if (handle == new IntPtr(-1)) { log($"{path}: open failed {Marshal.GetLastWin32Error()}"); continue; }
            using var safe = new SafeFileHandle(handle, true);
            HidCaps(safe, out var page, out var input, out var output);
            log($"{path}: page {page:X4} in {input} out {output}");
            if (page != 0xFF00 || output != 20) continue;
            using var stream = new FileStream(safe, FileAccess.ReadWrite, 0, true);
            foreach (byte slot in new byte[] { 1, 2, 3, 4, 5, 6, 0xFF })
            {
                var message = new byte[20];
                message[0] = LongReport; message[1] = slot; message[2] = 0; message[3] = SoftwareId; message[4] = 0x10; message[5] = 0x04;
                try { stream.WriteAsync(message, new CancellationTokenSource(500).Token).AsTask().GetAwaiter().GetResult(); }
                catch (Exception e) { log($"  slot {slot}: write failed {e.GetType().Name} {e.Message}"); continue; }
                var deadline = Environment.TickCount64 + 600;
                var buffer = new byte[64];
                while (Environment.TickCount64 < deadline)
                {
                    try
                    {
                        using var t = new CancellationTokenSource((int)Math.Max(1, deadline - Environment.TickCount64));
                        var n = stream.ReadAsync(buffer.AsMemory(), t.Token).AsTask().GetAwaiter().GetResult();
                        log($"  slot {slot}: got {Convert.ToHexString(buffer, 0, n)}");
                    }
                    catch { break; }
                }
            }
        }
    }

    private static List<DeviceReading> ReadInterface(string path, ushort pid)
    {
        var result = new List<DeviceReading>();
        var handle = Native.CreateFileW(path, 0xC0000000, 3, IntPtr.Zero, 3, 0x40000000, IntPtr.Zero);
        if (handle == new IntPtr(-1)) return result;
        using var safe = new SafeFileHandle(handle, true);
        // The long HID++ collection: vendor page 0xFF00, 20-byte reports.
        if (!HidCaps(safe, out var usagePage, out var input, out var output) || usagePage != 0xFF00 || output != 20 || input != 20) return result;
        using var stream = new FileStream(safe, FileAccess.ReadWrite, 0, true);
        var receiver = IsReceiver(pid);
        var via = pid switch { 0xC548 => "Logi Bolt receiver", 0xC52B or 0xC532 => "Unifying receiver", _ => receiver ? "Logitech receiver" : "USB" };
        // On a receiver the devices sit in slots 1–6; a device on a cable answers as 0xFF.
        var slots = receiver ? new byte[] { 1, 2, 3, 4, 5, 6 } : new byte[] { 0xFF };
        // Unknown slots are asked all at once: an idle mouse can take a second to wake up and answer,
        // and empty slots never answer, so one shared wait instead of one per slot.
        var unknown = slots.Where(s => !Cache.ContainsKey((path, s))
            && !(Empty.TryGetValue((path, s), out var since) && (DateTime.Now - since).TotalSeconds < 30)).ToArray();
        var found = unknown.Length > 0 ? FindBattery(stream, unknown) : new Dictionary<byte, byte>();
        foreach (var slot in unknown)
        {
            var known = found.TryGetValue(slot, out var unifiedIndex) ? Discover(stream, slot, unifiedIndex) : null;
            if (known == null) { Empty[(path, slot)] = DateTime.Now; continue; }
            Empty.Remove((path, slot));
            Cache[(path, slot)] = known;
        }
        foreach (var slot in slots)
        {
            if (ReadDevice(stream, path, slot) is { } device) result.Add(device with { Via = via });
        }
        return result;
    }

    /// <summary>Asks every slot for the unified-battery feature at once. Answering slots → its index (0 = not supported).</summary>
    private static Dictionary<byte, byte> FindBattery(FileStream stream, byte[] slots)
    {
        var answers = new Dictionary<byte, byte>();
        var function = SoftwareId; // GetFeature is function 0 of the root feature (index 0)
        foreach (var slot in slots)
        {
            var message = new byte[20];
            message[0] = LongReport; message[1] = slot; message[2] = 0; message[3] = function;
            message[4] = FeatureUnifiedBattery >> 8; message[5] = FeatureUnifiedBattery & 0xFF;
            try
            {
                using var writeTimeout = new CancellationTokenSource(Timeout);
                stream.WriteAsync(message, writeTimeout.Token).AsTask().GetAwaiter().GetResult();
            }
            catch { }
        }
        var deadline = Environment.TickCount64 + Timeout;
        var buffer = new byte[64];
        while (Environment.TickCount64 < deadline && answers.Count < slots.Length)
        {
            int read;
            try
            {
                using var timeout = new CancellationTokenSource((int)Math.Max(1, deadline - Environment.TickCount64));
                read = stream.ReadAsync(buffer.AsMemory(), timeout.Token).AsTask().GetAwaiter().GetResult();
            }
            catch { break; }
            if (read < 20 || buffer[0] != LongReport || !slots.Contains(buffer[1])) continue;
            if (buffer[2] == 0 && buffer[3] == function) answers[buffer[1]] = buffer[4];
            // An error reply to the same question still means a device is there (it lacks the feature).
            else if (buffer[2] == 0xFF && buffer[3] == 0 && buffer[4] == function) answers[buffer[1]] = 0;
        }
        return answers;
    }

    private static bool IsReceiver(ushort pid) => pid is 0xC548 or 0xC52B or 0xC532 or 0xC52F or 0xC534 or 0xC539 or 0xC53A or 0xC53F or 0xC541 or 0xC545 or 0xC547 or 0xC54D;

    private static readonly Dictionary<(string path, byte slot), (DeviceReading reading, int misses)> Last = new();

    private static DeviceReading? ReadDevice(FileStream stream, string path, byte slot)
    {
        if (!Cache.TryGetValue((path, slot), out var known)) return null;
        // The receiver said the link is down (switched off, out of range): gone right away, no need to ask.
        if (Links.TryGetValue((ReceiverKey(path), slot), out var online) && !online) { Last.Remove((path, slot)); return null; }
        var level = ReadBattery(stream, slot, known.BatteryFeature, known.BatteryIndex);
        if (level == null)
        {
            // One unanswered question is usually a mouse waking up: keep the last reading once before letting it go.
            if (Last.TryGetValue((path, slot), out var last) && last.misses == 0)
            {
                Last[(path, slot)] = (last.reading, 1);
                return last.reading;
            }
            Cache.Remove((path, slot));
            Last.Remove((path, slot));
            return null;
        }
        var reading = new DeviceReading
        {
            Id = $"logi:{known.Unit}", Name = known.Name, Kind = known.Kind, Percent = level.Value.percent, Coarse = level.Value.coarse,
            Charging = level.Value.charging, Connected = true, Updated = DateTime.Now,
        };
        Last[(path, slot)] = (reading, 0);
        return reading;
    }

    // MARK: Connection notifications

    /// <summary>A receiver reported a device linking or unlinking. Raised on a background thread.</summary>
    public static event Action? LinkChanged;
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<(string receiver, byte slot), bool> Links = new();
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, bool> Listening = new();

    /// <summary>"\\?\hid#vid_046d&amp;pid_c548&amp;mi_02&amp;col01#7&amp;3a9d41a5&amp;0&amp;0000#…" → the receiver both collections share.</summary>
    public static string ReceiverKey(string path)
    {
        var match = System.Text.RegularExpressions.Regex.Match(path, @"hid#(vid_[0-9a-f]{4}&pid_[0-9a-f]{4}(?:&mi_\d+)?)(?:&col\d+)?#([^&#]+&[^&#]+)", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        return match.Success ? $"{match.Groups[1].Value}#{match.Groups[2].Value}".ToLowerInvariant() : path.ToLowerInvariant();
    }

    /// <summary>
    /// HID++ "device connection" notification (short report 0x10, sub id 0x41): byte 4 bit 6 set = link not established.
    /// Null for any other report.
    /// </summary>
    public static (byte slot, bool online)? ParseConnection(ReadOnlySpan<byte> report)
    {
        if (report.Length < 5 || report[0] != 0x10 || report[2] != 0x41 || report[1] is 0 or > 6) return null;
        return (report[1], (report[4] & 0x40) == 0);
    }

    /// <summary>
    /// Listens on each receiver's short HID++ channel, where it announces devices linking and unlinking the moment it happens.
    /// Safe to call again: receivers already listened to are skipped, new ones (just plugged in) are added.
    /// </summary>
    internal static void Listen(IEnumerable<string> paths)
    {
        foreach (var path in paths)
        {
            if (DeviceProbe.HidIds(path) is not { } ids || ids.vid != Logitech || !IsReceiver(ids.pid) || Listening.ContainsKey(path)) continue;
            var handle = Native.CreateFileW(path, 0xC0000000, 3, IntPtr.Zero, 3, 0x40000000, IntPtr.Zero);
            if (handle == new IntPtr(-1)) continue;
            var safe = new SafeFileHandle(handle, true);
            if (!HidCaps(safe, out var page, out var input, out _) || page != 0xFF00 || input != 7) { safe.Dispose(); continue; }
            if (!Listening.TryAdd(path, true)) { safe.Dispose(); continue; }
            var key = ReceiverKey(path);
            var thread = new Thread(() =>
            {
                try
                {
                    using var stream = new FileStream(safe, FileAccess.Read, 0, true);
                    var buffer = new byte[64];
                    while (true)
                    {
                        var read = stream.ReadAsync(buffer.AsMemory()).AsTask().GetAwaiter().GetResult();
                        if (read <= 0) break;
                        if (ParseConnection(buffer.AsSpan(0, read)) is not { } link) continue;
                        Links[(key, link.slot)] = link.online;
                        if (link.online) _forgetEmpty = true;
                        LinkChanged?.Invoke();
                    }
                }
                catch { }
                finally { safe.Dispose(); Listening.TryRemove(path, out _); }   // receiver unplugged
            }) { IsBackground = true, Name = "Joule Logitech link" };
            thread.Start();
        }
    }

    private static Known? Discover(FileStream stream, byte slot, byte unifiedIndex)
    {
        // Unified battery (0x1004) was already asked for; older devices have 0x1000 or 0x1001 instead.
        ushort feature = 0; byte index = 0;
        if (unifiedIndex != 0) { feature = FeatureUnifiedBattery; index = unifiedIndex; }
        else
        {
            foreach (var candidate in new[] { FeatureBattery, FeatureVoltage })
            {
                if (Request(stream, slot, 0, 0, (byte)(candidate >> 8), (byte)candidate) is { } reply && reply[0] != 0) { feature = candidate; index = reply[0]; break; }
            }
        }
        if (index == 0) return null;

        string name = "Logitech device";
        var kind = DeviceKind.Other;
        if (Request(stream, slot, 0, 0, FeatureName >> 8, FeatureName & 0xFF) is { } nameFeature && nameFeature[0] != 0)
        {
            var nameIndex = nameFeature[0];
            if (Request(stream, slot, nameIndex, 0) is { } count && count[0] > 0)
            {
                var bytes = new List<byte>();
                while (bytes.Count < count[0] && Request(stream, slot, nameIndex, 1, (byte)bytes.Count) is { } chunk)
                {
                    var take = chunk.TakeWhile(b => b != 0).Take(count[0] - bytes.Count).ToList();
                    if (take.Count == 0) break;
                    bytes.AddRange(take);
                }
                if (bytes.Count > 0) name = Encoding.UTF8.GetString(bytes.ToArray()).Trim();
            }
            if (Request(stream, slot, nameIndex, 2) is { } type) kind = KindFromType(type[0]);
        }
        if (kind == DeviceKind.Other) kind = DeviceKindExtensions.Classify(Array.Empty<string>(), name);

        var unit = name.ToLowerInvariant().Replace(' ', '-');
        if (Request(stream, slot, 0, 0, FeatureDeviceInfo >> 8, FeatureDeviceInfo & 0xFF) is { } info && info[0] != 0
            && Request(stream, slot, info[0], 0) is { } details && details.Skip(1).Take(4).Any(b => b != 0))
            unit = Convert.ToHexString(details, 1, 4).ToLowerInvariant();
        return new Known(name, kind, unit, feature, index);
    }

    /// <summary>HID++ device type (feature 0x0005, function 2).</summary>
    public static DeviceKind KindFromType(byte type) => type switch
    {
        0 or 2 => DeviceKind.Keyboard,
        3 or 4 or 5 => DeviceKind.Mouse,
        6 => DeviceKind.Pen,
        8 => DeviceKind.Headphones,
        _ => DeviceKind.Other,
    };

    private static (double percent, bool charging, bool coarse)? ReadBattery(FileStream stream, byte slot, ushort feature, byte index)
    {
        var function = feature == FeatureUnifiedBattery ? (byte)1 : (byte)0;
        return Request(stream, slot, index, function) is { } reply ? ParseBattery(feature, reply) : null;
    }

    /// <summary>
    /// Battery reply → level. 0x1004 GetStatus: [0] percent, [1] level flags (8 full, 4 good, 2 low, 1 critical),
    /// [2] charging (1, 2 charging, 3 complete). 0x1000: [0] percent, [2] status (1 recharging, 2 almost full, 3 full).
    /// 0x1001: [0..1] millivolts, [2] flags (bit 7 charging); converted with a lithium-ion discharge curve.
    /// </summary>
    public static (double percent, bool charging, bool coarse)? ParseBattery(ushort feature, byte[] reply)
    {
        switch (feature)
        {
            case FeatureUnifiedBattery:
            {
                var charging = reply[2] is 1 or 2;
                if (reply[2] == 3) return (100, false, false);
                if (reply[0] is > 0 and <= 100) return (reply[0], charging, false);
                var flags = reply[1];
                if (flags == 0) return null;
                double level = (flags & 8) != 0 ? 100 : (flags & 4) != 0 ? 60 : (flags & 2) != 0 ? 15 : 5;
                return (level, charging, true);
            }
            case FeatureBattery:
            {
                if (reply[2] == 3) return (100, false, false);
                if (reply[0] is 0 or > 100) return null;
                return (reply[0], reply[2] is 1 or 2, false);
            }
            case FeatureVoltage:
            {
                var millivolts = reply[0] << 8 | reply[1];
                if (millivolts < 2500 || millivolts > 5000) return null;
                return (VoltagePercent(millivolts), (reply[2] & 0x80) != 0, false);
            }
        }
        return null;
    }

    private static readonly (int mv, double percent)[] Curve =
    {
        (4186, 100), (4067, 90), (3989, 80), (3922, 70), (3859, 60), (3811, 50), (3778, 40), (3751, 30), (3717, 20), (3671, 10), (3646, 5), (3579, 2), (3500, 0),
    };

    public static double VoltagePercent(int millivolts)
    {
        if (millivolts >= Curve[0].mv) return 100;
        for (var i = 1; i < Curve.Length; i++)
        {
            if (millivolts >= Curve[i].mv)
            {
                var (hiMv, hiP) = Curve[i - 1];
                var (loMv, loP) = Curve[i];
                return Math.Round(loP + (hiP - loP) * (millivolts - loMv) / (double)(hiMv - loMv));
            }
        }
        return 0;
    }

    /// <summary>One HID++ 2.0 request; the 16 parameter bytes of the matching reply, or null on error or timeout.</summary>
    private static byte[]? Request(FileStream stream, byte slot, byte feature, byte function, params byte[] parameters)
    {
        var message = new byte[20];
        message[0] = LongReport; message[1] = slot; message[2] = feature; message[3] = (byte)(function << 4 | SoftwareId);
        Array.Copy(parameters, 0, message, 4, Math.Min(parameters.Length, 16));
        try
        {
            using var writeTimeout = new CancellationTokenSource(Timeout);
            stream.WriteAsync(message, writeTimeout.Token).AsTask().GetAwaiter().GetResult();
        }
        catch { return null; }
        var deadline = Environment.TickCount64 + Timeout;
        var buffer = new byte[64];
        while (Environment.TickCount64 < deadline)
        {
            int read;
            try
            {
                using var timeout = new CancellationTokenSource((int)Math.Max(1, deadline - Environment.TickCount64));
                read = stream.ReadAsync(buffer.AsMemory(), timeout.Token).AsTask().GetAwaiter().GetResult();
            }
            catch { return null; }
            if (read < 20 || buffer[0] != LongReport || buffer[1] != slot) continue;
            // Error reply: feature 0xFF, then the request's feature and function.
            if (buffer[2] == 0xFF && buffer[3] == feature && buffer[4] == message[3]) return null;
            if (buffer[2] == feature && buffer[3] == message[3]) return buffer[4..20];
        }
        return null;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct HIDP_CAPS
    {
        public ushort Usage, UsagePage, InputReportByteLength, OutputReportByteLength, FeatureReportByteLength;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 27)] public ushort[] Rest;
    }
    [DllImport("hid.dll")] private static extern bool HidD_GetPreparsedData(SafeFileHandle handle, out IntPtr data);
    [DllImport("hid.dll")] private static extern bool HidD_FreePreparsedData(IntPtr data);
    [DllImport("hid.dll")] private static extern int HidP_GetCaps(IntPtr data, out HIDP_CAPS caps);

    private static bool HidCaps(SafeFileHandle handle, out ushort usagePage, out int input, out int output)
    {
        usagePage = 0; input = output = 0;
        if (!HidD_GetPreparsedData(handle, out var data)) return false;
        try
        {
            if (HidP_GetCaps(data, out var caps) != 0x110000) return false;
            usagePage = caps.UsagePage; input = caps.InputReportByteLength; output = caps.OutputReportByteLength;
            return true;
        }
        finally { HidD_FreePreparsedData(data); }
    }
}
