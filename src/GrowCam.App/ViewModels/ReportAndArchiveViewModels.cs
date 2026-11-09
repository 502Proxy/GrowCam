using System.Collections.ObjectModel;
using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GrowCam.App.Services;
using GrowCam.Domain;

namespace GrowCam.App.ViewModels;

public sealed record PhotoItem(Bitmap? Image, string Label);

public sealed record LinkItem(string Title, string Subtitle, bool IsCurrent, IRelayCommand OpenCommand);

public sealed record FactItem(string Label, string Value);

/// <summary>One weekly report.</summary>
public sealed partial class ReportViewModel(AppServices services, INavigator navigator, long reportId) : PageViewModel(services, navigator)
{
    [ObservableProperty] private Chip _label = new("", Tone.Ok);
    [ObservableProperty] private string _headline = "";
    [ObservableProperty] private string _range = "";
    [ObservableProperty] private IReadOnlyList<string> _happened = [];
    [ObservableProperty] private IReadOnlyList<string> _done = [];
    [ObservableProperty] private IReadOnlyList<string> _nextSteps = [];
    [ObservableProperty] private bool _hasPhotos;
    [ObservableProperty] private bool _hasOtherReports;
    [ObservableProperty] private bool _nothingDone;

    public ObservableCollection<PhotoItem> Photos { get; } = [];
    public ObservableCollection<LinkItem> OtherReports { get; } = [];

    public override void Refresh()
    {
        var report = Services.Reports.Get(reportId);
        if (report is null)
            return;
        var grow = Services.Grows.Get(report.GrowId);

        Label = new Chip($"Wochenbericht · KW {report.CalendarWeek}" + (grow is null ? "" : $" · {grow.Name}"),
            report.NeedsAttention ? Tone.Warn : Tone.Ok);
        Headline = report.Headline;
        Range = $"{Format.Short(report.WeekStart)} – {Format.Short(report.WeekEnd)}";
        Happened = report.Happened;
        Done = report.Done;
        NothingDone = report.Done.Count == 0;
        NextSteps = report.NextSteps;

        Photos.Clear();
        var day = 1;
        foreach (var id in report.SnapshotIds)
            Photos.Add(new PhotoItem(ToBitmap(Services.Monitoring.SnapshotImage(id), 400), $"Tag {day++}"));
        HasPhotos = Photos.Count > 0;

        OtherReports.Clear();
        foreach (var other in Services.Reports.Reports(report.GrowId))
        {
            var otherId = other.Id;
            OtherReports.Add(new LinkItem(
                $"KW {other.CalendarWeek}",
                $"{Format.Short(other.WeekStart)} – {Format.Short(other.WeekEnd)}",
                otherId == reportId,
                new RelayCommand(() => Navigator.ShowReport(otherId))));
        }
        HasOtherReports = OtherReports.Count > 1;
    }

    [RelayCommand]
    private void Back() => Navigator.ShowStart();
}

/// <summary>List of finished grows.</summary>
public sealed partial class ArchiveViewModel(AppServices services, INavigator navigator) : PageViewModel(services, navigator)
{
    [ObservableProperty] private bool _hasItems;

    public ObservableCollection<LinkItem> Items { get; } = [];

    public override void Refresh()
    {
        Items.Clear();
        foreach (var grow in Services.Grows.Archived())
        {
            var id = grow.Id;
            var end = grow.ArchivedOn ?? grow.SowDate;
            Items.Add(new LinkItem(
                grow.Seed is null ? grow.Name : $"{grow.Name} · {grow.Seed.Name}",
                $"{Format.Date(grow.SowDate)} – {Format.Date(end)} · {end.DayNumber - grow.SowDate.DayNumber} Tage",
                false,
                new RelayCommand(() => Navigator.ShowArchivedGrow(id))));
        }
        HasItems = Items.Count > 0;
    }

    [RelayCommand]
    private void NewGrow() => Navigator.ShowWizard();
}

/// <summary>Details of one finished grow.</summary>
public sealed partial class ArchiveDetailViewModel(AppServices services, INavigator navigator, long growId) : PageViewModel(services, navigator)
{
    [ObservableProperty] private string _name = "";
    [ObservableProperty] private string _range = "";
    [ObservableProperty] private string _summary = "";
    [ObservableProperty] private bool _hasSummary;
    [ObservableProperty] private Bitmap? _photo;
    [ObservableProperty] private bool _hasReports;

    public ObservableCollection<FactItem> Milestones { get; } = [];
    public ObservableCollection<FactItem> Facts { get; } = [];
    public ObservableCollection<LinkItem> Reports { get; } = [];

    public override void Refresh()
    {
        var grow = Services.Grows.Get(growId);
        if (grow is null)
            return;
        var end = grow.ArchivedOn ?? grow.SowDate;

        Name = grow.Name;
        Range = $"{Format.Date(grow.SowDate)} – {Format.Date(end)} · {end.DayNumber - grow.SowDate.DayNumber} Tage";
        Summary = grow.Summary;
        HasSummary = Summary.Length > 0;
        var snapshot = Services.Monitoring.LatestSnapshot(grow.Id);
        Photo = snapshot is null ? null : ToBitmap(Services.Monitoring.SnapshotImage(snapshot.Id), 1200);

        Milestones.Clear();
        foreach (var type in Enum.GetValues<MilestoneType>())
        {
            var date = type == MilestoneType.Sown
                ? grow.SowDate
                : grow.MilestoneOf(type) is { Status: MilestoneStatus.Confirmed } m ? m.ReachedOn : (DateOnly?)null;
            Milestones.Add(new FactItem(Labels.Of(type), date is { } d ? $"Tag {d.DayNumber - grow.SowDate.DayNumber} · {Format.Date(d)}" : "–"));
        }

        Facts.Clear();
        Facts.Add(new FactItem("Pflanzen", grow.PlantCount.ToString()));
        if (grow.Seed is { } seed)
        {
            Facts.Add(new FactItem("Sorte", seed.Name));
            Facts.Add(new FactItem("Typ", Labels.Of(seed.Type)));
        }

        Reports.Clear();
        foreach (var report in Services.Reports.Reports(grow.Id))
        {
            var id = report.Id;
            Reports.Add(new LinkItem($"KW {report.CalendarWeek}", report.Headline, false, new RelayCommand(() => Navigator.ShowReport(id))));
        }
        HasReports = Reports.Count > 0;
    }

    [RelayCommand]
    private void Back() => Navigator.ShowArchive();
}
