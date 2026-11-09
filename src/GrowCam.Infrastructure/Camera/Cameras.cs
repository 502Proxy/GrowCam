using GrowCam.Application;

namespace GrowCam.Infrastructure.Camera;

/// <summary>Tapo C200/C210: photos via RTSP (ffmpeg), pan/tilt and presets via ONVIF.</summary>
public sealed class TapoCamera(FfmpegSnapshotter snapshotter, OnvifPtzClient ptz) : ICamera
{
    public Task<byte[]> CaptureAsync(CancellationToken ct = default) => snapshotter.CaptureAsync(ct);

    public Task MoveAsync(Direction direction, CancellationToken ct = default) => ptz.MoveAsync(direction, ct);

    public Task<IReadOnlyList<CameraPreset>> GetPresetsAsync(CancellationToken ct = default) => ptz.GetPresetsAsync(ct);

    public Task GotoPresetAsync(string presetId, CancellationToken ct = default) => ptz.GotoPresetAsync(presetId, ct);

    public Task<CameraPreset> SavePresetAsync(string name, CancellationToken ct = default) => ptz.SavePresetAsync(name, ct);
}

/// <summary>Offline stand-in so the app can be tried without hardware.</summary>
public sealed class DemoCamera : ICamera
{
    private static readonly Lazy<byte[]> Image = new(() =>
    {
        using var stream = typeof(DemoCamera).Assembly.GetManifestResourceStream("GrowCam.demo.jpg")!;
        using var buffer = new MemoryStream();
        stream.CopyTo(buffer);
        return buffer.ToArray();
    });

    private readonly List<CameraPreset> _presets = [new("1", "Alle Pflanzen"), new("2", "Pflanze links")];

    public Task<byte[]> CaptureAsync(CancellationToken ct = default) => Task.FromResult(Image.Value);

    public Task MoveAsync(Direction direction, CancellationToken ct = default) => Task.CompletedTask;

    public Task<IReadOnlyList<CameraPreset>> GetPresetsAsync(CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<CameraPreset>>(_presets.ToList());

    public Task GotoPresetAsync(string presetId, CancellationToken ct = default) => Task.CompletedTask;

    public Task<CameraPreset> SavePresetAsync(string name, CancellationToken ct = default)
    {
        _presets.RemoveAll(p => p.Name == name);
        var preset = new CameraPreset((_presets.Select(p => int.Parse(p.Id)).DefaultIfEmpty(0).Max() + 1).ToString(), name);
        _presets.Add(preset);
        return Task.FromResult(preset);
    }
}

/// <summary>
/// Proxy that always delegates to the camera matching the current settings,
/// so changing the IP or password in the app takes effect without a restart.
/// </summary>
public sealed class ConfiguredCamera : ICamera
{
    private readonly ISettingsStore _settings;
    private readonly HttpClient _http;
    private ICamera _current;

    public ConfiguredCamera(ISettingsStore settings, HttpClient http)
    {
        _settings = settings;
        _http = http;
        _current = Build(settings.Current);
        settings.Changed += (_, updated) => _current = Build(updated);
    }

    public Task<byte[]> CaptureAsync(CancellationToken ct = default) => _current.CaptureAsync(ct);

    public Task MoveAsync(Direction direction, CancellationToken ct = default) => _current.MoveAsync(direction, ct);

    public Task<IReadOnlyList<CameraPreset>> GetPresetsAsync(CancellationToken ct = default) => _current.GetPresetsAsync(ct);

    public Task GotoPresetAsync(string presetId, CancellationToken ct = default) => _current.GotoPresetAsync(presetId, ct);

    public Task<CameraPreset> SavePresetAsync(string name, CancellationToken ct = default) => _current.SavePresetAsync(name, ct);

    private ICamera Build(AppSettings s)
    {
        if (s.IsDemoCamera)
            return new DemoCamera();
        var user = Uri.EscapeDataString(s.CameraUser);
        var password = Uri.EscapeDataString(s.CameraPassword);
        var rtsp = $"rtsp://{user}:{password}@{s.CameraHost.Trim()}:554/stream1";
        return new TapoCamera(
            new FfmpegSnapshotter(rtsp, FfmpegSnapshotter.Locate(s.FfmpegPath)),
            new OnvifPtzClient(_http, s.CameraHost.Trim(), s.CameraUser, s.CameraPassword));
    }
}
