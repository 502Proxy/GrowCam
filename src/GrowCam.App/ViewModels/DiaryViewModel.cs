using System.Collections.ObjectModel;
using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GrowCam.App.Services;
using GrowCam.Domain;

namespace GrowCam.App.ViewModels;

public enum DotState
{
    Done,
    Pending,
    Open,
}

public sealed record MilestoneItem(
    string Title,
    string DateText,
    Chip SourceChip,
    DotState Dot,
    bool IsLast,
    bool CanConfirmManually,
    IRelayCommand ConfirmCommand,
    IRelayCommand RejectCommand)
{
    public bool IsPending => Dot == DotState.Pending;
    public bool ShowLine => !IsLast;

    public IBrush DotFill => Dot switch
    {
        DotState.Done => new SolidColorBrush(Color.Parse("#8FD694")),
        DotState.Pending => new SolidColorBrush(Color.Parse("#FFD48A")),
        _ => Brushes.Transparent,
    };

    public IBrush DotStroke => Dot == DotState.Open ? new SolidColorBrush(Color.Parse("#6A7A70")) : Brushes.Transparent;
}

public sealed record DiaryItem(string Title, string Text, string When, IRelayCommand DeleteCommand)
{
    public bool HasText => Text.Length > 0;
}

public sealed record QuickAction(string Label, IRelayCommand Command);

/// <summary>Grow diary: milestone timeline and the "what was done" log.</summary>
public sealed partial class DiaryViewModel : PageViewModel
{
    [ObservableProperty] private string _growTitle = "";
    [ObservableProperty] private string _growDetails = "";
    [ObservableProperty] private string _noteText = "";
    [ObservableProperty] private bool _confirmFinish;
    [ObservableProperty] private bool _hasEntries;

    public ObservableCollection<MilestoneItem> Milestones { get; } = [];
    public ObservableCollection<DiaryItem> Entries { get; } = [];
    public IReadOnlyList<QuickAction> QuickActions { get; }

    public DiaryViewModel(AppServices services, INavigator navigator) : base(services, navigator)
    {
        QuickActions = new[] { DiaryKind.Watered, DiaryKind.Fertilized, DiaryKind.Repotted, DiaryKind.Trained }
            .Select(kind => new QuickAction(Labels.Of(kind), new RelayCommand(() => AddEntry(kind, ""))))
            .ToList();
    }

    public override void Refresh()
    {
        var grow = Services.Grows.Active();
        if (grow is null)
            return;

        GrowTitle = grow.Name;
        GrowDetails = string.Join(" · ", new[]
        {
            grow.Seed?.Name ?? "",
            grow.PlantCount == 1 ? "1 Pflanze" : $"{grow.PlantCount} Pflanzen",
        }.Where(s => s.Length > 0));

        Milestones.Clear();
        var types = Enum.GetValues<MilestoneType>();
        foreach (var type in types)
            Milestones.Add(BuildMilestone(grow, type, isLast: type == types[^1]));

        Entries.Clear();
        foreach (var e in Services.Grows.Diary(grow.Id))
        {
            var id = e.Id;
            Entries.Add(new DiaryItem(
                Labels.Of(e.Kind),
                e.Text,
                $"{Format.Short(DateOnly.FromDateTime(e.CreatedAt))}, {e.CreatedAt:HH:mm}",
                new RelayCommand(() => Run(() =>
                {
                    Services.Grows.DeleteDiaryEntry(id);
                    Refresh();
                }, "Eintrag gelöscht."))));
        }
        HasEntries = Entries.Count > 0;
    }

    [RelayCommand]
    private void SaveNote()
    {
        if (!string.IsNullOrWhiteSpace(NoteText))
            AddEntry(DiaryKind.Note, NoteText);
    }

    [RelayCommand]
    private void NewGrow() => Navigator.ShowWizard();

    [RelayCommand]
    private void AskFinish() => ConfirmFinish = true;

    [RelayCommand]
    private void CancelFinish() => ConfirmFinish = false;

    [RelayCommand]
    private void Finish() => Run(() =>
    {
        Services.Grows.Finish();
        Navigator.RefreshHeader();
        Navigator.ShowArchive();
    }, "Der Grow ist abgeschlossen und im Archiv.");

    private void AddEntry(DiaryKind kind, string text) => Run(() =>
    {
        Services.Grows.AddDiaryEntry(kind, text);
        NoteText = "";
        Refresh();
    }, $"Eingetragen: {Labels.Of(kind)}");

    private MilestoneItem BuildMilestone(Grow grow, MilestoneType type, bool isLast)
    {
        var confirm = new RelayCommand(() => Run(() =>
        {
            Services.Grows.ConfirmMilestone(type);
            Refresh();
            Navigator.RefreshHeader();
        }, "Gespeichert."));
        var reject = new RelayCommand(() => Run(() =>
        {
            Services.Grows.RejectMilestone(type);
            Refresh();
        }, "Okay, die KI schaut weiter."));

        var milestone = grow.MilestoneOf(type);
        if (type == MilestoneType.Sown)
            return new(Labels.Of(type), Format.Date(grow.SowDate), new Chip("Von dir eingetragen", Tone.Muted), DotState.Done, isLast, false, confirm, reject);

        if (milestone?.Status == MilestoneStatus.Confirmed)
        {
            var source = milestone.ByAi ? "Von der KI erkannt, von dir bestätigt" : "Von dir eingetragen";
            return new(Labels.Of(type), Format.Date(milestone.ReachedOn), new Chip(source, Tone.Ok), DotState.Done, isLast, false, confirm, reject);
        }

        if (milestone?.Status == MilestoneStatus.Suggested)
            return new(Labels.Of(type), Format.Date(milestone.ReachedOn), new Chip("Die KI vermutet es – bitte bestätigen", Tone.Warn), DotState.Pending, isLast, false, confirm, reject);

        var expected = grow.ExpectedDate(type);
        var dateText = expected is { } d ? $"erwartet etwa {Format.Date(d)}" : "noch offen";
        return new(Labels.Of(type), dateText, new Chip("Die KI schaut jeden Tag nach", Tone.Muted), DotState.Open, isLast,
            CanConfirmManually: type != MilestoneType.Harvest, confirm, reject);
    }
}
