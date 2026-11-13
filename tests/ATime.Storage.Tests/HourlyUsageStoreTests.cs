using ATime.Core;
using ATime.Storage;
using Microsoft.Data.Sqlite;
using Xunit;

namespace ATime.Storage.Tests;

public sealed class HourlyUsageStoreTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "ATime.Hourly.Tests", Guid.NewGuid().ToString("N"));
    private string Database => Path.Combine(_root, "usage.db");
    private static readonly TimeZoneInfo Zone = TimeZoneInfo.CreateCustomTimeZone("Chart UTC+8", TimeSpan.FromHours(8), "UTC+8", "UTC+8");

    [Fact]
    public void ExistingHistoryLoadsWithoutSchemaMigrationAndMatchesDailyTotals()
    {
        using (var original = new UsageStore(Database))
            original.Commit([Segment("a", 1, "2026-11-13T00:00:00Z", 12_000),
                Segment("b", 2, "2026-11-13T01:00:00Z", 20_000, false)]);
        using var reopened = new UsageStore(Database);
        var hours = reopened.QueryHourly("2026-11-13", Zone);
        var daily = reopened.QueryDaily("2026-11-13", "2026-11-13");
        Assert.Equal(24, hours.Count);
        Assert.Equal(12_000, hours[8].EffectiveMs);
        Assert.Equal(0, hours[9].EffectiveMs);
        Assert.Equal(20_000, hours[9].SessionMs);
        Assert.Equal(daily.Sum(row => row.EffectiveMs), hours.Sum(row => row.EffectiveMs));
        Assert.Equal(daily.Sum(row => row.SessionMs), hours.Sum(row => row.SessionMs));
        Assert.Equal("ok", reopened.IntegrityCheck());
    }

    [Fact]
    public void CachedHoursApplyRevisionsOnceAndDoNotMutatePublishedSnapshots()
    {
        using var store = new UsageStore(Database);
        var first = Segment("open", 1, "2026-11-13T01:00:00Z", 1_000);
        store.Commit([first]);
        var oldSnapshot = store.QueryHourly("2026-11-13", Zone);
        var extended = first with { EndMonoMs = 5_000, EndUtcMs = first.StartUtcMs + 5_000, Revision = 2 };
        store.Commit([extended, extended]);
        store.Commit([extended]);
        Assert.Equal(1_000, oldSnapshot[9].SessionMs);
        Assert.Equal(5_000, store.QueryHourly("2026-11-13", Zone)[9].SessionMs);
        var live = HourlyUsageProjector.WithPending(store.QueryHourly("2026-11-13", Zone),
            [extended with { EndMonoMs = 6_000, Revision = 3 }], new Dictionary<string, UsageSegment> { [extended.Id] = extended },
            "2026-11-13", Zone);
        Assert.Equal(6_000, live[9].SessionMs);
    }

    [Fact]
    public void FailedTransactionLeavesHourlyCacheAndDatabaseAtThePreviousRevision()
    {
        using var store = new UsageStore(Database);
        var first = Segment("first", 1, "2026-11-13T01:00:00Z", 1_000);
        store.Commit([first]);
        _ = store.QueryHourly("2026-11-13", Zone);
        var extended = first with { EndMonoMs = 5_000, EndUtcMs = first.StartUtcMs + 5_000, Revision = 2 };
        var duplicateSequence = Segment("conflict", 1, "2026-11-13T02:00:00Z", 3_000);
        Assert.Throws<SqliteException>(() => store.Commit([extended, duplicateSequence]));
        Assert.Equal(1_000, store.QueryHourly("2026-11-13", Zone).Sum(row => row.SessionMs));
        Assert.Equal(1_000, store.QueryDaily("2026-11-13", "2026-11-13").Sum(row => row.SessionMs));
        store.Commit([extended]);
        Assert.Equal(5_000, store.QueryHourly("2026-11-13", Zone).Sum(row => row.SessionMs));
    }

    [Fact]
    public void SwitchingDaysAndClearingHistoryCannotReuseStaleHours()
    {
        using var store = new UsageStore(Database);
        store.Commit([Segment("yesterday", 1, "2026-11-12T15:59:00Z", 12_000, date: "2026-11-12"),
            Segment("today", 2, "2026-11-12T16:00:00Z", 8_000)]);
        Assert.Equal(12_000, store.QueryHourly("2026-11-12", Zone)[23].SessionMs);
        Assert.Equal(8_000, store.QueryHourly("2026-11-13", Zone)[0].SessionMs);
        Assert.All(store.QueryHourly("2026-11-14", Zone), row => Assert.Equal(0, row.SessionMs));
        store.SaveSetting("preserved", true);
        store.ClearHistory();
        Assert.All(store.QueryHourly("2026-11-13", Zone), row => Assert.Equal(0, row.SessionMs));
        Assert.True(store.LoadSetting<bool>("preserved"));
    }

    [Fact]
    public void ClockChangesUseMonotonicDurationAndRepeatedDstHourKeepsBothIntervals()
    {
        var daylightStart = TimeZoneInfo.TransitionTime.CreateFloatingDateRule(new DateTime(1, 1, 1, 2, 0, 0), 3, 2, DayOfWeek.Sunday);
        var daylightEnd = TimeZoneInfo.TransitionTime.CreateFloatingDateRule(new DateTime(1, 1, 1, 2, 0, 0), 11, 1, DayOfWeek.Sunday);
        var rule = TimeZoneInfo.AdjustmentRule.CreateAdjustmentRule(new DateTime(2026, 1, 1), new DateTime(2026, 12, 31), TimeSpan.FromHours(1), daylightStart, daylightEnd);
        var zone = TimeZoneInfo.CreateCustomTimeZone("Chart Eastern", TimeSpan.FromHours(-5), "Eastern", "Standard", "Daylight", [rule]);
        using var store = new UsageStore(Database);
        var first = Segment("first-hour", 1, "2026-11-01T05:30:00Z", 60_000, date: "2026-11-01") with { TimeZoneId = zone.Id };
        var second = Segment("repeated-hour", 2, "2026-11-01T06:30:00Z", 60_000, date: "2026-11-01") with { TimeZoneId = zone.Id };
        store.Commit([first, second with { EndUtcMs = second.StartUtcMs + 3_600_000 }]);
        var hours = store.QueryHourly("2026-11-01", zone);
        Assert.Equal(120_000, hours[1].SessionMs);
        Assert.Equal(store.QueryDaily("2026-11-01", "2026-11-01").Sum(row => row.SessionMs), hours.Sum(row => row.SessionMs));
    }

    private static UsageSegment Segment(string id, long sequence, string timestamp, long duration, bool active = true, string date = "2026-11-13")
    {
        var start = DateTimeOffset.Parse(timestamp).ToUnixTimeMilliseconds();
        return new(id, "run", sequence, "epoch", "test", "Test app", "test.exe", date, Zone.Id,
            start, start + duration, 0, duration, active, 1, false);
    }

    public void Dispose()
    {
        var expectedParent = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "ATime.Hourly.Tests")) + Path.DirectorySeparatorChar;
        var full = Path.GetFullPath(_root);
        if (!full.StartsWith(expectedParent, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("Unsafe test cleanup path.");
        if (Directory.Exists(full)) Directory.Delete(full, true);
    }
}
