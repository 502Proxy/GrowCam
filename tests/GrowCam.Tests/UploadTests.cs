using GrowCam.Application;

namespace GrowCam.Tests;

public sealed class UploadTests : IDisposable
{
    private static readonly byte[] Photo = [0xFF, 0xD8, 0xFF, 0xD9];

    private readonly TestStack _stack = new();

    public void Dispose() => _stack.Dispose();

    [Fact]
    public async Task UploadedPhotoIsAnalyzedWithoutComparisonPhoto()
    {
        var grow = _stack.StartGrow();
        await _stack.Monitoring.AnalyzeNowAsync();

        var analysis = await _stack.Monitoring.AnalyzeUploadedPhotoAsync(Photo, "braune Flecken?");

        var context = _stack.Analyzer.Contexts[^1];
        Assert.True(context.IsUploadedPhoto);
        Assert.Equal("braune Flecken?", context.UserNote);
        Assert.False(_stack.Analyzer.HadPreviousImage[^1]);
        Assert.Equal(Photo, _stack.Monitoring.SnapshotImage(analysis.SnapshotId));
        Assert.Equal(analysis.Id, _stack.Monitoring.LatestAnalysis(grow.Id)?.Id);
    }

    [Fact]
    public async Task DailyCheckDoesNotCompareWithUploadedPhoto()
    {
        _stack.StartGrow();
        await _stack.Monitoring.AnalyzeUploadedPhotoAsync(Photo);

        await _stack.Monitoring.AnalyzeNowAsync();

        Assert.False(_stack.Analyzer.Contexts[^1].IsUploadedPhoto);
        Assert.False(_stack.Analyzer.HadPreviousImage[^1]);

        await _stack.Monitoring.AnalyzeNowAsync();
        Assert.True(_stack.Analyzer.HadPreviousImage[^1]); // camera photo before → compared
    }

    [Fact]
    public async Task UploadedPhotoIsNotPartOfWeeklyReport()
    {
        _stack.StartGrow();
        _stack.Clock.Now = new DateTime(2026, 11, 10, 12, 0, 0);
        await _stack.Monitoring.AnalyzeUploadedPhotoAsync(Photo);
        _stack.Clock.Now = new DateTime(2026, 11, 11, 12, 0, 0);
        await _stack.Monitoring.AnalyzeNowAsync();

        await _stack.Reports.GenerateForWeekAsync(new DateOnly(2026, 11, 9));

        Assert.Equal(1, _stack.Analyzer.ReportImageCount);
    }

    [Fact]
    public async Task UploadNeedsRunningGrowAndContent()
    {
        await Assert.ThrowsAsync<NoActiveGrowException>(() => _stack.Monitoring.AnalyzeUploadedPhotoAsync(Photo));
        _stack.StartGrow();
        await Assert.ThrowsAsync<UserFacingException>(() => _stack.Monitoring.AnalyzeUploadedPhotoAsync([]));
    }

    [Fact]
    public void SettingsPickTheChosenProvidersKey()
    {
        var settings = new AppSettings { AiProvider = AiProvider.OpenAi, AnthropicApiKey = "claude-key" };
        Assert.True(settings.IsDemoAi);
        Assert.False((settings with { OpenAiApiKey = "sk-1" }).IsDemoAi);
        Assert.False((settings with { AiProvider = AiProvider.Claude }).IsDemoAi);
    }
}
