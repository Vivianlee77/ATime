using System.Globalization;
using System.Text;
using System.Threading.Channels;
using System.Windows.Threading;
using ATime.Core;
using ATime.Storage;
using ATime.Windows;

namespace ATime.App.Services;

public sealed class TrackerService : ITrackerService
{
    private readonly UsageStore _store;
    private readonly WindowsSignalSource _source = new();
    private readonly Dispatcher _dispatcher;
    private readonly Channel<Action> _queue = Channel.CreateUnbounded<Action>(new UnboundedChannelOptions { SingleReader = true });
    private readonly Dictionary<string, UsageSegment> _pending = new();
    private readonly Dictionary<string, UsageSegment> _committedOpen = new();
    private IReadOnlyList<StoredDailyUsage> _baseRows = Array.Empty<StoredDailyUsage>();
    private AppSettings _settings;
    private UsageEngine _engine;
    private readonly TimeZoneInfo _timeZone;
    private TrackerViewState _state;
    private TrackingObservation? _latest;
    private DateTimeOffset? _lastSaved;
    private long _lastCommitMono;
    private long _minimumObservationMono;
    private long _reportedGaps;
    private long _reportedClockChanges;
    private long _previousRunGaps;
    private bool _healthy = true;
    private bool _settingsDirty;
    private bool _stopping;
    private string? _error;
    private readonly Task _worker;
    private long _queuedObservations;

    public TrackerViewState State => Volatile.Read(ref _state);
    public event Action? Changed;
    public event Action? WidgetRequested;
    public event Action? DashboardRequested;
    public event Action? QuitRequested;
    public string DataDirectory { get; }

    public TrackerService(string directory, Dispatcher dispatcher)
    {
        DataDirectory = directory;
        _dispatcher = dispatcher;
        _store = new UsageStore(Path.Combine(directory, "usage.db"));
        _settings = _store.LoadSetting<AppSettings>("preferences") ?? new AppSettings();
        if (!new[] {60,180,300,600,900}.Contains(_settings.IdleThresholdSeconds)) _settings.IdleThresholdSeconds = 300;
        _timeZone = TimeZoneInfo.FindSystemTimeZoneById(_settings.ReportingTimeZoneId);
        _engine = new UsageEngine(_timeZone, _settings.IdleThresholdSeconds);
        _state = new(Today(), Array.Empty<DailyUsageRow>(), _settings.Copy(), "等待系统状态", "—", true, null, 0);
        ReloadBase();
        Publish();
        _worker = Task.Run(async () => {
            await foreach (var work in _queue.Reader.ReadAllAsync())
            {
                try { work(); }
                catch (Exception error) { MarkFailure(error); Publish(); }
            }
        });
        _source.Observation += observation => {
            if (_stopping) return;
            if (Interlocked.Increment(ref _queuedObservations) > 32) { Interlocked.Decrement(ref _queuedObservations); return; }
            if (!_queue.Writer.TryWrite(() => { Interlocked.Decrement(ref _queuedObservations); OnObservation(observation); }))
                Interlocked.Decrement(ref _queuedObservations);
        };
    }

    public void Start() => _source.Start();

    private void OnObservation(TrackingObservation observation)
    {
        if (observation.MonotonicMs < _minimumObservationMono || observation.MonotonicMs < (_latest?.MonotonicMs ?? 0)) return;
        // Old buffered samples after a slow disk must not simulate a live collector.
        if (Environment.TickCount64 - observation.MonotonicMs > 3000) return;
        _latest = observation;
        if (!_healthy)
        {
            if (observation.MonotonicMs - _lastCommitMono >= 5000)
            {
                _lastCommitMono = observation.MonotonicMs;
                try {
                    Flush(); _healthy = true; _error = null;
                    _previousRunGaps += _engine.GapCount;
                    _engine = new UsageEngine(_timeZone, _settings.IdleThresholdSeconds);
                    _reportedGaps = _reportedClockChanges = 0;
                }
                catch (Exception error) { MarkFailure(error); }
            }
            Publish();
            return;
        }
        var normalized = Normalize(observation);
        _engine.Observe(normalized);
        foreach (var change in _engine.DrainChanges()) _pending[change.Id] = change;
        var boundary = _pending.Values.Any(s => s.IsSealed);
        if (boundary || observation.MonotonicMs - _lastCommitMono >= 5000)
        {
            try { Flush(); }
            catch (Exception error) { MarkFailure(error); }
        }
        Publish();
    }

    private TrackingObservation Normalize(TrackingObservation observation) => observation with {
        Eligible = observation.Eligible && !_settings.Paused && _healthy,
        InactiveReason = !_healthy ? "StorageError" : _settings.Paused ? "Paused" : observation.InactiveReason
    };

