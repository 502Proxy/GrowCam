using System.Collections.ObjectModel;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GrowCam.App.Services;
using GrowCam.Domain;

namespace GrowCam.App.ViewModels;

public sealed record FindingItem(Chip Chip, string Title, string Advice);

/// <summary>Start page: overall status, AI questions, weekly report, findings and latest photo.</summary>
public sealed partial class StartViewModel(AppServices services, INavigator navigator) : PageViewModel(services, navigator)
{
    private long? _reportId;
    private MilestoneType? _suggestion;

    [ObservableProperty] private bool _hasGrow;
    [ObservableProperty] private string _statusHeadline = "";
    [ObservableProperty] private string _statusText = "";
    [ObservableProperty] private Tone _statusTone = Tone.Info;
    [ObservableProperty] private bool _hasSuggestion;
    [ObservableProperty] private string _suggestionQuestion = "";
    [ObservableProperty] private string _suggestionNote = "";
    [ObservableProperty] private bool _hasReport;
    [ObservableProperty] private string _reportLabel = "";
    [ObservableProperty] private string _reportHeadline = "";
    [ObservableProperty] private string _reportRange = "";
    [ObservableProperty] private bool _hasFindings;
    [ObservableProperty] private Bitmap? _photo;
    [ObservableProperty] private string _photoCaption = "Noch kein Foto";
    [ObservableProperty] private string _note = "";

    public ObservableCollection<FindingItem> Findings { get; } = [];

    public IBrush StatusBackground => Palette.Background(StatusTone);
    public IBrush StatusBorder => Palette.Border(StatusTone);
    public IBrush StatusForeground => Palette.Foreground(StatusTone);

    partial void OnStatusToneChanged(Tone value)
    {
        OnPropertyChanged(nameof(StatusBackground));
        OnPropertyChanged(nameof(StatusBorder));
        OnPropertyChanged(nameof(StatusForeground));
    }

    public override void Refresh()
    {
        var grow = Services.Grows.Active();
        HasGrow = grow is not null;
        if (grow is null)
            return;

        var now = Services.Clock.Now;
        var analysis = Services.Monitoring.LatestAnalysis(grow.Id);
        if (analysis is null)
        {
            StatusHeadline = "Noch keine Prüfung";
            StatusText = $"Die KI schaut sich die Pflanzen jeden Tag um {Services.Settings.Current.AnalysisTime:HH\\:mm} Uhr an. Du kannst auch jetzt gleich prüfen lassen.";
            StatusTone = Tone.Info;
        }
        else
        {
            StatusHeadline = analysis.Headline;
            StatusText = "Zuletzt geprüft: " + Relative(analysis.CreatedAt, now);
            StatusTone = analysis.NeedsAttention ? Tone.Warn : Tone.Ok;
        }

        Findings.Clear();
        foreach (var f in analysis?.Findings ?? [])
            Findings.Add(new FindingItem(new Chip(Labels.Of(f.Severity), Palette.Of(f.Severity)), f.Title, f.Advice));
        HasFindings = Findings.Count > 0;

        var suggestion = grow.PendingSuggestion();
        _suggestion = suggestion?.Type;
        HasSuggestion = suggestion is not null;
        SuggestionQuestion = suggestion is null ? "" : Labels.QuestionFor(suggestion.Type);
        SuggestionNote = suggestion?.Note ?? "";

        var report = Services.Reports.Reports(grow.Id).FirstOrDefault();
        _reportId = report?.Id;
        HasReport = report is not null;
        if (report is not null)
        {
            ReportLabel = $"Wochenbericht · KW {report.CalendarWeek}";
            ReportHeadline = report.Headline;
            ReportRange = $"{Format.Short(report.WeekStart)} – {Format.Short(report.WeekEnd)}";
        }

        var snapshot = Services.Monitoring.LatestSnapshot(grow.Id);
        Photo = snapshot is null ? null : ToBitmap(Services.Monitoring.SnapshotImage(snapshot.Id), 1200);
        PhotoCaption = snapshot is null ? "Noch kein Foto" : Relative(snapshot.TakenAt, now);
    }

    [RelayCommand]
    private Task Analyze() => RunAsync(async () =>
    {
        await Services.Monitoring.AnalyzeNowAsync(Note);
        Note = "";
        Refresh();
        Navigator.RefreshHeader();
    }, "Die KI schaut sich die Pflanzen an … bitte warten", "Die KI hat sich die Pflanzen angesehen.");

    [RelayCommand]
    private Task AnalyzeOwnPhoto() => RunAsync(async () =>
    {
        var file = await Navigator.PickPhotoAsync();
        if (file is null)
            return; // cancelled
        BusyText = "Die KI schaut sich dein Foto an … bitte warten";
        var jpeg = await Task.Run(() => PhotoImport.ToJpeg(file));
        await Services.Monitoring.AnalyzeUploadedPhotoAsync(jpeg, Note);
        Note = "";
        Refresh();
        Navigator.RefreshHeader();
        Navigator.Notify("Die KI hat sich dein Foto angesehen.");
    }, "Foto auswählen …");

    [RelayCommand]
    private Task TakePhoto() => RunAsync(async () =>
    {
        await Services.Monitoring.TakeSnapshotAsync();
        Refresh();
    }, "Foto wird gemacht …");

    [RelayCommand]
    private void ConfirmSuggestion()
    {
        if (_suggestion is { } type)
            Run(() =>
            {
                Services.Grows.ConfirmMilestone(type);
                Refresh();
                Navigator.RefreshHeader();
            }, "Gespeichert.");
    }

    [RelayCommand]
    private void RejectSuggestion()
    {
        if (_suggestion is { } type)
            Run(() =>
            {
                Services.Grows.RejectMilestone(type);
                Refresh();
            }, "Okay, die KI schaut weiter.");
    }

    [RelayCommand]
    private void OpenReport()
    {
        if (_reportId is { } id)
            Navigator.ShowReport(id);
    }

    [RelayCommand]
    private void StartGrow() => Navigator.ShowWizard();
}
