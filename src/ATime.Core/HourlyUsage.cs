namespace ATime.Core;

public sealed record HourlyUsage(int Hour, long EffectiveMs, long SessionMs);

/// <summary>Projects the engine's hour/offset-bounded segments using monotonic durations.</summary>
public static class HourlyUsageProjector
{
    public static HourlyUsage[] EmptyDay() => Enumerable.Range(0, 24).Select(hour => new HourlyUsage(hour, 0, 0)).ToArray();

    public static int HourAt(long utcMilliseconds, TimeZoneInfo zone) =>
        TimeZoneInfo.ConvertTime(DateTimeOffset.FromUnixTimeMilliseconds(utcMilliseconds), zone).Hour;

    public static void AddDelta(HourlyUsage[] hours, string localDate, TimeZoneInfo zone,
        UsageSegment segment, UsageSegment? committed = null)
    {
        if (segment.LocalDate != localDate) return;
        var hour = HourAt(segment.StartUtcMs, zone);
        var previous = hours[hour];
        hours[hour] = previous with {
            EffectiveMs = previous.EffectiveMs + segment.EffectiveMs - (committed?.EffectiveMs ?? 0),
            SessionMs = previous.SessionMs + segment.DurationMs - (committed?.DurationMs ?? 0)
        };
    }

    public static IReadOnlyList<HourlyUsage> WithPending(IReadOnlyList<HourlyUsage> saved,
        IEnumerable<UsageSegment> pending, IReadOnlyDictionary<string, UsageSegment> committedOpen,
        string localDate, TimeZoneInfo zone)
    {
        var result = saved.ToArray();
        foreach (var segment in pending)
        {
            committedOpen.TryGetValue(segment.Id, out var committed);
            AddDelta(result, localDate, zone, segment, committed);
        }
        return Array.AsReadOnly(result);
    }

}
