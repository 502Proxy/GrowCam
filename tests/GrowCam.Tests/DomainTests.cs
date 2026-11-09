using GrowCam.Domain;

namespace GrowCam.Tests;

public class DomainTests
{
    private static Grow MakeGrow(SeedInfo? seed = null, params Milestone[] milestones) => new()
    {
        Id = 1,
        Name = "Test",
        SowDate = new DateOnly(2026, 10, 1),
        PlantCount = 2,
        Seed = seed,
        Milestones = milestones,
    };

    [Fact]
    public void NewGrowIsGerminating()
    {
        var grow = MakeGrow();
        Assert.Equal(Phase.Germination, grow.CurrentPhase());
        Assert.Equal(10, grow.DayCount(new DateOnly(2026, 10, 11)));
    }

    [Fact]
    public void PhaseFollowsLatestConfirmedMilestoneOnly()
    {
        var grow = MakeGrow(null,
            new Milestone(MilestoneType.Sprouted, MilestoneStatus.Confirmed, new DateOnly(2026, 10, 5)),
            new Milestone(MilestoneType.Vegetative, MilestoneStatus.Suggested, new DateOnly(2026, 10, 15)));

        Assert.Equal(Phase.Seedling, grow.CurrentPhase());
        Assert.Equal(MilestoneType.Vegetative, grow.PendingSuggestion()?.Type);
        Assert.DoesNotContain(MilestoneType.Sprouted, grow.OpenMilestones());
    }

    [Fact]
    public void AutoflowerHarvestUsesSeedData()
    {
        var grow = MakeGrow(new SeedInfo("Auto", SeedType.Autoflower, WeeksToHarvest: 10));
        Assert.Equal(new DateOnly(2026, 12, 10), grow.ExpectedDate(MilestoneType.Harvest));
    }

    [Fact]
    public void PhotoperiodHarvestUnknownUntilFloweringConfirmed()
    {
        var seed = new SeedInfo("Photo", SeedType.Photoperiod, FloweringWeeks: 8);
        Assert.Null(MakeGrow(seed).ExpectedDate(MilestoneType.Harvest));

        var flowering = MakeGrow(seed, new Milestone(MilestoneType.Flowering, MilestoneStatus.Confirmed, new DateOnly(2026, 11, 20)));
        Assert.Equal(new DateOnly(2027, 1, 15), flowering.ExpectedDate(MilestoneType.Harvest));
    }
}
