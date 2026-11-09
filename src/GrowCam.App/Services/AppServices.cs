using GrowCam.Application;
using GrowCam.Infrastructure;
using GrowCam.Infrastructure.Ai;
using GrowCam.Infrastructure.Camera;
using GrowCam.Infrastructure.Persistence;

namespace GrowCam.App.Services;

/// <summary>Everything the view models need, in one place.</summary>
public sealed record AppServices(
    GrowService Grows,
    CameraService Camera,
    MonitoringService Monitoring,
    ReportService Reports,
    SeedService Seeds,
    ScheduleService Schedule,
    ISettingsStore Settings,
    IClock Clock,
    Updater Updater);

/// <summary>Composition root: the only place that chooses concrete adapters.</summary>
public static class CompositionRoot
{
    public static AppServices Build()
    {
        var http = new HttpClient { Timeout = TimeSpan.FromMinutes(2) };
        var settings = new JsonSettingsStore(AppPaths.Settings);
        var db = new SqliteDatabase(AppPaths.Database);

        var growRepo = new SqliteGrowRepository(db);
        var snapshotRepo = new SqliteSnapshotRepository(db);
        var analysisRepo = new SqliteAnalysisRepository(db);
        var diaryRepo = new SqliteDiaryRepository(db);
        var reportRepo = new SqliteReportRepository(db);
        var keyValue = new SqliteKeyValueStore(db);
        var images = new FileImageStore(AppPaths.Images);
        var clock = new SystemClock();

        var camera = new ConfiguredCamera(settings, http);
        var ai = new ConfiguredAi(settings, http);

        var grows = new GrowService(growRepo, diaryRepo, reportRepo, clock);
        var cameraService = new CameraService(camera, keyValue);
        var monitoring = new MonitoringService(growRepo, snapshotRepo, analysisRepo, diaryRepo, images, camera, cameraService, ai, clock);
        var reports = new ReportService(growRepo, snapshotRepo, analysisRepo, diaryRepo, reportRepo, images, ai, clock);
        var seeds = new SeedService(ai, new SqliteSeedCache(db));
        var schedule = new ScheduleService(settings, keyValue, grows, monitoring, reports);

        return new AppServices(grows, cameraService, monitoring, reports, seeds, schedule, settings, clock, new Updater(settings));
    }
}
