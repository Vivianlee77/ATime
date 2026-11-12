using System.Globalization;

namespace ATime.Core;

/// <summary>
/// Serial, deterministic accounting for already-sampled Windows observations.
/// The caller owns sampling, eligibility, privacy replacement and persistence.
/// </summary>
public sealed class UsageEngine
{
    private const long MaximumSampleGapMs = 3_000;
    private const long MaximumSegmentMs = 60_000;
    private const long ClockAdjustmentToleranceMs = 2_000;
    private const long UnbiasedClockToleranceMs = 250;

    private readonly TimeZoneInfo _timeZone;
    private readonly string _runId;
    private readonly Dictionary<string, UsageSegment> _changes = [];
    private long _idleThresholdMs;
    private long _sequence;
    private long _anchorMonoMs;
    private long _anchorUtcMs;
    private string _epochId = "";
    private UsageSegment? _current;

    public UsageEngine(TimeZoneInfo timeZone, int idleThresholdSeconds = 300, string? runId = null)
    {
        ArgumentNullException.ThrowIfNull(timeZone);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(idleThresholdSeconds);
        _timeZone = timeZone;
        _idleThresholdMs = (long)idleThresholdSeconds * 1_000;
        _runId = string.IsNullOrWhiteSpace(runId) ? Guid.NewGuid().ToString("N") : runId;
    }

    public long GapCount { get; private set; }
    public long ClockAdjustmentCount { get; private set; }
    public TrackingObservation? Latest { get; private set; }

    /// <summary>
    /// Settles [previous observation, this observation) under the previous state.
    /// Input discovered inside that unsettled interval may split it; older samples
    /// never rewind the settlement watermark or modify already-accounted intervals.
    /// </summary>
    public void Observe(TrackingObservation observation)
    {
        ArgumentNullException.ThrowIfNull(observation);
        ArgumentNullException.ThrowIfNull(observation.App);
        ArgumentOutOfRangeException.ThrowIfNegative(observation.MonotonicMs);

        var previous = Latest;
        if (previous is null)
        {
            BeginEpoch(observation);
            Latest = observation;
            return;
        }

        if (observation.MonotonicMs < previous.MonotonicMs)
        {
            // The signal source must resample after a delayed event; an old
            // observation must never change the present application or state.
            return;
        }

        var elapsedMs = observation.MonotonicMs - previous.MonotonicMs;
        var expectedUtcMs = ToUtcMs(observation.MonotonicMs);
        var clockChanged = Math.Abs(observation.UtcNow.ToUnixTimeMilliseconds() - expectedUtcMs)
            > ClockAdjustmentToleranceMs;
        var discontinuous = elapsedMs > MaximumSampleGapMs || HasSleepDiscontinuity(previous, observation);

        if (discontinuous)
        {
            // Discard the entire unobserved interval, including its first seconds.
            Seal();
            GapCount++;
            if (clockChanged)
            {
                ClockAdjustmentCount++;
            }
            BeginEpoch(observation);
        }
        else
        {
            if (previous.Eligible && elapsedMs > 0)
            {
                Settle(previous, observation);
            }

            if (clockChanged)
            {
                // Finish old time using its old anchor; never add the wall-clock jump.
                Seal();
                ClockAdjustmentCount++;
                BeginEpoch(observation);
            }

            if (!observation.Eligible || observation.App.Id != previous.App.Id)
            {
                Seal();
            }
        }

        Latest = observation;
    }

    /// <summary>
    /// Returns each changed segment once, at its latest revision. The coordinator
    /// may drain each second for UI snapshots and persist its pending set every
    /// five seconds. A failed write must retry that retained set, not drain again.
    /// </summary>
    public IReadOnlyList<UsageSegment> DrainChanges()
    {
        var result = _changes.Values.OrderBy(segment => segment.Sequence).ToArray();
        _changes.Clear();
        return result;
    }

    /// <summary>Seals at the last observed endpoint; never invents an exit-time interval.</summary>
    public void Seal()
    {
        if (_current is null)
        {
            return;
        }

        var sealedSegment = _current with { IsSealed = true, Revision = _current.Revision + 1 };
        _changes[sealedSegment.Id] = sealedSegment;
        _current = null;
    }

