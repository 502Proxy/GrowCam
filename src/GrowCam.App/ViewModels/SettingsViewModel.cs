using System.Diagnostics;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GrowCam.App.Services;
using GrowCam.Infrastructure;

namespace GrowCam.App.ViewModels;

/// <summary>Connections, schedule, autostart and updates.</summary>
public sealed partial class SettingsViewModel : PageViewModel
{
    [ObservableProperty] private string _cameraHost = "";
    [ObservableProperty] private string _cameraUser = "";
    [ObservableProperty] private string _cameraPassword = "";
    [ObservableProperty] private string _ffmpegPath = "";
    [ObservableProperty] private string _apiKey = "";
    [ObservableProperty] private string _model = "";
    [ObservableProperty] private string _analysisTime = "";
    [ObservableProperty] private string _reportTime = "";
    [ObservableProperty] private decimal? _snapshotInterval;
    [ObservableProperty] private string _lightOn = "";
    [ObservableProperty] private string _lightOff = "";
    [ObservableProperty] private string _updateToken = "";
    [ObservableProperty] private bool _autoInstall;
    [ObservableProperty] private bool _startWithWindows;
    [ObservableProperty] private string _updateStatus = "Noch nicht nach Updates gesucht.";
    [ObservableProperty] private bool _canInstallUpdate;

    public SettingsViewModel(AppServices services, INavigator navigator) : base(services, navigator)
    {
        var s = services.Settings.Current;
        _cameraHost = s.CameraHost;
        _cameraUser = s.CameraUser;
        _cameraPassword = s.CameraPassword;
        _ffmpegPath = s.FfmpegPath;
        _apiKey = s.AnthropicApiKey;
        _model = s.AnthropicModel;
        _analysisTime = s.AnalysisTime.ToString("HH:mm", CultureInfo.InvariantCulture);
        _reportTime = s.ReportTime.ToString("HH:mm", CultureInfo.InvariantCulture);
        _snapshotInterval = s.SnapshotIntervalMinutes;
        _lightOn = s.LightOn.ToString("HH:mm", CultureInfo.InvariantCulture);
        _lightOff = s.LightOff.ToString("HH:mm", CultureInfo.InvariantCulture);
        _updateToken = s.UpdateToken;
        _autoInstall = s.AutoInstallUpdates;
        _startWithWindows = Autostart.IsEnabled();
    }

    public string Version => Services.Updater.CurrentVersion;
    public string DataFolder => AppPaths.DataDirectory;
    public bool AutostartSupported => Autostart.IsSupported;

    [RelayCommand]
    private void Save()
    {
        if (!TryTime(AnalysisTime, out var analysis) || !TryTime(ReportTime, out var report)
            || !TryTime(LightOn, out var lightOn) || !TryTime(LightOff, out var lightOff))
        {
            Navigator.Notify("Bitte Uhrzeiten so eingeben: 12:00", isError: true);
            return;
        }

        Run(() =>
        {
            Services.Settings.Save(Services.Settings.Current with
            {
                CameraHost = CameraHost.Trim(),
                CameraUser = CameraUser.Trim(),
                CameraPassword = CameraPassword,
                FfmpegPath = FfmpegPath.Trim(),
                AnthropicApiKey = ApiKey.Trim(),
                AnthropicModel = string.IsNullOrWhiteSpace(Model) ? "claude-sonnet-5-5" : Model.Trim(),
                AnalysisTime = analysis,
                ReportTime = report,
                SnapshotIntervalMinutes = (int)(SnapshotInterval ?? 0),
                LightOn = lightOn,
                LightOff = lightOff,
                UpdateToken = UpdateToken.Trim(),
                AutoInstallUpdates = AutoInstall,
            });
            Autostart.Set(StartWithWindows);
        }, "Gespeichert.");
    }

    [RelayCommand]
    private Task CheckUpdates() => RunAsync(async () =>
    {
        UpdateStatus = await Services.Updater.CheckAsync();
        CanInstallUpdate = Services.Updater.HasPendingUpdate;
    }, "Suche nach Updates …");

    [RelayCommand]
    private Task InstallUpdate() => RunAsync(() => Services.Updater.InstallAndRestartAsync(), "Update wird geladen. GrowCam startet gleich neu …");

    [RelayCommand]
    private Task GenerateReport() => RunAsync(async () =>
    {
        var report = await Services.Reports.GenerateLastWeekIfMissingAsync();
        if (report is null)
            Navigator.Notify("Für die letzte Woche gibt es schon einen Bericht (oder es läuft kein Grow).");
        else
            Navigator.ShowReport(report.Id);
    }, "Bericht wird erstellt …");

    [RelayCommand]
    private void OpenDataFolder()
    {
        var opener = OperatingSystem.IsWindows() ? "explorer.exe" : OperatingSystem.IsMacOS() ? "open" : "xdg-open";
        Process.Start(new ProcessStartInfo(opener, $"\"{DataFolder}\"") { UseShellExecute = false });
    }

    private static bool TryTime(string text, out TimeOnly time) =>
        TimeOnly.TryParseExact(text.Trim(), ["H:mm", "HH:mm"], CultureInfo.InvariantCulture, DateTimeStyles.None, out time);
}
