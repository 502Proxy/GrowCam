using GrowCam.Application;
using GrowCam.Domain;
using GrowCam.Infrastructure;
using GrowCam.Infrastructure.Camera;
using GrowCam.Infrastructure.Persistence;

namespace GrowCam.Tests;

public sealed class FakeClock(DateTime now) : IClock
{
    public DateTime Now { get; set; } = now;
}

/// <summary>Returns whatever the test configures and records what it received.</summary>
public sealed class FakeAnalyzer : IPlantAnalyzer
{
    public MilestoneType? Suggest { get; set; }
    public List<GrowContext> Contexts { get; } = [];
    public int ReportImageCount { get; private set; }

    public Task<AnalysisResult> AnalyzeAsync(byte[] image, byte[]? previousImage, GrowContext context, CancellationToken ct = default)
    {
        Contexts.Add(context);
        // Out-of-range score and too many findings on purpose: the service must clamp/trim.
        var findings = Enumerable.Range(0, 5).Select(i => new Finding(Severity.Ok, $"Befund {i}", "nichts")).ToList();
        return Task.FromResult(new AnalysisResult(150, "Alles gut", findings, Suggest, "gesehen"));
    }

    public Task<ReportDraft> WeeklyReportAsync(IReadOnlyList<byte[]> images, GrowContext context, IReadOnlyList<string> analyses, CancellationToken ct = default)
    {
        ReportImageCount = images.Count;
        return Task.FromResult(new ReportDraft("Gute Woche", ["wuchs"], ["weiter so"], false));
    }
}

/// <summary>Fully wired services on a temporary SQLite database with fakes.</summary>
public sealed class TestStack : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "growcam-tests-" + Guid.NewGuid().ToString("N"));

    public TestStack()
    {
        var db = new SqliteDatabase(Path.Combine(_dir, "test.db"));
        var growRepo = new SqliteGrowRepository(db);
        var snapshots = new SqliteSnapshotRepository(db);
        var analyses = new SqliteAnalysisRepository(db);
        var diary = new SqliteDiaryRepository(db);
        var reports = new SqliteReportRepository(db);
        var keyValue = new SqliteKeyValueStore(db);
        var images = new FileImageStore(Path.Combine(_dir, "images"));
        var camera = new DemoCamera();

        Camera = new CameraService(camera, keyValue);
        Grows = new GrowService(growRepo, diary, reports, Clock);
        Monitoring = new MonitoringService(growRepo, snapshots, analyses, diary, images, camera, Camera, Analyzer, Clock, TimeSpan.Zero);
        Reports = new ReportService(growRepo, snapshots, analyses, diary, reports, images, Analyzer, Clock);
    }

    public FakeClock Clock { get; } = new(new DateTime(2026, 11, 9, 12, 0, 0));
    public FakeAnalyzer Analyzer { get; } = new();
    public GrowService Grows { get; }
    public MonitoringService Monitoring { get; }
    public ReportService Reports { get; }
    public CameraService Camera { get; }

    public Grow StartGrow(DateOnly? sowDate = null) =>
        Grows.Start("Herbst", sowDate ?? new DateOnly(2026, 10, 20), 3, new SeedInfo("Northern Lights"));

    public void Dispose()
    {
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch (IOException)
        {
            // SQLite may still hold the file briefly on Windows; temp folder gets cleaned anyway.
        }
    }
}