    /// <summary>Call after observing the command's timestamp. History is never reclassified.</summary>
    public void SetIdleThreshold(int seconds)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(seconds);
        Seal();
        _idleThresholdMs = (long)seconds * 1_000;
    }

    private void Settle(TrackingObservation previous, TrackingObservation observation)
    {
        var start = previous.MonotonicMs;
        var end = observation.MonotonicMs;
        var lastInput = ValidInput(previous);
        var discoveredInput = ValidInput(observation);

        if (discoveredInput is long newInput && (lastInput is null || newInput > lastInput))
        {
            if (newInput > start && newInput < end)
            {
                AppendActivity(previous.App, start, newInput, lastInput);
                AppendActivity(previous.App, newInput, end, newInput);
                return;
            }

            if (newInput <= start)
            {
                // Late information can classify only the still-unsettled suffix.
                lastInput = newInput;
            }
        }

        AppendActivity(previous.App, start, end, lastInput);
    }

    private void AppendActivity(AppIdentity app, long start, long end, long? lastInput)
    {
        if (start >= end)
        {
            return;
        }

        if (lastInput is not long input)
        {
            Append(app, start, end, isActive: false);
            return;
        }

        var expiresAt = input > long.MaxValue - _idleThresholdMs
            ? long.MaxValue
            : input + _idleThresholdMs;
        if (expiresAt > start && expiresAt < end)
        {
            Append(app, start, expiresAt, isActive: true);
            Append(app, expiresAt, end, isActive: false);
        }
        else
        {
            Append(app, start, end, isActive: start < expiresAt);
        }
    }

    private void Append(AppIdentity app, long start, long end, bool isActive)
    {
        var cursor = start;
        while (cursor < end)
        {
            var bucket = BucketAt(cursor);
            if (_current is not null &&
                (_current.AppId != app.Id || _current.IsActive != isActive ||
                 _current.EpochId != _epochId || _current.EndMonoMs != cursor ||
                 BucketAt(_current.StartMonoMs) != bucket))
            {
                Seal();
            }

            var segmentStart = _current?.StartMonoMs ?? cursor;
            var limit = Math.Min(end, segmentStart + MaximumSegmentMs);
            if (BucketAt(limit - 1) != bucket)
            {
                limit = FirstBucketChange(cursor, limit, bucket);
            }

            if (_current is null)
            {
                _current = new UsageSegment(
                    Guid.NewGuid().ToString("N"), _runId, ++_sequence, _epochId,
                    app.Id, app.DisplayName, app.ExecutableName, bucket.Date, _timeZone.Id,
                    ToUtcMs(cursor), ToUtcMs(limit), cursor, limit, isActive, 1, false);
            }
            else
            {
                _current = _current with
                {
                    EndMonoMs = limit,
                    EndUtcMs = ToUtcMs(limit),
                    Revision = _current.Revision + 1
                };
            }

            _changes[_current.Id] = _current;
            cursor = limit;

            if (_current.DurationMs == MaximumSegmentMs || BucketAt(cursor) != bucket)
            {
                Seal();
            }
        }
    }

    private long FirstBucketChange(long start, long end, CalendarBucket original)
    {
        // Observe admits at most 3 seconds of continuous data. Within this short
        // interval a local hour/date/offset transition is a single boundary.
        var low = start + 1;
        var high = end;
        while (low < high)
        {
            var middle = low + (high - low) / 2;
            if (BucketAt(middle) == original)
            {
                low = middle + 1;
            }
            else
            {
                high = middle;
            }
        }
        return low;
    }

    private CalendarBucket BucketAt(long monotonicMs)
    {
        var local = TimeZoneInfo.ConvertTime(DateTimeOffset.FromUnixTimeMilliseconds(ToUtcMs(monotonicMs)), _timeZone);
        return new CalendarBucket(local.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), local.Hour, local.Offset);
    }

    private long ToUtcMs(long monotonicMs) => _anchorUtcMs + (monotonicMs - _anchorMonoMs);

    private void BeginEpoch(TrackingObservation observation)
    {
        _epochId = Guid.NewGuid().ToString("N");
        _anchorMonoMs = observation.MonotonicMs;
        _anchorUtcMs = observation.UtcNow.ToUnixTimeMilliseconds();
    }

    private static long? ValidInput(TrackingObservation observation) =>
        observation.LastInputMonotonicMs is long input && input <= observation.MonotonicMs ? input : null;

    private static bool HasSleepDiscontinuity(TrackingObservation previous, TrackingObservation current)
    {
        if (previous.UnbiasedMs is not long before || current.UnbiasedMs is not long after)
        {
            return false;
        }

        var awakeElapsed = after - before;
        var monotonicElapsed = current.MonotonicMs - previous.MonotonicMs;
        return awakeElapsed < 0 || Math.Abs(monotonicElapsed - awakeElapsed) > UnbiasedClockToleranceMs;
    }

    private readonly record struct CalendarBucket(string Date, int Hour, TimeSpan Offset);
}
