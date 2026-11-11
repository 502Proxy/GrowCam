using GrowCam.Domain;

namespace GrowCam.Application;

// ---------------------------------------------------------------------------
// AI
// ---------------------------------------------------------------------------

/// <summary>Everything the AI needs to know about the grow besides the images.</summary>
public sealed record GrowContext(
    string GrowName,
    int DayCount,
    Phase Phase,
    SeedInfo? Seed,
    IReadOnlyList<MilestoneType> OpenMilestones,
    IReadOnlyList<string> RecentDiary,
    string PreviousHeadline = "",
    string UserNote = "",
    bool IsUploadedPhoto = false);

public sealed record AnalysisResult(
    int HealthScore,
    string Headline,
    IReadOnlyList<Finding> Findings,
    MilestoneType? SuggestedMilestone = null,
    string MilestoneNote = "");

public sealed record ReportDraft(
    string Headline,
    IReadOnlyList<string> Happened,
    IReadOnlyList<string> NextSteps,
    bool NeedsAttention);

/// <summary>Vision model that looks at plant photos.</summary>
public interface IPlantAnalyzer
{
    Task<AnalysisResult> AnalyzeAsync(byte[] image, byte[]? previousImage, GrowContext context, CancellationToken ct = default);

    Task<ReportDraft> WeeklyReportAsync(IReadOnlyList<byte[]> images, GrowContext context, IReadOnlyList<string> analyses, CancellationToken ct = default);
}

/// <summary>Looks up strain data, e.g. via web search.</summary>
public interface ISeedCatalog
{
    Task<IReadOnlyList<SeedInfo>> SearchAsync(string query, CancellationToken ct = default);
}

// ---------------------------------------------------------------------------
// Camera
// ---------------------------------------------------------------------------

public enum Direction
{
    Up,
    Down,
    Left,
    Right,
}

public sealed record CameraPreset(string Id, string Name);

public interface ICamera
{
    /// <summary>Returns a single JPEG frame.</summary>
    Task<byte[]> CaptureAsync(CancellationToken ct = default);

    Task MoveAsync(Direction direction, CancellationToken ct = default);

    Task<IReadOnlyList<CameraPreset>> GetPresetsAsync(CancellationToken ct = default);

    Task GotoPresetAsync(string presetId, CancellationToken ct = default);

    Task<CameraPreset> SavePresetAsync(string name, CancellationToken ct = default);
}

// ---------------------------------------------------------------------------
// Persistence and system
// ---------------------------------------------------------------------------

public interface IGrowRepository
{
    Grow? Active();
    Grow? Get(long id);
    IReadOnlyList<Grow> Archived();
    Grow Add(Grow grow);
    void Archive(long growId, DateOnly on, string summary);
    void SetMilestone(long growId, Milestone milestone);
    void RemoveMilestone(long growId, MilestoneType type);
}

public interface ISnapshotRepository
{
    Snapshot Add(Snapshot snapshot);
    Snapshot? Get(long id);
    Snapshot? Latest(long growId);
    IReadOnlyList<Snapshot> ScheduledBetween(long growId, DateTime start, DateTime end);
}

public interface IAnalysisRepository
{
    Analysis Add(Analysis analysis);
    Analysis? Latest(long growId);
    IReadOnlyList<Analysis> Between(long growId, DateTime start, DateTime end);
}

public interface IDiaryRepository
{
    DiaryEntry Add(DiaryEntry entry);
    IReadOnlyList<DiaryEntry> Recent(long growId, int limit);
    IReadOnlyList<DiaryEntry> Between(long growId, DateTime start, DateTime end);
    void Delete(long id);
}

public interface IReportRepository
{
    WeeklyReport Add(WeeklyReport report);
    IReadOnlyList<WeeklyReport> ForGrow(long growId);
    WeeklyReport? Get(long id);
    bool Exists(long growId, DateOnly weekStart);
}

public interface ISeedCache
{
    IReadOnlyList<SeedInfo>? Get(string key);
    void Put(string key, IReadOnlyList<SeedInfo> seeds);
}

/// <summary>Small persistent key/value store (scheduler markers, remembered camera view).</summary>
public interface IKeyValueStore
{
    string? Get(string key);
    void Set(string key, string value);
}

public interface IImageStore
{
    /// <summary>Persists a JPEG and returns its relative path.</summary>
    string Save(long growId, DateTime takenAt, byte[] data);

    byte[] Load(string path);
}

public interface IClock
{
    DateTime Now { get; }
}

// ---------------------------------------------------------------------------
// Errors the UI can explain to the user
// ---------------------------------------------------------------------------

/// <summary>Base class for errors whose message is meant for the user (German).</summary>
public class UserFacingException(string message, Exception? inner = null) : Exception(message, inner);

public sealed class CameraException(string message, Exception? inner = null) : UserFacingException(message, inner);

public sealed class AnalyzerException(string message, Exception? inner = null) : UserFacingException(message, inner);

public sealed class NoActiveGrowException() : UserFacingException("Es läuft gerade kein Grow.");
