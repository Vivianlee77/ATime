using Microsoft.Data.Sqlite;
using ScreenTimeCompanion.Core;
using ScreenTimeCompanion.Storage;
using Xunit;

namespace ScreenTimeCompanion.Storage.Tests;

public sealed class UsageStoreTests
{
    private static readonly DateTimeOffset Origin = new(2026, 11, 12, 0, 0, 0, TimeSpan.Zero);

    [Fact]
    public void DuplicateRevisionIsIdempotentAcrossBatchesAndWithinOneBatch()
    {
        using var files = new TestFiles();
        using var store = files.OpenStore();
        var segment = Segment("first", 1, 0, 5_000);

        store.Commit([segment, segment, segment]);
        store.Commit([segment]);
        store.Commit([segment]);

        Assert.Equal(segment, store.Find(segment.Id));
        Assert.Equal(1, files.ScalarLong("SELECT COUNT(*) FROM usage_segments;"));
        var row = Assert.Single(store.QueryDaily("2026-11-12", "2026-11-12"));
        Assert.Equal(5_000, row.SessionMs);
        Assert.Equal(5_000, row.EffectiveMs);
        Assert.Equal("ok", store.IntegrityCheck());
    }

    [Fact]
    public void OlderCheckpointCannotReplaceNewerEndpointOrDoubleCountItsContribution()
    {
        using var files = new TestFiles();
        using var store = files.OpenStore();
        var first = Segment("growing", 1, 0, 5_000);
        var latest = first with { EndMonoMs = 10_000, EndUtcMs = Origin.AddSeconds(10).ToUnixTimeMilliseconds(), Revision = 2 };

        store.Commit([first]);
        store.Commit([latest]);
        store.Commit([first]);

        Assert.Equal(latest, store.Find(first.Id));
        var row = Assert.Single(store.QueryDaily("2026-11-12", "2026-11-12"));
        Assert.Equal(10_000, row.SessionMs);
        Assert.Equal(10_000, row.EffectiveMs);
        Assert.Equal(2, files.ScalarLong("SELECT revision FROM usage_segments WHERE id='growing';"));
    }

    [Fact]
    public void SameEndpointSealPersistsWithoutIncreasingUsageAndRejectsLaterExtension()
    {
        using var files = new TestFiles();
        using var store = files.OpenStore();
        var first = Segment("seal", 1, 0, 5_000);
        var sealedSegment = first with { Revision = 2, IsSealed = true };
        store.Commit([first]);
        store.Commit([sealedSegment]);
        store.Commit([sealedSegment]);

        Assert.Equal(sealedSegment, store.Find(first.Id));
        Assert.Equal(5_000, Assert.Single(store.QueryDaily("2026-11-12", "2026-11-12")).SessionMs);
        var committedSequence = store.CommitSequence;
        var forbiddenExtension = sealedSegment with
        {
            EndMonoMs = 6_000,
            EndUtcMs = Origin.AddSeconds(6).ToUnixTimeMilliseconds(),
            Revision = 3
        };

        Assert.Throws<InvalidOperationException>(() => store.Commit([forbiddenExtension]));
        Assert.Equal(committedSequence, store.CommitSequence);
        Assert.Equal(sealedSegment, store.Find(first.Id));
        Assert.Equal(5_000, Assert.Single(store.QueryDaily("2026-11-12", "2026-11-12")).SessionMs);
    }

