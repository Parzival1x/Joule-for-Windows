namespace Joule.Core;

/// <summary>Explains a session's battery use in a few plain sentences, most telling first.</summary>
public static class Narrative
{
    /// <param name="blockers">Names of apps currently keeping the PC awake.</param>
    public static List<string> Explain(SessionBreakdown breakdown, double fullWh, IReadOnlyList<string>? blockers = null)
    {
        var total = breakdown.PercentUsed;
        var sentences = new List<string>();
        if (total < 1) return sentences;
        var entries = breakdown.Entries;
        var apps = entries.Where(e => e.Kind == ShareKind.App).ToList();
        var appsShare = apps.Sum(e => e.Percent);
        var hardware = entries.FirstOrDefault(e => e.Kind == ShareKind.Hardware)?.Percent ?? 0;
        var sleep = entries.FirstOrDefault(e => e.Kind == ShareKind.Sleep)?.Percent ?? 0;
        var top = entries.FirstOrDefault();

        // 1. The biggest item.
        if (top?.Kind == ShareKind.Hardware && hardware >= total * 0.4)
        {
            var sentence = $"Most of it, {P(hardware)} of {P(total)}, went to the PC itself: display, graphics, memory and Wi-Fi – everything that can’t be pinned on a single app.";
            if (appsShare < total * 0.15) sentence += " Your apps barely mattered.";
            sentences.Add(sentence);
        }
        else if (top != null && (top.Kind == ShareKind.App || top.Kind == ShareKind.Service))
        {
            sentences.Add($"{top.Name} used the most: {P(top.Percent)} – {Portion(top.Percent / total)} of the drain.");
        }
        else if (top?.Kind == ShareKind.Sleep)
        {
            sentences.Add($"The biggest item is sleep, with {P(sleep)}.");
        }

        // 2. The apps that mattered.
        var notable = apps.Where(e => e.Percent >= 0.3 && e.Id != top?.Id).Take(2).ToList();
        if (notable.Count > 0)
        {
            var list = Format.List(notable.Select(e => $"{e.Name} ({P(e.Percent)})").ToList());
            if (top?.Kind == ShareKind.App) sentences.Add(notable.Count == 1 ? $"Followed by {list}." : $"Followed by {list}.");
            else sentences.Add($"Among apps, {list} led.");
        }

        // 3. Locked but awake – the classic silent drain.
        if (breakdown.LockedSeconds >= 10 * 60 && breakdown.LockedPercent >= 0.5)
        {
            var sentence = $"For {Format.Duration(breakdown.LockedSeconds)} of that the screen was locked but the PC didn’t go to sleep – that cost about {P(breakdown.LockedPercent)}.";
            if (blockers is { Count: > 0 }) sentence += $" Right now {blockers[0]} is keeping it awake – see “Keeping awake”.";
            else sentence += " Let it sleep or close the lid, and that stays in the battery.";
            sentences.Add(sentence);
        }

        // 4. Sleep.
        if (sleep > 0 && breakdown.SleepHours > 0.5)
        {
            var rate = sleep / breakdown.SleepHours;
            var duration = Format.Duration(breakdown.SleepHours * 3600);
            sentences.Add(rate < 0.5
                ? $"During sleep, {P(sleep)} was lost in {duration} – perfectly normal."
                : $"During sleep, {P(sleep)} was lost in {duration}. That’s a lot – usually something is waking the PC at night.");
        }

        // 5. Brightness, when the screen dominates.
        if (breakdown.AverageBrightness is double brightness && brightness >= 0.7 && hardware >= total * 0.4)
            sentences.Add($"Brightness averaged {Format.Percent(brightness * 100)}. Turning it down a little helps most here.");

        // 6. Pace.
        var watts = breakdown.Session.AverageWatts;
        if (watts > 0.5 && fullWh > 0 && breakdown.Session.AwakeSeconds >= 15 * 60)
            sentences.Add($"On average the PC drew {Format.Watts(watts)}. At this rate a full charge lasts about {Format.Duration(fullWh / watts * 3600)}.");
        return sentences.Select(s => s.Replace("..", ".")).ToList();
    }

    /// <summary>One or two short sentences for tight spaces like the tray panel.</summary>
    public static string Headline(SessionBreakdown breakdown)
    {
        var top = breakdown.Entries.FirstOrDefault();
        if (breakdown.PercentUsed < 1 || top == null) return "Not a full percent used yet.";
        var apps = breakdown.Entries.Where(e => e.Kind == ShareKind.App).Sum(e => e.Percent);
        var appsText = $"Apps together {Format.Percent(apps, 1)}.";
        return top.Kind switch
        {
            ShareKind.Hardware => "Biggest item: display and baseline. " + appsText,
            ShareKind.Sleep => "Biggest item: sleep. " + appsText,
            _ => $"Biggest item: {top.Name} with {Format.Percent(top.Percent, 1)}.",
        };
    }

    private static string P(double value) => Format.Percent(value, value < 10 ? 1 : 0);

    private static string Portion(double fraction) => fraction switch
    {
        >= 0.9 => "almost all",
        >= 0.7 => "most",
        >= 0.55 => "more than half",
        >= 0.45 => "about half",
        >= 0.3 => "about a third",
        >= 0.2 => "about a quarter",
        _ => $"about {Format.Share(fraction)}",
    };
}
