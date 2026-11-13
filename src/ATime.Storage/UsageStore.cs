using Microsoft.Data.Sqlite;
using ATime.Core;
using System.Globalization;
using System.Text.Json;

namespace ATime.Storage;

public sealed record StoredDailyUsage(string LocalDate, string AppId, string AppName, long EffectiveMs, long SessionMs);

/// <summary>All calls are serialized by the application's worker. No UI thread I/O.</summary>
public sealed class UsageStore : IDisposable
{
    private readonly SqliteConnection _connection;
    private HourlyCache? _hourlyCache;
    private sealed record HourlyCache(string Date, TimeZoneInfo Zone, IReadOnlyList<HourlyUsage> Rows);
    public string FilePath { get; }
    public string NativeVersion { get; }
    public long CommitSequence { get; private set; }

    public UsageStore(string filePath)
    {
        SQLitePCL.Batteries_V2.Init();
        FilePath = Path.GetFullPath(filePath);
        Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
        _connection = new SqliteConnection(new SqliteConnectionStringBuilder {
            DataSource = FilePath, Mode = SqliteOpenMode.ReadWriteCreate, Pooling = false, DefaultTimeout = 3 }.ToString());
        _connection.Open();
        NativeVersion = Scalar("SELECT sqlite_version();")?.ToString() ?? "0.0.0";
        if (Version.Parse(NativeVersion) < new Version(3, 51, 3))
            throw new InvalidOperationException("SQLite 原生库版本过旧，拒绝开启记录。");
        Execute("PRAGMA foreign_keys=ON; PRAGMA journal_mode=WAL; PRAGMA synchronous=FULL; PRAGMA busy_timeout=3000;");
        var schema = Convert.ToInt32(Scalar("PRAGMA user_version;"), CultureInfo.InvariantCulture);
        if (schema > 1) throw new InvalidOperationException("此数据库由更新版本创建，请使用新版ATime。");
        Execute("""
            CREATE TABLE IF NOT EXISTS settings(key TEXT PRIMARY KEY, value TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS store_state(id INTEGER PRIMARY KEY CHECK(id=1), commit_seq INTEGER NOT NULL, generation INTEGER NOT NULL);
            INSERT OR IGNORE INTO store_state VALUES(1,0,0);
            CREATE TABLE IF NOT EXISTS usage_segments(
              id TEXT PRIMARY KEY, run_id TEXT NOT NULL, sequence_no INTEGER NOT NULL, epoch_id TEXT NOT NULL,
              app_id TEXT NOT NULL, app_name TEXT NOT NULL, exe_name TEXT NOT NULL,
              local_date TEXT NOT NULL, timezone_id TEXT NOT NULL,
              start_utc INTEGER NOT NULL, end_utc INTEGER NOT NULL,
              start_mono INTEGER NOT NULL, end_mono INTEGER NOT NULL,
              active INTEGER NOT NULL CHECK(active IN(0,1)), revision INTEGER NOT NULL,
              sealed INTEGER NOT NULL CHECK(sealed IN(0,1)),
              CHECK(end_mono>start_mono AND end_mono-start_mono<=60000), UNIQUE(run_id,sequence_no));
            CREATE INDEX IF NOT EXISTS ix_segments_date_app ON usage_segments(local_date, app_id);
            CREATE TABLE IF NOT EXISTS daily_usage(
              local_date TEXT NOT NULL, app_id TEXT NOT NULL, app_name TEXT NOT NULL,
              effective_ms INTEGER NOT NULL CHECK(effective_ms>=0), session_ms INTEGER NOT NULL CHECK(session_ms>=effective_ms),
              PRIMARY KEY(local_date,app_id));
            CREATE TABLE IF NOT EXISTS coverage_events(
              id INTEGER PRIMARY KEY, utc_ms INTEGER NOT NULL, kind TEXT NOT NULL, count INTEGER NOT NULL);
            PRAGMA user_version=1;
            """);
        CommitSequence = Convert.ToInt64(Scalar("SELECT commit_seq FROM store_state WHERE id=1;"), CultureInfo.InvariantCulture);
    }

    public T? LoadSetting<T>(string key)
    {
        using var command = _connection.CreateCommand();
        command.CommandText = "SELECT value FROM settings WHERE key=$key;";
        command.Parameters.AddWithValue("$key", key);
        var json = command.ExecuteScalar() as string;
        return json is null ? default : JsonSerializer.Deserialize<T>(json);
    }

