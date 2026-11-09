using GrowCam.Application;
using GrowCam.Domain;

namespace GrowCam.Infrastructure.Ai;

/// <summary>Offline stand-in for the AI, used when no API key is configured.</summary>
public sealed class DemoAi : IPlantAnalyzer, ISeedCatalog
{
    // Rough day thresholds at which the demo "detects" a stage.
    private static readonly (MilestoneType Type, int Day)[] StageDays =
    [
        (MilestoneType.Sprouted, 3),
        (MilestoneType.Vegetative, 14),
        (MilestoneType.Flowering, 40),
    ];

    private static readonly SeedInfo[] Seeds =
    [
        new("Northern Lights Auto", SeedType.Autoflower, 11, null, "ca. 60–100 cm", "18/6 die ganze Zeit", "leicht"),
        new("Gorilla Glue #4", SeedType.Photoperiod, null, 9, "ca. 100–150 cm", "Wachstum 18/6, Blüte 12/12", "mittel"),
        new("Amnesia Haze", SeedType.Photoperiod, null, 11, "ca. 120–180 cm", "Wachstum 18/6, Blüte 12/12", "mittel bis schwer"),
    ];

    public Task<AnalysisResult> AnalyzeAsync(byte[] image, byte[]? previousImage, GrowContext context, CancellationToken ct = default)
    {
        var suggestion = StageDays
            .Where(s => context.OpenMilestones.Contains(s.Type) && context.DayCount >= s.Day)
            .Select(s => (MilestoneType?)s.Type)
            .FirstOrDefault();
        return Task.FromResult(new AnalysisResult(
            84,
            "Den Pflanzen geht es gut – eine Kleinigkeit im Auge behalten.",
            [
                new Finding(Severity.Watch, "Untere Blätter werden etwas heller", "Düngung prüfen und in zwei bis drei Tagen noch mal vergleichen."),
                new Finding(Severity.Ok, "Neue Blätter sind sattgrün", "Nichts zu tun."),
            ],
            suggestion,
            suggestion is null ? "" : "(Demo) Die KI glaubt, diese Phase auf dem Foto zu erkennen."));
    }

    public Task<ReportDraft> WeeklyReportAsync(IReadOnlyList<byte[]> images, GrowContext context, IReadOnlyList<string> analyses, CancellationToken ct = default) =>
        Task.FromResult(new ReportDraft(
            "Ruhige Woche, die Pflanzen wachsen gleichmäßig.",
            ["Viele neue Blätter sind dazugekommen.", "Keine Schädlinge oder Schimmel zu sehen."],
            ["Weiter wie bisher gießen.", "Untere Blätter im Auge behalten."],
            false));

    public Task<IReadOnlyList<SeedInfo>> SearchAsync(string query, CancellationToken ct = default)
    {
        var words = query.ToLowerInvariant().Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var matches = Seeds.Where(s => words.Any(w => s.Name.Contains(w, StringComparison.OrdinalIgnoreCase))).ToList();
        return Task.FromResult<IReadOnlyList<SeedInfo>>(matches.Count > 0 ? matches : Seeds.ToList());
    }
}

/// <summary>
/// Proxy that picks the real or demo AI according to the current settings
/// (Strategy chosen at runtime), so entering an API key needs no restart.
/// </summary>
public sealed class ConfiguredAi : IPlantAnalyzer, ISeedCatalog
{
    private readonly HttpClient _http;
    private (IPlantAnalyzer Analyzer, ISeedCatalog Catalog) _current;

    public ConfiguredAi(ISettingsStore settings, HttpClient http)
    {
        _http = http;
        _current = Build(settings.Current);
        settings.Changed += (_, updated) => _current = Build(updated);
    }

    public Task<AnalysisResult> AnalyzeAsync(byte[] image, byte[]? previousImage, GrowContext context, CancellationToken ct = default) =>
        _current.Analyzer.AnalyzeAsync(image, previousImage, context, ct);

    public Task<ReportDraft> WeeklyReportAsync(IReadOnlyList<byte[]> images, GrowContext context, IReadOnlyList<string> analyses, CancellationToken ct = default) =>
        _current.Analyzer.WeeklyReportAsync(images, context, analyses, ct);

    public Task<IReadOnlyList<SeedInfo>> SearchAsync(string query, CancellationToken ct = default) =>
        _current.Catalog.SearchAsync(query, ct);

    private (IPlantAnalyzer, ISeedCatalog) Build(AppSettings s)
    {
        if (s.IsDemoAi)
        {
            var demo = new DemoAi();
            return (demo, demo);
        }
        var client = new ClaudeClient(_http, s.AnthropicApiKey.Trim(), s.AnthropicModel);
        return (new ClaudePlantAnalyzer(client), new ClaudeSeedCatalog(client));
    }
}
