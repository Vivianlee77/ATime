namespace ATime.Storage;

public static class DataPaths
{
    /// <summary>Reuse an existing database in place; never copy an open SQLite WAL database.</summary>
    public static string ResolveDefaultDirectory(string? localApplicationData = null)
    {
        var root = localApplicationData ?? Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var current = Path.Combine(root, "ATime");
        var legacy = Path.Combine(root, "ScreenTimeCompanion");
        if (File.Exists(Path.Combine(current, "usage.db"))) return current;
        return File.Exists(Path.Combine(legacy, "usage.db")) ? legacy : current;
    }
}
