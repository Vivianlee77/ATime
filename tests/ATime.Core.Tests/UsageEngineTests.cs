using ATime.Core;
using Xunit;

namespace ATime.Core.Tests;

public sealed class UsageEngineTests
{
    private static readonly AppIdentity AppA = new("app-a", "Application A", "a.exe");
    private static readonly AppIdentity AppB = new("app-b", "Application B", "b.exe");
    private static readonly DateTimeOffset Origin = new(2026, 11, 12, 0, 0, 0, TimeSpan.Zero);

    [Fact]
    public void CT01_ForegroundSwitchAssignsEachIntervalToExactlyOneApplication()
    {
        var run = new Replay();
        run.Observe(0, AppA);
        run.Through(1, 120, AppA);
        run.Observe(120_000, AppB);
        run.Through(121, 180, AppB);

        Assert.Equal(180_000, run.SessionMs);
        Assert.Equal(180_000, run.EffectiveMs);
        Assert.Equal(120_000, run.ForApp(AppA.Id));
        Assert.Equal(60_000, run.ForApp(AppB.Id));
        AssertAccountingInvariants(run.Segments);
    }

    [Fact]
    public void CT02And03_IdleGraceIsKeptAndNewInputDoesNotBackfillIdleTime()
    {
        var run = new Replay();
        run.Observe(0, input: 0);
        run.Through(1, 720, input: 0);
        Assert.Equal(300_000, run.EffectiveMs);
        Assert.Equal(720_000, run.SessionMs);

        run.Observe(720_000, input: 720_000);
        run.Through(721, 750, input: 720_000);
        Assert.Equal(330_000, run.EffectiveMs);
        Assert.Equal(750_000, run.SessionMs);
        AssertAccountingInvariants(run.Segments);
    }

    [Theory]
    [InlineData("Locked")]
    [InlineData("DisplayOff")]
    [InlineData("Paused")]
    [InlineData("UnsupportedSession")]
    [InlineData("StorageError")]
    public void CT04To06And18_IneligibleIntervalsAreNeverRecorded(string reason)
    {
        var run = new Replay();
        run.Observe(0);
        run.Through(1, 60);
        run.Observe(60_000, eligible: false, reason: reason);
        run.Through(61, 180, eligible: false, reason: reason);
        run.Observe(180_000);
        run.Through(181, 240);

        Assert.Equal(120_000, run.SessionMs);
        Assert.Equal(120_000, run.EffectiveMs);
        Assert.DoesNotContain(run.Segments, s => s.StartMonoMs < 180_000 && s.EndMonoMs > 60_000);
    }

    [Fact]
    public void CT05_DimReasonDoesNotOverrideCallerEligibility()
    {
        var run = new Replay();
        run.Observe(0, reason: "Dim");
        run.Through(1, 240, reason: "Dim");
        Assert.Equal(240_000, run.SessionMs);
        Assert.Equal(240_000, run.EffectiveMs);
    }

    [Fact]
    public void CT07_PrivacyIdentityOnlyAffectsFutureIntervals()
    {
        var run = new Replay();
        run.Observe(0, AppA);
        run.Through(1, 60, AppA);
        run.Observe(60_000, AppIdentity.Private);
        run.Through(61, 120, AppIdentity.Private);

        Assert.Equal(60_000, run.ForApp(AppA.Id));
        Assert.Equal(60_000, run.ForApp(AppIdentity.Private.Id));
        Assert.All(run.Segments.Where(s => s.StartMonoMs >= 60_000), segment =>
        {
            Assert.Equal(AppIdentity.Private.DisplayName, segment.AppName);
            Assert.Equal("", segment.ExecutableName);
        });
    }

    [Fact]
    public void CT08_UnknownSelfAndDesktopRemainAccountableBuckets()
    {
        var run = new Replay();
        var apps = new[] { AppA, AppIdentity.Unknown, AppIdentity.Self, AppIdentity.Desktop };
        for (var index = 0; index < apps.Length; index++)
        {
            run.Observe(index * 30_000, apps[index]);
            run.Through(index * 30 + 1, (index + 1) * 30, apps[index]);
        }

        Assert.Equal(120_000, run.SessionMs);
        Assert.All(apps, app => Assert.Equal(30_000, run.ForApp(app.Id)));
    }

