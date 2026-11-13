using System.Text.Json;
using ATime.Core;
using ATime.Storage;
using ATime.Windows;
using ATime.App.Services;
using System.Windows.Threading;

namespace ATime.App;

internal static class Diagnostics
{
    public static async Task<int> ControllerTestAsync(string[] args)
    {
        var directory = App.Argument(args,"--data-dir");
        if (string.IsNullOrWhiteSpace(directory)) return 2;
        Directory.CreateDirectory(directory);
        TrackerService? tracker = null;
        try
        {
            if (File.Exists(Path.Combine(directory,"usage.db"))) throw new InvalidOperationException("Controller test requires a fresh, isolated data directory.");
            tracker = new TrackerService(directory, Dispatcher.CurrentDispatcher);
            tracker.Start();
            await Task.Delay(4000).ConfigureAwait(false);
            await tracker.SetPausedAsync(true).ConfigureAwait(false);
            var before = tracker.State.Rows.Sum(r=>r.SessionMs);
            await Task.Delay(3000).ConfigureAwait(false);
            var after = tracker.State.Rows.Sum(r=>r.SessionMs);
            CheckHourlyTotals(tracker.State);
            if (!tracker.State.Settings.Paused || before != after) throw new InvalidOperationException("Pause did not freeze usage.");
            await tracker.StopAsync().ConfigureAwait(false); tracker = null;
            tracker = new TrackerService(directory,Dispatcher.CurrentDispatcher);
            if (!tracker.State.Settings.Paused) throw new InvalidOperationException("Pause was not preserved after restart.");
            tracker.Start(); await Task.Delay(2000).ConfigureAwait(false);
            var restarted = tracker.State.Rows.Sum(r=>r.SessionMs);
            CheckHourlyTotals(tracker.State);
            if (restarted != after) throw new InvalidOperationException("Restart filled the stopped interval.");
            await tracker.SetPausedAsync(false).ConfigureAwait(false);
            await Task.Delay(3000).ConfigureAwait(false);
            await tracker.SetPausedAsync(true).ConfigureAwait(false);
            var resumed = tracker.State.Rows.Sum(r=>r.SessionMs);
            CheckHourlyTotals(tracker.State);
            if (resumed <= restarted) throw new InvalidOperationException("Resume did not restart usage in an eligible local session.");
            await tracker.ExportCsvAsync(Path.Combine(directory,"controller-export.csv"),1,false).ConfigureAwait(false);
            await tracker.ClearHistoryAsync().ConfigureAwait(false);
            if (tracker.State.Rows.Count != 0 || !tracker.State.Settings.Paused) throw new InvalidOperationException("Clear did not preserve pause or empty history.");
            CheckHourlyTotals(tracker.State);
            await tracker.StopAsync().ConfigureAwait(false); tracker=null;
            File.WriteAllText(Path.Combine(directory,"controller-result.json"),JsonSerializer.Serialize(new { Success=true, BeforePause=before,AfterPause=after,AfterRestart=restarted,AfterResume=resumed, CsvExport=true, ClearPreservesPause=true, HourlyTotalsMatch=true },new JsonSerializerOptions { WriteIndented=true }));
            return 0;
        }
        catch(Exception error) { File.WriteAllText(Path.Combine(directory,"controller-error.txt"),error.ToString()); return 1; }
        finally { if (tracker is not null) { try { await tracker.StopAsync().ConfigureAwait(false); } catch { } } }
    }

    private static void CheckHourlyTotals(TrackerViewState state)
    {
        var today = state.Today.ToString("yyyy-MM-dd");
        var rows = state.Rows.Where(row => row.LocalDate == today).ToArray();
        if (state.HourlyRows.Count != 24 ||
            state.HourlyRows.Sum(row => row.SessionMs) != rows.Sum(row => row.SessionMs) ||
            state.HourlyRows.Sum(row => row.EffectiveMs) != rows.Sum(row => row.EffectiveMs))
            throw new InvalidOperationException("Hourly chart totals do not match today's application totals.");
    }

    public static async Task<int> RunAsync(string[] args)
    {
        var directory = App.Argument(args,"--data-dir");
        if (string.IsNullOrEmpty(directory)) return 2;
        Directory.CreateDirectory(directory);
        var seconds = int.TryParse(App.Argument(args,"--seconds"),out var value) ? Math.Clamp(value,5,60) : 12;
        try
        {
            using var store = new UsageStore(Path.Combine(directory,"smoke.db"));
            using var source = new WindowsSignalSource();
            var engine = new UsageEngine(TimeZoneInfo.Local);
            var sync = new object();
            var samples = new List<object>();
            var pending = new Dictionary<string,UsageSegment>();
            source.Observation += observation => {
                lock(sync) {
                    engine.Observe(observation);
                    foreach (var segment in engine.DrainChanges()) pending[segment.Id] = segment;
                    samples.Add(new { observation.MonotonicMs, observation.Eligible, observation.InactiveReason,
                        App = observation.App.DisplayName, IdleMs = observation.LastInputMonotonicMs is long last ? observation.MonotonicMs-last : (long?)null });
                }
            };
            source.Start();
            await Task.Delay(TimeSpan.FromSeconds(seconds));
            source.Dispose();
            lock(sync) {
                engine.Seal(); foreach (var segment in engine.DrainChanges()) pending[segment.Id] = segment;
                store.Commit(pending.Values);
                var rows = store.QueryDaily("0001-01-01","9999-12-31");
                var report = new { Success = samples.Count>0, SQLite = store.NativeVersion, Integrity=store.IntegrityCheck(),
                    SampleCount=samples.Count, TotalEffectiveMs=rows.Sum(r=>r.EffectiveMs), TotalSessionMs=rows.Sum(r=>r.SessionMs),
                    engine.GapCount, engine.ClockAdjustmentCount, Samples=samples, Rows=rows };
                File.WriteAllText(Path.Combine(directory,"smoke-result.json"),JsonSerializer.Serialize(report,new JsonSerializerOptions { WriteIndented=true }));
            }
            return 0;
        }
        catch(Exception error) { File.WriteAllText(Path.Combine(directory,"smoke-error.txt"),error.ToString()); return 1; }
    }
}
