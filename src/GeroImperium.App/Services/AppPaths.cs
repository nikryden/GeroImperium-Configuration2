using System.IO;

namespace GeroImperium.App.Services;

/// <summary>Resolves where the App keeps its local authoring database.</summary>
public static class AppPaths
{
    private static string RootDirectory
    {
        get
        {
            var dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "timeonline",
                "GeroImperium");
            Directory.CreateDirectory(dir);
            return dir;
        }
    }

    public static string AuthoringDatabasePath => Path.Combine(RootDirectory, "GeroImperiumData.db");

    /// <summary>Where SyncViewModel's automatic pre-Pull backups and any manual backups live -- kept alongside
    /// the live database rather than buried in a temp folder, since a backup is only useful if the user can
    /// actually find it again to restore from.</summary>
    public static string BackupDirectory
    {
        get
        {
            var dir = Path.Combine(RootDirectory, "Backups");
            Directory.CreateDirectory(dir);
            return dir;
        }
    }

    /// <summary>A fresh, timestamped backup file path -- never collides with an existing backup, and the name
    /// alone tells the user when and why it was taken.</summary>
    public static string NewBackupFilePath(string reason) =>
        Path.Combine(BackupDirectory, $"GeroImperiumData.{reason}.{DateTime.Now:yyyyMMdd-HHmmss}.db");
}