    public void SaveSetting<T>(string key, T value)
    {
        using var command = _connection.CreateCommand();
        command.CommandText = "INSERT INTO settings VALUES($key,$value) ON CONFLICT(key) DO UPDATE SET value=excluded.value;";
        command.Parameters.AddWithValue("$key", key);
        command.Parameters.AddWithValue("$value", JsonSerializer.Serialize(value));
        command.ExecuteNonQuery();
    }

    public long Commit(IEnumerable<UsageSegment> segments)
    {
        var cachedHours = _hourlyCache?.Rows.ToArray();
        using var transaction = _connection.BeginTransaction();
        foreach (var segment in segments)
        {
            if (segment.DurationMs <= 0 || segment.DurationMs > 60000) throw new ArgumentOutOfRangeException(nameof(segments));
            var old = Find(segment.Id, transaction);
            if (old is not null && segment.Revision <= old.Revision) continue;
            if (old is not null && (old.IsSealed || segment.StartMonoMs != old.StartMonoMs || segment.EndMonoMs < old.EndMonoMs ||
                segment.AppId != old.AppId || segment.LocalDate != old.LocalDate || segment.IsActive != old.IsActive ||
                segment.RunId != old.RunId || segment.Sequence != old.Sequence || segment.EpochId != old.EpochId ||
                segment.TimeZoneId != old.TimeZoneId || segment.StartUtcMs != old.StartUtcMs ||
                segment.AppName != old.AppName || segment.ExecutableName != old.ExecutableName))
                throw new InvalidOperationException("已保存的时间段不能被重写或延长已封口的记录。");
            using var command = _connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = """
                INSERT INTO usage_segments VALUES($id,$run,$sequence,$epoch,$app,$name,$exe,$date,$zone,$startUtc,$endUtc,$startMono,$endMono,$active,$revision,$sealed)
                ON CONFLICT(id) DO UPDATE SET end_utc=excluded.end_utc,end_mono=excluded.end_mono,revision=excluded.revision,sealed=excluded.sealed;
                """;
            Add(command, "$id", segment.Id); Add(command, "$run", segment.RunId); Add(command, "$sequence", segment.Sequence);
            Add(command, "$epoch", segment.EpochId); Add(command, "$app", segment.AppId); Add(command, "$name", segment.AppName);
            Add(command, "$exe", segment.ExecutableName); Add(command, "$date", segment.LocalDate); Add(command, "$zone", segment.TimeZoneId);
            Add(command, "$startUtc", segment.StartUtcMs); Add(command, "$endUtc", segment.EndUtcMs);
            Add(command, "$startMono", segment.StartMonoMs); Add(command, "$endMono", segment.EndMonoMs);
            Add(command, "$active", segment.IsActive ? 1 : 0); Add(command, "$revision", segment.Revision); Add(command, "$sealed", segment.IsSealed ? 1 : 0);
            command.ExecuteNonQuery();
            using var aggregate = _connection.CreateCommand();
            aggregate.Transaction = transaction;
            aggregate.CommandText = """
                INSERT INTO daily_usage VALUES($date,$app,$name,$effective,$session)
                ON CONFLICT(local_date,app_id) DO UPDATE SET
                  effective_ms=effective_ms+excluded.effective_ms,
                  session_ms=session_ms+excluded.session_ms, app_name=excluded.app_name;
                """;
            Add(aggregate, "$date", segment.LocalDate); Add(aggregate, "$app", segment.AppId); Add(aggregate, "$name", segment.AppName);
            Add(aggregate, "$effective", segment.EffectiveMs - (old?.EffectiveMs ?? 0));
            Add(aggregate, "$session", segment.DurationMs - (old?.DurationMs ?? 0));
            aggregate.ExecuteNonQuery();
            if (cachedHours is not null && _hourlyCache is { } cache)
                HourlyUsageProjector.AddDelta(cachedHours, cache.Date, cache.Zone, segment, old);
        }
        using var update = _connection.CreateCommand();
        update.Transaction = transaction;
        update.CommandText = "UPDATE store_state SET commit_seq=commit_seq+1 WHERE id=1 RETURNING commit_seq;";
        var version = Convert.ToInt64(update.ExecuteScalar(), CultureInfo.InvariantCulture);
        transaction.Commit();
        CommitSequence = version;
        if (cachedHours is not null && _hourlyCache is { } previousCache)
            _hourlyCache = previousCache with { Rows = Array.AsReadOnly(cachedHours) };
        return version;
    }

