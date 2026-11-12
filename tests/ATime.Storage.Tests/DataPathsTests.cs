using ATime.Storage;
using Xunit;

namespace ATime.Storage.Tests;

public sealed class DataPathsTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "ATime.DataPaths.Tests", Guid.NewGuid().ToString("N"));

    [Fact]
    public void FreshInstallUsesATimeWithoutCreatingOrMovingFiles()
    {
        Assert.Equal(Path.Combine(_root, "ATime"), DataPaths.ResolveDefaultDirectory(_root));
        Assert.False(Directory.Exists(_root));
    }

    [Fact]
    public void LegacyDatabaseAndSettingsRemainAccessibleInPlace()
    {
        var legacy = Path.Combine(_root, "ScreenTimeCompanion");
        using (var store = new UsageStore(Path.Combine(legacy, "usage.db")))
            store.SaveSetting("paused", true);

        Directory.CreateDirectory(Path.Combine(_root, "ATime"));
        var selected = DataPaths.ResolveDefaultDirectory(_root);
        Assert.Equal(legacy, selected);
        using var reopened = new UsageStore(Path.Combine(selected, "usage.db"));
        Assert.True(reopened.LoadSetting<bool>("paused"));
        Assert.Equal("ok", reopened.IntegrityCheck());
        Assert.False(File.Exists(Path.Combine(_root, "ATime", "usage.db")));
    }

    [Fact]
    public void ExistingATimeDatabaseTakesPriorityWithoutOverwritingEitherDatabase()
    {
        var current = Path.Combine(_root, "ATime");
        var legacy = Path.Combine(_root, "ScreenTimeCompanion");
        using (var store = new UsageStore(Path.Combine(legacy, "usage.db"))) store.SaveSetting("owner", "legacy");
        using (var store = new UsageStore(Path.Combine(current, "usage.db"))) store.SaveSetting("owner", "current");

        Assert.Equal(current, DataPaths.ResolveDefaultDirectory(_root));
        using var currentStore = new UsageStore(Path.Combine(current, "usage.db"));
        using var legacyStore = new UsageStore(Path.Combine(legacy, "usage.db"));
        Assert.Equal("current", currentStore.LoadSetting<string>("owner"));
        Assert.Equal("legacy", legacyStore.LoadSetting<string>("owner"));
    }

    public void Dispose()
    {
        var expectedParent = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "ATime.DataPaths.Tests")) + Path.DirectorySeparatorChar;
        var full = Path.GetFullPath(_root);
        if (!full.StartsWith(expectedParent, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("Unsafe test cleanup path.");
        if (Directory.Exists(full)) Directory.Delete(full, true);
    }
}
