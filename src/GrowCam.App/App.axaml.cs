using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using GrowCam.App.Services;
using GrowCam.App.ViewModels;
using GrowCam.App.Views;

namespace GrowCam.App;

// Note: fully qualified because "GrowCam.Application" (our layer) shadows Avalonia.Application here.
public sealed partial class App : Avalonia.Application
{
    private static readonly TimeSpan UpdateCheckInterval = TimeSpan.FromHours(6);

    private AppServices? _services;
    private MainWindow? _window;
    private MainViewModel? _mainViewModel;
    private BackgroundScheduler? _scheduler;
    private DispatcherTimer? _updateTimer;

    /// <summary>True while the user really wants to quit (closing the window only hides it).</summary>
    public static bool IsQuitting { get; private set; }

    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            desktop.ShutdownMode = ShutdownMode.OnExplicitShutdown;
            _services = CompositionRoot.Build();
            _mainViewModel = new MainViewModel(_services);
            _window = new MainWindow { DataContext = _mainViewModel };

            var startMinimized = desktop.Args?.Contains("--minimized") == true;
            if (!startMinimized)
                ShowWindow();

            _scheduler = new BackgroundScheduler(_services.Schedule, _services.Clock);
            _scheduler.Start();
            StartUpdateChecks();

            desktop.ShutdownRequested += async (_, _) =>
            {
                IsQuitting = true;
                if (_scheduler is not null)
                    await _scheduler.DisposeAsync();
            };
            FileLog.Info("GrowCam started");
        }

        base.OnFrameworkInitializationCompleted();
    }

    private void ShowWindow()
    {
        if (_window is null)
            return;
        _window.Show();
        _window.WindowState = WindowState.Normal;
        _window.Activate();
    }

    private void StartUpdateChecks()
    {
        _updateTimer = new DispatcherTimer { Interval = UpdateCheckInterval };
        _updateTimer.Tick += async (_, _) => await CheckForUpdatesAsync();
        _updateTimer.Start();
        Dispatcher.UIThread.Post(async () => await CheckForUpdatesAsync(), DispatcherPriority.Background);
    }

    private async Task CheckForUpdatesAsync()
    {
        if (_services is null)
            return;
        try
        {
            await _services.Updater.CheckAndAutoInstallAsync(windowHidden: _window?.IsVisible != true);
        }
        catch (Exception ex)
        {
            FileLog.Error("Update check failed", ex);
        }
    }

    private void TrayIcon_OnClicked(object? sender, EventArgs e) => ShowWindow();

    private void OpenMenuItem_OnClick(object? sender, EventArgs e) => ShowWindow();

    private void QuitMenuItem_OnClick(object? sender, EventArgs e)
    {
        IsQuitting = true;
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
            desktop.Shutdown();
    }
}
