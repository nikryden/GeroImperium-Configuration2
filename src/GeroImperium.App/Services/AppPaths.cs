using System.IO;

namespace GeroImperium.App.Services;

/// <summary>Resolves where the App keeps its local authoring database.</summary>
public static class AppPaths
{
    public static string AuthoringDatabasePath
    {
        get
        {
            var dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "timeonline",
                "GeroImperium");
            Directory.CreateDirectory(dir);
            return Path.Combine(dir, "GeroImperiumData.db");
        }
    }
}