    private void Flush()
    {
        if (_settingsDirty) { _store.SaveSetting("preferences", _settings); _settingsDirty = false; }
        if (_pending.Count > 0)
        {
            _store.Commit(_pending.Values.ToArray());
            ReloadBase();
            foreach (var segment in _pending.Values)
            {
                if (segment.IsSealed) _committedOpen.Remove(segment.Id);
                else _committedOpen[segment.Id] = segment;
            }
            _pending.Clear();
            _lastSaved = DateTimeOffset.UtcNow;
        }
        if (_engine.GapCount > _reportedGaps) _store.RecordCoverage("SamplingGap", _engine.GapCount - _reportedGaps, DateTimeOffset.UtcNow);
        if (_engine.ClockAdjustmentCount > _reportedClockChanges) _store.RecordCoverage("ClockAdjustment", _engine.ClockAdjustmentCount - _reportedClockChanges, DateTimeOffset.UtcNow);
        _reportedGaps = _engine.GapCount;
        _reportedClockChanges = _engine.ClockAdjustmentCount;
        _lastCommitMono = Environment.TickCount64;
        ReloadBase();
    }

    private void ReloadBase()
    {
        var today = Today();
        _baseRows = _store.QueryDaily(today.AddDays(-29).ToString("yyyy-MM-dd"), today.ToString("yyyy-MM-dd"));
    }

