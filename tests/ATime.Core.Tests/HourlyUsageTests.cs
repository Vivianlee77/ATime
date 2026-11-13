using ATime.Core;
using Xunit;

namespace ATime.Core.Tests;

public sealed class HourlyUsageTests
{
    private static readonly TimeZoneInfo Zone = TimeZoneInfo.CreateCustomTimeZone("Chart UTC+8", TimeSpan.FromHours(8), "UTC+8", "UTC+8");

    [Fact]
    public void PendingRevisionAddsOnlyTheUncommittedSuffixWithoutMutatingSavedHours()
    {
        var first = Segment("open", "2026-11-13T01:00:00Z", 1_000);
        var saved = HourlyUsageProjector.EmptyDay();
        HourlyUsageProjector.AddDelta(saved, "2026-11-13", Zone, first);
        var extended = first with { EndMonoMs = 2_500, EndUtcMs = first.StartUtcMs + 2_500, Revision = 2 };
        var result = HourlyUsageProjector.WithPending(saved, [extended],
            new Dictionary<string, UsageSegment> { [first.Id] = first }, "2026-11-13", Zone);

        Assert.Equal(1_000, saved[9].EffectiveMs);
        Assert.Equal(2_500, result[9].EffectiveMs);
        Assert.Equal(2_500, result.Sum(row => row.SessionMs));
    }

    [Fact]
    public void IdleAndOtherDatesDoNotInflateEffectiveUsageOrTheCurrentDay()
    {
        var active = Segment("active", "2026-11-13T01:00:00Z", 4_000);
        var idle = Segment("idle", "2026-11-13T02:00:00Z", 5_000) with { IsActive = false };
        var yesterday = Segment("yesterday", "2026-11-12T01:00:00Z", 6_000) with { LocalDate = "2026-11-12" };
        var result = HourlyUsageProjector.WithPending(HourlyUsageProjector.EmptyDay(), [active, idle, yesterday],
            new Dictionary<string, UsageSegment>(), "2026-11-13", Zone);

        Assert.Equal(24, result.Count);
        Assert.Equal(4_000, result.Sum(row => row.EffectiveMs));
        Assert.Equal(9_000, result.Sum(row => row.SessionMs));
        Assert.Equal(0, result[10].EffectiveMs);
        Assert.Equal(5_000, result[10].SessionMs);
    }

    [Fact]
    public void HourBoundaryFromRealEngineProducesTwoMatchingChartBuckets()
    {
        var origin = DateTimeOffset.Parse("2026-11-13T01:59:59Z");
        var engine = new UsageEngine(Zone);
        var app = new AppIdentity("test", "Test app", "test.exe");
        engine.Observe(new(0, origin, 0, app, true, null));
        engine.Observe(new(2_000, origin.AddSeconds(2), 2_000, app, true, null));
        engine.Seal();
        var segments = engine.DrainChanges();
        var result = HourlyUsageProjector.WithPending(HourlyUsageProjector.EmptyDay(), segments,
            new Dictionary<string, UsageSegment>(), "2026-11-13", Zone);

        Assert.Equal(1_000, result[9].EffectiveMs);
        Assert.Equal(1_000, result[10].EffectiveMs);
        Assert.Equal(segments.Sum(s => s.DurationMs), result.Sum(row => row.SessionMs));
    }

    private static UsageSegment Segment(string id, string timestamp, long duration)
    {
        var start = DateTimeOffset.Parse(timestamp).ToUnixTimeMilliseconds();
        return new(id, "run", 1, "epoch", "test", "Test app", "test.exe", "2026-11-13", Zone.Id,
            start, start + duration, 0, duration, true, 1, false);
    }
}