    public UsageSegment? Find(string id, SqliteTransaction? transaction = null)
    {
        using var command = _connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT * FROM usage_segments WHERE id=$id;";
        Add(command, "$id", id);
        using var r = command.ExecuteReader();
        return r.Read() ? new UsageSegment(r.GetString(0),r.GetString(1),r.GetInt64(2),r.GetString(3),r.GetString(4),r.GetString(5),r.GetString(6),
            r.GetString(7),r.GetString(8),r.GetInt64(9),r.GetInt64(10),r.GetInt64(11),r.GetInt64(12),r.GetBoolean(13),r.GetInt64(14),r.GetBoolean(15)) : null;
    }

    public IReadOnlyList<StoredDailyUsage> QueryDaily(string from, string to)
    {
        using var command = _connection.CreateCommand();
        command.CommandText = "SELECT local_date,app_id,app_name,effective_ms,session_ms FROM daily_usage WHERE local_date >= $from AND local_date <= $to ORDER BY local_date,app_id;";
        Add(command, "$from", from); Add(command, "$to", to);
        using var reader = command.ExecuteReader();
        var rows = new List<StoredDailyUsage>();
        while (reader.Read()) rows.Add(new(reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetInt64(3), reader.GetInt64(4)));
        return rows;
    }

    public IReadOnlyList<HourlyUsage> QueryHourly(string localDate, TimeZoneInfo zone)
    {
        _ = DateOnly.ParseExact(localDate, "yyyy-MM-dd", CultureInfo.InvariantCulture);
        if (_hourlyCache is { } cached && cached.Date == localDate && cached.Zone.Equals(zone)) return cached.Rows;
        var rows = HourlyUsageProjector.EmptyDay();
        using var command = _connection.CreateCommand();
        command.CommandText = "SELECT start_utc,active,end_mono-start_mono FROM usage_segments WHERE local_date=$date;";
        Add(command, "$date", localDate);
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            var hour = HourlyUsageProjector.HourAt(reader.GetInt64(0), zone);
            var duration = reader.GetInt64(2);
            var previous = rows[hour];
            rows[hour] = previous with {
                EffectiveMs = previous.EffectiveMs + (reader.GetBoolean(1) ? duration : 0),
                SessionMs = previous.SessionMs + duration
            };
        }
        _hourlyCache = new(localDate, zone, Array.AsReadOnly(rows));
        return _hourlyCache.Rows;
    }

    public void RecordCoverage(string kind, long count, DateTimeOffset utc)
    {
        using var command = _connection.CreateCommand();
        command.CommandText = "INSERT INTO coverage_events(utc_ms,kind,count) VALUES($utc,$kind,$count);";
        Add(command,"$utc",utc.ToUnixTimeMilliseconds()); Add(command,"$kind",kind); Add(command,"$count",count);
        command.ExecuteNonQuery();
    }

    public void ClearHistory()
    {
        using var transaction = _connection.BeginTransaction();
        using var command = _connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "DELETE FROM usage_segments; DELETE FROM daily_usage; DELETE FROM coverage_events; UPDATE store_state SET generation=generation+1,commit_seq=commit_seq+1 WHERE id=1 RETURNING commit_seq;";
        var seq = Convert.ToInt64(command.ExecuteScalar(), CultureInfo.InvariantCulture);
        transaction.Commit();
        CommitSequence = seq;
        _hourlyCache = null;
        Execute("PRAGMA wal_checkpoint(TRUNCATE);");
    }

    public string IntegrityCheck() => Scalar("PRAGMA integrity_check;")?.ToString() ?? "unknown";
    public void Backup(string path)
    {
        using var destination = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ToString());
        destination.Open(); _connection.BackupDatabase(destination);
    }
    private static void Add(SqliteCommand command, string name, object value) => command.Parameters.AddWithValue(name, value);
    private object? Scalar(string sql) { using var command = _connection.CreateCommand(); command.CommandText = sql; return command.ExecuteScalar(); }
    private void Execute(string sql) { using var command = _connection.CreateCommand(); command.CommandText = sql; command.ExecuteNonQuery(); }
    public void Dispose() => _connection.Dispose();
}