    [Fact]
    public void AConstraintFailureLateInTheBatchRollsBackRawRowsAggregatesAndCommitSequence()
    {
        using var files = new TestFiles();
        using var store = files.OpenStore();
        var original = Segment("original", 1, 0, 5_000);
        store.Commit([original]);
        var beforeSequence = store.CommitSequence;
        var extended = original with { EndMonoMs = 10_000, EndUtcMs = Origin.AddSeconds(10).ToUnixTimeMilliseconds(), Revision = 2 };
        var newRow = Segment("new", 2, 10_000, 15_000, app: "app-b");
        // A different ID with an existing (run_id, sequence_no) violates a real DB constraint.
        var constraintViolation = Segment("duplicate-sequence", 1, 15_000, 20_000);

        Assert.Throws<SqliteException>(() => store.Commit([extended, newRow, constraintViolation]));

        Assert.Equal(original, store.Find(original.Id));
        Assert.Null(store.Find(newRow.Id));
        Assert.Null(store.Find(constraintViolation.Id));
        Assert.Equal(beforeSequence, store.CommitSequence);
        Assert.Equal(beforeSequence, files.ScalarLong("SELECT commit_seq FROM store_state WHERE id=1;"));
        Assert.Equal(1, files.ScalarLong("SELECT COUNT(*) FROM usage_segments;"));
        var row = Assert.Single(store.QueryDaily("2026-11-12", "2026-11-12"));
        Assert.Equal(5_000, row.SessionMs);
        Assert.Equal(5_000, row.EffectiveMs);

        // The connection remains usable after transaction disposal rolled back the failure.
        store.Commit([extended, newRow]);
        Assert.Equal(15_000, store.QueryDaily("2026-11-12", "2026-11-12").Sum(r => r.SessionMs));
    }

    [Fact]
    public void AnInvalidDurationLateInTheBatchAlsoRollsBackEarlierChanges()
    {
        using var files = new TestFiles();
        using var store = files.OpenStore();
        var first = Segment("first", 1, 0, 1_000);
        var invalid = Segment("zero-length", 2, 1_000, 1_000);

        Assert.Throws<ArgumentOutOfRangeException>(() => store.Commit([first, invalid]));

        Assert.Null(store.Find(first.Id));
        Assert.Empty(store.QueryDaily("2026-11-12", "2026-11-12"));
        Assert.Equal(0, store.CommitSequence);
    }

    [Fact]
    public void DailyCacheEqualsIndependentRawAggregationAcrossApplicationsDatesAndCheckpoints()
    {
        using var files = new TestFiles();
        using var store = files.OpenStore();
        var growing = Segment("active-a", 1, 0, 5_000);
        var records = new[]
        {
            growing,
            Segment("idle-a", 2, 10_000, 17_000, active: false),
            Segment("active-b", 3, 17_000, 25_000, app: "app-b"),
            Segment("private", 4, 25_000, 29_000, app: "private"),
            Segment("yesterday", 5, 29_000, 31_000, date: "2026-11-11"),
            Segment("tomorrow-idle", 6, 31_000, 35_000, app: "unknown", date: "2026-11-13", active: false)
        };
        store.Commit(records);
        store.Commit([growing with { EndMonoMs = 10_000, EndUtcMs = Origin.AddSeconds(10).ToUnixTimeMilliseconds(), Revision = 2 }]);

        using var read = files.OpenReadConnection();
        using var command = read.CreateCommand();
        command.CommandText = """
            SELECT local_date, app_id,
                   SUM(CASE WHEN active=1 THEN end_mono-start_mono ELSE 0 END),
                   SUM(end_mono-start_mono)
            FROM usage_segments GROUP BY local_date, app_id ORDER BY local_date, app_id;
            """;
        using var reader = command.ExecuteReader();
        var independent = new List<(string Date, string App, long Effective, long Session)>();
        while (reader.Read()) independent.Add((reader.GetString(0), reader.GetString(1), reader.GetInt64(2), reader.GetInt64(3)));
        var cached = store.QueryDaily("2026-11-11", "2026-11-13")
            .Select(row => (row.LocalDate, row.AppId, row.EffectiveMs, row.SessionMs)).ToArray();

        Assert.Equal(independent, cached);
        Assert.Equal(35_000, cached.Sum(row => row.SessionMs));
        Assert.Equal(24_000, cached.Sum(row => row.EffectiveMs));
        Assert.All(store.QueryDaily("2026-11-12", "2026-11-12"), row => Assert.Equal("2026-11-12", row.LocalDate));
    }

