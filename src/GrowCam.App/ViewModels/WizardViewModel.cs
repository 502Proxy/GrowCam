using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GrowCam.App.Services;
using GrowCam.Domain;

namespace GrowCam.App.ViewModels;

public sealed record SeedResultItem(string Name, string TypeLabel, IRelayCommand PickCommand);

/// <summary>"Neuen Grow starten" in three steps: name/date → strain search → check data.</summary>
public sealed partial class WizardViewModel : PageViewModel
{
    // Order matches the ComboBox in step 3.
    private static readonly SeedType[] SeedTypes = [SeedType.Autoflower, SeedType.Photoperiod, SeedType.Unknown];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsStep1), nameof(IsStep2), nameof(IsStep3), nameof(IsDone), nameof(StepText), nameof(Step2Reached), nameof(Step3Reached))]
    private int _step = 1;

    // Step 1
    [ObservableProperty] private string _growName = "";
    [ObservableProperty] private DateTime? _sowDate;
    [ObservableProperty] private string _runningGrowName = "";
    [ObservableProperty] private bool _hasRunningGrow;

    // Step 2
    [ObservableProperty] private string _query = "";
    [ObservableProperty] private string _searchMessage = "";
    [ObservableProperty] private bool _hasResults;

    // Step 3
    [ObservableProperty] private bool _foundOnline;
    [ObservableProperty] private string _seedName = "";
    [ObservableProperty] private int _seedTypeIndex = 2;
    [ObservableProperty] private decimal? _weeksToHarvest;
    [ObservableProperty] private decimal? _floweringWeeks;
    [ObservableProperty] private string _height = "";
    [ObservableProperty] private string _lightSchedule = "";
    [ObservableProperty] private string _difficulty = "";
    [ObservableProperty] private int _plants = 1;

    public ObservableCollection<SeedResultItem> Results { get; } = [];
    public IReadOnlyList<string> SeedTypeLabels { get; } = SeedTypes.Select(Labels.Of).ToList();

    public WizardViewModel(AppServices services, INavigator navigator) : base(services, navigator)
    {
        _sowDate = services.Clock.Now.Date;
        var running = services.Grows.Active();
        _hasRunningGrow = running is not null;
        _runningGrowName = running?.Name ?? "";
    }

    public bool IsStep1 => Step == 1;
    public bool IsStep2 => Step == 2;
    public bool IsStep3 => Step == 3;
    public bool IsDone => Step == 4;
    public bool Step2Reached => Step >= 2;
    public bool Step3Reached => Step >= 3;
    public string StepText => Step <= 3 ? $"Neuen Grow starten · Schritt {Step} von 3" : "Fertig";

    [RelayCommand]
    private void Next()
    {
        if (string.IsNullOrWhiteSpace(GrowName))
        {
            Navigator.Notify("Bitte gib dem Grow einen Namen.", isError: true);
            return;
        }
        Step = 2;
    }

    [RelayCommand]
    private void Back() => Step = Math.Max(1, Step - 1);

    [RelayCommand]
    private void Cancel() => Navigator.ShowStart();

    [RelayCommand]
    private Task Search() => RunAsync(async () =>
    {
        Results.Clear();
        SearchMessage = "";
        if (string.IsNullOrWhiteSpace(Query))
            return;
        var seeds = await Services.Seeds.SearchAsync(Query);
        foreach (var seed in seeds)
            Results.Add(new SeedResultItem(seed.Name, Labels.Of(seed.Type), new RelayCommand(() => Pick(seed))));
        HasResults = Results.Count > 0;
        if (!HasResults)
            SearchMessage = "Dazu wurde nichts gefunden. Prüfe die Schreibweise oder probiere nur einen Teil des Namens.";
    }, "Suche im Internet …");

    [RelayCommand]
    private void EnterManually()
    {
        Fill(new SeedInfo(Query.Trim()));
        FoundOnline = false;
        Step = 3;
    }

    [RelayCommand]
    private void PlantsMinus() => Plants = Math.Max(1, Plants - 1);

    [RelayCommand]
    private void PlantsPlus() => Plants = Math.Min(20, Plants + 1);

    [RelayCommand]
    private void StartGrow() => Run(() =>
    {
        SeedInfo? seed = string.IsNullOrWhiteSpace(SeedName)
            ? null
            : new SeedInfo(
                SeedName.Trim(),
                SeedTypes[Math.Clamp(SeedTypeIndex, 0, SeedTypes.Length - 1)],
                (int?)WeeksToHarvest,
                (int?)FloweringWeeks,
                Height.Trim(),
                LightSchedule.Trim(),
                Difficulty.Trim());
        var sowDate = DateOnly.FromDateTime(SowDate ?? Services.Clock.Now);
        Services.Grows.Start(GrowName, sowDate, Plants, seed);
        Navigator.RefreshHeader();
        Step = 4;
    });

    [RelayCommand]
    private void Finish() => Navigator.ShowStart();

    private void Pick(SeedInfo seed)
    {
        Fill(seed);
        FoundOnline = true;
        Step = 3;
    }

    private void Fill(SeedInfo seed)
    {
        SeedName = seed.Name;
        SeedTypeIndex = Array.IndexOf(SeedTypes, seed.Type);
        WeeksToHarvest = seed.WeeksToHarvest;
        FloweringWeeks = seed.FloweringWeeks;
        Height = seed.Height;
        LightSchedule = seed.LightSchedule;
        Difficulty = seed.Difficulty;
    }
}
