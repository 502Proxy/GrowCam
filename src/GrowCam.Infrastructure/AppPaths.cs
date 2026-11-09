namespace GrowCam.Infrastructure;

/// <summary>Where GrowCam keeps its data (database, photos, settings, logs).</summary>
public static class AppPaths
{
    /// <summary>
    /// Windows: %LOCALAPPDATA%\GrowCamData, macOS: ~/Library/Application Support/GrowCamData.
    /// Kept separate from the program folder so updates never touch user data.
    /// </summary>
    public static string DataDirectory { get; } = Resolve();

    public static string Database => Path.Combine(DataDirectory, "growcam.db");
    public static string Images => Path.Combine(DataDirectory, "images");
    public static string Settings => Path.Combine(DataDirectory, "settings.json");
    public static string Log => Path.Combine(DataDirectory, "growcam.log");

    private static string Resolve()
    {
        var overridden = Environment.GetEnvironmentVariable("GROWCAM_DATA_DIR");
        var dir = string.IsNullOrWhiteSpace(overridden)
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "GrowCamData")
            : overridden;
        Directory.CreateDirectory(dir);
        return dir;
    }
}