    [Fact]
    public void SettingsRoundTripAndOverwriteSurviveClosingTheDatabase()
    {
        using var files = new TestFiles();
        var privacyRules = new Dictionary<string, string> { ["private-app-hash"] = "隐私应用" };
        using (var store = files.OpenStore())
        {
            Assert.Null(store.LoadSetting<string>("missing"));
            store.SaveSetting("paused", true);
            store.SaveSetting("threshold", 300);
            store.SaveSetting("threshold", 120);
            store.SaveSetting("privacy", privacyRules);
            store.SaveSetting("window", new WindowPreference(125.5, -40, true, "跟随系统"));
        }

        using var reopened = files.OpenStore();
        Assert.True(reopened.LoadSetting<bool>("paused"));
        Assert.Equal(120, reopened.LoadSetting<int>("threshold"));
        Assert.Equal(privacyRules, reopened.LoadSetting<Dictionary<string, string>>("privacy"));
        Assert.Equal(new WindowPreference(125.5, -40, true, "跟随系统"), reopened.LoadSetting<WindowPreference>("window"));
    }

    [Fact]
    public void ClearHistoryDeletesUsageAndCoverageButPreservesSettingsAndPausePreference()
    {
        using var files = new TestFiles();
        using var store = files.OpenStore();
        var segment = Segment("clear-me", 1, 0, 5_000);
        store.Commit([segment]);
        store.RecordCoverage("SamplingGap", 1, Origin);
        store.SaveSetting("paused", true);
        store.SaveSetting("privacy", new[] { "private-app-hash" });
        store.SaveSetting("timezone", "China Standard Time");
        var oldCommit = store.CommitSequence;
        var oldGeneration = files.ScalarLong("SELECT generation FROM store_state WHERE id=1;");

        store.ClearHistory();

        Assert.Null(store.Find(segment.Id));
        Assert.Empty(store.QueryDaily("2020-01-01", "2099-12-31"));
        Assert.Equal(0, files.ScalarLong("SELECT COUNT(*) FROM usage_segments;"));
        Assert.Equal(0, files.ScalarLong("SELECT COUNT(*) FROM coverage_events;"));
        Assert.True(store.LoadSetting<bool>("paused"));
        Assert.Equal(new[] { "private-app-hash" }, store.LoadSetting<string[]>("privacy"));
        Assert.Equal("China Standard Time", store.LoadSetting<string>("timezone"));
        Assert.Equal(oldCommit + 1, store.CommitSequence);
        Assert.Equal(oldGeneration + 1, files.ScalarLong("SELECT generation FROM store_state WHERE id=1;"));
        Assert.Equal("ok", store.IntegrityCheck());
    }

    [Fact]
    public void OnlineBackupIsAnIndependentConsistentSnapshotWhileSourceRemainsOpen()
    {
        using var files = new TestFiles();
        using var store = files.OpenStore();
        var first = Segment("first", 1, 0, 5_000);
        store.Commit([first]);
        store.SaveSetting("paused", true);
        store.RecordCoverage("SamplingGap", 1, Origin);
        var snapshotCommit = store.CommitSequence;
        var backupPath = Path.Combine(files.DirectoryPath, "backup.db");

        store.Backup(backupPath);
        store.Commit([Segment("after-backup", 2, 5_000, 8_000)]);
        store.SaveSetting("paused", false);

        using var backup = new UsageStore(backupPath);
        Assert.Equal(first, backup.Find(first.Id));
        Assert.Null(backup.Find("after-backup"));
        Assert.Equal(snapshotCommit, backup.CommitSequence);
        Assert.Equal(5_000, Assert.Single(backup.QueryDaily("2026-11-12", "2026-11-12")).SessionMs);
        Assert.Equal(8_000, Assert.Single(store.QueryDaily("2026-11-12", "2026-11-12")).SessionMs);
        Assert.True(backup.LoadSetting<bool>("paused"));
        Assert.False(store.LoadSetting<bool>("paused"));
        Assert.Equal("ok", backup.IntegrityCheck());
    }

    [Fact]
    public void ReopeningTheDatabaseNeverExtendsAnUnsealedCheckpoint()
    {
        using var files = new TestFiles();
        var segment = Segment("unfinished", 1, 0, 5_000);
        long commitSequence;
        using (var first = files.OpenStore())
        {
            first.Commit([segment]);
            commitSequence = first.CommitSequence;
        }

        using var reopened = files.OpenStore();
        Assert.Equal(segment, reopened.Find(segment.Id));
        Assert.False(reopened.Find(segment.Id)!.IsSealed);
        Assert.Equal(5_000, Assert.Single(reopened.QueryDaily("2026-11-12", "2026-11-12")).SessionMs);
        Assert.Equal(commitSequence, reopened.CommitSequence);
        Assert.Equal("ok", reopened.IntegrityCheck());
    }

