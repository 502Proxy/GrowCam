using GrowCam.Application;
using Velopack;
using Velopack.Sources;

namespace GrowCam.App.Services;

/// <summary>
/// Self-update from GitHub releases of the (private) repo via Velopack.
/// Only works in an installed build (Setup.exe); in development it reports that.
/// </summary>
public sealed class Updater(ISettingsStore settings)
{
    private UpdateInfo? _pending;

    public string CurrentVersion
    {
        get
        {
            try
            {
                if (CreateManager().CurrentVersion is { } installed)
                    return installed.ToString();
            }
            catch (Exception)
            {
                // not installed via Velopack (development run)
            }
            return typeof(Updater).Assembly.GetName().Version?.ToString(3) ?? "?";
        }
    }

    public bool HasPendingUpdate => _pending is not null;

    /// <summary>Checks GitHub; returns a German status text for the UI.</summary>
    public async Task<string> CheckAsync()
    {
        var manager = CreateManager();
        if (!manager.IsInstalled)
            return "Updates gibt es nur in der installierten Version (nicht beim Testen).";
        if (string.IsNullOrWhiteSpace(settings.Current.UpdateToken))
            return "Für Updates fehlt noch der GitHub-Zugang (siehe unten).";

        _pending = await manager.CheckForUpdatesAsync();
        return _pending is null
            ? "Alles ist auf dem neuesten Stand."
            : $"Es gibt ein Update: Version {_pending.TargetFullRelease.Version}";
    }

    /// <summary>Downloads the pending update and restarts the app into it.</summary>
    public async Task InstallAndRestartAsync(bool startMinimized = false)
    {
        if (_pending is null)
            return;
        var manager = CreateManager();
        await manager.DownloadUpdatesAsync(_pending);
        manager.ApplyUpdatesAndRestart(_pending.TargetFullRelease, startMinimized ? ["--minimized"] : null);
    }

    /// <summary>Background check; installs right away if the user allowed automatic updates.</summary>
    public async Task CheckAndAutoInstallAsync(bool windowHidden)
    {
        await CheckAsync();
        if (_pending is not null && settings.Current.AutoInstallUpdates)
            await InstallAndRestartAsync(startMinimized: windowHidden);
    }

    private UpdateManager CreateManager()
    {
        var current = settings.Current;
        var token = string.IsNullOrWhiteSpace(current.UpdateToken) ? null : current.UpdateToken.Trim();
        return new UpdateManager(new GithubSource(current.UpdateRepoUrl, token, false));
    }
}
