namespace Joule.Core;

/// <summary>A device Joule has seen, with its stored levels.</summary>
public sealed record KnownDevice(string Id, string Name, DeviceKind Kind, string Via, DateTime Seen);

public sealed partial class Store
{
    /// <summary>
    /// Stores the devices' levels. A level is written when it changed, the charging state flipped, or six hours passed,
    /// at the time Windows says it was reported (Bluetooth), so a value read again after days is not a new point.
    /// </summary>
    public void RecordDevices(IEnumerable<DeviceReading> devices, DateTime? now = null) => Locked(() => _db.Transaction(() =>
    {
        var n = now ?? DateTime.Now;
        foreach (var device in devices)
        {
            _db.Run("INSERT INTO devices VALUES(?,?,?,?,?) ON CONFLICT(id) DO UPDATE SET name=excluded.name, kind=excluded.kind, via=excluded.via, seen=MAX(seen, excluded.seen)",
                device.Id, device.Name, device.Kind.Raw(), device.Via, device.Connected ? n : device.Updated ?? DateTime.UnixEpoch.ToLocalTime());
            if (device.Percent is not double percent) continue;
            var t = Time.Unix(device.Updated is DateTime u && u <= n ? u : n);
            (double t, double percent, bool charging)? last = null;
            _db.Query("SELECT t, percent, charging FROM device_levels WHERE id=? ORDER BY t DESC LIMIT 1",
                row => last = (row.Double(0), row.Double(1), row.Int(2) != 0), device.Id);
            if (last is { } l)
            {
                if (t <= l.t + 1) continue;
                if (Math.Abs(l.percent - percent) < 0.5 && l.charging == device.Charging && t - l.t < 6 * 3600) continue;
            }
            _db.Run("INSERT OR REPLACE INTO device_levels VALUES(?,?,?,?)", device.Id, t, percent, device.Charging);
        }
    }));

    public List<KnownDevice> KnownDevices() => Locked(() =>
    {
        var result = new List<KnownDevice>();
        _db.Query("SELECT id, name, kind, via, seen FROM devices ORDER BY seen DESC",
            row => result.Add(new KnownDevice(row.String(0), row.String(1), DeviceKindExtensions.ParseDeviceKind(row.String(2)), row.String(3), row.Date(4))));
        return result;
    });

    public List<DeviceLevel> DeviceLevels(string id, DateTime since) => Locked(() =>
    {
        var result = new List<DeviceLevel>();
        _db.Query("SELECT t, percent, charging FROM device_levels WHERE id=? AND t>=? ORDER BY t",
            row => result.Add(new DeviceLevel(row.Date(0), row.Double(1), row.Int(2) != 0)), id, Time.Unix(since));
        return result;
    });

    /// <summary>This PC's own battery level from the power history, one point per ten minutes.</summary>
    public List<DeviceLevel> PcLevels(DateTime since) => Locked(() =>
    {
        var result = new List<DeviceLevel>();
        _db.Query("SELECT MAX(t), AVG(percent), MAX(source='charging') FROM power WHERE t>=? GROUP BY CAST(t/600 AS INTEGER) ORDER BY 1",
            row => result.Add(new DeviceLevel(row.Date(0), row.Double(1), row.Int(2) != 0)), Time.Unix(since));
        return result;
    });

    public void ForgetDevice(string id) => Locked(() =>
    {
        _db.Run("DELETE FROM device_levels WHERE id=?", id);
        _db.Run("DELETE FROM devices WHERE id=?", id);
    });
}