    [Fact]
    public void NativeSqliteVersionIncludesTheWalFixAndWrittenDatabasePassesIntegrityCheck()
    {
        using var files = new TestFiles();
        using var store = files.OpenStore();
        Assert.True(Version.Parse(store.NativeVersion) >= new Version(3, 51, 3), $"Unexpected native SQLite {store.NativeVersion}");
        store.Commit([Segment("version-check", 1, 0, 5_000)]);
        Assert.Equal("ok", store.IntegrityCheck());
        using var connection = files.OpenReadConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA journal_mode;";
        Assert.Equal("wal", command.ExecuteScalar());
    }

    [Theory]
    [InlineData("epoch")]
    [InlineData("timezone")]
    [InlineData("utc-start")]
    public void ExistingSegmentCannotBeRetriedWithDifferentTimeMappingIdentity(string change)
    {
        using var files = new TestFiles();
        using var store = files.OpenStore();
        var original = Segment("fixed-mapping", 1, 0, 5_000);
        store.Commit([original]);
        var changed = change switch
        {
            "epoch" => original with { Revision = 2, EpochId = "a-different-clock-epoch" },
            "timezone" => original with { Revision = 2, TimeZoneId = "a-different-reporting-zone" },
            "utc-start" => original with { Revision = 2, StartUtcMs = original.StartUtcMs + 1_000 },
            _ => throw new InvalidOperationException()
        };

        Assert.Throws<InvalidOperationException>(() => store.Commit([changed]));
        Assert.Equal(original, store.Find(original.Id));
    }

    private static UsageSegment Segment(string id, long sequence, long start, long end,
        string app = "app-a", string date = "2026-11-12", bool active = true)
    {
        return new UsageSegment(id, "test-run", sequence, "test-epoch", app, app == "private" ? "隐私时段" : app,
            app == "private" ? "" : app + ".exe", date, "UTC",
            Origin.AddMilliseconds(start).ToUnixTimeMilliseconds(), Origin.AddMilliseconds(end).ToUnixTimeMilliseconds(),
            start, end, active, 1, false);
    }

    public sealed record WindowPreference(double Left, double Top, bool Topmost, string Theme);

    private sealed class TestFiles : IDisposable
    {
        private readonly string _testRoot = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "ScreenTimeCompanion.Storage.Tests"));

        public TestFiles()
        {
            DirectoryPath = Path.GetFullPath(Path.Combine(_testRoot, Guid.NewGuid().ToString("N")));
            Directory.CreateDirectory(DirectoryPath);
        }

        public string DirectoryPath { get; }
        public string DatabasePath => Path.Combine(DirectoryPath, "usage.db");
        public UsageStore OpenStore() => new(DatabasePath);

        public SqliteConnection OpenReadConnection()
        {
            var connection = new SqliteConnection(new SqliteConnectionStringBuilder
            {
                DataSource = DatabasePath,
                Mode = SqliteOpenMode.ReadOnly,
                Pooling = false
            }.ToString());
            connection.Open();
            return connection;
        }

        public long ScalarLong(string sql)
        {
            using var connection = OpenReadConnection();
            using var command = connection.CreateCommand();
            command.CommandText = sql;
            return Convert.ToInt64(command.ExecuteScalar());
        }

        public void Dispose()
        {
            // Only remove the exact UUID directory created by this fixture, after
            // every store/reader has been disposed. Never clean the shared parent.
            var resolved = Path.GetFullPath(DirectoryPath);
            if (!string.Equals(Path.GetDirectoryName(resolved), _testRoot, StringComparison.OrdinalIgnoreCase) ||
                !Guid.TryParseExact(Path.GetFileName(resolved), "N", out _))
            {
                throw new InvalidOperationException("Refusing to remove a path outside this test's temporary directory.");
            }
            if (Directory.Exists(resolved)) Directory.Delete(resolved, recursive: true);
        }
    }
}
