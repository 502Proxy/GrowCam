using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GrowCam.App.Services;
using GrowCam.Domain;

namespace GrowCam.App.ViewModels;

/// <summary>Main window: header, navigation, messages and the current page.</summary>
public sealed partial class MainViewModel : ViewModelBase, INavigator
{
    private readonly AppServices _services;
    private readonly DispatcherTimer _messageTimer = new() { Interval = TimeSpan.FromSeconds(8) };

    [ObservableProperty]
    private PageViewModel? _currentPage;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsStart), nameof(IsCamera), nameof(IsDiary), nameof(IsArchive), nameof(IsSettings))]
    private string _activeTab = "start";

    [ObservableProperty]
    private string _subtitle = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasMessage), nameof(MessageBackground), nameof(MessageBorder), nameof(MessageForeground))]
    private string _message = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(MessageBackground), nameof(MessageBorder), nameof(MessageForeground))]
    private bool _messageIsError;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Zoom), nameof(IsFontNormal), nameof(IsFontLarge), nameof(IsFontHuge))]
    private double _fontScale;

    [ObservableProperty]
    private string _demoHint = "";

    public MainViewModel(AppServices services)
    {
        _services = services;
        _fontScale = services.Settings.Current.FontScale is > 0.5 and < 2 ? services.Settings.Current.FontScale : 1.0;
        _messageTimer.Tick += (_, _) =>
        {
            _messageTimer.Stop();
            Message = "";
        };
        services.Settings.Changed += (_, _) => Dispatcher.UIThread.Post(UpdateDemoHint);
        services.Schedule.DataChanged += () => Dispatcher.UIThread.Post(() =>
        {
            RefreshHeader();
            CurrentPage?.Refresh();
        });
        UpdateDemoHint();
        RefreshHeader();
        ShowStart();
    }

    public bool IsStart => ActiveTab == "start";
    public bool IsCamera => ActiveTab == "camera";
    public bool IsDiary => ActiveTab == "diary";
    public bool IsArchive => ActiveTab == "archive";
    public bool IsSettings => ActiveTab == "settings";

    public ITransform Zoom => new ScaleTransform(FontScale, FontScale);
    public bool IsFontNormal => FontScale < 1.1;
    public bool IsFontLarge => FontScale is >= 1.1 and < 1.25;
    public bool IsFontHuge => FontScale >= 1.25;

    public bool HasMessage => Message.Length > 0;
    public IBrush MessageBackground => Palette.Background(MessageIsError ? Tone.Warn : Tone.Ok);
    public IBrush MessageBorder => Palette.Border(MessageIsError ? Tone.Warn : Tone.Ok);
    public IBrush MessageForeground => Palette.Foreground(MessageIsError ? Tone.Warn : Tone.Ok);

    [RelayCommand]
    private void Navigate(string tab)
    {
        switch (tab)
        {
            case "camera": ShowCamera(); break;
            case "diary": ShowDiary(); break;
            case "archive": ShowArchive(); break;
            case "settings": ShowSettings(); break;
            default: ShowStart(); break;
        }
    }

    [RelayCommand]
    private void SetFontScale(string scale)
    {
        FontScale = double.Parse(scale, System.Globalization.CultureInfo.InvariantCulture);
        _services.Settings.Save(_services.Settings.Current with { FontScale = FontScale });
    }

    [RelayCommand]
    private void DismissMessage() => Message = "";

    public void ShowStart() => Show(new StartViewModel(_services, this), "start");
    public void ShowCamera() => Show(new CameraViewModel(_services, this), "camera");
    public void ShowDiary()
    {
        if (_services.Grows.Active() is null)
            ShowStart(); // no grow yet: the start page offers to begin one
        else
            Show(new DiaryViewModel(_services, this), "diary");
    }
    public void ShowArchive() => Show(new ArchiveViewModel(_services, this), "archive");
    public void ShowSettings() => Show(new SettingsViewModel(_services, this), "settings");
    public void ShowWizard() => Show(new WizardViewModel(_services, this), "diary");
    public void ShowReport(long reportId) => Show(new ReportViewModel(_services, this, reportId), ActiveTab);
    public void ShowArchivedGrow(long growId) => Show(new ArchiveDetailViewModel(_services, this, growId), "archive");

    public void Notify(string message, bool isError = false)
    {
        MessageIsError = isError;
        Message = message;
        _messageTimer.Stop();
        _messageTimer.Start();
    }

    public async Task<byte[]?> PickPhotoAsync()
    {
        var window = (Avalonia.Application.Current?.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)?.MainWindow;
        if (TopLevel.GetTopLevel(window)?.StorageProvider is not { CanOpen: true } storage)
            return null;

        var files = await storage.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Foto auswählen",
            AllowMultiple = false,
            FileTypeFilter = [new FilePickerFileType("Fotos") { Patterns = PhotoImport.Patterns }],
        });
        if (files.Count == 0)
            return null;

        await using var stream = await files[0].OpenReadAsync();
        using var buffer = new MemoryStream();
        await stream.CopyToAsync(buffer);
        return buffer.ToArray();
    }

    public void RefreshHeader()
    {
        var grow = _services.Grows.Active();
        var today = DateOnly.FromDateTime(_services.Clock.Now);
        Subtitle = grow is null
            ? "Gerade läuft kein Grow"
            : $"{grow.Name} · {Labels.Of(grow.CurrentPhase())} · Tag {grow.DayCount(today)}";
    }

    /// <summary>Called when the window is hidden to the tray or shown again.</summary>
    public void OnWindowVisibilityChanged(bool visible)
    {
        if (CurrentPage is not IPageLifecycle page)
            return;
        if (visible)
            page.OnShown();
        else
            page.OnHidden();
    }

    private void Show(PageViewModel page, string tab)
    {
        (CurrentPage as IPageLifecycle)?.OnHidden();
        ActiveTab = tab;
        CurrentPage = page;
        page.Refresh();
        (page as IPageLifecycle)?.OnShown();
    }

    private void UpdateDemoHint()
    {
        var s = _services.Settings.Current;
        var parts = new List<string>();
        if (s.IsDemoCamera)
            parts.Add("keine Kamera eingerichtet");
        if (s.IsDemoAi)
            parts.Add("keine KI eingerichtet (Beispiel-Antworten)");
        DemoHint = parts.Count == 0 ? "" : $"Demo-Modus: {string.Join(", ", parts)}. Siehe Einstellungen.";
    }
}
