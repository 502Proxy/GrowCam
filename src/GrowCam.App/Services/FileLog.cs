using GrowCam.Infrastructure;

namespace GrowCam.App.Services;

/// <summary>Minimal append-only log file in the data folder (rotated at 1 MB).</summary>
public static class FileLog
{
    private const long MaxBytes = 1_000_000;
    private static readonly Lock Gate = new();

    public static void Info(string message) => Write("INFO", message);

    public static void Error(string message, Exception? ex = null) =>
        Write("ERROR", ex is null ? message : $"{message}: {ex}");

    private static void Write(string level, string message)
    {
        try
        {
            lock (Gate)
            {
                var path = AppPaths.Log;
                if (File.Exists(path) && new FileInfo(path).Length > MaxBytes)
                    File.Move(path, path + ".old", overwrite: true);
                File.AppendAllText(path, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} {level} {message}{Environment.NewLine}");
            }
        }
        catch (IOException)
        {
            // logging must never crash the app
        }
    }
}
