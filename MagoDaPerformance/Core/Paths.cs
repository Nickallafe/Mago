namespace MagoDaPerformance;

public static class Paths
{
    /// <summary>Pasta de dados: RIBANENSE_APP_DATA > %LOCALAPPDATA%\MagoDaPerformance.</summary>
    public static string DataDir
    {
        get
        {
            var configured = Environment.GetEnvironmentVariable("RIBANENSE_APP_DATA");
            if (!string.IsNullOrWhiteSpace(configured)) return configured!;
            var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            return Path.Combine(local, "MagoDaPerformance");
        }
    }

    public static string LogFile => Path.Combine(DataDir, "MagoDaPerformance_log.txt");
    public static string BackupFile => Path.Combine(DataDir, "mago_backup.json");
    public static string SnapshotDir => Path.Combine(DataDir, "mago_snapshots");
    public static string SettingsFile => Path.Combine(DataDir, "mago_settings.json");
}