    [Fact]
    public void CT09_MidnightSplitsTheTwoReportingDates()
    {
        var zone = TimeZoneInfo.CreateCustomTimeZone("Fixed UTC+8", TimeSpan.FromHours(8), "UTC+8", "UTC+8");
        var origin = new DateTimeOffset(2026, 11, 12, 15, 58, 0, TimeSpan.Zero);
        var run = new Replay(zone, origin);
        run.Observe(0);
        run.Through(1, 300);

        Assert.Equal(120_000, run.Segments.Where(s => s.LocalDate == "2026-11-12").Sum(s => s.DurationMs));
        Assert.Equal(180_000, run.Segments.Where(s => s.LocalDate == "2026-11-13").Sum(s => s.DurationMs));
        Assert.All(run.Segments, segment => Assert.Equal(zone.Id, segment.TimeZoneId));
    }

    [Theory]
    [InlineData(3_600_000)]
    [InlineData(-3_600_000)]
    public void CT10And11_WallClockJumpsCreateEpochsWithoutChangingElapsedUsage(long adjustment)
    {
        var run = new Replay();
        run.Observe(0);
        run.Through(1, 60);
        run.Observe(60_000, utcShift: adjustment);
        run.Through(61, 120, utcShift: adjustment);

        Assert.Equal(120_000, run.SessionMs);
        Assert.Equal(120_000, run.EffectiveMs);
        Assert.Equal(1, run.Engine.ClockAdjustmentCount);
        Assert.Equal(2, run.Segments.Select(s => s.EpochId).Distinct().Count());
        AssertAccountingInvariants(run.Segments);
    }

