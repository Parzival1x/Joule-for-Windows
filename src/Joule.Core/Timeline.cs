namespace Joule.Core;

/// <summary>What happened during one discharge session, minute by minute: battery level, power and which app was in front.</summary>
public sealed class SessionTimeline
{
    public sealed record Point(DateTime Date, double Percent, double Watts);

    public enum LaneKind { App, Locked }

    public sealed class Lane
    {
        public LaneKind Kind;
        public string Name = "";
        public AppIdentity? App;
        public List<DateRange> Blocks = new();
        public double Seconds;
        public string Id => App?.Key ?? Name;
    }

    public DateTime Start, End;
    public List<Point> Points = new();
    public List<Lane> Lanes = new();
}

public enum StripKind { Battery, Charging, Adapter, Sleep }

/// <summary>One calendar day as a strip: on battery, charging, on the adapter or asleep.</summary>
public sealed class DayStrip
{
    public sealed record Block(DateTime Start, DateTime End, StripKind Kind);
    public DateTime Day;
    public List<Block> Blocks = new();
    /// <summary>Percentage points lost while awake on battery that day.</summary>
    public double PercentUsed;
}

public sealed partial class Store
{
    private sealed record PowerRow(double Start, double End, PowerSource Source, double Percent, double Watts, string? Front);

    private List<PowerRow> Rows(double from, double to)
    {
        var rows = new List<PowerRow>();
        _db.Query("SELECT t, dt, source, percent, watts, front FROM power WHERE t > ? AND t <= ? ORDER BY t", row =>
        {
            var t = row.Double(0);
            rows.Add(new PowerRow(t - row.Double(1), t, PowerSourceExtensions.ParseSource(row.String(2)), row.Double(3), row.Double(4), row.OptionalString(5)));
        }, from, to);
        return rows;
    }

    /// <summary>Joins neighbouring intervals with the same key into blocks; short gaps (a missed minute) are bridged.</summary>
    public static List<(double start, double end, T key)> Blocks<T>(IEnumerable<(double start, double end, T key)> items, double gap = 150)
    {
        var result = new List<(double start, double end, T key)>();
        foreach (var item in items)
        {
            if (result.Count > 0 && EqualityComparer<T>.Default.Equals(result[^1].key, item.key) && item.start - result[^1].end <= gap)
                result[^1] = (result[^1].start, item.end, item.key);
            else
                result.Add(item);
        }
        return result;
    }

    public SessionTimeline Timeline(DischargeSession session, int laneCount = 3) => Locked(() =>
    {
        var apps = Apps();
        var rows = Rows(Time.Unix(session.Start), Time.Unix(session.End) + 1);
        var points = rows.Select(r => new SessionTimeline.Point(Time.FromUnix(r.End), r.Percent, r.Watts)).ToList();
        var merged = Blocks(rows.Where(r => r.Front != null).Select(r => (r.Start, r.End, r.Front!)));
        var seconds = new Dictionary<string, double>();
        foreach (var block in merged) seconds[block.key] = seconds.GetValueOrDefault(block.key) + block.end - block.start;

        var appKeys = seconds.Keys
            .Where(k => k != AppResolver.LockedKey && (apps.TryGetValue(k, out var a) ? a.Category : AppCategory.App) == AppCategory.App)
            .OrderByDescending(k => seconds[k])
            .Take(laneCount);
        var lanes = appKeys.Select(key =>
        {
            var identity = apps.TryGetValue(key, out var a) ? a
                : new AppIdentity(key, System.IO.Path.GetFileNameWithoutExtension(key), null, AppCategory.App);
            return new SessionTimeline.Lane
            {
                Kind = SessionTimeline.LaneKind.App, Name = identity.Name, App = identity,
                Blocks = merged.Where(b => b.key == key).Select(b => new DateRange(Time.FromUnix(b.start), Time.FromUnix(b.end))).ToList(),
                Seconds = seconds.GetValueOrDefault(key),
            };
        }).ToList();
        if (seconds.TryGetValue(AppResolver.LockedKey, out var locked) && locked >= 60)
        {
            lanes.Add(new SessionTimeline.Lane
            {
                Kind = SessionTimeline.LaneKind.Locked, Name = "Locked",
                Blocks = merged.Where(b => b.key == AppResolver.LockedKey).Select(b => new DateRange(Time.FromUnix(b.start), Time.FromUnix(b.end))).ToList(),
                Seconds = locked,
            });
        }
        return new SessionTimeline
        {
            Start = session.Start, End = Time.Max(session.End, session.Start.AddSeconds(60)), Points = points, Lanes = lanes,
        };
    });

    /// <summary>Newest day first.</summary>
    public List<DayStrip> DayStrips(DateRange range) => Locked(() =>
    {
        var rows = Rows(Time.Unix(range.Start), Time.Unix(range.End));
        var sleeps = new List<(double start, double end, bool charged)>();
        _db.Query("SELECT start, end, before, after FROM sleeps WHERE end > ? AND start < ?", row =>
            // Level went up while asleep: it was charging with the lid closed.
            sleeps.Add((row.Double(0), row.Double(1), row.Double(3) > row.Double(2) + 1)), Time.Unix(range.Start), Time.Unix(range.End));
        static StripKind Kind(PowerSource s) => s switch { PowerSource.Battery => StripKind.Battery, PowerSource.Charging => StripKind.Charging, _ => StripKind.Adapter };
        var merged = Blocks(rows.Select(r => (r.Start, r.End, Kind(r.Source))))
            .Concat(sleeps.Select(s => (s.start, s.end, s.charged ? StripKind.Charging : StripKind.Sleep))).ToList();

        var strips = new List<DayStrip>();
        var day = range.Start.Date;
        while (day < range.End)
        {
            var next = day.AddDays(1);
            double from = Time.Unix(day), to = Time.Unix(next);
            var blocks = merged.Select(b =>
            {
                double start = Math.Max(b.Item1, from), end = Math.Min(b.Item2, to);
                return end > start ? new DayStrip.Block(Time.FromUnix(start), Time.FromUnix(end), b.Item3) : null;
            }).Where(b => b != null).Select(b => b!).OrderBy(b => b.Start).ToList();
            double used = 0;
            PowerRow? previous = null;
            foreach (var row in rows.Where(r => r.End > from && r.End <= to))
            {
                if (previous != null && previous.Source == PowerSource.Battery && row.Source == PowerSource.Battery && previous.Percent > row.Percent)
                    used += previous.Percent - row.Percent;
                previous = row;
            }
            strips.Add(new DayStrip { Day = day, Blocks = blocks, PercentUsed = used });
            day = next;
        }
        strips.Reverse();
        return strips;
    });
}
