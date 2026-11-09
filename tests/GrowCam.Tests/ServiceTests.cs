using GrowCam.Application;
using GrowCam.Domain;

namespace GrowCam.Tests;

public sealed class ServiceTests : IDisposable
{
    private readonly TestStack _stack = new();

    public void Dispose() => _stack.Dispose();

    [Fact]
    public async Task AnalysisRequiresRunningGrow()
    {
        await Assert.ThrowsAsync<NoActiveGrowException>(() => _stack.Monitoring.AnalyzeNowAsync());
    }

    [Fact]
    public void StartingAGrowArchivesThePreviousOne()
    {
        var first = _stack.StartGrow();
        var second = _stack.Grows.Start("Winter", new DateOnly(2026, 11, 9), 1, null);

        Assert.Equal(second.Id, _stack.Grows.Active()?.Id);
        Assert.Equal(new[] { first.Id }, _stack.Grows.Archived().Select(g => g.Id));
    }

    [Fact]
    public async Task AnalysisIsStoredClampedAndTrimmed()
    {
        _stack.StartGrow();
        var analysis = await _stack.Monitoring.AnalyzeNowAsync("neuer Dünger");

        Assert.Equal(100, analysis.HealthScore);
        Assert.Equal(3, analysis.Findings.Count);
        Assert.Equal("neuer Dünger", _stack.Analyzer.Contexts[0].UserNote);
        Assert.Equal(20, _stack.Analyzer.Contexts[0].DayCount);
    }

    [Fact]
    public async Task AiSuggestionWaitsForUserConfirmation()
    {
        _stack.StartGrow();
        _stack.Analyzer.Suggest = MilestoneType.Sprouted;
        await _stack.Monitoring.AnalyzeNowAsync();

        var grow = _stack.Grows.Active()!;
        Assert.Equal(MilestoneType.Sprouted, grow.PendingSuggestion()?.Type);
        Assert.Equal(Phase.Germination, grow.CurrentPhase());

        _stack.Grows.ConfirmMilestone(MilestoneType.Sprouted);
        Assert.Equal(Phase.Seedling, _stack.Grows.Active()!.CurrentPhase());
    }

    [Fact]
    public async Task RejectedSuggestionHasCooldown()
    {
        _stack.StartGrow();
        _stack.Analyzer.Suggest = MilestoneType.Flowering;
        await _stack.Monitoring.AnalyzeNowAsync();
        _stack.Grows.RejectMilestone(MilestoneType.Flowering);

        _stack.Clock.Now = _stack.Clock.Now.AddDays(1);
        await _stack.Monitoring.AnalyzeNowAsync();
        Assert.Equal(MilestoneStatus.Rejected, _stack.Grows.Active()!.MilestoneOf(MilestoneType.Flowering)?.Status);

        _stack.Clock.Now = _stack.Clock.Now.AddDays(2);
        await _stack.Monitoring.AnalyzeNowAsync();
        Assert.Equal(MilestoneStatus.Suggested, _stack.Grows.Active()!.MilestoneOf(MilestoneType.Flowering)?.Status);
    }

    [Fact]
    public void ConfirmingLaterMilestoneImpliesEarlierOnes()
    {
        _stack.StartGrow();
        _stack.Grows.ConfirmMilestone(MilestoneType.Vegetative);

        var grow = _stack.Grows.Active()!;
        Assert.True(grow.IsReached(MilestoneType.Sprouted));
        Assert.Equal(Phase.Vegetative, grow.CurrentPhase());
    }

    [Fact]
    public async Task WeeklyReportUsesOnePhotoPerDayAndDiaryCounts()
    {
        _stack.StartGrow();
        var monday = new DateTime(2026, 11, 2);
        for (var day = 0; day < 7; day++)
        {
            foreach (var hour in new[] { 9, 12, 18 })
            {
                _stack.Clock.Now = monday.AddDays(day).AddHours(hour);
                await _stack.Monitoring.TakeSnapshotAsync(scheduled: true);
            }
        }
        _stack.Grows.AddDiaryEntry(DiaryKind.Watered);
        _stack.Grows.AddDiaryEntry(DiaryKind.Watered);
        _stack.Grows.AddDiaryEntry(DiaryKind.Note, "Lampe höher");

        _stack.Clock.Now = new DateTime(2026, 11, 8, 20, 0, 0); // Sunday evening
        var report = await _stack.Reports.GenerateLastWeekIfMissingAsync();

        Assert.NotNull(report);
        Assert.Equal(7, report.SnapshotIds.Count);
        Assert.Equal(7, _stack.Analyzer.ReportImageCount);
        Assert.Equal(new[] { "2× gegossen", "Lampe höher" }, report.Done);
        Assert.Null(await _stack.Reports.GenerateLastWeekIfMissingAsync()); // only once per week
    }

    [Fact]
    public async Task RememberedCameraViewIsStored()
    {
        var preset = await _stack.Camera.RememberAnalysisViewAsync();
        Assert.Equal(preset.Id, _stack.Camera.AnalysisPresetId);
    }

    [Fact]
    public void LightScheduleAcrossMidnight()
    {
        var settings = new AppSettings { LightOn = new TimeOnly(6, 0), LightOff = new TimeOnly(0, 0) };
        Assert.True(settings.IsLightOn(new TimeOnly(23, 30)));
        Assert.False(settings.IsLightOn(new TimeOnly(3, 0)));
    }
}
