namespace ScreenTimeCompanion.Core;

public sealed record AppIdentity(string Id, string DisplayName, string ExecutableName)
{
    public static AppIdentity Unknown { get; } = new("unknown", "无法识别的应用", "");
    public static AppIdentity Self { get; } = new("self", "屏幕时光", "ScreenTimeCompanion.App");
    public static AppIdentity Desktop { get; } = new("system-desktop", "桌面与系统界面", "");
    public static AppIdentity Private { get; } = new("private", "隐私时段", "");
}

public sealed record TrackingObservation(
    long MonotonicMs, DateTimeOffset UtcNow, long? LastInputMonotonicMs,
    AppIdentity App, bool Eligible, string? InactiveReason, long? UnbiasedMs = null);

public sealed record UsageSegment(
    string Id, string RunId, long Sequence, string EpochId,
    string AppId, string AppName, string ExecutableName,
    string LocalDate, string TimeZoneId, long StartUtcMs, long EndUtcMs,
    long StartMonoMs, long EndMonoMs, bool IsActive, long Revision, bool IsSealed)
{
    public long DurationMs => EndMonoMs - StartMonoMs;
    public long EffectiveMs => IsActive ? DurationMs : 0;
}
