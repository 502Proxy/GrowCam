using System.Text.Json;
using GrowCam.Application;

namespace GrowCam.Infrastructure;

/// <summary>Stores JPEGs as &lt;root&gt;/&lt;grow&gt;/&lt;yyyy-MM-dd&gt;/&lt;HHmmss_fff&gt;.jpg.</summary>
public sealed class FileImageStore : IImageStore
{
    private readonly string _root;

    public FileImageStore(string root)
    {
        _root = Path.GetFullPath(root);
        Directory.CreateDirectory(_root);
    }

    public string Save(long growId, DateTime takenAt, byte[] data)
    {
        var relative = Path.Combine(growId.ToString(), takenAt.ToString("yyyy-MM-dd"), takenAt.ToString("HHmmss_fff") + ".jpg");
        var target = Path.Combine(_root, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        File.WriteAllBytes(target, data);
        return relative.Replace('\\', '/');
    }

    public byte[] Load(string path)
    {
        var target = Path.GetFullPath(Path.Combine(_root, path));
        if (!target.StartsWith(_root, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Invalid image path."); // never read outside the image root
        return File.ReadAllBytes(target);
    }
}

public sealed class SystemClock : IClock
{
    public DateTime Now
    {
        get
        {
            var now = DateTime.Now;
            return new DateTime(now.Year, now.Month, now.Day, now.Hour, now.Minute, now.Second, now.Millisecond, DateTimeKind.Local);
        }
    }
}

/// <summary>Settings as a small JSON file next to the database.</summary>
public sealed class JsonSettingsStore : ISettingsStore
{
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };
    private readonly string _path;
    private readonly Lock _lock = new();

    public JsonSettingsStore(string path)
    {
        _path = path;
        Current = Load();
    }

    public AppSettings Current { get; private set; }

    public event EventHandler<AppSettings>? Changed;

    public void Save(AppSettings settings)
    {
        lock (_lock)
        {
            File.WriteAllText(_path, JsonSerializer.Serialize(settings, Options));
            Current = settings;
        }
        Changed?.Invoke(this, settings);
    }

    private AppSettings Load()
    {
        try
        {
            return File.Exists(_path)
                ? JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(_path), Options) ?? new AppSettings()
                : new AppSettings();
        }
        catch (JsonException)
        {
            return new AppSettings(); // corrupt file: start with defaults instead of crashing
        }
    }
}
