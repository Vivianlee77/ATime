namespace ScreenTimeCompanion.App.Services;

public sealed class AppSettings
{
    public int IdleThresholdSeconds { get; set; } = 300;
    public bool Paused { get; set; }
    public bool AlwaysOnTop { get; set; } = true;
    public double? WidgetLeft { get; set; }
    public double? WidgetTop { get; set; }
    public string ReportingTimeZoneId { get; set; } = TimeZoneInfo.Local.Id;
    public bool SessionMetric { get; set; }
    public bool HasStarted { get; set; }
    public AppSettings Copy() => (AppSettings)MemberwiseClone();
}

public sealed record DailyUsageRow(string LocalDate, string AppId, string AppName, long EffectiveMs, long SessionMs);
public sealed record TrackerViewState(DateOnly Today, IReadOnlyList<DailyUsageRow> Rows,
    AppSettings Settings, string Status, string CurrentApp, bool Healthy,
    DateTimeOffset? LastSavedUtc, long GapCount);

public interface ITrackerService
{
    TrackerViewState State { get; }
    event Action? Changed;
    Task SetPausedAsync(bool paused);
    Task SetIdleThresholdAsync(int seconds);
    Task SetAlwaysOnTopAsync(bool enabled);
    Task SaveWindowPositionAsync(double left, double top);
    Task ExportCsvAsync(string path, int days, bool sessionMetric);
    Task ClearHistoryAsync();
    void ShowWidget();
    void ShowDashboard();
    void Quit();
}
