using System.Collections.ObjectModel;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GrowCam.App.Services;
using GrowCam.Application;

namespace GrowCam.App.ViewModels;

public sealed record PresetItem(string Name, bool IsAnalysisView, IRelayCommand GoCommand);

/// <summary>Live view (refreshed every few seconds) and pan/tilt control.</summary>
public sealed partial class CameraViewModel : PageViewModel, IPageLifecycle
{
    private readonly DispatcherTimer _liveTimer = new() { Interval = TimeSpan.FromSeconds(4) };
    private bool _loadingFrame;

    [ObservableProperty] private Bitmap? _liveImage;
    [ObservableProperty] private string _liveStatus = "Live-Bild wird geladen …";
    [ObservableProperty] private string _analysisInfo = "";
    [ObservableProperty] private bool _hasPresets;

    public ObservableCollection<PresetItem> Presets { get; } = [];

    public CameraViewModel(AppServices services, INavigator navigator) : base(services, navigator)
    {
        _liveTimer.Tick += async (_, _) => await LoadFrameAsync();
    }

    public void OnShown()
    {
        _ = LoadFrameAsync();
        _ = LoadPresetsAsync();
        _liveTimer.Start();
    }

    public void OnHidden() => _liveTimer.Stop();

    [RelayCommand]
    private Task Move(string direction) => RunAsync(async () =>
    {
        await Services.Camera.MoveAsync(Enum.Parse<Direction>(direction));
        await LoadFrameAsync();
    });

    [RelayCommand]
    private Task RememberView() => RunAsync(async () =>
    {
        await Services.Camera.RememberAnalysisViewAsync();
        await LoadPresetsAsync();
    }, "Wird gespeichert …", "Gemerkt. Fotos werden ab jetzt aus dieser Ansicht gemacht.");

    private async Task LoadFrameAsync()
    {
        if (_loadingFrame)
            return;
        _loadingFrame = true;
        try
        {
            var frame = await Task.Run(() => Services.Camera.LiveFrameAsync());
            LiveImage = ToBitmap(frame, 1280);
            LiveStatus = $"Live-Bild · {DateTime.Now:HH:mm:ss} Uhr";
        }
        catch (UserFacingException ex)
        {
            LiveStatus = ex.Message;
        }
        finally
        {
            _loadingFrame = false;
        }
    }

    private async Task LoadPresetsAsync()
    {
        try
        {
            var presets = await Services.Camera.PresetsAsync();
            var analysisId = Services.Camera.AnalysisPresetId;
            Presets.Clear();
            foreach (var preset in presets)
            {
                var id = preset.Id;
                Presets.Add(new PresetItem(
                    preset.Name,
                    id == analysisId,
                    new AsyncRelayCommand(() => RunAsync(async () =>
                    {
                        await Services.Camera.GotoPresetAsync(id);
                        await LoadFrameAsync();
                    }))));
            }
            HasPresets = Presets.Count > 0;
            AnalysisInfo = presets.Any(p => p.Id == analysisId)
                ? "Die tägliche Prüfung wird aus der gemerkten Ansicht gemacht."
                : "Noch keine Ansicht gemerkt – die Kamera fotografiert so, wie sie gerade steht.";
        }
        catch (UserFacingException ex)
        {
            HasPresets = false;
            AnalysisInfo = ex.Message;
        }
    }
}