    private DateOnly Today() => DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow, _timeZone).DateTime);

    private void Publish()
    {
        var rows = _baseRows.ToDictionary(r => (r.LocalDate, r.AppId), r => new DailyUsageRow(r.LocalDate,r.AppId,r.AppName,r.EffectiveMs,r.SessionMs));
        foreach (var segment in _pending.Values)
        {
            _committedOpen.TryGetValue(segment.Id, out var baseline);
            var key = (segment.LocalDate, segment.AppId);
            rows.TryGetValue(key, out var row);
            rows[key] = new(segment.LocalDate,segment.AppId,segment.AppName,
                (row?.EffectiveMs ?? 0) + segment.EffectiveMs - (baseline?.EffectiveMs ?? 0),
                (row?.SessionMs ?? 0) + segment.DurationMs - (baseline?.DurationMs ?? 0));
        }
        var normalized = _latest is null ? null : Normalize(_latest);
        var idle = normalized?.LastInputMonotonicMs is long last && normalized.MonotonicMs-last >= _settings.IdleThresholdSeconds*1000L;
        var status = !_healthy ? "保存失败 · 已停止记录" : _settings.Paused ? "已暂停记录" : normalized?.Eligible == true ?
            (idle ? "空闲中 · 有效使用暂停" : "正在记录") : Reason(normalized?.InactiveReason);
        var snapshot = new TrackerViewState(Today(), rows.Values.ToArray(), _settings.Copy(), status,
            _latest?.App.DisplayName ?? "—", _healthy, _lastSaved, _previousRunGaps + _engine.GapCount);
        Volatile.Write(ref _state, snapshot);
        if (!_dispatcher.HasShutdownStarted) _dispatcher.BeginInvoke(() => Changed?.Invoke());
    }

    private static string Reason(string? reason) => reason switch {
        "Locked" => "锁屏中 · 已停止记录", "DisplayOff" => "显示已关闭 · 已停止记录",
        "Disconnected" => "会话已断开", "UnsupportedSession" => "远程会话暂不支持",
        "Suspended" => "系统挂起 · 已停止记录", "CollectorError" => "采集受限 · 等待重试",
        "InputUnknown" => "等待输入状态", _ => "等待系统状态"
    };

    private void MarkFailure(Exception error)
    {
        _healthy = false;
        _error = error.GetType().Name;
        _engine.Seal();
        foreach (var change in _engine.DrainChanges()) _pending[change.Id] = change;
        // Do not collect any new intervals while storage is unhealthy. The small last batch is retained.
    }

    private Task Enqueue(Action work)
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        if (_stopping || !_queue.Writer.TryWrite(() => {
            try { work(); completion.SetResult(); }
            catch (Microsoft.Data.Sqlite.SqliteException error) { MarkFailure(error); Publish(); completion.SetException(error); }
            catch (Exception error) { completion.SetException(error); }
        })) completion.TrySetException(new InvalidOperationException("程序正在退出。"));
        return completion.Task;
    }

    private void Boundary()
    {
        if (_latest is null) return;
        var now = _latest with { MonotonicMs = Environment.TickCount64, UtcNow = DateTimeOffset.UtcNow, UnbiasedMs = null };
        OnObservation(now);
    }

    private void SaveSettings(AppSettings settings)
    {
        _store.SaveSetting("preferences", settings);
        _settings = settings;
    }

    public Task SetPausedAsync(bool paused) => Enqueue(() => {
        Boundary();
        var settings = _settings.Copy(); settings.Paused = paused; _settings = settings; _settingsDirty = true;
        Boundary(); _engine.Seal(); foreach (var segment in _engine.DrainChanges()) _pending[segment.Id] = segment;
        try { Flush(); }
        catch (Exception error) { MarkFailure(error); throw new IOException("记录已停止，但设置暂时无法保存，请检查磁盘空间。",error); }
        finally { Publish(); }
    });

    public Task SetIdleThresholdAsync(int seconds) => Enqueue(() => {
        if (!new[] {60,180,300,600,900}.Contains(seconds)) throw new ArgumentOutOfRangeException(nameof(seconds));
        Boundary(); var settings = _settings.Copy(); settings.IdleThresholdSeconds = seconds; SaveSettings(settings);
        _engine.SetIdleThreshold(seconds); foreach (var segment in _engine.DrainChanges()) _pending[segment.Id] = segment;
        Flush(); Publish();
    });

    public Task SetAlwaysOnTopAsync(bool enabled) => Enqueue(() => {
        var settings = _settings.Copy(); settings.AlwaysOnTop = enabled; SaveSettings(settings); Publish();
    });

    public Task SaveWindowPositionAsync(double left, double top) => Enqueue(() => {
        if (!double.IsFinite(left) || !double.IsFinite(top)) return;
        var settings = _settings.Copy(); settings.WidgetLeft = left; settings.WidgetTop = top; SaveSettings(settings); Publish();
    });

    public Task ExportCsvAsync(string path, int days, bool sessionMetric) => Enqueue(() => {
        if (days is not (1 or 7 or 30)) throw new ArgumentOutOfRangeException(nameof(days));
        Boundary(); Flush();
        var today = Today();
        var rows = _store.QueryDaily(today.AddDays(1-days).ToString("yyyy-MM-dd"),today.ToString("yyyy-MM-dd"));
        using var stream = new StreamWriter(path, false, new UTF8Encoding(true));
        stream.WriteLine("date,timezone_id,app_name,app_kind,effective_seconds,session_seconds,selected_metric,selected_seconds,record_status");
        foreach (var row in rows)
        {
            var kind = row.AppId is "self" or "system-desktop" or "unknown" or "private" ? row.AppId : "application";
            stream.WriteLine(string.Join(",", Csv(row.LocalDate), Csv(_timeZone.Id), Csv(row.AppName), Csv(kind),
                Seconds(row.EffectiveMs),Seconds(row.SessionMs),sessionMetric ? "session" : "effective",
                Seconds(sessionMetric ? row.SessionMs : row.EffectiveMs),"observed_intervals_only"));
        }
        Publish();
    });

    private static string Seconds(long ms) => (ms/1000m).ToString("0.000",CultureInfo.InvariantCulture);
    private static string Csv(string value)
    {
        if (value.Length>0 && "=+-@\t\r\n".Contains(value[0])) value = "'"+value;
        return "\""+value.Replace("\"","\"\"")+"\"";
    }

    public Task ClearHistoryAsync() => Enqueue(() => {
        Boundary(); _engine.Seal(); foreach (var segment in _engine.DrainChanges()) _pending[segment.Id] = segment;
        Flush(); _store.ClearHistory(); _pending.Clear(); _committedOpen.Clear();
        _engine = new UsageEngine(_timeZone,_settings.IdleThresholdSeconds);
        _reportedGaps = _reportedClockChanges = _previousRunGaps = 0;
        _minimumObservationMono = Environment.TickCount64;
        _latest = null; _lastSaved = null; _healthy = true; _error = null;
        ReloadBase(); Publish();
    });

    public void ShowWidget() => _dispatcher.BeginInvoke(() => WidgetRequested?.Invoke());
    public void ShowDashboard() => _dispatcher.BeginInvoke(() => DashboardRequested?.Invoke());
    public void Quit() => _dispatcher.BeginInvoke(() => QuitRequested?.Invoke());

    public async Task StopAsync()
    {
        if (_stopping) return;
        _source.Dispose();
        try { await Enqueue(() => { Boundary(); _engine.Seal(); foreach (var segment in _engine.DrainChanges()) _pending[segment.Id] = segment; Flush(); }).ConfigureAwait(false); }
        finally { _stopping = true; _queue.Writer.TryComplete(); await _worker.ConfigureAwait(false); _store.Dispose(); }
    }
}