    [Fact]
    public void CT12_LongSamplingGapDiscardsTheWholeUnconfirmedInterval()
    {
        var run = new Replay();
        run.Observe(0);
        run.Through(1, 10);
        run.Observe(20_000);
        Assert.Equal(10_000, run.SessionMs);
        Assert.Equal(1, run.Engine.GapCount);
        run.Through(21, 25);
        Assert.Equal(15_000, run.SessionMs);
        Assert.DoesNotContain(run.Segments, s => s.StartMonoMs < 20_000 && s.EndMonoMs > 10_000);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void CT13_SleepDoesNotExtendThePreSleepApplication(bool receivedSuspend)
    {
        var run = new Replay();
        run.Observe(0);
        run.Through(1, 10);
        if (receivedSuspend)
        {
            run.Observe(10_000, eligible: false, reason: "Suspended");
        }
        run.Observe(3_610_000, unbiased: 10_000);
        for (long time = 3_611_000; time <= 3_620_000; time += 1_000)
        {
            run.Observe(time, unbiased: time - 3_600_000);
        }
        Assert.Equal(20_000, run.SessionMs);
        AssertAccountingInvariants(run.Segments);
    }

    [Fact]
    public void UnbiasedClockDetectsShortSleepEvenBelowTheSamplingGapThreshold()
    {
        var run = new Replay();
        run.Observe(0, unbiased: 0);
        run.Observe(1_000, unbiased: 1_000);
        run.Observe(3_000, unbiased: 1_100);
        run.Observe(4_000, unbiased: 2_100);

        Assert.Equal(2_000, run.SessionMs);
        Assert.Equal(1, run.Engine.GapCount);
    }

    [Fact]
    public void CT14_DuplicatedAndLateObservationsCannotRewindTheForeground()
    {
        var run = new Replay();
        run.Observe(0, AppA);
        run.Observe(1_000, AppA);
        run.Observe(1_000, AppA);
        run.Observe(2_000, AppB);
        run.Observe(500, AppA, eligible: false);
        run.Observe(3_000, AppB);

        Assert.Equal(2_000, run.ForApp(AppA.Id));
        Assert.Equal(1_000, run.ForApp(AppB.Id));
        Assert.Equal(AppB, run.Engine.Latest!.App);
        AssertAccountingInvariants(run.Segments);
    }

    [Fact]
    public void CT16_ChangingIdleThresholdPreservesAlreadyAccountedGrace()
    {
        var run = new Replay();
        run.Observe(0, input: 0);
        run.Through(1, 240, input: 0);
        run.Engine.SetIdleThreshold(120);
        run.Collect();
        run.Through(241, 300, input: 0);

        Assert.Equal(240_000, run.EffectiveMs);
        Assert.Equal(300_000, run.SessionMs);
        Assert.All(run.Segments.Where(s => s.StartMonoMs >= 240_000), segment => Assert.False(segment.IsActive));
    }

    [Fact]
    public void CT17_PausedEligibilityStaysStoppedThroughOtherStateChangesAndNewRun()
    {
        var run = new Replay();
        run.Observe(0, eligible: false, reason: "Paused");
        run.Through(1, 10, eligible: false, reason: "Locked");
        run.Through(11, 20, eligible: false, reason: "Paused");
        Assert.Empty(run.Segments);

        var restarted = new Replay();
        restarted.Observe(0, eligible: false, reason: "Paused");
        restarted.Through(1, 10, eligible: false, reason: "Paused");
        Assert.Empty(restarted.Segments);
    }

    [Fact]
    public void InputDiscoveredBetweenSamplesSplitsRecoveryAtItsKnownTime()
    {
        var run = new Replay(thresholdSeconds: 1);
        run.Observe(0, input: 0);
        run.Observe(1_000, input: 0);
        run.Observe(2_000, input: 0);
        run.Observe(3_000, input: 2_250);

        Assert.Equal(1_750, run.EffectiveMs);
        Assert.Equal(3_000, run.SessionMs);
        Assert.Contains(run.Segments, s => s.StartMonoMs == 2_250 && s.IsActive);
    }

    [Fact]
    public void LateInputCannotRewriteAlreadySettledButUndrainedIntervals()
    {
        var engine = new UsageEngine(TimeZoneInfo.Utc, 1);
        engine.Observe(Sample(0, input: 0));
        engine.Observe(Sample(1_000, input: 0));
        engine.Observe(Sample(2_000, input: 0));
        // The input at 1.5 s is discovered only after [1 s, 2 s) was settled.
        engine.Observe(Sample(3_000, input: 1_500));
        var segments = engine.DrainChanges();

        Assert.Equal(1_500, segments.Sum(s => s.EffectiveMs));
        Assert.Equal(3_000, segments.Sum(s => s.DurationMs));
        Assert.Contains(segments, s => !s.IsActive && s.StartMonoMs == 1_000 && s.EndMonoMs == 2_000);
    }

    [Fact]
    public void MissingOrFutureInputNeverCreatesEffectiveUsage()
    {
        var engine = new UsageEngine(TimeZoneInfo.Utc);
        engine.Observe(Sample(0, input: null));
        engine.Observe(Sample(1_000, input: 99_999));
        engine.Observe(Sample(2_000, input: null));
        var segments = engine.DrainChanges();

        Assert.Equal(2_000, segments.Sum(s => s.DurationMs));
        Assert.Equal(0, segments.Sum(s => s.EffectiveMs));
    }

    [Fact]
    public void CheckpointsReuseIdentityAndSameEndpointSealHasANewerRevision()
    {
        var engine = new UsageEngine(TimeZoneInfo.Utc);
        engine.Observe(Sample(0));
        for (var second = 1; second <= 5; second++) engine.Observe(Sample(second * 1_000));
        var first = Assert.Single(engine.DrainChanges());
        Assert.Equal(5_000, first.DurationMs);
        Assert.False(first.IsSealed);
        Assert.Empty(engine.DrainChanges());

        for (var second = 6; second <= 10; second++) engine.Observe(Sample(second * 1_000));
        var secondCheckpoint = Assert.Single(engine.DrainChanges());
        Assert.Equal(first.Id, secondCheckpoint.Id);
        Assert.Equal(10_000, secondCheckpoint.DurationMs);
        Assert.True(secondCheckpoint.Revision > first.Revision);

        engine.Seal();
        var sealedSegment = Assert.Single(engine.DrainChanges());
        Assert.Equal(secondCheckpoint.Id, sealedSegment.Id);
        Assert.Equal(secondCheckpoint.EndMonoMs, sealedSegment.EndMonoMs);
        Assert.Equal(secondCheckpoint.Revision + 1, sealedSegment.Revision);
        Assert.True(sealedSegment.IsSealed);
        engine.Seal();
        Assert.Empty(engine.DrainChanges());
    }

    [Fact]
    public void SealedSegmentsNeverExtendAndMinuteLimitCreatesNewIds()
    {
        var run = new Replay();
        run.Observe(0);
        run.Through(1, 61);
        Assert.Equal(2, run.Segments.Count);
        Assert.Equal(60_000, run.Segments[0].DurationMs);
        Assert.True(run.Segments[0].IsSealed);
        Assert.Equal(1_000, run.Segments[1].DurationMs);
        Assert.NotEqual(run.Segments[0].Id, run.Segments[1].Id);
    }

    [Theory]
    [InlineData("2026-03-08T06:59:59+00:00", 1, 3)]
    [InlineData("2026-11-01T05:59:59+00:00", 1, 1)]
    public void DaylightSavingTransitionsSplitLocalHourAndOffset(string timestamp, int firstHour, int secondHour)
    {
        var zone = CreateDaylightSavingZone();
        var run = new Replay(zone, DateTimeOffset.Parse(timestamp));
        run.Observe(0);
        run.Observe(2_000);
        var segments = run.Segments;

        Assert.Equal(2, segments.Count);
        Assert.Equal(1_000, segments[0].DurationMs);
        Assert.Equal(1_000, segments[1].DurationMs);
        var first = TimeZoneInfo.ConvertTime(DateTimeOffset.FromUnixTimeMilliseconds(segments[0].StartUtcMs), zone);
        var second = TimeZoneInfo.ConvertTime(DateTimeOffset.FromUnixTimeMilliseconds(segments[1].StartUtcMs), zone);
        Assert.Equal(firstHour, first.Hour);
        Assert.Equal(secondHour, second.Hour);
        Assert.NotEqual(first.Offset, second.Offset);
    }

    [Fact]
    public void IrregularSamplesUseElapsedMillisecondsRatherThanTimerCounts()
    {
        var run = new Replay();
        foreach (var time in new long[] { 0, 79, 412, 1_277, 2_116, 4_977, 5_001 }) run.Observe(time);
        Assert.Equal(5_001, run.SessionMs);
        Assert.Equal(5_001, run.EffectiveMs);
        Assert.Equal(0, run.Engine.GapCount);
    }

    [Fact]
    public void RandomizedTransitionsPreservePartitionAndDurationInvariants()
    {
        var run = new Replay(thresholdSeconds: 3);
        var random = new Random(7712);
        long time = 0;
        long input = 0;
        var eligible = true;
        long expectedSession = 0;
        var previousEligible = true;
        var apps = new[] { AppA, AppB, AppIdentity.Unknown, AppIdentity.Private };
        run.Observe(0);

        for (var index = 0; index < 2_000; index++)
        {
            var delta = random.Next(1, 4_000);
            time += delta;
            if (random.Next(5) == 0) input = time;
            eligible = random.Next(8) != 0;
            if (previousEligible && delta <= 3_000) expectedSession += delta;
            run.Observe(time, apps[random.Next(apps.Length)], input, eligible);
            previousEligible = eligible;
        }

        Assert.Equal(expectedSession, run.SessionMs);
        AssertAccountingInvariants(run.Segments);
        Assert.Equal(run.SessionMs, run.Segments.GroupBy(s => s.AppId).Sum(group => group.Sum(s => s.DurationMs)));
        Assert.Equal(run.EffectiveMs, run.Segments.GroupBy(s => s.AppId).Sum(group => group.Sum(s => s.EffectiveMs)));
    }

    [Fact]
    public void NewEngineDoesNotResumeOrInventAPreviousRun()
    {
        var previous = new Replay();
        previous.Observe(0);
        previous.Through(1, 10);
        var restarted = new Replay(origin: Origin.AddHours(1));
        restarted.Observe(0);
        restarted.Observe(1_000);

        Assert.Equal(1_000, restarted.SessionMs);
        Assert.NotEqual(previous.Segments[0].RunId, restarted.Segments[0].RunId);
        Assert.NotEqual(previous.Segments[0].EpochId, restarted.Segments[0].EpochId);
    }

    private static void AssertAccountingInvariants(IReadOnlyList<UsageSegment> segments)
    {
        var ordered = segments.OrderBy(s => s.StartMonoMs).ToArray();
        Assert.All(ordered, segment =>
        {
            Assert.InRange(segment.DurationMs, 1, 60_000);
            Assert.InRange(segment.EffectiveMs, 0, segment.DurationMs);
            Assert.Equal(segment.DurationMs, segment.EndUtcMs - segment.StartUtcMs);
        });
        for (var index = 1; index < ordered.Length; index++)
        {
            Assert.True(ordered[index - 1].EndMonoMs <= ordered[index].StartMonoMs, "Monotonic usage intervals overlap.");
            Assert.True(ordered[index - 1].Sequence < ordered[index].Sequence);
        }
    }

    private static TrackingObservation Sample(long mono, long? input = 0) =>
        new(mono, Origin.AddMilliseconds(mono), input, AppA, true, null, mono);

    private static TimeZoneInfo CreateDaylightSavingZone()
    {
        var daylightStart = TimeZoneInfo.TransitionTime.CreateFloatingDateRule(new DateTime(1, 1, 1, 2, 0, 0), 3, 2, DayOfWeek.Sunday);
        var daylightEnd = TimeZoneInfo.TransitionTime.CreateFloatingDateRule(new DateTime(1, 1, 1, 2, 0, 0), 11, 1, DayOfWeek.Sunday);
        var rule = TimeZoneInfo.AdjustmentRule.CreateAdjustmentRule(new DateTime(2026, 1, 1), new DateTime(2026, 12, 31), TimeSpan.FromHours(1), daylightStart, daylightEnd);
        return TimeZoneInfo.CreateCustomTimeZone("Test Eastern", TimeSpan.FromHours(-5), "Test Eastern", "Standard", "Daylight", [rule]);
    }

    private sealed class Replay
    {
        private readonly Dictionary<string, UsageSegment> _segments = [];
        private readonly DateTimeOffset _origin;

        public Replay(TimeZoneInfo? zone = null, DateTimeOffset? origin = null, int thresholdSeconds = 300)
        {
            _origin = origin ?? Origin;
            Engine = new UsageEngine(zone ?? TimeZoneInfo.Utc, thresholdSeconds);
        }

        public UsageEngine Engine { get; }
        public IReadOnlyList<UsageSegment> Segments => _segments.Values.OrderBy(s => s.Sequence).ToArray();
        public long SessionMs => _segments.Values.Sum(s => s.DurationMs);
        public long EffectiveMs => _segments.Values.Sum(s => s.EffectiveMs);
        public long ForApp(string appId) => _segments.Values.Where(s => s.AppId == appId).Sum(s => s.DurationMs);

        public void Observe(long mono, AppIdentity? app = null, long? input = null, bool eligible = true,
            string? reason = null, long utcShift = 0, long? unbiased = null)
        {
            Engine.Observe(new TrackingObservation(mono, _origin.AddMilliseconds(mono + utcShift),
                input ?? mono, app ?? AppA, eligible, reason, unbiased ?? mono));
            Collect();
        }

        public void Through(int firstSecond, int lastSecond, AppIdentity? app = null, long? input = null,
            bool eligible = true, string? reason = null, long utcShift = 0)
        {
            for (var second = firstSecond; second <= lastSecond; second++)
                Observe((long)second * 1_000, app, input, eligible, reason, utcShift);
        }

        public void Collect()
        {
            foreach (var segment in Engine.DrainChanges())
            {
                if (_segments.TryGetValue(segment.Id, out var old))
                {
                    Assert.True(segment.Revision > old.Revision);
                    Assert.False(old.IsSealed, "A sealed segment was modified.");
                    Assert.Equal(old.StartMonoMs, segment.StartMonoMs);
                    Assert.True(segment.EndMonoMs >= old.EndMonoMs);
                }
                _segments[segment.Id] = segment;
            }
        }
    }
}
