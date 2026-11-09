using System.Diagnostics;
using GrowCam.Application;

namespace GrowCam.Infrastructure.Camera;

/// <summary>Grabs one JPEG frame from an RTSP stream using ffmpeg.</summary>
public sealed class FfmpegSnapshotter(string rtspUrl, string ffmpegPath)
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(20);

    public async Task<byte[]> CaptureAsync(CancellationToken ct)
    {
        var start = new ProcessStartInfo(ffmpegPath)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true, // no console flashing on Windows
        };
        foreach (var arg in new[]
                 {
                     "-hide_banner", "-loglevel", "error", "-rtsp_transport", "tcp", "-i", rtspUrl,
                     "-frames:v", "1", "-q:v", "2", "-f", "image2", "-",
                 })
            start.ArgumentList.Add(arg);

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(Timeout);

        Process process;
        try
        {
            process = Process.Start(start) ?? throw new CameraException("ffmpeg konnte nicht gestartet werden.");
        }
        catch (System.ComponentModel.Win32Exception ex)
        {
            throw new CameraException("ffmpeg wurde nicht gefunden. Liegt ffmpeg neben GrowCam oder ist es installiert?", ex);
        }

        using (process)
        {
            using var image = new MemoryStream();
            try
            {
                var stdout = process.StandardOutput.BaseStream.CopyToAsync(image, timeout.Token);
                var stderr = process.StandardError.ReadToEndAsync(timeout.Token);
                await Task.WhenAll(stdout, stderr, process.WaitForExitAsync(timeout.Token));

                if (process.ExitCode != 0 || image.Length == 0)
                    throw new CameraException("Kein Bild von der Kamera. " + Shorten(await stderr));
                return image.ToArray();
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                TryKill(process);
                throw new CameraException("Die Kamera antwortet nicht. Ist sie eingeschaltet und im WLAN?");
            }
        }
    }

    /// <summary>Finds ffmpeg: configured path, next to the app, common install folders, then PATH.</summary>
    public static string Locate(string configured)
    {
        if (!string.IsNullOrWhiteSpace(configured))
            return configured;
        var exe = OperatingSystem.IsWindows() ? "ffmpeg.exe" : "ffmpeg";
        string[] candidates =
        [
            Path.Combine(AppContext.BaseDirectory, exe),
            "/opt/homebrew/bin/ffmpeg", // macOS apps don't see the shell PATH
            "/usr/local/bin/ffmpeg",
        ];
        return candidates.FirstOrDefault(File.Exists) ?? exe;
    }

    private static string Shorten(string text) => text.Length > 200 ? text[..200] : text.Trim();

    private static void TryKill(Process process)
    {
        try
        {
            if (!process.HasExited)
                process.Kill(entireProcessTree: true);
        }
        catch (InvalidOperationException)
        {
            // already gone
        }
    }
}
