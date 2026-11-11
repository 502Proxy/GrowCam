using GrowCam.Domain;

namespace GrowCam.Application;

/// <summary>Starting, archiving and documenting grows.</summary>
public sealed class GrowService(IGrowRepository grows, IDiaryRepository diary, IReportRepository reports, IClock clock)
{
    public Grow? Active() => grows.Active();

    public Grow RequireActive() => grows.Active() ?? throw new NoActiveGrowException();

    public Grow? Get(long id) => grows.Get(id);

    public IReadOnlyList<Grow> Archived() => grows.Archived();

    /// <summary>Archives the running grow (if any) and starts a new one.</summary>
    public Grow Start(string name, DateOnly sowDate, int plantCount, SeedInfo? seed)
    {
        if (grows.Active() is { } current)
            grows.Archive(current.Id, Today, SummaryFor(current));

        return grows.Add(new Grow
        {
            Name = string.IsNullOrWhiteSpace(name) ? "Neuer Grow" : name.Trim(),
            SowDate = sowDate,
            PlantCount = Math.Max(1, plantCount),
            Seed = seed,
        });
    }

    /// <summary>Archives the running grow after harvest.</summary>
    public void Finish()
    {
        var grow = RequireActive();
        grows.SetMilestone(grow.Id, new Milestone(MilestoneType.Harvest, MilestoneStatus.Confirmed, Today));
        grows.Archive(grow.Id, Today, SummaryFor(grow));
    }

    /// <summary>Confirms a milestone; earlier, unconfirmed milestones are implied.</summary>
    public void ConfirmMilestone(MilestoneType type)
    {
        var grow = RequireActive();
        var reachedOn = grow.MilestoneOf(type) is { Status: MilestoneStatus.Suggested } suggestion
            ? suggestion.ReachedOn
            : Today;

        foreach (var earlier in Enum.GetValues<MilestoneType>())
        {
            if (earlier == MilestoneType.Sown || earlier > type || grow.IsReached(earlier))
                continue;
            var existing = grow.MilestoneOf(earlier);
            grows.SetMilestone(grow.Id, new Milestone(
                earlier, MilestoneStatus.Confirmed, reachedOn, existing?.Note ?? "", existing?.ByAi ?? false));
        }
    }

    public void RejectMilestone(MilestoneType type)
    {
        var grow = RequireActive();
        grows.SetMilestone(grow.Id, new Milestone(type, MilestoneStatus.Rejected, Today));
    }

    public DiaryEntry AddDiaryEntry(DiaryKind kind, string text = "")
    {
        var grow = RequireActive();
        return diary.Add(new DiaryEntry(0, grow.Id, clock.Now, kind, text.Trim()));
    }

    public void DeleteDiaryEntry(long id) => diary.Delete(id);

    public IReadOnlyList<DiaryEntry> Diary(long growId, int limit = 30) => diary.Recent(growId, limit);

    private DateOnly Today => DateOnly.FromDateTime(clock.Now);

    private string SummaryFor(Grow grow) => reports.ForGrow(grow.Id).FirstOrDefault()?.Headline ?? "";
}

/// <summary>Manual camera control and the remembered "analysis view".</summary>
public sealed class CameraService(ICamera camera, IKeyValueStore store)
{
    private const string AnalysisPresetKey = "camera.analysis_preset";
    private const string AnalysisPresetName = "GrowCam Analyse";

    public Task<byte[]> LiveFrameAsync(CancellationToken ct = default) => camera.CaptureAsync(ct);

    public Task MoveAsync(Direction direction) => camera.MoveAsync(direction);

    public Task<IReadOnlyList<CameraPreset>> PresetsAsync() => camera.GetPresetsAsync();

    public Task GotoPresetAsync(string presetId) => camera.GotoPresetAsync(presetId);

    public string? AnalysisPresetId => store.Get(AnalysisPresetKey);

    /// <summary>Stores the current view as a camera preset used for the daily photo.</summary>
    public async Task<CameraPreset> RememberAnalysisViewAsync()
    {
        var preset = await camera.SavePresetAsync(AnalysisPresetName);
        store.Set(AnalysisPresetKey, preset.Id);
        return preset;
    }

