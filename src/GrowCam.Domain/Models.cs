using System.Globalization;

namespace GrowCam.Domain;

/// <summary>Strain data, looked up on the web or entered by hand.</summary>
public sealed record SeedInfo(
    string Name,
    SeedType Type = SeedType.Unknown,
    int? WeeksToHarvest = null,
    int? FloweringWeeks = null,
    string Height = "",
    string LightSchedule = "",
    string Difficulty = "");

public sealed record Milestone(
    MilestoneType Type,
    MilestoneStatus Status,
    DateOnly ReachedOn,
    string Note = "",
    bool ByAi = false);

public sealed record Snapshot(long Id, long GrowId, DateTime TakenAt, string Path, bool Scheduled);

public sealed record Finding(Severity Severity, string Title, string Advice);

public sealed record Analysis(
    long Id,
    long GrowId,
    long SnapshotId,
    DateTime CreatedAt,
    int HealthScore,
    string Headline,
    IReadOnlyList<Finding> Findings,
    MilestoneType? SuggestedMilestone,
    string MilestoneNote)
{
    public bool NeedsAttention => Findings.Any(f => f.Severity == Severity.Watch);
}

public sealed record DiaryEntry(long Id, long GrowId, DateTime CreatedAt, DiaryKind Kind, string Text);

public sealed record WeeklyReport(
    long Id,
    long GrowId,
    DateOnly WeekStart,
    string Headline,
    IReadOnlyList<string> Happened,
    IReadOnlyList<string> Done,
    IReadOnlyList<string> NextSteps,
    bool NeedsAttention,
    IReadOnlyList<long> SnapshotIds)
{
    public DateOnly WeekEnd => WeekStart.AddDays(6);

    public int CalendarWeek => ISOWeek.GetWeekOfYear(WeekStart.ToDateTime(TimeOnly.MinValue));
}

/// <summary>A grow from sowing to harvest. Holds the rules about phases and milestones.</summary>
public sealed class Grow
{
    public long Id { get; init; }
    public required string Name { get; init; }
    public required DateOnly SowDate { get; init; }
    public required int PlantCount { get; init; }
    public SeedInfo? Seed { get; init; }
    public DateOnly? ArchivedOn { get; init; }
    public string Summary { get; init; } = "";
    public IReadOnlyList<Milestone> Milestones { get; init; } = [];

    public bool IsActive => ArchivedOn is null;

    public int DayCount(DateOnly today) => Math.Max(0, today.DayNumber - SowDate.DayNumber);

    public Milestone? MilestoneOf(MilestoneType type) => Milestones.FirstOrDefault(m => m.Type == type);

    public bool IsReached(MilestoneType type) =>
        type == MilestoneType.Sown || MilestoneOf(type)?.Status == MilestoneStatus.Confirmed;

    public Phase CurrentPhase()
    {
        var latest = Enum.GetValues<MilestoneType>().Last(IsReached);
        return latest switch
        {
            MilestoneType.Sprouted => Phase.Seedling,
            MilestoneType.Vegetative => Phase.Vegetative,
            MilestoneType.Flowering => Phase.Flowering,
            MilestoneType.Harvest => Phase.Harvested,
            _ => Phase.Germination,
        };
    }

    /// <summary>The AI suggestion the user still has to confirm, if any.</summary>
    public Milestone? PendingSuggestion() =>
        Milestones.FirstOrDefault(m => m.Status == MilestoneStatus.Suggested);

    /// <summary>Milestones the AI may still detect.</summary>
    public IReadOnlyList<MilestoneType> OpenMilestones() =>
        Enum.GetValues<MilestoneType>().Where(t => t != MilestoneType.Sown && !IsReached(t)).ToList();

    /// <summary>Rough expectation shown until a milestone is reached.</summary>
    public DateOnly? ExpectedDate(MilestoneType type)
    {
        switch (type)
        {
            case MilestoneType.Sprouted:
                return SowDate.AddDays(4);
            case MilestoneType.Vegetative:
                return SowDate.AddDays(15);
        }

        if (Seed?.Type == SeedType.Autoflower)
        {
            if (type == MilestoneType.Flowering)
                return SowDate.AddDays(28);
            if (type == MilestoneType.Harvest && Seed.WeeksToHarvest is { } weeks)
                return SowDate.AddDays(weeks * 7);
        }

        if (type == MilestoneType.Harvest && Seed?.FloweringWeeks is { } floweringWeeks)
        {
            var flowering = MilestoneOf(MilestoneType.Flowering);
            if (flowering?.Status == MilestoneStatus.Confirmed)
                return flowering.ReachedOn.AddDays(floweringWeeks * 7);
        }

        return null;
    }
}