    /// <summary>Moves to the remembered view. Returns false if none is set.</summary>
    public async Task<bool> GoToAnalysisViewAsync(CancellationToken ct)
    {
        if (AnalysisPresetId is not { Length: > 0 } id)
            return false;
        await camera.GotoPresetAsync(id, ct);
        return true;
    }
}

/// <summary>Taking photos and letting the AI look at them.</summary>
public sealed class MonitoringService(
    IGrowRepository grows,
    ISnapshotRepository snapshots,
    IAnalysisRepository analyses,
    IDiaryRepository diary,
    IImageStore images,
    ICamera camera,
    CameraService cameraService,
    IPlantAnalyzer analyzer,
    IClock clock,
    TimeSpan? settleTime = null)
{
    /// <summary>A rejected AI suggestion may only come back after this many days.</summary>
    public const int RejectionCooldownDays = 2;

    private readonly SemaphoreSlim _gate = new(1, 1); // one camera/AI job at a time
    private readonly TimeSpan _settleTime = settleTime ?? TimeSpan.FromSeconds(5);

    public async Task<Snapshot> TakeSnapshotAsync(bool scheduled = false, CancellationToken ct = default)
    {
        var grow = grows.Active() ?? throw new NoActiveGrowException();
        await _gate.WaitAsync(ct);
        try
        {
            return await CaptureAsync(grow, scheduled, ct);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Moves to the analysis view, takes a photo and lets the AI assess it.</summary>
    public async Task<Analysis> AnalyzeNowAsync(string userNote = "", CancellationToken ct = default)
    {
        var grow = grows.Active() ?? throw new NoActiveGrowException();
        await _gate.WaitAsync(ct);
        try
        {
            if (await cameraService.GoToAnalysisViewAsync(ct) && _settleTime > TimeSpan.Zero)
                await Task.Delay(_settleTime, ct); // let the motor stop before capturing

            var snapshot = await CaptureAsync(grow, scheduled: true, ct);
            var previous = analyses.Latest(grow.Id);

            // Compare only with an earlier camera photo – an uploaded close-up shows something else.
            byte[]? previousImage = null;
            if (previous is not null && snapshots.Get(previous.SnapshotId) is { Scheduled: true } previousSnapshot)
                previousImage = images.Load(previousSnapshot.Path);

            var context = ContextBuilder.Build(grow, clock.Now, diary.Recent(grow.Id, 10), previous?.Headline ?? "", userNote);
            return await AnalyzeAndStoreAsync(grow, snapshot, previousImage, context, ct);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Lets the AI assess a photo the grower took themselves (e.g. a close-up of one leaf).
    /// It is stored with the grow but never used for the weekly report or as comparison photo.
    /// </summary>
    public async Task<Analysis> AnalyzeUploadedPhotoAsync(byte[] jpeg, string userNote = "", CancellationToken ct = default)
    {
        var grow = grows.Active() ?? throw new NoActiveGrowException();
        if (jpeg.Length == 0)
            throw new UserFacingException("Das Foto ist leer.");
        await _gate.WaitAsync(ct);
        try
        {
            var takenAt = clock.Now;
            var snapshot = snapshots.Add(new Snapshot(0, grow.Id, takenAt, images.Save(grow.Id, takenAt, jpeg), Scheduled: false));
            var previous = analyses.Latest(grow.Id);
            var context = ContextBuilder.Build(grow, takenAt, diary.Recent(grow.Id, 10), previous?.Headline ?? "", userNote)
                with { IsUploadedPhoto = true };
            return await AnalyzeAndStoreAsync(grow, snapshot, null, context, ct);
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<Analysis> AnalyzeAndStoreAsync(Grow grow, Snapshot snapshot, byte[]? previousImage, GrowContext context, CancellationToken ct)
    {
        var result = await analyzer.AnalyzeAsync(images.Load(snapshot.Path), previousImage, context, ct);
        var analysis = analyses.Add(new Analysis(
            0,
            grow.Id,
            snapshot.Id,
            clock.Now,
            Math.Clamp(result.HealthScore, 0, 100),
            result.Headline,
            result.Findings.Take(3).ToList(),
            result.SuggestedMilestone,
            result.MilestoneNote));

        ApplyMilestoneSuggestion(grow, analysis);
        return analysis;
    }

    public Analysis? LatestAnalysis(long growId) => analyses.Latest(growId);

    public Snapshot? LatestSnapshot(long growId) => snapshots.Latest(growId);

    public byte[]? SnapshotImage(long snapshotId) =>
        snapshots.Get(snapshotId) is { } snapshot ? images.Load(snapshot.Path) : null;

    private async Task<Snapshot> CaptureAsync(Grow grow, bool scheduled, CancellationToken ct)
    {
        var takenAt = clock.Now;
        var data = await camera.CaptureAsync(ct);
        var path = images.Save(grow.Id, takenAt, data);
        return snapshots.Add(new Snapshot(0, grow.Id, takenAt, path, scheduled));
    }

    private void ApplyMilestoneSuggestion(Grow grow, Analysis analysis)
    {
        if (analysis.SuggestedMilestone is not { } type || !grow.OpenMilestones().Contains(type))
            return;

        var today = DateOnly.FromDateTime(analysis.CreatedAt);
        var existing = grow.MilestoneOf(type);
        if (existing?.Status == MilestoneStatus.Suggested)
            return;
        if (existing?.Status == MilestoneStatus.Rejected
            && today.DayNumber - existing.ReachedOn.DayNumber < RejectionCooldownDays)
            return;

        grows.SetMilestone(grow.Id, new Milestone(type, MilestoneStatus.Suggested, today, analysis.MilestoneNote, ByAi: true));
    }
}

/// <summary>Weekly reports: one per calendar week, generated on Sunday evening.</summary>
public sealed class ReportService(
    IGrowRepository grows,
    ISnapshotRepository snapshots,
    IAnalysisRepository analyses,
    IDiaryRepository diary,
    IReportRepository reports,
    IImageStore images,
    IPlantAnalyzer analyzer,
    IClock clock)
{
    public IReadOnlyList<WeeklyReport> Reports(long growId) => reports.ForGrow(growId);

    public WeeklyReport? Get(long id) => reports.Get(id);

    public async Task<WeeklyReport?> GenerateForWeekAsync(DateOnly weekStart, CancellationToken ct = default)
    {
        var grow = grows.Active();
        if (grow is null || reports.Exists(grow.Id, weekStart))
            return null;

        var start = weekStart.ToDateTime(TimeOnly.MinValue);
        var end = start.AddDays(7);
        var dailyPhotos = ReportHelpers.OnePerDay(snapshots.ScheduledBetween(grow.Id, start, end));
        var weekAnalyses = analyses.Between(grow.Id, start, end);
        var entries = diary.Between(grow.Id, start, end);
        var context = ContextBuilder.Build(grow, clock.Now, entries, "", "");

        var draft = await analyzer.WeeklyReportAsync(
            dailyPhotos.Select(s => images.Load(s.Path)).ToList(),
            context,
            weekAnalyses.Select(a => $"{a.CreatedAt:ddd dd.MM.}: {a.Headline}").ToList(),
            ct);

        return reports.Add(new WeeklyReport(
            0,
            grow.Id,
            weekStart,
            draft.Headline,
            draft.Happened,
            ReportHelpers.SummarizeDiary(entries),
            draft.NextSteps,
            draft.NeedsAttention,
            dailyPhotos.Select(s => s.Id).ToList()));
    }

    /// <summary>Report for the week ending today (Sunday) or last Sunday.</summary>
    public Task<WeeklyReport?> GenerateLastWeekIfMissingAsync(CancellationToken ct = default)
    {
        var today = DateOnly.FromDateTime(clock.Now);
        var daysSinceMonday = ((int)today.DayOfWeek + 6) % 7;
        var weekStart = today.AddDays(-daysSinceMonday);
        if (today.DayOfWeek != DayOfWeek.Sunday)
            weekStart = weekStart.AddDays(-7); // the current week isn't over yet
        return GenerateForWeekAsync(weekStart, ct);
    }
}

/// <summary>Strain lookup with a local cache, so each strain is only searched once.</summary>
public sealed class SeedService(ISeedCatalog catalog, ISeedCache cache)
{
    public async Task<IReadOnlyList<SeedInfo>> SearchAsync(string query, CancellationToken ct = default)
    {
        var key = string.Join(' ', query.ToLowerInvariant().Split(' ', StringSplitOptions.RemoveEmptyEntries));
        if (key.Length == 0)
            return [];
        if (cache.Get(key) is { } cached)
            return cached;

        var results = await catalog.SearchAsync(query.Trim(), ct);
        if (results.Count > 0)
            cache.Put(key, results);
        return results;
    }
}

/// <summary>Decides which background job is due. Pure logic, so it is easy to test.</summary>
public sealed class ScheduleService(
    ISettingsStore settings,
    IKeyValueStore store,
    GrowService growService,
    MonitoringService monitoring,
    ReportService reportService)
{
    /// <summary>Raised when a job fails; the scheduler itself keeps running.</summary>
    public event Action<string, Exception>? JobFailed;

    /// <summary>Raised after a job changed data, so the UI can refresh.</summary>
    public event Action? DataChanged;

    public async Task TickAsync(DateTime now, CancellationToken ct = default)
    {
        if (growService.Active() is null)
            return;

        var config = settings.Current;
        var today = DateOnly.FromDateTime(now).ToString("yyyy-MM-dd");
        var time = TimeOnly.FromDateTime(now);

        if (time >= config.AnalysisTime && store.Get("scheduler.last_analysis") != today)
        {
            store.Set("scheduler.last_analysis", today); // mark first: never retry-loop against the API
            await RunAsync("Tägliche Prüfung", () => monitoring.AnalyzeNowAsync(ct: ct));
        }

        if (config.SnapshotIntervalMinutes > 0 && config.IsLightOn(time) && SnapshotDue(now, config.SnapshotIntervalMinutes))
        {
            store.Set("scheduler.last_snapshot", now.ToString("O"));
            await RunAsync("Foto", () => monitoring.TakeSnapshotAsync(scheduled: true, ct));
        }

        if (now.DayOfWeek == DayOfWeek.Sunday && time >= config.ReportTime && store.Get("scheduler.last_report") != today)
        {
            store.Set("scheduler.last_report", today);
            await RunAsync("Wochenbericht", () => reportService.GenerateLastWeekIfMissingAsync(ct));
        }
    }

    private async Task RunAsync(string name, Func<Task> job)
    {
        try
        {
            await job();
            DataChanged?.Invoke();
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            JobFailed?.Invoke(name, ex);
        }
    }

    private bool SnapshotDue(DateTime now, int intervalMinutes) =>
        store.Get("scheduler.last_snapshot") is not { } last
        || !DateTime.TryParse(last, null, System.Globalization.DateTimeStyles.RoundtripKind, out var lastTime)
        || now - lastTime >= TimeSpan.FromMinutes(intervalMinutes);
}

// ---------------------------------------------------------------------------
// Small pure helpers
// ---------------------------------------------------------------------------

public static class ContextBuilder
{
    public static GrowContext Build(Grow grow, DateTime now, IEnumerable<DiaryEntry> diary, string previousHeadline, string userNote) =>
        new(
            grow.Name,
            grow.DayCount(DateOnly.FromDateTime(now)),
            grow.CurrentPhase(),
            grow.Seed,
            grow.OpenMilestones(),
            diary.Select(e => $"{e.CreatedAt:dd.MM.} {Labels.Of(e.Kind)}" + (e.Text.Length > 0 ? $": {e.Text}" : "")).ToList(),
            previousHeadline,
            userNote.Trim());
}

public static class ReportHelpers
{
    /// <summary>Picks the snapshot closest to the preferred hour for each day.</summary>
    public static IReadOnlyList<Snapshot> OnePerDay(IEnumerable<Snapshot> snapshots, int preferredHour = 12) =>
        snapshots
            .GroupBy(s => s.TakenAt.Date)
            .OrderBy(g => g.Key)
            .Select(g => g.MinBy(s => Math.Abs(s.TakenAt.Hour - preferredHour))!)
            .ToList();

    /// <summary>"2× gegossen", "1× gedüngt" … – notes are listed as written.</summary>
    public static IReadOnlyList<string> SummarizeDiary(IEnumerable<DiaryEntry> entries)
    {
        var list = entries.ToList();
        var counts = list
            .Where(e => e.Kind != DiaryKind.Note)
            .GroupBy(e => e.Kind)
            .OrderByDescending(g => g.Count())
            .Select(g => $"{g.Count()}× {Labels.Of(g.Key).ToLowerInvariant()}");
        var notes = list.Where(e => e.Kind == DiaryKind.Note && e.Text.Length > 0).Select(e => e.Text);
        return counts.Concat(notes).ToList();
    }
}
